using System.IO;
using Musio.Core.Models;

namespace Musio.Core.Settings;

/// <summary>
/// Built-in background presets offering a spread of styles — a clean neutral
/// solid for distraction-free framing; bold saturated two-stop gradients for
/// marketing-grade visuals; and photographic Windows wallpapers (sourced from
/// the system wallpaper folder) for richer scenes.
/// </summary>
public static class DefaultBrandPresets
{
    // Resolved once: the OS wallpaper folder (e.g. C:\Windows\Web\Wallpaper).
    // Used by the image presets below. If a file is missing on a given
    // machine, BackgroundCompositor falls back to the preset's solid color.
    private static readonly string WallpaperDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");

    // --- Solid color ----------------------------------------------------

    public static BrandPreset Graphite { get; } = new()
    {
        Name = "Graphite",
        BackgroundType = BackgroundType.SolidColor,
        BackgroundColor = "#2B2D31",
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 40,
        ShadowOpacity = 0.5,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    // --- Gradients ------------------------------------------------------

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

    // --- Gradients (continued) ------------------------------------------

    public static BrandPreset Emerald { get; } = new()
    {
        Name = "Emerald",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#06402B",
        GradientEndColor = "#10B981",
        GradientAngle = 135.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.5,
        ShadowColor = "#04231A",
        BorderEnabled = false,
    };

    public static BrandPreset Tide { get; } = new()
    {
        Name = "Tide",
        BackgroundType = BackgroundType.Gradient,
        BackgroundColor = "#1E3A8A",
        GradientEndColor = "#22D3EE",
        GradientAngle = 150.0,
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 44,
        ShadowOpacity = 0.5,
        ShadowColor = "#0A1B3A",
        BorderEnabled = false,
    };

    // --- Wallpapers (photographic) -------------------------------------

    public static BrandPreset WindowsLight { get; } = new()
    {
        Name = "Windows Light",
        BackgroundType = BackgroundType.Image,
        BackgroundImagePath = Path.Combine(WallpaperDir, "Windows", "img0.jpg"),
        BackgroundColor = "#1B3A5B",
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 48,
        ShadowOpacity = 0.55,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    public static BrandPreset WindowsDark { get; } = new()
    {
        Name = "Windows Dark",
        BackgroundType = BackgroundType.Image,
        BackgroundImagePath = Path.Combine(WallpaperDir, "Windows", "img19.jpg"),
        BackgroundColor = "#2B2336",
        Padding = 72,
        CornerRadius = 16,
        ShadowEnabled = true,
        ShadowBlur = 48,
        ShadowOpacity = 0.55,
        ShadowColor = "#000000",
        BorderEnabled = false,
    };

    public static IReadOnlyList<BrandPreset> All { get; } = new[]
    {
        Graphite,
        Nebula,
        Lagoon,
        Sunset,
        Emerald,
        Tide,
        WindowsLight,
        WindowsDark,
    };
}
