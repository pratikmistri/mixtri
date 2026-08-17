using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

/// <summary>
/// Tests for <see cref="ChangeSegmentSpeedOperation"/> — the segment-timeline speed edit.
/// The invariant under test throughout is <c>SourceDuration = Duration × SpeedFactor</c>:
/// changing speed keeps the SAME footage (source in/out untouched) and re-derives how long
/// it occupies the output timeline.
/// </summary>
[TestClass]
public sealed class SegmentSpeedOperationTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, double speed = 1.0)
        => TestTimelineBuilder.Video(PrimaryPath, srcStartSec, srcDurSec, speed);

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    [TestMethod]
    public void SpeedUp_HalvesDurationAndRipplesFollowing()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        new ChangeSegmentSpeedOperation(a.Id, 2.0).Execute(model);

        var fast = (VideoSegment)model.Segments[0];
        Assert.AreEqual(2.0, fast.SpeedFactor);
        Assert.AreEqual(TimeSpan.FromSeconds(5), fast.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), fast.SourceDuration, "Same footage still plays");
        Assert.AreEqual(TimeSpan.Zero, fast.SourceStart, "In-point is untouched");
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.Segments[1].Start, "Following segment ripples left");
    }

    [TestMethod]
    public void SlowDown_ExtendsDurationAndRipplesFollowing()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        new ChangeSegmentSpeedOperation(a.Id, 0.5).Execute(model);

        var slow = (VideoSegment)model.Segments[0];
        Assert.AreEqual(0.5, slow.SpeedFactor);
        Assert.AreEqual(TimeSpan.FromSeconds(20), slow.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), slow.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Segments[1].Start);
    }

    [TestMethod]
    public void ChangeSpeed_FromNonDefaultSpeed_RecomputesFromSourceNotCurrentDuration()
    {
        // Already at 2x (10s of footage in 5s of output); going to 4x must yield 2.5s,
        // not 2.5s-of-the-already-halved value or a compounded 1.25s.
        var a = Video(0, 10, speed: 2.0);
        var model = ModelWith(a);
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.Segments[0].Duration);

        new ChangeSegmentSpeedOperation(a.Id, 4.0).Execute(model);

        var seg = (VideoSegment)model.Segments[0];
        Assert.AreEqual(4.0, seg.SpeedFactor);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), seg.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), seg.SourceDuration);
    }

    [TestMethod]
    public void ChangeSpeed_KeepsOutputMappingConsistent()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        new ChangeSegmentSpeedOperation(a.Id, 2.0).Execute(model);

        // Source 5s sits halfway through the footage, so it lands halfway through the
        // now-5s output block.
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), model.SourceToOutputTime(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.TotalSegmentsDuration);
    }

    [TestMethod]
    public void ChangeSpeed_Undo_RestoresSpeedAndDuration()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        var op = new ChangeSegmentSpeedOperation(a.Id, 4.0);
        op.Execute(model);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), model.Segments[0].Duration);

        op.Undo(model);

        var restored = (VideoSegment)model.Segments[0];
        Assert.AreEqual(1.0, restored.SpeedFactor);
        Assert.AreEqual(TimeSpan.FromSeconds(10), restored.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), restored.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[1].Start, "Following segment ripples back");
    }

    [TestMethod]
    public void ChangeSpeed_ToSameValue_IsNoOp()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        var op = new ChangeSegmentSpeedOperation(a.Id, 1.0);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel, "Re-selecting the current speed must not push an undo entry");
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[0].Duration);
    }

    [TestMethod]
    public void ChangeSpeed_UnknownSegment_IsNoOp()
    {
        var model = ModelWith(Video(0, 10));

        var op = new ChangeSegmentSpeedOperation("missing", 2.0);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[0].Duration);
    }

    [TestMethod]
    public void ChangeSpeed_TextSlide_IsNoOp()
    {
        // Speed is a video-only property; a slide has no footage to re-time.
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(5) };
        var model = ModelWith(slide, Video(0, 10));

        var op = new ChangeSegmentSpeedOperation(slide.Id, 2.0);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel);
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.Segments[0].Duration);
    }

    [TestMethod]
    public void ChangeSpeed_OutOfRange_ClampsToSupportedRange()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        new ChangeSegmentSpeedOperation(a.Id, 500.0).Execute(model);
        Assert.AreEqual(ChangeSegmentSpeedOperation.MaxSpeed, ((VideoSegment)model.Segments[0]).SpeedFactor);

        new ChangeSegmentSpeedOperation(model.Segments[0].Id, 0.0001).Execute(model);
        Assert.AreEqual(ChangeSegmentSpeedOperation.MinSpeed, ((VideoSegment)model.Segments[0]).SpeedFactor);
    }

    [TestMethod]
    public void ChangeSpeed_ExtremeSpeedOnShortSegment_ClampsToMinimumDuration()
    {
        // 0.2s of footage at 10x would be 20ms — below the degenerate-segment floor.
        var a = Video(0, 0.2);
        var model = ModelWith(a);

        new ChangeSegmentSpeedOperation(a.Id, 10.0).Execute(model);

        Assert.AreEqual(TrimSegmentEdgeOperation.MinDuration, model.Segments[0].Duration);
    }

    [TestMethod]
    public void ChangeSpeed_SegmentWithoutSourceDuration_DerivesFootageFromCurrentDuration()
    {
        // Legacy/hand-built segments can carry a zero SourceDuration; the footage is then
        // whatever the current output duration covers at the current speed.
        var a = new VideoSegment
        {
            VideoFilePath = PrimaryPath,
            Duration = TimeSpan.FromSeconds(8),
        };
        var model = ModelWith(a);

        new ChangeSegmentSpeedOperation(a.Id, 2.0).Execute(model);

        var seg = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(8), seg.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), seg.Duration);
    }

    [TestMethod]
    public void ChangeSpeed_OverlayTrackSegment_KeepsAuthoredStart()
    {
        var baseSeg = Video(0, 10);
        var overlay = Video(0, 6);
        overlay.TrackIndex = 1;
        overlay.Start = TimeSpan.FromSeconds(3);
        var model = ModelWith(baseSeg, overlay);

        new ChangeSegmentSpeedOperation(overlay.Id, 2.0).Execute(model);

        var seg = (VideoSegment)model.Segments[1];
        Assert.AreEqual(TimeSpan.FromSeconds(3), seg.Start, "Overlay start is authored, not reflowed");
        Assert.AreEqual(TimeSpan.FromSeconds(3), seg.Duration);
    }
}
