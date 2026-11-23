using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for the 2026-11 code-review fixes in the timeline model and its edit
/// operations: undo/redo exception safety and depth cap, no-op reporting, id-based undo,
/// zoom resize clamping, base-chain-only transitions, detached-audio links surviving a split,
/// trim bounds, and TimelineClip overflow.
/// </summary>
[TestClass]
public sealed class ModelReviewTimelineFixTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, string path = PrimaryPath)
        => TestTimelineBuilder.Video(path, srcStartSec, srcDurSec);

    private sealed class FakeOperation : IEditOperation
    {
        public bool ThrowOnExecute { get; set; }
        public bool ThrowOnUndo { get; set; }
        public int ExecuteCount { get; private set; }
        public int UndoCount { get; private set; }
        public string Description { get; init; } = "Fake";

        public void Execute(TimelineModel model)
        {
            if (ThrowOnExecute) throw new InvalidOperationException("execute failed");
            ExecuteCount++;
        }

        public void Undo(TimelineModel model)
        {
            if (ThrowOnUndo) throw new InvalidOperationException("undo failed");
            UndoCount++;
        }
    }

    // ── UndoRedoManager ──────────────────────────────────────────────

    [TestMethod]
    public void Execute_Throwing_LeavesStacksUntouchedAndReportsEdit()
    {
        var manager = new UndoRedoManager(new TimelineModel());
        manager.Execute(new FakeOperation { Description = "first" });
        manager.Undo(); // something on the redo stack

        int edits = 0;
        manager.EditPerformed += (_, _) => edits++;

        Assert.ThrowsException<InvalidOperationException>(
            () => manager.Execute(new FakeOperation { ThrowOnExecute = true }));

        Assert.IsFalse(manager.CanUndo, "a failed execute must not be pushed");
        Assert.IsTrue(manager.CanRedo, "a failed execute must not clear the redo stack");
        Assert.AreEqual(1, edits, "a possibly-partial change must still mark the project dirty");
    }

    [TestMethod]
    public void Undo_Throwing_KeepsOperationOnUndoStack()
    {
        var manager = new UndoRedoManager(new TimelineModel());
        var op = new FakeOperation();
        manager.Execute(op);
        op.ThrowOnUndo = true;

        Assert.ThrowsException<InvalidOperationException>(() => manager.Undo());

        Assert.IsTrue(manager.CanUndo);
        Assert.IsFalse(manager.CanRedo);
    }

    [TestMethod]
    public void Redo_Throwing_KeepsOperationOnRedoStack()
    {
        var manager = new UndoRedoManager(new TimelineModel());
        var op = new FakeOperation();
        manager.Execute(op);
        manager.Undo();
        op.ThrowOnExecute = true;

        Assert.ThrowsException<InvalidOperationException>(() => manager.Redo());

        Assert.IsTrue(manager.CanRedo);
        Assert.IsFalse(manager.CanUndo);
    }

    [TestMethod]
    public void UndoStack_IsCappedAndDropsOldest()
    {
        var manager = new UndoRedoManager(new TimelineModel(), maxUndoDepth: 3);
        for (int i = 0; i < 5; i++)
            manager.Execute(new FakeOperation { Description = $"op{i}" });

        Assert.AreEqual(3, manager.UndoCount);
        Assert.AreEqual("op4", manager.UndoDescription);

        manager.Undo();
        manager.Undo();
        Assert.AreEqual("op2", manager.UndoDescription, "the newest entries survive the cap");
        manager.Undo();
        Assert.IsFalse(manager.CanUndo, "op0 and op1 were dropped");
    }

    [TestMethod]
    public void DefaultUndoDepth_Is200()
    {
        var manager = new UndoRedoManager(new TimelineModel());
        for (int i = 0; i < UndoRedoManager.DefaultMaxUndoDepth + 10; i++)
            manager.Execute(new FakeOperation());

        Assert.AreEqual(UndoRedoManager.DefaultMaxUndoDepth, manager.UndoCount);
    }

    // ── SplitSegmentAtTimeOperation no-op ────────────────────────────

    [TestMethod]
    public void Split_OnUnsplittableSegmentType_IsReportedAsNoOp()
    {
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath,
            new CameraSegment { Duration = TimeSpan.FromSeconds(10) });
        var manager = new UndoRedoManager(model);

        manager.Execute(new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, model.Segments.Count);
        Assert.IsFalse(manager.CanUndo, "a split that split nothing must not push an undo entry");
    }

    // ── Undo by id rather than stored index ──────────────────────────

    [TestMethod]
    public void TrimUndo_FindsSegmentById_AfterListShifted()
    {
        var a = Video(0, 5);
        var b = Video(5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, a, b);

        var trim = new TrimSegmentEdgeOperation(b.Id, fromStart: false, TimeSpan.FromSeconds(2));
        trim.Execute(model);

        // Something shifts the list under the operation (index 1 is now `a`).
        var inserted = Video(20, 1);
        model.Segments.Insert(0, inserted);

        trim.Undo(model);

        Assert.AreSame(inserted, model.Segments[0]);
        Assert.AreSame(a, model.Segments[1], "the unrelated segment at the stale index must be untouched");
        Assert.AreSame(b, model.Segments[2], "the trimmed segment is restored where it now lives");
    }

    [TestMethod]
    public void SpeedUndo_FindsSegmentById_AfterListShifted()
    {
        var a = Video(0, 5);
        var b = Video(5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, a, b);

        var speed = new ChangeSegmentSpeedOperation(b.Id, 2.0);
        speed.Execute(model);
        model.Segments.Insert(0, Video(20, 1));

        speed.Undo(model);

        Assert.AreSame(a, model.Segments[1]);
        Assert.AreSame(b, model.Segments[2]);
    }

    [TestMethod]
    public void AudioModeUndo_FindsSegmentById_AfterListShifted()
    {
        var a = Video(0, 5);
        var b = Video(5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, a, b);

        var op = new ChangeSegmentAudioModeOperation(b.Id, SegmentAudioMode.Muted);
        op.Execute(model);
        model.Segments.Insert(0, Video(20, 1));

        op.Undo(model);

        Assert.AreEqual(SegmentAudioMode.TimeStretch, b.AudioMode);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, a.AudioMode);
    }

    [TestMethod]
    public void TrimUndo_SegmentGone_LeavesWhateverIsAtTheOldIndexUntouched()
    {
        var a = Video(0, 5);
        var b = Video(5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, a, b);

        var trim = new TrimSegmentEdgeOperation(b.Id, fromStart: false, TimeSpan.FromSeconds(2));
        trim.Execute(model);

        // The trimmed segment disappears and an unrelated clip takes its old index.
        var unrelated = Video(20, 1);
        model.Segments.RemoveAt(1);
        model.Segments.Insert(1, unrelated);

        trim.Undo(model);

        Assert.AreSame(unrelated, model.Segments[1], "no positional fallback onto an unrelated clip");
        Assert.AreSame(a, model.Segments[0]);
    }

    // ── ResizeZoomSegmentOperation clamping ──────────────────────────

    [TestMethod]
    public void ResizeZoomRightEdge_AppendedRecordingKeyframe_NotClampedToPrimaryDuration()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            PrimaryVideoFilePath = PrimaryPath,
        };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(30),
            SourceVideoFilePath = "appended.mp4",
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
        };
        model.ZoomKeyframes.Add(kf);

        new ResizeZoomSegmentOperation(kf.Id, resizeStart: false, TimeSpan.FromSeconds(35)).Execute(model);

        var resized = model.ZoomKeyframes[0];
        Assert.IsTrue(resized.PostDuration >= TimeSpan.Zero);
        Assert.IsTrue(resized.HoldDuration >= TimeSpan.Zero);
        Assert.AreEqual(TimeSpan.FromSeconds(35), resized.Timestamp + resized.HoldDuration + resized.PostDuration);
    }

    [TestMethod]
    public void ResizeZoomRightEdge_NearPrimaryEnd_KeepsMinimumAndNonNegativeDurations()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            PrimaryVideoFilePath = PrimaryPath,
        };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(9.95),
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
        };
        model.ZoomKeyframes.Add(kf);

        new ResizeZoomSegmentOperation(kf.Id, resizeStart: false, TimeSpan.FromSeconds(20)).Execute(model);

        var resized = model.ZoomKeyframes[0];
        Assert.IsTrue(resized.PostDuration >= TimeSpan.Zero, "PostDuration must never go negative");
        Assert.IsTrue(resized.HoldDuration >= TimeSpan.Zero);
        Assert.IsTrue(resized.HoldDuration + resized.PostDuration >= ZoomKeyframe.MinSegmentDuration);
    }

    [TestMethod]
    public void ResizeZoomRightEdge_PrimaryKeyframe_StillClampedToRecordingEnd()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            PrimaryVideoFilePath = PrimaryPath,
        };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
        };
        model.ZoomKeyframes.Add(kf);

        new ResizeZoomSegmentOperation(kf.Id, resizeStart: false, TimeSpan.FromSeconds(20)).Execute(model);

        var resized = model.ZoomKeyframes[0];
        Assert.AreEqual(TimeSpan.FromSeconds(10), resized.Timestamp + resized.HoldDuration + resized.PostDuration);
    }

    // ── Transitions use the base-track chain ─────────────────────────

    private static VideoSegment Overlay(double startSec, double durSec)
    {
        var overlay = TestTimelineBuilder.TransitionVideo("C:\\overlay.mp4", durSec);
        overlay.TrackIndex = 1;
        overlay.Start = TimeSpan.FromSeconds(startSec);
        return overlay;
    }

    private static TransitionConfig CrossFade() => new()
    {
        Type = TransitionType.CrossFade,
        Duration = TimeSpan.FromMilliseconds(500),
    };

    [TestMethod]
    public void UpdateTransition_OnOverlaySegment_IsRefused()
    {
        var a = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var overlay = Overlay(1, 2);
        var b = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var model = TestTimelineBuilder.ModelWith(a, overlay, b);

        var op = new UpdateTransitionOperation(overlay.Id, CrossFade());
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel);
        Assert.IsNull(overlay.InTransition);
    }

    [TestMethod]
    public void UpdateTransition_OnFirstBaseSegmentAfterAnOverlay_IsRefused()
    {
        var overlay = Overlay(1, 2);
        var a = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var b = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var model = TestTimelineBuilder.ModelWith(overlay, a, b);

        var first = new UpdateTransitionOperation(a.Id, CrossFade());
        first.Execute(model);
        Assert.IsFalse(first.ChangedModel, "list index 1 is still the FIRST base segment");
        Assert.IsNull(a.InTransition);

        var second = new UpdateTransitionOperation(b.Id, CrossFade());
        second.Execute(model);
        Assert.IsTrue(second.ChangedModel);
        Assert.IsNotNull(b.InTransition);
    }

    [TestMethod]
    public void ApplyTransitionToAll_SkipsOverlaysAndFirstBaseSegment()
    {
        var overlay = Overlay(1, 2);
        var a = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var b = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var c = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var model = TestTimelineBuilder.ModelWith(overlay, a, b, c);

        var op = new ApplyTransitionToAllBoundariesOperation(CrossFade());
        op.Execute(model);

        Assert.IsNull(overlay.InTransition);
        Assert.IsNull(a.InTransition);
        Assert.IsNotNull(b.InTransition);
        Assert.IsNotNull(c.InTransition);

        op.Undo(model);
        Assert.IsNull(b.InTransition);
        Assert.IsNull(c.InTransition);
    }

    [TestMethod]
    public void Resolver_SkipsOverlayBetweenBaseSegments()
    {
        var a = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5);
        var overlay = Overlay(4, 3);
        var b = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5, CrossFade());
        var model = TestTimelineBuilder.ModelWith(a, overlay, b);

        var resolution = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.1));

        Assert.IsTrue(resolution.Active);
        Assert.AreSame(b, resolution.IncomingSegment);
        Assert.AreSame(a, resolution.OutgoingSegment);
    }

    [TestMethod]
    public void Resolver_FirstBaseSegment_HasNoTransition()
    {
        var overlay = Overlay(0, 3);
        var a = TestTimelineBuilder.TransitionVideo("C:\\clip.mp4", 5, CrossFade());
        var model = TestTimelineBuilder.ModelWith(overlay, a);

        Assert.IsFalse(TransitionResolver.Resolve(model, TimeSpan.FromSeconds(0.1)).Active);
    }

    // ── Detached audio survives a split ──────────────────────────────

    [TestMethod]
    public void Split_OfSegmentWithDetachedAudio_KeepsReattachReachable()
    {
        var video = Video(0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, video);
        var manager = new UndoRedoManager(model);

        manager.Execute(new DetachSegmentAudioOperation(video.Id,
            [new DetachedAudioSource("system.wav", "System", 0, TimeSpan.Zero)]));
        Assert.AreEqual(1, model.AudioTracks.Count);

        manager.Execute(new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)));
        Assert.AreEqual(2, model.Segments.Count);

        var firstHalf = model.Segments[0];
        Assert.AreEqual(video.Id, firstHalf.Id, "the first half inherits the id the blocks link to");
        Assert.AreNotEqual(video.Id, model.Segments[1].Id);
        Assert.IsTrue(ReattachSegmentAudioOperation.HasDetachedAudio(model, firstHalf.Id));

        manager.Execute(new ReattachSegmentAudioOperation(firstHalf.Id));
        Assert.AreEqual(0, model.AudioTracks.Count);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode);
    }

    [TestMethod]
    public void Split_WithoutDetachedAudio_StillMintsFreshIds()
    {
        var video = Video(0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, video);

        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)).Execute(model);

        Assert.AreNotEqual(video.Id, model.Segments[0].Id);
        Assert.AreNotEqual(video.Id, model.Segments[1].Id);
    }

    [TestMethod]
    public void DetachUndo_FindsSegmentById_AfterListShifted()
    {
        var a = Video(0, 5);
        var b = Video(5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, a, b);

        var detach = new DetachSegmentAudioOperation(b.Id,
            [new DetachedAudioSource("system.wav", "System", 0, TimeSpan.Zero)]);
        detach.Execute(model);
        Assert.AreEqual(SegmentAudioMode.Muted, b.AudioMode);

        model.Segments.Insert(0, Video(20, 1));
        detach.Undo(model);

        Assert.AreEqual(SegmentAudioMode.TimeStretch, b.AudioMode);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, a.AudioMode);
    }

    // ── Trim out-edge bounded by the recording ───────────────────────

    [TestMethod]
    public void TrimRightEdge_PrimarySegment_CannotExtendPastRecordingEnd()
    {
        var video = Video(2, 6); // source 2..8 of a 10 s recording
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, video);
        model.Duration = TimeSpan.FromSeconds(10);

        new TrimSegmentEdgeOperation(video.Id, fromStart: false, TimeSpan.FromSeconds(20)).Execute(model);

        var trimmed = (VideoSegment)model.Segments[0];
        Assert.AreEqual(TimeSpan.FromSeconds(8), trimmed.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(8), trimmed.Duration);
    }

    [TestMethod]
    public void TrimRightEdge_AppendedSegment_StaysUnbounded()
    {
        var video = Video(0, 6, "appended.mp4");
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, video);
        model.Duration = TimeSpan.FromSeconds(10);

        new TrimSegmentEdgeOperation(video.Id, fromStart: false, TimeSpan.FromSeconds(20)).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Segments[0].Duration);
    }

    [TestMethod]
    public void TrimRightEdge_ShrinkIsUnaffectedByBound()
    {
        var video = Video(0, 10);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, video);
        model.Duration = TimeSpan.FromSeconds(10);

        new TrimSegmentEdgeOperation(video.Id, fromStart: false, TimeSpan.FromSeconds(4)).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(4), model.Segments[0].Duration);
    }

    // ── TimelineClip.SourceDuration overflow ─────────────────────────

    [TestMethod]
    public void TimelineClipSourceDuration_SaturatesInsteadOfOverflowing()
    {
        var huge = new TimelineClip(TimeSpan.Zero, TimeSpan.FromTicks(long.MaxValue / 2), "huge")
        {
            SpeedFactor = 10,
        };
        Assert.AreEqual(TimeSpan.MaxValue, huge.SourceDuration);

        var negative = new TimelineClip(TimeSpan.Zero, TimeSpan.FromTicks(long.MaxValue / 2), "neg")
        {
            SpeedFactor = -10,
        };
        Assert.AreEqual(TimeSpan.MinValue, negative.SourceDuration);

        var normal = new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(4), "ok") { SpeedFactor = 2 };
        Assert.AreEqual(TimeSpan.FromSeconds(8), normal.SourceDuration);
    }
}
