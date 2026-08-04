using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

/// <summary>
/// Tests for splitting a primary-track segment at the playhead. Splitting must
/// divide the source range (video) or duration (slide) into two contiguous halves
/// without changing total timeline duration or desyncing linked source-time data.
/// </summary>
[TestClass]
public sealed class SplitSegmentOperationTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, double speed = 1.0)
        => TestTimelineBuilder.Video(PrimaryPath, srcStartSec, srcDurSec, speed);

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    [TestMethod]
    public void Split_Video_ProducesTwoContiguousHalves()
    {
        var model = ModelWith(Video(0, 10)); // output 0..10

        var op = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4));
        op.Execute(model);

        Assert.IsTrue(op.DidSplit);
        Assert.AreEqual(2, model.Segments.Count);

        var a = (VideoSegment)model.Segments[0];
        var b = (VideoSegment)model.Segments[1];
        Assert.AreEqual(TimeSpan.FromSeconds(4), a.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), a.SourceDuration);
        Assert.AreEqual(TimeSpan.Zero, a.SourceStart);

        Assert.AreEqual(TimeSpan.FromSeconds(6), b.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), b.SourceStart); // source continues
        Assert.AreEqual(TimeSpan.FromSeconds(6), b.SourceDuration);

        // Contiguous, same total duration.
        Assert.AreEqual(TimeSpan.Zero, a.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(4), b.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.TotalSegmentsDuration);
    }

    [TestMethod]
    public void Split_PicksSegmentUnderPlayhead()
    {
        var model = ModelWith(Video(0, 5), Video(5, 5)); // [0..5][5..10]

        // Split at output 7s → inside the second segment.
        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(7)).Execute(model);

        Assert.AreEqual(3, model.Segments.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.Segments[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(7), model.Segments[2].Start);
    }

    [TestMethod]
    public void Split_KeepsZoomMappingInSync()
    {
        var model = ModelWith(Video(0, 10));
        // Source 6s maps to output 6s before the split.
        Assert.AreEqual(TimeSpan.FromSeconds(6), model.SourceToOutputTime(TimeSpan.FromSeconds(6)));

        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)).Execute(model);

        // Still maps to output 6s after splitting at 4s (source 6 is in second half).
        Assert.AreEqual(TimeSpan.FromSeconds(6), model.SourceToOutputTime(TimeSpan.FromSeconds(6)));
    }

    [TestMethod]
    public void Split_TextSlide_SplitsDuration()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(6) };
        var model = ModelWith(slide, Video(0, 4));

        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(2)).Execute(model);

        Assert.AreEqual(3, model.Segments.Count);
        Assert.IsInstanceOfType(model.Segments[0], typeof(TextSlideSegment));
        Assert.IsInstanceOfType(model.Segments[1], typeof(TextSlideSegment));
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.Segments[0].Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), model.Segments[1].Duration);
    }

    [TestMethod]
    public void Split_AtBoundary_DoesNothing()
    {
        var model = ModelWith(Video(0, 5), Video(5, 5));

        var op = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(5)); // exact boundary
        op.Execute(model);

        Assert.IsFalse(op.DidSplit);
        Assert.AreEqual(2, model.Segments.Count);
    }

    [TestMethod]
    public void Split_TooCloseToEdge_DoesNothing()
    {
        var model = ModelWith(Video(0, 10));

        var op = new SplitSegmentAtTimeOperation(TimeSpan.FromMilliseconds(10)); // < MinHalf
        op.Execute(model);

        Assert.IsFalse(op.DidSplit);
        Assert.AreEqual(1, model.Segments.Count);
    }

    [TestMethod]
    public void Split_Undo_RestoresSingleSegment()
    {
        var model = ModelWith(Video(0, 10));
        var originalId = model.Segments[0].Id;

        var op = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4));
        op.Execute(model);
        Assert.AreEqual(2, model.Segments.Count);

        op.Undo(model);
        Assert.AreEqual(1, model.Segments.Count);
        Assert.AreEqual(originalId, model.Segments[0].Id);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[0].Duration);
    }

    [TestMethod]
    public void Split_Video_WithSpeed_SplitsSourceProportionally()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0)); // 10s source -> 5s output

        // Split at output 2s → source offset = 2 * 2.0 = 4s.
        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(2)).Execute(model);

        var a = (VideoSegment)model.Segments[0];
        var b = (VideoSegment)model.Segments[1];
        Assert.AreEqual(TimeSpan.FromSeconds(2), a.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), a.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), b.SourceStart);
        Assert.AreEqual(TimeSpan.FromSeconds(3), b.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(6), b.SourceDuration);
    }
}
