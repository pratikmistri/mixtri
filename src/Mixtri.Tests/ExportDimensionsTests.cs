namespace Mixtri.Tests;

using Mixtri.Core.Processing;
using Mixtri.Core.Settings;

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

    [TestMethod]
    public void ComputeExportDimensions_TinyCompositor_NotUpscaledAbove16()
    {
        // 8×8 compositor must not be padded up to 16×16 (would violate no-upscale).
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(8, 8, VideoResolution.HD1080);
        Assert.IsTrue(w <= 8, $"width {w} must not exceed source 8");
        Assert.IsTrue(h <= 8, $"height {h} must not exceed source 8");
        Assert.AreEqual(0, w % 2, "must be even for encoder");
        Assert.AreEqual(0, h % 2, "must be even for encoder");
    }

    [TestMethod]
    public void ComputeExportDimensions_FourThree_At1080p()
    {
        // 4:3 compositor output, e.g. 1440×1080
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(1440, 1080, VideoResolution.HD1080);
        // Bounds min(1920,1440)=1440W, min(1080,1080)=1080H. Fit 4:3 in 1440×1080:
        // width-bound at 1440 → height = 1440*(3/4)=1080. Mod-16: 1440 stays, 1080→1072.
        Assert.AreEqual(1440, w);
        Assert.AreEqual(1072, h);
    }

    [DataTestMethod]
    [DataRow(1920, 1080, VideoResolution.HD720)]
    [DataRow(1920, 1080, VideoResolution.HD1080)]
    [DataRow(2560, 1440, VideoResolution.QHD)]
    [DataRow(3840, 2160, VideoResolution.UHD4K)]
    [DataRow(1080, 1920, VideoResolution.HD1080)]
    [DataRow(1500, 1500, VideoResolution.HD1080)]
    [DataRow(2520, 1080, VideoResolution.HD1080)]
    [DataRow(1280, 720, VideoResolution.UHD4K)]
    public void ComputeExportDimensions_AlwaysHonorsInvariants(int compW, int compH, VideoResolution res)
    {
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(compW, compH, res);
        var (maxW, maxH) = AspectRatioHelper.GetResolutionBounds(res);

        // Never exceeds resolution bound
        Assert.IsTrue(w <= maxW, $"w {w} exceeded maxW {maxW}");
        Assert.IsTrue(h <= maxH, $"h {h} exceeded maxH {maxH}");

        // Never exceeds compositor native size (no upscale)
        Assert.IsTrue(w <= compW, $"w {w} exceeded compositor {compW}");
        Assert.IsTrue(h <= compH, $"h {h} exceeded compositor {compH}");

        // Even dimensions required by encoder
        Assert.AreEqual(0, w % 2, $"w {w} not even");
        Assert.AreEqual(0, h % 2, $"h {h} not even");

        // Aspect drift bounded (mod-16 floor is the only source of drift)
        double srcAr = (double)compW / compH;
        double outAr = (double)w / h;
        double drift = Math.Abs(srcAr - outAr) / srcAr;
        Assert.IsTrue(drift < 0.03, $"aspect drift {drift:P} exceeds 3%");
    }

    [TestMethod]
    public void ComputeExportDimensions_UnknownEnum_FallsBackTo1080Bounds()
    {
        // Cast an out-of-range int to the enum; GetResolutionBounds default arm
        // returns (1920, 1080) and the helper must still produce sane output.
        var (w, h) = AspectRatioHelper.ComputeExportDimensions(
            1920, 1080, (VideoResolution)999);
        Assert.IsTrue(w > 0 && w <= 1920);
        Assert.IsTrue(h > 0 && h <= 1080);
    }
}
