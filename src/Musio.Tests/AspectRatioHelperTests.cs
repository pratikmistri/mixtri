using Musio.Core.Processing;
using Musio.Core.Settings;

namespace Musio.Tests;

[TestClass]
public sealed class AspectRatioHelperTests
{
    #region GetRatio

    [TestMethod]
    [DataRow(AspectRatio.Landscape16x9, 16, 9)]
    [DataRow(AspectRatio.Portrait9x16, 9, 16)]
    [DataRow(AspectRatio.Square1x1, 1, 1)]
    [DataRow(AspectRatio.Classic4x3, 4, 3)]
    [DataRow(AspectRatio.Tall3x4, 3, 4)]
    [DataRow(AspectRatio.Auto, 0, 0)]
    public void GetRatio_ReturnsExpectedValues(AspectRatio ratio, int expectedW, int expectedH)
    {
        var (w, h) = AspectRatioHelper.GetRatio(ratio);
        Assert.AreEqual(expectedW, w);
        Assert.AreEqual(expectedH, h);
    }

    #endregion

    #region CalculateOutputDimensions

    [TestMethod]
    public void CalculateOutputDimensions_Auto1080p_FitsSourceAspectRatio()
    {
        var (w, h) = AspectRatioHelper.CalculateOutputDimensions(1920, 1080, AspectRatio.Auto, VideoResolution.HD1080);

        Assert.AreEqual(1920, w);
        Assert.AreEqual(1080, h);
    }

    [TestMethod]
    public void CalculateOutputDimensions_Portrait9x16_At1080p_ProducesPortrait()
    {
        var (w, h) = AspectRatioHelper.CalculateOutputDimensions(1920, 1080, AspectRatio.Portrait9x16, VideoResolution.HD1080);

        double ar = (double)w / h;
        Assert.AreEqual(9.0 / 16.0, ar, 0.02, "Aspect ratio should be ~9:16");
        Assert.IsTrue(w <= 1920 && h <= 1080, "Should fit within 1080p bounds");
    }

    [TestMethod]
    public void CalculateOutputDimensions_Square_At720p_ProducesSquare()
    {
        var (w, h) = AspectRatioHelper.CalculateOutputDimensions(1280, 720, AspectRatio.Square1x1, VideoResolution.HD720);

        Assert.AreEqual(w, h, "Square output should have equal width and height");
        Assert.IsTrue(w <= 1280 && h <= 720, "Should fit within 720p bounds");
    }

    [TestMethod]
    public void CalculateOutputDimensions_AlwaysEvenDimensions()
    {
        foreach (var ratio in Enum.GetValues<AspectRatio>())
        foreach (var res in Enum.GetValues<VideoResolution>())
        {
            var (w, h) = AspectRatioHelper.CalculateOutputDimensions(1920, 1080, ratio, res);
            Assert.AreEqual(0, w % 2, $"Width {w} not even for {ratio}/{res}");
            Assert.AreEqual(0, h % 2, $"Height {h} not even for {ratio}/{res}");
        }
    }

    [TestMethod]
    public void CalculateOutputDimensions_4K_ProducesLargerDimensions()
    {
        var (w1080, h1080) = AspectRatioHelper.CalculateOutputDimensions(1920, 1080, AspectRatio.Landscape16x9, VideoResolution.HD1080);
        var (w4k, h4k) = AspectRatioHelper.CalculateOutputDimensions(1920, 1080, AspectRatio.Landscape16x9, VideoResolution.UHD4K);

        Assert.IsTrue(w4k >= w1080 && h4k >= h1080, "4K dimensions should be >= 1080p");
    }

    [TestMethod]
    public void CalculateOutputDimensions_ZeroSource_FallsBackToResolution()
    {
        var (w, h) = AspectRatioHelper.CalculateOutputDimensions(0, 0, AspectRatio.Auto, VideoResolution.HD1080);

        Assert.IsTrue(w > 0 && h > 0, "Should produce valid dimensions even for zero source");
    }

    [TestMethod]
    public void CalculateOutputDimensions_TallSource_AutoFits()
    {
        // Very tall source (portrait webcam)
        var (w, h) = AspectRatioHelper.CalculateOutputDimensions(720, 1280, AspectRatio.Auto, VideoResolution.HD1080);

        Assert.IsTrue(h <= 1080, "Height should fit within 1080p");
        Assert.IsTrue(w <= 1920, "Width should fit within 1080p");
        double sourceAr = 720.0 / 1280;
        double outAr = (double)w / h;
        Assert.AreEqual(sourceAr, outAr, 0.02, "Auto should preserve source aspect ratio");
    }

    #endregion

    #region CalculateCropRect

    [TestMethod]
    public void CalculateCropRect_Auto_ReturnsFullSource()
    {
        var (x, y, w, h) = AspectRatioHelper.CalculateCropRect(1920, 1080, AspectRatio.Auto);

        Assert.AreEqual(0, x);
        Assert.AreEqual(0, y);
        Assert.AreEqual(1920, w);
        Assert.AreEqual(1080, h);
    }

    [TestMethod]
    public void CalculateCropRect_Square_CropsWiderSource()
    {
        // 1920x1080: wider than 1:1, so should crop horizontally
        var (x, y, w, h) = AspectRatioHelper.CalculateCropRect(1920, 1080, AspectRatio.Square1x1);

        Assert.AreEqual(w, h, "Crop should be square");
        Assert.AreEqual(1080, h, "Height should use full source height");
        Assert.IsTrue(x > 0, "Horizontal offset should be non-zero for center crop");
        Assert.AreEqual(0, y, "No vertical offset needed");
    }

    [TestMethod]
    public void CalculateCropRect_Portrait_CropsTallerPortion()
    {
        // 1920x1080 is wider than 9:16, so crops horizontally
        var (x, y, w, h) = AspectRatioHelper.CalculateCropRect(1920, 1080, AspectRatio.Portrait9x16);

        double ar = (double)w / h;
        Assert.AreEqual(9.0 / 16.0, ar, 0.02, "Crop should be 9:16");
    }

    [TestMethod]
    public void CalculateCropRect_16x9_OnSquareSource_CropsVertically()
    {
        // 1000x1000 source with 16:9 target → crop vertically
        var (x, y, w, h) = AspectRatioHelper.CalculateCropRect(1000, 1000, AspectRatio.Landscape16x9);

        Assert.AreEqual(1000, w, "Width should use full source width");
        Assert.IsTrue(h < 1000, "Height should be cropped");
        Assert.AreEqual(0, x, "No horizontal offset");
        Assert.IsTrue(y > 0, "Vertical offset for center crop");
    }

    [TestMethod]
    public void CalculateCropRect_CropFitsWithinSource()
    {
        foreach (var ratio in Enum.GetValues<AspectRatio>())
        {
            var (x, y, w, h) = AspectRatioHelper.CalculateCropRect(1920, 1080, ratio);

            Assert.IsTrue(x >= 0 && y >= 0, $"{ratio}: offset must be non-negative");
            Assert.IsTrue(x + w <= 1920, $"{ratio}: crop must fit in source width");
            Assert.IsTrue(y + h <= 1080, $"{ratio}: crop must fit in source height");
            Assert.IsTrue(w > 0 && h > 0, $"{ratio}: crop must have positive size");
        }
    }

    #endregion

    #region GetDisplayName

    [TestMethod]
    public void GetDisplayName_ReturnsNonEmpty_ForAllValues()
    {
        foreach (var ratio in Enum.GetValues<AspectRatio>())
        {
            string name = AspectRatioHelper.GetDisplayName(ratio);
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), $"Display name for {ratio} should not be empty");
        }
    }

    [TestMethod]
    public void GetDisplayName_Auto_ReturnsAuto()
    {
        Assert.AreEqual("Auto", AspectRatioHelper.GetDisplayName(AspectRatio.Auto));
    }

    [TestMethod]
    public void GetDisplayName_Landscape_Contains16x9()
    {
        Assert.IsTrue(AspectRatioHelper.GetDisplayName(AspectRatio.Landscape16x9).Contains("16:9"));
    }

    #endregion
}
