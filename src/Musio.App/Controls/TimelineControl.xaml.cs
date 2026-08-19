using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Models;
using Musio.Core.Processing;
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

    private enum DragMode { None, Playhead, TrimStart, TrimEnd, ZoomSegmentBody, ZoomSegmentLeftEdge, ZoomSegmentRightEdge, ZoomSegmentCreate, SegmentBody, SegmentLeftEdge, SegmentRightEdge, TextSlideWindowInEdge, TextSlideWindowOutEdge, CameraSegmentBody, CameraSegmentLeftEdge, CameraSegmentRightEdge, CameraSegmentCreate, TextOverlayBody, TextOverlayLeftEdge, TextOverlayRightEdge, TextOverlayCreate, InsertedAudioBody, InsertedAudioLeftEdge, InsertedAudioRightEdge }
    private DragMode _dragMode = DragMode.None;

    // ── Primary-track (video / text slide) segment drag state ──
    private const double SegmentEdgeHitWidth = 8.0;     // px hit zone for trim edges
    private const double SegmentMoveThreshold = 5.0;    // px before a body press becomes a move
    private const double SegmentSnapThreshold = 8.0;    // px snapping distance while dragging

    private string? _draggedSegmentId;
    private double _segmentDragStartX = double.NaN;
    /// <summary>
    /// Pointer Y where the segment drag began. The target lane is derived from movement
    /// relative to this rather than from the pointer's absolute Y, because revealing the
    /// drop-hint lane changes the canvas height and therefore shifts every row underneath
    /// the (stationary) cursor mid-gesture.
    /// </summary>
    private double _segmentDragStartY = double.NaN;
    private double _segmentDragCurrentX = double.NaN;
    private bool _segmentDragMoved;
    private TimeSpan _segmentDragOriginalStart;
    private TimeSpan _segmentDragOriginalDuration;
    private int _segmentDragOriginalTrackIndex;
    private int _segmentDragCurrentTrackIndex;
    private double _segmentSnapGuideX = double.NaN;     // NaN = no snap guide drawn
    private double _segmentDropIndicatorX = double.NaN; // NaN = no drop indicator drawn
    private double _textSlideWindowDragCurrentX = double.NaN;
    private TimeSpan _textSlideWindowOriginalInStart;
    private TimeSpan _textSlideWindowOriginalOutEnd;

    /// <summary>Raised when a primary-track segment should be moved to a new index (commit).</summary>
    public event EventHandler<(string Id, int TargetIndex)>? SegmentMoveRequested;

    /// <summary>
    /// Raised when a full-frame segment is dropped on a different video lane or an overlay
    /// lane is moved in absolute time; the host owns the undoable model operation.
    /// </summary>
    public event EventHandler<SegmentTrackMoveEventArgs>? SegmentTrackMoveRequested;

    /// <summary>Raised when a primary-track segment edge should be ripple-trimmed (commit).</summary>
    public event EventHandler<(string Id, bool FromStart, TimeSpan NewDuration)>? SegmentTrimRequested;

    /// <summary>
    /// Raised when a playback speed is chosen from a video segment's right-click menu.
    /// Speed lives behind that menu rather than on a selection-triggered toolbar control
    /// because it re-times the footage under everything else the user has placed — it is a
    /// power-user edit that should be found deliberately, not offered to everyone who
    /// clicks a clip.
    /// </summary>
    public event EventHandler<(string Id, double Speed)>? SegmentSpeedChangeRequested;

    /// <summary>
    /// Raised when an audio handling mode is chosen from a speed-adjusted video segment's
    /// right-click menu (see <see cref="SegmentAudioMode"/>).
    /// </summary>
    public event EventHandler<(string Id, SegmentAudioMode Mode)>? SegmentAudioModeChangeRequested;

    /// <summary>
    /// Raised when "Detach audio" is chosen for a video segment: its recorded captures should
    /// become free-standing, movable timeline blocks and the segment itself should go silent.
    /// </summary>
    public event EventHandler<string>? SegmentAudioDetachRequested;

    /// <summary>
    /// Raised when "Re-attach audio" is chosen: the blocks detached from that segment should
    /// be removed and the segment should play its own audio again.
    /// </summary>
    public event EventHandler<string>? SegmentAudioReattachRequested;

    /// <summary>Raised when "Split at Playhead" is chosen from a segment's right-click menu.</summary>
    public event EventHandler? SegmentSplitRequested;

    /// <summary>Raised when "Delete Segment" is chosen from a segment's right-click menu.</summary>
    public event EventHandler<string>? SegmentDeleteRequested;

    /// <summary>
    /// Raised when a text slide's inner animation window is edited from the timeline so the
    /// page can keep preview, properties and undo in one authoritative transaction.
    /// </summary>
    public event EventHandler<TextSlideWindowEventArgs>? TextSlideWindowChanged;

    private enum SegmentHitTarget { None, Body, LeftEdge, RightEdge, TextWindowInEdge, TextWindowOutEdge }

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
    private Color SpeedBadgeFillColor;
    private Color SpeedBadgeForegroundColor;
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
    private Color ZoomSegmentLinkedConnector;
    private Color ZoomSegmentTextColor;
    private Color AudioTrackBackground;
    private Color AudioPlaceholderColor;
    private Color AudioWaveformColor;
    private Color AudioEnvelopeColor;
    private Color MicWaveformColor;
    private Color MicEnvelopeColor;
    private Color PlayheadColor;
    private Color CutLineColor;
    // ── Transition boundary chips — one fill per visual family (see GetTransitionChipVisual) ──
    private Color TransitionChipDissolveColor;
    private Color TransitionChipSlidePushColor;
    private Color TransitionChipWipeColor;
    private Color TransitionChipStylizedColor;
    private Color TransitionChipGlyphColor;
    private Color TransitionChipEmptyFill;
    private Color TransitionChipEmptyBorder;
    private Color TransitionChipEmptyGlyphColor;
    private Color TransitionChipHardCutFill;
    private Color TransitionChipHardCutBorder;
    private Color TransitionChipHardCutGlyphColor;
    private Color CursorTrackBackground;
    private Color CursorPathXColor;
    private Color CursorPathYColor;
    private Color CursorClickColor;
    private Color SpeedLabelTextColor;
    private Color TrackCenterLineColor;
    private Color TrackEmptyLineColor;
    private Color TrackHintTextColor;
    private Color EmptyPlaceholderFill;
    private Color EmptyPlaceholderStroke;
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

        // A track canvas recovers from a GPU device loss on its own — silently, with no
        // exception and without necessarily raising DeviceLost on the shared device the
        // editor watches. The thumbnails and waveform bitmaps cached here belong to the dead
        // device once that happens, so without this subscription every track simply stops
        // drawing and never comes back.
        foreach (var canvas in TrackCanvases())
        {
            if (canvas is not null)
                canvas.CreateResources += TrackCanvas_CreateResources;
        }

        // The playhead offset is derived from the ruler canvas width, and the Win2D canvases
        // repaint themselves on resize — so a resize only needs the overlay repositioned.
        Loaded += (_, _) => UpdatePlayheadVisual();
        TimeRulerCanvas.SizeChanged += (_, _) => UpdatePlayheadVisual();

        // The chip hover machinery owns a DispatcherTimer and a native text format; neither is
        // tied to a canvas's own lifetime, so they are released explicitly when the control
        // leaves the tree (e.g. navigating away from the editor mid-hover) rather than left
        // ticking against a control that is no longer shown.
        Unloaded += (_, _) =>
        {
            _hoveredTransitionChipId = null;
            HideTransitionChipToolTip();
            _transitionChipHoverTimer = null;
            _transitionChipGlyphFormat?.Dispose();
            _transitionChipGlyphFormat = null;

            // The drop-hint reveal timer is the same class of leak: it ticks a layout pass and
            // a canvas invalidation, so it must not outlive the control that owns them.
            _hintLaneRevealTimer?.Stop();
            _hintLaneRevealTimer = null;
            _hintLaneReveal = 0;
            _hintLaneRevealTarget = 0;
            _hintLaneArmed = false;
            _hintLaneLatched = false;
        };
    }

    /// <summary>
    /// Raised when a track canvas recreated its device after a GPU device loss. Every cached
    /// bitmap has already been dropped by the time this fires; the host is responsible for
    /// regenerating them.
    /// </summary>
    public event EventHandler? DeviceRecreated;

    private IEnumerable<CanvasControl?> TrackCanvases()
    {
        yield return TimeRulerCanvas;
        yield return VideoTrackCanvas;
        yield return CursorTrackCanvas;
        yield return ZoomTrackCanvas;
        yield return CameraTrackCanvas;
        yield return TextTrackCanvas;
        yield return AudioTrackCanvas;
        yield return MicTrackCanvas;
        yield return VoiceOverTrackCanvas;
        yield return MusicTrackCanvas;
    }

    private CanvasDevice? _lastRecoveredDevice;

    private void TrackCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (args.Reason != CanvasCreateResourcesReason.NewDevice) return;

        // All eight track canvases raise this for the same underlying loss when they share a
        // device, so keying on the replacement collapses that batch into one rebuild request.
        // When they each hold their own device the keying simply never matches and every
        // canvas requests a rebuild — harmless, because the device manager coalesces
        // concurrent requests into a single recovery pass either way.
        var device = sender.Device;
        if (device is not null && ReferenceEquals(device, _lastRecoveredDevice)) return;
        _lastRecoveredDevice = device;

        // These bitmaps were allocated on the dead device. They are cleared rather than
        // redrawn because nothing can draw them any more; the host regenerates them as part
        // of the recovery this request kicks off.
        ClearThumbnails();
        ClearSegmentTrackVisuals();
        Musio.Core.Diagnostics.DiagLog.Write(
            "Timeline", "track CanvasControl built a new device after loss; requesting rebuild");
        DeviceRecreated?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// Desaturates and fades a zoom-segment colour for a segment that is NOT the selected one.
    /// <para>
    /// Zoom segments legitimately overlap — that is what drives a camera handoff — and when two
    /// sit on top of each other in the same accent colour it is hard to tell which one the
    /// resize handles belong to. Draining most of the colour and roughly half the opacity out of
    /// the unselected ones lets the selected segment read as the foreground object without
    /// hiding its neighbours, which still need to be visible to be aimed at.
    /// </para>
    /// <para>
    /// Applied only while something is selected, so the track keeps its normal appearance at rest.
    /// </para>
    /// </summary>
    private static Color MutedZoomColor(Color c)
    {
        const float desaturation = 0.8f;
        const float fade = 0.45f;

        // Rec. 601 luma: matches how the eye weights the channels, so the muted colour keeps
        // the segment's apparent brightness instead of flattening light and dark ones together.
        float luma = (0.299f * c.R) + (0.587f * c.G) + (0.114f * c.B);
        byte Mix(byte channel) => (byte)Math.Clamp(channel + ((luma - channel) * desaturation), 0f, 255f);

        return Color.FromArgb((byte)(c.A * fade), Mix(c.R), Mix(c.G), Mix(c.B));
    }

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

        // ── Speed badge — deliberately NEUTRAL, not the status colors above. The badge
        //    sits on the video block itself, where a saturated fill reads as a status
        //    highlight (and the orange one was mistaken for a zoom marker). ──
        SpeedBadgeFillColor       = GetBrushColor("TimelineSpeedBadgeBrush", Color.FromArgb(224, 46, 46, 46));
        SpeedBadgeForegroundColor = GetBrushColor("OverlayForegroundBrush", Color.FromArgb(255, 255, 255, 255));

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
        ZoomSegmentLinkedConnector = WithAlpha(zoomLight, 190);
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

        // ── Transition chips — one family colour per visual grouping (see GetTransitionChipVisual) ──
        var dissolveBase      = isDark ? Color.FromArgb(255, 255, 179, 0) : Color.FromArgb(255, 200, 130, 0);   // amber — matched the legacy marker colour
        var slidePushBase     = isDark ? Color.FromArgb(255, 41, 121, 255) : Color.FromArgb(255, 25, 90, 200);  // blue
        var wipeBase          = isDark ? Color.FromArgb(255, 156, 39, 176) : Color.FromArgb(255, 120, 30, 140); // purple
        var stylizedBase      = isDark ? Color.FromArgb(255, 233, 30, 99) : Color.FromArgb(255, 190, 20, 80);   // pink/red
        TransitionChipDissolveColor  = WithAlpha(dissolveBase, 235);
        TransitionChipSlidePushColor = WithAlpha(slidePushBase, 235);
        TransitionChipWipeColor      = WithAlpha(wipeBase, 235);
        TransitionChipStylizedColor  = WithAlpha(stylizedBase, 235);
        TransitionChipGlyphColor     = Color.FromArgb(255, 255, 255, 255); // white glyph text reads on every family fill above
        // The unconfigured ("Automatic") chip is a real, clickable affordance, so it gets a
        // solid neutral slate fill rather than the near-invisible ControlFill/TextTertiary
        // treatment it used to have — that read as decorative and users did not discover it.
        // It stays deliberately desaturated so a configured boundary's family colour still
        // reads as "this one is set", but it is now plainly present against the filmstrip.
        var autoBase                 = isDark ? Color.FromArgb(255, 96, 104, 120) : Color.FromArgb(255, 118, 126, 142);
        TransitionChipEmptyFill      = WithAlpha(autoBase, 235);
        TransitionChipEmptyBorder    = isDark ? Color.FromArgb(255, 168, 178, 198) : Color.FromArgb(255, 74, 82, 96);
        TransitionChipEmptyGlyphColor = Color.FromArgb(255, 255, 255, 255);
        // An explicit hard cut reads as deliberately "off": darker and flatter than Automatic,
        // so the two are never mistaken for one another at a glance.
        var hardCutBase              = isDark ? Color.FromArgb(255, 44, 48, 56) : Color.FromArgb(255, 74, 80, 92);
        TransitionChipHardCutFill    = WithAlpha(hardCutBase, 235);
        TransitionChipHardCutBorder  = isDark ? Color.FromArgb(255, 120, 128, 144) : Color.FromArgb(255, 52, 58, 68);
        TransitionChipHardCutGlyphColor = isDark ? Color.FromArgb(255, 196, 204, 218) : Color.FromArgb(255, 244, 246, 250);

        // ── Text & lines — system ──
        SpeedLabelTextColor   = GetSystemBrushColor("TextFillColorPrimaryBrush", Color.FromArgb(255, 255, 255, 255));
        TrackCenterLineColor  = GetSystemBrushColor("DividerStrokeColorDefaultBrush", Color.FromArgb(100, 255, 255, 255));
        TrackEmptyLineColor   = GetSystemBrushColor("ControlStrokeColorDefaultBrush", Color.FromArgb(60, 255, 255, 255));
        TrackHintTextColor    = GetSystemBrushColor("TextFillColorTertiaryBrush", Color.FromArgb(140, 255, 255, 255));

        // ── Empty-state shadow segment — must read as an outline, never as a clip ──
        EmptyPlaceholderFill   = isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
        EmptyPlaceholderStroke = isDark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(80, 0, 0, 0);
    }

    /// <summary>
    /// Repaints every track canvas and re-positions the playhead overlay. Call this for
    /// changes that alter what the tracks draw (model/segment edits, zoom, scroll,
    /// selection, thumbnails, waveforms, theme) — never for a mere playhead move.
    /// </summary>
    public void InvalidateAllCanvases()
    {
        UpdateTrackVisibility();

        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        CameraTrackCanvas?.Invalidate();
        TextTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
        VoiceOverTrackCanvas?.Invalidate();
        MusicTrackCanvas?.Invalidate();
        // Durations / zoom / scroll may have changed with the tracks, which moves the
        // playhead's pixel position even when the time itself is unchanged.
        UpdatePlayheadVisual();
    }

    // --- Adaptive track visibility ---

    // Natural heights of the tracks that are only shown when the recording actually has
    // data for them. Kept here rather than read back from the RowDefinition because a
    // collapsed row's height is zeroed, so the original value would be lost.
    private const double CursorRowHeight = 40;
    private const double CameraRowHeight = 44;
    private const double AudioRowHeight = 40;
    private const double MicRowHeight = 40;

    // The inserted-audio lanes size themselves from their stacked sub-row count instead of a
    // fixed height — see LayoutInsertedAudioRows and InsertedAudioSubRowHeight.

    private const double BaseVideoTrackHeight = 80;
    private const double OverlayVideoTrackHeight = 44;

    /// <summary>
    /// Collapses the tracks that visualise recorded media the current project does not
    /// have — cursor, camera, system audio and microphone — so the timeline only spends
    /// vertical space on tracks with something to show. Video, zoom and text always stay
    /// visible: those are authoring surfaces the user creates content on (you drag on the
    /// zoom track to make a zoom), so hiding them when empty would hide the feature.
    /// A collapsed row is zero-height <em>and</em> its label/canvas are collapsed, so it
    /// cannot be hit-tested or draw a sliver.
    /// </summary>
    private void UpdateTrackVisibility()
    {
        UpdateVideoTrackHeight();

        // The XAML may not be realised yet when the model is assigned during construction.
        if (CursorRow is null || AudioRow is null) return;

        var model = Model;

        bool hasCursor = model is not null &&
            (model.CursorData?.Samples.Count > 0 ||
             _trackVisualsByFile.Values.Any(v => v.Cursor?.Samples.Count > 0));

        bool hasCamera = model is not null &&
            (model.CameraSegments.Count > 0 ||
             model.Segments.OfType<VideoSegment>().Any(s => !string.IsNullOrEmpty(s.WebcamFilePath)) ||
             _trackVisualsByFile.Values.Any(v => v.HasCamera));

        bool hasSystemAudio = model is not null &&
            (model.SystemAudioWaveformSamples is { Length: > 0 } ||
             _trackVisualsByFile.Values.Any(v => v.SystemWaveform is { Length: > 0 }) ||
             HasAudioFile(model, mic: false));

        bool hasMicAudio = model is not null &&
            (model.MicAudioWaveformSamples is { Length: > 0 } ||
             _trackVisualsByFile.Values.Any(v => v.MicWaveform is { Length: > 0 }) ||
             HasAudioFile(model, mic: true));

        ApplyTrackVisibility(CursorRow, CursorTrackLabel, CursorTrackCanvas, hasCursor, CursorRowHeight);
        ApplyTrackVisibility(CameraRow, CameraTrackLabel, CameraTrackCanvas, hasCamera, CameraRowHeight);
        ApplyTrackVisibility(AudioRow, AudioTrackLabel, AudioTrackCanvas, hasSystemAudio, AudioRowHeight);
        ApplyTrackVisibility(MicRow, MicTrackLabel, MicTrackCanvas, hasMicAudio, MicRowHeight);

        // Unlike the tracks above, these visualise what the user INSERTED rather than what
        // was recorded, so they are keyed off the lane items the host publishes. Each kind
        // gets its own lane so a voice-over and a music bed that overlap in time are still
        // independently grabbable — and each starts collapsed, since a project with no
        // audio of that kind must not pay a row for it. The height grows with the number of
        // stacked sub-rows the lane's blocks needed (see LayoutInsertedAudioRows).
        ApplyTrackVisibility(
            VoiceOverRow, VoiceOverTrackLabel, VoiceOverTrackCanvas,
            _insertedAudioTracks.Any(t => !t.IsMusic),
            _voiceSubRowCount * InsertedAudioSubRowHeight);
        ApplyTrackVisibility(
            MusicRow, MusicTrackLabel, MusicTrackCanvas,
            _insertedAudioTracks.Any(t => t.IsMusic),
            _musicSubRowCount * InsertedAudioSubRowHeight);
    }

    /// <summary>
    /// Sizes the video canvas to every full-frame lane instead of relying on a fixed grid
    /// height, so overlay tracks grow the control rather than being silently clipped.
    /// </summary>
    private void UpdateVideoTrackHeight()
    {
        if (VideoTrackCanvas is null) return;
        double height = VideoTrackHeight(Model);
        if (Math.Abs(VideoTrackCanvas.Height - height) > 0.1)
            VideoTrackCanvas.Height = height;
    }

    private double VideoTrackHeight(TimelineModel? model)
    {
        int used = Math.Max(1, model?.VideoTrackCount ?? 1);
        return BaseVideoTrackHeight + (used - 1) * OverlayVideoTrackHeight + HintLaneBandHeight;
    }

    // ── Transient overlay drop-hint lane ──
    //
    // The lane used to be snapped in and out as a whole extra 44px row, which made it flash
    // and the timeline stutter for three compounding reasons:
    //   1. the destination lane was rounded to the nearest row of drag travel, so the switch
    //      point sat on a knife edge and the sub-pixel jitter of a hand holding the mouse
    //      flipped the lane on and off many times a second;
    //   2. every flip instantly re-laid-out the whole timeline grid, teleporting the dragged
    //      block (and every track below it) by a full lane height;
    //   3. even with (1) and (2) fixed, the lane's presence still tracked the pointer's
    //      CURRENT lane, so the ordinary vertical drift of a hand positioning a clip
    //      horizontally kept opening and folding it away in the middle of one gesture.
    // ResolveDragTrackIndex's hysteresis fixes (1), the eased reveal fraction fixes (2), and
    // the latch below fixes (3): a gesture may grow the timeline ONCE, on the way in, and
    // shrink it ONCE, on release — never repeatedly while the user is still holding the clip.
    private const double HintLaneRevealDurationMs = 140;

    private double _hintLaneReveal;         // 0 = folded away, 1 = fully open
    private double _hintLaneRevealFrom;
    private double _hintLaneRevealTarget;
    private long _hintLaneRevealStartTicks;
    private DispatcherTimer? _hintLaneRevealTimer;

    /// <summary>
    /// Whether the drag is currently aiming AT the hint lane, latched separately from the drag
    /// state so the lane keeps its highlighted look for the whole fold-away. Reading the live
    /// drag index instead made the outline drop back to its idle grey on the release frame,
    /// one last colour pop at the end of the very gesture this animation is smoothing.
    /// </summary>
    private bool _hintLaneArmed;

    /// <summary>
    /// Set once the in-flight drag has reached the hint lane, and not cleared until the gesture
    /// ends. See <see cref="HintLaneRequested"/> for why the lane is held open rather than
    /// tracking the pointer.
    /// </summary>
    private bool _hintLaneLatched;

    /// <summary>
    /// True while the drag is currently aiming at the hint lane — i.e. releasing now would
    /// create the new overlay track. Drives only the lane's ARMED highlight; it must not drive
    /// the lane's existence (see <see cref="HintLaneRequested"/>).
    /// </summary>
    private bool ShowOverlayDropHint =>
        _dragMode == DragMode.SegmentBody
        && _segmentDragMoved
        && _segmentDragCurrentTrackIndex >= Math.Max(1, Model?.VideoTrackCount ?? 1);

    /// <summary>
    /// Whether the hint lane should be open. Deliberately LATCHED for the rest of the gesture
    /// once the drag has reached the lane, rather than following the pointer's current lane.
    /// </summary>
    /// <remarks>
    /// Hysteresis alone was not enough. Its dead band is necessarily about a third of a lane
    /// (~14px) — enough to absorb the jitter of a hand holding still, but nothing like the
    /// vertical drift of a hand sweeping a clip sideways across the timeline to position it.
    /// So the lane kept opening and folding mid-gesture: correct by the letter of "show the
    /// lane the pointer is in", useless as an affordance, and the source of the remaining
    /// churn even once each transition was individually smooth.
    /// <para>
    /// Latching makes the timeline's geometry CONSTANT for the whole drag, which is what the
    /// user is really asking for when they say the lane must stop coming and going. It costs
    /// nothing: the lane is empty, and whether the drop actually lands there is still decided
    /// entirely by <see cref="_segmentDragCurrentTrackIndex"/> and shown by the armed
    /// highlight. Crucially it also preserves the property the previous round added — a plain
    /// horizontal reorder still never reveals the lane at all, because the latch is only ever
    /// set by reaching for it.
    /// </para>
    /// </remarks>
    private bool HintLaneRequested =>
        _dragMode == DragMode.SegmentBody && _segmentDragMoved && _hintLaneLatched;

    /// <summary>
    /// Whether the hint lane currently occupies layout space — true while it is opening, open,
    /// or still folding away.
    /// </summary>
    private bool HintLaneVisible => _hintLaneReveal > 0.0005 || _hintLaneRevealTarget > 0;

    /// <summary>Height the hint lane occupies right now (0 = folded, 44 = fully open).</summary>
    private float HintLaneBandHeight =>
        HintLaneVisible
            ? (float)(Math.Clamp(_hintLaneReveal, 0, 1) * OverlayVideoTrackHeight)
            : 0f;

    /// <summary>
    /// Destination lane for the in-flight segment drag, derived from vertical travel since
    /// the grab so it is immune to the canvas resizing underneath the cursor. Allows reaching
    /// exactly one lane above the highest one currently in use — that is the lane the drop
    /// hint offers to create.
    /// </summary>
    /// <remarks>
    /// The travel is banded with HYSTERESIS rather than rounded to the nearest row: a
    /// neighbouring lane is only entered after <see cref="HintLaneEnterFraction"/> of a row of
    /// travel towards it, measured from the lane currently held. Plain rounding put the switch
    /// point exactly half a row out, so a hand holding still across that boundary flipped the
    /// destination lane — and with it the dragged block's row — continuously.
    /// </remarks>
    private int ResolveDragTrackIndex(TimelineModel model, double y)
    {
        if (double.IsNaN(_segmentDragStartY)) return _segmentDragOriginalTrackIndex;

        int used = Math.Max(1, model.VideoTrackCount);

        // Clamped so a fling far outside the track band can't spin the stepping loops below.
        double rows = Math.Clamp(
            (_segmentDragStartY - y) / OverlayVideoTrackHeight,
            -(used + 2),
            used + 2);

        int offset = _segmentDragCurrentTrackIndex - _segmentDragOriginalTrackIndex;
        while (rows >= offset + HintLaneEnterFraction) offset++;
        while (rows <= offset - HintLaneEnterFraction) offset--;

        return Math.Clamp(_segmentDragOriginalTrackIndex + offset, TimelineModel.BaseTrackIndex, used);
    }

    /// <summary>
    /// Fraction of a lane height the pointer must travel towards a neighbouring lane before it
    /// is adopted. Being &gt; 0.5 is what creates the dead band: leaving a lane needs the same
    /// travel back, so the switch points sit 0.5 of a lane (~22px) apart instead of coinciding.
    /// </summary>
    /// <remarks>
    /// Sized for a hand sweeping a clip sideways, not for a hand holding still. The first pass
    /// used 0.65 (a ~14px band), which stopped the jitter of a stationary pointer but not the
    /// vertical drift of an actual positioning gesture, so the drop target still flip-flopped
    /// while the user was moving the clip along the timeline.
    /// </remarks>
    private const double HintLaneEnterFraction = 0.75;

    /// <summary>
    /// Points the hint lane's reveal animation at the state <see cref="HintLaneRequested"/>
    /// now implies. Cheap to call on every pointer move: it returns immediately unless the
    /// target actually changed.
    /// </summary>
    /// <param name="animate">
    /// False snaps straight to the target. Used when a drop has just turned the hint lane into
    /// a real one — the real lane is exactly the same height, so animating the hint away would
    /// double the lane's height for a frame and then shrink it back.
    /// </param>
    private void SyncHintLaneReveal(bool animate = true)
    {
        double target = HintLaneRequested ? 1 : 0;
        if (Math.Abs(target - _hintLaneRevealTarget) < 0.0001) return;

        _hintLaneRevealTarget = target;

        if (!animate)
        {
            _hintLaneRevealTimer?.Stop();
            _hintLaneReveal = target;
            UpdateVideoTrackHeight();
            VideoTrackCanvas?.Invalidate();
            return;
        }

        // Re-target from wherever the previous animation got to, so a reversal mid-slide
        // continues from the current height rather than restarting from 0 or 1.
        _hintLaneRevealFrom = _hintLaneReveal;
        _hintLaneRevealStartTicks = Environment.TickCount64;

        if (_hintLaneRevealTimer is null)
        {
            _hintLaneRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _hintLaneRevealTimer.Tick += HintLaneReveal_Tick;
        }
        _hintLaneRevealTimer.Start();
    }

    private void HintLaneReveal_Tick(object? sender, object e)
    {
        double elapsed = Environment.TickCount64 - _hintLaneRevealStartTicks;
        double t = Math.Clamp(elapsed / HintLaneRevealDurationMs, 0, 1);
        double eased = t * t * (3 - 2 * t);   // smoothstep — no overshoot, soft at both ends
        _hintLaneReveal = _hintLaneRevealFrom + (_hintLaneRevealTarget - _hintLaneRevealFrom) * eased;

        if (t >= 1)
        {
            _hintLaneReveal = _hintLaneRevealTarget;
            _hintLaneRevealTimer?.Stop();
        }

        UpdateVideoTrackHeight();
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>
    /// Number of video lanes to lay out: the tracks the model actually uses, plus one
    /// transient empty lane while a segment is being dragged towards a new one (and for as
    /// long as that lane is still animating open or folding away).
    /// </summary>
    /// <remarks>
    /// <see cref="TimelineModel.VideoTrackCount"/> counts only tracks that currently hold a
    /// segment, so a project with nothing on an overlay track reports 1 and the base lane is
    /// the only row on screen. A segment could then never be dragged UP to a parallel track,
    /// because there is no row above it to aim at. Rather than permanently parking an empty
    /// lane on every timeline, the destination is revealed only once the drag actually
    /// reaches for it, and folds away again on release. It is a layout concept only and is
    /// never persisted; a lane the user drops nothing into simply stops being drawn.
    /// </remarks>
    private int VideoDisplayTrackCount(TimelineModel? model)
    {
        int used = Math.Max(1, model?.VideoTrackCount ?? 1);
        return HintLaneVisible ? used + 1 : used;
    }

    /// <summary>
    /// Whether any segment carries an audio file of the requested kind. A waveform alone is
    /// not a sufficient test for "this project has audio": waveform generation only
    /// recognises the recorder's own <c>system_*</c>/<c>mic_*</c> files, so an imported
    /// video — whose extracted track is just <c>audio.wav</c> — has real, audible audio but
    /// no waveform. Keying visibility solely off the waveform would hide that audio and its
    /// mute button entirely. Anything not named <c>mic_*</c> counts as system audio, which
    /// matches how the editor classifies these files when it builds the waveforms.
    /// </summary>
    private static bool HasAudioFile(TimelineModel model, bool mic)
    {
        foreach (var seg in model.Segments.OfType<VideoSegment>())
        {
            foreach (var path in seg.AudioFilePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                bool isMic = Path.GetFileName(path).StartsWith("mic_", StringComparison.OrdinalIgnoreCase);
                if (isMic == mic) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Shows or collapses one optional track. Writing the row height unconditionally would
    /// dirty layout on every repaint, so this only touches the tree when the state actually
    /// changes.
    /// </summary>
    /// <remarks>
    /// <paramref name="row"/> is nullable like the other two: this runs from
    /// <see cref="InsertedAudioTracks"/>'s setter, which the host can assign before the XAML
    /// tree is fully realised. A null-deref here throws inside the editor's preview
    /// initialisation, which aborts the whole rebuild and leaves a blank editor — the exact
    /// failure class the crash-hardening playbook exists for.
    /// </remarks>
    private static void ApplyTrackVisibility(
        RowDefinition? row, FrameworkElement? label, FrameworkElement? canvas, bool visible, double height)
    {
        var target = visible ? new GridLength(height) : new GridLength(0);
        if (row is not null && row.Height != target)
            row.Height = target;

        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (label is not null && label.Visibility != visibility)
            label.Visibility = visibility;
        if (canvas is not null && canvas.Visibility != visibility)
            canvas.Visibility = visibility;
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
                SafeDispose(t);
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
                SafeDispose(t);
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
                SafeDispose(t);
        }
        _thumbnailsByFile.Clear();

        if (primary is not null && !primaryDisposed)
        {
            foreach (var t in primary)
                SafeDispose(t);
        }

        _thumbnailIntervalSeconds = 0;
        _videoAspectRatio = 16.0 / 9.0;
        _primaryThumbnailFilePath = null;
        VideoTrackCanvas?.Invalidate();
    }

    private static void SafeDispose(CanvasBitmap? bitmap)
    {
        if (bitmap is null) return;
        try { bitmap.Dispose(); }
        catch { /* the bitmap may belong to a lost graphics device */ }
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

            // PERF-001: the playhead is a XAML overlay element (PlayheadLine) — no track
            // canvas draws it — so a playback tick / scrub only has to move that element.
            // Repainting every CanvasControl here re-rendered filmstrips, waveforms and the
            // cursor path on every frame. Track redraws stay wired to their real triggers
            // (model, zoom/scroll, selection, thumbnails, waveforms, theme and edits).
            control.UpdatePlayheadVisual();
        }
    }

    // --- Coordinate helpers ---
    // Extracted to TimeCoordinateConverter (same namespace) so future per-track gesture
    // handlers can share the conversions; these remain thin instance-state adapters.

    /// <summary>Horizontal inset so rounded clip edges aren't clipped at the canvas boundary.</summary>
    private const double TrackContentInset = TimeCoordinateConverter.TrackContentInset;

    private double TimeToX(TimeSpan time) =>
        TimeCoordinateConverter.TimeToX(Model, time, TimeRulerCanvas.ActualWidth, ActualWidth);

    private TimeSpan XToTime(double x) =>
        TimeCoordinateConverter.XToTime(Model, x, TimeRulerCanvas.ActualWidth, ActualWidth);

    /// <summary>
    /// Converts a source-video time (zoom keyframe / cursor timestamp) to an X
    /// coordinate, mapping through segments so it stays aligned with the video
    /// after text slides shift later content.
    /// </summary>
    private double SourceTimeToX(TimeSpan sourceTime) =>
        TimeCoordinateConverter.SourceTimeToX(Model, sourceTime, TimeRulerCanvas.ActualWidth, ActualWidth);

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
    private TimeSpan? XToPrimarySourceTime(double x) =>
        TimeCoordinateConverter.XToPrimarySourceTime(Model, x, TimeRulerCanvas.ActualWidth, ActualWidth);

    /// <summary>
    /// Internal alias for <see cref="InvalidateAllCanvases"/>, used by the drag/edit
    /// gesture handlers that change what the tracks paint.
    /// </summary>
    private void InvalidateAll() => InvalidateAllCanvases();


    /// <summary>Last offset pushed to <c>PlayheadLine</c>, to skip no-op layout passes.</summary>
    private double _playheadVisualX = double.NaN;

    /// <summary>Sub-pixel movement below this is not worth a layout pass.</summary>
    private const double PlayheadOffsetEpsilon = 0.05;

    /// <summary>Fallback width when <c>PlayheadLine.Width</c> is unset (XAML sets it to 2).</summary>
    private const double PlayheadFallbackWidth = 2;

    /// <summary>
    /// Moves the XAML playhead overlay to the current <see cref="PlayheadPosition"/>.
    /// This is the only visual update a playback tick / scrub needs.
    /// </summary>
    private void UpdatePlayheadVisual()
    {
        if (PlayheadLine is null) return;

        double viewportWidth = TimeRulerCanvas?.ActualWidth ?? 0;
        if (viewportWidth <= 0) viewportWidth = ActualWidth;

        double lineWidth = PlayheadLine.Width;
        if (double.IsNaN(lineWidth) || lineWidth <= 0) lineWidth = PlayheadFallbackWidth;

        var (offsetX, isVisible) = ComputePlayheadPlacement(TimeToX(PlayheadPosition), viewportWidth, lineWidth);

        if (!isVisible)
        {
            // Scrolled/zoomed out of the track viewport — hide rather than let a negative
            // margin paint the line over the (unclipped) track-label column.
            if (PlayheadLine.Visibility != Visibility.Collapsed)
                PlayheadLine.Visibility = Visibility.Collapsed;
            return;
        }

        if (double.IsNaN(_playheadVisualX) || Math.Abs(_playheadVisualX - offsetX) >= PlayheadOffsetEpsilon)
        {
            _playheadVisualX = offsetX;
            PlayheadLine.Margin = new Thickness(offsetX, 0, 0, 0);
        }

        if (PlayheadLine.Visibility != Visibility.Visible)
            PlayheadLine.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Pure placement rule for the playhead overlay: the left margin to apply and whether
    /// the line intersects the track viewport at all. Kept side-effect free so the geometry
    /// can be reasoned about (and exercised) without a UI harness.
    /// </summary>
    internal static (double OffsetX, bool IsVisible) ComputePlayheadPlacement(
        double x, double viewportWidth, double lineWidth)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return (0, false);
        if (viewportWidth <= 0) return (x, true); // not laid out yet — don't hide it
        bool visible = x + lineWidth > 0 && x < viewportWidth;
        if (!visible) return (x, false);

        double maxOffset = Math.Max(0, viewportWidth - lineWidth);
        return (Math.Clamp(x, 0, maxOffset), true);
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
    private const float BaseVideoTrackVerticalPadding = 14f;
    private const float OverlayVideoTrackVerticalPadding = 6f;

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
            DrawVideoTrackFromSegments(ds, model, w, h, hasThumbnails);

            // Trim handles + speed overlays don't apply to the segment view
            return;
        }

        // Nothing on the timeline at all: no segments, no legacy clips, no filmstrip to draw
        // one from. Rather than leave the lane blank, draw the placeholder that invites the
        // first clip — the same one whether the editor has never held a project or the user
        // just deleted their last one.
        if (model.Segments.Count == 0 && model.Clips.Count == 0 && !hasThumbnails)
        {
            DrawEmptyVideoTrackPlaceholder(ds, w, h, pad);
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

        // If no clips, draw the full duration as one clip — but only for a genuinely legacy
        // clip timeline, never for a segment timeline whose last segment was just removed.
        // That model still carries the discarded take's Duration, so this fallback would paint
        // a filmstrip block for footage the timeline no longer has: a ghost nothing can select
        // or delete, because hit testing runs over segments and none stands behind it.
        // PrimaryVideoFilePath is the discriminator — every segment-era project sets it.
        if (model.Clips.Count == 0 && model.PrimaryVideoFilePath is null)
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
    /// Draws the base lane's "nothing here yet" placeholder: a dashed shadow segment spanning
    /// the ruler with a one-line prompt in it.
    /// </summary>
    /// <remarks>
    /// Deliberately drawn as a SHADOW rather than as a clip. The lane used to fall through to
    /// the legacy fallback and paint a solid electric-blue block, which reads as a real clip —
    /// it invites a click, and every gesture aimed at it (select, split, delete, drag) silently
    /// does nothing, because hit testing runs over segments and there is no segment there. A
    /// dashed outline over a faint fill says "this is where a clip goes" instead of pretending
    /// one already does.
    /// </remarks>
    private void DrawEmptyVideoTrackPlaceholder(CanvasDrawingSession ds, float w, float h, float pad)
    {
        float x1 = (float)TimeToX(TimeSpan.Zero);
        float x2 = (float)TimeToX(Model?.DisplayDuration ?? TimeSpan.Zero);
        float blockW = Math.Max(2, x2 - x1);
        float blockH = Math.Max(2, h - pad * 2);

        using var geom = CanvasGeometry.CreateRoundedRectangle(
            ds, x1, pad, blockW, blockH, VideoClipCornerRadius, VideoClipCornerRadius);
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };

        ds.FillGeometry(geom, EmptyPlaceholderFill);
        ds.DrawGeometry(geom, EmptyPlaceholderStroke, 1.2f, dashed);

        using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = 12,
            FontFamily = "Segoe UI",
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
        };
        ds.DrawText(
            "Record or import a video to start your timeline",
            new Rect(x1, pad, blockW, blockH),
            TrackHintTextColor,
            format);
    }

    /// <summary>
    /// Renders the video track from the segment list: each <see cref="VideoSegment"/>
    /// is drawn as a filmstrip clip showing its source range, and each
    /// <see cref="TextSlideSegment"/> as a colored block with its text.
    /// </summary>
    private void DrawVideoTrackFromSegments(
        CanvasDrawingSession ds, TimelineModel model, float w, float h, bool hasThumbnails)
    {
        var textLabelColor = Color.FromArgb(255, 255, 255, 255);
        var snapGuideColor = Color.FromArgb(255, 255, 214, 10);  // Amber snap line
        int trackCount = VideoDisplayTrackCount(model);

        // The topmost lane is the transient drop hint whenever it sits above every track the
        // model actually uses. Drawn as a dashed outline rather than a solid row so it reads
        // as "release here to create V<n>" instead of an empty track that already exists.
        int hintTrack = HintLaneVisible && trackCount > Math.Max(1, model.VideoTrackCount)
            ? trackCount - 1
            : -1;

        for (int track = trackCount - 1; track >= 0; track--)
        {
            var (rowY, rowH, rowPad) = VideoTrackRowBounds(track, trackCount);

            if (track == hintTrack)
            {
                DrawOverlayDropHintLane(ds, track, rowY, rowH, rowPad, w);
                continue;
            }

            ds.DrawLine(0, rowY, w, rowY, TrackEmptyLineColor, 1f);

            if (track > 0)
            {
                ds.DrawText(
                    $"V{track}",
                    6, rowY + 3,
                    TrackHintTextColor,
                    new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                    {
                        FontSize = 10,
                        FontFamily = "Segoe UI",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
            }
        }

        // Collected alongside the main draw loop (post drag-preview x1/x2) so the
        // boundary-chip pass below shares the exact on-screen geometry the segments
        // were just painted at. Transitions belong to the contiguous base chain only.
        var baseRects = new List<(TimelineSegment Segment, float X1, float X2)>(model.Segments.Count);

        foreach (var segment in model.Segments)
        {
            var (x1, x2) = GetSegmentDisplayX(segment);
            int displayTrack = GetSegmentDisplayTrackIndex(segment, trackCount);
            var (rowY, rowH, rowPad) = VideoTrackRowBounds(displayTrack, trackCount);
            float clipY = rowY + rowPad;
            float clipH = rowH - rowPad * 2;
            bool isDragged = segment.Id == _draggedSegmentId;
            if (segment.TrackIndex == TimelineModel.BaseTrackIndex)
                baseRects.Add((segment, x1, x2));

            if (x2 < 0 || x1 > w || clipH <= 0) continue;
            float segW = Math.Max(2, x2 - x1);

            using var segGeom = CanvasGeometry.CreateRoundedRectangle(
                ds, x1, clipY, segW, clipH, VideoClipCornerRadius, VideoClipCornerRadius);

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
                        DrawFilmstripForSegment(ds, x1, x2, clipY, clipH, video, thumbSet);
                    }
                    var strokeColor = isSelected ? VideoClipSelectedBorder : FilmstripStrokeColor;
                    ds.DrawGeometry(segGeom, strokeColor, isSelected ? 2f : 1f);
                }
                else
                {
                    ds.FillGeometry(segGeom, isSelected ? VideoClipSelectedColor : VideoClipColor);
                    if (isSelected) ds.DrawGeometry(segGeom, VideoClipSelectedBorder, 2f);
                }

                DrawSegmentSpeedBadge(ds, video, x1, clipY, segW, clipH);
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
                    ds.DrawText(labelText, new Rect(x1 + 4, clipY, segW - 8, clipH), textLabelColor, fmt);
                }

                DrawTextSlideWindow(ds, slide, x1, x2, clipY, clipH, w);
            }

            // Trim-edge handles on the selected segment (grab affordance).
            if (segment.Id == _selectedSegmentId && segW > 10)
            {
                float handleW = 3f;
                float handleH = clipH * 0.5f;
                float handleY = clipY + (clipH - handleH) / 2;
                ds.FillRoundedRectangle(x1 + 1.5f, handleY, handleW, handleH, 1.5f, 1.5f, VideoClipSelectedBorder);
                ds.FillRoundedRectangle(x2 - handleW - 1.5f, handleY, handleW, handleH, 1.5f, 1.5f, VideoClipSelectedBorder);
            }

            // Transition marker replaced by the selectable boundary chip drawn in the
            // pass below (after every segment has its final on-screen rect), so the
            // chip can be centred on the cut line rather than pinned to the segment
            // start.

            // Boundary line between segments
            if (segment.Start > TimeSpan.Zero && !isDragged)
                ds.DrawLine(x1, clipY, x1, clipY + clipH, CutLineColor, 1.5f);
        }

        // Transition boundary chips — drawn after every segment rect is known so each
        // chip can be centred on the boundary and density-guarded against both
        // neighbours, not just the incoming segment. Only adjacent base-track
        // segments have transitions; overlays cover rather than crossfade.
        var (baseRowY, baseRowH, basePad) = VideoTrackRowBounds(TimelineModel.BaseTrackIndex, trackCount);
        float baseClipH = baseRowH - basePad * 2;
        for (int i = 1; i < baseRects.Count; i++)
        {
            DrawTransitionChipForBoundary(ds, baseRects[i - 1], baseRects[i], baseRowY + basePad, baseClipH, w);
        }

        // Drop indicator (where a moved segment will land).
        if (!double.IsNaN(_segmentDropIndicatorX))
        {
            var (rowY, rowH, _) = VideoTrackRowBounds(_segmentDragCurrentTrackIndex, trackCount);
            ds.DrawLine((float)_segmentDropIndicatorX, rowY, (float)_segmentDropIndicatorX, rowY + rowH, VideoClipSelectedBorder, 2.5f);
        }

        // Snap guide line.
        if (!double.IsNaN(_segmentSnapGuideX))
            ds.DrawLine((float)_segmentSnapGuideX, 0, (float)_segmentSnapGuideX, h, snapGuideColor, 1f);
    }

    /// <summary>
    /// Draws the "⏱ 1.5x" pill on a segment whose playback speed is not 1×. The legacy
    /// clip view tints the whole block instead; on the segment view that would hide the
    /// filmstrip, so a corner badge carries the same information.
    /// </summary>
    /// <remarks>
    /// The stopwatch mark is not decoration: the zoom track labels its segments with a bare
    /// "2x" too, so a speed badge showing only a multiplier reads as a zoom level on a quick
    /// scan. The glyph (plus the badge fill) is what separates the two.
    /// <para>
    /// The fill is a NEUTRAL scrim rather than the sped-up/slowed status colours used by the
    /// legacy clip view: those tint a whole block, where saturation reads as state, but a
    /// small saturated pill sitting on the video block reads as a marker of its own (the
    /// original orange was taken for a zoom badge). The multiplier itself already says which
    /// direction the speed went.
    /// </para>
    /// </remarks>
    private void DrawSegmentSpeedBadge(
        CanvasDrawingSession ds, VideoSegment video, float x1, float clipY, float segW, float clipH)
    {
        if (Math.Abs(video.SpeedFactor - 1.0) <= 0.001) return;

        string label = $"{video.SpeedFactor:0.##}x";
        float badgeH = Math.Min(15f, clipH - 4);
        float iconSize = Math.Min(9f, badgeH - 4f);
        float textW = label.Length * 6.5f;
        float badgeW = 11f + iconSize + textW;
        // Drop the whole badge rather than the glyph when space is tight — a bare
        // multiplier is exactly the ambiguity the glyph exists to remove.
        if (badgeH < 9f || iconSize < 6f || segW < badgeW + 8f) return;

        float badgeX = x1 + 4;
        float badgeY = clipY + 3;
        ds.FillRoundedRectangle(badgeX, badgeY, badgeW, badgeH, 3f, 3f, SpeedBadgeFillColor);

        DrawSpeedGlyph(ds, badgeX + 4f, badgeY + (badgeH - iconSize) / 2f, iconSize, SpeedBadgeForegroundColor);

        ds.DrawText(
            label,
            new Rect(badgeX + 6f + iconSize, badgeY, textW, badgeH),
            SpeedBadgeForegroundColor,
            new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontSize = 10,
                FontFamily = "Segoe UI",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
            });
    }

    /// <summary>
    /// Draws a stopwatch mark (dial, crown, two hands) inside the
    /// <paramref name="size"/>-square box at <paramref name="x"/>,<paramref name="y"/>.
    /// Drawn from primitives rather than an icon-font glyph because the timeline's Win2D
    /// surface only ever loads "Segoe UI", and it reads the same for a slowed segment as
    /// for a sped-up one — unlike a fast-forward chevron.
    /// </summary>
    private static void DrawSpeedGlyph(CanvasDrawingSession ds, float x, float y, float size, Color color)
    {
        float r = size / 2f - 0.75f;
        float cx = x + size / 2f;
        float cy = y + size / 2f + 0.5f;

        ds.DrawCircle(cx, cy, r, color, 1.2f);
        ds.FillRectangle(cx - 1f, y - 0.5f, 2f, 1.75f, color);
        ds.DrawLine(cx, cy, cx, cy - r * 0.62f, color, 1.1f);
        ds.DrawLine(cx, cy, cx + r * 0.58f, cy, color, 1.1f);
    }

    /// <summary>
    /// Paints the transient "drop here to create V&lt;n&gt;" lane.
    /// </summary>
    /// <remarks>
    /// Everything is drawn inside a layer that is both clipped to the lane's current band and
    /// scaled to its opacity, because mid-reveal the row is only a few pixels tall: an
    /// unclipped dashed outline and label would spill over the lane below, and a full-strength
    /// outline in a 4px slot is exactly the "pops in and out" artefact the animation exists to
    /// remove. The affordance therefore fades in as the lane slides open.
    /// </remarks>
    private void DrawOverlayDropHintLane(
        CanvasDrawingSession ds, int track, float rowY, float rowH, float rowPad, float w)
    {
        float reveal = (float)Math.Clamp(_hintLaneReveal, 0, 1);
        if (reveal <= 0.01f || rowH <= 0.5f) return;

        bool armed = _hintLaneArmed;
        var hintColor = armed ? VideoClipSelectedBorder : Color.FromArgb(120, 255, 255, 255);

        float hintY = rowY + rowPad;
        float hintH = Math.Max(1f, rowH - rowPad * 2);
        float hintW = Math.Max(2f, w - 2);

        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var label = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = 10,
            FontFamily = "Segoe UI",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        using (ds.CreateLayer(reveal, new Rect(0, rowY, w, rowH)))
        {
            ds.DrawLine(0, rowY, w, rowY, TrackEmptyLineColor, 1f);
            if (armed)
                ds.FillRoundedRectangle(1, hintY, hintW, hintH, 4, 4, Color.FromArgb(28, 255, 255, 255));
            ds.DrawRoundedRectangle(1, hintY, hintW, hintH, 4, 4, hintColor, 1.2f, dashed);
            ds.DrawText($"V{track}  ·  drop to create", 6, rowY + 3, TrackHintTextColor, label);
        }
    }

    /// <summary>
    /// Maps a logical full-frame video track to its on-canvas row. Track 0 remains the
    /// historical 80px base lane at the bottom; higher overlay tracks stack upward.
    /// </summary>
    /// <remarks>
    /// The transient drop-hint lane (always the topmost index when present) is only as tall as
    /// <see cref="HintLaneBandHeight"/>, which eases between 0 and a full lane while it opens
    /// and folds. Every real lane is therefore offset by that partial band rather than by a
    /// whole row, so the tracks slide down smoothly instead of teleporting. The hint lane's
    /// padding is scaled by the same fraction, which keeps the clip height it yields positive
    /// throughout — an unscaled 6px pad would exceed a 4px-tall band and make the segment
    /// being dragged into the lane vanish for the first frames of the reveal.
    /// </remarks>
    private (float Y, float Height, float Pad) VideoTrackRowBounds(int trackIndex, int trackCount)
    {
        trackCount = Math.Max(1, trackCount);
        trackIndex = Math.Clamp(trackIndex, TimelineModel.BaseTrackIndex, trackCount - 1);

        bool hasHintLane = HintLaneVisible;
        float hintBand = HintLaneBandHeight;
        int realCount = Math.Max(1, hasHintLane ? trackCount - 1 : trackCount);

        if (hasHintLane && trackIndex == trackCount - 1)
        {
            float reveal = (float)Math.Clamp(_hintLaneReveal, 0, 1);
            return (0f, hintBand, OverlayVideoTrackVerticalPadding * reveal);
        }

        if (trackIndex == TimelineModel.BaseTrackIndex)
        {
            float y = hintBand + (float)((realCount - 1) * OverlayVideoTrackHeight);
            return (y, (float)BaseVideoTrackHeight, BaseVideoTrackVerticalPadding);
        }

        float rowY = hintBand + (float)((realCount - 1 - trackIndex) * OverlayVideoTrackHeight);
        return (rowY, (float)OverlayVideoTrackHeight, OverlayVideoTrackVerticalPadding);
    }

    /// <summary>
    /// Resolves the video lane under the pointer before segment hit-testing so overlapping
    /// full-frame blocks on different tracks are independently selectable.
    /// </summary>
    private int VideoTrackIndexFromY(TimelineModel model, double y)
    {
        int trackCount = VideoDisplayTrackCount(model);
        int realCount = Math.Max(1, HintLaneVisible ? trackCount - 1 : trackCount);

        // Measured from below the (possibly partly open) hint band, so hit-testing agrees with
        // the row geometry mid-animation instead of being a lane out.
        double localY = y - HintLaneBandHeight;
        double overlayBandHeight = (realCount - 1) * OverlayVideoTrackHeight;
        if (localY >= overlayBandHeight) return TimelineModel.BaseTrackIndex;

        int visualRow = Math.Clamp((int)(Math.Max(0, localY) / OverlayVideoTrackHeight), 0, Math.Max(0, realCount - 2));
        return realCount - 1 - visualRow;
    }

    /// <summary>
    /// Computes a primary-track segment's on-screen X range, applying the live
    /// trim/move drag preview when <paramref name="segment"/> is the one currently
    /// being dragged. Shared by the segment draw pass, the transition-chip draw pass,
    /// and <see cref="HitTestTransitionChip"/> so all three always agree on the same
    /// geometry — a hit test computed from stale (pre-drag) positions could otherwise
    /// select the wrong boundary mid-drag.
    /// </summary>
    private (float X1, float X2) GetSegmentDisplayX(TimelineSegment segment)
    {
        float x1 = (float)TimeToX(segment.Start);
        float x2 = (float)TimeToX(segment.End);

        if (segment.Id == _draggedSegmentId && !double.IsNaN(_segmentDragCurrentX))
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

        return (x1, x2);
    }

    private int GetSegmentDisplayTrackIndex(TimelineSegment segment, int trackCount)
    {
        if (segment.Id == _draggedSegmentId &&
            _dragMode == DragMode.SegmentBody &&
            _segmentDragMoved)
        {
            return Math.Clamp(_segmentDragCurrentTrackIndex, TimelineModel.BaseTrackIndex, trackCount - 1);
        }

        return Math.Clamp(segment.TrackIndex, TimelineModel.BaseTrackIndex, trackCount - 1);
    }

    private void DrawTextSlideWindow(
        CanvasDrawingSession ds,
        TextSlideSegment slide,
        float segX1,
        float segX2,
        float segY,
        float segH,
        float canvasWidth)
    {
        if (segX2 <= segX1 || segH <= 0) return;

        var (inStart, outEnd) = GetTextSlideWindowForDisplay(slide);
        if (outEnd <= inStart) return;

        double rawInX = TimeToX(slide.Start + inStart);
        double rawOutX = TimeToX(slide.Start + outEnd);
        double visibleLeft = Math.Max(Math.Max(segX1, 0), rawInX);
        double visibleRight = Math.Min(Math.Min(segX2, canvasWidth), rawOutX);
        if (visibleRight <= visibleLeft) return;

        var (barY, barH) = TextSlideWindowBarBounds(segY, segH);
        var barFill = Color.FromArgb(130, 255, 255, 255);
        var rampColor = Color.FromArgb(115, 255, 214, 10);
        var borderColor = Color.FromArgb(180, 255, 255, 255);

        ds.FillRoundedRectangle((float)visibleLeft, barY, (float)(visibleRight - visibleLeft), barH, barH / 2, barH / 2, barFill);
        ds.DrawRoundedRectangle((float)visibleLeft, barY, (float)(visibleRight - visibleLeft), barH, barH / 2, barH / 2, borderColor, 1f);

        DrawTextSlideRampHatch(ds, rawInX, rawInX + TimeToXDuration(slide.ResolveTextInDuration()), rawInX, rawOutX, barY, barH, canvasWidth, rampColor);
        DrawTextSlideRampHatch(ds, rawOutX - TimeToXDuration(slide.ResolveTextOutDuration()), rawOutX, rawInX, rawOutX, barY, barH, canvasWidth, rampColor);

        if (slide.Id != _selectedSegmentId) return;

        double handleInX = TextSlideWindowHandleX(rawInX, segX1, segX2, isInHandle: true);
        double handleOutX = TextSlideWindowHandleX(rawOutX, segX1, segX2, isInHandle: false);

        bool inVisible = !double.IsNaN(handleInX)
            && handleInX >= Math.Max(segX1, 0) && handleInX <= Math.Min(segX2, canvasWidth);
        bool outVisible = !double.IsNaN(handleOutX)
            && handleOutX >= Math.Max(segX1, 0) && handleOutX <= Math.Min(segX2, canvasWidth);
        if (inVisible)
            DrawTextSlideWindowHandle(ds, (float)handleInX, barY, barH);
        if (outVisible)
            DrawTextSlideWindowHandle(ds, (float)handleOutX, barY, barH);
    }

    /// <summary>
    /// Screen X at which a text-slide window handle is drawn and grabbed, or
    /// <see cref="double.NaN"/> when the slide is too narrow to carry one.
    /// </summary>
    /// <remarks>
    /// A never-edited window spans the whole slide, so its handles would sit exactly on the
    /// segment's own trim edges — and since the window is hit-tested before the segment
    /// loop, they would swallow every trim drag on a selected slide (the recorded "a
    /// grabbable edge stole the drag that began near it" failure). Nudging a coincident
    /// handle inboard keeps both gestures reachable: the trim edge keeps the outer band and
    /// the window handle sits just inside it. Draw and hit-test share this so the pixel the
    /// user aims at is the pixel that responds.
    /// </remarks>
    private static double TextSlideWindowHandleX(double rawX, float segX1, float segX2, bool isInHandle)
    {
        double inset = SegmentEdgeHitWidth + 3.0;

        // Too narrow to separate the two affordances — trimming is the more destructive and
        // more expected gesture, so it keeps the whole block and the window handle is dropped.
        if (segX2 - segX1 < inset * 3) return double.NaN;

        return isInHandle
            ? Math.Max(rawX, segX1 + inset)
            : Math.Min(rawX, segX2 - inset);
    }

    private (TimeSpan InStart, TimeSpan OutEnd) GetTextSlideWindowForDisplay(TextSlideSegment slide)
    {
        if (slide.Id == _draggedSegmentId &&
            _dragMode is DragMode.TextSlideWindowInEdge or DragMode.TextSlideWindowOutEdge &&
            !double.IsNaN(_textSlideWindowDragCurrentX))
        {
            var draggedOffset = XToTime(_textSlideWindowDragCurrentX) - slide.Start;
            if (_dragMode == DragMode.TextSlideWindowInEdge)
                return ClampTextSlideWindow(draggedOffset, _textSlideWindowOriginalOutEnd, slide.Duration);
            return ClampTextSlideWindow(_textSlideWindowOriginalInStart, draggedOffset, slide.Duration);
        }

        return (slide.ResolveTextInStart(), slide.ResolveTextOutEnd());
    }

    private double TimeToXDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0;
        return TimeToX(duration) - TimeToX(TimeSpan.Zero);
    }

    private static (float Y, float Height) TextSlideWindowBarBounds(float segY, float segH)
    {
        float barH = Math.Clamp(segH * 0.16f, 4f, 8f);
        float y = segY + segH - barH - Math.Clamp(segH * 0.12f, 3f, 6f);
        return (y, barH);
    }

    private void DrawTextSlideRampHatch(
        CanvasDrawingSession ds,
        double rawRampX1,
        double rawRampX2,
        double rawWindowX1,
        double rawWindowX2,
        float barY,
        float barH,
        float canvasWidth,
        Color color)
    {
        double x1 = Math.Max(Math.Max(rawRampX1, rawWindowX1), 0);
        double x2 = Math.Min(Math.Min(rawRampX2, rawWindowX2), canvasWidth);
        if (x2 - x1 < 2) return;

        for (float x = (float)x1 - barH; x < x2; x += 5f)
        {
            float sx = Math.Max((float)x1, x);
            float ex = Math.Min((float)x2, x + barH);
            if (ex <= sx) continue;
            float sy = barY + barH - (sx - x);
            float ey = barY + barH - (ex - x);
            ds.DrawLine(sx, sy, ex, ey, color, 1f);
        }
    }

    private void DrawTextSlideWindowHandle(CanvasDrawingSession ds, float x, float barY, float barH)
    {
        float handleW = 4f;
        float handleH = barH + 8f;
        float handleY = barY - 4f;
        ds.FillRoundedRectangle(x - handleW / 2, handleY, handleW, handleH, 1.5f, 1.5f, VideoClipSelectedBorder);
    }

    private static (TimeSpan InStart, TimeSpan OutEnd) ClampTextSlideWindow(
        TimeSpan inStart,
        TimeSpan outEnd,
        TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration <= TimeSpan.Zero) return (TimeSpan.Zero, TimeSpan.Zero);
        if (duration <= SetTextSlideTextWindowOperation.MinTextWindow)
            return (TimeSpan.Zero, duration);

        var start = Clamp(inStart, TimeSpan.Zero, duration);
        var end = Clamp(outEnd, TimeSpan.Zero, duration);
        if (end < start) end = start;
        if (end - start < SetTextSlideTextWindowOperation.MinTextWindow)
        {
            end = start + SetTextSlideTextWindowOperation.MinTextWindow;
            if (end > duration)
            {
                end = duration;
                start = end - SetTextSlideTextWindowOperation.MinTextWindow;
            }
        }

        return (start, end);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private (string? Id, SegmentHitTarget Target) HitTestTextSlideWindowHandle(
        TimelineModel model,
        double posX,
        double posY,
        int trackIndex)
    {
        if (_selectedSegmentId is null) return (null, SegmentHitTarget.None);

        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s =>
            s.Id == _selectedSegmentId && s.TrackIndex == trackIndex);
        if (slide is null) return (null, SegmentHitTarget.None);

        int trackCount = VideoDisplayTrackCount(model);
        var (segX1, segX2) = GetSegmentDisplayX(slide);
        var (rowY, rowH, rowPad) = VideoTrackRowBounds(trackIndex, trackCount);
        float segY = rowY + rowPad;
        float segH = rowH - rowPad * 2;
        var (barY, barH) = TextSlideWindowBarBounds(segY, segH);
        if (posY < barY - 5 || posY > barY + barH + 5) return (null, SegmentHitTarget.None);

        var (inStart, outEnd) = GetTextSlideWindowForDisplay(slide);
        double rawInX = TimeToX(slide.Start + inStart);
        double rawOutX = TimeToX(slide.Start + outEnd);
        double visibleSegLeft = Math.Max(segX1, 0);
        double visibleSegRight = Math.Min(segX2, VideoTrackCanvas?.ActualWidth ?? 0);

        double edge = Math.Clamp(Math.Abs(rawOutX - rawInX) / 3.0, 3.0, SegmentEdgeHitWidth);

        // Same nudged positions the handles are DRAWN at, so the pixel the user aims at is
        // the pixel that responds and the segment's own trim edges stay grabbable.
        double handleInX = TextSlideWindowHandleX(rawInX, segX1, segX2, isInHandle: true);
        double handleOutX = TextSlideWindowHandleX(rawOutX, segX1, segX2, isInHandle: false);

        if (!double.IsNaN(handleInX)
            && handleInX >= visibleSegLeft && handleInX <= visibleSegRight
            && Math.Abs(posX - handleInX) <= edge)
        {
            return (slide.Id, SegmentHitTarget.LeftEdge);
        }

        if (!double.IsNaN(handleOutX)
            && handleOutX >= visibleSegLeft && handleOutX <= visibleSegRight
            && Math.Abs(posX - handleOutX) <= edge)
        {
            return (slide.Id, SegmentHitTarget.RightEdge);
        }

        return (null, SegmentHitTarget.None);
    }

    // ── Transition boundary chip — the selectable affordance sitting on the cut line
    // between two adjacent primary-track segments (segment[i].InTransition describes
    // the boundary between segment[i-1] and segment[i], per TimelineSegment.InTransition).
    // Sits centred on the same cut line the boundary line is drawn on, and straddles the
    // incoming segment's leading trim handle — HitTestTransitionChip is therefore always
    // consulted BEFORE HitTestSegment in the pointer handlers so the chip wins the click.

    private const float TransitionChipWidth = 20f;
    private const float TransitionChipHeight = 15f;

    /// <summary>
    /// Minimum on-screen width (px) BOTH neighbouring segments must have for a boundary
    /// chip to be drawn/hit-tested. Mirrors the existing segW>20 / segW>10 density guards
    /// used elsewhere in this file — without this, a dense timeline turns into a wall of
    /// overlapping chips and the trim handles they sit on top of become unreachable.
    /// </summary>
    private const float TransitionChipMinAdjacentSegmentWidth = 24f;

    /// <summary>Chip rect centred on <paramref name="boundaryX"/>, vertically centred in the clip band.</summary>
    private static Rect GetTransitionChipRect(float boundaryX, float pad, float clipH)
    {
        double y = pad + (clipH - TransitionChipHeight) / 2.0;
        return new Rect(boundaryX - TransitionChipWidth / 2.0, y, TransitionChipWidth, TransitionChipHeight);
    }

    /// <summary>
    /// True when both segments flanking a boundary are wide enough on-screen for a chip
    /// to be drawn/hit there without crowding the trim handles or neighbouring chips.
    /// </summary>
    private static bool IsTransitionChipEligible(float prevX1, float prevX2, float curX1, float curX2) =>
        (prevX2 - prevX1) >= TransitionChipMinAdjacentSegmentWidth &&
        (curX2 - curX1) >= TransitionChipMinAdjacentSegmentWidth;

    /// <summary>
    /// Maps a <see cref="TransitionType"/> to its visual family: a fill colour and a
    /// short (1-2 char) glyph. Grouped rather than drawing 20 distinct icons — dissolve
    /// (Fade/CrossFade/DipToWhite), slide/push (the 8 directional slide+push types),
    /// wipe (the 4 wipe directions), and stylised (ZoomBlur/WhipPan/Glitch).
    /// </summary>
    private (Color Fill, string Glyph) GetTransitionChipVisual(TransitionType type) => type switch
    {
        TransitionType.Fade or TransitionType.CrossFade or TransitionType.DipToWhite
            => (TransitionChipDissolveColor, "D"),
        TransitionType.SlideLeft or TransitionType.SlideRight or TransitionType.SlideUp or TransitionType.SlideDown
            or TransitionType.PushLeft or TransitionType.PushRight or TransitionType.PushUp or TransitionType.PushDown
            => (TransitionChipSlidePushColor, "S"),
        TransitionType.Wipe or TransitionType.WipeRight or TransitionType.WipeUp or TransitionType.WipeDown
            => (TransitionChipWipeColor, "W"),
        TransitionType.ZoomBlur or TransitionType.WhipPanLeft or TransitionType.WhipPanRight or TransitionType.Glitch
            => (TransitionChipStylizedColor, "FX"),
        _ => (TransitionChipEmptyFill, "A"),
    };

    /// <summary>
    /// Human-readable description of a boundary's transition, shown in the hover tooltip so the
    /// chip's 1-2 character glyph is discoverable rather than something to be decoded.
    /// </summary>
    private static string DescribeTransitionForTooltip(TransitionConfig? config)
    {
        if (config is null)
        {
            return "Transition: Automatic\n" +
                   "Cross dissolves next to a text slide, hard cut elsewhere.\n" +
                   "Click to choose an effect.";
        }

        if (config.Type == TransitionType.None)
            return "Transition: None (hard cut)\nClick to choose an effect · right-click to reset to Automatic.";

        return $"Transition: {DescribeTransitionType(config.Type)}\n" +
               $"{config.Duration.TotalSeconds:0.00}s · {DescribeEasing(config.Easing)}\n" +
               "Click to edit · right-click to remove.";
    }

    private static string DescribeTransitionType(TransitionType type) => type switch
    {
        TransitionType.Fade => "Fade (through black)",
        TransitionType.CrossFade => "Cross dissolve",
        TransitionType.DipToWhite => "Dip to white",
        TransitionType.SlideLeft => "Slide left",
        TransitionType.SlideRight => "Slide right",
        TransitionType.SlideUp => "Slide up",
        TransitionType.SlideDown => "Slide down",
        TransitionType.PushLeft => "Push left",
        TransitionType.PushRight => "Push right",
        TransitionType.PushUp => "Push up",
        TransitionType.PushDown => "Push down",
        TransitionType.Wipe => "Wipe left \u2192 right",
        TransitionType.WipeRight => "Wipe right \u2192 left",
        TransitionType.WipeUp => "Wipe bottom \u2192 top",
        TransitionType.WipeDown => "Wipe top \u2192 bottom",
        TransitionType.ZoomBlur => "Zoom blur",
        TransitionType.WhipPanLeft => "Whip pan left",
        TransitionType.WhipPanRight => "Whip pan right",
        TransitionType.Glitch => "Glitch",
        _ => type.ToString(),
    };

    private static string DescribeEasing(TransitionEasing easing) => easing switch
    {
        TransitionEasing.Linear => "linear",
        TransitionEasing.EaseIn => "ease in",
        TransitionEasing.EaseOut => "ease out",
        _ => "ease in-out",
    };

    /// <summary>
    /// Draws the selectable boundary chip between two adjacent segments, if both are
    /// wide enough on-screen (<see cref="IsTransitionChipEligible"/>). A configured
    /// boundary (incoming.InTransition with a non-None type) gets a solid, family-
    /// coloured pill; an unconfigured boundary gets an equally solid neutral "A"
    /// (Automatic) pill, so the transition surface is discoverable before anything is set.
    /// </summary>
    private void DrawTransitionChipForBoundary(
        CanvasDrawingSession ds,
        (TimelineSegment Segment, float X1, float X2) prev,
        (TimelineSegment Segment, float X1, float X2) cur,
        float pad, float clipH, float w)
    {
        if (!IsTransitionChipEligible(prev.X1, prev.X2, cur.X1, cur.X2)) return;
        // Geometry is unstable mid-drag for either flanking segment — skip rather than
        // draw/hit a chip that would jump around under the pointer.
        if (prev.Segment.Id == _draggedSegmentId || cur.Segment.Id == _draggedSegmentId) return;

        float boundaryX = cur.X1;
        if (boundaryX < -TransitionChipWidth || boundaryX > w + TransitionChipWidth) return;

        var rect = GetTransitionChipRect(boundaryX, pad, clipH);
        bool isSelected = cur.Segment.Id == _selectedTransitionId;
        var config = cur.Segment.InTransition;

        Color fill;
        Color border;
        Color glyphColor;
        string glyph;
        if (config is null)
        {
            // Automatic: no config at all. Falls back to the legacy crossfade next to a text
            // slide and a hard cut elsewhere.
            fill = TransitionChipEmptyFill;
            border = TransitionChipEmptyBorder;
            glyph = "A";
            glyphColor = TransitionChipEmptyGlyphColor;
        }
        else if (config.Type == TransitionType.None)
        {
            // An EXPLICIT hard cut is a real, user-chosen setting and must not be drawn as
            // "Automatic" — the two behave differently (an explicit None suppresses even the
            // slide-adjacent legacy crossfade), so showing them identically would tell the user
            // their choice hadn't taken.
            fill = TransitionChipHardCutFill;
            border = TransitionChipHardCutBorder;
            glyph = "\u2015"; // horizontal bar — "straight through, no effect"
            glyphColor = TransitionChipHardCutGlyphColor;
        }
        else
        {
            (fill, glyph) = GetTransitionChipVisual(config.Type);
            border = fill;
            glyphColor = TransitionChipGlyphColor;
        }

        if (isSelected) border = VideoClipSelectedBorder;

        float radius = (float)rect.Height / 2f;
        // Drawn with the rounded-rectangle primitives rather than a CanvasGeometry: this runs for
        // every visible boundary on every canvas invalidation (including each pointer-move frame
        // of a drag), and allocating a native geometry per chip per frame was pure churn.
        ds.FillRoundedRectangle(
            (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, radius, radius, fill);
        ds.DrawRoundedRectangle(
            (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, radius, radius,
            border, isSelected ? 2f : 1f);

        ds.DrawText(glyph, rect, glyphColor, TransitionChipGlyphFormat);
    }

    /// <summary>
    /// Shared text format for every chip glyph. Cached for the same reason the chip no longer
    /// builds a <see cref="CanvasGeometry"/> per draw — it is otherwise re-created for every
    /// boundary on every invalidation. Disposed with the control.
    /// </summary>
    private Microsoft.Graphics.Canvas.Text.CanvasTextFormat TransitionChipGlyphFormat =>
        _transitionChipGlyphFormat ??= new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontSize = 10,
            FontFamily = "Segoe UI",
            // The unconfigured chip is weighted the same as a configured one: it is an equally
            // clickable affordance, and rendering it lighter is what made it read as decorative.
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
        };

    private Microsoft.Graphics.Canvas.Text.CanvasTextFormat? _transitionChipGlyphFormat;

    /// <summary>
    /// Hit-tests the boundary chips on the primary track. Returns the incoming
    /// segment's Id (the same Id <see cref="TimelineSegment.InTransition"/> lives on
    /// for that boundary) when a chip is hit. Must be consulted BEFORE
    /// <see cref="HitTestSegment"/> in every pointer handler on this track — the chip
    /// physically overlaps the incoming segment's leading trim handle and the cut
    /// line, so testing segments first would make chips unreachable.
    /// </summary>
    private (string? IncomingSegmentId, bool Hit) HitTestTransitionChip(double posX, double posY)
        => HitTestTransitionChip(posX, posY, out _);

    /// <summary>
    /// As <see cref="HitTestTransitionChip(double, double)"/>, additionally reporting the hit
    /// chip's rectangle so the hover tooltip can be anchored to it.
    /// </summary>
    private (string? IncomingSegmentId, bool Hit) HitTestTransitionChip(double posX, double posY, out Rect chipRect)
    {
        chipRect = default;
        var model = Model;
        if (model is null || model.Segments.Count < 2 || VideoTrackCanvas is null)
            return (null, false);

        if (VideoTrackIndexFromY(model, posY) != TimelineModel.BaseTrackIndex)
            return (null, false);

        float w = (float)VideoTrackCanvas.ActualWidth;
        int trackCount = VideoDisplayTrackCount(model);
        var (rowY, rowH, pad) = VideoTrackRowBounds(TimelineModel.BaseTrackIndex, trackCount);
        float clipY = rowY + pad;
        float clipH = rowH - pad * 2;

        TimelineSegment? prevSegment = null;
        float prevX1 = 0, prevX2 = 0;

        foreach (var segment in model.BaseSegments)
        {
            var (x1, x2) = GetSegmentDisplayX(segment);

            if (prevSegment is not null &&
                IsTransitionChipEligible(prevX1, prevX2, x1, x2) &&
                prevSegment.Id != _draggedSegmentId && segment.Id != _draggedSegmentId)
            {
                float boundaryX = x1;
                if (boundaryX >= -TransitionChipWidth && boundaryX <= w + TransitionChipWidth)
                {
                    var rect = GetTransitionChipRect(boundaryX, clipY, clipH);
                    if (rect.Contains(new Point(posX, posY)))
                    {
                        chipRect = rect;
                        return (segment.Id, true);
                    }
                }
            }

            prevSegment = segment;
            prevX1 = x1;
            prevX2 = x2;
        }

        return (null, false);
    }

    // ── Transition chip hover tooltip ────────────────────────────────────────────────

    /// <summary>How long the pointer must linger on a chip before its tooltip appears.</summary>
    private static readonly TimeSpan TransitionChipHoverDelay = TimeSpan.FromMilliseconds(450);

    private ToolTip? _transitionChipToolTip;
    private DispatcherTimer? _transitionChipHoverTimer;
    private string? _hoveredTransitionChipId;
    private Rect _hoveredTransitionChipRect;

    /// <summary>
    /// Tracks which chip (if any) the pointer is over and schedules its tooltip.
    /// </summary>
    /// <remarks>
    /// The track is a <c>CanvasControl</c>, so chips are drawn pixels rather than elements and
    /// cannot carry an attached <c>ToolTipService.ToolTip</c> of their own — the linger delay
    /// and placement are therefore driven manually from hit-testing. Re-scheduling only when
    /// the hovered chip actually <em>changes</em> keeps the timer from restarting on every
    /// pointer move, which would mean the tooltip never fired while the pointer drifted a pixel.
    /// </remarks>
    private void UpdateTransitionChipHover(string? incomingSegmentId, Rect chipRect)
    {
        if (incomingSegmentId == _hoveredTransitionChipId)
        {
            _hoveredTransitionChipRect = chipRect;
            return;
        }

        _hoveredTransitionChipId = incomingSegmentId;
        _hoveredTransitionChipRect = chipRect;
        HideTransitionChipToolTip();

        if (incomingSegmentId is null)
            return;

        if (_transitionChipHoverTimer is null)
        {
            _transitionChipHoverTimer = new DispatcherTimer { Interval = TransitionChipHoverDelay };
            _transitionChipHoverTimer.Tick += (_, _) => ShowTransitionChipToolTip();
        }

        _transitionChipHoverTimer.Stop();
        _transitionChipHoverTimer.Start();
    }

    private void ShowTransitionChipToolTip()
    {
        _transitionChipHoverTimer?.Stop();

        if (_hoveredTransitionChipId is not { } id || VideoTrackCanvas is null)
            return;

        var segment = Model?.Segments.FirstOrDefault(s => s.Id == id);
        if (segment is null)
            return;

        _transitionChipToolTip ??= new ToolTip
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top,
        };
        _transitionChipToolTip.Content = DescribeTransitionForTooltip(segment.InTransition);
        _transitionChipToolTip.PlacementTarget = VideoTrackCanvas;
        _transitionChipToolTip.PlacementRect = _hoveredTransitionChipRect;

        // Rooting it through ToolTipService keeps the popup owned by the canvas, so it is torn
        // down with the control rather than leaking if the page is navigated away mid-hover.
        ToolTipService.SetToolTip(VideoTrackCanvas, _transitionChipToolTip);
        _transitionChipToolTip.IsOpen = true;
    }

    private void HideTransitionChipToolTip()
    {
        _transitionChipHoverTimer?.Stop();

        if (_transitionChipToolTip is not null)
            _transitionChipToolTip.IsOpen = false;

        // Detached whenever the pointer is not on a chip, so the framework's own hover handling
        // can never surface this tooltip over unrelated parts of the track.
        if (VideoTrackCanvas is not null)
            ToolTipService.SetToolTip(VideoTrackCanvas, null);
    }

    private void VideoTrack_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveredTransitionChipId = null;
        HideTransitionChipToolTip();
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

    // ── Cross-track selection mutual exclusion ──
    // The primary/zoom/camera/text-overlay/transition tracks each own an independent
    // _selected*Id field and Clear*Selection() method. Any selection path that only
    // clears a hand-picked subset of the others (as the transition chip's first cut
    // did — clearing clip/segment but not zoom/camera/text-overlay) silently leaves
    // two things looking selected at once. ClearOtherSelections is the single place
    // that enforces "exactly one of these six is ever selected" — every selection AND
    // deselection path below must route through it rather than re-deriving its own
    // subset of Clear*Selection() calls.

    /// <summary>The distinct selection surfaces this control exposes. See <see cref="ClearOtherSelections"/>.</summary>
    private enum SelectionKind { None, Clip, Segment, Zoom, Camera, TextOverlay, Transition, InsertedAudio }

    /// <summary>
    /// Re-entrancy guard for <see cref="ClearOtherSelections"/>. EditorPage's
    /// *Selected event handlers can call back into this control (e.g. <see cref="SelectSegment"/>
    /// to normalise state after committing an edit), which would otherwise start a
    /// second clearing pass in the middle of the first and risk double-firing or
    /// mis-ordering the null events consumers rely on.
    /// </summary>
    private bool _isClearingOtherSelections;

    /// <summary>
    /// Clears every selection kind except <paramref name="keep"/> (pass
    /// <see cref="SelectionKind.None"/> to clear all six), raising each cleared kind's
    /// "null" selection event — but only for kinds that were actually selected, so a
    /// routine click doesn't fire a storm of redundant null events (EditorPage's
    /// handlers may do real work, such as collapsing a property pane, on each one).
    /// </summary>
    private void ClearOtherSelections(SelectionKind keep)
    {
        if (_isClearingOtherSelections) return;
        _isClearingOtherSelections = true;
        try
        {
            if (keep != SelectionKind.Clip) ClearClipSelection();
            if (keep != SelectionKind.Segment) ClearSegmentSelectionOnly();
            if (keep != SelectionKind.Zoom) ClearZoomSelection();
            if (keep != SelectionKind.Camera) ClearCameraSelection();
            if (keep != SelectionKind.TextOverlay) ClearTextOverlaySelection();
            if (keep != SelectionKind.Transition) ClearTransitionSelection();
            if (keep != SelectionKind.InsertedAudio) ClearInsertedAudioSelection();
        }
        finally
        {
            _isClearingOtherSelections = false;
        }
    }

    /// <summary>
    /// Clears the primary-track segment selection only, firing <see cref="SegmentSelected"/>
    /// with null when it was actually selected. Factored out of what used to be an
    /// inline pattern duplicated at every segment-track call site, so
    /// <see cref="ClearOtherSelections"/> has a single symmetric Clear* to call here,
    /// mirroring <see cref="ClearZoomSelection"/> / <see cref="ClearCameraSelection"/> /
    /// <see cref="ClearTextOverlaySelection"/>.
    /// </summary>
    private void ClearSegmentSelectionOnly()
    {
        if (_selectedSegmentId is not null)
        {
            _selectedSegmentId = null;
            SegmentSelected?.Invoke(this, null);
            VideoTrackCanvas?.Invalidate();
        }
    }

    /// <summary>Currently selected segment ID for text slide highlighting.</summary>
    private string? _selectedSegmentId;

    /// <summary>Sets the selected segment ID (called from EditorPage).</summary>
    public void SelectSegment(string? segmentId)
    {
        ClearOtherSelections(segmentId is null ? SelectionKind.None : SelectionKind.Segment);
        _selectedSegmentId = segmentId;
        VideoTrackCanvas?.Invalidate();

        // The recorded-audio lanes outline the selected segment's block too, so they have to
        // repaint with the video track or the highlight is left on the previous block.
        InvalidateAudioLanes();
    }

    /// <summary>Raised when a text slide segment is clicked on the timeline.</summary>
    public event EventHandler<string?>? SegmentSelected;

    /// <summary>
    /// Id of the incoming segment whose boundary chip is selected (i.e. the boundary
    /// between that segment and its predecessor — see <see cref="TimelineSegment.InTransition"/>),
    /// or null when no boundary is selected. This is <em>this control's</em> selection
    /// state; T6's properties pane reads <see cref="TransitionConfig"/> off
    /// <c>model.Segments.First(s => s.Id == id).InTransition</c> using this Id.
    /// </summary>
    private string? _selectedTransitionId;

    /// <summary>Sets the selected transition boundary by its incoming segment's Id (called from EditorPage).</summary>
    public void SelectTransition(string? incomingSegmentId)
    {
        if (_selectedTransitionId == incomingSegmentId) return;
        ClearOtherSelections(incomingSegmentId is null ? SelectionKind.None : SelectionKind.Transition);
        _selectedTransitionId = incomingSegmentId;
        VideoTrackCanvas?.Invalidate();
    }

    /// <summary>Clears the selected transition boundary, mirroring <see cref="ClearZoomSelection"/> et al.</summary>
    public void ClearTransitionSelection()
    {
        if (_selectedTransitionId is not null)
        {
            _selectedTransitionId = null;
            TransitionSelected?.Invoke(this, null);
            VideoTrackCanvas?.Invalidate();
        }
    }

    /// <summary>
    /// Raised when a transition boundary chip is selected or deselected. The payload is
    /// the incoming segment's Id (null = deselected) — the same Id that carries the
    /// boundary's <see cref="TransitionConfig"/> via <see cref="TimelineSegment.InTransition"/>.
    /// </summary>
    public event EventHandler<string?>? TransitionSelected;

    /// <summary>
    /// Raised when the user requests removal of a boundary's transition (right-click on
    /// a configured chip). Carries the incoming segment's Id. This control never mutates
    /// the model itself — the page owns the edit + undo, exactly as with
    /// <see cref="ZoomSegmentRemoveRequested"/> / <see cref="TextOverlayRemoveRequested"/>.
    /// </summary>
    public event EventHandler<string>? TransitionRemoveRequested;

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
        var sorted = model.ZoomKeyframes
            .OrderBy(k => k.Timestamp)
            .ThenBy(k => k.Start)
            .ToList();

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

        // Paint the selected segment LAST so it sits on top of everything it overlaps. Zoom
        // segments overlap by design (that is what drives a handoff), and the one being edited
        // has to be the one you can see and grab. Hit-testing gives its edges the same priority.
        var drawOrder = sorted.Where(k => k.Id != _selectedZoomKeyframeId).ToList();
        var selectedKeyframe = sorted.FirstOrDefault(k => k.Id == _selectedZoomKeyframeId);
        if (selectedKeyframe is not null)
            drawOrder.Add(selectedKeyframe);

        bool hasSelection = selectedKeyframe is not null;

        foreach (var kf in drawOrder)
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
            bool muted = hasSelection && !isSelected;

            // Fill
            var fillColor = isSelected ? ZoomSegmentSelectedFill
                : isEditable ? ZoomSegmentFill
                : ZoomSegmentAutoFill;
            if (muted) fillColor = MutedZoomColor(fillColor);

            using var roundedRect = CanvasGeometry.CreateRoundedRectangle(ds, x1, segY, segW, segH, ZoomSegmentCornerRadius, ZoomSegmentCornerRadius);
            ds.FillGeometry(roundedRect, fillColor);

            // Border
            var borderColor = isSelected ? ZoomSegmentSelectedBorder : ZoomSegmentBorder;
            if (muted) borderColor = MutedZoomColor(borderColor);
            float borderWidth = isSelected ? 1.5f : 1f;
            ds.DrawGeometry(roundedRect, borderColor, borderWidth);

            // Zoom level text
            if (segW > 30)
            {
                string label = $"{kf.ZoomLevel:0.#}x";
                ds.DrawText(label, x1 + 6, segY + segH / 2 - 7,
                    muted ? MutedZoomColor(ZoomSegmentTextColor) : ZoomSegmentTextColor,
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

        DrawZoomLinkedSegmentIndicators(ds, sorted, w, h);

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

        if (!double.IsNaN(_segmentSnapGuideX))
        {
            var snapGuideColor = Color.FromArgb(255, 255, 214, 10);
            ds.DrawLine((float)_segmentSnapGuideX, 0, (float)_segmentSnapGuideX, h, snapGuideColor, 1f);
        }
    }

    private void DrawZoomLinkedSegmentIndicators(
        CanvasDrawingSession ds,
        IReadOnlyList<ZoomKeyframe> sorted,
        float w,
        float h)
    {
        if (sorted.Count < 2)
            return;

        var previousByPath = new List<ZoomKeyframe>();
        float segY = ZoomSegmentVerticalPadding;
        float segH = h - ZoomSegmentVerticalPadding * 2;
        float bridgeY = segY + Math.Max(2f, segH - 5f);

        foreach (var current in sorted)
        {
            int pathIndex = previousByPath.FindIndex(previous => SameZoomPath(previous, current));
            if (pathIndex < 0)
            {
                previousByPath.Add(current);
                continue;
            }

            var earlier = previousByPath[pathIndex];
            previousByPath[pathIndex] = current;

            if (!ZoomCameraPath.AreLinked(earlier, current))
                continue;

            float fromX = (float)GetZoomSegmentEndX(earlier);
            float toX = (float)GetZoomSegmentStartX(current);
            if (float.IsNaN(fromX) || float.IsNaN(toX))
                continue;

            float left = Math.Min(fromX, toX);
            float right = Math.Max(fromX, toX);
            if (right < 0 || left > w)
                continue;

            float mid = (fromX + toX) / 2f;
            if (right - left < 6f)
            {
                left = mid - 3f;
                right = mid + 3f;
            }

            left = Math.Clamp(left, 0, w);
            right = Math.Clamp(right, 0, w);
            if (right <= left)
                continue;

            using var stroke = new CanvasStrokeStyle
            {
                StartCap = CanvasCapStyle.Round,
                EndCap = CanvasCapStyle.Round,
            };
            ds.DrawLine(left, bridgeY, right, bridgeY, ZoomSegmentLinkedConnector, 3f, stroke);
        }
    }

    private static bool SameZoomPath(ZoomKeyframe a, ZoomKeyframe b)
        => a.IsManual == b.IsManual &&
           string.Equals(a.SourceVideoFilePath, b.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the video segment that owns a zoom keyframe: the segment matching the
    /// keyframe's source file whose source range contains its Timestamp (falling back
    /// to the first file-matching segment). This anchors the keyframe to one occurrence
    /// of a recording even when that source was duplicated or reordered.
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
    /// Maps a zoom keyframe's source time to an output X, anchored to the video segment
    /// that owns it (primary or appended). An edge may traverse directly adjacent,
    /// source-contiguous pieces of that same occurrence so mixed-speed slices map
    /// piecewise; it never resolves each edge against an arbitrary first file match.
    /// </summary>
    private double ZoomKeyframeTimeToX(ZoomKeyframe kf, TimeSpan sourceTime)
    {
        var model = Model;
        if (model is null) return double.NaN;
        if (model.Segments.Count == 0) return TimeToX(sourceTime);

        var seg = OwningSegmentForKeyframe(kf);
        if (seg is null) return double.NaN;

        return TimeToX(model.MapSourceTimeFromOwningSegment(seg, sourceTime));
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
        if (model is null || ZoomTrackCanvas is null) return (null, ZoomHitTarget.None);

        float h = (float)ZoomTrackCanvas.ActualHeight;
        float segY = ZoomSegmentVerticalPadding;
        float segH = h - ZoomSegmentVerticalPadding * 2;

        // Check if Y is within segment vertical bounds
        if (posY < segY || posY > segY + segH)
            return (null, ZoomHitTarget.None);

        // Check segments in reverse order (last drawn = on top)
        var sorted = model.ZoomKeyframes
            .OrderBy(k => k.Timestamp)
            .ThenBy(k => k.Start)
            .ToList();

        // The selected segment's EDGES win over everything else, because it is painted on top
        // and its resize handles are drawn there — without this, an overlapping neighbour's body
        // claims the click first and the handles become impossible to grab, which is exactly the
        // case overlapping zoom segments create.
        // Deliberately edges only: letting its BODY win too would trap the selection, since a
        // segment sitting underneath could then never be clicked to select it.
        if (_selectedZoomKeyframeId is not null)
        {
            var selected = sorted.FirstOrDefault(k => k.Id == _selectedZoomKeyframeId);
            if (selected is not null)
            {
                float sx1 = (float)ZoomKeyframeTimeToX(selected, selected.Start);
                float sx2 = (float)ZoomKeyframeTimeToX(selected, selected.End);
                if (!float.IsNaN(sx1) && Math.Abs(posX - sx1) <= ZoomSegmentEdgeHitWidth)
                    return (selected.Id, ZoomHitTarget.LeftEdge);
                if (!float.IsNaN(sx2) && Math.Abs(posX - sx2) <= ZoomSegmentEdgeHitWidth)
                    return (selected.Id, ZoomHitTarget.RightEdge);
            }
        }

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
        if (sender is not CanvasControl canvas || TimeRulerCanvas is null) return;
        var pos = e.GetCurrentPoint(canvas).Position;
        _segmentSnapGuideX = double.NaN;

        var (hitId, hitTarget) = HitTestZoomSegment(pos.X, pos.Y);

        if (hitId is not null)
        {
            // Select the segment — clears clip/segment/camera/text-overlay/transition.
            ClearOtherSelections(SelectionKind.Zoom);
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
            // Deselect any selected segment (and every other selection kind).
            ClearOtherSelections(SelectionKind.None);

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
        var model = Model;
        if (sender is not CanvasControl canvas || TimeRulerCanvas is null || model is null) return;
        var pos = e.GetCurrentPoint(canvas).Position;
        double clampedX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
        bool snap = !IsAltDown();

        switch (_dragMode)
        {
            case DragMode.ZoomSegmentBody:
            {
                var kf = model.ZoomKeyframes.FirstOrDefault(k => k.Id == _selectedZoomKeyframeId);
                double originalStartX = kf is not null
                    ? ZoomKeyframeTimeToX(kf, _zoomDragOriginalStart)
                    : double.NaN;
                if (kf is not null && !double.IsNaN(originalStartX))
                {
                    double leftX = originalStartX + (clampedX - _zoomDragStartX);
                    double snappedLeftX = SnapZoomX(model, leftX, kf.Id, kf.SourceVideoFilePath, snap);
                    _zoomDragCurrentX = _zoomDragStartX + (snappedLeftX - originalStartX);
                }
                else
                {
                    _segmentSnapGuideX = double.NaN;
                    _zoomDragCurrentX = clampedX;
                }
                SetCursor(InputSystemCursorShape.SizeAll);
                InvalidateAll();
                break;
            }

            case DragMode.ZoomSegmentLeftEdge:
            case DragMode.ZoomSegmentRightEdge:
                _zoomDragCurrentX = SnapZoomX(model, clampedX, _selectedZoomKeyframeId, SelectedZoomKeyframeFile, snap);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateAll();
                break;

            case DragMode.ZoomSegmentCreate:
                _segmentSnapGuideX = double.NaN;
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
                _segmentSnapGuideX = double.NaN;
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
                _segmentSnapGuideX = double.NaN;
                if (_dragMode == DragMode.Playhead)
                    PlayheadPosition = XToTime(pos.X);
                break;
        }
    }

    private void ZoomTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas || TimeRulerCanvas is null)
        {
            _segmentSnapGuideX = double.NaN;
            _dragMode = DragMode.None;
            return;
        }

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
        _segmentSnapGuideX = double.NaN;
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
        ClearOtherSelections(SelectionKind.Zoom);
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

    /// <summary>
    /// Pushes the model's persisted mute state onto the track labels.
    /// </summary>
    /// <remarks>
    /// The glyphs were previously only ever written by the click handlers, so a project
    /// restored with a muted track showed an UNMUTED icon: the flag round-tripped through
    /// the package correctly, but nothing ever applied it to the UI. Call this whenever the
    /// model is (re)attached — the labels are the only place this state is visible.
    /// </remarks>
    public void SyncAudioMuteVisuals()
    {
        if (Model is null) return;

        // E767 = Volume3 (unmuted), E74F = Mute
        if (AudioMuteIcon is not null)
            AudioMuteIcon.Glyph = Model.IsMuted(AudioMixChannel.System) ? "\uE74F" : "\uE767";
        if (MicMuteIcon is not null)
            MicMuteIcon.Glyph = Model.IsMuted(AudioMixChannel.Mic) ? "\uE74F" : "\uE767";

        // The inserted lanes keep their own identifying glyph (a person for voice, a note for
        // music) and show mute by dimming instead, so the lane stays recognisable at a glance.
        if (VoiceOverMuteIcon is not null)
            VoiceOverMuteIcon.Opacity = Model.IsMuted(AudioMixChannel.VoiceOver) ? 0.4 : 1.0;
        if (MusicMuteIcon is not null)
            MusicMuteIcon.Opacity = Model.IsMuted(AudioMixChannel.Music) ? 0.4 : 1.0;

        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
        VoiceOverTrackCanvas?.Invalidate();
        MusicTrackCanvas?.Invalidate();
    }

    /// <summary>
    /// Raised when a track's volume or mute changes, with the channel that changed.
    /// </summary>
    public event EventHandler<AudioMixChannel>? AudioChannelMixChanged;

    /// <summary>
    /// Opens the volume/mute flyout for whichever track label was clicked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flyout is built in code rather than declared in XAML, deliberately. A declared
    /// <c>Slider</c>/<c>ToggleSwitch</c> would need its value set from the model, and setting
    /// it in XAML fires <c>ValueChanged</c>/<c>Toggled</c> during <c>InitializeComponent</c> —
    /// before the suppress flag exists and before the named fields are assigned. That is the
    /// documented cause of a hang after recording in this codebase, so this path never gives
    /// it the chance: the controls are created, populated, and only then subscribed.
    /// </para>
    /// <para>
    /// One handler for all four labels, keyed by the button's <c>Tag</c>, because every track
    /// exposes exactly the same two controls — see <see cref="AudioMixChannel"/>.
    /// </para>
    /// </remarks>
    private void AudioTrackLabel_Click(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        if (sender is not FrameworkElement source) return;
        if (!Enum.TryParse<AudioMixChannel>(source.Tag as string, out var channel)) return;

        // A fixed content width, with every child stretching to it. The slider previously
        // carried its own smaller Width, which left it visibly indented relative to the
        // labels above — a fader that does not line up with its own readout reads as broken.
        const double ContentWidth = 220;

        var panel = new StackPanel { Spacing = 6, Width = ContentWidth };

        panel.Children.Add(new TextBlock
        {
            Text = ChannelDisplayName(channel),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // Readout on the left, mute on the right — the compact mixer-strip layout, and it
        // keeps the slider's full width free below.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var volumeLabel = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(volumeLabel);

        var muteIcon = new FontIcon { FontSize = 14 };
        var muteButton = new Button
        {
            Content = muteIcon,
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Grid.SetColumn(muteButton, 1);
        header.Children.Add(muteButton);
        panel.Children.Add(header);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
            // Assigned BEFORE the handler is attached, so populating it cannot raise a change.
            Value = Math.Round(Math.Clamp(Model.GetVolume(channel), 0, 1) * 100),
            IsEnabled = !Model.IsMuted(channel),
        };

        void RefreshMuteVisual()
        {
            bool muted = Model!.IsMuted(channel);
            // E74F = Mute, E767 = Volume3. The single control shows the CURRENT state and
            // toggles it, rather than a checkbox that has to be read as a setting.
            muteIcon.Glyph = muted ? "\uE74F" : "\uE767";
            muteIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                muted ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush"];
            ToolTipService.SetToolTip(muteButton, muted ? "Unmute" : "Mute");
            volumeLabel.Text = muted ? "Muted" : $"Volume  {slider.Value:F0}%";
        }

        RefreshMuteVisual();

        slider.ValueChanged += (_, args) =>
        {
            if (Model is null) return;
            Model.SetVolume(channel, args.NewValue / 100.0);
            RefreshMuteVisual();
            AudioChannelMixChanged?.Invoke(this, channel);
        };

        muteButton.Click += (_, _) =>
        {
            if (Model is null) return;
            bool muted = !Model.IsMuted(channel);
            Model.SetMuted(channel, muted);
            // Disabled rather than zeroed, so unmuting restores the level the user set
            // instead of coming back silent.
            slider.IsEnabled = !muted;
            RefreshMuteVisual();
            SyncAudioMuteVisuals();
            AudioChannelMixChanged?.Invoke(this, channel);
        };

        panel.Children.Add(slider);

        new Flyout { Content = panel }.ShowAt(source);
    }

    private static string ChannelDisplayName(AudioMixChannel channel) => channel switch
    {
        AudioMixChannel.System => "System audio",
        AudioMixChannel.Mic => "Microphone",
        AudioMixChannel.VoiceOver => "Voice over lane",
        AudioMixChannel.Music => "Music lane",
        _ => "Audio",
    };

    private void AudioTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, isMic: false, AudioWaveformColor, AudioEnvelopeColor,
            Model?.EffectiveVolume(AudioMixChannel.System) <= 0);
    }

    private void MicTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformTrack(sender, args, isMic: true, MicWaveformColor, MicEnvelopeColor,
            Model?.EffectiveVolume(AudioMixChannel.Mic) <= 0);
    }

    /// <summary>
    /// Right-clicking a recorded-audio block acts on the segment that owns it.
    /// </summary>
    /// <remarks>
    /// Recorded audio cannot be moved or trimmed independently of its segment — it IS the
    /// segment's audio — so this menu offers the edits that are actually per-segment: what
    /// the audio does while the segment is speed-adjusted. The block is also selected first,
    /// exactly like right-clicking the segment itself, so the properties pane follows the
    /// thing the menu is about.
    /// </remarks>
    private void RecordedAudioTrack_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;

        var model = Model;
        if (model is null) return;

        var pos = e.GetPosition(canvas);
        var time = XToTime(pos.X);

        // Resolve the segment whose block is actually drawn under the cursor. The draw loop
        // paints every VideoSegment in list order, and overlay inserts are appended, so an
        // overlay's block lands on top — excluding overlay segments here would act on the
        // base segment hidden underneath the one the user aimed at. Highest TrackIndex wins,
        // matching TimelineModel.GetSegmentAtTime.
        VideoSegment? segment = null;
        foreach (var candidate in model.Segments.OfType<VideoSegment>())
        {
            if (time < candidate.Start || time >= candidate.End) continue;
            if (segment is null || candidate.TrackIndex >= segment.TrackIndex)
                segment = candidate;
        }

        if (segment is null) return;

        ClearOtherSelections(SelectionKind.Segment);
        if (_selectedSegmentId != segment.Id)
        {
            _selectedSegmentId = segment.Id;
            SegmentSelected?.Invoke(this, segment.Id);
        }
        InvalidateAudioLanes();
        VideoTrackCanvas?.Invalidate();

        var menu = new MenuFlyout();
        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        AppendSegmentAudioSection(menu, segment, speed, includeLeadingSeparator: false);

        menu.ShowAt(canvas, pos);

        // Must not bubble: the lane sits inside the timeline's own right-tap handling.
        e.Handled = true;
    }

    /// <summary>Repaints both recorded-audio lanes (per-segment blocks live on them).</summary>
    private void InvalidateAudioLanes()
    {
        AudioTrackCanvas?.Invalidate();
        MicTrackCanvas?.Invalidate();
    }

    // ─────────────────────── Inserted audio lanes (voice / music) ───────────────────────
    // Two lanes, one per AudioTrackKind, so a voice-over and a music bed that overlap in
    // time are still independently grabbable. Unlike every other track here, these are
    // positioned in OUTPUT time (TimeToX/XToTime directly, never SourceTimeToX) — an
    // inserted track is pinned to the finished timeline, not to the footage.

    /// <summary>
    /// One inserted voice-over/music track as the timeline draws it: an output-timeline
    /// block, optionally with a waveform the host generated for it.
    /// </summary>
    /// <param name="Id">The <c>AudioTrack.Id</c> this block came from.</param>
    /// <param name="Name">Label drawn on the block.</param>
    /// <param name="Start">Output-timeline start.</param>
    /// <param name="Duration">How long it sounds for.</param>
    /// <param name="TrimStart">How far into the SOURCE FILE this block starts playing.</param>
    /// <param name="SourceDuration">
    /// Full length of the source file, so a drag preview can stop exactly where the
    /// operation will clamp it instead of showing an edit that will not survive release.
    /// </param>
    /// <param name="IsMusic">Which lane it belongs to.</param>
    /// <param name="IsMuted">Drawn dimmed, matching the recorded tracks' muted state.</param>
    /// <param name="Waveform">
    /// Peaks spanning the WHOLE source file (not just this block's slice), or null while they
    /// build. Whole-file deliberately: the block is a window onto the file, so trimming must
    /// reveal a different part of the same peaks rather than rescale them — see
    /// <see cref="DrawInsertedAudioLane"/>.
    /// </param>
    /// <param name="WaveformDurationSeconds">Source-time span <paramref name="Waveform"/> covers.</param>
    public readonly record struct InsertedAudioLaneItem(
        string Id,
        string Name,
        TimeSpan Start,
        TimeSpan Duration,
        TimeSpan TrimStart,
        TimeSpan SourceDuration,
        bool IsMusic,
        bool IsMuted,
        float[]? Waveform,
        double WaveformDurationSeconds)
    {
        /// <summary>Output-timeline instant at which this file's second 0 would sit.</summary>
        public TimeSpan FileOriginTime => Start - TrimStart;

        /// <summary>Output-timeline instant at which the file runs out, or null when unknown.</summary>
        public TimeSpan? FileEndTime =>
            SourceDuration > TimeSpan.Zero ? FileOriginTime + SourceDuration : null;
    }

    private IReadOnlyList<InsertedAudioLaneItem> _insertedAudioTracks = [];

    /// <summary>
    /// Sub-row each inserted block is packed into within its lane, by track id.
    /// </summary>
    /// <remarks>
    /// Two music beds that overlap in time drew on top of each other, which made both
    /// unreadable and the lower one hard to grab. Overlapping blocks are therefore stacked
    /// into sub-rows and the lane grows taller — the timeline's own grid row is
    /// <c>Auto</c>-sized, so the control simply gets taller rather than clipping anything.
    /// </remarks>
    private readonly Dictionary<string, int> _insertedAudioRowByTrackId = new(StringComparer.Ordinal);
    private int _voiceSubRowCount = 1;
    private int _musicSubRowCount = 1;

    /// <summary>Height of one stacked sub-row within an inserted-audio lane.</summary>
    private const double InsertedAudioSubRowHeight = 30;

    /// <summary>
    /// Most sub-rows a lane will grow to. Past this, blocks share the last row again — an
    /// unbounded stack would push the video track off the top of a small window, which is a
    /// worse problem than two overlapping beds.
    /// </summary>
    private const int InsertedAudioMaxSubRows = 5;

    /// <summary>
    /// Packs each lane's blocks into sub-rows so no two that overlap in time share one.
    /// </summary>
    /// <remarks>
    /// Packing uses the PLAYED range only — the dimmed full-file extent routinely overlaps
    /// everything and would force every block onto its own row. The algorithm itself lives in
    /// <see cref="AudioLaneLayout"/> so it can be tested: it decides what is CLICKABLE, not
    /// merely what is pretty, since hit testing resolves a pointer to a row before it looks
    /// at any block.
    /// </remarks>
    private void LayoutInsertedAudioRows()
    {
        _insertedAudioRowByTrackId.Clear();
        _voiceSubRowCount = PackLane(music: false);
        _musicSubRowCount = PackLane(music: true);
    }

    private int PackLane(bool music)
    {
        var blocks = ItemsForLane(music)
            .Select(i => new LaneBlock(i.Id, i.Start, i.Start + i.Duration));

        var rows = AudioLaneLayout.PackIntoRows(blocks, InsertedAudioMaxSubRows);
        foreach (var (id, row) in rows)
            _insertedAudioRowByTrackId[id] = row;

        return AudioLaneLayout.RowCount(rows);
    }

    private int SubRowFor(string trackId)
        => _insertedAudioRowByTrackId.TryGetValue(trackId, out int row) ? row : 0;

    /// <summary>Raised when an inserted audio block is selected, or null when deselected.</summary>
    public event EventHandler<string?>? InsertedAudioTrackSelected;

    /// <summary>Raised when a block is dragged to a new OUTPUT-timeline start.</summary>
    public event EventHandler<(string Id, TimeSpan NewStart)>? InsertedAudioTrackMoved;

    /// <summary>Raised when an edge is dragged. <c>IsStartEdge</c> distinguishes left from right.</summary>
    public event EventHandler<(string Id, bool IsStartEdge, TimeSpan NewEdgeTime)>? InsertedAudioTrackResized;

    /// <summary>
    /// Raised on right-click, so the host can offer split/mute/remove. Carries the canvas and
    /// the click point so the host can anchor its flyout to the block that was clicked rather
    /// than to the control as a whole.
    /// </summary>
    public event EventHandler<(string Id, FrameworkElement Target, Point Position)>? InsertedAudioTrackContextRequested;

    /// <summary>
    /// The inserted voice-over/music blocks to draw, published by the editor.
    /// </summary>
    /// <remarks>
    /// A control-level projection rather than reading <c>TimelineModel.AudioTracks</c>
    /// directly, because it also carries the decoded waveform peaks — which are a UI
    /// artefact the host generates in the background, not model state that belongs in the
    /// serialised <c>.musio</c> manifest.
    /// </remarks>
    public IReadOnlyList<InsertedAudioLaneItem> InsertedAudioTracks
    {
        get => _insertedAudioTracks;
        set
        {
            _insertedAudioTracks = value ?? [];

            // A block the host just removed (or undid) must not stay selected, or a later
            // Delete would act on an id that no longer exists.
            if (_selectedInsertedAudioTrackId is not null
                && !_insertedAudioTracks.Any(t => t.Id == _selectedInsertedAudioTrackId))
            {
                _selectedInsertedAudioTrackId = null;
            }

            // Before UpdateTrackVisibility: the lane heights are derived from the row counts.
            LayoutInsertedAudioRows();

            UpdateTrackVisibility();
            VoiceOverTrackCanvas?.Invalidate();
            MusicTrackCanvas?.Invalidate();
        }
    }

    private string? _selectedInsertedAudioTrackId;

    /// <summary>Currently selected inserted audio block, or null.</summary>
    public string? SelectedInsertedAudioTrackId
    {
        get => _selectedInsertedAudioTrackId;
        set
        {
            if (_selectedInsertedAudioTrackId == value) return;
            _selectedInsertedAudioTrackId = value;
            VoiceOverTrackCanvas?.Invalidate();
            MusicTrackCanvas?.Invalidate();
        }
    }

    /// <summary>Clears the inserted-audio selection, firing the null event when there was one.</summary>
    public void ClearInsertedAudioSelection()
    {
        if (_selectedInsertedAudioTrackId is null) return;
        _selectedInsertedAudioTrackId = null;
        InsertedAudioTrackSelected?.Invoke(this, null);
        VoiceOverTrackCanvas?.Invalidate();
        MusicTrackCanvas?.Invalidate();
    }

    // ── Drag state (shared by both lanes: only one block can be dragged at a time) ──
    private double _audioTrackDragStartX = double.NaN;
    private double _audioTrackDragCurrentX = double.NaN;
    private TimeSpan _audioTrackDragOriginalStart;
    private TimeSpan _audioTrackDragOriginalEnd;

    /// <summary>Vertical inset of a block within its lane.</summary>
    private const float InsertedAudioVerticalPadding = 4f;

    /// <summary>
    /// Grab zone for an inserted block's trim edges. Wider than
    /// <see cref="SegmentEdgeHitWidth"/> because these blocks carry a drawn handle the user
    /// aims at, and because an audio trim is the primary gesture on these lanes.
    /// </summary>
    private const double InsertedAudioEdgeHitWidth = 10.0;

    /// <summary>Width of the drawn trim handle at each end of a block.</summary>
    private const float InsertedAudioHandleWidth = 4f;

    private IEnumerable<InsertedAudioLaneItem> ItemsForLane(bool music)
        => _insertedAudioTracks.Where(t => t.IsMusic == music);

    private bool LaneIsMusic(CanvasControl canvas) => ReferenceEquals(canvas, MusicTrackCanvas);

    /// <summary>
    /// A block's on-screen extent, with each edge clamped into the visible canvas.
    /// </summary>
    /// <remarks>
    /// <b>Clamping is what makes trimming possible at all for long audio.</b> The ruler spans
    /// only <see cref="TimelineModel.DisplayDuration"/> — the VIDEO's length — so a music bed
    /// or voice-over longer than the footage runs past the right edge of the canvas, and
    /// (unlike a zoomed timeline) there is nowhere to scroll to reach it. Hit-testing the raw
    /// coordinates therefore made that edge permanently ungrabbable: every press on the block
    /// resolved to Body, so it could be moved but never trimmed. Clamping brings both handles
    /// back onto the canvas, and <paramref name="clippedStart"/>/<paramref name="clippedEnd"/>
    /// let the drawing code mark an edge that is really somewhere off-screen.
    /// </remarks>
    private (double X1, double X2, bool clippedStart, bool clippedEnd) VisibleExtent(
        double rawX1, double rawX2, double canvasWidth)
    {
        double x1 = Math.Max(rawX1, 0);
        double x2 = Math.Min(rawX2, canvasWidth);
        return (x1, x2, rawX1 < 0, rawX2 > canvasWidth);
    }

    /// <summary>
    /// Edge grab width for a block of <paramref name="blockWidth"/> pixels: narrowed for small
    /// blocks so the two edge zones can never meet and swallow the body (which would make a
    /// short block impossible to MOVE), and never smaller than a few pixels.
    /// </summary>
    private static double EdgeHitWidthFor(double blockWidth)
        => Math.Clamp(blockWidth / 3.0, 3.0, InsertedAudioEdgeHitWidth);

    /// <summary>
    /// Left edge X of a block, showing the in-flight drag position while one is being
    /// dragged so the block tracks the pointer before the edit is committed on release.
    /// </summary>
    /// <remarks>
    /// An edge preview is clamped to the same bounds
    /// <see cref="TrimAudioTrackOperation"/> will apply, so the block stops where the trim
    /// really stops: dragging the left edge further left than the file's own start would
    /// otherwise preview audio that does not exist and then snap back on release.
    /// </remarks>
    private double GetInsertedAudioStartX(InsertedAudioLaneItem item)
    {
        if (item.Id == _selectedInsertedAudioTrackId && !double.IsNaN(_audioTrackDragCurrentX))
        {
            if (_dragMode == DragMode.InsertedAudioBody)
                return TimeToX(_audioTrackDragOriginalStart) + (_audioTrackDragCurrentX - _audioTrackDragStartX);

            if (_dragMode == DragMode.InsertedAudioLeftEdge)
            {
                double min = TimeToX(item.FileOriginTime);
                double max = TimeToX(item.Start + item.Duration - AudioTrackEditing.MinDuration);
                return Math.Clamp(_audioTrackDragCurrentX, Math.Min(min, max), max);
            }
        }
        return TimeToX(item.Start);
    }

    private double GetInsertedAudioEndX(InsertedAudioLaneItem item)
    {
        if (item.Id == _selectedInsertedAudioTrackId && !double.IsNaN(_audioTrackDragCurrentX))
        {
            if (_dragMode == DragMode.InsertedAudioBody)
                return TimeToX(_audioTrackDragOriginalEnd) + (_audioTrackDragCurrentX - _audioTrackDragStartX);

            if (_dragMode == DragMode.InsertedAudioRightEdge)
            {
                double min = TimeToX(item.Start + AudioTrackEditing.MinDuration);
                double max = item.FileEndTime is { } fileEnd
                    ? TimeToX(fileEnd)
                    : double.MaxValue;
                return Math.Clamp(_audioTrackDragCurrentX, min, Math.Max(min, max));
            }
        }
        return TimeToX(item.Start + item.Duration);
    }

    private (string? Id, SegmentHitTarget Target) HitTestInsertedAudio(
        CanvasControl canvas, double posX, double posY)
    {
        bool music = LaneIsMusic(canvas);
        int rowCount = music ? _musicSubRowCount : _voiceSubRowCount;

        // Which stacked sub-row the pointer is over. Blocks in other rows are ignored
        // outright, which is what makes two overlapping beds independently grabbable.
        int pointerRow = Math.Clamp(
            (int)(posY / InsertedAudioSubRowHeight), 0, Math.Max(0, rowCount - 1));

        var (blockY, blockH) = SubRowBounds(pointerRow);
        if (posY < blockY || posY > blockY + blockH) return (null, SegmentHitTarget.None);

        // Reverse order so the topmost (last-drawn) block wins where two overlap.
        var items = ItemsForLane(music).Where(i => SubRowFor(i.Id) == pointerRow).ToList();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            double rawX1 = TimeToX(item.Start);
            double rawX2 = TimeToX(item.Start + item.Duration);
            var (x1, x2, clippedStart, clippedEnd) = VisibleExtent(rawX1, rawX2, canvas.ActualWidth);
            if (x2 <= x1) continue;              // scrolled entirely out of view
            if (posX < x1 || posX > x2) continue;

            // Only a REAL edge is a trim handle. Treating the canvas boundary of a clipped
            // block as one made a block longer than the video look pinned to the timeline's
            // end — and, worse, stole every body drag that started near that boundary, so
            // the block could not be moved until it had first been shortened. An edge that
            // is genuinely off-screen is trimmed from the context menu instead
            // ("Trim start/end to playhead"), which needs no pixel to aim at.
            double edge = EdgeHitWidthFor(x2 - x1);
            if (!clippedStart && posX - x1 <= edge) return (item.Id, SegmentHitTarget.LeftEdge);
            if (!clippedEnd && x2 - posX <= edge) return (item.Id, SegmentHitTarget.RightEdge);
            return (item.Id, SegmentHitTarget.Body);
        }
        return (null, SegmentHitTarget.None);
    }

    /// <summary>Top and height of one stacked sub-row's drawable band.</summary>
    private static (float Y, float Height) SubRowBounds(int row)
    {
        float top = (float)(row * InsertedAudioSubRowHeight) + InsertedAudioVerticalPadding;
        float height = (float)InsertedAudioSubRowHeight - InsertedAudioVerticalPadding * 2;
        return (top, height);
    }

    private void AudioTrackLane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;
        var (hitId, target) = HitTestInsertedAudio(canvas, pos.X, pos.Y);

        if (hitId is null)
        {
            // Empty lane space behaves like every other read-only track: move the playhead.
            ClearOtherSelections(SelectionKind.None);
            PlayheadPosition = XToTime(pos.X);
            return;
        }

        ClearOtherSelections(SelectionKind.InsertedAudio);
        SelectedInsertedAudioTrackId = hitId;
        InsertedAudioTrackSelected?.Invoke(this, hitId);

        var item = _insertedAudioTracks.FirstOrDefault(t => t.Id == hitId);
        if (item.Id != hitId) return;

        _audioTrackDragStartX = pos.X;
        _audioTrackDragCurrentX = pos.X;
        _audioTrackDragOriginalStart = item.Start;
        _audioTrackDragOriginalEnd = item.Start + item.Duration;

        _dragMode = target switch
        {
            SegmentHitTarget.LeftEdge => DragMode.InsertedAudioLeftEdge,
            SegmentHitTarget.RightEdge => DragMode.InsertedAudioRightEdge,
            _ => DragMode.InsertedAudioBody,
        };
        SetCursor(target is SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
            ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
        canvas.CapturePointer(e.Pointer);
    }

    /// <summary>
    /// Repaints only the two inserted-audio lanes.
    /// </summary>
    /// <remarks>
    /// Used for the per-pointer-move repaints of an audio drag, instead of
    /// <see cref="InvalidateAllCanvases"/>: nothing else on the timeline changes while an
    /// audio block is being dragged, and repainting the filmstrip and every other track on
    /// every pointer move is real GPU work on a machine that is already prone to device loss
    /// under editor load.
    /// </remarks>
    private void InvalidateInsertedAudioLanes()
    {
        VoiceOverTrackCanvas?.Invalidate();
        MusicTrackCanvas?.Invalidate();
    }

    private void AudioTrackLane_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;

        switch (_dragMode)
        {
            case DragMode.InsertedAudioBody:
                _audioTrackDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeAll);
                InvalidateInsertedAudioLanes();
                break;
            case DragMode.InsertedAudioLeftEdge:
            case DragMode.InsertedAudioRightEdge:
                _audioTrackDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateInsertedAudioLanes();
                break;
            case DragMode.None:
                var (_, target) = HitTestInsertedAudio(canvas, pos.X, pos.Y);
                SetCursor(target switch
                {
                    SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge => InputSystemCursorShape.SizeWestEast,
                    SegmentHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;
        }
    }

    private void AudioTrackLane_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;

        switch (_dragMode)
        {
            case DragMode.InsertedAudioBody when _selectedInsertedAudioTrackId is not null:
            {
                double deltaX = _audioTrackDragCurrentX - _audioTrackDragStartX;
                if (Math.Abs(deltaX) > 1)
                {
                    // Both endpoints go through XToTime so the delta is measured in the
                    // same (zoom/scroll-dependent) mapping the block was drawn with.
                    var grabbed = XToTime(_audioTrackDragStartX);
                    var dropped = XToTime(_audioTrackDragStartX + deltaX);
                    var newStart = _audioTrackDragOriginalStart + (dropped - grabbed);
                    if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                    InsertedAudioTrackMoved?.Invoke(this, (_selectedInsertedAudioTrackId, newStart));
                }
                break;
            }
            case DragMode.InsertedAudioLeftEdge when _selectedInsertedAudioTrackId is not null:
            {
                var newEdge = XToTime(Math.Clamp(_audioTrackDragCurrentX, 0, canvas.ActualWidth));
                if (newEdge != _audioTrackDragOriginalStart)
                    InsertedAudioTrackResized?.Invoke(this, (_selectedInsertedAudioTrackId, true, newEdge));
                break;
            }
            case DragMode.InsertedAudioRightEdge when _selectedInsertedAudioTrackId is not null:
            {
                var newEdge = XToTime(Math.Clamp(_audioTrackDragCurrentX, 0, canvas.ActualWidth));
                if (newEdge != _audioTrackDragOriginalEnd)
                    InsertedAudioTrackResized?.Invoke(this, (_selectedInsertedAudioTrackId, false, newEdge));
                break;
            }
        }

        _audioTrackDragStartX = double.NaN;
        _audioTrackDragCurrentX = double.NaN;
        _dragMode = DragMode.None;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        InvalidateInsertedAudioLanes();
    }

    private void AudioTrackLane_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);
        var (hitId, _) = HitTestInsertedAudio(canvas, pos.X, pos.Y);
        if (hitId is null) return;

        ClearOtherSelections(SelectionKind.InsertedAudio);
        SelectedInsertedAudioTrackId = hitId;
        InsertedAudioTrackSelected?.Invoke(this, hitId);
        InsertedAudioTrackContextRequested?.Invoke(this, (hitId, canvas, pos));

        // The right-tap must not bubble to the parent, which would let another handler open
        // its own menu on top of the host's.
        e.Handled = true;
    }

    private void VoiceOverTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        => DrawInsertedAudioLane(sender, args, music: false);

    /// <summary>
    /// Canvas x at which the source file's second 0 would sit, for a given block.
    /// </summary>
    /// <remarks>
    /// For an EDGE drag this is derived from the COMMITTED start, so the peaks stay
    /// physically stationary and the dragged edge sweeps over them — a left trim moves
    /// <c>StartTime</c> and <c>TrimStart</c> by the same delta, so the file's origin genuinely
    /// does not move. For a BODY drag the audio really is travelling, so the anchor rides the
    /// previewed block instead.
    /// </remarks>
    private double InsertedAudioFileOriginX(InsertedAudioLaneItem item, double pixelsPerSecond)
    {
        bool bodyDrag = item.Id == _selectedInsertedAudioTrackId
            && _dragMode == DragMode.InsertedAudioBody
            && !double.IsNaN(_audioTrackDragCurrentX);

        double anchorLeftX = bodyDrag ? GetInsertedAudioStartX(item) : TimeToX(item.Start);
        return anchorLeftX - (item.TrimStart.TotalSeconds * pixelsPerSecond);
    }

    private void MusicTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        => DrawInsertedAudioLane(sender, args, music: true);

    private void DrawInsertedAudioLane(CanvasControl sender, CanvasDrawEventArgs args, bool music)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(AudioTrackBackground);
        if (model is null || model.DisplayDuration.TotalSeconds <= 0) return;

        // Violet/blue, deliberately unlike the recorded audio and mic tracks: an inserted
        // track is not re-cut with the footage, so it must not read as one of them.
        var fill = music
            ? Color.FromArgb(210, 96, 138, 214)
            : Color.FromArgb(210, 138, 108, 224);
        var fillSelected = music
            ? Color.FromArgb(245, 138, 180, 250)
            : Color.FromArgb(245, 180, 156, 250);
        var mutedFill = Color.FromArgb(110, 130, 130, 140);
        var borderColor = Color.FromArgb(255, 226, 216, 255);
        var borderSelected = Color.FromArgb(255, 255, 255, 255);
        var textColor = Color.FromArgb(255, 255, 255, 255);

        // The trimmed-away head and tail: same shape, heavily desaturated and dimmed, so the
        // audio you could still drag back in is visible without competing with the audio that
        // is actually playing (the After Effects convention).
        var trimmedFill = Color.FromArgb(46, 176, 176, 190);
        var trimmedWaveform = Color.FromArgb(90, 200, 200, 214);
        var trimmedBorder = Color.FromArgb(70, 200, 200, 214);

        // Timeline scale, resolved once: the waveform positions every peak by its own source
        // time rather than by a fraction of the block, which is what keeps a trim preview a
        // clip instead of a rescale.
        double pixelsPerSecond = TimeToX(TimeSpan.FromSeconds(1)) - TimeToX(TimeSpan.Zero);

        // ── Background layer: the selected block's dimmed full-file extent ──
        // Drawn BEFORE every block, not inline with its own, because a file's extent is
        // wider than the block and runs under its neighbours in the same sub-row. Drawn
        // inline it would paint headroom over a real clip whenever the selected block sat
        // earlier in the row.
        foreach (var item in ItemsForLane(music))
        {
            if (item.Id != _selectedInsertedAudioTrackId) continue;
            if (item.SourceDuration <= TimeSpan.Zero || pixelsPerSecond <= 0) continue;

            var (bandY, bandH) = SubRowBounds(SubRowFor(item.Id));
            double originX = InsertedAudioFileOriginX(item, pixelsPerSecond);
            double fullX2 = originX + (item.SourceDuration.TotalSeconds * pixelsPerSecond);
            var (fx1, fx2, _, _) = VisibleExtent(originX, fullX2, w);
            if (fx2 <= fx1) continue;

            float fillX = (float)fx1;
            float fillW = (float)(fx2 - fx1);
            ds.FillRectangle(fillX, bandY, fillW, bandH, trimmedFill);

            if (item.Waveform is { Length: > 1 } fullWave)
            {
                DrawInsertedAudioWaveform(
                    ds, item, fullWave, originX, pixelsPerSecond,
                    fillX, (float)fx2, bandH, bandY + bandH / 2f, trimmedWaveform);
            }

            ds.DrawRectangle(fillX, bandY, fillW, bandH, trimmedBorder, 1f);
        }

        foreach (var item in ItemsForLane(music))
        {
            // Each block sits in the sub-row it was packed into, so two that overlap in time
            // are drawn (and hit-tested) on separate bands instead of on top of each other.
            var (blockY, blockH) = SubRowBounds(SubRowFor(item.Id));
            float centerY = blockY + blockH / 2f;
            // Positioned in OUTPUT time directly — no segment mapping, which is exactly what
            // keeps an inserted track where the user put it when the footage is re-cut.
            double rawX1 = GetInsertedAudioStartX(item);
            double rawX2 = GetInsertedAudioEndX(item);

            // An edge dragged past its opposite would otherwise invert the rectangle and make
            // the block disappear mid-gesture; the operation clamps the value on release, so
            // the preview just pins it to a sliver until then.
            if (rawX1 > rawX2) (rawX1, rawX2) = (rawX2, rawX1);

            var (x1d, x2d, clippedStart, clippedEnd) = VisibleExtent(rawX1, rawX2, w);
            if (x2d <= x1d) continue;

            float x1 = (float)x1d;
            float x2 = (float)x2d;
            bool isSelected = item.Id == _selectedInsertedAudioTrackId;
            float blockW = Math.Max(2, x2 - x1);

            double fileOriginX = InsertedAudioFileOriginX(item, pixelsPerSecond);

            // ── The part that actually plays, at full strength ──
            using var rect = CanvasGeometry.CreateRoundedRectangle(ds, x1, blockY, blockW, blockH, 4, 4);
            ds.FillGeometry(rect, item.IsMuted ? mutedFill : isSelected ? fillSelected : fill);
            ds.DrawGeometry(rect, isSelected ? borderSelected : borderColor, isSelected ? 1.5f : 1f);

            if (item.Waveform is { Length: > 1 } waveform && !item.IsMuted)
            {
                DrawInsertedAudioWaveform(
                    ds, item, waveform, fileOriginX, pixelsPerSecond,
                    x1, x2, blockH, centerY, borderColor);
            }

            if (blockW > 40)
            {
                using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 10,
                    FontFamily = "Segoe UI",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                    WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
                    TrimmingGranularity = Microsoft.Graphics.Canvas.Text.CanvasTextTrimmingGranularity.Character,
                    TrimmingSign = Microsoft.Graphics.Canvas.Text.CanvasTrimmingSign.Ellipsis,
                };
                string label = item.IsMuted ? item.Name + " (muted)" : item.Name;
                // Inset past both handles so the label never sits under a grab target.
                float textInset = InsertedAudioHandleWidth + 4;
                ds.DrawText(label,
                    new Rect(x1 + textInset, blockY, Math.Max(1, blockW - textInset * 2), blockH),
                    textColor, fmt);
            }

            // Trim handles are drawn on EVERY block, not just the selected one: an invisible
            // grab strip is indistinguishable from "you cannot trim this", which is exactly
            // how the first version of this lane read. A CLIPPED edge draws only the
            // "continues off-screen" ticks and no handle, because it is not grabbable —
            // showing a handle there implied the audio ended at the timeline's edge.
            if (!clippedStart)
                DrawTrimHandle(ds, x1, blockY, blockH, isSelected, atStart: true);
            else
                DrawContinuationTicks(ds, x1, blockY, blockH, atStart: true);

            if (!clippedEnd)
                DrawTrimHandle(ds, x2, blockY, blockH, isSelected, atStart: false);
            else
                DrawContinuationTicks(ds, x2, blockY, blockH, atStart: false);
        }
    }

    /// <summary>
    /// Draws the portion of a track's whole-file waveform that its trim window actually
    /// plays, with every peak pinned to its own position on the output timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Peaks are positioned absolutely, never as a fraction of the block's width.</b> The
    /// fraction form is correct at rest and wrong during a drag: while an edge is being
    /// dragged the block's width is the PREVIEW width but the trim window is still the
    /// committed one, so the old window gets rescaled into the changing width — the waveform
    /// visibly slides and squashes instead of being clipped. Anchoring to
    /// <paramref name="fileOriginX"/> (the x at which the source file's second 0 would sit)
    /// makes the peaks physically stationary, so a trim edge simply sweeps over them and
    /// reveals or hides audio, which is what a trim IS.
    /// </para>
    /// <para>
    /// The caller keeps <paramref name="fileOriginX"/> fixed for an edge drag (it derives it
    /// from the committed start, since a left trim moves <c>StartTime</c> and
    /// <c>TrimStart</c> by the same delta, leaving the file's origin exactly where it was) and
    /// moves it with the block for a body drag, where the audio really does travel.
    /// </para>
    /// </remarks>
    /// <param name="fileOriginX">Canvas x at which the source file's second 0 would sit.</param>
    /// <param name="pixelsPerSecond">Timeline scale, so a peak's source time becomes an x.</param>
    /// <param name="clipLeft">Left bound to draw within (the block's visible left edge).</param>
    /// <param name="clipRight">Right bound to draw within (the block's visible right edge).</param>
    private static void DrawInsertedAudioWaveform(
        CanvasDrawingSession ds, InsertedAudioLaneItem item, float[] waveform,
        double fileOriginX, double pixelsPerSecond,
        float clipLeft, float clipRight, float blockH, float centerY, Color color)
    {
        double fileSeconds = item.WaveformDurationSeconds;
        if (fileSeconds <= 0 || pixelsPerSecond <= 0 || clipRight <= clipLeft) return;

        // Only the peaks that fall inside the drawn rectangle matter; deriving the range from
        // the rectangle (rather than from the trim window) is what makes the preview correct,
        // because the rectangle is already the previewed one.
        double startSeconds = (clipLeft - fileOriginX) / pixelsPerSecond;
        double endSeconds = (clipRight - fileOriginX) / pixelsPerSecond;

        var window = WaveformWindow.Resolve(
            waveform.Length, fileSeconds, startSeconds, endSeconds - startSeconds);
        if (window.IsEmpty) return;

        float barWidth = Math.Max(1f, (float)(pixelsPerSecond * fileSeconds / waveform.Length));
        float maxBar = blockH * 0.42f;

        for (int i = window.FirstIndex; i <= window.LastIndex; i++)
        {
            float bx = (float)(fileOriginX + window.SecondsFor(i) * pixelsPerSecond);

            // Clipped to the block so the waveform never paints outside the edge that is
            // currently cutting it — the visual definition of a trim.
            float left = Math.Max(bx, clipLeft);
            float right = Math.Min(bx + barWidth, clipRight);
            if (right <= left) continue;

            float amplitude = Math.Clamp(waveform[i], 0f, 1f);
            float barHeight = amplitude * maxBar;
            ds.FillRectangle(left, centerY - barHeight, right - left, barHeight * 2, color);
        }
    }

    /// <summary>
    /// Draws one trim handle at a REAL (on-screen) block edge.
    /// </summary>
    private static void DrawTrimHandle(
        CanvasDrawingSession ds, float x, float blockY, float blockH, bool isSelected, bool atStart)
    {
        float handleW = InsertedAudioHandleWidth;
        float handleH = blockH * (isSelected ? 0.7f : 0.5f);
        float handleY = blockY + (blockH - handleH) / 2;
        float handleX = atStart ? x + 1 : x - handleW - 1;

        var color = isSelected
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(190, 245, 245, 255);

        ds.FillRoundedRectangle(handleX, handleY, handleW, handleH, 2, 2, color);
    }

    /// <summary>
    /// Marks an edge whose real position is off-canvas: the block continues past the visible
    /// timeline. Deliberately NOT a handle — this edge cannot be dragged (there is no pixel
    /// for it), so drawing one would promise a gesture that does not exist.
    /// </summary>
    private static void DrawContinuationTicks(
        CanvasDrawingSession ds, float x, float blockY, float blockH, bool atStart)
    {
        var tick = Color.FromArgb(150, 255, 255, 255);
        float tickH = blockH * 0.5f;
        float tickY = blockY + (blockH - tickH) / 2;
        float dir = atStart ? 1 : -1;

        for (int i = 0; i < 3; i++)
        {
            float tx = x + (dir * (2 + i * 3));
            ds.FillRectangle(tx, tickY, 1.5f, tickH, tick);
        }
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

        // Segment-based timeline: draw each video segment's own audio as its OWN block,
        // deliberately shaped like the inserted voice-over/music clips.
        //
        // Recorded audio really is per-segment — it is cut, reordered, sped up and (now)
        // muted with the segment that owns it — but drawing it as one continuous ribbon of
        // peaks made it read as a single uninterrupted track, so a per-segment state like
        // "this one is muted" had nowhere to appear. Blocks give every segment's audio an
        // outline to carry its own state and its own right-click target.
        //
        // They are NOT draggable like the inserted lanes: this audio is bound to its
        // segment's position by definition, so a grab handle would promise an edit that
        // cannot exist. Moving the segment moves its audio.
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
                bool hasWaveform = wf is { Length: > 0 } && visual!.WaveformDurationSeconds > 0;

                DrawRecordedAudioBlock(
                    ds, seg, hasWaveform ? wf : null, visual?.WaveformDurationSeconds ?? 0,
                    waveformColor, envelopeColor, w, h);
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
    /// <summary>
    /// Draws one video segment's recorded audio as a discrete block on a recorded-audio lane,
    /// with its waveform inside and its per-segment audio state on the face of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chrome (rounded rect, fill, border, inset label) deliberately matches
    /// <see cref="DrawInsertedAudioLane"/>'s clips, because the two now mean the same thing to
    /// the user: a piece of audio you can point at and act on. The COLOURS stay the lane's own
    /// (green for system, orange for mic) so a recorded block is never mistaken for an
    /// inserted one — only the latter can be dragged, trimmed and re-levelled.
    /// </para>
    /// <para>
    /// Muting is the one per-segment audio state that has to be legible here, because it is
    /// otherwise invisible: silencing a segment changed nothing on the timeline at all. An
    /// audible block needs no label — its audio matches the picture by definition, which is
    /// what a block sitting under its segment already looks like.
    /// </para>
    /// </remarks>
    private void DrawRecordedAudioBlock(
        CanvasDrawingSession ds, VideoSegment seg, float[]? wf, double waveformDurationSeconds,
        Color waveformColor, Color envelopeColor, float w, float h)
    {
        double rawX1 = TimeToX(seg.Start);
        double rawX2 = TimeToX(seg.End);
        var (x1d, x2d, _, _) = VisibleExtent(rawX1, rawX2, w);
        if (x2d <= x1d) return;

        float x1 = (float)x1d;
        float blockW = Math.Max(2f, (float)(x2d - x1d));
        float blockY = RecordedAudioBlockPadding;
        float blockH = Math.Max(4f, h - (RecordedAudioBlockPadding * 2));
        float centerY = blockY + (blockH / 2f);

        bool muted = seg.AudioMode == SegmentAudioMode.Muted;
        bool isSelected = seg.Id == _selectedSegmentId;

        // Tinted from the lane's own waveform colour, so system and mic blocks stay as
        // distinguishable as their peaks always were.
        var fill = muted
            ? Color.FromArgb(70, 130, 130, 140)
            : Color.FromArgb((byte)(isSelected ? 78 : 52), waveformColor.R, waveformColor.G, waveformColor.B);
        var border = muted
            ? Color.FromArgb(120, 190, 190, 200)
            : Color.FromArgb((byte)(isSelected ? 255 : 170), waveformColor.R, waveformColor.G, waveformColor.B);

        using (var rect = CanvasGeometry.CreateRoundedRectangle(ds, x1, blockY, blockW, blockH, 4, 4))
        {
            ds.FillGeometry(rect, fill);
            ds.DrawGeometry(rect, border, isSelected ? 1.5f : 1f);
        }

        if (!muted && wf is { Length: > 0 } && waveformDurationSeconds > 0)
        {
            // Clipped to the block so a segment's peaks can never spill over its neighbour's
            // outline — the whole point of drawing outlines.
            using (ds.CreateLayer(1f, new Rect(x1, blockY, blockW, blockH)))
            {
                DrawSegmentWaveform(
                    ds, seg, wf, waveformDurationSeconds, waveformColor, envelopeColor,
                    w, blockH, centerY);
            }
        }

        if (muted && blockW > 46)
        {
            using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontSize = 10,
                FontFamily = "Segoe UI",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
                TrimmingGranularity = Microsoft.Graphics.Canvas.Text.CanvasTextTrimmingGranularity.Character,
                TrimmingSign = Microsoft.Graphics.Canvas.Text.CanvasTrimmingSign.Ellipsis,
            };

            ds.DrawText(
                "muted",
                new Rect(x1 + 6, blockY, Math.Max(1, blockW - 12), blockH),
                Color.FromArgb(230, 235, 235, 240),
                fmt);
        }
    }

    /// <summary>Vertical inset of a recorded-audio block within its lane.</summary>
    private const float RecordedAudioBlockPadding = 3f;

    /// <summary>Speed factors within this distance of 1.0 are treated as unmodified.</summary>
    private const double SegmentSpeedEpsilon = 0.001;

    /// <summary>
    /// Draws one segment's audio waveform across its output range by sampling the
    /// file-aligned waveform at the segment's source time, so it stays aligned after
    /// the segment is moved, trimmed, or split.
    /// </summary>
    /// <remarks>
    /// Audible recorded audio is always re-timed with the picture, so source time maps to
    /// output time through the segment's own speed and the peaks fill the block exactly.
    /// Audio that should NOT follow the picture is not drawn here at all — it has been
    /// detached into its own block on the inserted lane (see
    /// <c>DetachSegmentAudioOperation</c>), which owns its own drawing.
    /// </remarks>
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
        double outDur = seg.Duration.TotalSeconds;
        if (srcDur <= 0 || outDur <= 0) return;

        double speed = seg.SpeedFactor > 0 ? seg.SpeedFactor : 1.0;

        // Resolve the waveform index range covering this segment's source span.
        int len = wf.Length;
        int firstIdx = Math.Clamp((int)(srcStart / waveformDurationSeconds * len), 0, len - 1);
        int lastIdx = Math.Clamp((int)((srcStart + srcDur) / waveformDurationSeconds * len), firstIdx, len - 1);

        double pixelsPerOutputSecond = segW / outDur;
        double secondsPerSample = waveformDurationSeconds / len;

        // Derived from the timeline scale rather than from the sample count, so a bar is
        // always exactly as wide as the time it represents.
        float barWidth = Math.Max(1f, (float)(secondsPerSample / speed * pixelsPerOutputSecond));
        float maxBar = h * 0.45f;

        for (int i = firstIdx; i <= lastIdx; i++)
        {
            double srcSec = (double)i / len * waveformDurationSeconds;
            double outLocal = (srcSec - srcStart) / speed;
            if (outLocal < 0) continue;
            if (outLocal >= outDur) break;

            float bx = (float)TimeToX(seg.Start + TimeSpan.FromSeconds(outLocal));
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
        bool isPrimary = model.PrimaryVideoFilePath is null ||
            string.Equals(seg.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(seg.VideoFilePath) &&
            _trackVisualsByFile.TryGetValue(seg.VideoFilePath, out var v))
        {
            // A registered visual normally wins outright. The exception is the primary
            // recording with NO waveform in that registration: "which file is primary" is
            // decided here from TimelineModel.PrimaryVideoFilePath but by the host (in
            // LoadAppendedTrackVisualsAsync) from Project.VideoFilePath, and when those two
            // disagree the primary gets a per-file entry registered for it with empty
            // waveforms — which would then permanently shadow the model-level samples below
            // and leave the audio tracks blank for the whole session.
            bool hasWaveform = v.SystemWaveform is { Length: > 0 } || v.MicWaveform is { Length: > 0 };
            if (hasWaveform || !isPrimary)
                return v;
        }

        if (isPrimary)
        {
            return new SegmentTrackVisual
            {
                Cursor = model.CursorData,
                MouseToVideoOffsetSeconds = model.MouseToVideoOffsetSeconds,
                SystemWaveform = model.SystemAudioWaveformSamples,
                MicWaveform = model.MicAudioWaveformSamples,
                // Falls back to the timeline's own span: a restored project whose top-level
                // Duration never got written would otherwise divide by zero here and draw
                // nothing, which looks identical to "this recording has no audio".
                WaveformDurationSeconds = model.Duration.TotalSeconds > 0
                    ? model.Duration.TotalSeconds
                    : model.DisplayDuration.TotalSeconds,
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
            VideoTrack_SegmentPressed(model, canvas, pos.X, pos.Y, e);
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
            ClearOtherSelections(SelectionKind.Clip);
            SelectedClipIndex = hitClipIndex;
            VideoClipSelected?.Invoke(this, hitClipIndex);
        }
        else
        {
            ClearOtherSelections(SelectionKind.None);
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
    private void VideoTrack_SegmentPressed(TimelineModel model, CanvasControl canvas, double x, double y, PointerRoutedEventArgs e)
    {
        // Transition boundary chips sit on top of the incoming segment's leading trim
        // handle and the cut line, so they MUST be tested before HitTestSegment below —
        // otherwise a chip click would be swallowed as a trim-edge press instead. A chip
        // hit is a discrete selection only: no drag, no pointer capture, no playhead move.
        // ClearOtherSelections enforces that this is the ONLY thing selected afterwards —
        // not just on this track (segment/clip) but across zoom/camera/text-overlay too.
        var (chipIncomingId, chipHit) = HitTestTransitionChip(x, y);
        if (chipHit)
        {
            // The hover tooltip has served its purpose once the chip is actually clicked, and
            // leaving it up would obscure the properties pane opening behind it.
            HideTransitionChipToolTip();
            ClearOtherSelections(SelectionKind.Transition);
            if (_selectedTransitionId != chipIncomingId)
            {
                _selectedTransitionId = chipIncomingId;
                TransitionSelected?.Invoke(this, chipIncomingId);
            }
            VideoTrackCanvas?.Invalidate();
            return;
        }

        var target = HitTestSegment(model, x, y, out var segId);

        if (segId is null)
        {
            // Empty space → deselect everything (including a transition selection, so
            // it doesn't linger selected alongside nothing) and scrub the playhead.
            ClearOtherSelections(SelectionKind.None);
            PlayheadPosition = XToTime(x);
            _dragMode = DragMode.Playhead;
            canvas.CapturePointer(e.Pointer);
            // The deselect above changes what the primary track paints; a playhead change no
            // longer repaints canvases, so request the redraw explicitly.
            VideoTrackCanvas?.Invalidate();
            return;
        }

        var segment = model.Segments.First(s => s.Id == segId);

        // Select the hit segment — clears clip/zoom/camera/text-overlay/transition.
        ClearOtherSelections(SelectionKind.Segment);
        if (_selectedSegmentId != segId)
        {
            _selectedSegmentId = segId;
            SegmentSelected?.Invoke(this, segId);
        }

        _draggedSegmentId = segId;
        _segmentDragStartX = x;
        _segmentDragStartY = y;
        _segmentDragCurrentX = x;
        _segmentDragMoved = false;
        _segmentDragOriginalStart = segment.Start;
        _segmentDragOriginalDuration = segment.Duration;
        _segmentDragOriginalTrackIndex = segment.TrackIndex;
        _segmentDragCurrentTrackIndex = segment.TrackIndex;
        _hintLaneArmed = false;
        _hintLaneLatched = false;
        _segmentSnapGuideX = double.NaN;
        _segmentDropIndicatorX = double.NaN;
        _textSlideWindowDragCurrentX = double.NaN;
        if (segment is TextSlideSegment slide)
        {
            _textSlideWindowOriginalInStart = slide.ResolveTextInStart();
            _textSlideWindowOriginalOutEnd = slide.ResolveTextOutEnd();
        }

        _dragMode = target switch
        {
            SegmentHitTarget.LeftEdge => DragMode.SegmentLeftEdge,
            SegmentHitTarget.RightEdge => DragMode.SegmentRightEdge,
            SegmentHitTarget.TextWindowInEdge => DragMode.TextSlideWindowInEdge,
            SegmentHitTarget.TextWindowOutEdge => DragMode.TextSlideWindowOutEdge,
            _ => DragMode.SegmentBody,
        };

        if (_dragMode == DragMode.SegmentBody)
            PlayheadPosition = XToTime(x);
        else if (_dragMode is DragMode.TextSlideWindowInEdge or DragMode.TextSlideWindowOutEdge)
            _textSlideWindowDragCurrentX = x;

        SetCursor(target is SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
            or SegmentHitTarget.TextWindowInEdge or SegmentHitTarget.TextWindowOutEdge
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
            VideoTrack_SegmentMoved(model, canvas, pos.X, pos.Y);
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
    private void VideoTrack_SegmentMoved(TimelineModel model, CanvasControl canvas, double x, double y)
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

            case DragMode.TextSlideWindowInEdge:
            case DragMode.TextSlideWindowOutEdge:
                _textSlideWindowDragCurrentX = Math.Clamp(x, 0, canvas.ActualWidth);
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

                // Resolve the destination lane from how far the pointer has TRAVELLED, not
                // from where it now sits. Revealing the hint lane grows the canvas, which
                // pushes every existing row down by one lane height while the cursor stays
                // put — reading absolute Y would then report a lane the user never aimed at,
                // silently promoting a plain horizontal reorder into an overlay move.
                _segmentDragCurrentTrackIndex = ResolveDragTrackIndex(model, y);
                _hintLaneArmed = ShowOverlayDropHint;
                // Latch on the way in only, and never let go until the gesture ends — see
                // HintLaneRequested for why the lane must not follow the pointer back out.
                _hintLaneLatched |= _hintLaneArmed;
                SyncHintLaneReveal();
                SetCursor(InputSystemCursorShape.SizeAll);

                // Snap the dragged segment's projected left edge to nearby boundaries.
                double deltaX = clampedX - _segmentDragStartX;
                double leftX = TimeToX(_segmentDragOriginalStart) + deltaX;
                double snappedLeftX = SnapX(model, leftX, _draggedSegmentId, snap);
                _segmentDragCurrentX = _segmentDragStartX + (snappedLeftX - TimeToX(_segmentDragOriginalStart));

                _segmentDropIndicatorX = _segmentDragCurrentTrackIndex == TimelineModel.BaseTrackIndex
                    ? ComputeDropIndicatorX(model, snappedLeftX)
                    : double.NaN;

                // Only the video canvas shows the drag preview (block position, drop
                // indicator, snap guide, hint lane); repainting all ten track canvases —
                // filmstrips and waveforms included — on every pointer sample is what made
                // the gesture feel like it was stuttering.
                VideoTrackCanvas?.Invalidate();
                break;

            case DragMode.None:
                // A chip hovered over a trim handle must win the cursor feedback too —
                // otherwise the resize cursor would suggest a drag that a click there
                // can't actually start (see VideoTrack_SegmentPressed's chip-first order).
                var (hoverChipId, chipHit) = HitTestTransitionChip(x, y, out var hoverChipRect);
                UpdateTransitionChipHover(chipHit ? hoverChipId : null, hoverChipRect);
                if (chipHit)
                {
                    SetCursor(InputSystemCursorShape.Hand);
                    break;
                }
                var target = HitTestSegment(model, x, y, out _);
                SetCursor(target switch
                {
                    SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
                        or SegmentHitTarget.TextWindowInEdge or SegmentHitTarget.TextWindowOutEdge => InputSystemCursorShape.SizeWestEast,
                    SegmentHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;

            case DragMode.Playhead:
                PlayheadPosition = XToTime(x);
                break;
        }
    }

    /// <summary>
    /// Abandons an in-flight drag when the pointer is taken away without a release (window
    /// deactivation, a touch cancel, a system gesture). Without this the drag state — and so
    /// the drop-hint lane and the canvas height it forces — would stay latched with no
    /// gesture left to clear it.
    /// </summary>
    private void VideoTrack_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        _draggedSegmentId = null;
        _segmentDragStartX = double.NaN;
        _segmentDragStartY = double.NaN;
        _segmentDragCurrentX = double.NaN;
        _segmentDragMoved = false;
        _segmentDragOriginalTrackIndex = TimelineModel.BaseTrackIndex;
        _segmentDragCurrentTrackIndex = TimelineModel.BaseTrackIndex;
        _segmentSnapGuideX = double.NaN;
        _segmentDropIndicatorX = double.NaN;
        _textSlideWindowDragCurrentX = double.NaN;
        _dragMode = DragMode.None;

        // _hintLaneArmed is deliberately NOT cleared: it keeps the outline's highlight for the
        // fold-away animation that SyncHintLaneReveal is about to start.
        _hintLaneLatched = false;

        SetCursor(InputSystemCursorShape.Arrow);
        SyncHintLaneReveal();
        UpdateVideoTrackHeight();
        InvalidateAll();
    }

    private void VideoTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (sender is not CanvasControl canvas)
        {
            _dragMode = DragMode.None;
            return;
        }

        // Captured before the commit, which may itself add the lane the hint was offering.
        int usedTracksBeforeDrop = Math.Max(1, model?.VideoTrackCount ?? 1);

        if (model is not null && model.Segments.Count > 0 && _draggedSegmentId is { } draggedId)
        {
            VideoTrack_SegmentReleased(model, draggedId);
        }

        _draggedSegmentId = null;
        _segmentDragStartX = double.NaN;
        _segmentDragCurrentX = double.NaN;
        _segmentDragMoved = false;
        _segmentDragOriginalTrackIndex = TimelineModel.BaseTrackIndex;
        _segmentDragCurrentTrackIndex = TimelineModel.BaseTrackIndex;
        _segmentSnapGuideX = double.NaN;
        _segmentDropIndicatorX = double.NaN;
        _textSlideWindowDragCurrentX = double.NaN;
        _dragMode = DragMode.None;
        _hintLaneLatched = false;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        // _dragMode is cleared above, so the hint lane is gone. Fold it away with the same
        // eased animation it opened with — UNLESS the drop just turned it into a real lane,
        // in which case the real row takes over the identical height and the hint must be
        // dropped in the same frame or the track band would visibly bulge and settle.
        bool laneBecameReal = Math.Max(1, model?.VideoTrackCount ?? 1) > usedTracksBeforeDrop;
        SyncHintLaneReveal(animate: !laneBecameReal);
        UpdateVideoTrackHeight();
        InvalidateAll();
    }

    /// <summary>Commits the segment move / trim gesture by raising the appropriate event.</summary>
    private void VideoTrack_SegmentReleased(TimelineModel model, string draggedId)
    {
        switch (_dragMode)
        {
            case DragMode.TextSlideWindowInEdge:
            case DragMode.TextSlideWindowOutEdge:
            {
                var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == draggedId);
                if (slide is null) break;

                var draggedOffset = XToTime(_textSlideWindowDragCurrentX) - slide.Start;
                var window = _dragMode == DragMode.TextSlideWindowInEdge
                    ? ClampTextSlideWindow(draggedOffset, _textSlideWindowOriginalOutEnd, slide.Duration)
                    : ClampTextSlideWindow(_textSlideWindowOriginalInStart, draggedOffset, slide.Duration);

                if (Math.Abs((window.InStart - _textSlideWindowOriginalInStart).TotalMilliseconds) > 1 ||
                    Math.Abs((window.OutEnd - _textSlideWindowOriginalOutEnd).TotalMilliseconds) > 1)
                {
                    TextSlideWindowChanged?.Invoke(this, new TextSlideWindowEventArgs
                    {
                        SegmentId = draggedId,
                        InStart = window.InStart,
                        OutEnd = window.OutEnd,
                    });
                }
                break;
            }

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
                var grabbed = XToTime(_segmentDragStartX);
                var dropped = XToTime(_segmentDragStartX + deltaX);
                var newStart = _segmentDragOriginalStart + (dropped - grabbed);
                if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;

                if (_segmentDragOriginalTrackIndex != TimelineModel.BaseTrackIndex ||
                    _segmentDragCurrentTrackIndex != TimelineModel.BaseTrackIndex)
                {
                    if (_segmentDragOriginalTrackIndex != _segmentDragCurrentTrackIndex ||
                        Math.Abs((newStart - _segmentDragOriginalStart).TotalMilliseconds) > 1)
                    {
                        SegmentTrackMoveRequested?.Invoke(this, new SegmentTrackMoveEventArgs
                        {
                            SegmentId = draggedId,
                            NewStart = newStart,
                            NewTrackIndex = _segmentDragCurrentTrackIndex,
                        });
                    }
                    break;
                }

                var dropCenter = _segmentDragOriginalStart + _segmentDragOriginalDuration / 2 + (dropped - grabbed);

                int targetIndex = ComputeMoveTargetIndex(model, draggedId, dropCenter);
                int fromIndex = model.Segments.FindIndex(s => s.Id == draggedId);
                if (targetIndex != fromIndex && targetIndex != fromIndex + 1)
                    SegmentMoveRequested?.Invoke(this, (draggedId, targetIndex));
                break;
            }
        }
    }

    /// <summary>
    /// Right-clicking a chip that carries ANY explicit config — an effect or an explicit hard
    /// cut — requests its removal, resetting the boundary to Automatic (mirroring
    /// <see cref="ZoomTrack_RightTapped"/> / <see cref="CameraTrack_RightTapped"/>).
    /// Right-clicking an already-Automatic chip is a no-op: there is nothing to remove, and
    /// this control must not create model state from a pointer event either.
    /// Anywhere else on a video segment, the segment context menu opens instead.
    /// </summary>
    private void VideoTrack_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);

        var (hitId, chipHit) = HitTestTransitionChip(pos.X, pos.Y);
        if (chipHit && hitId is not null)
        {
            var incoming = Model?.Segments.FirstOrDefault(s => s.Id == hitId);
            // Any non-null config is removable. An explicit Type=None is a real user choice that
            // suppresses the slide-adjacent legacy crossfade, so it must be resettable back to
            // Automatic through the same gesture — treating it as "nothing to remove" left the
            // user with no way to undo that choice from the timeline.
            if (incoming?.InTransition is null) return;

            // Right-clicking a chip to remove its transition also SELECTS that boundary
            // (matching the existing zoom/camera right-tap pattern below), so — like any
            // other selection — it deliberately takes over from whatever else was selected
            // (e.g. a text overlay) rather than leaving both looking selected.
            ClearOtherSelections(SelectionKind.Transition);
            if (_selectedTransitionId != hitId)
            {
                _selectedTransitionId = hitId;
                TransitionSelected?.Invoke(this, hitId);
            }
            TransitionRemoveRequested?.Invoke(this, hitId);
            return;
        }

        var model = Model;
        if (model is null || model.Segments.Count == 0) return;

        HitTestSegment(model, pos.X, pos.Y, out var segmentId);
        if (segmentId is null) return;
        if (model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == segmentId) is not { } video) return;

        // Select the right-clicked segment first, so the menu visibly acts on the block the
        // user aimed at and the properties pane follows — same contract as the left-click
        // and the zoom/camera right-tap paths.
        ClearOtherSelections(SelectionKind.Segment);
        if (_selectedSegmentId != segmentId)
        {
            _selectedSegmentId = segmentId;
            SegmentSelected?.Invoke(this, segmentId);
        }
        VideoTrackCanvas?.Invalidate();
        InvalidateAudioLanes();

        ShowVideoSegmentContextMenu(canvas, pos, video);
    }

    /// <summary>Playback speeds offered by the video segment context menu.</summary>
    private static readonly double[] SpeedPresets = [0.25, 0.5, 1.0, 1.5, 2.0, 4.0];

    /// <summary>
    /// Builds the video segment's right-click menu: a "Speed" label with the presets listed
    /// flat beneath it, then the segment actions. Deliberately NOT a cascading submenu —
    /// picking a speed is the point of the menu, and a fly-out level to reach it is one
    /// hover-and-aim more than the edit is worth. The edits themselves are the host's (the
    /// control raises events rather than touching the model), matching how
    /// <see cref="ZoomTrack_RightTapped"/> requests a zoom removal.
    /// </summary>
    /// <remarks>
    /// Every entry is a plain <see cref="MenuFlyoutItem"/>, and the active speed is marked
    /// with a check GLYPH in the icon slot rather than by a <c>RadioMenuFlyoutItem</c> /
    /// <c>ToggleMenuFlyoutItem</c>. Those reserve their own check column IN ADDITION to the
    /// icon column that the other entries need, so the menu ends up with two empty gutters
    /// and text pushed twice as far right. One column, one indent.
    /// </remarks>
    private void ShowVideoSegmentContextMenu(CanvasControl canvas, Point pos, VideoSegment video)
    {
        var menu = new MenuFlyout();

        // MenuFlyout has no header item, so the label is a disabled entry — the
        // conventional stand-in.
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Speed",
            Icon = new FontIcon { Glyph = "\uE916" },
            IsEnabled = false,
        });

        double current = video.SpeedFactor > 0 ? video.SpeedFactor : 1.0;
        foreach (double preset in SpeedPresets)
        {
            double speed = preset;
            var item = new MenuFlyoutItem
            {
                Text = Math.Abs(speed - 1.0) < 0.001 ? "Normal (1x)" : $"{speed:0.##}x",
            };
            if (Math.Abs(current - speed) < 0.01)
                item.Icon = new FontIcon { Glyph = "\uE73E" };
            item.Click += (_, _) => SegmentSpeedChangeRequested?.Invoke(this, (video.Id, speed));
            menu.Items.Add(item);
        }

        AppendSegmentAudioSection(menu, video, current);

        menu.Items.Add(new MenuFlyoutSeparator());

        var splitItem = new MenuFlyoutItem
        {
            Text = "Split at Playhead",
            Icon = new FontIcon { Glyph = "\uE8C6" },
            // Splitting always cuts whatever the playhead is over, so offering it while the
            // playhead sits outside the right-clicked block would silently edit a DIFFERENT
            // segment than the one whose menu is open.
            IsEnabled = PlayheadPosition > video.Start && PlayheadPosition < video.End,
        };
        splitItem.Click += (_, _) => SegmentSplitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(splitItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Delete Segment",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        deleteItem.Click += (_, _) => SegmentDeleteRequested?.Invoke(this, video.Id);
        menu.Items.Add(deleteItem);

        menu.ShowAt(canvas, pos);
    }

    /// <summary>
    /// Appends the "Audio" section: whether this segment's recorded audio plays, and the
    /// escape hatch for wanting it somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One toggle, at every speed. Audible audio is always re-timed to match the picture
    /// (pitch preserved), so there is nothing else to choose: a user who wants the audio at
    /// its original rate detaches it, which hands them the whole recording as a movable block
    /// instead of silently truncating it at the segment boundary — strictly more useful than
    /// the "keep original speed" mode this replaced.
    /// </para>
    /// <para>
    /// Same one-gutter rule as the speed presets: plain <see cref="MenuFlyoutItem"/>s with a
    /// glyph in the icon slot, never a <c>RadioMenuFlyoutItem</c>, which would reserve a
    /// second column.
    /// </para>
    /// </remarks>
    private void AppendSegmentAudioSection(
        MenuFlyout menu, VideoSegment video, double currentSpeed, bool includeLeadingSeparator = true)
    {
        if (includeLeadingSeparator)
            menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Audio",
            Icon = new FontIcon { Glyph = "\uE767" },
            IsEnabled = false,
        });

        bool muted = video.AudioMode == SegmentAudioMode.Muted;
        bool detached = Model is { } model && ReattachSegmentAudioOperation.HasDetachedAudio(model, video.Id);

        if (detached)
        {
            // Its recording is already on the timeline as its own blocks. A plain unmute here
            // would play the same capture twice, so the only coherent way back is to take the
            // blocks away again.
            var reattach = new MenuFlyoutItem
            {
                Text = "Re-attach audio (removes detached blocks)",
                Icon = new FontIcon { Glyph = "\uE72C" },
            };
            reattach.Click += (_, _) => SegmentAudioReattachRequested?.Invoke(this, video.Id);
            menu.Items.Add(reattach);
            return;
        }

        var toggle = new MenuFlyoutItem
        {
            Text = muted ? "Unmute this segment" : "Mute this segment",
            Icon = new FontIcon { Glyph = muted ? "\uE767" : "\uE74F" },
        };
        toggle.Click += (_, _) => SegmentAudioModeChangeRequested?.Invoke(
            this, (video.Id, muted ? SegmentAudioMode.TimeStretch : SegmentAudioMode.Muted));
        menu.Items.Add(toggle);

        var detach = new MenuFlyoutItem
        {
            Text = "Detach audio (move / trim freely)",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            // Already silent: detaching would lift audio the segment is not playing, and
            // then silence a segment that is already silenced. Unmute (or undo) first.
            IsEnabled = !muted,
        };
        detach.Click += (_, _) => SegmentAudioDetachRequested?.Invoke(this, video.Id);
        menu.Items.Add(detach);
    }

    /// <summary>
    /// Determines which segment (and which part of it — body or trim edge) is under
    /// the given X coordinate on the primary track.
    /// </summary>
    private SegmentHitTarget HitTestSegment(TimelineModel model, double x, double y, out string? segmentId)
    {
        segmentId = null;
        int trackIndex = VideoTrackIndexFromY(model, y);
        int trackCount = VideoDisplayTrackCount(model);
        var (rowY, rowH, rowPad) = VideoTrackRowBounds(trackIndex, trackCount);
        float clipY = rowY + rowPad;
        float clipH = rowH - rowPad * 2;
        if (y < clipY || y > clipY + clipH) return SegmentHitTarget.None;

        var (windowHitId, windowTarget) = HitTestTextSlideWindowHandle(model, x, y, trackIndex);
        if (windowHitId is not null)
        {
            segmentId = windowHitId;
            return windowTarget == SegmentHitTarget.LeftEdge
                ? SegmentHitTarget.TextWindowInEdge
                : SegmentHitTarget.TextWindowOutEdge;
        }

        for (int i = model.Segments.Count - 1; i >= 0; i--)
        {
            var seg = model.Segments[i];
            if (seg.TrackIndex != trackIndex) continue;
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
    /// Snaps a zoom-track X coordinate to nearby snap targets in the dragged
    /// keyframe's own source-file domain.
    /// </summary>
    private double SnapZoomX(
        TimelineModel model,
        double x,
        string? excludeKeyframeId,
        string? sourceVideoFilePath,
        bool enabled)
    {
        _segmentSnapGuideX = double.NaN;
        if (!enabled) return x;

        double best = x;
        double bestDist = SegmentSnapThreshold;

        void Consider(double candidate)
        {
            if (double.IsNaN(candidate))
                return;

            double dist = Math.Abs(candidate - x);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        Consider(TimeToX(PlayheadPosition));
        foreach (var kf in model.ZoomKeyframes)
        {
            if (kf.Id == excludeKeyframeId)
                continue;

            if (!string.Equals(kf.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            Consider(ZoomKeyframeTimeToX(kf, kf.Start));
            Consider(ZoomKeyframeTimeToX(kf, kf.End));
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
        if (targetIndex >= model.Segments.Count ||
            model.Segments[targetIndex].TrackIndex != TimelineModel.BaseTrackIndex)
        {
            var lastBase = model.BaseSegments.LastOrDefault();
            return TimeToX(lastBase?.End ?? model.TotalSegmentsDuration);
        }
        return TimeToX(model.Segments[targetIndex].Start);
    }

    /// <summary>
    /// Computes the target insertion index (in original-list coordinates, as expected
    /// by <see cref="MoveSegmentOperation"/>) for a segment dropped at <paramref name="dropCenter"/>.
    /// </summary>
    private static int ComputeMoveTargetIndex(TimelineModel model, string? draggedId, TimeSpan dropCenter)
    {
        int afterLastBase = model.Segments.Count;
        for (int i = 0; i < model.Segments.Count; i++)
        {
            var seg = model.Segments[i];
            if (seg.Id == draggedId) continue;
            if (seg.TrackIndex != TimelineModel.BaseTrackIndex) continue;
            afterLastBase = i + 1;
            var mid = seg.Start + seg.Duration / 2;
            if (mid > dropCenter) return i;
        }
        return afterLastBase;
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
            ClearOtherSelections(SelectionKind.Camera);
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
            ClearOtherSelections(SelectionKind.None);
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
            ClearOtherSelections(SelectionKind.Camera);
            SelectedCameraSegmentId = hitId;
            CameraSegmentSelected?.Invoke(this, hitId);
            CameraSegmentRemoveRequested?.Invoke(this, hitId);
        }
    }

    // ─────────────────────────── Text Overlay Track ───────────────────────────
    // Unlike the camera track — whose CameraSegment ranges are always primary source
    // time (see XToPrimarySourceTime) — a TextOverlaySegment explicitly supports
    // per-recording ownership via SourceVideoFilePath (null = primary, otherwise an
    // appended/imported recording), exactly like ZoomKeyframe. Export
    // (SegmentFrameComposer.SelectTextOverlays) and preview (TimelineModel
    // .GetActiveTextOverlays) already honour that per-recording scoping, so this track
    // must too: every draw/hit-test/move/resize/create below routes through the owning
    // VideoSegment via OwningSegmentForTextOverlay/TextOverlayTimeToX, mirroring the
    // zoom track's OwningSegmentForKeyframe/ZoomKeyframeTimeToX rather than the camera
    // track's SourceTimeToX/XToPrimarySourceTime.

    private const float TextOverlayVerticalPadding = 6f;

    private string? _selectedTextOverlayId;
    private double _textOverlayDragStartX = double.NaN;
    private double _textOverlayDragCurrentX = double.NaN;
    private TimeSpan _textOverlayDragOriginalStart;
    private TimeSpan _textOverlayDragOriginalEnd;
    private bool _textOverlayCreateActive;
    private TimeSpan _textOverlayCreateStart;
    private TimeSpan _textOverlayCreateEnd;

    /// <summary>Source file a text overlay is being drag-to-created against, set once at
    /// press-time by <see cref="XToSegmentVideoTime"/> (null = primary). Mirrors
    /// <see cref="_zoomCreateFile"/>.</summary>
    private string? _textOverlayCreateFile;

    /// <summary>Id of the selected text overlay, or null.</summary>
    public string? SelectedTextOverlayId
    {
        get => _selectedTextOverlayId;
        set
        {
            // Routed through the same mutual-exclusion helper the pointer paths use, because
            // this setter is also how PROGRAMMATIC selection happens (e.g. the toolbar's "Add
            // Text Overlay", which selects the overlay it just created). Assigning the field
            // directly left whatever was previously selected — a transition, say — still
            // selected and never raised its deselection event, so its properties pane stayed
            // open showing state the user had moved on from.
            if (value is not null)
                ClearOtherSelections(SelectionKind.TextOverlay);

            _selectedTextOverlayId = value;
            TextTrackCanvas?.Invalidate();
        }
    }

    public void ClearTextOverlaySelection()
    {
        if (_selectedTextOverlayId is not null)
        {
            _selectedTextOverlayId = null;
            TextOverlaySelected?.Invoke(this, null);
            TextTrackCanvas?.Invalidate();
        }
    }

    public event EventHandler<string?>? TextOverlaySelected;
    /// <summary>
    /// Raised when a new overlay is created, reporting the source file it was created
    /// against (null = primary), so it's tagged against the correct recording just like
    /// <see cref="ZoomSegmentCreated"/>.
    /// </summary>
    public event EventHandler<(TimeSpan Start, TimeSpan End, string? SourceVideoFilePath)>? TextOverlayCreated;
    public event EventHandler<(string Id, TimeSpan NewStart)>? TextOverlayMoved;
    public event EventHandler<(string Id, bool IsStartEdge, TimeSpan NewEdgeTime)>? TextOverlayResized;
    public event EventHandler<string>? TextOverlayRemoveRequested;

    /// <summary>Source file of the currently selected text overlay (null = primary).
    /// Mirrors <see cref="SelectedZoomKeyframeFile"/>.</summary>
    private string? SelectedTextOverlaySourceFile =>
        Model?.TextOverlays.FirstOrDefault(o => o.Id == _selectedTextOverlayId)?.SourceVideoFilePath;

    private void TextTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(ZoomTrackBackground);
        if (model is null || model.DisplayDuration.TotalSeconds <= 0) return;

        // ── Amber/orange palette — kept visually distinct from the green camera track ──
        var fill = Color.FromArgb(230, 245, 166, 35);
        var fillSelected = Color.FromArgb(245, 255, 196, 80);
        var fillDisabled = Color.FromArgb(120, 130, 125, 115);
        var border = Color.FromArgb(255, 196, 128, 20);
        var borderSelected = Color.FromArgb(255, 255, 235, 190);
        var handleColor = Color.FromArgb(255, 255, 255, 255);
        var textColor = Color.FromArgb(255, 255, 255, 255);

        // Unlike the camera track, there is no per-video-segment "presence" bar here —
        // text overlays don't correspond to a source recording, only to user-created
        // TextOverlaySegments — so we go straight to drawing the overlay blocks.
        foreach (var seg in model.TextOverlays)
        {
            float x1 = (float)GetTextOverlayStartX(seg);
            float x2 = (float)GetTextOverlayEndX(seg);
            if (float.IsNaN(x1) || float.IsNaN(x2)) continue; // overlay's recording isn't on the timeline
            if (x2 < 0 || x1 > w) continue;

            float segW = Math.Max(2, x2 - x1);
            float segY = TextOverlayVerticalPadding;
            float segH = h - TextOverlayVerticalPadding * 2;
            bool isSelected = seg.Id == _selectedTextOverlayId;

            using var rect = CanvasGeometry.CreateRoundedRectangle(ds, x1, segY, segW, segH, 4, 4);
            ds.FillGeometry(rect, !seg.Enabled ? fillDisabled : isSelected ? fillSelected : fill);
            ds.DrawGeometry(rect, isSelected ? borderSelected : border, isSelected ? 1.5f : 1f);

            if (segW > 20)
            {
                // Label each block with the overlay's own text (single line, ellipsised
                // when it doesn't fit) so the track is scannable without opening the
                // property pane for every block, falling back to "Text" when empty.
                string baseText = string.IsNullOrWhiteSpace(seg.Text) ? "Text" : seg.Text;
                string label = seg.Enabled ? baseText : baseText + " (off)";
                using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 11,
                    FontFamily = "Segoe UI",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
                    WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
                    TrimmingGranularity = Microsoft.Graphics.Canvas.Text.CanvasTextTrimmingGranularity.Character,
                    TrimmingSign = Microsoft.Graphics.Canvas.Text.CanvasTrimmingSign.Ellipsis,
                };
                ds.DrawText(label, new Rect(x1 + 6, segY, segW - 12, segH), textColor, fmt);
            }

            if (isSelected)
            {
                float handleW = 3, handleH = segH * 0.5f, handleY = segY + (segH - handleH) / 2;
                ds.FillRoundedRectangle(x1 + 1, handleY, handleW, handleH, 1, 1, handleColor);
                ds.FillRoundedRectangle(x2 - handleW - 1, handleY, handleW, handleH, 1, 1, handleColor);
            }
        }

        // Create preview
        if (_textOverlayCreateActive && _dragMode == DragMode.TextOverlayCreate)
        {
            float cx1 = (float)TextOverlayCreateTimeToX(_textOverlayCreateStart < _textOverlayCreateEnd ? _textOverlayCreateStart : _textOverlayCreateEnd);
            float cx2 = (float)TextOverlayCreateTimeToX(_textOverlayCreateStart < _textOverlayCreateEnd ? _textOverlayCreateEnd : _textOverlayCreateStart);
            float cw = Math.Max(2, cx2 - cx1);
            float cy = TextOverlayVerticalPadding;
            float ch = h - TextOverlayVerticalPadding * 2;
            using var preview = CanvasGeometry.CreateRoundedRectangle(ds, cx1, cy, cw, ch, 4, 4);
            ds.FillGeometry(preview, Color.FromArgb(120, 245, 166, 35));
            ds.DrawGeometry(preview, border, 1f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }
    }

    /// <summary>
    /// Finds the video segment that owns a text overlay: the segment matching the
    /// overlay's source file whose source range contains its Start (falling back to the
    /// first file-matching segment). Mirrors <see cref="OwningSegmentForKeyframe"/>
    /// exactly, using Start as the anchor since a <see cref="TextOverlaySegment"/> (unlike
    /// a <see cref="ZoomKeyframe"/>) has no single Timestamp to anchor on.
    /// </summary>
    private VideoSegment? OwningSegmentForTextOverlay(TextOverlaySegment overlay)
    {
        var model = Model;
        if (model is null) return null;

        VideoSegment? firstMatch = null;
        foreach (var seg in model.Segments.OfType<VideoSegment>())
        {
            bool match = overlay.SourceVideoFilePath is null
                ? (model.PrimaryVideoFilePath is null ||
                   string.Equals(seg.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase))
                : string.Equals(seg.VideoFilePath, overlay.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            firstMatch ??= seg;
            var local = overlay.Start - seg.SourceStart;
            if (local >= TimeSpan.Zero && local <= seg.SourceDuration)
                return seg;
        }
        return firstMatch;
    }

    /// <summary>
    /// Maps a text overlay's source time to an output X by routing through the video
    /// segment that owns it (primary or appended), mirroring
    /// <see cref="ZoomKeyframeTimeToX"/>. Directly adjacent source-contiguous speed
    /// pieces are traversed piecewise; unrelated occurrences of the same file are not.
    /// </summary>
    private double TextOverlayTimeToX(TextOverlaySegment overlay, TimeSpan sourceTime)
    {
        var model = Model;
        if (model is null) return double.NaN;
        if (model.Segments.Count == 0) return TimeToX(sourceTime);

        var seg = OwningSegmentForTextOverlay(overlay);
        if (seg is null) return double.NaN;

        return TimeToX(model.MapSourceTimeFromOwningSegment(seg, sourceTime));
    }

    /// <summary>Maps a create-time (in <see cref="_textOverlayCreateFile"/>'s source
    /// space) to X, mirroring <see cref="ZoomCreateTimeToX"/>.</summary>
    private double TextOverlayCreateTimeToX(TimeSpan sourceTime)
        => TextOverlayTimeToX(
            new TextOverlaySegment { SourceVideoFilePath = _textOverlayCreateFile, Start = _textOverlayCreateStart },
            sourceTime);

    private double GetTextOverlayStartX(TextOverlaySegment seg)
    {
        if (seg.Id == _selectedTextOverlayId && !double.IsNaN(_textOverlayDragCurrentX))
        {
            if (_dragMode == DragMode.TextOverlayBody)
                return TextOverlayTimeToX(seg, _textOverlayDragOriginalStart) + (_textOverlayDragCurrentX - _textOverlayDragStartX);
            if (_dragMode == DragMode.TextOverlayLeftEdge)
                return _textOverlayDragCurrentX;
        }
        return TextOverlayTimeToX(seg, seg.Start);
    }

    private double GetTextOverlayEndX(TextOverlaySegment seg)
    {
        if (seg.Id == _selectedTextOverlayId && !double.IsNaN(_textOverlayDragCurrentX))
        {
            if (_dragMode == DragMode.TextOverlayBody)
                return TextOverlayTimeToX(seg, _textOverlayDragOriginalEnd) + (_textOverlayDragCurrentX - _textOverlayDragStartX);
            if (_dragMode == DragMode.TextOverlayRightEdge)
                return _textOverlayDragCurrentX;
        }
        return TextOverlayTimeToX(seg, seg.End);
    }

    private (string? Id, SegmentHitTarget Target) HitTestTextOverlay(double posX, double posY)
    {
        var model = Model;
        if (model is null) return (null, SegmentHitTarget.None);

        float h = (float)TextTrackCanvas.ActualHeight;
        float segY = TextOverlayVerticalPadding;
        float segH = h - TextOverlayVerticalPadding * 2;
        if (posY < segY || posY > segY + segH) return (null, SegmentHitTarget.None);

        for (int i = model.TextOverlays.Count - 1; i >= 0; i--)
        {
            var seg = model.TextOverlays[i];
            float x1 = (float)TextOverlayTimeToX(seg, seg.Start);
            float x2 = (float)TextOverlayTimeToX(seg, seg.End);
            if (float.IsNaN(x1) || float.IsNaN(x2)) continue; // overlay's recording isn't on the timeline
            if (posX < x1 || posX > x2) continue;

            if (posX - x1 <= SegmentEdgeHitWidth) return (seg.Id, SegmentHitTarget.LeftEdge);
            if (x2 - posX <= SegmentEdgeHitWidth) return (seg.Id, SegmentHitTarget.RightEdge);
            return (seg.Id, SegmentHitTarget.Body);
        }
        return (null, SegmentHitTarget.None);
    }

    private void TextTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;
        var (hitId, target) = HitTestTextOverlay(pos.X, pos.Y);

        if (hitId is not null)
        {
            ClearOtherSelections(SelectionKind.TextOverlay);
            SelectedTextOverlayId = hitId;
            TextOverlaySelected?.Invoke(this, hitId);

            var seg = Model?.TextOverlays.FirstOrDefault(s => s.Id == hitId);
            if (seg is null) return;

            _textOverlayDragStartX = pos.X;
            _textOverlayDragCurrentX = pos.X;
            _textOverlayDragOriginalStart = seg.Start;
            _textOverlayDragOriginalEnd = seg.End;

            _dragMode = target switch
            {
                SegmentHitTarget.LeftEdge => DragMode.TextOverlayLeftEdge,
                SegmentHitTarget.RightEdge => DragMode.TextOverlayRightEdge,
                _ => DragMode.TextOverlayBody,
            };
            SetCursor(target is SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge
                ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
            canvas.CapturePointer(e.Pointer);
        }
        else
        {
            ClearOtherSelections(SelectionKind.None);
            PlayheadPosition = XToTime(pos.X);

            // Capture the source file under the cursor (like _zoomCreateFile) so an
            // overlay created over an appended clip belongs to that clip, not the
            // primary recording.
            var start = XToSegmentVideoTime(pos.X, out _textOverlayCreateFile);
            if (start is null)
            {
                // No video segment anywhere to attach a text overlay to. Reject the
                // gesture, leaving no transient drag state.
                _textOverlayCreateFile = null;
                _dragMode = DragMode.None;
                return;
            }

            _textOverlayDragStartX = pos.X;
            _textOverlayDragCurrentX = pos.X;
            _textOverlayCreateActive = false;
            _textOverlayCreateStart = start.Value;
            _dragMode = DragMode.TextOverlayCreate;
            canvas.CapturePointer(e.Pointer);
        }
    }

    private void TextTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetCurrentPoint(canvas).Position;

        switch (_dragMode)
        {
            case DragMode.TextOverlayBody:
                _textOverlayDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeAll);
                InvalidateAll();
                break;
            case DragMode.TextOverlayLeftEdge:
            case DragMode.TextOverlayRightEdge:
                _textOverlayDragCurrentX = Math.Clamp(pos.X, 0, canvas.ActualWidth);
                SetCursor(InputSystemCursorShape.SizeWestEast);
                InvalidateAll();
                break;
            case DragMode.TextOverlayCreate:
                if (Math.Abs(pos.X - _textOverlayDragStartX) >= ZoomCreateDragThreshold)
                {
                    // Stay anchored to _textOverlayCreateFile's source domain (set once
                    // at press-time), same rationale as the zoom track's
                    // ZoomSegmentCreate case — re-resolving whatever segment is under
                    // the pointer now would mix a different recording's timestamp into
                    // this (still file-tagged) overlay.
                    var end = XToKeyframeFileTime(pos.X, _textOverlayCreateFile);
                    if (end is not null)
                    {
                        _textOverlayCreateActive = true;
                        _textOverlayCreateEnd = end.Value;
                        InvalidateAll();
                    }
                    // else: pointer is outside every segment for _textOverlayCreateFile
                    // with none to clamp to — keep the previous _textOverlayCreateEnd.
                }
                else
                {
                    PlayheadPosition = XToTime(pos.X);
                }
                break;
            case DragMode.None:
                var (_, target) = HitTestTextOverlay(pos.X, pos.Y);
                SetCursor(target switch
                {
                    SegmentHitTarget.LeftEdge or SegmentHitTarget.RightEdge => InputSystemCursorShape.SizeWestEast,
                    SegmentHitTarget.Body => InputSystemCursorShape.Hand,
                    _ => InputSystemCursorShape.Arrow,
                });
                break;
        }
    }

    private void TextTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;

        switch (_dragMode)
        {
            case DragMode.TextOverlayBody when _selectedTextOverlayId is not null:
            {
                double deltaX = _textOverlayDragCurrentX - _textOverlayDragStartX;
                if (Math.Abs(deltaX) > 1)
                {
                    // Convert the dragged X back to a source time in the OWNING
                    // overlay's own recording's domain, not the primary's — mirrors the
                    // zoom track's ZoomSegmentBody case exactly.
                    var file = SelectedTextOverlaySourceFile;
                    var startTime = XToKeyframeFileTime(_textOverlayDragStartX, file);
                    var movedTime = XToKeyframeFileTime(_textOverlayDragStartX + deltaX, file);
                    if (startTime is not null && movedTime is not null)
                    {
                        var newStart = _textOverlayDragOriginalStart + (movedTime.Value - startTime.Value);
                        if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                        TextOverlayMoved?.Invoke(this, (_selectedTextOverlayId, newStart));
                    }
                    // else: this overlay's recording has no segment on the timeline at all
                    // — reject the move rather than writing a time from another
                    // recording's domain into it. (When the pointer merely strays over a
                    // different clip or a text slide, XToKeyframeFileTime clamps to the
                    // nearest edge of THIS overlay's own recording, so the move pins to
                    // the clip boundary instead of jumping sources — same as zoom.)
                }
                break;
            }
            case DragMode.TextOverlayLeftEdge when _selectedTextOverlayId is not null:
            {
                var newEdge = XToKeyframeFileTime(Math.Clamp(_textOverlayDragCurrentX, 0, canvas.ActualWidth), SelectedTextOverlaySourceFile);
                if (newEdge is not null && newEdge.Value != _textOverlayDragOriginalStart)
                    TextOverlayResized?.Invoke(this, (_selectedTextOverlayId, true, newEdge.Value));
                break;
            }
            case DragMode.TextOverlayRightEdge when _selectedTextOverlayId is not null:
            {
                var newEdge = XToKeyframeFileTime(Math.Clamp(_textOverlayDragCurrentX, 0, canvas.ActualWidth), SelectedTextOverlaySourceFile);
                if (newEdge is not null && newEdge.Value != _textOverlayDragOriginalEnd)
                    TextOverlayResized?.Invoke(this, (_selectedTextOverlayId, false, newEdge.Value));
                break;
            }
            case DragMode.TextOverlayCreate when _textOverlayCreateActive:
            {
                var start = _textOverlayCreateStart < _textOverlayCreateEnd ? _textOverlayCreateStart : _textOverlayCreateEnd;
                var end = _textOverlayCreateStart < _textOverlayCreateEnd ? _textOverlayCreateEnd : _textOverlayCreateStart;
                if ((end - start) >= TrimTextOverlayOperation.MinDuration)
                    TextOverlayCreated?.Invoke(this, (start, end, _textOverlayCreateFile));
                _textOverlayCreateActive = false;
                break;
            }
        }

        _textOverlayDragStartX = double.NaN;
        _textOverlayDragCurrentX = double.NaN;
        _dragMode = DragMode.None;
        SetCursor(InputSystemCursorShape.Arrow);
        canvas.ReleasePointerCapture(e.Pointer);
        InvalidateAll();
    }

    private void TextTrack_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);
        var (hitId, _) = HitTestTextOverlay(pos.X, pos.Y);
        if (hitId is not null)
        {
            ClearOtherSelections(SelectionKind.TextOverlay);
            SelectedTextOverlayId = hitId;
            TextOverlaySelected?.Invoke(this, hitId);
            TextOverlayRemoveRequested?.Invoke(this, hitId);
        }
    }

    /// <summary>
    /// Double-clicking empty track space creates a default 3-second overlay starting at
    /// the clicked source time. The camera track only supports drag-to-create, but a text
    /// overlay's whole point is its wording, so this gives users a one-click way to drop
    /// a default block down and then edit its text in the property pane — the primary
    /// discoverable way to add an overlay. Double-clicking an existing block is a no-op
    /// here because selection already happened on the preceding pointer-press.
    /// </summary>
    private void TextTrack_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not CanvasControl canvas) return;
        var pos = e.GetPosition(canvas);
        var (hitId, _) = HitTestTextOverlay(pos.X, pos.Y);
        if (hitId is not null) return;

        // Capture the source file under the cursor (like the drag-to-create gesture)
        // so a double-click over an appended clip creates an overlay owned by that
        // clip, not the primary recording.
        var start = XToSegmentVideoTime(pos.X, out var sourceFile);
        if (start is null) return;

        var clampedStart = start.Value < TimeSpan.Zero ? TimeSpan.Zero : start.Value;
        var duration = TimeSpan.FromSeconds(3);
        if (duration < TrimTextOverlayOperation.MinDuration)
            duration = TrimTextOverlayOperation.MinDuration;

        TextOverlayCreated?.Invoke(this, (clampedStart, clampedStart + duration, sourceFile));
    }

    private void Grid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null) return;

        // Zooming or scrolling moves every chip out from under the pointer. Both the pending
        // linger timer and any already-open tooltip are anchored to a rectangle that is about to
        // be wrong, so drop the hover entirely — the next PointerMoved re-hit-tests and starts a
        // fresh linger against wherever the chips actually landed.
        _hoveredTransitionChipId = null;
        HideTransitionChipToolTip();

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

/// <summary>
/// Describes a requested full-frame video segment lane move; the control reports intent
/// only so the editor page can apply the shared undoable timeline operation.
/// </summary>
public sealed class SegmentTrackMoveEventArgs : EventArgs
{
    /// <summary>Segment to move; ids survive reflow whereas list indexes do not.</summary>
    public string SegmentId { get; init; } = "";

    /// <summary>Absolute output start requested for overlay lanes, or the base insertion hint.</summary>
    public TimeSpan NewStart { get; init; }

    /// <summary>Destination full-frame video track: 0 is the contiguous base chain.</summary>
    public int NewTrackIndex { get; init; }
}

/// <summary>
/// Describes a requested edit to a text slide's inner animation window, separate from
/// segment trim so preview/export keep using the shared Core timing rules.
/// </summary>
public sealed class TextSlideWindowEventArgs : EventArgs
{
    /// <summary>Text slide whose animation window changed.</summary>
    public string SegmentId { get; init; } = "";

    /// <summary>Offset from the segment start where the text begins animating in.</summary>
    public TimeSpan InStart { get; init; }

    /// <summary>Offset from the segment start where the text has finished animating out.</summary>
    public TimeSpan OutEnd { get; init; }
}
