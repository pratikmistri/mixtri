using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Models;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Musio_App.Controls;

public sealed partial class TimelineControl : UserControl
{
    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(nameof(Model), typeof(TimelineModel), typeof(TimelineControl),
            new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty PlayheadPositionProperty =
        DependencyProperty.Register(nameof(PlayheadPosition), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnPlayheadPositionChanged));

    public TimelineModel? Model
    {
        get => (TimelineModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public TimeSpan PlayheadPosition
    {
        get => (TimeSpan)GetValue(PlayheadPositionProperty);
        set => SetValue(PlayheadPositionProperty, value);
    }

    private enum DragMode { None, Playhead, TrimStart, TrimEnd, ZoomSegmentBody, ZoomSegmentLeftEdge, ZoomSegmentRightEdge, ZoomSegmentCreate }
    private DragMode _dragMode = DragMode.None;

    // Video clip selection
    private int? _selectedClipIndex;

    /// <summary>Raised when a video clip is selected or deselected (null = deselected).</summary>
    public event EventHandler<int?>? VideoClipSelected;

    /// <summary>The index of the currently selected video clip, or null.</summary>
    public int? SelectedClipIndex
    {
        get => _selectedClipIndex;
        set
        {
            if (_selectedClipIndex == value) return;
            _selectedClipIndex = value;
            VideoTrackCanvas?.Invalidate();
        }
    }

    /// <summary>Clears the video clip selection.</summary>
    public void ClearClipSelection()
    {
        if (_selectedClipIndex is not null)
        {
            SelectedClipIndex = null;
            VideoClipSelected?.Invoke(this, null);
        }
    }

    // Zoom segment selection & drag state
    private string? _selectedZoomKeyframeId;
    private double _zoomDragStartX = double.NaN;
    private double _zoomDragCurrentX = double.NaN;
    private TimeSpan _zoomDragOriginalTimestamp;
    private TimeSpan _zoomDragOriginalStart;
    private TimeSpan _zoomDragOriginalEnd;

    // Drag-to-create state
    private TimeSpan _zoomCreateStart;
    private TimeSpan _zoomCreateEnd;
    private bool _zoomCreateActive;
    private const double ZoomCreateDragThreshold = 5.0; // pixels before creating

    // Colors — resolved from theme resources (see Themes/AppColors.xaml)
    private Color RulerBackground;
    private Color RulerTickColor;
    private Color RulerTextColor;
    private Color VideoTrackBackground;
    private Color VideoClipColor;
    private Color VideoClipSelectedColor;
    private Color VideoClipSelectedBorder;
    private Color SpeedUpOverlayColor;
    private Color SlowDownOverlayColor;
    private Color TrimHandleColor;
    private Color TrimHandleBorderColor;
    private Color ZoomTrackBackground;
    private Color ZoomSegmentFill;
    private Color ZoomSegmentAutoFill;
    private Color ZoomSegmentSelectedFill;
    private Color ZoomSegmentBorder;
    private Color ZoomSegmentSelectedBorder;
    private Color ZoomSegmentHandleColor;
    private Color ZoomSegmentCreatePreview;
    private Color ZoomSegmentTextColor;
    private Color AudioTrackBackground;
    private Color AudioPlaceholderColor;
    private Color AudioWaveformColor;
    private Color AudioEnvelopeColor;
    private Color MicWaveformColor;
    private Color MicEnvelopeColor;
    private Color PlayheadColor;
    private Color CutLineColor;
    private Color CursorTrackBackground;
    private Color CursorPathXColor;
    private Color CursorPathYColor;
    private Color CursorClickColor;
    private Color SpeedLabelTextColor;
    private Color TrackCenterLineColor;
    private Color TrackEmptyLineColor;

    private const double TrimHandleWidth = 8;

    public TimelineControl()
    {
        InitializeComponent();
        ResolveThemeColors();
        ActualThemeChanged += (_, _) => { ResolveThemeColors(); InvalidateAllCanvases(); };
    }

    private Color GetBrushColor(string key, Color fallback)
    {
        try
        {
            if (Resources.TryGetValue(key, out var val) && val is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                return brush.Color;
        }
        catch { /* resource not found — use fallback */ }
        return fallback;
    }

    private void ResolveThemeColors()
    {
        RulerBackground          = GetBrushColor("TimelineRulerBackgroundBrush", Color.FromArgb(255, 40, 40, 40));
        RulerTickColor           = GetBrushColor("TimelineRulerTickBrush", Color.FromArgb(255, 160, 160, 160));
        RulerTextColor           = GetBrushColor("TimelineTrackLabelForegroundBrush", Color.FromArgb(255, 200, 200, 200));
        VideoTrackBackground     = GetBrushColor("TimelineTrackPrimaryBackgroundBrush", Color.FromArgb(255, 30, 30, 30));
        VideoClipColor           = GetBrushColor("TimelineVideoClipBrush", Color.FromArgb(255, 60, 120, 200));
        VideoClipSelectedColor   = GetBrushColor("TimelineVideoClipSelectedBrush", Color.FromArgb(255, 90, 155, 235));
        VideoClipSelectedBorder  = GetBrushColor("TimelineVideoClipSelectedBorderBrush", Color.FromArgb(255, 180, 210, 255));
        SpeedUpOverlayColor      = GetBrushColor("TimelineSpeedUpOverlayBrush", Color.FromArgb(200, 230, 160, 50));
        SlowDownOverlayColor     = GetBrushColor("TimelineSlowDownOverlayBrush", Color.FromArgb(200, 60, 130, 230));
        TrimHandleColor          = GetBrushColor("TimelineTrimHandleBrush", Color.FromArgb(255, 255, 255, 255));
        TrimHandleBorderColor    = GetBrushColor("TimelineTrimHandleBorderBrush", Color.FromArgb(255, 100, 100, 100));
        ZoomTrackBackground      = GetBrushColor("TimelineTrackSecondaryBackgroundBrush", Color.FromArgb(255, 35, 35, 35));
        ZoomSegmentFill          = GetBrushColor("TimelineZoomSegmentBrush", Color.FromArgb(200, 60, 160, 80));
        ZoomSegmentAutoFill      = GetBrushColor("TimelineZoomSegmentAutoBrush", Color.FromArgb(120, 60, 140, 70));
        ZoomSegmentSelectedFill  = GetBrushColor("TimelineZoomSegmentSelectedBrush", Color.FromArgb(230, 80, 200, 100));
        ZoomSegmentBorder        = GetBrushColor("TimelineZoomSegmentBorderBrush", Color.FromArgb(255, 100, 200, 100));
        ZoomSegmentSelectedBorder = GetBrushColor("TimelineZoomSegmentSelectedBorderBrush", Color.FromArgb(255, 180, 255, 180));
        ZoomSegmentHandleColor   = GetBrushColor("TimelineZoomSegmentHandleBrush", Color.FromArgb(255, 220, 255, 220));
        ZoomSegmentCreatePreview = GetBrushColor("TimelineZoomSegmentCreatePreviewBrush", Color.FromArgb(100, 100, 200, 100));
        ZoomSegmentTextColor     = GetBrushColor("TimelineZoomSegmentTextBrush", Color.FromArgb(255, 240, 255, 240));
        AudioTrackBackground     = GetBrushColor("TimelineTrackSecondaryBackgroundBrush", Color.FromArgb(255, 35, 35, 35));
        AudioPlaceholderColor    = GetBrushColor("TimelineAudioPlaceholderBrush", Color.FromArgb(255, 80, 160, 80));
        AudioWaveformColor       = GetBrushColor("TimelineAudioWaveformBrush", Color.FromArgb(220, 80, 180, 80));
        AudioEnvelopeColor       = GetBrushColor("TimelineAudioEnvelopeBrush", Color.FromArgb(100, 120, 220, 120));
        MicWaveformColor         = Color.FromArgb(220, 180, 120, 220);
        MicEnvelopeColor         = Color.FromArgb(100, 200, 140, 255);
        PlayheadColor            = GetBrushColor("TimelinePlayheadBrush", Color.FromArgb(255, 255, 50, 50));
        CutLineColor             = GetBrushColor("TimelineCutLineBrush", Color.FromArgb(200, 255, 255, 100));
        CursorTrackBackground    = GetBrushColor("TimelineTrackSecondaryBackgroundBrush", Color.FromArgb(255, 35, 35, 35));
        CursorPathXColor         = GetBrushColor("TimelineCursorPathXBrush", Color.FromArgb(220, 100, 180, 255));
        CursorPathYColor         = GetBrushColor("TimelineCursorPathYBrush", Color.FromArgb(220, 255, 160, 100));
        CursorClickColor         = GetBrushColor("TimelineCursorClickBrush", Color.FromArgb(255, 255, 80, 80));
        SpeedLabelTextColor      = GetBrushColor("TimelineSpeedLabelTextBrush", Color.FromArgb(255, 255, 255, 255));
        TrackCenterLineColor     = GetBrushColor("TimelineTrackCenterLineBrush", Color.FromArgb(100, 255, 255, 255));
        TrackEmptyLineColor      = GetBrushColor("TimelineTrackEmptyLineBrush", Color.FromArgb(60, 255, 255, 255));
    }

    private void InvalidateAllCanvases()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
    }

    /// <summary>Raised when a zoom segment is selected or deselected (null = deselected).</summary>
    public event EventHandler<string?>? ZoomSegmentSelected;

    /// <summary>Raised when a zoom segment drag completes. Carries the keyframe Id and new timestamp.</summary>
    public event EventHandler<(string Id, TimeSpan NewTimestamp)>? ZoomSegmentMoved;

    /// <summary>Raised when a zoom segment is resized. Carries the keyframe Id, whether it was the start edge, and the new edge time.</summary>
    public event EventHandler<(string Id, bool IsStartEdge, TimeSpan NewEdgeTime)>? ZoomSegmentResized;

    /// <summary>Raised when a new zoom segment is created by dragging. Carries the start and end times.</summary>
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? ZoomSegmentCreated;

    /// <summary>Raised when the user requests removal of a zoom segment.</summary>
    public event EventHandler<string>? ZoomSegmentRemoveRequested;

    /// <summary>The Id of the currently selected zoom segment, or null.</summary>
    public string? SelectedZoomKeyframeId
    {
        get => _selectedZoomKeyframeId;
        set
        {
            if (_selectedZoomKeyframeId == value) return;
            _selectedZoomKeyframeId = value;
            ZoomTrackCanvas?.Invalidate();
        }
    }

    public void Refresh() => InvalidateAll();

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl control)
            control.InvalidateAll();
    }

    private static void OnPlayheadPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl control)
        {
            if (control.Model is { } model)
                model.PlayheadPosition = (TimeSpan)e.NewValue;
            control.UpdatePlayheadVisual();
            control.InvalidateAll();
        }
    }

    // --- Coordinate helpers ---

    private double TimeToX(TimeSpan time)
    {
        var model = Model;
        if (model is null || model.Duration.TotalSeconds <= 0)
            return 0;

        double totalSeconds = model.Duration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = (canvasWidth / totalSeconds) * model.ZoomLevel;
        return (time.TotalSeconds - model.ScrollOffset) * pixelsPerSecond;
    }

    private TimeSpan XToTime(double x)
    {
        var model = Model;
        if (model is null || model.Duration.TotalSeconds <= 0)
            return TimeSpan.Zero;

        double totalSeconds = model.Duration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = (canvasWidth / totalSeconds) * model.ZoomLevel;
        if (pixelsPerSecond <= 0) return TimeSpan.Zero;

        double seconds = (x / pixelsPerSecond) + model.ScrollOffset;
        seconds = Math.Clamp(seconds, 0, totalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void InvalidateAll()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
        UpdatePlayheadVisual();
    }

    private void UpdatePlayheadVisual()
    {
        if (PlayheadLine is null) return;
        double x = TimeToX(PlayheadPosition);
        PlayheadLine.Margin = new Thickness(x, 0, 0, 0);
    }

    // --- Time Ruler ---

    private void TimeRulerCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(RulerBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        double totalSeconds = model.Duration.TotalSeconds;
        double pixelsPerSecond = (w / totalSeconds) * model.ZoomLevel;

        // Choose tick interval based on zoom
        double tickInterval = ChooseTickInterval(pixelsPerSecond);
        double minorInterval = tickInterval / 5;

        double startSec = Math.Max(0, model.ScrollOffset - tickInterval);
        double endSec = Math.Min(totalSeconds, model.ScrollOffset + w / pixelsPerSecond + tickInterval);

        // Minor ticks
        for (double t = Math.Floor(startSec / minorInterval) * minorInterval; t <= endSec; t += minorInterval)
        {
            float x = (float)((t - model.ScrollOffset) * pixelsPerSecond);
            if (x < 0 || x > w) continue;
            ds.DrawLine(x, h * 0.7f, x, h, RulerTickColor);
        }

        // Major ticks + labels
        for (double t = Math.Floor(startSec / tickInterval) * tickInterval; t <= endSec; t += tickInterval)
        {
            float x = (float)((t - model.ScrollOffset) * pixelsPerSecond);
            if (x < -50 || x > w + 50) continue;
            ds.DrawLine(x, h * 0.4f, x, h, RulerTickColor);

            string label = FormatTime(TimeSpan.FromSeconds(t));
            ds.DrawText(label, x + 3, 1, RulerTextColor,
                new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 11,
                    FontFamily = "Segoe UI"
                });
        }
    }

    private static double ChooseTickInterval(double pixelsPerSecond)
    {
        double[] intervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60];
        foreach (double interval in intervals)
        {
            if (interval * pixelsPerSecond >= 60)
                return interval;
        }
        return 60;
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return $"0:{t.Seconds:D2}.{t.Milliseconds / 100}";
    }

    // --- Video Track ---

    private void VideoTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(VideoTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        // Draw clips
        for (int idx = 0; idx < model.Clips.Count; idx++)
        {
            var clip = model.Clips[idx];
            float x1 = (float)TimeToX(clip.Start);
            float x2 = (float)TimeToX(clip.End);
            if (x2 < 0 || x1 > w) continue;

            bool isSelected = idx == _selectedClipIndex;
            var clipColor = isSelected ? VideoClipSelectedColor : VideoClipColor;
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, clipColor);

            if (isSelected)
                ds.DrawRectangle(x1, 4, x2 - x1, h - 8, VideoClipSelectedBorder, 2f);

            // Speed indicator for clips with non-default SpeedFactor
            if (Math.Abs(clip.SpeedFactor - 1.0) > 0.001)
            {
                var segColor = clip.SpeedFactor > 1.0 ? SpeedUpOverlayColor : SlowDownOverlayColor;
                ds.FillRectangle(x1, 4, x2 - x1, h - 8, segColor);

                string speedLabel = $"{clip.SpeedFactor:0.##}x";
                ds.DrawText(speedLabel, x1 + 4, h / 2 - 7, SpeedLabelTextColor,
                    new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                    {
                        FontSize = 12,
                        FontFamily = "Segoe UI",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
            }
        }

        // If no clips, draw the full duration as one clip
        if (model.Clips.Count == 0)
        {
            float x1 = (float)TimeToX(model.TrimStart);
            float x2 = (float)TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, VideoClipColor);
        }

        // Speed segments overlay (orange = sped up, blue = slowed down)
        foreach (var seg in model.SpeedSegments)
        {
            float x1 = (float)TimeToX(seg.Start);
            float x2 = (float)TimeToX(seg.End);
            if (x2 < 0 || x1 > w) continue;

            var segColor = seg.Speed > 1.0 ? SpeedUpOverlayColor : SlowDownOverlayColor;
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, segColor);

            string speedLabel = $"{seg.Speed:0.##}x";
            ds.DrawText(speedLabel, x1 + 4, h / 2 - 7, SpeedLabelTextColor,
                new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 12,
                    FontFamily = "Segoe UI",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
        }

        // Trim handles
        DrawTrimHandle(ds, model.TrimStart, h);
        DrawTrimHandle(ds, model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration, h);

        // Cut points (gaps between clips)
        for (int i = 1; i < model.Clips.Count; i++)
        {
            float x = (float)TimeToX(model.Clips[i].Start);
            ds.DrawLine(x, 0, x, h, CutLineColor, 1.5f);
        }

        // Playhead on this track
        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    private void DrawTrimHandle(CanvasDrawingSession ds, TimeSpan time, float trackHeight)
    {
        float x = (float)TimeToX(time);
        ds.FillRectangle(x - (float)TrimHandleWidth / 2, 0, (float)TrimHandleWidth, trackHeight, TrimHandleColor);
        ds.DrawRectangle(x - (float)TrimHandleWidth / 2, 0, (float)TrimHandleWidth, trackHeight,
            TrimHandleBorderColor, 1);
    }

    // --- Zoom Track ---

    // --- Zoom Track (segment-based rendering) ---

    private const double ZoomSegmentEdgeHitWidth = 8;
    private const float ZoomSegmentCornerRadius = 4;
    private const float ZoomSegmentVerticalPadding = 6;

    private void ZoomTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(ZoomTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        // Draw zoom segments as rounded rectangles
        var sorted = model.ZoomKeyframes.OrderBy(k => k.Timestamp).ToList();
        foreach (var kf in sorted)
        {
            float x1 = (float)GetZoomSegmentStartX(kf);
            float x2 = (float)GetZoomSegmentEndX(kf);
            if (x2 < 0 || x1 > w) continue;

            float segW = Math.Max(2, x2 - x1);
            float segY = ZoomSegmentVerticalPadding;
            float segH = h - ZoomSegmentVerticalPadding * 2;

            bool isSelected = kf.Id == _selectedZoomKeyframeId;
            bool isEditable = kf.IsManual;

            // Fill
            var fillColor = isSelected ? ZoomSegmentSelectedFill
                : isEditable ? ZoomSegmentFill
                : ZoomSegmentAutoFill;

            var roundedRect = CanvasGeometry.CreateRoundedRectangle(ds, x1, segY, segW, segH, ZoomSegmentCornerRadius, ZoomSegmentCornerRadius);
            ds.FillGeometry(roundedRect, fillColor);

            // Border
            var borderColor = isSelected ? ZoomSegmentSelectedBorder : ZoomSegmentBorder;
            float borderWidth = isSelected ? 1.5f : 1f;
            ds.DrawGeometry(roundedRect, borderColor, borderWidth);

            // Zoom level text
            if (segW > 30)
            {
                string label = $"{kf.ZoomLevel:0.#}x";
                ds.DrawText(label, x1 + 6, segY + segH / 2 - 7, ZoomSegmentTextColor,
                    new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                    {
                        FontSize = 11,
                        FontFamily = "Segoe UI",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
            }

            // Resize handles (visible when selected)
            if (isSelected)
            {
                float handleW = 3;
                float handleH = segH * 0.5f;
                float handleY = segY + (segH - handleH) / 2;

                // Left handle
                ds.FillRoundedRectangle(x1 + 1, handleY, handleW, handleH, 1, 1, ZoomSegmentHandleColor);
                // Right handle
                ds.FillRoundedRectangle(x2 - handleW - 1, handleY, handleW, handleH, 1, 1, ZoomSegmentHandleColor);
            }
        }

        // Draw create-preview if dragging to create
        if (_zoomCreateActive && _dragMode == DragMode.ZoomSegmentCreate)
        {
            float cx1 = (float)TimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateStart : _zoomCreateEnd);
            float cx2 = (float)TimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateEnd : _zoomCreateStart);
            float cw = Math.Max(2, cx2 - cx1);
            float cy = ZoomSegmentVerticalPadding;
            float ch = h - ZoomSegmentVerticalPadding * 2;

            var previewRect = CanvasGeometry.CreateRoundedRectangle(ds, cx1, cy, cw, ch, ZoomSegmentCornerRadius, ZoomSegmentCornerRadius);
            ds.FillGeometry(previewRect, ZoomSegmentCreatePreview);
            ds.DrawGeometry(previewRect, ZoomSegmentBorder, 1f,
                new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }

        // Playhead
        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    /// <summary>
    /// Returns the X position for a zoom segment's start edge, using the transient drag
    /// position if the segment is currently being dragged.
    /// </summary>
    private double GetZoomSegmentStartX(ZoomKeyframe kf)
    {
        if (kf.Id == _selectedZoomKeyframeId && !double.IsNaN(_zoomDragCurrentX))
        {
            if (_dragMode == DragMode.ZoomSegmentBody)
            {
                double deltaX = _zoomDragCurrentX - _zoomDragStartX;
                return TimeToX(_zoomDragOriginalStart) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentLeftEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return TimeToX(kf.Start);
    }

    /// <summary>
    /// Returns the X position for a zoom segment's end edge, using the transient drag
    /// position if the segment is currently being dragged.
    /// </summary>
    private double GetZoomSegmentEndX(ZoomKeyframe kf)
    {
        if (kf.Id == _selectedZoomKeyframeId && !double.IsNaN(_zoomDragCurrentX))
        {
            if (_dragMode == DragMode.ZoomSegmentBody)
            {
                double deltaX = _zoomDragCurrentX - _zoomDragStartX;
                return TimeToX(_zoomDragOriginalEnd) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentRightEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return TimeToX(kf.End);
    }

    // --- Zoom Track Interaction ---

    private enum ZoomHitTarget { None, Body, LeftEdge, RightEdge }

    /// <summary>
    /// Hit-test zoom segments at the given position.
    /// Only manual (user-editable) segments can be hit for drag/resize.
    /// Returns the Id and hit target.
    /// </summary>
    private (string? Id, ZoomHitTarget Target) HitTestZoomSegment(double posX, double posY)
    {
        var model = Model;
        if (model is null) return (null, ZoomHitTarget.None);

        float h = (float)ZoomTrackCanvas.ActualHeight;
        float segY = ZoomSegmentVerticalPadding;
        float segH = h - ZoomSegmentVerticalPadding * 2;

        // Check if Y is within segment vertical bounds
        if (posY < segY || posY > segY + segH)
            return (null, ZoomHitTarget.None);

        // Check segments in reverse order (last drawn = on top)
        var sorted = model.ZoomKeyframes.OrderBy(k => k.Timestamp).ToList();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var kf = sorted[i];
            float x1 = (float)TimeToX(kf.Start);
            float x2 = (float)TimeToX(kf.End);

            // Check edges first (only if selected)
            if (kf.Id == _selectedZoomKeyframeId)
            {
                if (Math.Abs(posX - x1) <= ZoomSegmentEdgeHitWidth)
                    return (kf.Id, ZoomHitTarget.LeftEdge);
                if (Math.Abs(posX - x2) <= ZoomSegmentEdgeHitWidth)
                    return (kf.Id, ZoomHitTarget.RightEdge);
            }

            // Check body
            if (posX >= x1 && posX <= x2)
                return (kf.Id, ZoomHitTarget.Body);
        }

        return (null, ZoomHitTarget.None);
    }

    private void ZoomTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;

        var (hitId, hitTarget) = HitTestZoomSegment(pos.X, pos.Y);

        if (hitId is not null)
        {
            // Select the segment
            SelectedZoomKeyframeId = hitId;
            ZoomSegmentSelected?.Invoke(this, hitId);

            var kf = Model?.ZoomKeyframes.FirstOrDefault(k => k.Id == hitId);
            if (kf is null) return;

            _zoomDragStartX = pos.X;
            _zoomDragCurrentX = pos.X;
            _zoomDragOriginalTimestamp = kf.Timestamp;
            _zoomDragOriginalStart = kf.Start;
            _zoomDragOriginalEnd = kf.End;

            switch (hitTarget)
            {
                case ZoomHitTarget.LeftEdge:
                    _dragMode = DragMode.ZoomSegmentLeftEdge;
                    SetCursor(InputSystemCursorShape.SizeWestEast);
                    break;
                case ZoomHitTarget.RightEdge:
                    _dragMode = DragMode.ZoomSegmentRightEdge;
                    SetCursor(InputSystemCursorShape.SizeWestEast);
                    break;
                case ZoomHitTarget.Body:
                    _dragMode = DragMode.ZoomSegmentBody;
                    SetCursor(InputSystemCursorShape.SizeAll);
                    break;
            }

            canvas.CapturePointer(e.Pointer);
        }
        else
        {
            // Deselect any selected segment
            if (_selectedZoomKeyframeId is not null)
            {
                SelectedZoomKeyframeId = null;
                ZoomSegmentSelected?.Invoke(this, null);
            }

            // Start potential drag-to-create or playhead scrub
            _zoomDragStartX = pos.X;
            _zoomDragCurrentX = pos.X;
            _zoomCreateActive = false;
            _zoomCreateStart = XToTime(pos.X);
            _dragMode = DragMode.ZoomSegmentCreate;
            PlayheadPosition = _zoomCreateStart;
            canvas.CapturePointer(e.Pointer);
        }
    }

    private void ZoomTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;

        switch (_dragMode)
        {
            case DragMode.ZoomSegmentBody:
                _zoomDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeAll);
                InvalidateAll();
                break;

            case DragMode.ZoomSegmentLeftEdge:
            case DragMode.ZoomSegmentRightEdge:
                _zoomDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateAll();
                break;

            case DragMode.ZoomSegmentCreate:
                double dragDistance = Math.Abs(pos.X - _zoomDragStartX);
                if (dragDistance >= ZoomCreateDragThreshold)
                {
                    _zoomCreateActive = true;
                    _zoomCreateEnd = XToTime(pos.X);
                    InvalidateAll();
                }
                else
                {
                    // Still within threshold — scrub playhead
                    PlayheadPosition = XToTime(pos.X);
                }
                break;

            case DragMode.None:
                // Hover cursor feedback
                var (hitId, hitTarget) = HitTestZoomSegment(pos.X, pos.Y);
                SetCursor(hitTarget switch
                {
                    ZoomHitTarget.LeftEdge or ZoomHitTarget.RightEdge => InputSystemCursorShape.SizeWestEast,
                    ZoomHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;

            default:
                if (_dragMode == DragMode.Playhead)
                    PlayheadPosition = XToTime(pos.X);
                break;
        }
    }

    private void ZoomTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;

        switch (_dragMode)
        {
            case DragMode.ZoomSegmentBody when _selectedZoomKeyframeId is not null:
            {
                double deltaX = _zoomDragCurrentX - _zoomDragStartX;
                var deltaTime = XToTime(_zoomDragStartX + deltaX) - XToTime(_zoomDragStartX);
                var newTimestamp = _zoomDragOriginalTimestamp + deltaTime;

                // Only fire move event if the segment actually moved
                if (Math.Abs(deltaX) > 1)
                {
                    ZoomSegmentMoved?.Invoke(this, (_selectedZoomKeyframeId, newTimestamp));
                }
                break;
            }

            case DragMode.ZoomSegmentLeftEdge when _selectedZoomKeyframeId is not null:
            {
                var newEdgeTime = XToTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth));
                if (newEdgeTime != _zoomDragOriginalStart)
                {
                    ZoomSegmentResized?.Invoke(this, (_selectedZoomKeyframeId, true, newEdgeTime));
                }
                break;
            }

            case DragMode.ZoomSegmentRightEdge when _selectedZoomKeyframeId is not null:
            {
                var newEdgeTime = XToTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth));
                if (newEdgeTime != _zoomDragOriginalEnd)
                {
                    ZoomSegmentResized?.Invoke(this, (_selectedZoomKeyframeId, false, newEdgeTime));
                }
                break;
            }

            case DragMode.ZoomSegmentCreate when _zoomCreateActive:
            {
                var start = _zoomCreateStart < _zoomCreateEnd ? _zoomCreateStart : _zoomCreateEnd;
                var end = _zoomCreateStart < _zoomCreateEnd ? _zoomCreateEnd : _zoomCreateStart;
                if ((end - start) >= ZoomKeyframe.MinSegmentDuration)
                {
                    ZoomSegmentCreated?.Invoke(this, (start, end));
                }
                _zoomCreateActive = false;
                break;
            }
        }

        _zoomDragStartX = double.NaN;
        _zoomDragCurrentX = double.NaN;
        _dragMode = DragMode.None;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        InvalidateAll();
    }

    private void ZoomTrack_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);

        var (hitId, _) = HitTestZoomSegment(pos.X, pos.Y);
        if (hitId is null) return;

        // Select the right-clicked segment
        SelectedZoomKeyframeId = hitId;
        ZoomSegmentSelected?.Invoke(this, hitId);

        // Show context menu
        var menu = new MenuFlyout();
        var removeItem = new MenuFlyoutItem
        {
            Text = "Remove Zoom Segment",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        removeItem.Click += (_, _) =>
        {
            ZoomSegmentRemoveRequested?.Invoke(this, hitId);
        };
        menu.Items.Add(removeItem);
        menu.ShowAt(canvas, pos);
    }

    /// <summary>Clears the selected zoom segment.</summary>
    public void ClearZoomSelection()
    {
        if (_selectedZoomKeyframeId is not null)
        {
            SelectedZoomKeyframeId = null;
            ZoomSegmentSelected?.Invoke(this, null);
        }
    }

    // --- Audio Track ---

    private void AudioTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, Model?.SystemAudioWaveformSamples, AudioWaveformColor, AudioEnvelopeColor);
    }

    private void MicTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, Model?.MicAudioWaveformSamples, MicWaveformColor, MicEnvelopeColor);
    }

    private void DrawWaveformTrack(CanvasControl sender, CanvasDrawEventArgs args,
        float[]? waveform, Color waveformColor, Color envelopeColor)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(AudioTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        float x1 = (float)TimeToX(model.TrimStart);
        float x2 = (float)TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
        float centerY = h / 2f;

        if (waveform is not null && waveform.Length > 0)
        {
            float trackWidth = x2 - x1;
            if (trackWidth <= 0) trackWidth = w;
            float barWidth = Math.Max(1f, trackWidth / waveform.Length);

            for (int i = 0; i < waveform.Length; i++)
            {
                float bx = x1 + (i * trackWidth / waveform.Length);
                if (bx > w || bx + barWidth < 0) continue;

                float amplitude = Math.Clamp(waveform[i], 0f, 1f);
                float barHeight = amplitude * (h * 0.45f);

                ds.FillRectangle(bx, centerY - barHeight, barWidth, barHeight * 2, waveformColor);
            }

            if (waveform.Length > 1)
            {
                var envBuilder = new CanvasPathBuilder(sender);
                float startX = x1;
                float startY = centerY - waveform[0] * (h * 0.45f);
                envBuilder.BeginFigure(startX, startY);

                for (int i = 1; i < waveform.Length; i++)
                {
                    float ex = x1 + (i * trackWidth / waveform.Length);
                    float ey = centerY - Math.Clamp(waveform[i], 0f, 1f) * (h * 0.45f);
                    envBuilder.AddLine(ex, ey);
                }

                envBuilder.EndFigure(CanvasFigureLoop.Open);
                var envGeometry = CanvasGeometry.CreatePath(envBuilder);
                ds.DrawGeometry(envGeometry, envelopeColor, 1.5f);
            }
        }
        else
        {
            ds.FillRectangle(x1, h * 0.3f, x2 - x1, h * 0.4f, AudioPlaceholderColor);
        }

        ds.DrawLine(x1, centerY, x2, centerY, TrackCenterLineColor, 0.5f);

        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    // --- Cursor Path Track ---

    private void CursorTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(CursorTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, TrackEmptyLineColor, 0.5f);
            float ppx = (float)TimeToX(PlayheadPosition);
            ds.DrawLine(ppx, 0, ppx, h, PlayheadColor, 2);
            return;
        }

        var cursorData = model.CursorData;
        if (cursorData is null || cursorData.Samples.Count == 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, TrackEmptyLineColor, 0.5f);
            float ppx = (float)TimeToX(PlayheadPosition);
            ds.DrawLine(ppx, 0, ppx, h, PlayheadColor, 2);
            return;
        }

        // Determine cursor coordinate ranges for normalization
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var sample in cursorData.Samples)
        {
            if (sample.X < minX) minX = sample.X;
            if (sample.X > maxX) maxX = sample.X;
            if (sample.Y < minY) minY = sample.Y;
            if (sample.Y > maxY) maxY = sample.Y;
        }

        int rangeX = Math.Max(1, maxX - minX);
        int rangeY = Math.Max(1, maxY - minY);
        float margin = 4f;
        float drawHeight = h - margin * 2;
        double tickFreq = cursorData.TickFrequency > 0 ? cursorData.TickFrequency : 1.0;
        long startTicks = cursorData.StartTimestampTicks;
        double mouseOffset = model.MouseToVideoOffsetSeconds;

        // Draw X-position path (blue) and Y-position path (orange)
        if (cursorData.Samples.Count > 1)
        {
            var xPathBuilder = new CanvasPathBuilder(sender);
            var yPathBuilder = new CanvasPathBuilder(sender);
            bool xStarted = false, yStarted = false;

            foreach (var sample in cursorData.Samples)
            {
                double timeSec = (sample.TimestampTicks - startTicks) / tickFreq - mouseOffset;
                float px = (float)TimeToX(TimeSpan.FromSeconds(timeSec));
                if (px < -1 || px > w + 1) continue;

                float normX = (float)(sample.X - minX) / rangeX;
                float normY = (float)(sample.Y - minY) / rangeY;
                float yPosX = margin + (1f - normX) * drawHeight;
                float yPosY = margin + normY * drawHeight;

                if (!xStarted) { xPathBuilder.BeginFigure(px, yPosX); xStarted = true; }
                else xPathBuilder.AddLine(px, yPosX);

                if (!yStarted) { yPathBuilder.BeginFigure(px, yPosY); yStarted = true; }
                else yPathBuilder.AddLine(px, yPosY);
            }

            if (xStarted)
            {
                xPathBuilder.EndFigure(CanvasFigureLoop.Open);
                ds.DrawGeometry(CanvasGeometry.CreatePath(xPathBuilder), CursorPathXColor, 1.2f);
            }
            if (yStarted)
            {
                yPathBuilder.EndFigure(CanvasFigureLoop.Open);
                ds.DrawGeometry(CanvasGeometry.CreatePath(yPathBuilder), CursorPathYColor, 1.2f);
            }
        }

        // Draw click events as dots
        foreach (var click in cursorData.Clicks)
        {
            if (!click.IsDown) continue;
            double timeSec = (click.TimestampTicks - startTicks) / tickFreq - mouseOffset;
            float cx = (float)TimeToX(TimeSpan.FromSeconds(timeSec));
            if (cx < -4 || cx > w + 4) continue;

            float normY = (float)(click.Y - minY) / rangeY;
            float cy = margin + normY * drawHeight;
            ds.FillCircle(cx, cy, 3f, CursorClickColor);
            ds.DrawCircle(cx, cy, 3f, SpeedLabelTextColor, 0.8f);
        }

        // Playhead
        float playX = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(playX, 0, playX, h, PlayheadColor, 2);
    }

    // --- Interaction ---

    private void Track_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Click on ruler, zoom, or audio track → move playhead
        if (sender is CanvasControl canvas)
        {
            var pos = e.GetCurrentPoint(canvas).Position;
            PlayheadPosition = XToTime(pos.X);
            _dragMode = DragMode.Playhead;
            canvas.CapturePointer(e.Pointer);
        }
    }

    private void VideoTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null || sender is not CanvasControl canvas) return;

        var pos = e.GetCurrentPoint(canvas).Position;

        // Check trim handles first
        double trimStartX = TimeToX(model.TrimStart);
        double trimEndX = TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);

        if (Math.Abs(pos.X - trimStartX) <= TrimHandleWidth)
        {
            _dragMode = DragMode.TrimStart;
            canvas.CapturePointer(e.Pointer);
            return;
        }

        if (Math.Abs(pos.X - trimEndX) <= TrimHandleWidth)
        {
            _dragMode = DragMode.TrimEnd;
            canvas.CapturePointer(e.Pointer);
            return;
        }

        // Hit-test clips for selection
        var clickTime = XToTime(pos.X);
        int? hitClipIndex = null;
        for (int i = 0; i < model.Clips.Count; i++)
        {
            var clip = model.Clips[i];
            if (clickTime >= clip.Start && clickTime < clip.End)
            {
                hitClipIndex = i;
                break;
            }
        }

        if (hitClipIndex is not null)
        {
            SelectedClipIndex = hitClipIndex;
            VideoClipSelected?.Invoke(this, hitClipIndex);
        }
        else if (_selectedClipIndex is not null)
        {
            ClearClipSelection();
        }

        // Always move playhead on click
        PlayheadPosition = clickTime;
        _dragMode = DragMode.Playhead;
        canvas.CapturePointer(e.Pointer);
    }

    private void VideoTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null || sender is not CanvasControl canvas) return;

        var pos = e.GetCurrentPoint(canvas).Position;

        // Update cursor for trim handles
        if (_dragMode == DragMode.None)
        {
            double trimStartX = TimeToX(model.TrimStart);
            double trimEndX = TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
            bool nearHandle = Math.Abs(pos.X - trimStartX) <= TrimHandleWidth ||
                              Math.Abs(pos.X - trimEndX) <= TrimHandleWidth;
            SetCursor(nearHandle
                ? InputSystemCursorShape.SizeWestEast
                : InputSystemCursorShape.Arrow);
        }

        switch (_dragMode)
        {
            case DragMode.Playhead:
                PlayheadPosition = XToTime(pos.X);
                break;

            case DragMode.TrimStart:
                var newStart = XToTime(pos.X);
                var maxStart = (model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration)
                    - TimeSpan.FromMilliseconds(100);
                if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                if (newStart > maxStart) newStart = maxStart;
                model.TrimStart = newStart;
                InvalidateAll();
                break;

            case DragMode.TrimEnd:
                var newEnd = XToTime(pos.X);
                if (newEnd < model.TrimStart + TimeSpan.FromMilliseconds(100))
                    newEnd = model.TrimStart + TimeSpan.FromMilliseconds(100);
                if (newEnd > model.Duration) newEnd = model.Duration;
                model.TrimEnd = newEnd;
                InvalidateAll();
                break;
        }
    }

    private void VideoTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragMode = DragMode.None;
        if (sender is CanvasControl canvas)
            canvas.ReleasePointerCapture(e.Pointer);
    }

    private void Grid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null) return;

        var props = e.GetCurrentPoint(this).Properties;
        int delta = props.MouseWheelDelta;

        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            // Ctrl+Scroll → zoom
            double factor = delta > 0 ? 1.15 : 1.0 / 1.15;
            model.ZoomLevel = Math.Clamp(model.ZoomLevel * factor, 0.1, 50.0);
        }
        else
        {
            // Scroll → horizontal scroll
            double scrollDelta = -delta / 120.0 * 0.5; // 0.5 seconds per notch
            model.ScrollOffset = Math.Clamp(
                model.ScrollOffset + scrollDelta,
                0,
                Math.Max(0, model.Duration.TotalSeconds - 1));
        }

        InvalidateAll();
        e.Handled = true;
    }

    private void SetCursor(InputSystemCursorShape shape)
    {
        if (ProtectedCursor is InputSystemCursor current && current.CursorShape == shape)
            return;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
