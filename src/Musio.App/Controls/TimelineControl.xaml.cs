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

    private enum DragMode { None, Playhead, TrimStart, TrimEnd, ZoomSegmentBody, ZoomSegmentLeftEdge, ZoomSegmentRightEdge, ZoomSegmentCreate, SegmentBody, SegmentLeftEdge, SegmentRightEdge, CameraSegmentBody, CameraSegmentLeftEdge, CameraSegmentRightEdge, CameraSegmentCreate }
    private DragMode _dragMode = DragMode.None;

    // ── Primary-track (video / text slide) segment drag state ──
    private const double SegmentEdgeHitWidth = 8.0;     // px hit zone for trim edges
    private const double SegmentMoveThreshold = 5.0;    // px before a body press becomes a move
    private const double SegmentSnapThreshold = 8.0;    // px snapping distance while dragging

    private string? _draggedSegmentId;
    private double _segmentDragStartX = double.NaN;
    private double _segmentDragCurrentX = double.NaN;
    private bool _segmentDragMoved;
    private TimeSpan _segmentDragOriginalStart;
    private TimeSpan _segmentDragOriginalDuration;
    private double _segmentSnapGuideX = double.NaN;     // NaN = no snap guide drawn
    private double _segmentDropIndicatorX = double.NaN; // NaN = no drop indicator drawn

    /// <summary>Raised when a primary-track segment should be moved to a new index (commit).</summary>
    public event EventHandler<(string Id, int TargetIndex)>? SegmentMoveRequested;

    /// <summary>Raised when a primary-track segment edge should be ripple-trimmed (commit).</summary>
    public event EventHandler<(string Id, bool FromStart, TimeSpan NewDuration)>? SegmentTrimRequested;

    private enum SegmentHitTarget { None, Body, LeftEdge, RightEdge }

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
    private string? _zoomCreateFile;
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
    private Color TrackHintTextColor;
    private Color ClickStrokeColor;

    // Filmstrip thumbnail cache.
    // _thumbnails/_thumbnailIntervalSeconds/_videoAspectRatio hold the PRIMARY
    // recording's set (also used by the legacy clip-based filmstrip). Appended
    // recordings draw from their OWN source file, so per-file sets are stored in
    // _thumbnailsByFile keyed by VideoSegment.VideoFilePath to avoid showing the
    // primary's frames under an appended segment.
    private CanvasBitmap[]? _thumbnails;
    private double _thumbnailIntervalSeconds;
    private double _videoAspectRatio = 16.0 / 9.0;
    private string? _primaryThumbnailFilePath;

    private sealed class ThumbnailSet
    {
        public required CanvasBitmap[] Thumbnails;
        public double IntervalSeconds;
        public double AspectRatio = 16.0 / 9.0;
    }

    private readonly Dictionary<string, ThumbnailSet> _thumbnailsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-source-file track visualization data (cursor path/clicks + audio
    /// waveforms), keyed by <see cref="VideoSegment.VideoFilePath"/>. Lets each
    /// video segment — including appended recordings — render its own cursor and
    /// audio on the tracks, positioned relative to the segment so the markers move
    /// with the segment when it is reordered/moved/trimmed.
    /// </summary>
    public sealed class SegmentTrackVisual
    {
        public MouseRecordingData? Cursor;
        public double MouseToVideoOffsetSeconds;
        public float[]? SystemWaveform;
        public float[]? MicWaveform;
        /// <summary>Video-time duration (seconds) the waveform arrays span for this file.</summary>
        public double WaveformDurationSeconds;
        public bool HasCamera;
    }

    private readonly Dictionary<string, SegmentTrackVisual> _trackVisualsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers or replaces the per-file track visualization data.</summary>
    public void SetSegmentTrackVisual(string videoFilePath, SegmentTrackVisual visual)
    {
        if (string.IsNullOrEmpty(videoFilePath)) return;
        _trackVisualsByFile[videoFilePath] = visual;
        InvalidateAllCanvases();
    }

    /// <summary>Clears all per-file track visualization data.</summary>
    public void ClearSegmentTrackVisuals()
    {
        _trackVisualsByFile.Clear();
        InvalidateAllCanvases();
    }

    /// <summary>
    /// Maps a video-time (in a segment's own source file, frame 0 = 0) to an output
    /// X coordinate within that segment, or NaN when the time is outside the
    /// segment's kept source range. Used to draw per-segment cursor/click markers.
    /// </summary>
    private double SegmentVideoTimeToX(VideoSegment seg, double fileVideoSeconds)
    {
        var local = TimeSpan.FromSeconds(fileVideoSeconds) - seg.SourceStart;
        if (local < TimeSpan.Zero) return double.NaN;
        if (local > seg.SourceDuration) return double.NaN;
        var outLocal = seg.SpeedFactor != 0
            ? TimeSpan.FromTicks((long)(local.Ticks / seg.SpeedFactor))
            : local;
        return TimeToX(seg.Start + outLocal);
    }

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
        TrackHintTextColor    = GetSystemBrushColor("TextFillColorTertiaryBrush", Color.FromArgb(140, 255, 255, 255));
    }

    public void InvalidateAllCanvases()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        CameraTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
    }

    // --- Filmstrip Thumbnail Management ---

    /// <summary>
    /// Sets pre-scaled thumbnails for the video filmstrip.
    /// TimelineControl takes ownership and disposes previous thumbnails.
    /// </summary>
    /// <param name="filePath">
    /// Source video file these thumbnails belong to. When provided, the set is also
    /// registered per-file so appended recordings show their own frames; this path
    /// is treated as the primary set used by the legacy clip filmstrip.
    /// </param>
    /// <param name="filePath">
    /// Source video file these thumbnails belong to. When provided, the set is also
    /// registered per-file so appended recordings show their own frames; this path
    /// is treated as the primary set used by the legacy clip filmstrip.
    /// </param>
    public void SetThumbnails(CanvasBitmap[]? thumbnails, double intervalSeconds, double aspectRatio,
        string? filePath = null)
    {
        if (_thumbnails is not null)
        {
            // Drop any per-file entry that shares this exact array before disposing,
            // so the per-file cache never references disposed bitmaps.
            var shared = _thumbnailsByFile
                .Where(kv => ReferenceEquals(kv.Value.Thumbnails, _thumbnails))
                .Select(kv => kv.Key).ToList();
            foreach (var key in shared) _thumbnailsByFile.Remove(key);

            foreach (var t in _thumbnails)
                t?.Dispose();
        }

        _thumbnails = thumbnails;
        _thumbnailIntervalSeconds = intervalSeconds;
        _videoAspectRatio = aspectRatio > 0 ? aspectRatio : 16.0 / 9.0;
        _primaryThumbnailFilePath = filePath;

        if (filePath is not null && thumbnails is { Length: > 0 })
            SetThumbnailsForFile(filePath, thumbnails, intervalSeconds, aspectRatio);

        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>
    /// Registers a thumbnail set for a specific source video file (e.g. an appended
    /// recording). The control does NOT take ownership of these bitmaps beyond the
    /// per-file cache; they are disposed by <see cref="ClearThumbnails"/>.
    /// </summary>
    public void SetThumbnailsForFile(string filePath, CanvasBitmap[] thumbnails,
        double intervalSeconds, double aspectRatio)
    {
        if (string.IsNullOrEmpty(filePath) || thumbnails.Length == 0) return;

        if (_thumbnailsByFile.TryGetValue(filePath, out var existing) &&
            !ReferenceEquals(existing.Thumbnails, thumbnails))
        {
            foreach (var t in existing.Thumbnails)
                t?.Dispose();
        }

        _thumbnailsByFile[filePath] = new ThumbnailSet
        {
            Thumbnails = thumbnails,
            IntervalSeconds = intervalSeconds,
            AspectRatio = aspectRatio > 0 ? aspectRatio : 16.0 / 9.0,
        };
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>
    /// Resolves the thumbnail set to draw for a segment's source file. Returns null
    /// when no thumbnails are available for that file (caller draws backplate only,
    /// never another file's frames).
    /// </summary>
    private ThumbnailSet? ResolveThumbnailSet(string? filePath)
    {
        if (filePath is not null &&
            _thumbnailsByFile.TryGetValue(filePath, out var set) &&
            set.Thumbnails.Length > 0)
            return set;

        // Primary set fallback only when the file matches the primary (or is unknown).
        if (_thumbnails is { Length: > 0 } && _thumbnailIntervalSeconds > 0 &&
            (filePath is null ||
             string.Equals(filePath, _primaryThumbnailFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return new ThumbnailSet
            {
                Thumbnails = _thumbnails,
                IntervalSeconds = _thumbnailIntervalSeconds,
                AspectRatio = _videoAspectRatio,
            };
        }

        // Logged once per distinct key: this runs on every redraw, and a miss here is
        // exactly what paints a video segment as a flat colour block.
        if (!string.Equals(_lastUnresolvedThumbnailPath, filePath, StringComparison.Ordinal))
        {
            _lastUnresolvedThumbnailPath = filePath;
            Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                $"no thumbnails for segment '{filePath}' (primary '{_primaryThumbnailFilePath}')");
        }

        return null;
    }

    private string? _lastUnresolvedThumbnailPath;

    /// <summary>Clears and disposes all cached thumbnails (primary and per-file).</summary>
    public void ClearThumbnails()
    {
        // The primary array may also be registered in _thumbnailsByFile; dispose each
        // bitmap exactly once by deduplicating on reference.
        var primary = _thumbnails;
        _thumbnails = null;

        bool primaryDisposed = false;
        foreach (var set in _thumbnailsByFile.Values)
        {
            if (ReferenceEquals(set.Thumbnails, primary)) primaryDisposed = true;
            foreach (var t in set.Thumbnails)
                t?.Dispose();
        }
        _thumbnailsByFile.Clear();

        if (primary is not null && !primaryDisposed)
        {
            foreach (var t in primary)
                t?.Dispose();
        }

        _thumbnailIntervalSeconds = 0;
        _videoAspectRatio = 16.0 / 9.0;
        _primaryThumbnailFilePath = null;
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>Raised when a zoom segment is selected or deselected (null = deselected).</summary>
    public event EventHandler<string?>? ZoomSegmentSelected;

    /// <summary>Raised when a zoom segment drag completes. Carries the keyframe Id and new timestamp.</summary>
    public event EventHandler<(string Id, TimeSpan NewTimestamp)>? ZoomSegmentMoved;

    /// <summary>Raised when a zoom segment is resized. Carries the keyframe Id, whether it was the start edge, and the new edge time.</summary>
    public event EventHandler<(string Id, bool IsStartEdge, TimeSpan NewEdgeTime)>? ZoomSegmentResized;

    /// <summary>Raised when a new zoom segment is created by dragging. Carries the start and end times.</summary>
    public event EventHandler<(TimeSpan Start, TimeSpan End, string? FilePath)>? ZoomSegmentCreated;

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
    /// Converts an X coordinate to a source-video time in the PRIMARY recording's time
    /// domain, for camera-track gestures (<see cref="CameraSegment"/> ranges are always
    /// expressed in primary source time). When the X lands on a text slide or a
    /// non-primary video segment — where <see cref="TimelineModel.OutputToSourceTime"/>
    /// has no primary source time to return — this clamps to the nearest primary video
    /// segment boundary (by output-time distance) instead of mixing raw output time into
    /// the primary source-time domain. Returns <c>null</c> only when the timeline has no
    /// primary video segment at all to clamp against.
    /// </summary>
    private TimeSpan? XToPrimarySourceTime(double x)
    {
        var model = Model;
        var outputTime = XToTime(x);
        if (model is null || model.Segments.Count == 0)
            return outputTime;

        var mapped = model.OutputToSourceTime(outputTime);
        if (mapped is not null)
            return mapped.Value;

        var primarySegments = model.Segments.OfType<VideoSegment>()
            .Where(v => model.PrimaryVideoFilePath is null ||
                string.Equals(v.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase));
        var (nearest, atStart) = TimelineModel.NearestVideoSegmentEdge(primarySegments, outputTime);
        if (nearest is null) return null;
        return atStart ? nearest.SourceStart : nearest.SourceStart + nearest.SourceDuration;
    }

    private void InvalidateAll()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        CameraTrackCanvas?.Invalidate();
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
        var transitionColor = Color.FromArgb(180, 255, 193, 7);  // Amber
        var textLabelColor = Color.FromArgb(255, 255, 255, 255);
        var snapGuideColor = Color.FromArgb(255, 255, 214, 10);  // Amber snap line
        float clipH = h - pad * 2;

        foreach (var segment in model.Segments)
        {
            float x1 = (float)TimeToX(segment.Start);
            float x2 = (float)TimeToX(segment.End);

            // Apply live drag preview to the segment being manipulated.
            bool isDragged = segment.Id == _draggedSegmentId;
            if (isDragged && !double.IsNaN(_segmentDragCurrentX))
            {
                switch (_dragMode)
                {
                    case DragMode.SegmentRightEdge:
                        x2 = (float)_segmentDragCurrentX;
                        if (x2 < x1 + 2) x2 = x1 + 2;
                        break;
                    case DragMode.SegmentLeftEdge:
                        x1 = (float)_segmentDragCurrentX;
                        if (x1 > x2 - 2) x1 = x2 - 2;
                        break;
                    case DragMode.SegmentBody when _segmentDragMoved:
                        float dx = (float)(_segmentDragCurrentX - _segmentDragStartX);
                        x1 += dx;
                        x2 += dx;
                        break;
                }
            }

            if (x2 < 0 || x1 > w) continue;
            float segW = Math.Max(2, x2 - x1);

            using var segGeom = CanvasGeometry.CreateRoundedRectangle(
                ds, x1, pad, segW, clipH, VideoClipCornerRadius, VideoClipCornerRadius);

            if (segment is VideoSegment video)
            {
                bool isSelected = video.Id == _selectedSegmentId;
                // Resolve thumbnails for THIS segment's own source file so appended
                // recordings never show the primary recording's frames.
                var thumbSet = ResolveThumbnailSet(video.VideoFilePath);
                if (thumbSet is not null)
                {
                    using (ds.CreateLayer(1f, segGeom))
                    {
                        ds.FillGeometry(segGeom, FilmstripBackplateColor);
                        DrawFilmstripForSegment(ds, x1, x2, pad, clipH, video, thumbSet);
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
                ds.FillGeometry(segGeom, isSelected ? VideoClipSelectedColor : VideoClipColor);
                if (isSelected)
                    ds.DrawGeometry(segGeom, VideoClipSelectedBorder, 2f);

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

            // Trim-edge handles on the selected segment (grab affordance).
            if (segment.Id == _selectedSegmentId && segW > 10)
            {
                float handleW = 3f;
                float handleH = clipH * 0.5f;
                float handleY = pad + (clipH - handleH) / 2;
                ds.FillRoundedRectangle(x1 + 1.5f, handleY, handleW, handleH, 1.5f, 1.5f, VideoClipSelectedBorder);
                ds.FillRoundedRectangle(x2 - handleW - 1.5f, handleY, handleW, handleH, 1.5f, 1.5f, VideoClipSelectedBorder);
            }

            // Transition marker at segment start
            if (segment.InTransition is { Type: not TransitionType.None } transition)
            {
                float transW = Math.Max(4, (float)TimeToX(segment.Start + transition.Duration) - x1);
                transW = Math.Min(transW, segW);
                ds.FillRoundedRectangle(x1, pad, transW, 4, 2, 2, transitionColor);
            }

            // Boundary line between segments
            if (segment.Start > TimeSpan.Zero && !isDragged)
                ds.DrawLine(x1, pad, x1, h - pad, CutLineColor, 1.5f);
        }

        // Drop indicator (where a moved segment will land).
        if (!double.IsNaN(_segmentDropIndicatorX))
            ds.DrawLine((float)_segmentDropIndicatorX, 0, (float)_segmentDropIndicatorX, h, VideoClipSelectedBorder, 2.5f);

        // Snap guide line.
        if (!double.IsNaN(_segmentSnapGuideX))
            ds.DrawLine((float)_segmentSnapGuideX, 0, (float)_segmentSnapGuideX, h, snapGuideColor, 1f);
    }

    /// <summary>
    /// Draws a filmstrip for a <see cref="VideoSegment"/>, mapping the segment's
    /// timeline position to its source range so the correct thumbnails are shown.
    /// Uses the supplied per-file <paramref name="thumbSet"/> so appended recordings
    /// render their own frames rather than the primary recording's.
    /// </summary>
    private void DrawFilmstripForSegment(CanvasDrawingSession ds, float clipX1, float clipX2,
        float y, float trackH, VideoSegment segment, ThumbnailSet thumbSet)
    {
        var thumbnails = thumbSet.Thumbnails;
        double intervalSeconds = thumbSet.IntervalSeconds;
        if (thumbnails.Length == 0 || intervalSeconds <= 0)
            return;

        float thumbH = trackH;
        float thumbW = thumbH * (float)thumbSet.AspectRatio;
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

            int thumbIndex = (int)(sourceTime.TotalSeconds / intervalSeconds);
            thumbIndex = Math.Clamp(thumbIndex, 0, thumbnails.Length - 1);

            var thumb = thumbnails[thumbIndex];
            if (thumb is null) continue;

            float drawX = Math.Max(tileX, clipX1);
            float drawEndX = Math.Min(tileX + thumbW, clipX2);
            float drawW = drawEndX - drawX;
            if (drawW <= 0) continue;

            float srcX = (drawX - tileX) / thumbW * thumb.SizeInPixels.Width;
            float srcW = drawW / thumbW * thumb.SizeInPixels.Width;

            try
            {
                ds.DrawImage(thumb,
                    new Rect(drawX, y, drawW, thumbH),
                    new Rect(srcX, 0, srcW, thumb.SizeInPixels.Height));
            }
            catch (ObjectDisposedException)
            {
                // A single stale tile must not abort the draw - letting it propagate
                // leaves the whole remainder of the track unpainted.
            }
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

            try
            {
                ds.DrawImage(thumb,
                    new Rect(drawX, y, drawW, thumbH),
                    new Rect(srcX, 0, srcW, thumb.SizeInPixels.Height));
            }
            catch (ObjectDisposedException)
            {
                // A single stale tile must not abort the draw - letting it propagate
                // leaves the whole remainder of the track unpainted.
            }
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

        // Empty state: the hint belongs on the track it describes, where the user is
        // looking, rather than in the toolbar.
        if (sorted.Count == 0)
        {
            using var hintFormat = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontSize = 11,
                FontFamily = "Segoe UI",
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            };

            ds.DrawText("Drag on zoom track to add segment",
                new Rect(0, 0, w, h), TrackHintTextColor, hintFormat);
            return;
        }

        foreach (var kf in sorted)
        {
            float x1 = (float)GetZoomSegmentStartX(kf);
            float x2 = (float)GetZoomSegmentEndX(kf);
            if (float.IsNaN(x1) || float.IsNaN(x2)) continue; // keyframe not in any kept range
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
            float cx1 = (float)ZoomCreateTimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateStart : _zoomCreateEnd);
            float cx2 = (float)ZoomCreateTimeToX(_zoomCreateStart < _zoomCreateEnd ? _zoomCreateEnd : _zoomCreateStart);
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
    /// Finds the video segment that owns a zoom keyframe: the segment matching the
    /// keyframe's source file whose source range contains its Timestamp (falling back
    /// to the first file-matching segment). Both edges of the keyframe map through this
    /// single segment so it renders at full width even if an edge slightly overflows
    /// the clip.
    /// </summary>
    private VideoSegment? OwningSegmentForKeyframe(ZoomKeyframe kf)
    {
        var model = Model;
        if (model is null) return null;

        VideoSegment? firstMatch = null;
        foreach (var seg in model.Segments.OfType<VideoSegment>())
        {
            bool match = kf.SourceVideoFilePath is null
                ? (model.PrimaryVideoFilePath is null ||
                   string.Equals(seg.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase))
                : string.Equals(seg.VideoFilePath, kf.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            firstMatch ??= seg;
            var local = kf.Timestamp - seg.SourceStart;
            if (local >= TimeSpan.Zero && local <= seg.SourceDuration)
                return seg;
        }
        return firstMatch;
    }

    /// <summary>
    /// Maps a zoom keyframe's source time to an output X by routing through the video
    /// segment that owns it (primary or appended). Both edges of a keyframe map through
    /// the same owning segment so the segment renders at its true width. Returns NaN
    /// when no segment owns the keyframe; falls back to the legacy whole-timeline
    /// mapping when the timeline has no segments.
    /// </summary>
    private double ZoomKeyframeTimeToX(ZoomKeyframe kf, TimeSpan sourceTime)
    {
        var model = Model;
        if (model is null) return double.NaN;
        if (model.Segments.Count == 0) return TimeToX(sourceTime);

        var seg = OwningSegmentForKeyframe(kf);
        if (seg is null) return double.NaN;

        var local = sourceTime - seg.SourceStart;
        var outLocal = seg.SpeedFactor != 0
            ? TimeSpan.FromTicks((long)(local.Ticks / seg.SpeedFactor))
            : local;
        return TimeToX(seg.Start + outLocal);
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
                return ZoomKeyframeTimeToX(kf, _zoomDragOriginalStart) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentLeftEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return ZoomKeyframeTimeToX(kf, kf.Start);
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
                return ZoomKeyframeTimeToX(kf, _zoomDragOriginalEnd) + deltaX;
            }
            if (_dragMode == DragMode.ZoomSegmentRightEdge)
            {
                return _zoomDragCurrentX;
            }
        }
        return ZoomKeyframeTimeToX(kf, kf.End);
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
            float x1 = (float)ZoomKeyframeTimeToX(kf, kf.Start);
            float x2 = (float)ZoomKeyframeTimeToX(kf, kf.End);
            if (float.IsNaN(x1) || float.IsNaN(x2)) continue;

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

    /// <summary>
    /// Maps an output X to the video-time within the segment under it, and reports the
    /// owning source file (null = primary). Used when creating a zoom segment so the
    /// keyframe is tagged with and positioned relative to the correct recording. When X
    /// lands on a text slide (no video segment underneath), clamps to the boundary of the
    /// nearest video segment (by output-time distance) instead of stamping the created
    /// keyframe with a raw output-time value in a source-time field. Returns <c>null</c>
    /// only when the timeline has no video segment at all to clamp against.
    /// </summary>
    private TimeSpan? XToSegmentVideoTime(double x, out string? filePath)
    {
        filePath = null;
        var model = Model;
        var outputTime = XToTime(x);
        if (model is null || model.Segments.Count == 0) return outputTime;

        var videoSegments = model.Segments.OfType<VideoSegment>().ToList();
        var containing = videoSegments.FirstOrDefault(seg => outputTime >= seg.Start && outputTime < seg.End);

        VideoSegment target;
        TimeSpan mappingOutputTime;
        if (containing is not null)
        {
            target = containing;
            mappingOutputTime = outputTime;
        }
        else
        {
            var (nearest, atStart) = TimelineModel.NearestVideoSegmentEdge(videoSegments, outputTime);
            if (nearest is null) return null;
            target = nearest;
            mappingOutputTime = atStart ? nearest.Start : nearest.End;
        }

        bool isPrimary = model.PrimaryVideoFilePath is null ||
            string.Equals(target.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase);
        filePath = isPrimary ? null : target.VideoFilePath;
        var local = mappingOutputTime - target.Start;
        return target.SourceStart + TimeSpan.FromTicks((long)(local.Ticks * target.SpeedFactor));
    }

    /// <summary>Maps a create-time (in <see cref="_zoomCreateFile"/>'s source space) to X.</summary>
    private double ZoomCreateTimeToX(TimeSpan sourceTime)
        => ZoomKeyframeTimeToX(
            new ZoomKeyframe { SourceVideoFilePath = _zoomCreateFile, Timestamp = _zoomCreateStart },
            sourceTime);

    /// <summary>
    /// Inverse of <see cref="ZoomKeyframeTimeToX"/>: maps an output X to a source time
    /// in the file space of the segment that owns a keyframe with the given
    /// <paramref name="filePath"/> (null = primary). Used so moving/resizing a zoom
    /// segment on an appended recording stays in that recording's time, not the
    /// primary's. When X lands outside every segment for this file (e.g. over a text
    /// slide, or a segment belonging to a different recording), clamps to the nearest
    /// boundary of a video segment matching <paramref name="filePath"/> — never through
    /// <see cref="TimelineModel.OutputToSourceTime"/>, which always maps via the PRIMARY
    /// recording and would silently mix an unrelated source file's timestamp into this
    /// keyframe. Returns <c>null</c> only when no segment for this file exists at all
    /// (so the caller should reject the gesture rather than move the keyframe).
    /// </summary>
    private TimeSpan? XToKeyframeFileTime(double x, string? filePath)
    {
        var model = Model;
        var outputTime = XToTime(x);
        if (model is null || model.Segments.Count == 0) return outputTime;

        var matching = model.Segments.OfType<VideoSegment>()
            .Where(seg => filePath is null
                ? (model.PrimaryVideoFilePath is null ||
                   string.Equals(seg.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase))
                : string.Equals(seg.VideoFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var containing = matching.FirstOrDefault(seg => outputTime >= seg.Start && outputTime < seg.End);
        if (containing is not null)
        {
            var local = outputTime - containing.Start;
            return containing.SourceStart + TimeSpan.FromTicks((long)(local.Ticks * containing.SpeedFactor));
        }

        var (nearest, atStart) = TimelineModel.NearestVideoSegmentEdge(matching, outputTime);
        if (nearest is null) return null;
        return atStart ? nearest.SourceStart : nearest.SourceStart + nearest.SourceDuration;
    }

    private string? SelectedZoomKeyframeFile =>
        Model?.ZoomKeyframes.FirstOrDefault(k => k.Id == _selectedZoomKeyframeId)?.SourceVideoFilePath;

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
            PlayheadPosition = XToTime(pos.X);

            var start = XToSegmentVideoTime(pos.X, out _zoomCreateFile);
            if (start is null)
            {
                // No video segment anywhere to attach a zoom keyframe to — reject the
                // gesture rather than stamping an output-time value into a source-time
                // field, and leave no transient drag state behind.
                _zoomCreateFile = null;
                _dragMode = DragMode.None;
                _zoomDragStartX = double.NaN;
                _zoomDragCurrentX = double.NaN;
                return;
            }
            _zoomCreateStart = start.Value;
            _dragMode = DragMode.ZoomSegmentCreate;
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
                    // Stay anchored to _zoomCreateFile's source domain (set once at
                    // press-time) rather than re-resolving whatever segment/file is
                    // under the pointer now — otherwise dragging across a slide or a
                    // different recording's segment would mix that other source's
                    // timestamp into this (still A-tagged) keyframe.
                    var end = XToKeyframeFileTime(pos.X, _zoomCreateFile);
                    if (end is not null)
                    {
                        _zoomCreateActive = true;
                        _zoomCreateEnd = end.Value;
                        InvalidateAll();
                    }
                    // else: pointer is outside every segment for _zoomCreateFile with
                    // none to clamp to — keep the previous _zoomCreateEnd rather than
                    // adopting a mismatched-domain value.
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
                // Only fire move event if the segment actually moved
                if (Math.Abs(deltaX) > 1)
                {
                    var file = SelectedZoomKeyframeFile;
                    var startTime = XToKeyframeFileTime(_zoomDragStartX, file);
                    var movedTime = XToKeyframeFileTime(_zoomDragStartX + deltaX, file);
                    if (startTime is not null && movedTime is not null)
                    {
                        var newTimestamp = _zoomDragOriginalTimestamp + (movedTime.Value - startTime.Value);
                        ZoomSegmentMoved?.Invoke(this, (_selectedZoomKeyframeId, newTimestamp));
                    }
                    // else: unmappable in this file's time domain — reject the move,
                    // leaving the keyframe at its original position.
                }
                break;
            }

            case DragMode.ZoomSegmentLeftEdge when _selectedZoomKeyframeId is not null:
            {
                var newEdgeTime = XToKeyframeFileTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth), SelectedZoomKeyframeFile);
                if (newEdgeTime is not null && newEdgeTime.Value != _zoomDragOriginalStart)
                {
                    ZoomSegmentResized?.Invoke(this, (_selectedZoomKeyframeId, true, newEdgeTime.Value));
                }
                break;
            }

            case DragMode.ZoomSegmentRightEdge when _selectedZoomKeyframeId is not null:
            {
                var newEdgeTime = XToKeyframeFileTime(Math.Clamp(_zoomDragCurrentX, 0, canvas.ActualWidth), SelectedZoomKeyframeFile);
                if (newEdgeTime is not null && newEdgeTime.Value != _zoomDragOriginalEnd)
                {
                    ZoomSegmentResized?.Invoke(this, (_selectedZoomKeyframeId, false, newEdgeTime.Value));
                }
                break;
            }

            case DragMode.ZoomSegmentCreate when _zoomCreateActive:
            {
                var start = _zoomCreateStart < _zoomCreateEnd ? _zoomCreateStart : _zoomCreateEnd;
                var end = _zoomCreateStart < _zoomCreateEnd ? _zoomCreateEnd : _zoomCreateStart;
                if ((end - start) >= ZoomKeyframe.MinSegmentDuration)
                {
                    ZoomSegmentCreated?.Invoke(this, (start, end, _zoomCreateFile));
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
        DrawWaveformTrack(sender, args, isMic: false, AudioWaveformColor, AudioEnvelopeColor,
            Model?.IsSystemAudioMuted == true);
    }

    private void MicTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, isMic: true, MicWaveformColor, MicEnvelopeColor,
            Model?.IsMicAudioMuted == true);
    }

    private void DrawWaveformTrack(CanvasControl sender, CanvasDrawEventArgs args,
        bool isMic, Color waveformColor, Color envelopeColor, bool isMuted = false)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(AudioTrackBackground);

        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return;

        float centerY = h / 2f;

        // Segment-based timeline: draw each video segment's OWN audio waveform within
        // its output range so appended recordings show their audio and it moves with
        // the segment.
        if (model.Segments.Count > 0)
        {
            if (isMuted)
            {
                ds.DrawLine(0, centerY, w, centerY, TrackEmptyLineColor, 0.5f);
                return;
            }

            foreach (var seg in model.Segments.OfType<VideoSegment>())
            {
                var visual = ResolveTrackVisual(seg, model);
                var wf = isMic ? visual?.MicWaveform : visual?.SystemWaveform;
                if (wf is { Length: > 0 } && visual!.WaveformDurationSeconds > 0)
                    DrawSegmentWaveform(ds, seg, wf, visual.WaveformDurationSeconds,
                        waveformColor, envelopeColor, w, h, centerY);
            }
            ds.DrawLine(0, centerY, w, centerY, TrackCenterLineColor, 0.5f);
            return;
        }

        // ── Legacy whole-timeline waveform ──
        float[]? waveform = isMic ? model.MicAudioWaveformSamples : model.SystemAudioWaveformSamples;
        float x1 = (float)TimeToX(model.TrimStart);
        float x2 = (float)TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);

        if (isMuted)
        {
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
            ds.DrawLine(x1, centerY, x2, centerY, TrackCenterLineColor, 0.5f);
        }

        ds.DrawLine(x1, centerY, x2, centerY, TrackCenterLineColor, 0.5f);
    }

    /// <summary>
    /// Draws one segment's audio waveform across its output range by sampling the
    /// file-aligned waveform at the segment's source time, so it stays aligned after
    /// the segment is moved, trimmed, or split.
    /// </summary>
    private void DrawSegmentWaveform(CanvasDrawingSession ds, VideoSegment seg,
        float[] wf, double waveformDurationSeconds, Color waveformColor, Color envelopeColor,
        float w, float h, float centerY)
    {
        float segX1 = (float)TimeToX(seg.Start);
        float segX2 = (float)TimeToX(seg.End);
        if (segX2 < 0 || segX1 > w) return;

        float segW = Math.Max(1f, segX2 - segX1);
        double srcStart = seg.SourceStart.TotalSeconds;
        double srcDur = seg.SourceDuration.TotalSeconds;
        if (srcDur <= 0) return;

        // Resolve the waveform index range covering this segment's source span.
        int len = wf.Length;
        int firstIdx = Math.Clamp((int)(srcStart / waveformDurationSeconds * len), 0, len - 1);
        int lastIdx = Math.Clamp((int)((srcStart + srcDur) / waveformDurationSeconds * len), firstIdx, len - 1);
        int sampleCount = Math.Max(1, lastIdx - firstIdx);
        float barWidth = Math.Max(1f, segW / sampleCount);
        float maxBar = h * 0.45f;

        for (int i = firstIdx; i <= lastIdx; i++)
        {
            double srcSec = (double)i / len * waveformDurationSeconds;
            double x = SegmentVideoTimeToX(seg, srcSec);
            if (double.IsNaN(x)) continue;
            float bx = (float)x;
            if (bx > w || bx + barWidth < 0) continue;

            float amplitude = Math.Clamp(wf[i], 0f, 1f);
            float barHeight = amplitude * maxBar;
            ds.FillRectangle(bx, centerY - barHeight, barWidth, barHeight * 2, waveformColor);
        }
    }

    // --- Cursor Path Track ---

    private void CursorTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(CursorTrackBackground);

        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, TrackEmptyLineColor, 0.5f);
            return;
        }

        // Segment-based timeline: draw each video segment's OWN cursor data within
        // its output range so appended recordings show their own track and the
        // markers move with the segment.
        if (model.Segments.Count > 0)
        {
            bool drewAny = false;
            foreach (var seg in model.Segments.OfType<VideoSegment>())
            {
                var visual = ResolveTrackVisual(seg, model);
                if (visual?.Cursor is { Samples.Count: > 0 })
                {
                    DrawSegmentCursor(ds, sender, seg, visual, w, h);
                    drewAny = true;
                }
            }
            if (!drewAny)
                ds.DrawLine(0, h / 2, w, h / 2, TrackEmptyLineColor, 0.5f);
            return;
        }

        // ── Legacy single-recording cursor path ──
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

    /// <summary>
    /// Resolves the per-file track visual for a segment: the registered per-file
    /// data when available, otherwise a model-backed visual for the primary
    /// recording (so the primary keeps working without an explicit registration).
    /// </summary>
    private SegmentTrackVisual? ResolveTrackVisual(VideoSegment seg, TimelineModel model)
    {
        if (!string.IsNullOrEmpty(seg.VideoFilePath) &&
            _trackVisualsByFile.TryGetValue(seg.VideoFilePath, out var v))
            return v;

        bool isPrimary = model.PrimaryVideoFilePath is null ||
            string.Equals(seg.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase);
        if (isPrimary)
        {
            return new SegmentTrackVisual
            {
                Cursor = model.CursorData,
                MouseToVideoOffsetSeconds = model.MouseToVideoOffsetSeconds,
                SystemWaveform = model.SystemAudioWaveformSamples,
                MicWaveform = model.MicAudioWaveformSamples,
                WaveformDurationSeconds = model.Duration.TotalSeconds,
            };
        }
        return null;
    }

    /// <summary>Draws one segment's cursor path + click dots within its output range.</summary>
    private void DrawSegmentCursor(CanvasDrawingSession ds, CanvasControl sender,
        VideoSegment seg, SegmentTrackVisual visual, float w, float h)
    {
        var cursor = visual.Cursor!;
        float segX1 = (float)TimeToX(seg.Start);
        float segX2 = (float)TimeToX(seg.End);
        if (segX2 < 0 || segX1 > w) return;

        // Normalize within this segment's coordinate spread.
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var s in cursor.Samples)
        {
            if (s.X < minX) minX = s.X; if (s.X > maxX) maxX = s.X;
            if (s.Y < minY) minY = s.Y; if (s.Y > maxY) maxY = s.Y;
        }
        int rangeX = Math.Max(1, maxX - minX);
        int rangeY = Math.Max(1, maxY - minY);
        float margin = 4f;
        float drawHeight = h - margin * 2;
        double tickFreq = cursor.TickFrequency > 0 ? cursor.TickFrequency : 1.0;
        long startTicks = cursor.StartTimestampTicks;
        double offset = visual.MouseToVideoOffsetSeconds;

        using var xPath = new CanvasPathBuilder(sender);
        using var yPath = new CanvasPathBuilder(sender);
        bool xStarted = false, yStarted = false;

        foreach (var sample in cursor.Samples)
        {
            double fileVideoSec = (sample.TimestampTicks - startTicks) / tickFreq - offset;
            double x = SegmentVideoTimeToX(seg, fileVideoSec);
            if (double.IsNaN(x)) continue;
            float px = (float)x;

            float normX = (float)(sample.X - minX) / rangeX;
            float normY = (float)(sample.Y - minY) / rangeY;
            float yPosX = margin + (1f - normX) * drawHeight;
            float yPosY = margin + normY * drawHeight;

            if (!xStarted) { xPath.BeginFigure(px, yPosX); xStarted = true; }
            else xPath.AddLine(px, yPosX);
            if (!yStarted) { yPath.BeginFigure(px, yPosY); yStarted = true; }
            else yPath.AddLine(px, yPosY);
        }

        if (xStarted)
        {
            xPath.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(xPath);
            ds.DrawGeometry(g, CursorPathXColor, 1.2f);
        }
        if (yStarted)
        {
            yPath.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(yPath);
            ds.DrawGeometry(g, CursorPathYColor, 1.2f);
        }

        foreach (var click in cursor.Clicks)
        {
            if (!click.IsDown) continue;
            double fileVideoSec = (click.TimestampTicks - startTicks) / tickFreq - offset;
            double x = SegmentVideoTimeToX(seg, fileVideoSec);
            if (double.IsNaN(x)) continue;

            float normY = (float)(click.Y - minY) / rangeY;
            float cy = margin + normY * drawHeight;
            ds.FillCircle((float)x, cy, 3.5f, CursorClickColor);
            ds.DrawCircle((float)x, cy, 3.5f, ClickStrokeColor, 1f);
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

        // Segment-based timeline (video + text slides): select / move / ripple-trim.
        if (model.Segments.Count > 0)
        {
            VideoTrack_SegmentPressed(model, canvas, pos.X, e);
            return;
        }

        // ── Legacy clip / whole-timeline trim path ──
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

    /// <summary>
    /// Handles a pointer press on the segment-based primary track: hit-tests the
    /// segment under the cursor and begins a select / move / trim gesture.
    /// </summary>
    private void VideoTrack_SegmentPressed(TimelineModel model, CanvasControl canvas, double x, PointerRoutedEventArgs e)
    {
        var target = HitTestSegment(model, x, out var segId);

        if (segId is null)
        {
            // Empty space → deselect and scrub the playhead.
            if (_selectedSegmentId is not null)
            {
                _selectedSegmentId = null;
                SegmentSelected?.Invoke(this, null);
            }
            ClearClipSelection();
            PlayheadPosition = XToTime(x);
            _dragMode = DragMode.Playhead;
            canvas.CapturePointer(e.Pointer);
            return;
        }

        var segment = model.Segments.First(s => s.Id == segId);

        // Select the hit segment.
        if (_selectedSegmentId != segId)
        {
            _selectedSegmentId = segId;
            SegmentSelected?.Invoke(this, segId);
        }
        ClearClipSelection();

        _draggedSegmentId = segId;
        _segmentDragStartX = x;
        _segmentDragCurrentX = x;
        _segmentDragMoved = false;
        _segmentDragOriginalStart = segment.Start;
        _segmentDragOriginalDuration = segment.Duration;
        _segmentSnapGuideX = double.NaN;
        _segmentDropIndicatorX = double.NaN;

        _dragMode = target switch
        {
            SegmentHitTarget.LeftEdge => DragMode.SegmentLeftEdge,
            SegmentHitTarget.RightEdge => DragMode.SegmentRightEdge,
            _ => DragMode.SegmentBody,
        };

        if (_dragMode == DragMode.SegmentBody)
            PlayheadPosition = XToTime(x);

        SetCursor(target is SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
            ? InputSystemCursorShape.SizeWestEast
            : InputSystemCursorShape.SizeAll);

        canvas.CapturePointer(e.Pointer);
        InvalidateAll();
    }

    private void VideoTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null || sender is not CanvasControl canvas) return;

        var pos = e.GetCurrentPoint(canvas).Position;

        // Segment-based timeline interactions.
        if (model.Segments.Count > 0)
        {
            VideoTrack_SegmentMoved(model, canvas, pos.X);
            return;
        }

        // ── Legacy clip / whole-timeline trim path ──
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

    /// <summary>Handles pointer movement during a segment select / move / trim gesture.</summary>
    private void VideoTrack_SegmentMoved(TimelineModel model, CanvasControl canvas, double x)
    {
        double clampedX = Math.Clamp(x, 0, canvas.ActualWidth);
        bool snap = !IsAltDown();

        switch (_dragMode)
        {
            case DragMode.SegmentLeftEdge:
            case DragMode.SegmentRightEdge:
                _segmentDragCurrentX = SnapX(model, clampedX, _draggedSegmentId, snap);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateAll();
                break;

            case DragMode.SegmentBody:
                if (!_segmentDragMoved && Math.Abs(x - _segmentDragStartX) < SegmentMoveThreshold)
                {
                    // Still a click — scrub the playhead.
                    PlayheadPosition = XToTime(x);
                    break;
                }
                _segmentDragMoved = true;
                SetCursor(InputSystemCursorShape.SizeAll);

                // Snap the dragged segment's projected left edge to nearby boundaries.
                double deltaX = clampedX - _segmentDragStartX;
                double leftX = TimeToX(_segmentDragOriginalStart) + deltaX;
                double snappedLeftX = SnapX(model, leftX, _draggedSegmentId, snap);
                _segmentDragCurrentX = _segmentDragStartX + (snappedLeftX - TimeToX(_segmentDragOriginalStart));

                _segmentDropIndicatorX = ComputeDropIndicatorX(model, snappedLeftX);
                InvalidateAll();
                break;

            case DragMode.None:
                var target = HitTestSegment(model, x, out _);
                SetCursor(target switch
                {
                    SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge => InputSystemCursorShape.SizeWestEast,
                    SegmentHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;

            case DragMode.Playhead:
                PlayheadPosition = XToTime(x);
                break;
        }
    }

    private void VideoTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (sender is not CanvasControl canvas)
        {
            _dragMode = DragMode.None;
            return;
        }

        if (model is not null && model.Segments.Count > 0 && _draggedSegmentId is { } draggedId)
        {
            VideoTrack_SegmentReleased(model, draggedId);
        }

        _draggedSegmentId = null;
        _segmentDragStartX = double.NaN;
        _segmentDragCurrentX = double.NaN;
        _segmentDragMoved = false;
        _segmentSnapGuideX = double.NaN;
        _segmentDropIndicatorX = double.NaN;
        _dragMode = DragMode.None;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        InvalidateAll();
    }

    /// <summary>Commits the segment move / trim gesture by raising the appropriate event.</summary>
    private void VideoTrack_SegmentReleased(TimelineModel model, string draggedId)
    {
        switch (_dragMode)
        {
            case DragMode.SegmentRightEdge:
            {
                var newRight = XToTime(_segmentDragCurrentX);
                var newDuration = newRight - _segmentDragOriginalStart;
                if (newDuration < TrimSegmentEdgeOperation.MinDuration)
                    newDuration = TrimSegmentEdgeOperation.MinDuration;
                if (Math.Abs((newDuration - _segmentDragOriginalDuration).TotalMilliseconds) > 1)
                    SegmentTrimRequested?.Invoke(this, (draggedId, false, newDuration));
                break;
            }

            case DragMode.SegmentLeftEdge:
            {
                var origEnd = _segmentDragOriginalStart + _segmentDragOriginalDuration;
                var newLeft = XToTime(_segmentDragCurrentX);
                var newDuration = origEnd - newLeft;
                if (newDuration < TrimSegmentEdgeOperation.MinDuration)
                    newDuration = TrimSegmentEdgeOperation.MinDuration;
                if (Math.Abs((newDuration - _segmentDragOriginalDuration).TotalMilliseconds) > 1)
                    SegmentTrimRequested?.Invoke(this, (draggedId, true, newDuration));
                break;
            }

            case DragMode.SegmentBody when _segmentDragMoved:
            {
                double deltaX = _segmentDragCurrentX - _segmentDragStartX;
                var dropCenter = _segmentDragOriginalStart + _segmentDragOriginalDuration / 2
                    + (XToTime(_segmentDragStartX + deltaX) - XToTime(_segmentDragStartX));

                int targetIndex = ComputeMoveTargetIndex(model, draggedId, dropCenter);
                int fromIndex = model.Segments.FindIndex(s => s.Id == draggedId);
                if (targetIndex != fromIndex && targetIndex != fromIndex + 1)
                    SegmentMoveRequested?.Invoke(this, (draggedId, targetIndex));
                break;
            }
        }
    }

    /// <summary>
    /// Determines which segment (and which part of it — body or trim edge) is under
    /// the given X coordinate on the primary track.
    /// </summary>
    private SegmentHitTarget HitTestSegment(TimelineModel model, double x, out string? segmentId)
    {
        segmentId = null;
        foreach (var seg in model.Segments)
        {
            double x1 = TimeToX(seg.Start);
            double x2 = TimeToX(seg.End);
            if (x < x1 || x > x2) continue;

            segmentId = seg.Id;
            if (x - x1 <= SegmentEdgeHitWidth) return SegmentHitTarget.LeftEdge;
            if (x2 - x <= SegmentEdgeHitWidth) return SegmentHitTarget.RightEdge;
            return SegmentHitTarget.Body;
        }
        return SegmentHitTarget.None;
    }

    /// <summary>
    /// Snaps an X coordinate to nearby snap targets (the playhead and segment
    /// boundaries, excluding the dragged segment's own edges). Sets
    /// <see cref="_segmentSnapGuideX"/> for the snap guide line. Returns the
    /// original X (and clears the guide) when snapping is disabled or out of range.
    /// </summary>
    private double SnapX(TimelineModel model, double x, string? excludeSegmentId, bool enabled)
    {
        _segmentSnapGuideX = double.NaN;
        if (!enabled) return x;

        double best = x;
        double bestDist = SegmentSnapThreshold;

        void Consider(double candidate)
        {
            double dist = Math.Abs(candidate - x);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        Consider(TimeToX(PlayheadPosition));
        foreach (var seg in model.Segments)
        {
            if (seg.Id == excludeSegmentId) continue;
            Consider(TimeToX(seg.Start));
            Consider(TimeToX(seg.End));
        }

        if (bestDist < SegmentSnapThreshold && Math.Abs(best - x) > 0.001)
        {
            _segmentSnapGuideX = best;
            return best;
        }
        return x;
    }

    /// <summary>
    /// Computes the X of the drop indicator (the boundary where the dragged segment
    /// would land) given the dragged segment's projected left edge.
    /// </summary>
    private double ComputeDropIndicatorX(TimelineModel model, double leftX)
    {
        var leftTime = XToTime(leftX);
        var dropCenter = leftTime + _segmentDragOriginalDuration / 2;
        int targetIndex = ComputeMoveTargetIndex(model, _draggedSegmentId, dropCenter);

        // The indicator sits at the start of the segment now occupying targetIndex,
        // or at the end of the timeline when appending.
        if (targetIndex >= model.Segments.Count)
            return TimeToX(model.TotalSegmentsDuration);
        return TimeToX(model.Segments[targetIndex].Start);
    }

    /// <summary>
    /// Computes the target insertion index (in original-list coordinates, as expected
    /// by <see cref="MoveSegmentOperation"/>) for a segment dropped at <paramref name="dropCenter"/>.
    /// </summary>
    private static int ComputeMoveTargetIndex(TimelineModel model, string? draggedId, TimeSpan dropCenter)
    {
        for (int i = 0; i < model.Segments.Count; i++)
        {
            var seg = model.Segments[i];
            if (seg.Id == draggedId) continue;
            var mid = seg.Start + seg.Duration / 2;
            if (mid > dropCenter) return i;
        }
        return model.Segments.Count;
    }

    private static bool IsAltDown()
    {
        try
        {
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
            return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────── Camera Track ───────────────────────────
    // Camera segments are positioned in SOURCE time (like zoom), mapped to X via
    // SourceTimeToX so they stay aligned with the recording after reorder/trim.

    private const float CameraSegmentVerticalPadding = 6f;

    private string? _selectedCameraSegmentId;
    private double _cameraDragStartX = double.NaN;
    private double _cameraDragCurrentX = double.NaN;
    private TimeSpan _cameraDragOriginalStart;
    private TimeSpan _cameraDragOriginalEnd;
    private bool _cameraCreateActive;
    private TimeSpan _cameraCreateStart;
    private TimeSpan _cameraCreateEnd;

    /// <summary>Id of the selected camera segment, or null.</summary>
    public string? SelectedCameraSegmentId
    {
        get => _selectedCameraSegmentId;
        set { _selectedCameraSegmentId = value; CameraTrackCanvas?.Invalidate(); }
    }

    public void ClearCameraSelection()
    {
        if (_selectedCameraSegmentId is not null)
        {
            _selectedCameraSegmentId = null;
            CameraSegmentSelected?.Invoke(this, null);
            CameraTrackCanvas?.Invalidate();
        }
    }

    public event EventHandler<string?>? CameraSegmentSelected;
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? CameraSegmentCreated;
    public event EventHandler<(string Id, TimeSpan NewStart)>? CameraSegmentMoved;
    public event EventHandler<(string Id, bool IsStartEdge, TimeSpan NewEdgeTime)>? CameraSegmentResized;
    public event EventHandler<string>? CameraSegmentRemoveRequested;

    private void CameraTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(ZoomTrackBackground);
        if (model is null || model.DisplayDuration.TotalSeconds <= 0) return;

        var fill = Color.FromArgb(230, 76, 175, 80);          // green
        var fillSelected = Color.FromArgb(245, 110, 215, 115);
        var fillDisabled = Color.FromArgb(120, 120, 130, 120);
        var border = Color.FromArgb(255, 56, 142, 60);
        var borderSelected = Color.FromArgb(255, 200, 255, 200);
        var handleColor = Color.FromArgb(255, 255, 255, 255);
        var textColor = Color.FromArgb(255, 255, 255, 255);

        // Per-segment camera presence: each video segment that has a webcam recording
        // shows a faint bar across its own output range, so appended recordings'
        // camera availability is visible and moves with the segment. User-created
        // CameraSegments (clips) are drawn on top.
        if (model.Segments.Count > 0)
        {
            var presenceFill = Color.FromArgb(70, 76, 175, 80);
            var presenceBorder = Color.FromArgb(140, 56, 142, 60);
            float py = CameraSegmentVerticalPadding;
            float ph = h - CameraSegmentVerticalPadding * 2;

            foreach (var seg in model.Segments.OfType<VideoSegment>())
            {
                bool hasCam = !string.IsNullOrEmpty(seg.WebcamFilePath) ||
                    (ResolveTrackVisual(seg, model)?.HasCamera ?? false);
                if (!hasCam) continue;

                float sx1 = (float)TimeToX(seg.Start);
                float sx2 = (float)TimeToX(seg.End);
                if (sx2 < 0 || sx1 > w) continue;
                float sw = Math.Max(2, sx2 - sx1);

                using var pr = CanvasGeometry.CreateRoundedRectangle(ds, sx1, py, sw, ph, 4, 4);
                ds.FillGeometry(pr, presenceFill);
                ds.DrawGeometry(pr, presenceBorder, 1f);
                if (sw > 44)
                    ds.DrawText("Camera", sx1 + 6, py + ph / 2 - 7, Color.FromArgb(200, 255, 255, 255),
                        new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                        {
                            FontSize = 11,
                            FontFamily = "Segoe UI",
                        });
            }
        }

        foreach (var seg in model.CameraSegments)
        {
            float x1 = (float)GetCameraSegmentStartX(seg);
            float x2 = (float)GetCameraSegmentEndX(seg);
            if (x2 < 0 || x1 > w) continue;

            float segW = Math.Max(2, x2 - x1);
            float segY = CameraSegmentVerticalPadding;
            float segH = h - CameraSegmentVerticalPadding * 2;
            bool isSelected = seg.Id == _selectedCameraSegmentId;

            using var rect = CanvasGeometry.CreateRoundedRectangle(ds, x1, segY, segW, segH, 4, 4);
            ds.FillGeometry(rect, !seg.Enabled ? fillDisabled : isSelected ? fillSelected : fill);
            ds.DrawGeometry(rect, isSelected ? borderSelected : border, isSelected ? 1.5f : 1f);

            if (segW > 36)
            {
                string label = seg.Enabled ? "Camera" : "Camera (off)";
                ds.DrawText(label, x1 + 6, segY + segH / 2 - 7, textColor,
                    new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                    {
                        FontSize = 11,
                        FontFamily = "Segoe UI",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
            }

            if (isSelected)
            {
                float handleW = 3, handleH = segH * 0.5f, handleY = segY + (segH - handleH) / 2;
                ds.FillRoundedRectangle(x1 + 1, handleY, handleW, handleH, 1, 1, handleColor);
                ds.FillRoundedRectangle(x2 - handleW - 1, handleY, handleW, handleH, 1, 1, handleColor);
            }
        }

        // Create preview
        if (_cameraCreateActive && _dragMode == DragMode.CameraSegmentCreate)
        {
            float cx1 = (float)SourceTimeToX(_cameraCreateStart < _cameraCreateEnd ? _cameraCreateStart : _cameraCreateEnd);
            float cx2 = (float)SourceTimeToX(_cameraCreateStart < _cameraCreateEnd ? _cameraCreateEnd : _cameraCreateStart);
            float cw = Math.Max(2, cx2 - cx1);
            float cy = CameraSegmentVerticalPadding;
            float ch = h - CameraSegmentVerticalPadding * 2;
            using var preview = CanvasGeometry.CreateRoundedRectangle(ds, cx1, cy, cw, ch, 4, 4);
            ds.FillGeometry(preview, Color.FromArgb(120, 76, 175, 80));
            ds.DrawGeometry(preview, border, 1f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }
    }

    private double GetCameraSegmentStartX(CameraSegment seg)
    {
        if (seg.Id == _selectedCameraSegmentId && !double.IsNaN(_cameraDragCurrentX))
        {
            if (_dragMode == DragMode.CameraSegmentBody)
                return SourceTimeToX(_cameraDragOriginalStart) + (_cameraDragCurrentX - _cameraDragStartX);
            if (_dragMode == DragMode.CameraSegmentLeftEdge)
                return _cameraDragCurrentX;
        }
        return SourceTimeToX(seg.Start);
    }

    private double GetCameraSegmentEndX(CameraSegment seg)
    {
        if (seg.Id == _selectedCameraSegmentId && !double.IsNaN(_cameraDragCurrentX))
        {
            if (_dragMode == DragMode.CameraSegmentBody)
                return SourceTimeToX(_cameraDragOriginalEnd) + (_cameraDragCurrentX - _cameraDragStartX);
            if (_dragMode == DragMode.CameraSegmentRightEdge)
                return _cameraDragCurrentX;
        }
        return SourceTimeToX(seg.End);
    }

    private (string? Id, SegmentHitTarget Target) HitTestCameraSegment(double posX, double posY)
    {
        var model = Model;
        if (model is null) return (null, SegmentHitTarget.None);

        float h = (float)CameraTrackCanvas.ActualHeight;
        float segY = CameraSegmentVerticalPadding;
        float segH = h - CameraSegmentVerticalPadding * 2;
        if (posY < segY || posY > segY + segH) return (null, SegmentHitTarget.None);

        for (int i = model.CameraSegments.Count - 1; i >= 0; i--)
        {
            var seg = model.CameraSegments[i];
            float x1 = (float)SourceTimeToX(seg.Start);
            float x2 = (float)SourceTimeToX(seg.End);
            if (posX < x1 || posX > x2) continue;

            if (posX - x1 <= SegmentEdgeHitWidth) return (seg.Id, SegmentHitTarget.LeftEdge);
            if (x2 - posX <= SegmentEdgeHitWidth) return (seg.Id, SegmentHitTarget.RightEdge);
            return (seg.Id, SegmentHitTarget.Body);
        }
        return (null, SegmentHitTarget.None);
    }

    private void CameraTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;
        var (hitId, target) = HitTestCameraSegment(pos.X, pos.Y);

        if (hitId is not null)
        {
            SelectedCameraSegmentId = hitId;
            CameraSegmentSelected?.Invoke(this, hitId);

            var seg = Model?.CameraSegments.FirstOrDefault(s => s.Id == hitId);
            if (seg is null) return;

            _cameraDragStartX = pos.X;
            _cameraDragCurrentX = pos.X;
            _cameraDragOriginalStart = seg.Start;
            _cameraDragOriginalEnd = seg.End;

            _dragMode = target switch
            {
                SegmentHitTarget.LeftEdge => DragMode.CameraSegmentLeftEdge,
                SegmentHitTarget.RightEdge => DragMode.CameraSegmentRightEdge,
                _ => DragMode.CameraSegmentBody,
            };
            SetCursor(target is SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
                ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
            canvas.CapturePointer(e.Pointer);
        }
        else
        {
            if (_selectedCameraSegmentId is not null)
            {
                SelectedCameraSegmentId = null;
                CameraSegmentSelected?.Invoke(this, null);
            }
            PlayheadPosition = XToTime(pos.X);

            var start = XToPrimarySourceTime(pos.X);
            if (start is null)
            {
                // No primary video on the timeline — nothing to attach a camera
                // segment to. Reject the gesture, leaving no transient drag state.
                _dragMode = DragMode.None;
                return;
            }

            _cameraDragStartX = pos.X;
            _cameraDragCurrentX = pos.X;
            _cameraCreateActive = false;
            _cameraCreateStart = start.Value;
            _dragMode = DragMode.CameraSegmentCreate;
            canvas.CapturePointer(e.Pointer);
        }
    }

    private void CameraTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;

        switch (_dragMode)
        {
            case DragMode.CameraSegmentBody:
                _cameraDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeAll);
                InvalidateAll();
                break;
            case DragMode.CameraSegmentLeftEdge:
            case DragMode.CameraSegmentRightEdge:
                _cameraDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateAll();
                break;
            case DragMode.CameraSegmentCreate:
                if (Math.Abs(pos.X - _cameraDragStartX) >= ZoomCreateDragThreshold)
                {
                    var end = XToPrimarySourceTime(pos.X);
                    if (end is not null)
                    {
                        _cameraCreateActive = true;
                        _cameraCreateEnd = end.Value;
                        InvalidateAll();
                    }
                }
                else
                {
                    PlayheadPosition = XToTime(pos.X);
                }
                break;
            case DragMode.None:
                var (_, target) = HitTestCameraSegment(pos.X, pos.Y);
                SetCursor(target switch
                {
                    SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge => InputSystemCursorShape.SizeWestEast,
                    SegmentHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;
        }
    }

    private void CameraTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;

        switch (_dragMode)
        {
            case DragMode.CameraSegmentBody when _selectedCameraSegmentId is not null:
            {
                double deltaX = _cameraDragCurrentX - _cameraDragStartX;
                if (Math.Abs(deltaX) > 1)
                {
                    var startTime = XToPrimarySourceTime(_cameraDragStartX);
                    var movedTime = XToPrimarySourceTime(_cameraDragStartX + deltaX);
                    if (startTime is not null && movedTime is not null)
                    {
                        var newStart = _cameraDragOriginalStart + (movedTime.Value - startTime.Value);
                        if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                        CameraSegmentMoved?.Invoke(this, (_selectedCameraSegmentId, newStart));
                    }
                    // else: unmappable — reject the move, leave the segment in place.
                }
                break;
            }
            case DragMode.CameraSegmentLeftEdge when _selectedCameraSegmentId is not null:
            {
                var newEdge = XToPrimarySourceTime(Math.Clamp(_cameraDragCurrentX, 0, canvas.ActualWidth));
                if (newEdge is not null && newEdge.Value != _cameraDragOriginalStart)
                    CameraSegmentResized?.Invoke(this, (_selectedCameraSegmentId, true, newEdge.Value));
                break;
            }
            case DragMode.CameraSegmentRightEdge when _selectedCameraSegmentId is not null:
            {
                var newEdge = XToPrimarySourceTime(Math.Clamp(_cameraDragCurrentX, 0, canvas.ActualWidth));
                if (newEdge is not null && newEdge.Value != _cameraDragOriginalEnd)
                    CameraSegmentResized?.Invoke(this, (_selectedCameraSegmentId, false, newEdge.Value));
                break;
            }
            case DragMode.CameraSegmentCreate when _cameraCreateActive:
            {
                var start = _cameraCreateStart < _cameraCreateEnd ? _cameraCreateStart : _cameraCreateEnd;
                var end = _cameraCreateStart < _cameraCreateEnd ? _cameraCreateEnd : _cameraCreateStart;
                if ((end - start) >= TrimCameraSegmentOperation.MinDuration)
                    CameraSegmentCreated?.Invoke(this, (start, end));
                _cameraCreateActive = false;
                break;
            }
        }

        _cameraDragStartX = double.NaN;
        _cameraDragCurrentX = double.NaN;
        _dragMode = DragMode.None;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        InvalidateAll();
    }

    private void CameraTrack_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);
        var (hitId, _) = HitTestCameraSegment(pos.X, pos.Y);
        if (hitId is not null)
        {
            SelectedCameraSegmentId = hitId;
            CameraSegmentSelected?.Invoke(this, hitId);
            CameraSegmentRemoveRequested?.Invoke(this, hitId);
        }
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
