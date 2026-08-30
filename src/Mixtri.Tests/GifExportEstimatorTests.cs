using Mixtri.Core.Export;
using Mixtri.Core.Processing;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

[TestClass]
public class GifExportEstimatorTests
{
    [TestMethod]
    public void Estimate_ScalesWithPixelCount()
    {
        var clip = TimeSpan.FromSeconds(10);

        var small = GifExportEstimator.Estimate(480, 270, 30, clip);
        var large = GifExportEstimator.Estimate(1920, 1080, 30, clip);

        // 1920x1080 is 16x the pixels of 480x270, so it must cost ~16x as long.
        Assert.AreEqual(16.0, large.TotalMilliseconds / small.TotalMilliseconds, 0.01);
    }

    [TestMethod]
    public void Estimate_ScalesWithFrameCount()
    {
        var atThirty = GifExportEstimator.Estimate(960, 540, 30, TimeSpan.FromSeconds(10));
        var atFifteen = GifExportEstimator.Estimate(960, 540, 15, TimeSpan.FromSeconds(10));

        Assert.AreEqual(2.0, atThirty.TotalMilliseconds / atFifteen.TotalMilliseconds, 0.01);
    }

    [TestMethod]
    public void Estimate_MatchesMeasuredEncoderCost_WithinAnOrderOfMagnitude()
    {
        // Measured on the shipping WIC GIF encoder: ~390 ms/frame at 1920x1080.
        // A 10s/30fps clip is 300 frames, i.e. roughly two minutes. The estimate is
        // deliberately conservative but must stay in the same ballpark, otherwise the
        // warning shown to the user is worse than no warning at all.
        var estimate = GifExportEstimator.Estimate(1920, 1080, 30, TimeSpan.FromSeconds(10));

        Assert.IsTrue(estimate.TotalSeconds > 30, $"Estimate too low: {estimate}");
        Assert.IsTrue(estimate.TotalSeconds < 300, $"Estimate too high: {estimate}");
    }

    [TestMethod]
    [DataRow(0, 1080, 30, 10)]
    [DataRow(1920, 0, 30, 10)]
    [DataRow(1920, 1080, 0, 10)]
    [DataRow(1920, 1080, 30, 0)]
    public void Estimate_DegenerateInput_ReturnsZeroRatherThanThrowing(
        int width, int height, int fps, int seconds)
    {
        var estimate = GifExportEstimator.Estimate(width, height, fps, TimeSpan.FromSeconds(seconds));

        Assert.AreEqual(TimeSpan.Zero, estimate);
    }

    [TestMethod]
    public void ResolveOutputSize_HonorsSelectedResolution()
    {
        var (width, height) = GifExportEstimator.ResolveOutputSize(
            3840, 2160, AspectRatio.Auto, VideoResolution.HD720);

        Assert.IsTrue(width <= 1280, $"Width {width} exceeded the 720p bound.");
        Assert.IsTrue(height <= 720, $"Height {height} exceeded the 720p bound.");
    }

    [TestMethod]
    public void ResolveOutputSize_NeverUpscalesBeyondSource()
    {
        var (width, height) = GifExportEstimator.ResolveOutputSize(
            640, 360, AspectRatio.Auto, VideoResolution.UHD4K);

        Assert.IsTrue(width <= 640, $"Width {width} upscaled beyond the source.");
        Assert.IsTrue(height <= 360, $"Height {height} upscaled beyond the source.");
    }

    [TestMethod]
    public void ResolveOutputSize_AppliesAspectRatio_NotRawSourceSize()
    {
        // A 16:9 recording exported at 9:16 composites onto a portrait canvas. Estimating
        // from the raw 1920x1080 source would claim ~3x the pixels the exporter produces.
        var (width, height) = GifExportEstimator.ResolveOutputSize(
            1920, 1080, AspectRatio.Portrait9x16, VideoResolution.HD1080);

        Assert.IsTrue(width < height, $"Expected a portrait result, got {width}x{height}.");
        Assert.IsTrue(width < 1920, $"Width {width} ignored the 9:16 canvas.");
    }

    [TestMethod]
    public void ResolveOutputSize_MatchesCompositorCanvasRule()
    {
        var (canvasWidth, canvasHeight) =
            AspectRatioHelper.ComputeCanvasSize(1920, 1080, AspectRatio.Square1x1);

        Assert.AreEqual(1080, canvasWidth);
        Assert.AreEqual(1080, canvasHeight);
    }

    [TestMethod]
    public void ResolveExportedDuration_PrefersTimelineOverRawRecording()
    {
        var timeline = new TimelineModel { Duration = TimeSpan.FromMinutes(10) };
        timeline.Segments.Add(new TextSlideSegment
        {
            Start = TimeSpan.Zero,
            Duration = TimeSpan.FromSeconds(20),
        });

        var resolved = GifExportEstimator.ResolveExportedDuration(
            timeline, TimeSpan.FromMinutes(10));

        Assert.AreEqual(TimeSpan.FromSeconds(20), resolved);
    }

    [TestMethod]
    public void ResolveExportedDuration_NoTimeline_FallsBackToProjectDuration()
    {
        var resolved = GifExportEstimator.ResolveExportedDuration(null, TimeSpan.FromSeconds(42));

        Assert.AreEqual(TimeSpan.FromSeconds(42), resolved);
    }

    [TestMethod]
    public void FormatEstimate_ShortExport_ReadsAsUnderAMinute()
    {
        Assert.AreEqual("under a minute", GifExportEstimator.FormatEstimate(TimeSpan.FromSeconds(25)));
    }

    [TestMethod]
    public void FormatEstimate_MinutesAndHours()
    {
        Assert.AreEqual("about 3 min", GifExportEstimator.FormatEstimate(TimeSpan.FromMinutes(3)));
        Assert.AreEqual("about 1 h 30 min", GifExportEstimator.FormatEstimate(TimeSpan.FromMinutes(90)));
    }

    [TestMethod]
    public void FormatEstimate_NoEstimate_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, GifExportEstimator.FormatEstimate(TimeSpan.Zero));
    }
}
