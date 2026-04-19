using Musio.Core.Models;

namespace Musio.Core.Settings;

/// <summary>
/// Provides a library of built-in brand presets with professional color schemes.
/// </summary>
public static class DefaultBrandPresets
{
    public static BrandPreset DarkMinimal { get; } = new()
    {
        Name = "Dark Minimal",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#1E1E2E",
        Padding = 48,
        CornerRadius = 12,
        ShadowEnabled = true,
        ShadowBlur = 20,
        ShadowOpacity = 0.4,
        ShadowColor = "#000000",
        BorderEnabled = false
    };

    public static BrandPreset OceanGradient { get; } = new()
    {
        Name = "Ocean Gradient",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#0F2027",
        GradientEndColor = "#2C5364",
        GradientAngle = 135.0,
        Padding = 80,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 40,
        ShadowOpacity = 0.5,
        ShadowColor = "#000000",
        BorderEnabled = false
    };

    public static BrandPreset SunsetGlow { get; } = new()
    {
        Name = "Sunset Glow",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#F857A6",
        GradientEndColor = "#FF5858",
        GradientAngle = 120.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 36,
        ShadowOpacity = 0.45,
        ShadowColor = "#3D0A1E",
        BorderEnabled = false
    };

    public static BrandPreset CleanWhite { get; } = new()
    {
        Name = "Clean White",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#F5F5F7",
        Padding = 64,
        CornerRadius = 14,
        ShadowEnabled = true,
        ShadowBlur = 30,
        ShadowOpacity = 0.25,
        ShadowColor = "#000000",
        BorderEnabled = true,
        BorderWidth = 1,
        BorderColor = "#D1D1D6"
    };

    public static BrandPreset DeepPurple { get; } = new()
    {
        Name = "Deep Purple",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#4A0E4E",
        GradientEndColor = "#812F90",
        GradientAngle = 150.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 40,
        ShadowOpacity = 0.5,
        ShadowColor = "#1A0520",
        BorderEnabled = false
    };

    public static BrandPreset ForestGreen { get; } = new()
    {
        Name = "Forest Green",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#0B3D0B",
        GradientEndColor = "#1B8A1B",
        GradientAngle = 140.0,
        Padding = 72,
        CornerRadius = 14,
        ShadowEnabled = true,
        ShadowBlur = 36,
        ShadowOpacity = 0.45,
        ShadowColor = "#041A04",
        BorderEnabled = false
    };

    public static BrandPreset NeonDark { get; } = new()
    {
        Name = "Neon Dark",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#0D0D0D",
        Padding = 56,
        CornerRadius = 10,
        ShadowEnabled = true,
        ShadowBlur = 24,
        ShadowOpacity = 0.6,
        ShadowColor = "#000000",
        BorderEnabled = true,
        BorderWidth = 2,
        BorderColor = "#00FFAB"
    };

    public static BrandPreset SoftPastel { get; } = new()
    {
        Name = "Soft Pastel",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#FADADD",
        GradientEndColor = "#D4F1F9",
        GradientAngle = 135.0,
        Padding = 80,
        CornerRadius = 20,
        ShadowEnabled = true,
        ShadowBlur = 32,
        ShadowOpacity = 0.2,
        ShadowColor = "#4A4A4A",
        BorderEnabled = false
    };

    public static IReadOnlyList<BrandPreset> All { get; } = new[]
    {
        DarkMinimal,
        OceanGradient,
        SunsetGlow,
        CleanWhite,
        DeepPurple,
        ForestGreen,
        NeonDark,
        SoftPastel
    };
}
