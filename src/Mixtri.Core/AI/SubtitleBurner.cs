using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace Mixtri.Core.AI;

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
/// <remarks>
/// Called once per composed frame, so the active segment is found by binary search and
/// its text format/layout are built once per segment (not per frame) and reused until the
/// segment, canvas size or device changes.
/// </remarks>
public class SubtitleBurner : IDisposable
{
    private readonly SubtitleSegment[] _segments;
    // _maxEnd[i] = latest End among _segments[0..i]; bounds the backwards scan for
    // overlapping segments.
    private readonly TimeSpan[] _maxEnd;
    private readonly SubtitleStyle _style;
    private readonly Color _textColor;
    private readonly Color _bgColor;
    private readonly CanvasTextFormat _textFormat;

    private SubtitleSegment? _cachedSegment;
    private int _cachedWidth;
    private int _cachedHeight;
    private CanvasDevice? _cachedDevice;
    private CanvasTextLayout? _cachedLayout;
    private bool _disposed;

    public SubtitleBurner(List<SubtitleSegment> segments, SubtitleStyle style)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(style);

        _segments = segments.OrderBy(s => s.Start).ToArray();
        _maxEnd = new TimeSpan[_segments.Length];
        var maxEnd = TimeSpan.MinValue;
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].End > maxEnd) maxEnd = _segments[i].End;
            _maxEnd[i] = maxEnd;
        }

        _style = style;
        _textColor = ParseColor(style.TextColor);
        _bgColor = ParseColor(style.BackgroundColor);
        _textFormat = new CanvasTextFormat
        {
            FontFamily = _style.FontFamily,
            FontSize = _style.FontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap
        };
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var current = FindActiveSegment(timeSeconds);
        if (current is null)
            return;

        var textLayout = GetLayout(session, current, canvasWidth, canvasHeight);

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

    private CanvasTextLayout GetLayout(
        CanvasDrawingSession session, SubtitleSegment segment, int canvasWidth, int canvasHeight)
    {
        var device = session.Device;
        if (_cachedLayout is not null
            && ReferenceEquals(_cachedSegment, segment)
            && _cachedWidth == canvasWidth
            && _cachedHeight == canvasHeight
            && ReferenceEquals(_cachedDevice, device))
        {
            return _cachedLayout;
        }

        // Measure the text to size the background rectangle
        var maxTextWidth = canvasWidth - (_style.PaddingHorizontal * 4);
        var layout = new CanvasTextLayout(device, segment.Text, _textFormat, maxTextWidth, canvasHeight);
        _cachedLayout?.Dispose();
        _cachedLayout = layout;
        _cachedSegment = segment;
        _cachedWidth = canvasWidth;
        _cachedHeight = canvasHeight;
        _cachedDevice = device;
        return layout;
    }

    private SubtitleSegment? FindActiveSegment(double timeSeconds)
    {
        var ts = TimeSpan.FromSeconds(timeSeconds);

        // Consecutive frames almost always stay on the same segment.
        if (_cachedSegment is { } cached && ts >= cached.Start && ts < cached.End)
            return cached;

        // Last segment starting at or before ts.
        int lo = 0, hi = _segments.Length - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_segments[mid].Start <= ts)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Walk back only while an earlier segment could still cover ts (overlaps).
        for (int i = candidate; i >= 0 && _maxEnd[i] > ts; i--)
        {
            if (ts < _segments[i].End)
                return _segments[i];
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cachedLayout?.Dispose();
        _cachedLayout = null;
        _cachedDevice = null;
        _textFormat.Dispose();
        GC.SuppressFinalize(this);
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

        try
        {
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
        catch
        {
            return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        }
    }
}
