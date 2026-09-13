using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace Mixtri_App.Controls;

internal enum TimelineTextStyle
{
    Plain11,
    SemiBold10,
    SemiBold11,
    SemiBold12,
    EmptyPlaceholder,
    ZoomHint,
    TransitionGlyph,
    SpeedBadge,
    AudioLabel,
    OverlayLabel,
    SlideLabel,
}

/// <summary>UI-thread-owned, device-independent formats shared by the timeline's draw passes.</summary>
internal sealed class TimelineDrawingResources : IDisposable
{
    private readonly Dictionary<TimelineTextStyle, CanvasTextFormat> _formats = new();
    private CanvasStrokeStyle? _dashedStroke;
    private CanvasStrokeStyle? _roundedStroke;
    private bool _disposed;

    internal int CreatedTextFormatCount { get; private set; }
    internal int CreatedStrokeStyleCount { get; private set; }
    internal int CachedTextFormatCount => _formats.Count;

    internal CanvasTextFormat GetTextFormat(TimelineTextStyle style)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_formats.TryGetValue(style, out var format)) return format;
        format = CreateTextFormat(style);
        _formats.Add(style, format);
        CreatedTextFormatCount++;
        return format;
    }

    internal CanvasTextFormat GetSlideLabel(float fontSize)
    {
        if (!float.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        var format = GetTextFormat(TimelineTextStyle.SlideLabel);
        // Animated band heights must not create a cache entry for every fractional size.
        if (format.FontSize != fontSize) format.FontSize = fontSize;
        return format;
    }

    internal CanvasStrokeStyle DashedStroke
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_dashedStroke is null)
            {
                _dashedStroke = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                CreatedStrokeStyleCount++;
            }
            return _dashedStroke;
        }
    }

    internal CanvasStrokeStyle RoundedStroke
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_roundedStroke is null)
            {
                _roundedStroke = new CanvasStrokeStyle
                {
                    StartCap = CanvasCapStyle.Round,
                    EndCap = CanvasCapStyle.Round,
                };
                CreatedStrokeStyleCount++;
            }
            return _roundedStroke;
        }
    }

    private static CanvasTextFormat CreateTextFormat(TimelineTextStyle style)
    {
        float fontSize = style switch
        {
            TimelineTextStyle.Plain11 or TimelineTextStyle.SemiBold11 or TimelineTextStyle.OverlayLabel => 11,
            TimelineTextStyle.SemiBold12 or TimelineTextStyle.EmptyPlaceholder => 12,
            TimelineTextStyle.SlideLabel => 15,
            TimelineTextStyle.SemiBold10 or TimelineTextStyle.ZoomHint or TimelineTextStyle.TransitionGlyph
                or TimelineTextStyle.SpeedBadge or TimelineTextStyle.AudioLabel => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
        var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = fontSize };
        if (style is not (TimelineTextStyle.Plain11 or TimelineTextStyle.EmptyPlaceholder or TimelineTextStyle.ZoomHint))
            format.FontWeight = new FontWeight { Weight = 600 };
        if (style == TimelineTextStyle.ZoomHint)
            format.FontStyle = FontStyle.Italic;
        if (style is TimelineTextStyle.EmptyPlaceholder or TimelineTextStyle.ZoomHint
            or TimelineTextStyle.TransitionGlyph or TimelineTextStyle.SlideLabel)
            format.HorizontalAlignment = CanvasHorizontalAlignment.Center;
        if (style is TimelineTextStyle.EmptyPlaceholder or TimelineTextStyle.ZoomHint
            or TimelineTextStyle.TransitionGlyph or TimelineTextStyle.SpeedBadge
            or TimelineTextStyle.AudioLabel or TimelineTextStyle.OverlayLabel or TimelineTextStyle.SlideLabel)
            format.VerticalAlignment = CanvasVerticalAlignment.Center;
        if (style is TimelineTextStyle.EmptyPlaceholder or TimelineTextStyle.SpeedBadge
            or TimelineTextStyle.AudioLabel or TimelineTextStyle.OverlayLabel or TimelineTextStyle.SlideLabel)
            format.WordWrapping = CanvasWordWrapping.NoWrap;
        if (style is TimelineTextStyle.AudioLabel or TimelineTextStyle.OverlayLabel or TimelineTextStyle.SlideLabel)
            format.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
        if (style is TimelineTextStyle.AudioLabel or TimelineTextStyle.OverlayLabel)
            format.TrimmingSign = CanvasTrimmingSign.Ellipsis;
        return format;
    }

    internal void Clear()
    {
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
        _dashedStroke?.Dispose();
        _dashedStroke = null;
        _roundedStroke?.Dispose();
        _roundedStroke = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }
}
