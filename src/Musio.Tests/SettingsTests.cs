namespace Musio.Tests;

using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;

[TestClass]
public sealed class SettingsTests
{
    #region ExportPreset Defaults

    [TestMethod]
    public void ExportPreset_DefaultValues_AreCorrect()
    {
        var preset = new ExportPreset();

        Assert.AreEqual(string.Empty, preset.Name);
        Assert.AreEqual(VideoResolution.HD1080, preset.Resolution);
        Assert.AreEqual(30, preset.Fps);
        Assert.AreEqual(VideoFormat.MP4, preset.Format);
        Assert.AreEqual(AspectRatio.Auto, preset.AspectRatio);
        Assert.AreEqual(VideoQuality.High, preset.Quality);
    }

    [TestMethod]
    public void ExportPreset_SetValues_AreRetained()
    {
        var preset = new ExportPreset
        {
            Name = "4K Export",
            Resolution = VideoResolution.UHD4K,
            Fps = 30,
            Format = VideoFormat.WebM,
            AspectRatio = AspectRatio.Landscape16x9,
            Quality = VideoQuality.Ultra,
        };

        Assert.AreEqual("4K Export", preset.Name);
        Assert.AreEqual(VideoResolution.UHD4K, preset.Resolution);
        Assert.AreEqual(30, preset.Fps);
        Assert.AreEqual(VideoFormat.WebM, preset.Format);
        Assert.AreEqual(AspectRatio.Landscape16x9, preset.AspectRatio);
        Assert.AreEqual(VideoQuality.Ultra, preset.Quality);
    }

    #endregion

    #region Enum Coverage

    [TestMethod]
    public void VideoResolution_AllValues_AreDefined()
    {
        var values = Enum.GetValues<VideoResolution>();
        Assert.AreEqual(4, values.Length);
        CollectionAssert.Contains(values, VideoResolution.HD720);
        CollectionAssert.Contains(values, VideoResolution.HD1080);
        CollectionAssert.Contains(values, VideoResolution.QHD);
        CollectionAssert.Contains(values, VideoResolution.UHD4K);
    }

    [TestMethod]
    public void VideoFormat_AllValues_AreDefined()
    {
        var values = Enum.GetValues<VideoFormat>();
        Assert.AreEqual(3, values.Length);
        CollectionAssert.Contains(values, VideoFormat.MP4);
        CollectionAssert.Contains(values, VideoFormat.GIF);
        CollectionAssert.Contains(values, VideoFormat.WebM);
    }

    [TestMethod]
    public void AspectRatio_AllValues_AreDefined()
    {
        var values = Enum.GetValues<AspectRatio>();
        Assert.AreEqual(6, values.Length);
        CollectionAssert.Contains(values, AspectRatio.Auto);
        CollectionAssert.Contains(values, AspectRatio.Square1x1);
    }

    [TestMethod]
    public void VideoQuality_AllValues_AreDefined()
    {
        var values = Enum.GetValues<VideoQuality>();
        Assert.AreEqual(4, values.Length);
        CollectionAssert.Contains(values, VideoQuality.Draft);
        CollectionAssert.Contains(values, VideoQuality.Ultra);
    }

    #endregion

    #region BrandPreset

    [TestMethod]
    public void BrandPreset_DefaultValues_AreCorrect()
    {
        var preset = new BrandPreset();

        Assert.AreEqual(string.Empty, preset.Name);
        Assert.AreEqual(BackgroundType.SolidColor, preset.BackgroundType);
        Assert.AreEqual("#1E1E2E", preset.BackgroundColor);
        Assert.AreEqual("#302D41", preset.GradientEndColor);
        Assert.AreEqual(135.0, preset.GradientAngle);
        Assert.AreEqual(string.Empty, preset.BackgroundImagePath);
        Assert.AreEqual(64, preset.Padding);
        Assert.AreEqual(12, preset.CornerRadius);
        Assert.IsTrue(preset.ShadowEnabled);
        Assert.AreEqual(40, preset.ShadowBlur);
        Assert.AreEqual(0.5, preset.ShadowOpacity);
        Assert.AreEqual("#000000", preset.ShadowColor);
        Assert.IsFalse(preset.BorderEnabled);
        Assert.AreEqual(1, preset.BorderWidth);
        Assert.AreEqual("#FFFFFF", preset.BorderColor);
    }

    [TestMethod]
    public void BrandPreset_CustomConstruction_Works()
    {
        var preset = new BrandPreset
        {
            Name = "My Brand",
            BackgroundType = BackgroundType.Gradient,
            BackgroundColor = "#FF0000",
            GradientEndColor = "#0000FF",
            GradientAngle = 90.0,
            Padding = 100,
            CornerRadius = 20,
            ShadowEnabled = false,
            BorderEnabled = true,
            BorderWidth = 3,
            BorderColor = "#00FF00",
        };

        Assert.AreEqual("My Brand", preset.Name);
        Assert.AreEqual(BackgroundType.Gradient, preset.BackgroundType);
        Assert.AreEqual("#FF0000", preset.BackgroundColor);
        Assert.AreEqual(90.0, preset.GradientAngle);
        Assert.AreEqual(100, preset.Padding);
        Assert.IsFalse(preset.ShadowEnabled);
        Assert.IsTrue(preset.BorderEnabled);
        Assert.AreEqual(3, preset.BorderWidth);
    }

    #endregion

    #region BrandPresetConverter

    [TestMethod]
    public void BrandPresetConverter_ToBackgroundStyle_MapsAllFields()
    {
        var preset = new BrandPreset
        {
            Name = "Test",
            BackgroundType = BackgroundType.Gradient,
            BackgroundColor = "#ABCDEF",
            GradientEndColor = "#123456",
            GradientAngle = 45.0,
            BackgroundImagePath = "C:\\image.png",
            Padding = 50,
            CornerRadius = 8,
            ShadowEnabled = false,
            ShadowBlur = 10,
            ShadowOpacity = 0.3,
            ShadowColor = "#111111",
            BorderEnabled = true,
            BorderWidth = 2,
            BorderColor = "#EEEEEE",
        };

        var style = BrandPresetConverter.ToBackgroundStyle(preset);

        Assert.AreEqual(BackgroundType.Gradient, style.Type);
        Assert.AreEqual("#ABCDEF", style.Color);
        Assert.AreEqual("#123456", style.GradientEndColor);
        Assert.AreEqual(45.0, style.GradientAngle);
        Assert.AreEqual("C:\\image.png", style.BackgroundImagePath);
        Assert.AreEqual(50, style.Padding);
        Assert.AreEqual(8, style.CornerRadius);
        Assert.IsFalse(style.ShadowEnabled);
        Assert.AreEqual(10, style.ShadowBlur);
        Assert.AreEqual(0.3, style.ShadowOpacity);
        Assert.AreEqual("#111111", style.ShadowColor);
        Assert.IsTrue(style.BorderEnabled);
        Assert.AreEqual(2, style.BorderWidth);
        Assert.AreEqual("#EEEEEE", style.BorderColor);
    }

    [TestMethod]
    public void BrandPresetConverter_EmptyImagePath_BecomesNull()
    {
        var preset = new BrandPreset { BackgroundImagePath = string.Empty };

        var style = BrandPresetConverter.ToBackgroundStyle(preset);

        Assert.IsNull(style.BackgroundImagePath);
    }

    [TestMethod]
    public void BrandPresetConverter_RoundTrip_PreservesValues()
    {
        var original = new BrandPreset
        {
            Name = "Round Trip",
            BackgroundType = BackgroundType.SolidColor,
            BackgroundColor = "#AABBCC",
            GradientEndColor = "#DDEEFF",
            GradientAngle = 180.0,
            BackgroundImagePath = "",
            Padding = 72,
            CornerRadius = 16,
            ShadowEnabled = true,
            ShadowBlur = 32,
            ShadowOpacity = 0.6,
            ShadowColor = "#222222",
            BorderEnabled = false,
            BorderWidth = 4,
            BorderColor = "#444444",
        };

        var style = BrandPresetConverter.ToBackgroundStyle(original);
        var roundTripped = BrandPresetConverter.FromBackgroundStyle(style, "Round Trip");

        Assert.AreEqual(original.Name, roundTripped.Name);
        Assert.AreEqual(original.BackgroundType, roundTripped.BackgroundType);
        Assert.AreEqual(original.BackgroundColor, roundTripped.BackgroundColor);
        Assert.AreEqual(original.GradientEndColor, roundTripped.GradientEndColor);
        Assert.AreEqual(original.GradientAngle, roundTripped.GradientAngle);
        // Empty → null → empty round trip
        Assert.AreEqual(string.Empty, roundTripped.BackgroundImagePath);
        Assert.AreEqual(original.Padding, roundTripped.Padding);
        Assert.AreEqual(original.CornerRadius, roundTripped.CornerRadius);
        Assert.AreEqual(original.ShadowEnabled, roundTripped.ShadowEnabled);
        Assert.AreEqual(original.ShadowBlur, roundTripped.ShadowBlur);
        Assert.AreEqual(original.ShadowOpacity, roundTripped.ShadowOpacity);
        Assert.AreEqual(original.ShadowColor, roundTripped.ShadowColor);
        Assert.AreEqual(original.BorderEnabled, roundTripped.BorderEnabled);
        Assert.AreEqual(original.BorderWidth, roundTripped.BorderWidth);
        Assert.AreEqual(original.BorderColor, roundTripped.BorderColor);
    }

    [TestMethod]
    public void BrandPresetConverter_FromBackgroundStyle_SetsName()
    {
        var style = new BackgroundStyle();
        var preset = BrandPresetConverter.FromBackgroundStyle(style, "Custom Name");

        Assert.AreEqual("Custom Name", preset.Name);
    }

    #endregion

    #region DefaultBrandPresets

    [TestMethod]
    public void DefaultBrandPresets_All_ReturnsEightPresets()
    {
        Assert.AreEqual(8, DefaultBrandPresets.All.Count);
    }

    [TestMethod]
    public void DefaultBrandPresets_All_NoNullNames()
    {
        foreach (var preset in DefaultBrandPresets.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Name),
                "All presets must have a non-empty Name");
        }
    }

    [TestMethod]
    public void DefaultBrandPresets_All_NoNullColors()
    {
        foreach (var preset in DefaultBrandPresets.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.BackgroundColor),
                $"Preset '{preset.Name}' must have a BackgroundColor");
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.ShadowColor),
                $"Preset '{preset.Name}' must have a ShadowColor");
        }
    }

    [TestMethod]
    public void DefaultBrandPresets_All_ColorsAreParseable()
    {
        foreach (var preset in DefaultBrandPresets.All)
        {
            // Should not throw
            ColorHelper.ParseColor(preset.BackgroundColor);
            ColorHelper.ParseColor(preset.ShadowColor);
            if (!string.IsNullOrEmpty(preset.GradientEndColor))
                ColorHelper.ParseColor(preset.GradientEndColor);
            if (!string.IsNullOrEmpty(preset.BorderColor))
                ColorHelper.ParseColor(preset.BorderColor);
        }
    }

    [TestMethod]
    public void DefaultBrandPresets_All_HaveUniqueNames()
    {
        var names = DefaultBrandPresets.All.Select(p => p.Name).ToList();
        Assert.AreEqual(names.Count, names.Distinct().Count(),
            "All preset names should be unique");
    }

    [TestMethod]
    public void DefaultBrandPresets_NamedPresets_AreInAllCollection()
    {
        var all = DefaultBrandPresets.All;
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.DarkMinimal);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.OceanGradient);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.SunsetGlow);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.CleanWhite);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.DeepPurple);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.ForestGreen);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.NeonDark);
        CollectionAssert.Contains((System.Collections.ICollection)all, DefaultBrandPresets.SoftPastel);
    }

    #endregion
}
