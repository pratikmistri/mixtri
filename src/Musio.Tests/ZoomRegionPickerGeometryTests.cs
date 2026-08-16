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
    public async Task ComputeRegionCenterBounds_FrameScope_SpendsPaddingAsSlack()
    {
        using var compositor = await BuildAsync(Config(padding: 40));
        if (compositor is null) { Assert.Inconclusive("No Win2D device."); return; }

        var bounds = compositor.ComputeRegionCenterBounds(2f);

        // Half a viewport in from each edge would be 0.25; the background around the source
        // area lets the camera go further before it clamps, and the picker has to allow the
        // same range or its rectangle stops short of what renders.
        Assert.IsTrue(bounds.MinX > 0 && bounds.MinX < 0.25,
            $"expected the centre to reach past 0.25, got {bounds.MinX}");
        Assert.IsTrue(bounds.MinY > 0 && bounds.MinY < 0.25,
            $"expected the centre to reach past 0.25, got {bounds.MinY}");
        Assert.AreEqual(1.0 - bounds.MinX, bounds.MaxX, 1e-6, "bounds are symmetric for a centred crop anchor");
        Assert.AreEqual(1.0 - bounds.MinY, bounds.MaxY, 1e-6, "bounds are symmetric for a centred crop anchor");
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
