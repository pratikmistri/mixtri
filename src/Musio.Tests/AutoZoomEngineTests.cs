namespace Musio.Tests;

using Musio.Core.Processing;
using Musio.Core.Models;
using Musio.Core.Timeline;

[TestClass]
public sealed class AutoZoomEngineTests
{
    private const double TickFrequency = 10_000_000.0;

    private static MouseRecordingData BuildRecordingWithClicks(
        double durationSeconds,
        List<(double timeSeconds, int x, int y)> clicks)
    {
        long startTick = 0;
        long endTick = (long)(durationSeconds * TickFrequency);
        int sampleCount = Math.Max(2, (int)(durationSeconds * 100));

        var samples = new List<MouseSample>();
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i * durationSeconds / (sampleCount - 1);
            samples.Add(new MouseSample
            {
                TimestampTicks = (long)(t * TickFrequency),
                X = 960,
                Y = 540,
                EventKind = MouseEventKind.Move,
                Button = MouseButton.None,
                ScrollDelta = 0,
            });
        }

        var clickEvents = clicks.Select(c => new ClickEvent(
            TimestampTicks: (long)(c.timeSeconds * TickFrequency),
            X: c.x,
            Y: c.y,
            Button: MouseButton.Left,
            IsDown: true
        )).ToList();

        return new MouseRecordingData
        {
            Samples = samples,
            Clicks = clickEvents,
            StartTimestampTicks = startTick,
            EndTimestampTicks = endTick,
            TickFrequency = TickFrequency,
        };
    }

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
}
