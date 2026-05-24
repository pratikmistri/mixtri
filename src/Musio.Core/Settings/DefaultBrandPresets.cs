using Musio.Core.Models;

namespace Musio.Core.Settings;

/// <summary>
/// Built-in background presets featuring bold, saturated two-stop gradients
/// designed to read well behind screen recordings — high contrast against
/// typical UI content, vivid enough for marketing-grade visuals without
/// fighting the foreground for attention.
/// </summary>
public static class DefaultBrandPresets
{
    public static BrandPreset Nebula { get; } = new()
    {
        Name = "Nebula",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#1A0B3C",
        GradientEndColor = "#E94560",
        GradientAngle = 135.0,
        Padding = 72,
        CornerRadius = 18,
        ShadowEnabled = true,
        ShadowBlur = 48,
        ShadowOpacity = 0.55,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    public static BrandPreset Lagoon { get; } = new()
    {
        Name = "Lagoon",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#2E1A47",
        GradientEndColor = "#00B7C3",
        GradientAngle = 145.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.5,
        ShadowColor = "#0A0820",
        BorderEnabled = false,
    };

    public static BrandPreset Prism { get; } = new()
    {
        Name = "Prism",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#4796E3",
        GradientEndColor = "#FF6363",
        GradientAngle = 140.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 40,
        ShadowOpacity = 0.45,
        ShadowColor = "#1A1A2E",
        BorderEnabled = false,
    };

    public static BrandPreset Emerald { get; } = new()
    {
        Name = "Emerald",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#1DB954",
        GradientEndColor = "#121212",
        GradientAngle = 160.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.55,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    public static BrandPreset Coral { get; } = new()
    {
        Name = "Coral",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#FF5A5F",
        GradientEndColor = "#BD1E59",
        GradientAngle = 150.0,
        Padding = 72,
        CornerRadius = 18,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.45,
        ShadowColor = "#3A0A1F",
        BorderEnabled = false,
    };

    public static BrandPreset Ember { get; } = new()
    {
        Name = "Ember",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#131A22",
        GradientEndColor = "#FF9900",
        GradientAngle = 160.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.55,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    public static BrandPreset Tide { get; } = new()
    {
        Name = "Tide",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#6772E5",
        GradientEndColor = "#00D4FF",
        GradientAngle = 135.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 40,
        ShadowOpacity = 0.4,
        ShadowColor = "#10183A",
        BorderEnabled = false,
    };

    public static BrandPreset Sunset { get; } = new()
    {
        Name = "Sunset",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#F58529",
        GradientEndColor = "#8134AF",
        GradientAngle = 140.0,
        Padding = 72,
        CornerRadius = 18,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.45,
        ShadowColor = "#2A0E2F",
        BorderEnabled = false,
    };

    public static IReadOnlyList<BrandPreset> All { get; } = new[]
    {
        Nebula,
        Lagoon,
        Prism,
        Emerald,
        Coral,
        Ember,
        Tide,
        Sunset,
    };
}
