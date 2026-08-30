using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public sealed class AutomaticTypingSpeedOperationTests
{
    [TestMethod]
    public void Execute_SplitsAroundTypingAndMutesOnlyAcceleratedSlice()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);

        new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))])
            .Execute(model);

        Assert.AreEqual(3, model.Segments.Count);
        var pieces = model.Segments.Cast<VideoSegment>().ToList();
        Assert.AreEqual(1.0, pieces[0].SpeedFactor);
        Assert.AreEqual(1.5, pieces[1].SpeedFactor);
        Assert.AreEqual(SegmentAudioMode.Muted, pieces[1].AudioMode);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, pieces[0].AudioMode);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, pieces[2].AudioMode);
        Assert.AreEqual(TimeSpan.FromSeconds(2), pieces[1].Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(9), model.TotalSegmentsDuration);
    }

    [TestMethod]
    public void Execute_PreservesSourceCoverageAndFirstSegmentIdentity()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 4, 8);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);

        new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8))])
            .Execute(model);

        var pieces = model.Segments.Cast<VideoSegment>().ToList();
        Assert.AreEqual(video.Id, pieces[0].Id);
        Assert.AreEqual(TimeSpan.FromSeconds(4), pieces[0].SourceStart);
        Assert.AreEqual(TimeSpan.FromSeconds(12), pieces[^1].SourceStart + pieces[^1].SourceDuration);
        Assert.AreEqual(
            video.SourceDuration,
            pieces.Aggregate(TimeSpan.Zero, (sum, piece) => sum + piece.SourceDuration));
    }

    [TestMethod]
    public void Execute_OverlayPiecesRemainContiguousAtAuthoredStart()
    {
        var baseVideo = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var overlay = TestTimelineBuilder.Video("append.mp4", 0, 6);
        overlay.TrackIndex = 1;
        overlay.Start = TimeSpan.FromSeconds(3);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", baseVideo, overlay);

        new AutomaticTypingSpeedOperation(
            overlay.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4))])
            .Execute(model);

        var pieces = model.Segments.OfType<VideoSegment>()
            .Where(s => s.VideoFilePath == "append.mp4")
            .ToList();
        Assert.AreEqual(TimeSpan.FromSeconds(3), pieces[0].Start);
        Assert.AreEqual(pieces[0].End, pieces[1].Start);
        Assert.AreEqual(pieces[1].End, pieces[2].Start);
    }

    [TestMethod]
    public void Execute_AlreadyRetimedSegmentIsLeftUntouched()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 0, 10, speed: 2);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);
        var operation = new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))]);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel);
        Assert.AreEqual(1, model.Segments.Count);
        Assert.AreEqual(2.0, ((VideoSegment)model.Segments[0]).SpeedFactor);
    }

    [TestMethod]
    public void Execute_RejectsMicroRangeThatWouldCreateDegenerateAcceleratedSlice()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);
        var operation = new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.01))]);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel);
        Assert.AreEqual(1, model.Segments.Count);
    }

    [TestMethod]
    public void Undo_RestoresOriginalSegment()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);
        var operation = new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))]);

        operation.Execute(model);
        operation.Undo(model);

        Assert.AreEqual(1, model.Segments.Count);
        Assert.AreSame(video, model.Segments[0]);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.TotalSegmentsDuration);
    }

    [TestMethod]
    public void Redo_ReusesGeneratedPieceIds()
    {
        var video = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);
        var manager = new UndoRedoManager(model);
        var operation = new AutomaticTypingSpeedOperation(
            video.Id,
            [new TypingActivityRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))]);

        manager.Execute(operation);
        var firstIds = model.Segments.Select(segment => segment.Id).ToArray();

        manager.Undo();
        manager.Redo();
        var redoneIds = model.Segments.Select(segment => segment.Id).ToArray();

        CollectionAssert.AreEqual(firstIds, redoneIds);
    }
}
