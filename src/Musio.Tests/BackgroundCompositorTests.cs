namespace Musio.Tests;

using Musio.Core.Models;
using Musio.Core.Processing;

[TestClass]
public sealed class BackgroundCompositorTests
{
    #region CalculateOutputSize

    [TestMethod]
    public void CalculateOutputSize_WithDefaultPadding_ReturnsCanvasUnchanged()
    {
        var compositor = new BackgroundCompositor();
        var style = new BackgroundStyle(); // Padding defaults to 48

        var (width, height) = compositor.CalculateOutputSize(1920, 1080, style);

        // Padding now insets the content within the canvas; canvas size is unchanged.
        Assert.AreEqual(1920, width);
        Assert.AreEqual(1080, height);
    }

    [TestMethod]
    public void CalculateOutputSize_WithZeroPadding_ReturnsSameSize()
    {
        var compositor = new BackgroundCompositor();
        var style = new BackgroundStyle { Padding = 0 };

        var (width, height) = compositor.CalculateOutputSize(1920, 1080, style);

        Assert.AreEqual(1920, width);
        Assert.AreEqual(1080, height);
    }

    [TestMethod]
    public void CalculateOutputSize_WithLargePadding_ReturnsCanvasUnchanged()
    {
        var compositor = new BackgroundCompositor();
        var style = new BackgroundStyle { Padding = 200 };

        var (width, height) = compositor.CalculateOutputSize(800, 600, style);

        Assert.AreEqual(800, width);
        Assert.AreEqual(600, height);
    }

    [TestMethod]
    public void CalculateOutputSize_SmallSource_Works()
    {
        var compositor = new BackgroundCompositor();
        var style = new BackgroundStyle { Padding = 10 };

        var (width, height) = compositor.CalculateOutputSize(1, 1, style);

        Assert.AreEqual(1, width);
        Assert.AreEqual(1, height);
    }

    #endregion

    #region BackgroundStyle Defaults

    [TestMethod]
    public void BackgroundStyle_DefaultValues_AreCorrect()
    {
        var style = new BackgroundStyle();

        Assert.AreEqual(BackgroundType.SolidColor, style.Type);
        Assert.AreEqual("#1a1a2e", style.Color);
        Assert.AreEqual("#16213e", style.GradientEndColor);
        Assert.AreEqual(135.0, style.GradientAngle);
        Assert.IsNull(style.BackgroundImagePath);
        Assert.AreEqual(48, style.Padding);
        Assert.AreEqual(12, style.CornerRadius);
        Assert.IsTrue(style.ShadowEnabled);
        Assert.AreEqual(24, style.ShadowBlur);
        Assert.AreEqual(0.5, style.ShadowOpacity);
        Assert.AreEqual("#000000", style.ShadowColor);
        Assert.AreEqual(0, style.ShadowOffsetX);
        Assert.AreEqual(4, style.ShadowOffsetY);
        Assert.IsFalse(style.BorderEnabled);
        Assert.AreEqual(1, style.BorderWidth);
        Assert.AreEqual("#333333", style.BorderColor);
    }

    [TestMethod]
    public void BackgroundStyle_WithCustomValues_ReturnsNewRecord()
    {
        var original = new BackgroundStyle();
        var modified = original with { Padding = 100, CornerRadius = 24, ShadowEnabled = false };

        Assert.AreEqual(48, original.Padding);
        Assert.AreEqual(100, modified.Padding);
        Assert.AreEqual(24, modified.CornerRadius);
        Assert.IsFalse(modified.ShadowEnabled);
    }

    #endregion

    #region ColorHelper.ParseColor

    [TestMethod]
    public void ParseColor_ShortHex_ExpandsNibbles()
    {
        var color = ColorHelper.ParseColor("#F80");

        Assert.AreEqual(0xFF, color.A);
        Assert.AreEqual(0xFF, color.R);
        Assert.AreEqual(0x88, color.G);
        Assert.AreEqual(0x00, color.B);
    }

    [TestMethod]
    public void ParseColor_SixDigitHex_ParsesCorrectly()
    {
        var color = ColorHelper.ParseColor("#1A2B3C");

        Assert.AreEqual(0xFF, color.A);
        Assert.AreEqual(0x1A, color.R);
        Assert.AreEqual(0x2B, color.G);
        Assert.AreEqual(0x3C, color.B);
    }

    [TestMethod]
    public void ParseColor_EightDigitHex_ParsesAlphaCorrectly()
    {
        var color = ColorHelper.ParseColor("#80FF0000");

        Assert.AreEqual(0x80, color.A);
        Assert.AreEqual(0xFF, color.R);
        Assert.AreEqual(0x00, color.G);
        Assert.AreEqual(0x00, color.B);
    }

    [TestMethod]
    public void ParseColor_Black_ReturnsCorrect()
    {
        var color = ColorHelper.ParseColor("#000000");

        Assert.AreEqual(0xFF, color.A);
        Assert.AreEqual(0x00, color.R);
        Assert.AreEqual(0x00, color.G);
        Assert.AreEqual(0x00, color.B);
    }

    [TestMethod]
    public void ParseColor_White_ReturnsCorrect()
    {
        var color = ColorHelper.ParseColor("#FFFFFF");

        Assert.AreEqual(0xFF, color.A);
        Assert.AreEqual(0xFF, color.R);
        Assert.AreEqual(0xFF, color.G);
        Assert.AreEqual(0xFF, color.B);
    }

    [TestMethod]
    public void ParseColor_WithoutHash_StillWorks()
    {
        var color = ColorHelper.ParseColor("FF8000");

        Assert.AreEqual(0xFF, color.A);
        Assert.AreEqual(0xFF, color.R);
        Assert.AreEqual(0x80, color.G);
        Assert.AreEqual(0x00, color.B);
    }

    [TestMethod]
    public void ParseColor_InvalidLength_ThrowsFormatException()
    {
        Assert.ThrowsException<FormatException>(() => ColorHelper.ParseColor("#12345"));
    }

    [TestMethod]
    public void ParseColor_Null_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => ColorHelper.ParseColor(null!));
    }

    #endregion

    #region ColorHelper.WithOpacity

    [TestMethod]
    public void WithOpacity_FullOpacity_ReturnsAlpha255()
    {
        var color = ColorHelper.ParseColor("#FF0000");
        var result = ColorHelper.WithOpacity(color, 1.0);

        Assert.AreEqual(255, result.A);
        Assert.AreEqual(color.R, result.R);
        Assert.AreEqual(color.G, result.G);
        Assert.AreEqual(color.B, result.B);
    }

    [TestMethod]
    public void WithOpacity_ZeroOpacity_ReturnsAlpha0()
    {
        var color = ColorHelper.ParseColor("#00FF00");
        var result = ColorHelper.WithOpacity(color, 0.0);

        Assert.AreEqual(0, result.A);
    }

    [TestMethod]
    public void WithOpacity_HalfOpacity_ReturnsAlpha127Or128()
    {
        var color = ColorHelper.ParseColor("#0000FF");
        var result = ColorHelper.WithOpacity(color, 0.5);

        // 0.5 * 255 = 127.5 → clamped via Math.Clamp then cast to byte
        Assert.IsTrue(result.A is 127 or 128, $"Expected 127 or 128, got {result.A}");
    }

    [TestMethod]
    public void WithOpacity_OverOne_ClampedTo255()
    {
        var color = ColorHelper.ParseColor("#AABBCC");
        var result = ColorHelper.WithOpacity(color, 2.0);

        Assert.AreEqual(255, result.A);
    }

    [TestMethod]
    public void WithOpacity_Negative_ClampedTo0()
    {
        var color = ColorHelper.ParseColor("#AABBCC");
        var result = ColorHelper.WithOpacity(color, -0.5);

        Assert.AreEqual(0, result.A);
    }

    #endregion
}
