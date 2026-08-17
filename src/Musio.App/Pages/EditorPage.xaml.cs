using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Media;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.Helpers;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    public ExportViewModel ExportVM { get; }
    private VideoFrameReader? _frameReader;
    private PreviewRenderer? _previewRenderer;
    private TimelineMapper? _timelineMapper;
    private AudioPlaybackEngine? _audioPlayer;

    /// <summary>
    /// Second engine, for inserted voice-over/music tracks only.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="_audioPlayer"/> because the two are seeked in different
    /// clocks: recorded audio is positioned in the primary recording's own file time (via
    /// <see cref="AudioPositionForVideo"/>, which maps through the segments), while inserted
    /// tracks are anchored to OUTPUT time. A single engine seeks every reader from one
    /// position, so it cannot serve both.
    /// </remarks>
    private AudioPlaybackEngine? _insertedAudioPlayer;
    private bool _compositorReady;

    // Text slide rendering for segment-based preview
    private TextSlideRenderer? _textSlideRenderer;

    // Blends the outgoing/incoming pair at every configured segment-boundary transition in
    // the preview (not just slide↔neighbour boundaries — see TransitionResolver), lazily
    // created.
    private TransitionRenderer? _transitionRenderer;

    // Per-appended-recording preview contexts (frame reader + compositor + webcam),
    // keyed by VideoSegment.Id. The primary recording uses _frameReader/_previewRenderer.
    private readonly Dictionary<string, SegmentPreview> _segmentPreviews = new();
    private string? _lastRenderedSegmentId;

    /// <summary>
    /// Cache generation for <see cref="_segmentPreviews"/>. A build awaits a multi-second
    /// decoder open, during which any of the clear sites below can run — disposing the
    /// entry that build is still populating and then dropping it from the dictionary. The
    /// build would go on to assign a live decoder and compositor onto that orphan, which
    /// nothing would ever dispose. Each build captures this value and abandons once it
    /// moves on, the same way <see cref="_previewInitGeneration"/> guards the primary.
    /// </summary>
    private int _segmentPreviewGeneration;

    /// <summary>
    /// Generation guard for primary-recording preview state (<see cref="_previewRenderer"/>
    /// and <see cref="_frameReader"/>) that a transition compose might be using across an
    /// await. Bumped everywhere either is disposed or replaced (page unload, graphics
    /// recovery, a full re-init, a style rebuild, or an adaptive-quality resolution swap).
    /// Composing a transition's outgoing/incoming side awaits a frame decode (and,
    /// on the singleton, a webcam extraction) with that state read beforehand; any of the
    /// above can run to completion on the UI thread while that await is in flight (this is
    /// single-threaded interleaving, not true parallelism, but the effect is the same for
    /// a resumed continuation). Re-checking this value after the await — see
    /// <see cref="ComposePreviewFrameAtOffsetAsync"/> — is what lets the continuation
    /// detect that and bail out instead of touching a disposed compositor/reader or
    /// silently compositing against a replacement built for different state.
    /// </summary>
    private int _primaryPreviewStateGeneration;

    // Thumbnail generation versioning — prevents stale results
    private int _thumbnailGenerationId;

    /// <summary>
    /// Preview initialization versioning. <see cref="InitializePreviewCoreAsync"/> awaits a
    /// multi-second decoder open, so an unload or a second run can start before the first
    /// one finishes. Each run captures this value up front and abandons (disposing anything
    /// it built) once the value moves on, instead of publishing a decoder onto a page that
    /// has already torn down or that a newer run now owns.
    /// </summary>
    private int _previewInitGeneration;

    /// <summary>
    /// Source video path whose filmstrip is fully generated, so a re-entrant
    /// <see cref="InitializePreviewAsync"/> does not tear down and restart a finished strip.
    /// </summary>
    private string? _thumbnailsCompletedForPath;

    /// <summary>
    /// Source video path whose filmstrip is currently being generated, so overlapping
    /// initialisations join the running pass instead of cancelling and restarting it.
    /// </summary>
    private string? _thumbnailsInFlightForPath;

    /// <summary>
    /// Non-primary source files whose filmstrip is built or currently building. Appending a
    /// recording or importing a video adds a source without changing the primary, so the
    /// primary-keyed gate above cannot tell which strips are still missing; this tracks them
    /// per file so a re-entrant initialisation builds only the new ones and never rebuilds
    /// (and re-disposes the bitmaps of) a strip that is already on screen.
    /// </summary>
    private readonly HashSet<string> _thumbnailsDoneForFiles = new(StringComparer.OrdinalIgnoreCase);

    // Webcam overlay for editor preview
    private Windows.Media.Editing.MediaComposition? _webcamComposition;
    private CanvasBitmap? _lastWebcamFrame;
    private int _lastRenderedFrameIndex = -1;
    private bool _isRendering;
    private TimeSpan? _pendingRenderPosition;
    private bool _pendingRenderForce;

    /// <summary>
    /// Set by the frame-render path when the decoder returns no bitmap for the requested
    /// position, so <see cref="UpdatePreviewFrameAsync"/> can schedule a retry.
    /// </summary>
    private bool _decodeMissed;

    /// <summary>
    /// Consecutive decode misses at a stationary playhead. Reset by the first frame that
    /// decodes, and bounded by <c>MaxDecodeMissRetries</c> so a genuinely undecodable
    /// position costs a few retries rather than spinning forever.
    /// </summary>
    private int _decodeMissRetries;
    private bool _syncingTimelineFromPlayback;
    private readonly EditorGraphicsDeviceManager _graphicsDeviceManager;
    private bool _pageUnloaded;

    // Background style editing state
    private Debouncer? _styleDebouncer;

    // Motion (motion blur / camera drift) editing state — separate debounce timer so a
    // slider drag on these controls doesn't interact with the background-style debounce.
    private Debouncer? _motionDebouncer;

    // Cursor style editing state
    private Debouncer? _cursorDebouncer;

    // Text overlay editing state — separate debounce timer so a text-box keystroke never
    // interacts with the background-style / motion / cursor debounces above. The model is
    // still committed (through UndoRedoManager, for undo) on every keystroke; only the
    // (expensive) preview re-render is debounced.
    private Debouncer? _overlayPreviewDebouncer;

    // Text-slide animation-window sliders — its own timer so a window drag is never coalesced
    // with a style/motion/cursor edit. Unlike the overlay debounce above, this one defers the
    // MODEL commit too, so a whole thumb drag lands as a single undo step.
    private Debouncer? _slideTextWindowDebouncer;
    private bool _hasWebcamOverlay;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        ExportVM = new ExportViewModel();
        InitializeComponent();
        _graphicsDeviceManager = new EditorGraphicsDeviceManager(
            DispatcherQueue, RecoverGraphicsDeviceAsync, FlushPendingRenderAsync,
            () => _pageUnloaded);
        _graphicsDeviceManager.Attach();

        // A CanvasControl recovers from GPU device loss on its own, and need not raise
        // DeviceLost on the shared device the manager watches. When that happens every
        // bitmap and render target the editor cached is dead, and nothing else notices:
        // the preview and all timeline tracks just stop drawing. These are the only
        // notifications the app gets for that case.
        Preview.DeviceRecreated += (_, _) =>
            _graphicsDeviceManager.RequestRecovery("preview canvas device recreated");
        Timeline.DeviceRecreated += (_, _) =>
            _graphicsDeviceManager.RequestRecovery("timeline canvas device recreated");
        Loaded += (_, _) =>
        {
            _pageUnloaded = false;
            _graphicsDeviceManager.Attach();
        };
        WirePropertyPanels();

        // OverlayHeightSlider lives in the same view as OverlayWidthSlider (wired in
        // WirePropertyPanels, which this file does not own), so it is wired here instead —
        // same handler shape/style as OverlayWidthSlider_ValueChanged.
        PropertiesPanel.TextOverlay.OverlayHeightSlider.ValueChanged += OverlayHeightSlider_ValueChanged;

        Preview.Duration = GetMappedDuration();

        // Load frames and initialize compositor with cursor effects
        _ = InitializePreviewAsync();

        // Keep webcam overlay in sync with preview frame layout
        Preview.FrameLayoutChanged += (_, _) =>
        {
            if (_hasWebcamOverlay)
                UpdateWebcamOverlayPosition();

            // The zoom-region picker maps through the composed frame's layout, so it has to
            // follow every redraw that can move or resize it.
            if (_zoomRegionEditMode)
                UpdateZoomRegionRect();

            // Keep the shared text-edit overlay (slide or text overlay) aligned with the
            // preview frame.
            if (TextEditCanvas.Visibility == Visibility.Visible && GetActiveEditTarget() is { } activeTarget)
                PositionTextEditControls(activeTarget);
        };

        // Hide the in-place text editor while playing
        Preview.IsPlayingChanged += (_, playing) =>
        {
            if (playing) HideTextEditOverlay();
        };

        Preview.GoToStartRequested += (_, _) => GoToStart();
        Preview.GoToEndRequested += (_, _) => GoToEnd();

        // Sync playhead: when timeline scrubs, update preview + audio
        Timeline.RegisterPropertyChangedCallback(
            Controls.TimelineControl.PlayheadPositionProperty,
            (_, _) =>
            {
                Preview.PlayheadPosition = Timeline.PlayheadPosition;
                ViewModel.Model.PlayheadPosition = Timeline.PlayheadPosition;
                if (_syncingTimelineFromPlayback)
                    return;

                _ = UpdatePreviewFrameAsync(Timeline.PlayheadPosition);
                // Play short audio burst at scrub position for editing feedback
                if (!Preview.IsPlaying)
                {
                    if (AudioPositionForVideo(Timeline.PlayheadPosition) is { } audioPos)
                        _audioPlayer?.ScrubTo(audioPos);

                    // Inserted tracks scrub in OUTPUT time, so the playhead position IS
                    // their position — no segment mapping to apply.
                    if (_insertedAudioPlayer is { IsLoaded: true } inserted
                        && inserted.HasAudioAt(Timeline.PlayheadPosition))
                    {
                        inserted.ScrubTo(Timeline.PlayheadPosition);
                    }
                }
            });

        // Sync playhead: when preview plays, update timeline
        Preview.PlaybackTick += (_, _) =>
        {
            _syncingTimelineFromPlayback = true;
            try
            {
                Timeline.PlayheadPosition = Preview.PlayheadPosition;
            }
            finally
            {
                _syncingTimelineFromPlayback = false;
            }
            ViewModel.Model.PlayheadPosition = Preview.PlayheadPosition;
            _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition);

            // Keep the audio aligned with the edited timeline on every tick: start it when the
            // playhead enters footage that has audio, pause it over slides/gaps, and correct
            // drift once linear file playback diverges from where the segments say it should be.
            if (Preview.IsPlaying)
                SyncAudioToPlayhead(Preview.PlayheadPosition);
        };

        // Sync audio play/pause with preview
        Preview.IsPlayingChanged += (_, isPlaying) =>
        {
            if (!HasPreviewAudio) return;
            if (isPlaying)
            {
                // Seeks, starts, or leaves it paused if the playhead is somewhere with no
                // audio behind it (a title slide, say) — PlaybackTick picks it up on entry.
                SyncAudioToPlayhead(Preview.PlayheadPosition);
            }
            else
            {
                _audioPlayer?.Pause();
                _insertedAudioPlayer?.Pause();
            }
        };

        // Re-seek audio when playback loops
        Preview.PlaybackLooped += (_, _) =>
        {
            if (!HasPreviewAudio) return;
            SyncAudioToPlayhead(TimeSpan.Zero);
        };

        ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;

        // Audio mix: a volume or mute change on any track label. Only a change to the SET of
        // loaded tracks (mute/unmute) rebuilds an engine or repaints the whole timeline; a
        // level change updates the open readers in place and repaints just the audio lanes.
        // Dragging the slider raises this ~30 times a second, and the first version rebuilt
        // the engine AND invalidated every canvas on each tick — enough UI-thread and decode
        // churn to starve the preview decoder ("no decoded frame; preview is stale").
        Timeline.AudioChannelMixChanged += (_, channel) =>
        {
            if (ApplyAudioMixChange(channel))
                Timeline.Refresh();
            else
                Timeline.SyncAudioMuteVisuals();
        };

        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Every statement here mutates live UI state, and the preview re-init at the
                // end is what rebuilds it. A throw partway through used to escape into the
                // XAML dispatcher with the timeline already re-pointed and its selections
                // cleared but InitializePreviewAsync never reached — a blank editor whose
                // cause depended entirely on how the app-level handler classified the
                // exception. Log it and still re-init, so a failure in the timeline swap
                // cannot cost the preview as well.
                try
                {
                    // Assigned explicitly as well as bound: the timeline must never keep
                    // rendering a model the project has moved on from.
                    Timeline.Model = ViewModel.Model;
                    _timelineMapper = null;
                    Timeline.ClearZoomSelection();
                    Timeline.ClearClipSelection();
                    Timeline.ClearTransitionSelection();
                    Preview.Duration = GetMappedDuration();
                    Timeline.Refresh();
                    ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
                }
                catch (Exception ex)
                {
                    Musio.Core.Diagnostics.DiagLog.Write(
                        "Editor", $"model reload UI sync FAILED: {ex}");
                }

                _ = InitializePreviewAsync();
            });
        };

        // Zoom segment interaction events
        Timeline.ZoomSegmentSelected += OnZoomSegmentSelected;
        Timeline.ZoomSegmentMoved += OnZoomSegmentMoved;
        Timeline.ZoomSegmentResized += OnZoomSegmentResized;
        Timeline.ZoomSegmentCreated += OnZoomSegmentCreated;
        Timeline.ZoomSegmentRemoveRequested += OnZoomSegmentRemoveRequested;

        // Video clip selection events
        Timeline.VideoClipSelected += OnVideoClipSelected;

        // Segment selection events (text slides)
        Timeline.SegmentSelected += OnSegmentSelected;

        // Primary-track segment move / ripple-trim events
        Timeline.SegmentMoveRequested += OnSegmentMoveRequested;
        Timeline.SegmentTrimRequested += OnSegmentTrimRequested;
        Timeline.SegmentSpeedChangeRequested += OnSegmentSpeedChangeRequested;
        Timeline.SegmentSplitRequested += OnSegmentSplitRequested;
        Timeline.SegmentDeleteRequested += OnSegmentDeleteRequested;
        Timeline.SegmentTrackMoveRequested += OnSegmentTrackMoveRequested;
        Timeline.TextSlideWindowChanged += OnTextSlideWindowChanged;

        // Camera track events
        Timeline.CameraSegmentSelected += OnCameraSegmentSelected;
        Timeline.CameraSegmentCreated += OnCameraSegmentCreated;
        Timeline.CameraSegmentMoved += OnCameraSegmentMoved;
        Timeline.CameraSegmentResized += OnCameraSegmentResized;
        Timeline.CameraSegmentRemoveRequested += OnCameraSegmentRemoveRequested;

        // Text overlay track events
        Timeline.TextOverlaySelected += OnTextOverlaySelected;
        Timeline.TextOverlayCreated += OnTextOverlayCreated;
        Timeline.TextOverlayMoved += OnTextOverlayMoved;
        Timeline.TextOverlayResized += OnTextOverlayResized;
        Timeline.TextOverlayRemoveRequested += OnTextOverlayRemoveRequested;

        // Inserted voice-over / music track events
        Timeline.InsertedAudioTrackSelected += OnInsertedAudioTrackSelected;
        Timeline.InsertedAudioTrackMoved += OnInsertedAudioTrackMoved;
        Timeline.InsertedAudioTrackResized += OnInsertedAudioTrackResized;
        Timeline.InsertedAudioTrackContextRequested += OnInsertedAudioTrackContextRequested;

        // Transition boundary events
        Timeline.TransitionSelected += OnTransitionSelected;
        Timeline.TransitionRemoveRequested += OnTransitionRemoveRequested;

        // Export flyout state management
        ExportFlyout.Opened += ExportFlyout_Opened;
        ExportFlyout.Closed += ExportFlyout_Closed;
        ExportVM.PropertyChanged += ExportVM_PropertyChanged;

        // Clean up when page is unloaded to prevent leaks
        Unloaded += (_, _) =>
        {
            _pageUnloaded = true;
            _graphicsDeviceManager.Detach();
            _styleDebouncer?.Stop();
            _styleDebouncer = null;
            _cursorDebouncer?.Stop();
            _cursorDebouncer = null;
            _motionDebouncer?.Stop();
            _motionDebouncer = null;
            _overlayPreviewDebouncer?.Stop();
            _overlayPreviewDebouncer = null;

            // Stop playback to halt timer ticks
            Preview.Pause();

            // Abandon any preview init still awaiting the decoder open, so it disposes
            // rather than publishes whatever it built onto this dead page.
            _previewInitGeneration++;

            // Dispose owned resources
            DisposeOffUiThread(_frameReader);
            _frameReader = null;
            _previewRenderer?.Dispose();
            _previewRenderer = null;
            _textSlideRenderer?.Dispose();
            _textSlideRenderer = null;
            _transitionRenderer?.Dispose();
            _transitionRenderer = null;
            DisposeSegmentPreviews();
            DisposePrimaryStyleRenderers();
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _insertedAudioPlayer?.Dispose();
            _insertedAudioPlayer = null;
            _webcamComposition?.Clips.Clear();
            _webcamComposition = null;
            _lastWebcamFrame?.Dispose();
            _lastWebcamFrame = null;
            _compositorReady = false;
            _thumbnailGenerationId++; // cancel any in-flight thumbnail generation
            Timeline.ClearThumbnails();
            _thumbnailsCompletedForPath = null;
            _thumbnailsInFlightForPath = null;
            _thumbnailsDoneForFiles.Clear();

            // Unsubscribe VMs from singleton event sources
            ViewModel.Cleanup();
            ExportVM.Cleanup();
        };
    }

    /// <summary>
    /// Pauses preview playback and audio. Called by App when the window
    /// becomes hidden (minimize-to-tray / system suspension).
    /// </summary>
    public void PausePlayback() => Preview.Pause();

    /// <summary>
    /// Recovery work run by <see cref="EditorGraphicsDeviceManager"/> after a
    /// <see cref="CanvasDevice.DeviceLost"/> event: tears down and rebuilds the preview
    /// pipeline, renderers, and timeline thumbnails against a freshly (re)attached shared
    /// device. The manager owns the re-entrancy/unloaded guard around this method and the
    /// queued-retry loop that calls it; this method only does the page-specific work.
    /// </summary>
    private async Task RecoverGraphicsDeviceAsync()
    {
        TimeSpan position = Preview.PlayheadPosition;
        Preview.Pause();
        _pendingRenderPosition = null;
        _pendingRenderForce = false;

        while (_isRendering && !_pageUnloaded)
            await Task.Delay(25);

        if (_pageUnloaded)
            return;

        _previewInitGeneration++;
        _thumbnailGenerationId++;
        _segmentPreviewGeneration++;

        Preview.ClearFrame();
        DisposeOffUiThread(_frameReader);
        _frameReader = null;
        TryDispose(_previewRenderer);
        _previewRenderer = null;
        _compositorReady = false;
        TryDispose(_textSlideRenderer);
        _textSlideRenderer = null;
        TryDispose(_transitionRenderer);
        _transitionRenderer = null;
        DisposeSegmentPreviews();
        DisposePrimaryStyleRenderers();
        TryDispose(_lastWebcamFrame);
        _lastWebcamFrame = null;
        try { _webcamComposition?.Clips.Clear(); } catch { }
        _webcamComposition = null;

        Timeline.ClearThumbnails();
        Timeline.ClearSegmentTrackVisuals();
        _thumbnailsCompletedForPath = null;
        _thumbnailsInFlightForPath = null;
        _thumbnailsDoneForFiles.Clear();

        _graphicsDeviceManager.Detach();
        _graphicsDeviceManager.Attach();
        await InitializePreviewCoreAsync();
        position = Preview.PlayheadPosition;

        if (_pageUnloaded)
            return;

        Timeline.InvalidateAllCanvases();
        Preview.InvalidateSurface();

        // Deliberately parked rather than drawn: this method runs inside the device
        // manager's recovery scope, so UpdatePreviewFrameAsync would refuse to render
        // and park the request anyway — but the position it parked would then be
        // discarded, because only an already-running render loop drains pending
        // requests. FlushPendingRenderAsync, which the manager invokes once the scope
        // closes, is what actually repaints the surface this method just cleared.
        _pendingRenderPosition = position;
        _pendingRenderForce = true;
        Musio.Core.Diagnostics.DiagLog.Write(
            "Editor", "editor graphics recovery rebuilt; awaiting post-recovery repaint");
    }

    /// <summary>
    /// Draws whatever render request was parked while the graphics device was recovering.
    /// Invoked by <see cref="EditorGraphicsDeviceManager"/> after its recovery scope closes,
    /// so <see cref="EditorGraphicsDeviceManager.IsRecoveryInProgress"/> is false and the
    /// render actually reaches the canvas.
    /// </summary>
    private async Task FlushPendingRenderAsync()
    {
        if (_pageUnloaded) return;

        var position = _pendingRenderPosition ?? Preview.PlayheadPosition;
        _pendingRenderPosition = null;
        _pendingRenderForce = false;

        Timeline.InvalidateAllCanvases();
        Preview.InvalidateSurface();
        await UpdatePreviewFrameAsync(position, force: true);

        Musio.Core.Diagnostics.DiagLog.Write(
            "Editor", "editor graphics recovery completed");
    }

    // In-preview text editing state, shared by full-screen text slides and text overlays
    // (see ITextEditTarget below). _previewSlideId/_previewOverlayId identify whichever
    // segment the render pipeline currently considers "on screen" for editing purposes —
    // at most one of them is non-null at a time (a slide always wins; see
    // GetActiveEditTarget). _previewFrameW/H is the canvas size that ComputeRect's pixel
    // math (and PositionTextEditControls' scale-to-layout math) is relative to.
    private string? _previewSlideId;
    private string? _previewOverlayId;
    private int _previewFrameW = 1920;
    private int _previewFrameH = 1080;
    private string? _editingTextId;

    private string? PrimaryVideoPath => ProjectService.Instance.CurrentProject?.VideoFilePath;

    /// <summary>Id of the currently selected primary-track segment (video or text slide).</summary>
    private string? _selectedPrimarySegmentId;

    // ── Text overlay track handlers ──

    private string? _selectedTextOverlayId;

    // ─── Transition boundary panel ──────────────────────────────────────

    /// <summary>Incoming segment Id of the currently-selected boundary chip, or null.</summary>
    private string? _selectedTransitionId;

    // --- Zoom Segment Handlers ---

    private bool _suppressZoomPropertyUpdate;

    // --- Zoom Region Edit Mode ---

    private bool _zoomRegionEditMode;
    private string? _zoomRegionKeyframeId;

    /// <summary>
    /// Source recording the edited keyframe belongs to (null = the primary recording).
    /// Resolves which compositor's geometry the overlay maps through.
    /// </summary>
    private string? _zoomRegionSourceFile;

    /// <summary>
    /// Mouse position at the edited keyframe's moment, in SOURCE pixels, or NaN when the
    /// source has no cursor data. Drawn as a marker so the region can be aimed at it, but
    /// only on the raw-frame fallback — a composed frame draws the real cursor itself.
    /// </summary>
    private double _zoomRegionCursorX = double.NaN;
    private double _zoomRegionCursorY = double.NaN;
    private double _zoomRegionCenterX, _zoomRegionCenterY;
    private double _zoomRegionZoomLevel;
    private int _zoomRegionSourceW, _zoomRegionSourceH;
    private double _frameDisplayX, _frameDisplayY, _frameDisplayW, _frameDisplayH;
    private bool _isDraggingZoomRegion;
    private Point _dragStartPoint;
    private double _dragStartCenterX, _dragStartCenterY;

    private enum ZoomDragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private ZoomDragMode _zoomDragMode = ZoomDragMode.None;
    private const double HandleHitRadius = 10.0;
    private const double HandleSize = 10.0;
    private const double MinZoomLevel = 1.0;
    private const double MaxZoomLevel = 4.0;
    private const double CenterSnapThreshold = 0.02; // normalized (~2% of frame)
    private double _dragAnchorDispX, _dragAnchorDispY;
    private double _dragStartCenterDispX, _dragStartCenterDispY;
    private double _dragStartRectW, _dragStartRectH;
    private double _dragStartZoomLevel;

    private void EditZoomRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == selectedId);
        if (kf is null) return;
        EnterZoomRegionEditMode(selectedId, kf);
    }

    private void EnterZoomRegionEditMode(string keyframeId, ZoomKeyframe kf)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _zoomRegionEditMode = true;
        _zoomRegionKeyframeId = keyframeId;
        _zoomRegionSourceFile = kf.SourceVideoFilePath;
        _zoomRegionCenterX = kf.CenterX;
        _zoomRegionCenterY = kf.CenterY;
        _zoomRegionZoomLevel = kf.ZoomLevel;

        // A zoom centre is normalised 0..1 against the dimensions of the SOURCE it belongs
        // to, and appended recordings / imported videos can be sized differently from the
        // primary recording. Mapping the overlay against the primary's Width/Height would put
        // the rectangle in the wrong place on any differently-sized clip, so resolve the
        // owning segment (identified by the keyframe's SourceVideoFilePath) and use its
        // source dimensions. Falls back to the primary project size when the source cannot be
        // resolved (e.g. a keyframe on the primary recording, whose SourceVideoFilePath is null).
        var owning = SegmentForSource(kf.SourceVideoFilePath);
        _zoomRegionSourceW = owning is { SourceWidth: > 0 }
            ? owning.SourceWidth
            : (project.Width > 0 ? project.Width : 1920);
        _zoomRegionSourceH = owning is { SourceHeight: > 0 }
            ? owning.SourceHeight
            : (project.Height > 0 ? project.Height : 1080);

        // Pause playback and move to the segment for context.
        //
        // kf.Timestamp is a SOURCE time in the owning clip's own time space; the playhead runs
        // in OUTPUT time. Assigning one to the other lands the playhead wherever the arithmetic
        // happens to point — with a text slide ahead of the video it lands inside the SLIDE, so
        // the region editor showed the slide instead of the frame being framed.
        Preview.Pause();
        var pos = ResolveKeyframeOutputTime(kf);
        Timeline.PlayheadPosition = pos;
        Preview.PlayheadPosition = pos;
        ViewModel.Model.PlayheadPosition = pos;

        // Where the mouse actually is at this moment, so the region can be aimed at it on
        // the raw-frame fallback (a composed frame draws the cursor itself).
        ResolveZoomRegionCursorPoint(kf);

        // Re-render composed but held at rest: the picker has to frame the SAME image the
        // export produces — background, padding, aspect-ratio fit, cursor — while the zoom
        // being edited stays out of the way (PreviewRenderer.SuppressZoom, applied by the
        // render path now that _zoomRegionEditMode is set).
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(pos, force: true);

        ZoomRegionOverlay.Visibility = Visibility.Visible;

        // Only offer "follow mouse" when there is something to undo — a segment that already
        // follows the cursor has nothing to hand back. Null-guarded because handlers and setup
        // here can run before x:Name fields are assigned.
        if (ZoomRegionFollowMouseButton is not null)
            ZoomRegionFollowMouseButton.IsEnabled = kf.UsesAuthoredCenter;

        UpdateZoomRegionRect();
    }

    private void ExitZoomRegionEditMode()
    {
        _zoomRegionEditMode = false;
        _isDraggingZoomRegion = false;
        _zoomDragMode = ZoomDragMode.None;
        _zoomRegionKeyframeId = null;
        _zoomRegionSourceFile = null;
        _zoomRegionCursorX = double.NaN;
        _zoomRegionCursorY = double.NaN;
        if (ZoomRegionCursorMarker is not null) ZoomRegionCursorMarker.Visibility = Visibility.Collapsed;
        if (ZoomRegionCursorDot is not null) ZoomRegionCursorDot.Visibility = Visibility.Collapsed;
        ZoomRegionOverlay.Visibility = Visibility.Collapsed;
        UpdateSnapGuides(false, false);

        // Re-render with compositor
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    /// <summary>
    /// Maps a zoom keyframe's SOURCE time to its position on the OUTPUT timeline, routing
    /// through the video segment that owns it.
    /// <para>
    /// Zoom keyframes are stored in the source-time space of the clip they were authored
    /// against, which is not the timeline's time space as soon as anything shifts it — a text
    /// slide ahead of the video, a trim, a speed change, or an appended recording with its own
    /// source clock. This mirrors what the timeline control does for drawing zoom segments
    /// (<c>OwningSegmentForKeyframe</c>), so the playhead lands on the same frame the segment
    /// is drawn over.
    /// </para>
    /// </summary>
    private TimeSpan ResolveKeyframeOutputTime(ZoomKeyframe kf)
    {
        var model = ViewModel.Model;

        // The primary recording already has a tested mapping that walks timeline order and
        // handles slides, trims, reorders and speed changes — use it rather than a second
        // implementation that could drift from it.
        if (kf.SourceVideoFilePath is null)
            return model.SourceToOutputTime(kf.Timestamp);

        // An appended/imported clip has its own source clock, which the primary-only mapping
        // above does not cover, so resolve through the segment that owns this keyframe —
        // the same rule the timeline uses to draw it (OwningSegmentForKeyframe).
        VideoSegment? firstMatch = null;
        foreach (var seg in model.Segments.OfType<VideoSegment>())
        {
            if (!string.Equals(seg.VideoFilePath, kf.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            firstMatch ??= seg;

            var local = kf.Timestamp - seg.SourceStart;
            if (local >= TimeSpan.Zero && local <= seg.SourceDuration)
                return OutputTimeWithin(seg, local);
        }

        // Outside every kept range of its source (trimmed out): clamp into the first segment
        // cut from that file rather than leaving the playhead adrift.
        if (firstMatch is not null)
        {
            var local = kf.Timestamp - firstMatch.SourceStart;
            if (local < TimeSpan.Zero) local = TimeSpan.Zero;
            if (local > firstMatch.SourceDuration) local = firstMatch.SourceDuration;
            return OutputTimeWithin(firstMatch, local);
        }

        return kf.Timestamp;

        static TimeSpan OutputTimeWithin(VideoSegment seg, TimeSpan localSourceOffset)
        {
            double speed = seg.SpeedFactor > 0 ? seg.SpeedFactor : 1.0;
            return seg.Start + TimeSpan.FromTicks((long)(localSourceOffset.Ticks / speed));
        }
    }

    /// <summary>
    /// Resolves where the mouse is at a keyframe's moment, in source pixels, so the region
    /// editor can show it on the RAW-frame fallback (used only while the compositor is not
    /// ready — a composed frame draws the real cursor itself).
    /// Sets <see cref="_zoomRegionCursorX"/>/<see cref="_zoomRegionCursorY"/> to NaN when the
    /// source carries no cursor data — an imported clip, typically — and the marker stays hidden.
    /// </summary>
    private void ResolveZoomRegionCursorPoint(ZoomKeyframe kf)
    {
        _zoomRegionCursorX = double.NaN;
        _zoomRegionCursorY = double.NaN;

        // Only the primary recording's cursor path is available on the model; a keyframe on an
        // appended/imported clip has none to show.
        if (kf.SourceVideoFilePath is not null) return;
        if (ViewModel.Model.CursorData is not { } cursorData || cursorData.Samples.Count == 0) return;

        var project = ProjectService.Instance.CurrentProject;
        double mouseOffset = project?.MouseToVideoOffsetSeconds ?? 0;
        if (cursorData.FindSampleNearest(kf.Timestamp.TotalSeconds + mouseOffset) is not { } closest)
            return;

        // Hook coordinates are already physical pixels (PerMonitorV2) — no DPI scaling here.
        _zoomRegionCursorX = closest.X - (project?.CropOffsetX ?? 0);
        _zoomRegionCursorY = closest.Y - (project?.CropOffsetY ?? 0);
    }

    /// <summary>
    /// Places the cursor marker over the frame, using the same source→display transform the
    /// region rectangle uses so the two agree exactly. Only shown on the raw-frame fallback:
    /// a composed frame already draws the real cursor, and a second ring on top of it is
    /// exactly the kind of "picker shows one thing, preview another" mismatch this overlay
    /// exists to avoid.
    /// </summary>
    private void UpdateZoomRegionCursorMarker(bool composed)
    {
        if (ZoomRegionCursorMarker is null || ZoomRegionCursorDot is null) return;

        bool known = !composed
            && !double.IsNaN(_zoomRegionCursorX) && !double.IsNaN(_zoomRegionCursorY)
            && _zoomRegionSourceW > 0 && _zoomRegionSourceH > 0;
        if (!known)
        {
            ZoomRegionCursorMarker.Visibility = Visibility.Collapsed;
            ZoomRegionCursorDot.Visibility = Visibility.Collapsed;
            return;
        }

        double x = _frameDisplayX + (_zoomRegionCursorX / _zoomRegionSourceW) * _frameDisplayW;
        double y = _frameDisplayY + (_zoomRegionCursorY / _zoomRegionSourceH) * _frameDisplayH;

        Canvas.SetLeft(ZoomRegionCursorMarker, x - (ZoomRegionCursorMarker.Width / 2));
        Canvas.SetTop(ZoomRegionCursorMarker, y - (ZoomRegionCursorMarker.Height / 2));
        Canvas.SetLeft(ZoomRegionCursorDot, x - (ZoomRegionCursorDot.Width / 2));
        Canvas.SetTop(ZoomRegionCursorDot, y - (ZoomRegionCursorDot.Height / 2));

        ZoomRegionCursorMarker.Visibility = Visibility.Visible;
        ZoomRegionCursorDot.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Projects the SOURCE frame into canvas coordinates through the transform the
    /// compositor applies to it, so the region rectangle sits over the same pixels the
    /// composed preview (and the export) shows.
    /// <para>
    /// The result is where the WHOLE source frame maps, even the parts a
    /// <see cref="FitMode.Cover"/> crop leaves off screen — every downstream calculation
    /// here works in source-normalised coordinates, and a rect that means "the full source
    /// frame" keeps all of it valid.
    /// </para>
    /// Returns false while the composed geometry is unavailable (compositor still building,
    /// or the preview has not drawn a frame yet); the caller then fits the raw source frame
    /// to the canvas as before.
    /// </summary>
    private bool TryComputeComposedSourceLayout(out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;

        if (RendererForSource(_zoomRegionSourceFile) is not { } renderer) return false;
        if (_zoomRegionSourceW <= 0 || _zoomRegionSourceH <= 0) return false;

        // Where the composed frame is drawn inside the preview control. ZoomRegionCanvas
        // fills the same grid cell as the preview, so this needs no further translation
        // (the text-edit overlay maps through the same rect).
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0) return false;

        int outW = renderer.OutputWidth, outH = renderer.OutputHeight;
        var area = renderer.SourceAreaRect;        // source frame within the composed output
        var visible = renderer.RestSourceViewport; // source pixels that composed frame shows
        if (outW <= 0 || outH <= 0
            || area.Width <= 0 || area.Height <= 0
            || visible.Width <= 0 || visible.Height <= 0)
        {
            return false;
        }

        // output px → canvas px, then source px → canvas px (the visible source viewport is
        // drawn into the source-area rect).
        double outToCanvasX = layout.Width / outW;
        double outToCanvasY = layout.Height / outH;
        double scaleX = area.Width * outToCanvasX / visible.Width;
        double scaleY = area.Height * outToCanvasY / visible.Height;

        w = _zoomRegionSourceW * scaleX;
        h = _zoomRegionSourceH * scaleY;
        x = layout.X + (area.X * outToCanvasX) - (visible.X * scaleX);
        y = layout.Y + (area.Y * outToCanvasY) - (visible.Y * scaleY);
        return true;
    }

    private void UpdateZoomRegionRect()
    {
        if (!_zoomRegionEditMode) return;

        double canvasW = ZoomRegionCanvas.ActualWidth;
        double canvasH = ZoomRegionCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;
        if (_zoomRegionSourceW <= 0 || _zoomRegionSourceH <= 0) return;

        // Preferred: map through the compositor, so the picker and the render agree. The
        // fallback fits the raw source frame to the canvas, which is what the picker did
        // before the preview composed in edit mode.
        bool composed = TryComputeComposedSourceLayout(
            out _frameDisplayX, out _frameDisplayY, out _frameDisplayW, out _frameDisplayH);
        if (!composed)
        {
            double scale = Math.Min(canvasW / _zoomRegionSourceW, canvasH / _zoomRegionSourceH);
            _frameDisplayW = _zoomRegionSourceW * scale;
            _frameDisplayH = _zoomRegionSourceH * scale;
            _frameDisplayX = (canvasW - _frameDisplayW) / 2;
            _frameDisplayY = (canvasH - _frameDisplayH) / 2;
        }

        // The region this zoom will actually show, in canvas coordinates. Asking the
        // compositor for it keeps the zoom scope, aspect-ratio-fit crop, crop anchor and
        // bounds clamping identical to what gets rendered instead of re-deriving them beside
        // it, which is how the rectangle and the rendered frame drifted apart.
        double rectX, rectY, rectW, rectH;
        var renderer = RendererForSource(_zoomRegionSourceFile);
        var outputRect = composed
            ? renderer?.ComputeRegionOutputRect(
                (float)_zoomRegionZoomLevel, (float)_zoomRegionCenterX, (float)_zoomRegionCenterY)
            : null;

        if (outputRect is { Width: > 0, Height: > 0 } outRect && renderer is { OutputWidth: > 0, OutputHeight: > 0 })
        {
            var layout = Preview.FrameLayoutRect;
            double outToCanvasX = layout.Width / renderer.OutputWidth;
            double outToCanvasY = layout.Height / renderer.OutputHeight;

            rectX = layout.X + outRect.X * outToCanvasX;
            rectY = layout.Y + outRect.Y * outToCanvasY;
            rectW = outRect.Width * outToCanvasX;
            rectH = outRect.Height * outToCanvasY;
        }
        else
        {
            // Raw-frame fallback: derive the source viewport here, as the picker did before
            // the preview composed in edit mode.
            double vpW = _zoomRegionSourceW / _zoomRegionZoomLevel;
            double vpH = _zoomRegionSourceH / _zoomRegionZoomLevel;

            // Adjust for output aspect ratio if set
            var config = ProjectService.Instance.CurrentComposition;
            if (config is not null && config.AspectRatio != AspectRatio.Auto)
            {
                double contentRatio = GetAspectRatioValue(config.AspectRatio);
                if (contentRatio > 0)
                {
                    double vpRatio = vpW / vpH;
                    if (vpRatio > contentRatio)
                        vpW = vpH * contentRatio;
                    else
                        vpH = vpW / contentRatio;
                }
            }

            double vpX = _zoomRegionCenterX * _zoomRegionSourceW - vpW / 2;
            double vpY = _zoomRegionCenterY * _zoomRegionSourceH - vpH / 2;

            // Clamp viewport to source bounds
            vpX = Math.Clamp(vpX, 0, Math.Max(0, _zoomRegionSourceW - vpW));
            vpY = Math.Clamp(vpY, 0, Math.Max(0, _zoomRegionSourceH - vpH));

            // Map to display coordinates
            rectX = _frameDisplayX + (vpX / _zoomRegionSourceW) * _frameDisplayW;
            rectY = _frameDisplayY + (vpY / _zoomRegionSourceH) * _frameDisplayH;
            rectW = (vpW / _zoomRegionSourceW) * _frameDisplayW;
            rectH = (vpH / _zoomRegionSourceH) * _frameDisplayH;
        }

        Canvas.SetLeft(ZoomRegionRect, rectX);
        Canvas.SetTop(ZoomRegionRect, rectY);
        ZoomRegionRect.Width = rectW;
        ZoomRegionRect.Height = rectH;

        // Position corner handles centered on each corner
        PositionHandle(HandleTL, rectX, rectY);
        PositionHandle(HandleTR, rectX + rectW, rectY);
        PositionHandle(HandleBL, rectX, rectY + rectH);
        PositionHandle(HandleBR, rectX + rectW, rectY + rectH);

        UpdateZoomRegionCursorMarker(composed);

        // Dim everything the zoom will crop away. That area is scope-dependent: a frame zoom
        // magnifies the whole canvas, so its background and padding go too, while a source
        // zoom leaves them at a fixed size — dimming them there would claim they get cropped
        // when they survive untouched. The compositor owns that distinction (RegionCanvasRect).
        // On the raw fallback it stays the frame rect, as before.
        double fX, fY, fW, fH;
        var regionCanvas = composed ? renderer?.RegionCanvasRect : null;
        if (composed && regionCanvas is { Width: > 0, Height: > 0 } canvasRect
            && renderer is { OutputWidth: > 0, OutputHeight: > 0 })
        {
            var layout = Preview.FrameLayoutRect;
            double outToCanvasX = layout.Width / renderer.OutputWidth;
            double outToCanvasY = layout.Height / renderer.OutputHeight;

            fX = layout.X + canvasRect.X * outToCanvasX;
            fY = layout.Y + canvasRect.Y * outToCanvasY;
            fW = canvasRect.Width * outToCanvasX;
            fH = canvasRect.Height * outToCanvasY;
        }
        else
        {
            fX = _frameDisplayX; fY = _frameDisplayY;
            fW = _frameDisplayW; fH = _frameDisplayH;
        }

        Canvas.SetLeft(DimTop, fX);
        Canvas.SetTop(DimTop, fY);
        DimTop.Width = fW;
        DimTop.Height = Math.Max(0, rectY - fY);

        Canvas.SetLeft(DimBottom, fX);
        Canvas.SetTop(DimBottom, rectY + rectH);
        DimBottom.Width = fW;
        DimBottom.Height = Math.Max(0, (fY + fH) - (rectY + rectH));

        Canvas.SetLeft(DimLeft, fX);
        Canvas.SetTop(DimLeft, rectY);
        DimLeft.Width = Math.Max(0, rectX - fX);
        DimLeft.Height = rectH;

        Canvas.SetLeft(DimRight, rectX + rectW);
        Canvas.SetTop(DimRight, rectY);
        DimRight.Width = Math.Max(0, (fX + fW) - (rectX + rectW));
        DimRight.Height = rectH;
    }

    private static double GetAspectRatioValue(AspectRatio ratio)
    {
        var (w, h) = AspectRatioHelper.GetRatio(ratio);
        return (w == 0 || h == 0) ? -1.0 : (double)w / h;
    }

    /// <summary>
    /// The range the region's normalised centre may occupy at a given zoom. Comes from the
    /// compositor, which clamps in output space under the default frame zoom scope — the
    /// background around the source area is slack the centre can spend, so half-a-viewport-in
    /// from each edge (the fallback below) is stricter than what actually renders.
    /// </summary>
    private (double MinX, double MaxX, double MinY, double MaxY) GetCenterBounds(double zoom)
    {
        if (RendererForSource(_zoomRegionSourceFile)?.ComputeRegionCenterBounds((float)zoom)
            is { } bounds && bounds.MaxX >= bounds.MinX && bounds.MaxY >= bounds.MinY)
        {
            return bounds;
        }

        (double halfW, double halfH) = GetNormalizedHalfExtents(zoom);
        return (halfW, 1.0 - halfW, halfH, 1.0 - halfH);
    }

    /// <summary>
    /// No-compositor fallback for <see cref="GetCenterBounds"/>: half a viewport in from each
    /// source edge, aspect-ratio fit included. Deliberately does NOT consult a renderer — the
    /// only caller reaches here precisely because there is no live compositor to ask, and a
    /// source-space extent is not the same box as the drawn (scope-aware) rectangle.
    /// </summary>
    private (double halfW, double halfH) GetNormalizedHalfExtents(double zoom)
    {
        double vpW = _zoomRegionSourceW / zoom;
        double vpH = _zoomRegionSourceH / zoom;

        var config = ProjectService.Instance.CurrentComposition;
        if (config is not null && config.AspectRatio != AspectRatio.Auto)
        {
            double contentRatio = GetAspectRatioValue(config.AspectRatio);
            if (contentRatio > 0)
            {
                double vpRatio = vpW / vpH;
                if (vpRatio > contentRatio) vpW = vpH * contentRatio;
                else vpH = vpW / contentRatio;
            }
        }
        return (vpW / _zoomRegionSourceW / 2.0, vpH / _zoomRegionSourceH / 2.0);
    }

    private static void PositionHandle(Microsoft.UI.Xaml.Shapes.Rectangle handle, double cornerX, double cornerY)
    {
        Canvas.SetLeft(handle, cornerX - HandleSize / 2);
        Canvas.SetTop(handle, cornerY - HandleSize / 2);
    }

    private ZoomDragMode HitTestCorners(Point p)
    {
        double rectX = Canvas.GetLeft(ZoomRegionRect);
        double rectY = Canvas.GetTop(ZoomRegionRect);
        double rectR = rectX + ZoomRegionRect.Width;
        double rectB = rectY + ZoomRegionRect.Height;

        if (IsNear(p, rectX, rectY)) return ZoomDragMode.ResizeTL;
        if (IsNear(p, rectR, rectY)) return ZoomDragMode.ResizeTR;
        if (IsNear(p, rectX, rectB)) return ZoomDragMode.ResizeBL;
        if (IsNear(p, rectR, rectB)) return ZoomDragMode.ResizeBR;
        return ZoomDragMode.None;

        static bool IsNear(Point pt, double cx, double cy)
            => System.Math.Abs(pt.X - cx) <= HandleHitRadius && System.Math.Abs(pt.Y - cy) <= HandleHitRadius;
    }

    /// <summary>
    /// Alt state for a pointer gesture: hold it while dragging a corner to resize the region
    /// around its own centre instead of the opposite corner.
    /// <para>
    /// <see cref="PointerRoutedEventArgs.KeyModifiers"/> is the primary source, with a
    /// keyboard-state fallback (the pattern <c>RegionSelectorOverlay.IsShiftHeld</c> uses)
    /// because Alt is routed to menu handling and does not always survive into the pointer
    /// message's modifiers.
    /// </para>
    /// </summary>
    private static bool IsAltHeld(PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Menu) == VirtualKeyModifiers.Menu)
            return true;

        return Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void ZoomRegionCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_zoomRegionEditMode) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        double rectX = Canvas.GetLeft(ZoomRegionRect);
        double rectY = Canvas.GetTop(ZoomRegionRect);
        double rectR = rectX + ZoomRegionRect.Width;
        double rectB = rectY + ZoomRegionRect.Height;

        var corner = HitTestCorners(point.Position);
        if (corner != ZoomDragMode.None)
        {
            _zoomDragMode = corner;
            _isDraggingZoomRegion = true;
            _dragStartPoint = point.Position;
            _dragStartCenterX = _zoomRegionCenterX;
            _dragStartCenterY = _zoomRegionCenterY;
            _dragStartRectW = ZoomRegionRect.Width;
            _dragStartRectH = ZoomRegionRect.Height;
            _dragStartZoomLevel = _zoomRegionZoomLevel;
            // Anchor is the opposite corner in display coords
            _dragAnchorDispX = corner is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeBL ? rectR : rectX;
            _dragAnchorDispY = corner is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeTR ? rectB : rectY;
            // The rect's own centre, which an Alt-drag resizes around instead.
            _dragStartCenterDispX = rectX + ZoomRegionRect.Width / 2.0;
            _dragStartCenterDispY = rectY + ZoomRegionRect.Height / 2.0;
            ZoomRegionCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (point.Position.X >= rectX && point.Position.X <= rectR &&
            point.Position.Y >= rectY && point.Position.Y <= rectB)
        {
            _zoomDragMode = ZoomDragMode.Move;
            _isDraggingZoomRegion = true;
            _dragStartPoint = point.Position;
            _dragStartCenterX = _zoomRegionCenterX;
            _dragStartCenterY = _zoomRegionCenterY;
            ZoomRegionCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void ZoomRegionCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingZoomRegion) return;
        if (_frameDisplayW <= 0 || _frameDisplayH <= 0) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) == VirtualKeyModifiers.Shift;

        if (_zoomDragMode == ZoomDragMode.Move)
        {
            double deltaX = point.Position.X - _dragStartPoint.X;
            double deltaY = point.Position.Y - _dragStartPoint.Y;

            double deltaNormX = deltaX / _frameDisplayW;
            double deltaNormY = deltaY / _frameDisplayH;

            (double minX, double maxX, double minY, double maxY) = GetCenterBounds(_zoomRegionZoomLevel);

            double newCx = System.Math.Clamp(_dragStartCenterX + deltaNormX, minX, maxX);
            double newCy = System.Math.Clamp(_dragStartCenterY + deltaNormY, minY, maxY);

            bool snappedX = false, snappedY = false;
            if (!shift)
            {
                if (System.Math.Abs(newCx - 0.5) < CenterSnapThreshold) { newCx = 0.5; snappedX = true; }
                if (System.Math.Abs(newCy - 0.5) < CenterSnapThreshold) { newCy = 0.5; snappedY = true; }
            }

            _zoomRegionCenterX = newCx;
            _zoomRegionCenterY = newCy;
            UpdateZoomRegionRect();
            UpdateSnapGuides(snappedX, snappedY);
        }
        else if (_zoomDragMode is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeTR or ZoomDragMode.ResizeBL or ZoomDragMode.ResizeBR)
        {
            int signX = _zoomDragMode is ZoomDragMode.ResizeTR or ZoomDragMode.ResizeBR ? 1 : -1;
            int signY = _zoomDragMode is ZoomDragMode.ResizeBL or ZoomDragMode.ResizeBR ? 1 : -1;

            // Alt resizes around the rect's centre instead of pinning the opposite corner, so
            // the framing stays put and only the zoom level changes. Measuring from the centre
            // over HALF the diagonal is what makes the pointer track the dragged corner in both
            // modes: at 1:1 the corner is half a diagonal from the centre, a full one from the
            // opposite corner.
            bool symmetric = IsAltHeld(e);
            double originX = symmetric ? _dragStartCenterDispX : _dragAnchorDispX;
            double originY = symmetric ? _dragStartCenterDispY : _dragAnchorDispY;
            double diagX = signX * _dragStartRectW / (symmetric ? 2.0 : 1.0);
            double diagY = signY * _dragStartRectH / (symmetric ? 2.0 : 1.0);

            double dispDx = point.Position.X - originX;
            double dispDy = point.Position.Y - originY;

            // Project pointer-from-origin onto the rect's diagonal direction
            // (signX*W0, signY*H0). This makes resize feel natural in both
            // outward and inward directions while preserving aspect.
            double diagLenSq = diagX * diagX + diagY * diagY;
            if (diagLenSq <= 0) { e.Handled = true; return; }
            double projLen = (dispDx * diagX + dispDy * diagY) / System.Math.Sqrt(diagLenSq);
            double diagLen = System.Math.Sqrt(diagLenSq);
            double scale = projLen / diagLen;
            if (scale < 0.0001) scale = 0.0001;

            double newZoom = System.Math.Clamp(_dragStartZoomLevel / scale, MinZoomLevel, MaxZoomLevel);
            double effectiveScale = _dragStartZoomLevel / newZoom;

            double newRectW = _dragStartRectW * effectiveScale;
            double newRectH = _dragStartRectH * effectiveScale;

            bool snappedX = false, snappedY = false;
            double newCx, newCy;

            if (symmetric)
            {
                // Keep the centre exactly as it was — deriving it back from display
                // coordinates would let rounding (and any edge clamping already folded into
                // the drawn rect) nudge the framing the user is holding still. No centre
                // snapping either: nothing is moving to snap.
                newCx = _dragStartCenterX;
                newCy = _dragStartCenterY;
            }
            else
            {
                double centerDispX = _dragAnchorDispX + signX * newRectW / 2.0;
                double centerDispY = _dragAnchorDispY + signY * newRectH / 2.0;

                newCx = (centerDispX - _frameDisplayX) / _frameDisplayW;
                newCy = (centerDispY - _frameDisplayY) / _frameDisplayH;

                if (!shift)
                {
                    if (System.Math.Abs(newCx - 0.5) < CenterSnapThreshold) { newCx = 0.5; snappedX = true; }
                    if (System.Math.Abs(newCy - 0.5) < CenterSnapThreshold) { newCy = 0.5; snappedY = true; }
                }
            }

            (double rMinX, double rMaxX, double rMinY, double rMaxY) = GetCenterBounds(newZoom);
            newCx = System.Math.Clamp(newCx, rMinX, rMaxX);
            newCy = System.Math.Clamp(newCy, rMinY, rMaxY);

            _zoomRegionZoomLevel = newZoom;
            _zoomRegionCenterX = newCx;
            _zoomRegionCenterY = newCy;
            UpdateZoomRegionRect();
            SyncZoomLevelComboToFreeform(newZoom);
            UpdateSnapGuides(snappedX, snappedY);
        }

        e.Handled = true;
    }

    private void ZoomRegionCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingZoomRegion)
        {
            _isDraggingZoomRegion = false;
            _zoomDragMode = ZoomDragMode.None;
            ZoomRegionCanvas.ReleasePointerCapture(e.Pointer);
            UpdateSnapGuides(false, false);
            e.Handled = true;
        }
    }

    private void SyncZoomLevelComboToFreeform(double zoom)
    {
        string text = zoom.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        if (ZoomLevelCombo.Text == text) return;
        using (SuppressScope.Enter(ref _suppressZoomPropertyUpdate))
            ZoomLevelCombo.Text = text;
    }

    private void UpdateSnapGuides(bool snappedX, bool snappedY)
    {
        if (snappedX)
        {
            double cx = _frameDisplayX + _frameDisplayW / 2.0;
            SnapGuideV.X1 = cx; SnapGuideV.X2 = cx;
            SnapGuideV.Y1 = _frameDisplayY; SnapGuideV.Y2 = _frameDisplayY + _frameDisplayH;
            SnapGuideV.Visibility = Visibility.Visible;
        }
        else
        {
            SnapGuideV.Visibility = Visibility.Collapsed;
        }

        if (snappedY)
        {
            double cy = _frameDisplayY + _frameDisplayH / 2.0;
            SnapGuideH.Y1 = cy; SnapGuideH.Y2 = cy;
            SnapGuideH.X1 = _frameDisplayX; SnapGuideH.X2 = _frameDisplayX + _frameDisplayW;
            SnapGuideH.Visibility = Visibility.Visible;
        }
        else
        {
            SnapGuideH.Visibility = Visibility.Collapsed;
        }
    }

    private void ZoomRegionCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomRegionEditMode)
            UpdateZoomRegionRect();
    }

    private void ZoomRegionApply_Click(object sender, RoutedEventArgs e)
    {
        if (_zoomRegionKeyframeId is null) return;

        var operation = new UpdateZoomSegmentPropertiesOperation(
            _zoomRegionKeyframeId,
            zoomLevel: _zoomRegionZoomLevel,
            centerX: _zoomRegionCenterX,
            centerY: _zoomRegionCenterY);
        ViewModel.UndoRedoManager.Execute(operation);

        ExitZoomRegionEditMode();
    }

    private void ZoomRegionCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitZoomRegionEditMode();
    }

    /// <summary>
    /// Hands the framing back to the cursor: the segment stops holding the region shown here
    /// and follows the mouse again, which is what a click-driven zoom does by default.
    /// <para>
    /// The zoom LEVEL the region rectangle currently expresses is kept, since that is a
    /// separate decision the user may well have just made here. No centre is passed —
    /// supplying one would be read as authoring a region and would immediately re-pin it.
    /// </para>
    /// </summary>
    private void ZoomRegionFollowMouse_Click(object sender, RoutedEventArgs e)
    {
        if (_zoomRegionKeyframeId is null) return;

        var operation = new UpdateZoomSegmentPropertiesOperation(
            _zoomRegionKeyframeId,
            zoomLevel: _zoomRegionZoomLevel,
            hasAuthoredCenter: false);
        ViewModel.UndoRedoManager.Execute(operation);

        ExitZoomRegionEditMode();
    }

    private void UpdateZoomPanelVisibility()
    {
        if (Timeline is null || ZoomSegmentPanel is null) return;
        bool hasSelection = Timeline.SelectedZoomKeyframeId is not null;
        ZoomSegmentPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static float GetDpiScale(int capturedDimension, bool isWidth = true)
    {
        try
        {
            int logical = GetSystemMetrics(isWidth ? 0 : 1); // SM_CXSCREEN=0, SM_CYSCREEN=1
            if (logical > 0 && capturedDimension > logical)
                return (float)capturedDimension / logical;
        }
        catch { }
        return 1.0f;
    }
    private BackgroundStyle? _primaryRenderBackground;
    private CursorStyle? _primaryRenderCursor;

    private bool _suppressWebcamEvents;

    // ─── Text Slide & Append Recording handlers ─────────────────────────

    private string? _selectedTextSlideId;

    // ─── Project package (.musio) save / open ───────────────────────────

    private async void SaveProject_Click(SplitButton sender, SplitButtonClickEventArgs args)
        => await SaveProjectAsync(forcePrompt: false);

    private async void SaveProjectAs_Click(object sender, RoutedEventArgs e)
        => await SaveProjectAsync(forcePrompt: true);

    private bool _isSavingProject;

    /// <summary>
    /// Saves the current project, writing straight back to the file it came from unless
    /// <paramref name="forcePrompt"/> is set or it has never been saved.
    /// </summary>
    private async Task SaveProjectAsync(bool forcePrompt)
    {
        if (_isSavingProject || ProjectService.Instance.IsSaveInFlight)
            return;

        _isSavingProject = true;
        SaveProjectButton.IsEnabled = false;
        try
        {
            var project = ProjectService.Instance.CurrentProject;
            if (project is null)
            {
                await ShowProjectDialogAsync("Nothing to save", "Record or open a project first.");
                return;
            }

            var targetPath = forcePrompt ? null : ProjectService.Instance.CurrentPackagePath;

            if (targetPath is null)
            {
                targetPath = await PickSavePathAsync(project.Name);
                if (targetPath is null) return;
            }

            await ProjectService.Instance.SavePackageAsync(targetPath);
            ShowSaveConfirmation(targetPath);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Project save failed: {ex}");
            await ShowProjectDialogAsync("Could not save project", ex.Message);
        }
        finally
        {
            _isSavingProject = false;
            SaveProjectButton.IsEnabled = true;
        }
    }

    private async Task<string?> PickSavePathAsync(string projectName)
    {
        try
        {
            // Shared with the save-before-close prompt so the two cannot drift on file type,
            // default location or name sanitisation.
            return await ProjectSaveCoordinator.PickSavePathAsync(projectName, App.Current.MainAppWindow);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Save picker failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Confirms a save without a modal dialog: saving over an existing project is a
    /// routine action and should not need dismissing.
    /// </summary>
    private void ShowSaveConfirmation(string path)
    {
        if (SavedFlyoutText is not null)
            SavedFlyoutText.Text = System.IO.Path.GetFileName(path);
        SavedFlyout?.ShowAt(SaveProjectButton);
    }

    private async Task ShowProjectDialogAsync(string title, string message)
    {
        try
        {
            await DialogHelper.ShowInfoAsync(XamlRoot, title, message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] Dialog failed: {ex.Message}");
        }
    }
}
