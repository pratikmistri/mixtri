using Musio.Core.Models;

namespace Musio.Core.Settings;

/// <summary>
/// Provides a library of built-in brand presets with professional color schemes.
/// </summary>
public static class DefaultBrandPresets
{
    public static BrandPreset DarkMinimal { get; } = new()
    {
        Name = "Midnight",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#1A1B26",
        Padding = 48,
        CornerRadius = 12,
        ShadowEnabled = true,
        ShadowBlur = 28,
        ShadowOpacity = 0.45,
        ShadowColor = "#000000",
        BorderEnabled = false
    };

    public static BrandPreset OceanGradient { get; } = new()
    {
        Name = "Mist",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#1B2A41",
        GradientEndColor = "#3E5C76",
        GradientAngle = 160.0,
        Padding = 80,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 48,
        ShadowOpacity = 0.5,
        ShadowColor = "#0A1322",
        BorderEnabled = false
    };

    public static BrandPreset SunsetGlow { get; } = new()
    {
        Name = "Dusk",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#3D2A4D",
        GradientEndColor = "#C98A6B",
        GradientAngle = 155.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.45,
        ShadowColor = "#1F1024",
        BorderEnabled = false
    };

    public static BrandPreset CleanWhite { get; } = new()
    {
        Name = "Porcelain",
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
        Name = "Twilight",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#241B36",
        GradientEndColor = "#5B4B7C",
        GradientAngle = 165.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 48,
        ShadowOpacity = 0.5,
        ShadowColor = "#0E0820",
        BorderEnabled = false
    };

    public static BrandPreset ForestGreen { get; } = new()
    {
        Name = "Pine",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#1F2E25",
        GradientEndColor = "#4A6B5C",
        GradientAngle = 155.0,
        Padding = 72,
        CornerRadius = 14,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.45,
        ShadowColor = "#0A1410",
        BorderEnabled = false
    };

    public static BrandPreset NeonDark { get; } = new()
    {
        Name = "Aurora",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#0F1419",
        Padding = 56,
        CornerRadius = 10,
        ShadowEnabled = true,
        ShadowBlur = 28,
        ShadowOpacity = 0.6,
        ShadowColor = "#000000",
        BorderEnabled = true,
        BorderWidth = 1,
        BorderColor = "#3DDC97"
    };

    public static BrandPreset SoftPastel { get; } = new()
    {
        Name = "Linen",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#EFE3D9",
        GradientEndColor = "#C9D6DF",
        GradientAngle = 160.0,
        Padding = 80,
        CornerRadius = 20,
        ShadowEnabled = true,
        ShadowBlur = 36,
        ShadowOpacity = 0.22,
        ShadowColor = "#5A5560",
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
