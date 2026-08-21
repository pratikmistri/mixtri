using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

/// <summary>
/// Tests for the FCP-style primary-track edit operations: moving/reordering
/// segments and ripple-trimming either edge, including that linked source-time
/// data (zoom/click/cursor) stays in sync afterwards.
/// </summary>
[TestClass]
public sealed class SegmentEditOperationsTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, double speed = 1.0)
        => TestTimelineBuilder.Video(PrimaryPath, srcStartSec, srcDurSec, speed);

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    // ── Move / reorder ──

    [TestMethod]
    public void Move_SecondBeforeFirst_ReordersAndRepositions()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        new MoveSegmentOperation(b.Id, 0).Execute(model);

        Assert.AreEqual(b.Id, model.Segments[0].Id);
        Assert.AreEqual(a.Id, model.Segments[1].Id);
        Assert.AreEqual(TimeSpan.Zero, model.Segments[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[1].Start);
    }

    [TestMethod]
    public void Move_KeepsLinkedZoomInSync()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        // A zoom keyframe at source 12s lives inside segment B.
        var sourceZoom = TimeSpan.FromSeconds(12);
        Assert.AreEqual(TimeSpan.FromSeconds(12), model.SourceToOutputTime(sourceZoom));

        new MoveSegmentOperation(b.Id, 0).Execute(model);

        // B is now first (output 0..10); the same source zoom now maps to output 2s.
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.SourceToOutputTime(sourceZoom));
    }

    [TestMethod]
    public void Move_Undo_RestoresOriginalOrder()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var c = Video(20, 10);
        var model = ModelWith(a, b, c);

        var op = new MoveSegmentOperation(c.Id, 0);
        op.Execute(model);
        Assert.AreEqual(c.Id, model.Segments[0].Id);

        op.Undo(model);
        CollectionAssert.AreEqual(
            new[] { a.Id, b.Id, c.Id },
            model.Segments.Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public void Reorder_Undo_RestoresOriginalOrder()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var c = Video(20, 10);
        var model = ModelWith(a, b, c);

        var op = new ReorderSegmentOperation(0, 2);
        op.Execute(model);
        CollectionAssert.AreEqual(
            new[] { b.Id, c.Id, a.Id },
            model.Segments.Select(s => s.Id).ToArray());

        op.Undo(model);
        CollectionAssert.AreEqual(
            new[] { a.Id, b.Id, c.Id },
            model.Segments.Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public void Reorder_Undo_RestoresOrder_WhenTargetPastEnd()
    {
        // A target index past the end is clamped on Execute; Undo must still fully
        // revert (regression: the old index-arithmetic Undo silently no-opped here).
        var a = Video(0, 10);
        var b = Video(10, 10);
        var c = Video(20, 10);
        var model = ModelWith(a, b, c);

        var op = new ReorderSegmentOperation(0, 10);
        op.Execute(model);
        CollectionAssert.AreEqual(
            new[] { b.Id, c.Id, a.Id },
            model.Segments.Select(s => s.Id).ToArray());

        op.Undo(model);
        CollectionAssert.AreEqual(
            new[] { a.Id, b.Id, c.Id },
            model.Segments.Select(s => s.Id).ToArray());
    }

    // ── Ripple trim ──

    [TestMethod]
    public void TrimRightEdge_Video_ShortensAndRipplesFollowing()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, TimeSpan.FromSeconds(6)).Execute(model);

        var trimmedA = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(6), trimmedA.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(6), trimmedA.SourceDuration);
        Assert.AreEqual(TimeSpan.Zero, trimmedA.SourceStart);
        // B ripples left to close the gap.
        Assert.AreEqual(TimeSpan.FromSeconds(6), model.Segments[1].Start);
    }

    [TestMethod]
    public void TrimLeftEdge_Video_AdvancesInPoint()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        // Shrink to 6s from the left → in-point advances by 4s.
        new TrimSegmentEdgeOperation(a.Id, fromStart: true, TimeSpan.FromSeconds(6)).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(6), trimmed.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(4), trimmed.SourceStart);
        Assert.AreEqual(TimeSpan.FromSeconds(6), trimmed.SourceDuration);
    }

    [TestMethod]
    public void TrimLeftEdge_GrowBeyondSourceStart_ClampsToZero()
    {
        // In-point already at source 3s; only 3s of head can be revealed.
        var a = Video(3, 7);
        var model = ModelWith(a);

        // Request growing to 12s (would need 5s extra head, only 3s available).
        new TrimSegmentEdgeOperation(a.Id, fromStart: true, TimeSpan.FromSeconds(12)).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.Zero, trimmed.SourceStart, "Source start clamps to zero");
        Assert.AreEqual(TimeSpan.FromSeconds(10), trimmed.Duration, "Duration limited by available head");
    }

    [TestMethod]
    public void TrimRightEdge_BelowMinimum_ClampsToMinDuration()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, TimeSpan.Zero).Execute(model);

        Assert.AreEqual(TrimSegmentEdgeOperation.MinDuration, model.Segments[0].Duration);
    }

    // ── Live trim-edge preview mapping (editor drags the edge; nothing is committed yet) ──

    [TestMethod]
    public void ResolveEdgePreview_LeftEdge_IsTheCommittedInPoint()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);
        var requested = TimeSpan.FromSeconds(6);

        var (duration, sourceTime) = TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: true, requested);

        new TrimSegmentEdgeOperation(a.Id, fromStart: true, requested).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(trimmed.SourceStart, sourceTime,
            "The in-edge preview must show the first frame the commit keeps");
        Assert.AreEqual(trimmed.Duration, duration);
    }

    [TestMethod]
    public void ResolveEdgePreview_LeftEdge_ReportsTheHeadClampedDurationNotTheRequestedOne()
    {
        // In-point at source 3s: growing by 5s is impossible, only 3s of head exists.
        var a = Video(3, 7);
        var model = ModelWith(a);
        var requested = TimeSpan.FromSeconds(12);

        var (duration, sourceTime) = TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: true, requested);

        new TrimSegmentEdgeOperation(a.Id, fromStart: true, requested).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.Zero, sourceTime);
        Assert.AreEqual(trimmed.SourceStart, sourceTime);
        Assert.AreEqual(TimeSpan.FromSeconds(10), duration,
            "The readout must show the duration the commit really produces, not the request");
        Assert.AreEqual(trimmed.Duration, duration);
    }

    [TestMethod]
    public void ResolveEdgePreview_RightEdge_IsOneTickInsideTheExclusiveOutPoint()
    {
        var a = Video(2, 10);
        var model = ModelWith(a);
        var requested = TimeSpan.FromSeconds(6);

        var (duration, sourceTime) = TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: false, requested);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, requested).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        var exclusiveOut = trimmed.SourceStart + trimmed.SourceDuration;
        Assert.AreEqual(exclusiveOut - TimeSpan.FromTicks(1), sourceTime);
        Assert.IsTrue(sourceTime < exclusiveOut,
            "The out-point is exclusive, so the previewed frame must fall inside the kept range");
        Assert.AreEqual(trimmed.Duration, duration);
    }

    [DataRow(24)]
    [DataRow(30)]
    [DataRow(60)]
    [DataTestMethod]
    public void ResolveEdgePreview_RightEdge_ResolvesToTheLastKeptFrameAtAnyFrameRate(int fps)
    {
        var a = Video(0, 10);

        // An out-point deliberately placed part-way through a frame: the last KEPT frame is
        // the one that begins before it, which is what floor(seconds x fps) — the mapping
        // every decoder in the app uses — must yield for the previewed instant.
        var requested = TimeSpan.FromSeconds(6) + TimeSpan.FromMilliseconds(5);
        var (_, sourceTime) = TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: false, requested);

        int expectedFrame = (int)((requested.TotalSeconds * fps) - 1e-9);
        Assert.AreEqual(expectedFrame, (int)(sourceTime.TotalSeconds * fps));
    }

    [TestMethod]
    public void ResolveEdgePreview_RightEdge_SpeedAdjusted_UsesSourceRate()
    {
        // 2x speed: 6s of output consumes 12s of footage from an in-point of 1s.
        var a = Video(1, 20, speed: 2.0);
        var model = ModelWith(a);
        var requested = TimeSpan.FromSeconds(6);

        var (duration, sourceTime) = TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: false, requested);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, requested).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(13) - TimeSpan.FromTicks(1), sourceTime);
        Assert.AreEqual(trimmed.SourceStart + trimmed.SourceDuration - TimeSpan.FromTicks(1), sourceTime);
        Assert.AreEqual(TimeSpan.FromSeconds(6), duration);
    }

    [TestMethod]
    public void ResolveEdgePreview_BelowMinimum_MatchesTheClampedCommit()
    {
        var a = Video(4, 10);
        var model = ModelWith(a);

        var (duration, sourceTime) =
            TrimSegmentEdgeOperation.ResolveEdgePreview(a, fromStart: false, TimeSpan.Zero);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, TimeSpan.Zero).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TrimSegmentEdgeOperation.MinDuration, duration);
        Assert.AreEqual(trimmed.Duration, duration);
        Assert.AreEqual(trimmed.SourceStart + trimmed.SourceDuration - TimeSpan.FromTicks(1), sourceTime);
        Assert.IsTrue(sourceTime > trimmed.SourceStart);
    }

    [TestMethod]
    public void TrimTextSlide_ChangesDurationOnly()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(5) };
        var video = Video(0, 10);
        var model = ModelWith(slide, video);

        new TrimSegmentEdgeOperation(slide.Id, fromStart: false, TimeSpan.FromSeconds(2)).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(2), model.Segments[0].Duration);
        // Following video ripples to start at 2s.
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.Segments[1].Start);
    }

    [TestMethod]
    public void Trim_Undo_RestoresOriginalSegment()
    {
        var a = Video(0, 10);
        var model = ModelWith(a);

        var op = new TrimSegmentEdgeOperation(a.Id, fromStart: false, TimeSpan.FromSeconds(4));
        op.Execute(model);
        Assert.AreEqual(TimeSpan.FromSeconds(4), model.Segments[0].Duration);

        op.Undo(model);
        var restored = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(10), restored.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), restored.SourceDuration);
    }
}
