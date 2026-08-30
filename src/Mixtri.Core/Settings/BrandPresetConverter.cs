using Mixtri.Core.Processing;

namespace Mixtri.Core.Settings;

/// <summary>
/// Converts between <see cref="BrandPreset"/> (Settings layer) and
/// <see cref="BackgroundStyle"/> (Processing layer).
/// </summary>
public static class BrandPresetConverter
{
    /// <summary>
    /// Converts a <see cref="BrandPreset"/> to a <see cref="BackgroundStyle"/>
    /// for use by <see cref="BackgroundCompositor"/>.
    /// </summary>
    public static BackgroundStyle ToBackgroundStyle(BrandPreset preset)
    {
        return new BackgroundStyle
        {
            Type = preset.BackgroundType,
            Color = preset.BackgroundColor,
            GradientEndColor = preset.GradientEndColor,
            GradientAngle = preset.GradientAngle,
            BackgroundImagePath = string.IsNullOrEmpty(preset.BackgroundImagePath)
                ? null
                : preset.BackgroundImagePath,
            Padding = preset.Padding,
            CornerRadius = preset.CornerRadius,
            ShadowEnabled = preset.ShadowEnabled,
            ShadowBlur = preset.ShadowBlur,
            ShadowOpacity = preset.ShadowOpacity,
            ShadowColor = preset.ShadowColor,
            BorderEnabled = preset.BorderEnabled,
            BorderWidth = preset.BorderWidth,
            BorderColor = preset.BorderColor
        };
    }

    /// <summary>
    /// Converts a <see cref="BackgroundStyle"/> back to a <see cref="BrandPreset"/>.
    /// </summary>
    public static BrandPreset FromBackgroundStyle(BackgroundStyle style, string name)
    {
        return new BrandPreset
        {
            Name = name,
            BackgroundType = style.Type,
            BackgroundColor = style.Color,
            GradientEndColor = style.GradientEndColor,
            GradientAngle = style.GradientAngle,
            BackgroundImagePath = style.BackgroundImagePath ?? string.Empty,
            Padding = style.Padding,
            CornerRadius = style.CornerRadius,
            ShadowEnabled = style.ShadowEnabled,
            ShadowBlur = style.ShadowBlur,
            ShadowOpacity = style.ShadowOpacity,
            ShadowColor = style.ShadowColor,
            BorderEnabled = style.BorderEnabled,
            BorderWidth = style.BorderWidth,
            BorderColor = style.BorderColor
        };
    }
}
