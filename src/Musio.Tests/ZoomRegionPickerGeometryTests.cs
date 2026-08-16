using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Covers the geometry the zoom-region picker maps its overlay through
/// (<see cref="FrameCompositor.SourceAreaRect"/>, <see cref="FrameCompositor.RestSourceViewport"/>,
/// <see cref="FrameCompositor.ComputeRegionViewport"/>) and the rest-frame composition it relies
/// on (<see cref="FrameCompositor.SuppressZoom"/>).
/// <para>
/// The picker used to frame the RAW capture while the preview showed the composed frame, so the
/// rectangle a user drew and the render they got back disagreed about padding, aspect-ratio fit
/// and the cursor. These assert the single source of that geometry stays the compositor's own.
/// </para>
/// </summary>
/// <remarks>
/// Win2D needs a graphics device; without one every test here reports Inconclusive rather than
/// failing, matching <see cref="PreviewSustainedRenderProbeTests"/>'s environment gate.
/// </remarks>
[TestClass]
public class ZoomRegionPickerGeometryTests
{
    private const int SourceW = 640;
    private const int SourceH = 360;

    private static bool HasDevice()
    {
        try { _ = CanvasDevice.GetSharedDevice(); return true; }
        catch { return false; }
    }

    private static async Task<FrameCompositor?> BuildAsync(CompositionConfig config)
    {
        if (!HasDevice()) return null;

        var compositor = new FrameCompositor(config);
        await compositor.InitializeAsync(
            new MouseRecordingData { TickFrequency = TimeSpan.TicksPerSecond },
            SourceW, SourceH, duration: TimeSpan.FromSeconds(4));
        return compositor;
    }

    private static CompositionConfig Config(
        int padding = 0,
        AspectRatio aspectRatio = AspectRatio.Auto,
        FitMode fitMode = FitMode.Contain) => new()
        {
            OutputFps = 30,
            AspectRatio = aspectRatio,
            FitMode = fitMode,
            Background = new BackgroundStyle
            {
                Type = BackgroundType.SolidColor,
                Color = "#1a1a2e",
                Padding = padding,
            },
        };

    [TestMethod]
    public async Task SourceAreaRect_WithoutPadding_IsTheWholeCanvas()
    {
        using var compositor = await BuildAsync(Config());
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var area = compositor.SourceAreaRect;

        Assert.AreEqual(0.0, area.X, "no padding means no left gap");
        Assert.AreEqual(0.0, area.Y, "no padding means no top gap");
        Assert.AreEqual((double)SourceW, area.Width);
        Assert.AreEqual((double)SourceH, area.Height);
    }

    [TestMethod]
    public async Task SourceAreaRect_ReservesPadding_AndKeepsSourceAspect()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var area = compositor.SourceAreaRect;

        // 40px reserved on each side leaves a 560x280 box; the source keeps its 16:9 ratio
        // inside it (280 tall => 498 wide) and is centred, so the horizontal gap grows past
        // the requested padding. This is exactly the offset the picker must honour.
        Assert.AreEqual(280.0, area.Height, "height is padding-bound");
        Assert.AreEqual(498.0, area.Width, "width follows the source aspect ratio, not the padding");
        Assert.AreEqual(71.0, area.X, "source area is centred in the canvas");
        Assert.AreEqual(40.0, area.Y);
    }

    [TestMethod]
    public async Task RestSourceViewport_ForAutoAspect_ShowsTheWholeSource()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var visible = compositor.RestSourceViewport;

        Assert.AreEqual(0.0, visible.X);
        Assert.AreEqual(0.0, visible.Y);
        Assert.AreEqual((double)SourceW, visible.Width, "padding insets within the canvas, it never crops the source");
        Assert.AreEqual((double)SourceH, visible.Height);
    }

    [TestMethod]
    public async Task RestSourceViewport_InCoverMode_CropsToTheTargetRatio()
    {
        using var compositor = await BuildAsync(
            Config(aspectRatio: AspectRatio.Square1x1, fitMode: FitMode.Cover));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var visible = compositor.RestSourceViewport;

        // A 1:1 canvas over a 16:9 source drops the sides, anchored at the centre (0.5).
        Assert.AreEqual(360.0, visible.Width);
        Assert.AreEqual(360.0, visible.Height);
        Assert.AreEqual(140.0, visible.X, "centre anchor splits the cropped width evenly");
        Assert.AreEqual(0.0, visible.Y);
    }

    [TestMethod]
    public async Task ComputeRegionViewport_CentredZoom_HalvesTheViewport()
    {
        using var compositor = await BuildAsync(Config());
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var vp = compositor.ComputeRegionViewport(2f, 0.5f, 0.5f);

        Assert.AreEqual(320.0, vp.Width);
        Assert.AreEqual(180.0, vp.Height);
        Assert.AreEqual(160.0, vp.X);
        Assert.AreEqual(90.0, vp.Y);
    }

    [TestMethod]
    public async Task ComputeRegionViewport_OffEdgeCentre_ClampsIntoTheSource()
    {
        using var compositor = await BuildAsync(Config());
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var vp = compositor.ComputeRegionViewport(2f, 0f, 0f);

        Assert.AreEqual(0.0, vp.X, "a centre on the edge cannot push the viewport off the source");
        Assert.AreEqual(0.0, vp.Y);
        Assert.AreEqual(320.0, vp.Width);
        Assert.AreEqual(180.0, vp.Height);
    }

    [TestMethod]
    public async Task ComputeRegionViewport_InCoverMode_AppliesTheAspectRatioCrop()
    {
        using var compositor = await BuildAsync(
            Config(aspectRatio: AspectRatio.Square1x1, fitMode: FitMode.Cover));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var vp = compositor.ComputeRegionViewport(2f, 0.5f, 0.5f);

        // 2x gives a 320x180 viewport, which the 1:1 canvas then narrows to 180x180 — the
        // rectangle the picker draws has to be the narrowed one, since that is what renders.
        Assert.AreEqual(180.0, vp.Width);
        Assert.AreEqual(180.0, vp.Height);
    }

    [TestMethod]
    public async Task ComputeRegionOutputRect_FrameScope_CropsTheWholeCanvasIncludingPadding()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var outRect = compositor.ComputeRegionOutputRect(2f, 0.5f, 0.5f);

        // Frame scope magnifies the composed canvas, so 2x shows half of it — padding and
        // all. Measuring this in source pixels (320 wide) would under-draw the region.
        Assert.AreEqual(320.0, outRect.Width, 0.5);
        Assert.AreEqual(180.0, outRect.Height, 0.5);
        Assert.AreEqual(160.0, outRect.X, 0.5);
        Assert.AreEqual(90.0, outRect.Y, 0.5);
    }

    [TestMethod]
    public async Task ComputeRegionOutputRect_SourceScope_StaysWithinTheSourceArea()
    {
        var config = Config(padding: 40) with { ZoomScope = ZoomScope.Source };
        using var compositor = await BuildAsync(config);
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var area = compositor.SourceAreaRect;
        var outRect = compositor.ComputeRegionOutputRect(2f, 0.5f, 0.5f);

        // Source scope leaves the background and padding untouched, so the region can only
        // ever be part of the source area.
        Assert.IsTrue(outRect.X >= area.X - 0.5 && outRect.Y >= area.Y - 0.5,
            $"region {outRect} started outside the source area {area}");
        Assert.IsTrue(outRect.X + outRect.Width <= area.X + area.Width + 0.5
            && outRect.Y + outRect.Height <= area.Y + area.Height + 0.5,
            $"region {outRect} spilled outside the source area {area}");
        Assert.AreEqual(area.Width / 2, outRect.Width, 0.5, "2x shows half the source area's width");
    }

    [TestMethod]
    public async Task ComputeRegionCenterBounds_SourceScope_IsHalfAViewportFromEachEdge()
    {
        var config = Config() with { ZoomScope = ZoomScope.Source };
        using var compositor = await BuildAsync(config);
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var bounds = compositor.ComputeRegionCenterBounds(2f);

        Assert.AreEqual(0.25, bounds.MinX, 1e-6);
        Assert.AreEqual(0.75, bounds.MaxX, 1e-6);
        Assert.AreEqual(0.25, bounds.MinY, 1e-6);
        Assert.AreEqual(0.75, bounds.MaxY, 1e-6);
    }

    [TestMethod]
    public async Task ComputeRegionOutputRect_FrameScope_AtRest_IsTheWholeCanvas()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var outRect = compositor.ComputeRegionOutputRect(1f, 0.5f, 0.5f);

        // 1x renders the canvas untouched, background included; framing only the source area
        // would claim the padding gets cropped.
        Assert.AreEqual(0.0, outRect.X);
        Assert.AreEqual(0.0, outRect.Y);
        Assert.AreEqual(640.0, outRect.Width);
        Assert.AreEqual(360.0, outRect.Height);
    }

    [TestMethod]
    public async Task ComputeRegionCenterBounds_FrameScope_MinCentre_RendersAtTheClampedEdge()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var bounds = compositor.ComputeRegionCenterBounds(2f);

        // The invariant that matters: a centre AT the bound must still move the rendered crop.
        // Push past it by a hair and the crop must not have moved — that is the clamp.
        var atMin = compositor.ComputeRegionOutputRect(2f, (float)bounds.MinX, 0.5f);
        var pastMin = compositor.ComputeRegionOutputRect(2f, (float)(bounds.MinX - 0.05), 0.5f);
        var insideMin = compositor.ComputeRegionOutputRect(2f, (float)(bounds.MinX + 0.05), 0.5f);

        Assert.AreEqual(atMin.X, pastMin.X, 0.5,
            "a centre below the bound must render identically to the bound, or the bound is too loose");
        Assert.IsTrue(insideMin.X > atMin.X + 0.5,
            "a centre above the bound must actually move the crop, or the bound is too tight");
    }

    [TestMethod]
    public async Task ComputeRegionCenterBounds_FrameScope_HonoursTheCameraEngineClamp()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var bounds = compositor.ComputeRegionCenterBounds(2f);

        // AutoZoomEngine clamps the un-narrowed viewport into the source frame before output
        // space is ever considered, so padding is NOT slack the centre can spend.
        Assert.AreEqual(0.25, bounds.MinX, 1e-6);
        Assert.AreEqual(0.75, bounds.MaxX, 1e-6);
        Assert.AreEqual(0.25, bounds.MinY, 1e-6);
        Assert.AreEqual(0.75, bounds.MaxY, 1e-6);
    }

    [TestMethod]
    public async Task ComputeRegionCenterBounds_CoverCrop_IsTighterThanTheSourceClamp()
    {
        using var compositor = await BuildAsync(
            Config(aspectRatio: AspectRatio.Square1x1, fitMode: FitMode.Cover));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var bounds = compositor.ComputeRegionCenterBounds(2f);

        // Only the middle 360px of the 640px source survives the 1:1 cover crop, so the
        // camera saturates well before the source-space 0.25.
        Assert.IsTrue(bounds.MinX > 0.25, $"expected a tighter bound than 0.25, got {bounds.MinX}");
        var atMin = compositor.ComputeRegionOutputRect(2f, (float)bounds.MinX, 0.5f);
        var pastMin = compositor.ComputeRegionOutputRect(2f, (float)(bounds.MinX - 0.05), 0.5f);
        Assert.AreEqual(atMin.X, pastMin.X, 0.5, "past the bound the rendered crop must not move");
    }

    [TestMethod]
    public async Task ComputeRegionOutputRect_SourceScopeWithCoverCrop_StaysOnScreen()
    {
        var config = Config(aspectRatio: AspectRatio.Square1x1, fitMode: FitMode.Cover)
            with { ZoomScope = ZoomScope.Source };
        using var compositor = await BuildAsync(config);
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var area = compositor.SourceAreaRect;

        // A centre far outside the cover crop used to extrapolate to a negative X, parking the
        // rectangle and its handles off the preview.
        var outRect = compositor.ComputeRegionOutputRect(2f, 0.1f, 0.5f);

        Assert.IsTrue(outRect.X >= area.X - 0.5, $"region {outRect} started left of the source area {area}");
        Assert.IsTrue(outRect.X + outRect.Width <= area.X + area.Width + 0.5,
            $"region {outRect} spilled past the source area {area}");
        Assert.IsTrue(outRect.Width > 0 && outRect.Height > 0, "the region must stay grabbable");
    }

    [TestMethod]
    public async Task ComputeRegionOutputRect_CoverCrop_TracksTheCropAnchor()
    {
        var centred = Config(aspectRatio: AspectRatio.Square1x1, fitMode: FitMode.Cover);
        var leftAnchored = centred with { CropAnchorX = 0.0 };

        using var centredCompositor = await BuildAsync(centred);
        using var anchoredCompositor = await BuildAsync(leftAnchored);
        if (centredCompositor is null || anchoredCompositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        // The anchor moves which source pixels the canvas shows, so the same normalised centre
        // must land the rendered crop somewhere else.
        var centredRect = centredCompositor.ComputeRegionOutputRect(2f, 0.5f, 0.5f);
        var anchoredRect = anchoredCompositor.ComputeRegionOutputRect(2f, 0.5f, 0.5f);

        Assert.AreEqual(140.0, centredCompositor.RestSourceViewport.X, 0.5);
        Assert.AreEqual(0.0, anchoredCompositor.RestSourceViewport.X, 0.5);
        Assert.AreNotEqual(centredRect.X, anchoredRect.X,
            "a left-anchored cover crop must not frame the same region as a centred one");
    }

    [TestMethod]
    public async Task RegionCanvasRect_FrameScope_IsTheWholeCanvas()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var canvas = compositor.RegionCanvasRect;

        // A frame zoom magnifies the background and padding along with the source, so the
        // picker dims all of it outside the region.
        Assert.AreEqual(0.0, canvas.X);
        Assert.AreEqual(0.0, canvas.Y);
        Assert.AreEqual(640.0, canvas.Width);
        Assert.AreEqual(360.0, canvas.Height);
    }

    [TestMethod]
    public async Task RegionCanvasRect_SourceScope_IsJustTheSourceArea()
    {
        var config = Config(padding: 40) with { ZoomScope = ZoomScope.Source };
        using var compositor = await BuildAsync(config);
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        // A source zoom leaves the background and padding at a fixed size, so dimming them
        // would claim they get cropped when they survive untouched.
        Assert.AreEqual(compositor.SourceAreaRect, compositor.RegionCanvasRect);
        Assert.IsTrue(compositor.RegionCanvasRect.Width < 640, "padding must be excluded");
    }

    [TestMethod]
    public async Task SuppressZoom_ComposesTheSameFrameAsAnUnzoomedClip()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device."); return; }

        var keyframe = ZoomKeyframe.FromRange(
            TimeSpan.Zero, TimeSpan.FromSeconds(4), zoomLevel: 3.0, centerX: 0.25, centerY: 0.25);
        double sampleSeconds = keyframe.Timestamp.TotalSeconds + 0.4;

        using var source = BuildSourceFrame();

        using var rest = await BuildAsync(Config());
        using var zoomed = await BuildAsync(Config());
        using var suppressed = await BuildAsync(Config());
        if (rest is null || zoomed is null || suppressed is null) { Assert.Inconclusive("No Win2D device."); return; }

        zoomed.SyncManualZoomKeyframes([keyframe]);
        suppressed.SyncManualZoomKeyframes([keyframe]);
        suppressed.SuppressZoom = true;

        using var restFrame = rest.ComposeFrame(source, sampleSeconds);
        using var zoomedFrame = zoomed.ComposeFrame(source, sampleSeconds);
        using var suppressedFrame = suppressed.ComposeFrame(source, sampleSeconds);

        Assert.AreNotEqual(0, DifferingPixels(restFrame, zoomedFrame),
            "the keyframe must actually change the frame, or this test proves nothing");
        Assert.AreEqual(0, DifferingPixels(restFrame, suppressedFrame),
            "SuppressZoom must hold the camera at rest while composing everything else normally");
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
}
