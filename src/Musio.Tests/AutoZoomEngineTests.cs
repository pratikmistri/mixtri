namespace Musio.Tests;

using Musio.Core.Processing;
using Musio.Core.Models;
using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

[TestClass]
public sealed class AutoZoomEngineTests
{
    private const double TickFrequency = 10_000_000.0;

    private static MouseRecordingData BuildRecordingWithClicks(
        double durationSeconds,
        List<(double timeSeconds, int x, int y)> clicks)
        => TestMouseRecordingBuilder.WithClicks(durationSeconds, clicks, TickFrequency);

    [TestMethod]
    public void GetZoomState_NoClicks_ReturnsNoZoom()
    {
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var state = engine.GetZoomState(2.5);

        Assert.AreEqual(1.0f, state.ZoomLevel, 0.001f, "Zoom should be 1.0 with no clicks");
        Assert.AreEqual(1920f, state.ViewportWidth, 0.01f, "Viewport width should equal source width at 1x zoom");
        Assert.AreEqual(1080f, state.ViewportHeight, 0.01f, "Viewport height should equal source height at 1x zoom");
    }

    [TestMethod]
    public void GetZoomState_AtClick_ReturnsZoomedIn()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // At click time (end of zoom-in phase), should be fully or nearly fully zoomed
        var state = engine.GetZoomState(2.0);

        Assert.IsTrue(state.ZoomLevel > 1.5f,
            $"Expected zoom > 1.5 at click time, got {state.ZoomLevel}");

        // During hold phase (just after click), should be at target zoom
        var holdState = engine.GetZoomState(2.1);
        Assert.AreEqual(2.0f, holdState.ZoomLevel, 0.01f,
            $"Expected zoom = 2.0 during hold, got {holdState.ZoomLevel}");
    }

    [TestMethod]
    public void GetZoomState_AfterEaseOut_ReturnsNoZoom()
    {
        var config = new AutoZoomConfig
        {
            DefaultZoomLevel = 2.0f,
            PreClickDuration = 0.3f,
            HoldDuration = 0.5f,
            EaseOutDuration = 0.5f,
        };
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, [(1.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // click=1.0 + hold=0.5 + easeOut=0.5 → segment ends at 2.0
        // Well after the segment, zoom should be 1.0
        var state = engine.GetZoomState(3.0);

        Assert.AreEqual(1.0f, state.ZoomLevel, 0.01f,
            $"Expected zoom ≈ 1.0 after ease-out, got {state.ZoomLevel}");
    }

    [TestMethod]
    public void GetZoomState_ViewportClamped_StaysInBounds()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 3.0f };
        var engine = new AutoZoomEngine(config);
        // Click at top-left corner
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 0, 0)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var state = engine.GetZoomState(2.1); // during hold

        Assert.IsTrue(state.ViewportX >= 0,
            $"ViewportX should be >= 0, got {state.ViewportX}");
        Assert.IsTrue(state.ViewportY >= 0,
            $"ViewportY should be >= 0, got {state.ViewportY}");
        Assert.IsTrue(state.ViewportX + state.ViewportWidth <= 1920 + 0.01f,
            $"Viewport right edge ({state.ViewportX + state.ViewportWidth}) should be <= 1920");
        Assert.IsTrue(state.ViewportY + state.ViewportHeight <= 1080 + 0.01f,
            $"Viewport bottom edge ({state.ViewportY + state.ViewportHeight}) should be <= 1080");

        // Also test bottom-right corner click
        var recording2 = BuildRecordingWithClicks(5.0, [(2.0, 1920, 1080)]);
        engine.BuildZoomTimeline(recording2, 1920, 1080, TickFrequency);

        var state2 = engine.GetZoomState(2.1);

        Assert.IsTrue(state2.ViewportX >= 0,
            $"ViewportX should be >= 0 for corner click, got {state2.ViewportX}");
        Assert.IsTrue(state2.ViewportY >= 0,
            $"ViewportY should be >= 0 for corner click, got {state2.ViewportY}");
        Assert.IsTrue(state2.ViewportX + state2.ViewportWidth <= 1920 + 0.01f,
            $"Viewport right edge should be <= 1920 for corner click");
    }

    [TestMethod]
    public void GetZoomState_ManualKeyframe_OverridesAuto()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        // Auto click at t=2.0 with zoom=2.0
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Manual keyframe at same time with higher zoom=3.5
        engine.AddManualKeyframe(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2.0),
            ZoomLevel = 3.5,
            CenterX = 0.5,
            CenterY = 0.5,
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
        });

        // During hold phase of the manual keyframe
        var state = engine.GetZoomState(2.0);

        Assert.AreEqual(3.5f, state.ZoomLevel, 0.01f,
            $"Manual keyframe zoom (3.5) should override auto (2.0), got {state.ZoomLevel}");
    }

    [TestMethod]
    public void RemoveManualKeyframe_RemovesCorrectKeyframe()
    {
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var ts = TimeSpan.FromSeconds(2.0);
        engine.AddManualKeyframe(new ZoomKeyframe
        {
            Timestamp = ts,
            ZoomLevel = 3.0,
            CenterX = 0.5,
            CenterY = 0.5,
        });

        // Verify keyframe is active
        var before = engine.GetZoomState(2.0);
        Assert.AreEqual(3.0f, before.ZoomLevel, 0.01f);

        // Remove and verify it's gone
        engine.RemoveManualKeyframe(ts);
        var after = engine.GetZoomState(2.0);
        Assert.AreEqual(1.0f, after.ZoomLevel, 0.01f, "Zoom should be 1.0 after removing manual keyframe");
    }

    [TestMethod]
    public void GetZoomState_SuppressedClick_SkipsAutoZoom()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        // Click at t=2.0s with source tick = 2.0 * TickFrequency
        long clickTicks = (long)(2.0 * TickFrequency);
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Before suppression: zoom is active at click time
        var before = engine.GetZoomState(2.1);
        Assert.AreEqual(2.0f, before.ZoomLevel, 0.01f,
            "Expected zoom = 2.0 at click time before suppression");

        // Suppress the click
        engine.SetSuppressedClickTicks([clickTicks]);

        // After suppression: zoom should be 1.0
        var after = engine.GetZoomState(2.1);
        Assert.AreEqual(1.0f, after.ZoomLevel, 0.01f,
            "Zoom should be 1.0 after suppressing the auto-zoom click");
    }

    [TestMethod]
    public void GetZoomState_SuppressOneOfTwoClicks_OnlyOneZooms()
    {
        var config = new AutoZoomConfig
        {
            DefaultZoomLevel = 2.0f,
            MinTimeBetweenZooms = 0.5f,
        };
        var engine = new AutoZoomEngine(config);
        // Two clicks far apart: t=1.0 and t=4.0
        long click1Ticks = (long)(1.0 * TickFrequency);
        var recording = BuildRecordingWithClicks(6.0, [(1.0, 200, 200), (4.0, 800, 600)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Both clicks produce zoom before suppression
        Assert.AreEqual(2.0f, engine.GetZoomState(1.1).ZoomLevel, 0.01f);
        Assert.AreEqual(2.0f, engine.GetZoomState(4.1).ZoomLevel, 0.01f);

        // Suppress only the first click
        engine.SetSuppressedClickTicks([click1Ticks]);

        // First click: no zoom; second click: still zoomed
        Assert.AreEqual(1.0f, engine.GetZoomState(1.1).ZoomLevel, 0.01f,
            "Suppressed click should not produce zoom");
        Assert.AreEqual(2.0f, engine.GetZoomState(4.1).ZoomLevel, 0.01f,
            "Non-suppressed click should still zoom");
    }

    [TestMethod]
    public void RemoveZoomKeyframeOperation_SuppressesAutoClick()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        long clickTicks = 20_000_000; // 2.0s at 10MHz

        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2.0),
            ZoomLevel = 2.0,
            CenterX = 0.5,
            CenterY = 0.5,
            SourceClickTicks = clickTicks,
        });

        var op = new Core.Timeline.RemoveZoomKeyframeOperation(model.ZoomKeyframes[0].Id);
        op.Execute(model);

        Assert.AreEqual(0, model.ZoomKeyframes.Count, "Keyframe should be removed");
        Assert.IsTrue(model.SuppressedClickTicks.Contains(clickTicks),
            "Source click should be suppressed after removing auto keyframe");

        // Undo should restore both the keyframe and remove the suppression
        op.Undo(model);
        Assert.AreEqual(1, model.ZoomKeyframes.Count, "Keyframe should be restored on undo");
        Assert.IsFalse(model.SuppressedClickTicks.Contains(clickTicks),
            "Suppression should be removed on undo");
    }

    [TestMethod]
    public void SpringInterpolate_ConvergesToTarget()
    {
        float current = 1.0f;
        float target = 2.0f;

        // Simulate 100 steps
        for (int i = 0; i < 100; i++)
            current = AutoZoomEngine.SpringInterpolate(current, target, 200f, 20f, 0.016f);

        Assert.AreEqual(target, current, 0.01f,
            $"SpringInterpolate should converge to target, got {current}");
    }

    [TestMethod]
    public void GetZoomState_OverlappingManualKeyframes_NoJump()
    {
        // Regression test: two overlapping manual keyframes should transition
        // seamlessly — the higher zoom wins, preventing a snap/jump.
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(10.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Keyframe A at t=2.0, keyframe B at t=3.0
        // With default durations (pre=345ms, hold=575ms, post=575ms):
        //   A: active from 1.655 to 3.15 (zoom-out from 2.575 to 3.15)
        //   B: active from 2.655 to 4.15 (zoom-in from 2.655 to 3.0)
        // Overlap zone: 2.655 to 3.15
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 2.0,
                CenterX = 0.3,
                CenterY = 0.3,
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.0,
                CenterX = 0.7,
                CenterY = 0.7,
            },
        ]);

        // Sample through the overlap zone: zoom should never dip below 1.0
        // and the transition should be monotonically smooth (no sudden jumps)
        float prevZoom = 0;
        bool hadJump = false;
        for (double t = 2.5; t <= 3.5; t += 0.01)
        {
            var state = engine.GetZoomState(t);
            Assert.IsTrue(state.ZoomLevel >= 1.0f,
                $"Zoom at t={t:F2} should be >= 1.0, got {state.ZoomLevel}");

            // A large sudden drop (> 0.3 in 10ms) indicates a visual jump
            if (prevZoom > 0 && prevZoom - state.ZoomLevel > 0.3f)
                hadJump = true;

            prevZoom = state.ZoomLevel;
        }

        Assert.IsFalse(hadJump,
            "Zoom should not have sudden drops during overlapping keyframe transition");
    }

    [TestMethod]
    public void GetZoomState_OverlappingManualKeyframes_CenterDoesNotSnap()
    {
        // Regression: when two overlapping keyframes have different centers,
        // the focal point should glide smoothly from A to B rather than
        // snapping at the moment B's zoom first exceeds A's.
        const int W = 1920;
        const int H = 1080;
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(10.0, []);
        engine.BuildZoomTimeline(recording, W, H, TickFrequency);

        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 2.0,
                CenterX = 0.3,
                CenterY = 0.3,
                PreDuration = TimeSpan.FromMilliseconds(450),
                HoldDuration = TimeSpan.FromMilliseconds(600),
                PostDuration = TimeSpan.FromMilliseconds(700),
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.0,
                CenterX = 0.7,
                CenterY = 0.7,
                PreDuration = TimeSpan.FromMilliseconds(450),
                HoldDuration = TimeSpan.FromMilliseconds(600),
                PostDuration = TimeSpan.FromMilliseconds(700),
            },
        ]);

        // Step through and assert per-step center movement is bounded.
        // With smoothed blending across a ~500ms overlap, peak rate is roughly
        // (W * 0.4) / 0.5s ≈ 1.5 px/ms ≈ 8 px per 5ms linear, ~16 px with
        // cubic-bezier ease. Pick 60 px as a generous-but-meaningful threshold
        // — well below the old ~768 px hard-switch snap (B's center minus A's
        // center across the crossover), while still catching any future
        // regression that re-introduces a jump.
        const double step = 0.005;
        const float maxStepPixels = 60f;
        float prevCx = -1, prevCy = -1;
        for (double t = 2.0; t <= 3.6; t += step)
        {
            var state = engine.GetZoomState(t);
            float cx = state.CenterX;
            float cy = state.CenterY;
            if (prevCx >= 0)
            {
                Assert.IsTrue(Math.Abs(cx - prevCx) < maxStepPixels,
                    $"Center X jumped {Math.Abs(cx - prevCx):F1}px at t={t:F3}");
                Assert.IsTrue(Math.Abs(cy - prevCy) < maxStepPixels,
                    $"Center Y jumped {Math.Abs(cy - prevCy):F1}px at t={t:F3}");
            }
            prevCx = cx;
            prevCy = cy;
        }

        // Endpoint assertions: outside the overlap, each keyframe should own
        // the focal point — verifies the blend doesn't bias the result.
        // At t = 2.0 (kf A's timestamp, hold phase, kf B not yet active),
        // center must equal A's center.
        var stateA = engine.GetZoomState(2.0);
        Assert.AreEqual(0.3f * W, stateA.CenterX, 1f, "Center at A's timestamp should be A's center");
        Assert.AreEqual(0.3f * H, stateA.CenterY, 1f, "Center at A's timestamp should be A's center");

        // At t = 3.0 (kf B's timestamp, hold phase, kf A's post-ease finished
        // at 2.0 + hold(0.575) + post(0.575) = 3.15 — A is still tailing off
        // here, but B's weight at hold-peak dominates by ~3x). Use a looser
        // tolerance reflecting the still-active tail.
        var stateB = engine.GetZoomState(3.0);
        Assert.IsTrue(stateB.CenterX > 0.55f * W,
            $"Center at B's timestamp should be near B's center (0.7*W=1344), got {stateB.CenterX}");

        // After both segments end (B's post-ease finishes at 3.0 + 0.575 + 0.575
        // = 4.15), no manual override → falls through to auto (empty), so
        // zoom returns to 1.0.
        var stateAfter = engine.GetZoomState(4.5);
        Assert.AreEqual(1.0f, stateAfter.ZoomLevel, 0.01f,
            "Zoom should return to 1.0 after all segments end");
    }

    [TestMethod]
    public void GetZoomState_ClickBeforeVideoStart_DoesNotZoom()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        // Click lands 0.2s into the mouse recording, but capture only started 0.5s in, so
        // it maps to -0.3s — before the first video frame.
        var recording = BuildRecordingWithClicks(5.0, [(0.2, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency,
            timeOffsetSeconds: 0.5, durationSeconds: 5.0);

        // The pre-roll click's hold and ease-out still overlap the start of the video, so
        // without the range check the preview zooms while the editor shows no segment on
        // the zoom track — leaving the user no way to select or delete it.
        Assert.AreEqual(1.0f, engine.GetZoomState(0.0).ZoomLevel, 0.001f,
            "A click before the video started must not zoom the first frame");
        Assert.AreEqual(1.0f, engine.GetZoomState(0.5).ZoomLevel, 0.001f,
            "A click before the video started must not zoom during its hold window");
    }

    [TestMethod]
    public void GetZoomState_ClickAfterVideoEnd_DoesNotZoom()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        // Click 0.4s past the end of a 5s video; its anticipatory zoom-in would otherwise
        // begin at 4.4s and zoom the tail of the video.
        var recording = BuildRecordingWithClicks(5.0, [(5.4, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency, durationSeconds: 5.0);

        Assert.AreEqual(1.0f, engine.GetZoomState(4.9).ZoomLevel, 0.001f,
            "A click past the end of the video must not zoom its final frames");
    }

    [TestMethod]
    public void GetZoomState_ClickInsideVideo_StillZoomsWhenDurationKnown()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var recording = BuildRecordingWithClicks(5.0, [(2.5, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency, durationSeconds: 5.0);

        Assert.IsTrue(engine.GetZoomState(2.5).ZoomLevel > 1.5f,
            "Passing a duration must not stop in-range clicks from zooming");
    }

    [TestMethod]
    public void GetZoomState_NoDurationSupplied_KeepsEveryInRangeClick()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var recording = BuildRecordingWithClicks(5.0, [(4.9, 960, 540)]);
        // durationSeconds defaults to 0, meaning "unknown" — only the negative check applies.
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        Assert.IsTrue(engine.GetZoomState(4.9).ZoomLevel > 1.5f,
            "With no duration supplied, clicks must not be filtered by an upper bound");
    }
}