using System.Numerics;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.Foundation;
using Windows.UI;

namespace Mixtri.Tests;

/// <summary>
/// Regressions for the 2026-11 rendering review: one interpolation rule, the bounded
/// frosted-glass sample region, and first-delivery ownership in the frame cache.
/// </summary>
[TestClass]
public class RenderReviewFixTests
{
    [TestMethod]
    public void ResampleInterpolation_IsLinearOnlyAtUnitScale()
    {
        Assert.AreEqual(CanvasImageInterpolation.Linear,
            FrameCompositor.ResolveResampleInterpolation(1.0, 1.0));
        Assert.AreEqual(CanvasImageInterpolation.Linear,
            FrameCompositor.ResolveResampleInterpolation(1.001, 0.999));

        // The old 0.95-1.05 "near unit" band flipped filters partway through a slow zoom.
        Assert.AreEqual(CanvasImageInterpolation.HighQualityCubic,
            FrameCompositor.ResolveResampleInterpolation(1.04, 1.04));
        Assert.AreEqual(CanvasImageInterpolation.HighQualityCubic,
            FrameCompositor.ResolveResampleInterpolation(0.96, 1.0));
        Assert.AreEqual(CanvasImageInterpolation.HighQualityCubic,
            FrameCompositor.ResolveResampleInterpolation(double.PositiveInfinity, 1.0));
    }

    [TestMethod]
    public void BlurSampleRegion_CoversBoxPlusBlurReach_ClampedToFrame()
    {
        var box = new Rect(100, 200, 300, 100);
        var center = new Vector2(250, 250);

        var region = TextOverlayRenderer.ComputeBlurSampleRegion(
            box, 1f, center, 0f, 0f, blurAmount: 10, width: 1920, height: 1080);

        // 3σ + 2 px of reach on every side, whole pixels.
        Assert.AreEqual(new Rect(68, 168, 364, 164), region);

        var edge = TextOverlayRenderer.ComputeBlurSampleRegion(
            new Rect(0, 0, 50, 50), 1f, new Vector2(25, 25), 0f, 0f, 10, 1920, 1080);
        Assert.AreEqual(0, edge.X);
        Assert.AreEqual(0, edge.Y);

        // Entirely off-frame boxes still yield a valid (non-empty) region.
        var offscreen = TextOverlayRenderer.ComputeBlurSampleRegion(
            new Rect(-500, -500, 10, 10), 1f, new Vector2(-495, -495), 0f, 0f, 1, 1920, 1080);
        Assert.IsTrue(offscreen.Width >= 1 && offscreen.Height >= 1);
    }

    [TestMethod]
    public void BlurSampleRegion_FollowsTheAnimationEnvelope()
    {
        var box = new Rect(100, 100, 200, 100);
        var center = new Vector2(200, 150);

        var region = TextOverlayRenderer.ComputeBlurSampleRegion(
            box, 0.5f, center, 40f, -20f, blurAmount: 0, width: 1920, height: 1080);

        // Half-size box around (240, 130), plus the 2 px floor margin.
        Assert.AreEqual(new Rect(188, 103, 104, 54), region);
    }

    [TestMethod]
    public async Task FrameCache_FirstDeliveryIsTheDecodedFrame_RepeatIsCachedCopy()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var source = new CountingSource(device);
        using var reader = new VideoFrameReader(source, 10);

        using var first = await reader.LoadFrameAsync(0);
        Assert.AreSame(source.LastDecoded, first, "first delivery must not be copied");

        using var second = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(second);
        Assert.AreNotSame(source.LastDecoded, second, "a cached frame is handed out as a copy");
        Assert.AreEqual(2, source.Loads);

        using var third = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(third);
        Assert.AreEqual(2, source.Loads, "the repeat request must have populated the cache");
        Assert.AreEqual((byte)180, third.GetPixelColors()[0].R);
    }

    private sealed class CountingSource(CanvasDevice device) : IFrameSource
    {
        public int Width => 16;
        public int Height => 16;
        public int FrameCount => 4;
        public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;
        public int Loads { get; private set; }
        public CanvasBitmap? LastDecoded { get; private set; }

        public Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
        {
            Loads++;
            LastDecoded = CanvasBitmapTestHelpers.MakeBitmap(
                device, Color.FromArgb(255, 180, 60, 20), Width, Height);
            return Task.FromResult<CanvasBitmap?>(LastDecoded);
        }

        public void Dispose() { }
    }
}
