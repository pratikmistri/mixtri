namespace Musio.Tests;

using Musio.Core.Processing;
using Musio.Core.Settings;

[TestClass]
public sealed class ExportDimensionsTests
{
    [TestMethod]
    public void ComputeExportDimensions_16x9_At1080p_FillsExactly()
    {
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1920, 1080, VideoResolution.HD1080);
        Assert.AreEqual(1920, w);
        Assert.AreEqual(1072, h, "1080 floored to mod-16 is 1072");
    }

    [TestMethod]
    public void ComputeExportDimensions_PortraitAt1080p_HeightIsBound()
    {
        // Portrait 9:16 source, e.g. 1080×1920 compositor output
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1080, 1920, VideoResolution.HD1080);
        // Bounds become min(1920, 1080)=1080W × min(1080, 1920)=1080H.
        // Fitting 9:16 within 1080×1080: width-bound at 1080? No, height-bound.
        // FitWithinBounds: outW=1080, outH=1080/(1080/1920)=1920 → too tall.
        // → outH=1080, outW=1080*(1080/1920)=607.5 → 608 mod-16 → 608
        Assert.AreEqual(608, w);
        Assert.AreEqual(1072, h);
    }

    [TestMethod]
    public void ComputeExportDimensions_Square_At1080p()
    {
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1500, 1500, VideoResolution.HD1080);
        // Bounds min(1920,1500)=1500W, min(1080,1500)=1080H. Fit 1:1 → 1080×1080 → 1072×1072.
        Assert.AreEqual(1072, w);
        Assert.AreEqual(1072, h);
    }

    [TestMethod]
    public void ComputeExportDimensions_SmallSource_NotUpscaled()
    {
        // 720p source asked to export at 4K: should stay around 720p, not upscale.
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1280, 720, VideoResolution.UHD4K);
        Assert.AreEqual(1280, w);
        Assert.AreEqual(720, h);
    }

    [TestMethod]
    public void ComputeExportDimensions_UltraWideAt1080p_KeepsAspect()
    {
        // 21:9 cinematic compositor output (e.g. 2520×1080)
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(2520, 1080, VideoResolution.HD1080);
        // Bounds min(1920,2520)=1920W, min(1080,1080)=1080H.
        // Fit 21:9 within 1920×1080: width-bound, outW=1920, outH=1920/(2520/1080)=822.86 → 822 even.
        // mod-16 floor: 1920 stays, 822/16 = 51.375 → 816.
        Assert.AreEqual(1920, w);
        Assert.AreEqual(816, h);
        // Sanity: aspect drift is small
        double sourceAr = 2520.0 / 1080.0;
        double outAr = (double)w / h;
        Assert.IsTrue(Math.Abs(sourceAr - outAr) / sourceAr < 0.02, "aspect drift < 2%");
    }

    [TestMethod]
    public void ComputeExportDimensions_At720p_CapsSmaller()
    {
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1920, 1080, VideoResolution.HD720);
        Assert.AreEqual(1280, w);
        // 720 mod-16 = 720 (exact)
        Assert.AreEqual(720, h);
    }

    [TestMethod]
    public void ComputeExportDimensions_DegenerateInput_ReturnsSafeMinimum()
    {
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(0, 0, VideoResolution.HD1080);
        Assert.IsTrue(w >= 16);
        Assert.IsTrue(h >= 16);
    }
}
