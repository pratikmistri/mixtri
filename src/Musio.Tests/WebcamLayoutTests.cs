using Musio.Core.Processing;

namespace Musio.Tests;

/// <summary>
/// Tests for the pure webcam-overlay interpolation math used by the fullscreen
/// camera animation. Factor 0 must reproduce the legacy square overlay; factor 1
/// must cover the whole canvas with a center "cover" crop.
/// </summary>
[TestClass]
public sealed class WebcamLayoutTests
{
    private const int CanvasW = 1920;
    private const int CanvasH = 1080;

    // A typical 720p webcam frame.
    private const float FrameW = 1280f;
    private const float FrameH = 720f;

    private static WebcamOverlayStyle Style() => new()
    {
        Shape = WebcamShape.Circle,
        Position = WebcamPosition.BottomRight,
        Size = 300f,
        Margin = 20f,
        BorderWidth = 8f,
        ShadowEnabled = true,
    };

    [TestMethod]
    public void Factor0_ReproducesSquareOverlay()
    {
        var layout = WebcamLayoutCalculator.ComputeAnimatedLayout(
            Style(), CanvasW, CanvasH, FrameW, FrameH, 0f);

        // BottomRight: x = W - size - margin, y = H - size - margin.
        Assert.AreEqual(1920 - 300 - 20, layout.Destination.X, 0.01);
        Assert.AreEqual(1080 - 300 - 20, layout.Destination.Y, 0.01);
        Assert.AreEqual(300, layout.Destination.Width, 0.01);
        Assert.AreEqual(300, layout.Destination.Height, 0.01);

        // Circle => rounded rect with radius = half the side.
        Assert.AreEqual(150, layout.CornerRadius, 0.01);

        // Square center crop of a 1280x720 frame.
        Assert.AreEqual(720, layout.SourceCrop.Width, 0.01);
        Assert.AreEqual(720, layout.SourceCrop.Height, 0.01);
        Assert.AreEqual((1280 - 720) / 2.0, layout.SourceCrop.X, 0.01);
        Assert.AreEqual(0, layout.SourceCrop.Y, 0.01);

        Assert.AreEqual(8f, layout.BorderWidth, 0.01);
        Assert.AreEqual(1f, layout.ShadowAlpha, 0.01);
        Assert.IsTrue(layout.IsCircle);
    }

    [TestMethod]
    public void Factor1_CoversWholeCanvas()
    {
        var layout = WebcamLayoutCalculator.ComputeAnimatedLayout(
            Style(), CanvasW, CanvasH, FrameW, FrameH, 1f);

        Assert.AreEqual(0, layout.Destination.X, 0.01);
        Assert.AreEqual(0, layout.Destination.Y, 0.01);
        Assert.AreEqual(CanvasW, layout.Destination.Width, 0.01);
        Assert.AreEqual(CanvasH, layout.Destination.Height, 0.01);

        // No rounding, border, or shadow at fullscreen.
        Assert.AreEqual(0, layout.CornerRadius, 0.01);
        Assert.AreEqual(0f, layout.BorderWidth, 0.01);
        Assert.AreEqual(0f, layout.ShadowAlpha, 0.01);

        // 16:9 source covering a 16:9 canvas => the whole frame is used.
        Assert.AreEqual(0, layout.SourceCrop.X, 0.01);
        Assert.AreEqual(0, layout.SourceCrop.Y, 0.01);
        Assert.AreEqual(FrameW, layout.SourceCrop.Width, 0.01);
        Assert.AreEqual(FrameH, layout.SourceCrop.Height, 0.01);
        Assert.IsFalse(layout.IsCircle);
    }

    [TestMethod]
    public void MidFactor_IsBetweenOverlayAndFullscreen()
    {
        var layout = WebcamLayoutCalculator.ComputeAnimatedLayout(
            Style(), CanvasW, CanvasH, FrameW, FrameH, 0.5f);

        // Destination grows toward the full canvas.
        Assert.IsTrue(layout.Destination.Width > 300 && layout.Destination.Width < CanvasW);
        Assert.IsTrue(layout.Destination.Height > 300 && layout.Destination.Height < CanvasH);

        // Border and shadow fade.
        Assert.AreEqual(4f, layout.BorderWidth, 0.01);
        Assert.AreEqual(0.5f, layout.ShadowAlpha, 0.01);
    }

    [TestMethod]
    public void ComputeCoverCrop_PortraitTarget_CropsWidth()
    {
        // Target taller than source => crop the sides.
        var crop = WebcamLayoutCalculator.ComputeCoverCrop(FrameW, FrameH, 100f, 200f);
        Assert.AreEqual(FrameH, crop.Height, 0.01);
        Assert.AreEqual(FrameH * (100f / 200f), crop.Width, 0.01);
    }
}
