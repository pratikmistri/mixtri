namespace Musio.Core.Timeline;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A named, one-click text overlay style. <see cref="Apply"/> mutates an existing
/// overlay in place so a preset can be applied through the normal undo/redo
/// property-update operation rather than replacing the segment (which would lose
/// its id, timing and track position).
/// </summary>
public sealed record TextOverlayPreset(string Name, string Glyph, Action<TextOverlaySegment> Apply);

/// <summary>
/// The curated set of built-in text overlay styles offered in the properties pane.
/// Each preset is authored as if for a 1080p frame — <see cref="TextOverlayRenderer"/>
/// scales every pixel-valued property (font size, corner radius, outline width, accent
/// thickness, blur amount) by <c>height / 1080</c> before drawing, so the look holds up
/// proportionally at any output resolution.
///
/// Every preset's <see cref="TextOverlayPreset.Apply"/> sets <b>all</b> style properties —
/// animation, placement, typography, background mode, and every background parameter
/// field, even the ones the chosen background mode ignores — never leaving any of them
/// at the overlay's prior value. Presets are applied to overlays that may already carry
/// style from a previous preset (or from manual tweaking), and switching presets is
/// expected to fully replace the look in one click: if a field were left untouched,
/// switching e.g. from Callout Pill to Frosted Card would leave Callout Pill's accent
/// colour lingering on an overlay that visually has nothing to do with an accent bar.
/// Only <see cref="TimelineSegment.Id"/>, <see cref="TimelineSegment.Start"/>,
/// <see cref="TimelineSegment.Duration"/>, <see cref="TextOverlaySegment.Enabled"/>,
/// <see cref="TextOverlaySegment.Text"/>, <see cref="TextOverlaySegment.SourceVideoFilePath"/>,
/// <see cref="TextOverlaySegment.X"/> and <see cref="TextOverlaySegment.Y"/> are left
/// alone — those are the user's content/timing, not style. (<see cref="TextOverlaySegment.Anchor"/>
/// IS style and is set by every preset below; the properties-pane UI is responsible for
/// resetting X/Y once a new anchor is applied.)
/// </summary>
public static class TextOverlayPresets
{
    /// <summary>The broadcast staple: bottom-left text over an upward gradient scrim.</summary>
    public static TextOverlayPreset LowerThird { get; } = new(
        "Lower Third",
        "\uE8E4", // AlignLeft
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.SlideUp;
            overlay.Anchor = TextOverlayAnchor.BottomLeft;
            overlay.WidthFraction = 0.55;
            overlay.MarginFraction = 0.06;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 40;
            overlay.IsBold = true;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Left;

            overlay.Background = TextOverlayBackground.GradientScrim;
            overlay.BackgroundColor = "#000000";
            overlay.BackgroundOpacity = 0.55;
            overlay.CornerRadius = 0;
            overlay.PaddingScale = 0.4;
            overlay.BlurAmount = 12;
            overlay.BlurTintOpacity = 0.25;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.85;
            overlay.OutlineWidth = 2;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.6;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>Subtitle style: a centred dark box sitting just above the bottom edge.</summary>
    public static TextOverlayPreset CaptionBar { get; } = new(
        "Caption Bar",
        "\uE7F0", // CC
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.FadeIn;
            overlay.Anchor = TextOverlayAnchor.BottomCenter;
            overlay.WidthFraction = 0.7;
            overlay.MarginFraction = 0.05;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 30;
            overlay.IsBold = false;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Center;

            overlay.Background = TextOverlayBackground.Solid;
            overlay.BackgroundColor = "#000000";
            overlay.BackgroundOpacity = 0.65;
            overlay.CornerRadius = 10;
            overlay.PaddingScale = 0.45;
            overlay.BlurAmount = 12;
            overlay.BlurTintOpacity = 0.25;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.7;
            overlay.OutlineWidth = 2;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.6;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>A compact accent-coloured pill badge that pops into a top corner.</summary>
    public static TextOverlayPreset CalloutPill { get; } = new(
        "Callout Pill",
        "\uE8EC", // Tag
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.ScalePop;
            overlay.Anchor = TextOverlayAnchor.TopRight;
            overlay.WidthFraction = 0.26;
            overlay.MarginFraction = 0.05;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 32;
            overlay.IsBold = true;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Center;

            overlay.Background = TextOverlayBackground.Solid;
            overlay.BackgroundColor = "#0078D4";
            overlay.BackgroundOpacity = 1.0;
            // A radius well beyond half the box's own height/width guarantees a true
            // pill (fully-rounded ends) regardless of how long the text runs; Win2D
            // clamps CanvasGeometry.CreateRoundedRectangle's radius to fit the box.
            overlay.CornerRadius = 80;
            overlay.PaddingScale = 0.55;
            overlay.BlurAmount = 12;
            overlay.BlurTintOpacity = 0.25;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.7;
            overlay.OutlineWidth = 2;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.6;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>A cinematic centred title with an outline/shadow — no box, so the footage stays visible.</summary>
    public static TextOverlayPreset BigTitle { get; } = new(
        "Big Title",
        "\uE8E8", // FontIncrease
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.ZoomBlurIn;
            overlay.Anchor = TextOverlayAnchor.MiddleCenter;
            overlay.WidthFraction = 0.82;
            overlay.MarginFraction = 0.06;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 88;
            overlay.IsBold = true;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Center;

            overlay.Background = TextOverlayBackground.OutlineShadow;
            overlay.BackgroundColor = "#000000";
            overlay.BackgroundOpacity = 0.55;
            overlay.CornerRadius = 0;
            overlay.PaddingScale = 0.35;
            overlay.BlurAmount = 12;
            overlay.BlurTintOpacity = 0.25;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.7;
            overlay.OutlineWidth = 3;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.75;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>Modern frosted glass: a blurred, lightly-tinted card behind the text.</summary>
    public static TextOverlayPreset FrostedCard { get; } = new(
        "Frosted Card",
        "\uE794", // Effects
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.FadeIn;
            overlay.Anchor = TextOverlayAnchor.BottomCenter;
            overlay.WidthFraction = 0.5;
            overlay.MarginFraction = 0.08;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 36;
            overlay.IsBold = false;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Center;

            overlay.Background = TextOverlayBackground.Blur;
            overlay.BackgroundColor = "#FFFFFF";
            overlay.BackgroundOpacity = 0.55;
            overlay.CornerRadius = 28;
            overlay.PaddingScale = 0.5;
            overlay.BlurAmount = 18;
            overlay.BlurTintOpacity = 0.28;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.7;
            overlay.OutlineWidth = 2;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.6;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>Text only, maximum footage visibility: a thin outline and soft shadow, no box.</summary>
    public static TextOverlayPreset MinimalOutline { get; } = new(
        "Minimal Outline",
        "\uE736", // ReadingMode (distraction-free, text-only)
        overlay =>
        {
            overlay.Animation = TextSlideAnimation.TrackingIn;
            overlay.Anchor = TextOverlayAnchor.MiddleCenter;
            overlay.WidthFraction = 0.7;
            overlay.MarginFraction = 0.06;

            overlay.FontFamily = "Segoe UI";
            overlay.FontSize = 42;
            overlay.IsBold = false;
            overlay.IsItalic = false;
            overlay.TextColor = "#FFFFFF";
            overlay.TextAlignment = SlideTextAlignment.Center;

            overlay.Background = TextOverlayBackground.OutlineShadow;
            overlay.BackgroundColor = "#000000";
            overlay.BackgroundOpacity = 0.55;
            overlay.CornerRadius = 0;
            overlay.PaddingScale = 0.35;
            overlay.BlurAmount = 12;
            overlay.BlurTintOpacity = 0.25;
            overlay.ScrimDirection = ScrimDirection.Bottom;
            overlay.ScrimStrength = 0.7;
            overlay.OutlineWidth = 1.5;
            overlay.OutlineColor = "#000000";
            overlay.ShadowStrength = 0.35;
            overlay.AccentColor = "#0078D4";
            overlay.AccentThickness = 5;
            overlay.AccentSide = AccentSide.Left;
        });

    /// <summary>All built-in presets, in display order.</summary>
    public static IReadOnlyList<TextOverlayPreset> All { get; } = new[]
    {
        LowerThird,
        CaptionBar,
        CalloutPill,
        BigTitle,
        FrostedCard,
        MinimalOutline,
    };

    /// <summary>Finds a preset by <see cref="TextOverlayPreset.Name"/>, or null.</summary>
    public static TextOverlayPreset? ByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
