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

    // Colors — resolved from WinUI system theme resources (see ResolveThemeColors)
    private Color RulerBackground;
    private Color RulerTickColor;
    private Color RulerTextColor;
    private Color VideoTrackBackground;
    private Color VideoClipColor;
    private Color VideoClipSelectedColor;
    private Color VideoClipSelectedBorder;
    private Color FilmstripBackplateColor;
    private Color FilmstripStrokeColor;
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
    private Color ClickStrokeColor;

    // Filmstrip thumbnail cache
    private CanvasBitmap[]? _thumbnails;
    private double _thumbnailIntervalSeconds;
    private double _videoAspectRatio = 16.0 / 9.0;
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
            // Check control-local resources first
            if (Resources.TryGetValue(key, out var val) && val is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                return brush.Color;

            // Fall back to Application.Resources (where AppColors.xaml is merged)
            if (Application.Current?.Resources?.TryGetValue(key, out var appVal) == true
                && appVal is Microsoft.UI.Xaml.Media.SolidColorBrush appBrush)
                return appBrush.Color;
        }
        catch { /* resource not found — use fallback */ }
        return fallback;
    }

    /// <summary>Resolves a WinUI system theme resource brush/color from Application.Current.Resources.</summary>
    private static Color GetSystemBrushColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources?.TryGetValue(key, out var val) == true)
            {
                if (val is Microsoft.UI.Xaml.Media.SolidColorBrush b)
                    return b.Color;
                if (val is Color c)
                    return c;
            }
        }
        catch { }
        return fallback;
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private void ResolveThemeColors()
    {
        bool isDark = ActualTheme != ElementTheme.Light;

        // ── Backgrounds — WinUI system surface colors ──
        RulerBackground      = GetSystemBrushColor("SolidBackgroundFillColorBaseBrush", Color.FromArgb(255, 32, 32, 32));
        VideoTrackBackground = GetSystemBrushColor("CardBackgroundFillColorDefaultBrush", Color.FromArgb(255, 45, 45, 45));
        ZoomTrackBackground  = GetSystemBrushColor("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(255, 28, 28, 28));
        AudioTrackBackground = GetSystemBrushColor("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(255, 28, 28, 28));
        CursorTrackBackground = GetSystemBrushColor("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(255, 28, 28, 28));

        // ── Ruler — system text colors ──
        RulerTickColor = GetSystemBrushColor("TextFillColorTertiaryBrush", Color.FromArgb(255, 135, 135, 135));
        RulerTextColor = GetSystemBrushColor("TextFillColorSecondaryBrush", Color.FromArgb(255, 197, 197, 197));

        // ── Video track — #0C2EE8 electric blue ──
        VideoClipColor        = isDark ? Color.FromArgb(255, 12, 46, 232) : Color.FromArgb(255, 10, 38, 195);
        VideoClipSelectedColor = isDark ? Color.FromArgb(255, 50, 80, 255) : Color.FromArgb(255, 40, 65, 220);
        VideoClipSelectedBorder = GetSystemBrushColor("FocusStrokeColorOuterBrush", Color.FromArgb(255, 255, 255, 255));
        FilmstripBackplateColor = GetSystemBrushColor("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(255, 28, 28, 28));
        FilmstripStrokeColor  = GetSystemBrushColor("ControlStrokeColorDefaultBrush", Color.FromArgb(30, 255, 255, 255));

        // ── Speed overlays — semantic status colors ──
        SpeedUpOverlayColor   = GetBrushColor("TimelineSpeedUpOverlayBrush", Color.FromArgb(200, 230, 160, 50));
        SlowDownOverlayColor  = GetBrushColor("TimelineSlowDownOverlayBrush", Color.FromArgb(200, 60, 130, 230));

        // ── Trim handles — system text/stroke ──
        TrimHandleColor       = GetSystemBrushColor("TextFillColorPrimaryBrush", Color.FromArgb(255, 255, 255, 255));
        TrimHandleBorderColor = GetSystemBrushColor("SurfaceStrokeColorDefaultBrush", Color.FromArgb(255, 117, 117, 117));

        // ── Zoom segments — #DDFF00 neon yellow ──
        var zoomBase          = isDark ? Color.FromArgb(255, 221, 255, 0) : Color.FromArgb(255, 180, 210, 0);
        var zoomLight         = isDark ? Color.FromArgb(255, 235, 255, 60) : Color.FromArgb(255, 200, 230, 40);
        ZoomSegmentFill       = WithAlpha(zoomBase, 200);
        ZoomSegmentAutoFill   = WithAlpha(zoomBase, 120);
        ZoomSegmentSelectedFill = WithAlpha(zoomLight, 230);
        ZoomSegmentBorder     = WithAlpha(zoomBase, 220);
        ZoomSegmentSelectedBorder = isDark ? Color.FromArgb(255, 240, 255, 100) : Color.FromArgb(255, 160, 190, 0);
        ZoomSegmentHandleColor = isDark ? Color.FromArgb(255, 245, 255, 150) : Color.FromArgb(255, 140, 170, 0);
        ZoomSegmentCreatePreview = WithAlpha(zoomBase, 120);
        // Dark text on bright neon yellow for readability
        ZoomSegmentTextColor  = isDark ? Color.FromArgb(255, 30, 40, 0) : Color.FromArgb(255, 40, 50, 0);

        // ── Audio — #0DFF89 neon green ──
        var audioBase         = isDark ? Color.FromArgb(255, 13, 255, 137) : Color.FromArgb(255, 10, 210, 110);
        AudioWaveformColor    = WithAlpha(audioBase, 240);
        AudioEnvelopeColor    = WithAlpha(audioBase, 120);
        AudioPlaceholderColor = WithAlpha(audioBase, 150);

        // ── Mic — #FF00AA hot pink ──
        var micBase           = isDark ? Color.FromArgb(255, 255, 0, 170) : Color.FromArgb(255, 210, 0, 140);
        MicWaveformColor      = WithAlpha(micBase, 240);
        MicEnvelopeColor      = WithAlpha(micBase, 120);

        // ── Cursor paths — #E87C06 orange ──
        var cursorBase        = isDark ? Color.FromArgb(255, 232, 124, 6) : Color.FromArgb(255, 210, 110, 5);
        CursorPathXColor      = WithAlpha(cursorBase, 255);
        CursorPathYColor      = WithAlpha(cursorBase, 150);
        CursorClickColor      = GetBrushColor("TimelineCursorClickBrush", Color.FromArgb(255, 255, 80, 80));
        ClickStrokeColor      = GetSystemBrushColor("ControlStrokeColorDefaultBrush", Color.FromArgb(255, 120, 120, 120));

        // ── Playhead & cut lines — semantic ──
        PlayheadColor         = isDark ? Color.FromArgb(255, 221, 255, 0) : Color.FromArgb(255, 180, 210, 0);
        CutLineColor          = GetBrushColor("TimelineCutLineBrush", Color.FromArgb(200, 255, 255, 100));

        // ── Text & lines — system ──
        SpeedLabelTextColor   = GetSystemBrushColor("TextFillColorPrimaryBrush", Color.FromArgb(255, 255, 255, 255));
        TrackCenterLineColor  = GetSystemBrushColor("DividerStrokeColorDefaultBrush", Color.FromArgb(100, 255, 255, 255));
        TrackEmptyLineColor   = GetSystemBrushColor("ControlStrokeColorDefaultBrush", Color.FromArgb(60, 255, 255, 255));
    }

    public void InvalidateAllCanvases()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
    }

    // --- Filmstrip Thumbnail Management ---

    /// <summary>
    /// Sets pre-scaled thumbnails for the video filmstrip.
    /// TimelineControl takes ownership and disposes previous thumbnails.
    /// </summary>
    public void SetThumbnails(CanvasBitmap[]? thumbnails, double intervalSeconds, double aspectRatio)
    {
        if (_thumbnails is not null)
        {
            foreach (var t in _thumbnails)
                t?.Dispose();
        }

        _thumbnails = thumbnails;
        _thumbnailIntervalSeconds = intervalSeconds;
        _videoAspectRatio = aspectRatio > 0 ? aspectRatio : 16.0 / 9.0;
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>Clears and disposes all cached thumbnails.</summary>
    public void ClearThumbnails() => SetThumbnails(null, 0, 16.0 / 9.0);

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

    /// <summary>Horizontal inset so rounded clip edges aren't clipped at the canvas boundary.</summary>
    private const double TrackContentInset = 4;

    private double TimeToX(TimeSpan time)
    {
        var model = Model;
        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return TrackContentInset;

        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = ((canvasWidth - TrackContentInset * 2) / totalSeconds) * model.ZoomLevel;
        return TrackContentInset + (time.TotalSeconds - model.ScrollOffset) * pixelsPerSecond;
    }

    private TimeSpan XToTime(double x)
    {
        var model = Model;
        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return TimeSpan.Zero;

        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = ((canvasWidth - TrackContentInset * 2) / totalSeconds) * model.ZoomLevel;
        if (pixelsPerSecond <= 0) return TimeSpan.Zero;

        double seconds = ((x - TrackContentInset) / pixelsPerSecond) + model.ScrollOffset;
        seconds = Math.Clamp(seconds, 0, totalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Converts a source-video time (zoom keyframe / cursor timestamp) to an X
    /// coordinate, mapping through segments so it stays aligned with the video
    /// after text slides shift later content.
    /// </summary>
    private double SourceTimeToX(TimeSpan sourceTime)
    {
        var model = Model;
        if (model is null || model.Segments.Count == 0)
            return TimeToX(sourceTime);
        return TimeToX(model.SourceToOutputTime(sourceTime));
    }

    /// <summary>
    /// Converts an X coordinate to a source-video time, the inverse of
    /// <see cref="SourceTimeToX"/>. Falls back to plain output time when the X
    /// lands on a text slide.
    /// </summary>
    private TimeSpan XToSourceTime(double x)
    {
        var model = Model;
        var outputTime = XToTime(x);
        if (model is null || model.Segments.Count == 0)
            return outputTime;
        return model.OutputToSourceTime(outputTime) ?? outputTime;
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

        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return;

        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double pixelsPerSecond = ((w - TrackContentInset * 2) / totalSeconds) * model.ZoomLevel;

        // Choose tick interval based on zoom
        double tickInterval = ChooseTickInterval(pixelsPerSecond);
        double minorInterval = tickInterval / 5;

        double startSec = Math.Max(0, model.ScrollOffset - tickInterval);
        double endSec = Math.Min(totalSeconds, model.ScrollOffset + w / pixelsPerSecond + tickInterval);

        // Minor ticks
        for (double t = Math.Floor(startSec / minorInterval) * minorInterval; t <= endSec; t += minorInterval)
        {
            float x = (float)(TrackContentInset + (t - model.ScrollOffset) * pixelsPerSecond);
            if (x < 0 || x > w) continue;
            ds.DrawLine(x, h * 0.7f, x, h, RulerTickColor);
        }

        // Major ticks + labels
        for (double t = Math.Floor(startSec / tickInterval) * tickInterval; t <= endSec; t += tickInterval)
        {
            float x = (float)(TrackContentInset + (t - model.ScrollOffset) * pixelsPerSecond);
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

    private const float VideoClipCornerRadius = 6;

    private void VideoTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(VideoTrackBackground);

        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return;

        bool hasThumbnails = _thumbnails is not null && _thumbnails.Length > 0 && _thumbnailIntervalSeconds > 0;
        const float pad = 14f;

        // When the timeline uses segments (video + text slides), render each
        // segment at its own position instead of drawing the continuous clip
        // filmstrip. This keeps text slides occupying real timeline space.
        if (model.Segments.Count > 0)
        {
            DrawVideoTrackFromSegments(ds, model, w, h, pad, hasThumbnails);

            // Trim handles + speed overlays don't apply to the segment view
            return;
        }

        // Draw clips
        for (int idx = 0; idx < model.Clips.Count; idx++)
        {
            var clip = model.Clips[idx];
            float x1 = (float)TimeToX(clip.Start);
            float x2 = (float)TimeToX(clip.End);
            if (x2 < 0 || x1 > w) continue;

            bool isSelected = idx == _selectedClipIndex;
            float clipW = Math.Max(1, x2 - x1);
            float clipH = h - pad * 2;

            using var clipGeom = CanvasGeometry.CreateRoundedRectangle(ds, x1, pad, clipW, clipH, VideoClipCornerRadius, VideoClipCornerRadius);

            if (hasThumbnails)
            {
                // FCP-style filmstrip: clip to rounded rect, draw backplate → thumbnails → stroke
                using (ds.CreateLayer(1f, clipGeom))
                {
                    ds.FillGeometry(clipGeom, FilmstripBackplateColor);
                    DrawFilmstrip(ds, x1, x2, pad, clipH, clip, w);
                }
                var strokeColor = isSelected ? VideoClipSelectedBorder : FilmstripStrokeColor;
                float strokeWidth = isSelected ? 2f : 1f;
                ds.DrawGeometry(clipGeom, strokeColor, strokeWidth);
            }
            else
            {
                var clipColor = isSelected ? VideoClipSelectedColor : VideoClipColor;
                ds.FillGeometry(clipGeom, clipColor);
                if (isSelected)
                    ds.DrawGeometry(clipGeom, VideoClipSelectedBorder, 2f);
            }

            // Speed indicator for clips with non-default SpeedFactor
            if (Math.Abs(clip.SpeedFactor - 1.0) > 0.001)
            {
                var segColor = clip.SpeedFactor > 1.0 ? SpeedUpOverlayColor : SlowDownOverlayColor;
                using (ds.CreateLayer(1f, clipGeom))
                {
                    ds.FillGeometry(clipGeom, segColor);
                }

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
            float clipW = Math.Max(1, x2 - x1);
            float clipH = h - pad * 2;

            using var clipGeom = CanvasGeometry.CreateRoundedRectangle(ds, x1, pad, clipW, clipH, VideoClipCornerRadius, VideoClipCornerRadius);

            if (hasThumbnails)
            {
                using (ds.CreateLayer(1f, clipGeom))
                {
                    ds.FillGeometry(clipGeom, FilmstripBackplateColor);
                    DrawFilmstrip(ds, x1, x2, pad, clipH, null, w);
                }
                ds.DrawGeometry(clipGeom, FilmstripStrokeColor, 1f);
            }
            else
            {
                ds.FillGeometry(clipGeom, VideoClipColor);
            }
        }

        // Speed segments overlay (orange = sped up, blue = slowed down)
        foreach (var seg in model.SpeedSegments)
        {
            float x1 = (float)TimeToX(seg.Start);
            float x2 = (float)TimeToX(seg.End);
            if (x2 < 0 || x1 > w) continue;

            var segColor = seg.Speed > 1.0 ? SpeedUpOverlayColor : SlowDownOverlayColor;
            float segW = Math.Max(1, x2 - x1);
            using var segGeom = CanvasGeometry.CreateRoundedRectangle(ds, x1, 4, segW, h - 8, VideoClipCornerRadius, VideoClipCornerRadius);
            ds.FillGeometry(segGeom, segColor);

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
    }

    /// <summary>
    /// Renders the video track from the segment list: each <see cref="VideoSegment"/>
    /// is drawn as a filmstrip clip showing its source range, and each
    /// <see cref="TextSlideSegment"/> as a colored block with its text.
    /// </summary>
    private void DrawVideoTrackFromSegments(
        CanvasDrawingSession ds, TimelineModel model, float w, float h, float pad, bool hasThumbnails)
    {
        var textSlideColor = Color.FromArgb(230, 100, 149, 237); // CornflowerBlue
        var textSlideSelectedColor = Color.FromArgb(245, 130, 170, 255);
        var textSlideBorder = Color.FromArgb(255, 80, 120, 200);
        var transitionColor = Color.FromArgb(180, 255, 193, 7);  // Amber
        var textLabelColor = Color.FromArgb(255, 255, 255, 255);
        float clipH = h - pad * 2;

        foreach (var segment in model.Segments)
        {
            float x1 = (float)TimeToX(segment.Start);
            float x2 = (float)TimeToX(segment.End);
            if (x2 < 0 || x1 > w) continue;
            float segW = Math.Max(2, x2 - x1);

            using var segGeom = CanvasGeometry.CreateRoundedRectangle(
                ds, x1, pad, segW, clipH, VideoClipCornerRadius, VideoClipCornerRadius);

            if (segment is VideoSegment video)
            {
                bool isSelected = video.Id == _selectedSegmentId;
                if (hasThumbnails)
                {
                    using (ds.CreateLayer(1f, segGeom))
                    {
                        ds.FillGeometry(segGeom, FilmstripBackplateColor);
                        DrawFilmstripForSegment(ds, x1, x2, pad, clipH, video);
                    }
                    var strokeColor = isSelected ? VideoClipSelectedBorder : FilmstripStrokeColor;
                    ds.DrawGeometry(segGeom, strokeColor, isSelected ? 2f : 1f);
                }
                else
                {
                    ds.FillGeometry(segGeom, isSelected ? VideoClipSelectedColor : VideoClipColor);
                    if (isSelected) ds.DrawGeometry(segGeom, VideoClipSelectedBorder, 2f);
                }
            }
            else if (segment is TextSlideSegment slide)
            {
                bool isSelected = slide.Id == _selectedSegmentId;
                ds.FillGeometry(segGeom, isSelected ? textSlideSelectedColor : textSlideColor);
                if (isSelected)
                    ds.DrawGeometry(segGeom, textSlideBorder, 2f);

                if (segW > 20)
                {
                    var labelText = slide.Text.Length > 24 ? slide.Text[..24] + "…" : slide.Text;
                    using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                    {
                        FontSize = Math.Min(15, clipH * 0.35f),
                        FontFamily = "Segoe UI",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                        VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                        WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
                        TrimmingGranularity = Microsoft.Graphics.Canvas.Text.CanvasTextTrimmingGranularity.Character,
                    };
                    ds.DrawText(labelText, new Rect(x1 + 4, pad, segW - 8, clipH), textLabelColor, fmt);
                }
            }

            // Transition marker at segment start
            if (segment.InTransition is { Type: not TransitionType.None } transition)
            {
                float transW = Math.Max(4, (float)TimeToX(segment.Start + transition.Duration) - x1);
                transW = Math.Min(transW, segW);
                ds.FillRoundedRectangle(x1, pad, transW, 4, 2, 2, transitionColor);
            }

            // Boundary line between segments
            if (segment.Start > TimeSpan.Zero)
                ds.DrawLine(x1, pad, x1, h - pad, CutLineColor, 1.5f);
        }
    }

    /// <summary>
    /// Draws a filmstrip for a <see cref="VideoSegment"/>, mapping the segment's
    /// timeline position to its source range so the correct thumbnails are shown.
    /// </summary>
    private void DrawFilmstripForSegment(CanvasDrawingSession ds, float clipX1, float clipX2,
        float y, float trackH, VideoSegment segment)
    {
        if (_thumbnails is null || _thumbnails.Length == 0 || _thumbnailIntervalSeconds <= 0)
            return;

        float thumbH = trackH;
        float thumbW = thumbH * (float)_videoAspectRatio;
        if (thumbW < 2) return;

        float canvasWidth = (float)VideoTrackCanvas.ActualWidth;
        float visibleX1 = Math.Max(0, clipX1);
        float visibleX2 = Math.Min(canvasWidth, clipX2);
        if (visibleX1 >= visibleX2) return;

        for (float tileX = clipX1; tileX < visibleX2; tileX += thumbW)
        {
            if (tileX + thumbW < visibleX1) continue;

            float tileCenterX = Math.Clamp(tileX + thumbW / 2, clipX1, clipX2);
            var timelineTime = XToTime(tileCenterX);

            // Map timeline time → source time within this segment
            var offset = timelineTime - segment.Start;
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
            var sourceTime = segment.SourceStart +
                TimeSpan.FromTicks((long)(offset.Ticks * segment.SpeedFactor));

            int thumbIndex = (int)(sourceTime.TotalSeconds / _thumbnailIntervalSeconds);
            thumbIndex = Math.Clamp(thumbIndex, 0, _thumbnails.Length - 1);

            var thumb = _thumbnails[thumbIndex];
            if (thumb is null) continue;

            float drawX = Math.Max(tileX, clipX1);
            float drawEndX = Math.Min(tileX + thumbW, clipX2);
            float drawW = drawEndX - drawX;
            if (drawW <= 0) continue;

            float srcX = (drawX - tileX) / thumbW * thumb.SizeInPixels.Width;
            float srcW = drawW / thumbW * thumb.SizeInPixels.Width;

            ds.DrawImage(thumb,
                new Rect(drawX, y, drawW, thumbH),
                new Rect(srcX, 0, srcW, thumb.SizeInPixels.Height));
        }
    }

    /// <summary>Currently selected segment ID for text slide highlighting.</summary>
    private string? _selectedSegmentId;

    /// <summary>Sets the selected segment ID (called from EditorPage).</summary>
    public void SelectSegment(string? segmentId)
    {
        _selectedSegmentId = segmentId;
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>Raised when a text slide segment is clicked on the timeline.</summary>
    public event EventHandler<string?>? SegmentSelected;

    private void DrawTrimHandle(CanvasDrawingSession ds, TimeSpan time, float trackHeight)
    {
        float x = (float)TimeToX(time);
        const float pad = 4f;
        float handleW = 6f;
        float pillW = 3f;
        float pillH = trackHeight * 0.35f;
        float pillY = (trackHeight - pillH) / 2f;
        float pillRadius = pillW / 2f;

        // Subtle rounded handle background at clip edge
        ds.FillRoundedRectangle(x - handleW / 2, pad, handleW, trackHeight - pad * 2,
            VideoClipCornerRadius, VideoClipCornerRadius, TrimHandleBorderColor);

        // Pill grip indicator centered in handle
        ds.FillRoundedRectangle(x - pillW / 2, pillY, pillW, pillH,
            pillRadius, pillRadius, TrimHandleColor);
    }

    /// <summary>
    /// Draws a filmstrip of pre-scaled thumbnails tiled across a clip region.
    /// </summary>
    private void DrawFilmstrip(CanvasDrawingSession ds, float clipX1, float clipX2,
        float y, float trackH, TimelineClip? clip, float canvasWidth)
    {
        if (_thumbnails is null || _thumbnails.Length == 0 || _thumbnailIntervalSeconds <= 0)
            return;

        float thumbH = trackH;
        float thumbW = thumbH * (float)_videoAspectRatio;
        if (thumbW < 2) return;

        // Determine visible range
        float visibleX1 = Math.Max(0, clipX1);
        float visibleX2 = Math.Min(canvasWidth, clipX2);
        if (visibleX1 >= visibleX2) return;

        // Align tile grid to clip start, skip off-screen tiles
        float firstTileX = clipX1;
        if (firstTileX < visibleX1 - thumbW)
        {
            int skipTiles = (int)((visibleX1 - firstTileX) / thumbW);
            firstTileX += skipTiles * thumbW;
        }

        for (float tileX = firstTileX; tileX < visibleX2; tileX += thumbW)
        {
            // Source time for the center of this tile
            float tileCenterX = Math.Clamp(tileX + thumbW / 2, clipX1, clipX2);
            var timelineTime = XToTime(tileCenterX);

            // Map timeline time → source time (respects speed changes and clip offsets)
            TimeSpan sourceTime;
            if (clip is not null)
            {
                var offset = timelineTime - clip.Start;
                if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
                sourceTime = clip.EffectiveSourceStart +
                    TimeSpan.FromTicks((long)(offset.Ticks * clip.SpeedFactor));
            }
            else
            {
                sourceTime = timelineTime;
            }

            // Find nearest cached thumbnail
            int thumbIndex = (int)(sourceTime.TotalSeconds / _thumbnailIntervalSeconds);
            thumbIndex = Math.Clamp(thumbIndex, 0, _thumbnails.Length - 1);

            var thumb = _thumbnails[thumbIndex];
            if (thumb is null) continue;

            // Clip draw rect to clip bounds
            float drawX = Math.Max(tileX, clipX1);
            float drawEndX = Math.Min(tileX + thumbW, clipX2);
            float drawW = drawEndX - drawX;
            if (drawW <= 0) continue;

            // Source rect within the thumbnail bitmap
            float srcX = (drawX - tileX) / thumbW * thumb.SizeInPixels.Width;
            float srcW = drawW / thumbW * thumb.SizeInPixels.Width;

            ds.DrawImage(thumb,
                new Rect(drawX, y, drawW, thumbH),
                new Rect(srcX, 0, srcW, thumb.SizeInPixels.Height));
        }
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

            using var roundedRect = CanvasGeometry.CreateRoundedRectangle(ds, x1, segY, segW, segH, ZoomSegmentCornerRadius, ZoomSegmentCornerRadius);
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
            float cx1 = (float)SourceTimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateStart : _zoomCreateEnd);
            float cx2 = (float)SourceTimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateEnd : _zoomCreateStart);
            float cw = Math.Max(2, cx2 - cx1);
            float cy = ZoomSegmentVerticalPadding;
            float ch = h - ZoomSegmentVerticalPadding * 2;

            using var previewRect = CanvasGeometry.CreateRoundedRectangle(ds, cx1, cy, cw, ch, ZoomSegmentCornerRadius, ZoomSegmentCornerRadius);
            ds.FillGeometry(previewRect, ZoomSegmentCreatePreview);
            ds.DrawGeometry(previewRect, ZoomSegmentBorder, 1f,
                new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }
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
                return SourceTimeToX(_zoomDragOriginalStart) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentLeftEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return SourceTimeToX(kf.Start);
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
                return SourceTimeToX(_zoomDragOriginalEnd) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentRightEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return SourceTimeToX(kf.End);
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
            float x1 = (float)SourceTimeToX(kf.Start);
            float x2 = (float)SourceTimeToX(kf.End);

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
            _zoomCreateStart = XToSourceTime(pos.X);
            _dragMode = DragMode.ZoomSegmentCreate;
            PlayheadPosition = XToTime(pos.X);
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
                    _zoomCreateEnd = XToSourceTime(pos.X);
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
                var deltaTime = XToSourceTime(_zoomDragStartX + deltaX) - XToSourceTime(_zoomDragStartX);
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
                var newEdgeTime = XToSourceTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth));
                if (newEdgeTime != _zoomDragOriginalStart)
                {
                    ZoomSegmentResized?.Invoke(this, (_selectedZoomKeyframeId, true, newEdgeTime));
                }
                break;
            }

            case DragMode.ZoomSegmentRightEdge when _selectedZoomKeyframeId is not null:
            {
                var newEdgeTime = XToSourceTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth));
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

    /// <summary>Raised when the system audio track mute state changes.</summary>
    public event EventHandler<bool>? SystemAudioMuteChanged;

    /// <summary>Raised when the mic track mute state changes.</summary>
    public event EventHandler<bool>? MicAudioMuteChanged;

    private static Color MutedWaveformOverlay => GetMutedWaveformOverlayColor();

    private static Color GetMutedWaveformOverlayColor()
    {
        if (Application.Current?.Resources is ResourceDictionary resources)
        {
            if (resources.TryGetValue("TextFillColorDisabledBrush", out var disabledBrushObject) &&
                disabledBrushObject is Microsoft.UI.Xaml.Media.SolidColorBrush disabledBrush)
            {
                return disabledBrush.Color;
            }

            if (resources.TryGetValue("SystemControlDisabledBaseMediumLowBrush", out var legacyBrushObject) &&
                legacyBrushObject is Microsoft.UI.Xaml.Media.SolidColorBrush legacyBrush)
            {
                return legacyBrush.Color;
            }
        }

        return Color.FromArgb(160, 30, 30, 30);
    }

    private void AudioMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        Model.IsSystemAudioMuted = !Model.IsSystemAudioMuted;
        // E767 = Volume3 (unmuted), E74F = Mute
        AudioMuteIcon.Glyph = Model.IsSystemAudioMuted ? "\uE74F" : "\uE767";
        AudioTrackCanvas?.Invalidate();
        SystemAudioMuteChanged?.Invoke(this, Model.IsSystemAudioMuted);
    }

    private void MicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        Model.IsMicAudioMuted = !Model.IsMicAudioMuted;
        MicMuteIcon.Glyph = Model.IsMicAudioMuted ? "\uE74F" : "\uE767";
        MicTrackCanvas?.Invalidate();
        MicAudioMuteChanged?.Invoke(this, Model.IsMicAudioMuted);
    }

    private void AudioTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, Model?.SystemAudioWaveformSamples, AudioWaveformColor, AudioEnvelopeColor,
            Model?.IsSystemAudioMuted == true);
    }

    private void MicTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, Model?.MicAudioWaveformSamples, MicWaveformColor, MicEnvelopeColor,
            Model?.IsMicAudioMuted == true);
    }

    private void DrawWaveformTrack(CanvasControl sender, CanvasDrawEventArgs args,
        float[]? waveform, Color waveformColor, Color envelopeColor, bool isMuted = false)
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

        if (isMuted)
        {
            // Muted: show only a faint dashed center line
            ds.DrawLine(x1, centerY, x2, centerY, TrackEmptyLineColor, 0.5f);
            return;
        }

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
                using var envBuilder = new CanvasPathBuilder(sender);
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
                using var envGeometry = CanvasGeometry.CreatePath(envBuilder);
                ds.DrawGeometry(envGeometry, envelopeColor, 1.5f);
            }
        }
        else
        {
            // No waveform data — show a subtle placeholder line instead of a filled bar
            ds.DrawLine(x1, centerY, x2, centerY, TrackCenterLineColor, 0.5f);
        }

        ds.DrawLine(x1, centerY, x2, centerY, TrackCenterLineColor, 0.5f);
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
            return;
        }

        var cursorData = model.CursorData;
        if (cursorData is null || cursorData.Samples.Count == 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, TrackEmptyLineColor, 0.5f);
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
            using var xPathBuilder = new CanvasPathBuilder(sender);
            using var yPathBuilder = new CanvasPathBuilder(sender);
            bool xStarted = false, yStarted = false;

            foreach (var sample in cursorData.Samples)
            {
                double timeSec = (sample.TimestampTicks - startTicks) / tickFreq - mouseOffset;
                float px = (float)SourceTimeToX(TimeSpan.FromSeconds(timeSec));
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
                using var xGeometry = CanvasGeometry.CreatePath(xPathBuilder);
                ds.DrawGeometry(xGeometry, CursorPathXColor, 1.2f);
            }
            if (yStarted)
            {
                yPathBuilder.EndFigure(CanvasFigureLoop.Open);
                using var yGeometry = CanvasGeometry.CreatePath(yPathBuilder);
                ds.DrawGeometry(yGeometry, CursorPathYColor, 1.2f);
            }
        }

        // Draw click events as dots
        foreach (var click in cursorData.Clicks)
        {
            if (!click.IsDown) continue;
            double timeSec = (click.TimestampTicks - startTicks) / tickFreq - mouseOffset;
            float cx = (float)SourceTimeToX(TimeSpan.FromSeconds(timeSec));
            if (cx < -4 || cx > w + 4) continue;

            float normY = (float)(click.Y - minY) / rangeY;
            float cy = margin + normY * drawHeight;
            ds.FillCircle(cx, cy, 3.5f, CursorClickColor);
            ds.DrawCircle(cx, cy, 3.5f, ClickStrokeColor, 1f);
        }
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

        // Hit-test timeline segments (text slides)
        if (model.Segments.Count > 0)
        {
            string? hitSegId = null;
            foreach (var seg in model.Segments)
            {
                if (seg is TextSlideSegment slide && clickTime >= seg.Start && clickTime < seg.End)
                {
                    hitSegId = slide.Id;
                    break;
                }
            }
            if (hitSegId != _selectedSegmentId)
            {
                _selectedSegmentId = hitSegId;
                SegmentSelected?.Invoke(this, hitSegId);
                VideoTrackCanvas?.Invalidate();
            }
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
