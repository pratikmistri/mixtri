using Microsoft.Graphics.Canvas;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Windows.UI;

namespace Mixtri.Tests;

/// <summary>
/// Covers <see cref="FrameCompositor"/>'s end of the per-zoom-segment camera drift move:
/// <see cref="ZoomKeyframe.Drift"/> has to survive all the way through
/// <c>FrameCompositor.ResolveZoomState</c>, including the cursor-recentring rebuild that an
/// auto (cursor-following) zoom goes through on every frame.
/// <para>
/// That rebuild is the historical bug class here: <c>ComputeViewportForCenter</c> returns a
/// FRESH <see cref="ZoomState"/> (with <c>DriftScale = 1f</c> and <c>DriftSettings = null</c>
/// baked in as defaults), so the caller has to copy every segment-identity field across by
/// hand. Forgetting <c>DriftSettings</c> specifically would silently disable per-segment drift
/// for every cursor-following zoom — the common case — while leaving pinned-region zooms
/// unaffected, which is exactly the kind of gap that hides until someone notices camera drift
/// "doesn't work anymore" on ordinary auto zooms.
/// </para>
/// </summary>
/// <remarks>
/// Win2D needs a graphics device; without one every test here reports Inconclusive rather than
/// failing, matching <see cref="ZoomRegionPickerGeometryTests"/>'s environment gate.
/// </remarks>
[TestClass]
public sealed class PerSegmentCameraDriftTests
{
    // Deliberately the same modest size as ZoomRegionPickerGeometryTests. Two full-HD
    // compositors plus their render targets on the SHARED Win2D device were enough to leave
    // later tests in this assembly composing blank frames — the silent device-loss failure
    // mode the crash-hardening playbook describes, which surfaces as an unrelated test
    // suddenly seeing zero differing pixels rather than as an exception.
    private const int SourceW = 640;
    private const int SourceH = 360;

    private static bool HasDevice()
    {
        try { _ = CanvasDevice.GetSharedDevice(); return true; }
        catch { return false; }
    }

    private static async Task<FrameCompositor?> BuildAsync()
    {
        if (!HasDevice()) return null;

        var config = new CompositionConfig
        {
            OutputFps = 30,
            Background = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = "#000000" },
        };
        var compositor = new FrameCompositor(config);
        await compositor.InitializeAsync(
            new MouseRecordingData { TickFrequency = TimeSpan.TicksPerSecond },
            SourceW, SourceH, duration: TimeSpan.FromSeconds(6));
        return compositor;
    }

    /// <summary>A frame with strong spatial structure, so any camera move changes pixels.</summary>
    private static CanvasRenderTarget BuildSourceFrame()
    {
        var device = CanvasDevice.GetSharedDevice();
        var target = new CanvasRenderTarget(device, SourceW, SourceH, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 10, 10, 10));
            for (int y = 0; y < SourceH; y += 20)
            {
                for (int x = 0; x < SourceW; x += 20)
                {
                    ds.FillRectangle(x, y, 10, 10,
                        Color.FromArgb(255, (byte)(x % 256), (byte)(y % 256), 200));
                }
            }
        }
        return target;
    }

    private static int DifferingPixels(CanvasRenderTarget a, CanvasRenderTarget b)
    {
        Assert.AreEqual(a.SizeInPixels.Width, b.SizeInPixels.Width, "compared frames must be the same size");
        Assert.AreEqual(a.SizeInPixels.Height, b.SizeInPixels.Height, "compared frames must be the same size");

        var pa = a.GetPixelColors();
        var pb = b.GetPixelColors();
        int differing = 0;
        for (int i = 0; i < pa.Length; i++)
            if (pa[i] != pb[i]) differing++;
        return differing;
    }

    [TestMethod]
    public async Task CursorFollowingZoom_PerSegmentDrift_SurvivesTheRecentringRebuild()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device."); return; }

        // FromRange gives HasAuthoredCenter = false, so ToZoomShot resolves HasFixedCenter =
        // false and the compositor re-centres the viewport on the live cursor every frame —
        // exactly the path that throws away and rebuilds the ZoomState.
        var enabledKeyframe = ZoomKeyframe.FromRange(
            TimeSpan.Zero, TimeSpan.FromSeconds(6), zoomLevel: 2.5, centerX: 0.5, centerY: 0.5)
            with { Drift = new CameraDriftSettings { Enabled = true, Strength = 4f } };
        var disabledKeyframe = enabledKeyframe with { Drift = new CameraDriftSettings { Enabled = false } };

        double sampleSeconds = enabledKeyframe.Timestamp.TotalSeconds
            + (enabledKeyframe.HoldDuration.TotalSeconds * 0.5);

        using var source = BuildSourceFrame();
        using var enabledCompositor = await BuildAsync();
        using var disabledCompositor = await BuildAsync();
        if (enabledCompositor is null || disabledCompositor is null)
        {
            Assert.Inconclusive("No Win2D device.");
            return;
        }

        enabledCompositor.SyncManualZoomKeyframes([enabledKeyframe]);
        disabledCompositor.SyncManualZoomKeyframes([disabledKeyframe]);

        using var enabledFrame = enabledCompositor.ComposeFrame(source, sampleSeconds);
        using var disabledFrame = disabledCompositor.ComposeFrame(source, sampleSeconds);

        Assert.AreNotEqual(0, DifferingPixels(enabledFrame, disabledFrame),
            "an enabled per-segment Drift must render a different frame than a disabled one on a " +
            "cursor-following zoom — if DriftSettings were dropped during the recentring rebuild " +
            "both frames would be pixel-identical.");
    }
}
