using Mixtri.Core.Models;

namespace Mixtri.Core.Settings;

/// <summary>
/// Defines background and styling settings for a recording's visual appearance.
/// </summary>
public class BrandPreset
{
    public string Name { get; set; } = string.Empty;
    public BackgroundType BackgroundType { get; set; } = BackgroundType.SolidColor;
    public string BackgroundColor { get; set; } = "#1E1E2E";
    public string GradientEndColor { get; set; } = "#302D41";
    public double GradientAngle { get; set; } = 135.0;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public int Padding { get; set; } = 64;
    public int CornerRadius { get; set; } = 12;
    public bool ShadowEnabled { get; set; } = true;
    public int ShadowBlur { get; set; } = 40;
    public double ShadowOpacity { get; set; } = 0.5;
    public string ShadowColor { get; set; } = "#000000";
    public bool BorderEnabled { get; set; }
    public int BorderWidth { get; set; } = 1;
    public string BorderColor { get; set; } = "#FFFFFF";
}
