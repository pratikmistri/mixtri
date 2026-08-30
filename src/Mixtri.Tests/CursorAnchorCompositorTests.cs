using Microsoft.Graphics.Canvas;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// End-to-end cover for cursor anchors reaching the compositor: the conversions between an
/// anchor's STORAGE form (source time + normalized position) and the smoothed path's own space
/// (frame index + capture-frame pixels).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CursorPathWarpTests"/> proves the displacement field itself. What can only be
/// checked here is that <see cref="FrameCompositor.SyncCursorAnchors"/> converts correctly —
/// a wrong normalization base or a missing crop-offset term produces a perfectly smooth warp
/// to entirely the wrong place, which no pure test of the field would catch.
/// </para>
/// <para>
/// Also covers the re-warp-from-base rule: syncing twice must not compound.
/// </para>
/// </remarks>
[TestClass]
public sealed class CursorAnchorCompositorTests
{
    // Deliberately modest, per the crash-hardening playbook: large render targets on the SHARED
    // Win2D device have previously left later, unrelated tests composing blank frames.
    private const int SourceW = 640;
    private const int SourceH = 360;
    private const double DurationSeconds = 6.0;

    private static bool HasDevice()
    {
        try { _ = CanvasDevice.GetSharedDevice(); return true; }
        catch { return false; }
    }

    private static async Task<FrameCompositor?> BuildAsync(bool withClicks = false)
    {
        if (!HasDevice()) return null;

        var config = new CompositionConfig
        {
            OutputFps = 30,
            Background = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = "#000000" },
        };

        // A steady diagonal sweep, so the recorded position at any instant is unambiguous.
        var mouse = TestMouseRecordingBuilder.WithPositions(
            sampleCount: 600,
            sampleRateHz: 100,
            positionFunc: i => (100 + (i * 0.5), 60 + (i * 0.25)));

        if (withClicks)
        {
            // Clicks bound the influence window (see CursorPathWarp): everything outside the
            // nearest click either side of an anchor is left exactly as recorded.
            mouse.Clicks.Add(new ClickEvent(
                (long)(1.0 * mouse.TickFrequency), 0, 0, MouseButton.Left, IsDown: true));
            mouse.Clicks.Add(new ClickEvent(
                (long)(5.0 * mouse.TickFrequency), 0, 0, MouseButton.Left, IsDown: true));
        }

        var compositor = new FrameCompositor(config);
        await compositor.InitializeAsync(
            mouse, SourceW, SourceH, duration: TimeSpan.FromSeconds(DurationSeconds));
        return compositor;
    }

    [TestMethod]
    public async Task SyncCursorAnchors_PutsTheCursorAtTheNormalizedTargetAtThatMoment()
    {
        var compositor = await BuildAsync();
        if (compositor is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using (compositor)
        {
            var anchor = new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 };
            compositor.SyncCursorAnchors([anchor]);

            Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double x, out double y));

            // Normalized 0..1 maps onto the SOURCE frame, which is the space the smoothed path
            // already lives in (the crop offset has been subtracted from it).
            Assert.AreEqual(0.25 * SourceW, x, 0.5, "anchor X did not land on the target");
            Assert.AreEqual(0.75 * SourceH, y, 0.5, "anchor Y did not land on the target");
        }
    }

    [TestMethod]
    public async Task SyncCursorAnchors_LeavesEverythingOutsideTheNeighbouringClicksWhereItWas()
    {
        // The feature's central promise. With clicks at 1s and 5s, an anchor at 3s may only
        // affect (1s, 5s) — everything either side plays exactly as it was captured.
        var compositor = await BuildAsync(withClicks: true);
        if (compositor is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using (compositor)
        {
            double[] outside = [0.0, 0.5, 1.0, 5.0, 5.5, DurationSeconds - 0.1];
            var before = new List<(double X, double Y)>();
            foreach (double t in outside)
            {
                Assert.IsTrue(compositor.TryGetCursorPosition(t, out double bx, out double by));
                before.Add((bx, by));
            }

            compositor.SyncCursorAnchors(
                [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 }]);

            for (int i = 0; i < outside.Length; i++)
            {
                Assert.IsTrue(compositor.TryGetCursorPosition(outside[i], out double x, out double y));
                Assert.AreEqual(before[i].X, x, 1e-6, $"X moved at t={outside[i]}s");
                Assert.AreEqual(before[i].Y, y, 1e-6, $"Y moved at t={outside[i]}s");
            }

            // ...and the anchored moment really did move, so the assertions above are not
            // passing simply because nothing happened.
            Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double ax, out double ay));
            Assert.AreEqual(0.25 * SourceW, ax, 0.5);
            Assert.AreEqual(0.75 * SourceH, ay, 0.5);
        }
    }

    [TestMethod]
    public async Task SyncCursorAnchors_WithNoClicks_StillPinsBothEndsOfTheRecording()
    {
        // With nothing to bound it, the window spans the whole recording — but the endpoints
        // are zero-nodes, so the recording still starts and finishes where it was captured.
        var compositor = await BuildAsync();
        if (compositor is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using (compositor)
        {
            Assert.IsTrue(compositor.TryGetCursorPosition(0.0, out double startX, out double startY));
            Assert.IsTrue(compositor.TryGetCursorPosition(DurationSeconds, out double endX, out double endY));

            compositor.SyncCursorAnchors(
                [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 }]);

            Assert.IsTrue(compositor.TryGetCursorPosition(0.0, out double startX2, out double startY2));
            Assert.IsTrue(compositor.TryGetCursorPosition(DurationSeconds, out double endX2, out double endY2));

            Assert.AreEqual(startX, startX2, 1e-6, "the start of the recording moved");
            Assert.AreEqual(startY, startY2, 1e-6, "the start of the recording moved");
            Assert.AreEqual(endX, endX2, 1e-6, "the end of the recording moved");
            Assert.AreEqual(endY, endY2, 1e-6, "the end of the recording moved");
        }
    }

    [TestMethod]
    public async Task SyncCursorAnchors_CalledRepeatedly_DoesNotCompoundTheWarp()
    {
        // The editor calls this on every pointer sample of a drag. Warping the ALREADY-warped
        // path instead of the recorded one would send the cursor further away with each sample.
        var compositor = await BuildAsync();
        if (compositor is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using (compositor)
        {
            var anchor = new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 };

            for (int i = 0; i < 5; i++)
                compositor.SyncCursorAnchors([anchor]);

            Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double x, out double y));
            Assert.AreEqual(0.25 * SourceW, x, 0.5);
            Assert.AreEqual(0.75 * SourceH, y, 0.5);
        }
    }

    [TestMethod]
    public async Task SyncCursorAnchors_BeforeInitialize_IsHeldAndAppliedOnInitialize()
    {
        // The export path syncs per-source state after InitializeAsync, but nothing forbids the
        // reverse order, and a compositor that quietly dropped anchors handed to it early would
        // export a cursor that had never been repositioned.
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        var config = new CompositionConfig
        {
            OutputFps = 30,
            Background = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = "#000000" },
        };

        using var compositor = new FrameCompositor(config);
        compositor.SyncCursorAnchors(
            [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 }]);

        var mouse = TestMouseRecordingBuilder.WithPositions(
            sampleCount: 600,
            sampleRateHz: 100,
            positionFunc: i => (100 + (i * 0.5), 60 + (i * 0.25)));

        await compositor.InitializeAsync(
            mouse, SourceW, SourceH, duration: TimeSpan.FromSeconds(DurationSeconds));

        Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double x, out double y));
        Assert.AreEqual(0.25 * SourceW, x, 0.5);
        Assert.AreEqual(0.75 * SourceH, y, 0.5);
    }

    [TestMethod]
    public async Task SyncCursorAnchors_WithNoMouseDataAndNoDuration_DoesNotThrow()
    {
        // Anchors held from before initialization meet an EMPTY path here, which used to reach
        // Math.Clamp(index, 0, -1) and throw out of InitializeAsync — taking the whole preview
        // rebuild with it.
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        var config = new CompositionConfig { OutputFps = 30 };

        using var compositor = new FrameCompositor(config);
        compositor.SyncCursorAnchors(
            [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(1), X = 0.5, Y = 0.5 }]);

        await compositor.InitializeAsync(
            new MouseRecordingData { TickFrequency = TimeSpan.TicksPerSecond },
            SourceW, SourceH);

        Assert.IsFalse(compositor.TryGetCursorPosition(1.0, out _, out _),
            "an empty recording has no cursor position to report");
    }

    [TestMethod]
    public async Task SyncCursorAnchors_AnchoredOnAClick_DoesNotSnapBackAtTheButtonUp()
    {
        // End-to-end cover for BuildClickSpans' down/up pairing, which the pure warp tests
        // cannot reach. The recorder stores a press as TWO ClickEvents ~100ms apart; treating
        // them as independent instants let an anchor claim the down while the up dragged the
        // path back three frames later — the reported "abrupt and flashy" transition.
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        var config = new CompositionConfig
        {
            OutputFps = 30,
            Background = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = "#000000" },
        };

        var mouse = TestMouseRecordingBuilder.WithPositions(
            sampleCount: 600,
            sampleRateHz: 100,
            positionFunc: i => (100 + (i * 0.5), 60 + (i * 0.25)));

        // A realistic press: down at 3.00s, up at 3.10s.
        mouse.Clicks.Add(new ClickEvent(
            (long)(3.00 * mouse.TickFrequency), 250, 135, MouseButton.Left, IsDown: true));
        mouse.Clicks.Add(new ClickEvent(
            (long)(3.10 * mouse.TickFrequency), 250, 135, MouseButton.Left, IsDown: false));

        using var compositor = new FrameCompositor(config);
        await compositor.InitializeAsync(
            mouse, SourceW, SourceH, duration: TimeSpan.FromSeconds(DurationSeconds));

        compositor.SyncCursorAnchors(
            [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 }]);

        Assert.IsTrue(compositor.TryGetCursorPosition(3.00, out double downX, out double downY));
        Assert.AreEqual(0.25 * SourceW, downX, 0.5, "the anchor must still land exactly");
        Assert.AreEqual(0.75 * SourceH, downY, 0.5);

        // Sample across the press and just past it. Every step must stay small; the old
        // behaviour covered the whole displacement in the ~3 frames between down and up.
        double previousX = downX, previousY = downY;
        for (double t = 3.00 + (1.0 / 30); t <= 3.30; t += 1.0 / 30)
        {
            Assert.IsTrue(compositor.TryGetCursorPosition(t, out double x, out double y));
            double step = Math.Sqrt(Math.Pow(x - previousX, 2) + Math.Pow(y - previousY, 2));
            Assert.IsTrue(step < 25, $"cursor jumped {step:F1}px in one frame at t={t:F2}s");
            previousX = x;
            previousY = y;
        }
    }

    [TestMethod]
    public async Task SyncCursorAnchors_WithAnEmptyList_RestoresTheRecordedPath()
    {
        var compositor = await BuildAsync();
        if (compositor is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using (compositor)
        {
            Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double recordedX, out double recordedY));

            compositor.SyncCursorAnchors(
                [new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3), X = 0.25, Y = 0.75 }]);
            compositor.SyncCursorAnchors([]);

            Assert.IsTrue(compositor.TryGetCursorPosition(3.0, out double x, out double y));
            Assert.AreEqual(recordedX, x, 1e-6, "removing every anchor must restore the recording");
            Assert.AreEqual(recordedY, y, 1e-6, "removing every anchor must restore the recording");
        }
    }
}
