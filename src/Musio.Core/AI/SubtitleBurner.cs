using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace Musio.Core.AI;

/// <summary>
/// Vertical position of the subtitle text overlay.
/// </summary>
public enum SubtitlePosition
{
    Top,
    Center,
    Bottom
}

/// <summary>
/// Visual styling for burned-in subtitles.
/// </summary>
public record SubtitleStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public float FontSize { get; init; } = 24f;
    public string TextColor { get; init; } = "#FFFFFF";
    public string BackgroundColor { get; init; } = "#80000000";
    public SubtitlePosition Position { get; init; } = SubtitlePosition.Bottom;
    public float PaddingHorizontal { get; init; } = 16f;
    public float PaddingVertical { get; init; } = 8f;
    public float MarginBottom { get; init; } = 40f;
}

/// <summary>
/// Renders subtitle text onto Win2D <see cref="CanvasDrawingSession"/> frames
/// during video composition.
/// </summary>
public class SubtitleBurner
{
    private readonly List<SubtitleSegment> _segments;
    private readonly SubtitleStyle _style;
    private readonly Color _textColor;
    private readonly Color _bgColor;

    public SubtitleBurner(List<SubtitleSegment> segments, SubtitleStyle style)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(style);

        _segments = segments;
        _style = style;
        _textColor = ParseColor(style.TextColor);
        _bgColor = ParseColor(style.BackgroundColor);
    }

    /// <summary>
    /// Renders the active subtitle for <paramref name="timeSeconds"/> onto
    /// the given <see cref="CanvasDrawingSession"/>.
    /// </summary>
    public void RenderSubtitle(
        CanvasDrawingSession session,
        double timeSeconds,
        int canvasWidth,
        int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(session);

        var current = FindActiveSegment(timeSeconds);
        if (current is null)
            return;

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = _style.FontFamily,
            FontSize = _style.FontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap
        };

        // Measure the text to size the background rectangle
        var maxTextWidth = canvasWidth - (_style.PaddingHorizontal * 4);
        using var textLayout = new CanvasTextLayout(
            session, current.Text, textFormat, maxTextWidth, canvasHeight);

        var textWidth = (float)textLayout.LayoutBounds.Width;
        var textHeight = (float)textLayout.LayoutBounds.Height;

        var bgWidth = textWidth + (_style.PaddingHorizontal * 2);
        var bgHeight = textHeight + (_style.PaddingVertical * 2);
        var bgX = (canvasWidth - bgWidth) / 2f;
        var bgY = CalculateY(canvasHeight, bgHeight);

        // Draw semi-transparent background pill
        session.FillRoundedRectangle(bgX, bgY, bgWidth, bgHeight, 8f, 8f, _bgColor);

        // Draw text centered in the pill
        var textX = bgX + _style.PaddingHorizontal;
        var textY = bgY + _style.PaddingVertical;
        session.DrawTextLayout(textLayout, new Vector2(textX, textY), _textColor);
    }

    private SubtitleSegment? FindActiveSegment(double timeSeconds)
    {
        var ts = TimeSpan.FromSeconds(timeSeconds);
        return _segments.FirstOrDefault(s => ts >= s.Start && ts < s.End);
    }

    private float CalculateY(int canvasHeight, float bgHeight) => _style.Position switch
    {
        SubtitlePosition.Top => _style.MarginBottom,
        SubtitlePosition.Center => (canvasHeight - bgHeight) / 2f,
        SubtitlePosition.Bottom => canvasHeight - bgHeight - _style.MarginBottom,
        _ => canvasHeight - bgHeight - _style.MarginBottom
    };

    /// <summary>
    /// Parses a hex color string (#RRGGBB or #AARRGGBB) to a <see cref="Color"/>.
    /// </summary>
    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');

        return hex.Length switch
        {
            6 => Color.FromArgb(
                0xFF,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
        };
    }
}
