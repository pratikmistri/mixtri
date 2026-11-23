using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Undo coalescing for continuous edits (slider drags) and the overlay-lane / slide-insert
/// guards in the segment operations.
/// </summary>
[TestClass]
public sealed class UndoMergeAndOverlayLaneTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static VideoSegment Video(double start, double duration, int track = 0) => new()
    {
        VideoFilePath = "video.mp4",
        Start = S(start),
        Duration = S(duration),
        SourceStart = TimeSpan.Zero,
        SourceDuration = S(duration),
        TrackIndex = track,
    };

    // ── UndoRedoManager merge ───────────────────────────────────────────

    private static (TimelineModel Model, UndoRedoManager Manager, TextOverlaySegment Overlay) OverlayFixture()
    {
        var model = new TimelineModel();
        var overlay = new TextOverlaySegment { Start = S(0), Duration = S(5), WidthFraction = 0.5 };
        model.TextOverlays.Add(overlay);
        return (model, new UndoRedoManager(model), overlay);
    }

    private static UpdateTextOverlayPropertiesOperation Width(
        string id, double value, string key = "w", TimeSpan? window = null) =>
        new(id, o => o.WidthFraction = value, "Change Overlay Width", key, window ?? TimeSpan.FromHours(1));

    [TestMethod]
    public void Merge_DragTicks_CollapseIntoOneUndoEntryRestoringTheOriginal()
    {
        var (model, manager, overlay) = OverlayFixture();

        manager.Execute(Width(overlay.Id, 0.6));
        manager.Execute(Width(overlay.Id, 0.7));
        manager.Execute(Width(overlay.Id, 0.8));

        Assert.AreEqual(1, manager.UndoCount);
        Assert.AreEqual(0.8, model.TextOverlays[0].WidthFraction);

        manager.Undo();
        Assert.AreEqual(0.5, model.TextOverlays[0].WidthFraction);
        Assert.IsFalse(manager.CanUndo);
    }

    [TestMethod]
    public void Merge_RedoAfterMergedUndo_ReappliesTheFinalValue()
    {
        var (model, manager, overlay) = OverlayFixture();

        manager.Execute(Width(overlay.Id, 0.6));
        manager.Execute(Width(overlay.Id, 0.9));
        manager.Undo();
        manager.Redo();

        Assert.AreEqual(0.9, model.TextOverlays[0].WidthFraction);
        manager.Undo();
        Assert.AreEqual(0.5, model.TextOverlays[0].WidthFraction);
    }

    [TestMethod]
    public void Merge_DifferentKeyOrOverlay_StaysSeparate()
    {
        var (model, manager, overlay) = OverlayFixture();
        var other = new TextOverlaySegment { Start = S(6), Duration = S(2) };
        model.TextOverlays.Add(other);

        manager.Execute(Width(overlay.Id, 0.6, "w"));
        manager.Execute(Width(overlay.Id, 0.7, "h"));
        manager.Execute(Width(other.Id, 0.7, "h"));

        Assert.AreEqual(3, manager.UndoCount);
    }

    [TestMethod]
    public void Merge_WithoutKeyOrOutsideWindow_StaysSeparate()
    {
        var (_, manager, overlay) = OverlayFixture();

        manager.Execute(new UpdateTextOverlayPropertiesOperation(overlay.Id, o => o.WidthFraction = 0.6, "a"));
        manager.Execute(new UpdateTextOverlayPropertiesOperation(overlay.Id, o => o.WidthFraction = 0.7, "a"));
        Assert.AreEqual(2, manager.UndoCount);

        manager.Execute(Width(overlay.Id, 0.8, "k", TimeSpan.Zero));
        manager.Execute(Width(overlay.Id, 0.9, "k", TimeSpan.Zero));
        Assert.AreEqual(4, manager.UndoCount);
    }

    [TestMethod]
    public void Merge_NotAppliedWhileARedoIsPending()
    {
        var (model, manager, overlay) = OverlayFixture();

        manager.Execute(Width(overlay.Id, 0.6));
        manager.Execute(new UpdateTextOverlayPropertiesOperation(overlay.Id, o => o.Text = "x", "text"));
        manager.Undo();
        manager.Execute(Width(overlay.Id, 0.7));

        // A pending redo means the top entry does not directly precede this edit: no merge.
        Assert.IsFalse(manager.CanRedo);
        Assert.AreEqual(2, manager.UndoCount);
        Assert.AreEqual(0.7, model.TextOverlays[0].WidthFraction);
        manager.Undo();
        Assert.AreEqual(0.6, model.TextOverlays[0].WidthFraction);
    }

    // ── Overlay lane clamps ─────────────────────────────────────────────

    [TestMethod]
    public void TrimOverlayTail_DoesNotRunIntoNextLaneMate()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 60));
        var a = Video(0, 10, track: 1);
        var b = Video(12, 8, track: 1);
        model.Segments.Add(a);
        model.Segments.Add(b);

        new TrimSegmentEdgeOperation(a.Id, fromStart: false, S(20)).Execute(model);

        var trimmed = model.Segments.First(s => s.Id == a.Id);
        Assert.IsTrue(trimmed.End <= b.Start, $"Trimmed overlay ends at {trimmed.End}, lane-mate starts at {b.Start}.");
        Assert.AreEqual(S(12), trimmed.End);
    }

    [TestMethod]
    public void TrimOverlayHead_DoesNotReachBackOverPreviousLaneMate()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 60));
        var a = Video(0, 10, track: 1);
        var b = Video(12, 8, track: 1);
        model.Segments.Add(a);
        model.Segments.Add(b);

        new TrimSegmentEdgeOperation(b.Id, fromStart: true, S(18)).Execute(model);

        var trimmed = model.Segments.First(s => s.Id == b.Id);
        Assert.IsTrue(trimmed.Start >= a.End, $"Trimmed overlay starts at {trimmed.Start}, lane-mate ends at {a.End}.");
        Assert.AreEqual(S(20), trimmed.End);
    }

    [TestMethod]
    public void SlowOverlay_DoesNotRunIntoNextLaneMate()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 60));
        var a = Video(0, 10, track: 1);
        var b = Video(12, 8, track: 1);
        model.Segments.Add(a);
        model.Segments.Add(b);

        new ChangeSegmentSpeedOperation(a.Id, 0.5).Execute(model);

        var changed = model.Segments.First(s => s.Id == a.Id);
        Assert.IsTrue(changed.End <= b.Start, $"Slowed overlay ends at {changed.End}, lane-mate starts at {b.Start}.");
    }

    // ── SplitAndInsertTextSlide ─────────────────────────────────────────

    [TestMethod]
    public void SplitAndInsertSlide_MidClip_SplitsBaseIntoTwoHalvesAroundTheSlide()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 10));
        var slide = new TextSlideSegment { Duration = S(2) };

        new SplitAndInsertTextSlideOperation(S(4), slide).Execute(model);

        Assert.AreEqual(3, model.Segments.Count);
        Assert.AreSame(slide, model.Segments[1]);
        Assert.AreEqual(S(4), model.Segments[0].Duration);
        Assert.AreEqual(S(6), model.Segments[2].Duration);
    }

    [TestMethod]
    public void SplitAndInsertSlide_NearClipEdge_SnapsInsteadOfCreatingASliver()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 10));
        var slide = new TextSlideSegment { Duration = S(2) };

        new SplitAndInsertTextSlideOperation(S(0.01), slide).Execute(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(slide, model.Segments[0]);
    }

    [TestMethod]
    public void SplitAndInsertSlide_OverOverlay_SplitsTheBaseClipNotTheOverlay()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 10));
        var overlay = Video(2, 6, track: 1);
        model.Segments.Add(overlay);
        var slide = new TextSlideSegment { Duration = S(2) };

        new SplitAndInsertTextSlideOperation(S(5), slide).Execute(model);

        var overlayAfter = model.Segments.Single(s => s.Id == overlay.Id);
        Assert.AreEqual(S(6), overlayAfter.Duration);
        Assert.AreEqual(3, model.Segments.Count(s => s.TrackIndex == 0));
    }
}
