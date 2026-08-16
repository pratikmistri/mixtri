using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

[TestClass]
public sealed class SegmentOverlayOperationsTests
{
    private const string PrimaryPath = "primary.mp4";

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static VideoSegment Video(double sourceStartSeconds, double durationSeconds) =>
        TestTimelineBuilder.Video(PrimaryPath, sourceStartSeconds, durationSeconds);

    private static TextSlideSegment Slide(double durationSeconds) =>
        new() { Duration = S(durationSeconds) };

    private static TimelineModel ModelWith(params TimelineSegment[] segments) =>
        TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    [TestMethod]
    public void InsertOverlay_DoesNotSplitOrShiftBaseTrackAndUndoRestoresList()
    {
        var first = Video(0, 4);
        var second = Video(4, 4);
        var overlay = Slide(2);
        var model = ModelWith(first, second);
        var originalIds = model.Segments.Select(s => s.Id).ToArray();

        var op = new InsertSegmentOnOverlayTrackOperation(overlay, S(2));
        op.Execute(model);

        Assert.AreEqual(1, op.ResolvedTrackIndex);
        Assert.AreEqual(3, model.Segments.Count);
        Assert.AreEqual(TimeSpan.Zero, first.Start);
        Assert.AreEqual(S(4), second.Start);
        Assert.AreEqual(S(2), overlay.Start);
        Assert.AreEqual(1, overlay.TrackIndex);

        op.Undo(model);

        CollectionAssert.AreEqual(originalIds, model.Segments.Select(s => s.Id).ToArray());
        Assert.AreEqual(TimeSpan.Zero, first.Start);
        Assert.AreEqual(S(4), second.Start);
    }

    [TestMethod]
    public void InsertOverlay_AutoTrackSelectionSkipsCollidingTrack()
    {
        var existing = Slide(5) with { TrackIndex = 1, Start = S(1) };
        var inserted = Slide(1);
        var model = ModelWith(Video(0, 10), existing);

        var op = new InsertSegmentOnOverlayTrackOperation(inserted, S(2), trackIndex: -1);
        op.Execute(model);

        Assert.AreEqual(2, op.ResolvedTrackIndex);
        Assert.AreEqual(2, inserted.TrackIndex);
        Assert.AreEqual(S(2), inserted.Start);
    }

    /// <summary>
    /// This is the undo regression guard for SegmentListSnapshot: a shallow list restore is
    /// insufficient because the same segment instance has already had Start and TrackIndex
    /// mutated by Execute.
    /// </summary>
    [TestMethod]
    public void MoveSegmentOnTrack_UndoRestoresStartAndTrackIndexAndRedoKeepsIds()
    {
        var first = Video(0, 4);
        var overlay = Slide(2) with { TrackIndex = 1, Start = S(5) };
        var second = Video(4, 3);
        var model = ModelWith(first, overlay, second);
        var originalIds = model.Segments.Select(s => s.Id).ToArray();

        var op = new MoveSegmentOnTrackOperation(overlay.Id, S(7), newTrackIndex: 2);
        op.Execute(model);
        var redoIds = model.Segments.Select(s => s.Id).ToArray();

        Assert.AreEqual(2, overlay.TrackIndex);
        Assert.AreEqual(S(7), overlay.Start);

        op.Undo(model);

        CollectionAssert.AreEqual(originalIds, model.Segments.Select(s => s.Id).ToArray());
        Assert.AreEqual(1, overlay.TrackIndex);
        Assert.AreEqual(S(5), overlay.Start);

        op.Execute(model);

        CollectionAssert.AreEqual(redoIds, model.Segments.Select(s => s.Id).ToArray());
        Assert.AreEqual(2, overlay.TrackIndex);
        Assert.AreEqual(S(7), overlay.Start);
    }

    [TestMethod]
    public void InsertOverlay_RedoUsesSameSegmentId()
    {
        var model = ModelWith(Video(0, 4));
        var overlay = Slide(1);
        var overlayId = overlay.Id;
        var op = new InsertSegmentOnOverlayTrackOperation(overlay, S(1));

        op.Execute(model);
        op.Undo(model);
        op.Execute(model);

        Assert.AreEqual(overlayId, model.Segments.Single(s => s.TrackIndex == 1).Id);
    }
}
