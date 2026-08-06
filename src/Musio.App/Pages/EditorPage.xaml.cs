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
    private bool _syncingTimelineFromPlayback;
    private CanvasDevice? _graphicsDevice;
    private int _graphicsRecoveryQueued;
    private int _graphicsRecoveryRequested;
    private bool _graphicsRecoveryInProgress;
    private bool _pageUnloaded;

    // Background style editing state
    private DispatcherTimer? _styleDebounceTimer;

    // Motion (motion blur / camera drift) editing state — separate debounce timer so a
    // slider drag on these controls doesn't interact with the background-style debounce.
    private DispatcherTimer? _motionDebounceTimer;

    // Cursor style editing state
    private DispatcherTimer? _cursorDebounceTimer;

    // Text overlay editing state — separate debounce timer so a text-box keystroke never
    // interacts with the background-style / motion / cursor debounces above. The model is
    // still committed (through UndoRedoManager, for undo) on every keystroke; only the
    // (expensive) preview re-render is debounced.
    private DispatcherTimer? _overlayPreviewDebounceTimer;
    private bool _hasWebcamOverlay;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        ExportVM = new ExportViewModel();
        InitializeComponent();
        AttachGraphicsDevice();
        Loaded += (_, _) =>
        {
            _pageUnloaded = false;
            AttachGraphicsDevice();
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
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            if (isPlaying)
            {
                // Seeks, starts, or leaves it paused if the playhead is somewhere with no
                // audio behind it (a title slide, say) — PlaybackTick picks it up on entry.
                SyncAudioToPlayhead(Preview.PlayheadPosition);
            }
            else
            {
                _audioPlayer.Pause();
            }
        };

        // Re-seek audio when playback loops
        Preview.PlaybackLooped += (_, _) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            SyncAudioToPlayhead(TimeSpan.Zero);
        };

        ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;

        // Audio mute: toggle playback and sync mute state for export
        Timeline.SystemAudioMuteChanged += (_, isMuted) =>
        {
            ReloadAudioPlayer();
        };
        Timeline.MicAudioMuteChanged += (_, isMuted) =>
        {
            ReloadAudioPlayer();
        };

        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Assigned explicitly as well as bound: the timeline must never keep
                // rendering a model the project has moved on from.
                Timeline.Model = ViewModel.Model;
                _timelineMapper = null;
                Timeline.ClearZoomSelection();
                Timeline.ClearClipSelection();
                Timeline.ClearTransitionSelection();
                UpdateSpeedPanelVisibility();
                Preview.Duration = GetMappedDuration();
                Timeline.Refresh();
                ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
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
            DetachGraphicsDevice();
            _styleDebounceTimer?.Stop();
            _styleDebounceTimer = null;
            _cursorDebounceTimer?.Stop();
            _cursorDebounceTimer = null;
            _motionDebounceTimer?.Stop();
            _motionDebounceTimer = null;
            _overlayPreviewDebounceTimer?.Stop();
            _overlayPreviewDebounceTimer = null;

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

    private void AttachGraphicsDevice()
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            if (ReferenceEquals(_graphicsDevice, device))
                return;

            DetachGraphicsDevice();
            _graphicsDevice = device;
            _graphicsDevice.DeviceLost += OnGraphicsDeviceLost;
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write(
                "Editor", $"failed to attach graphics-device recovery: {ex.Message}");
        }
    }

    private void DetachGraphicsDevice()
    {
        if (_graphicsDevice is null)
            return;

        _graphicsDevice.DeviceLost -= OnGraphicsDeviceLost;
        _graphicsDevice = null;
    }

    private void OnGraphicsDeviceLost(CanvasDevice sender, object args)
    {
        Musio.Core.Diagnostics.DiagLog.Write(
            "Editor", "shared CanvasDevice lost; scheduling editor graphics recovery");

        Interlocked.Exchange(ref _graphicsRecoveryRequested, 1);
        if (Interlocked.Exchange(ref _graphicsRecoveryQueued, 1) != 0)
            return;

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    do
                    {
                        Interlocked.Exchange(ref _graphicsRecoveryRequested, 0);
                        await RecoverGraphicsDeviceAsync();
                    }
                    while (!_pageUnloaded
                        && Interlocked.CompareExchange(
                            ref _graphicsRecoveryRequested, 0, 0) != 0);
                }
                catch (Exception ex)
                {
                    Musio.Core.Diagnostics.DiagLog.Write(
                        "Editor", $"graphics recovery failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _graphicsRecoveryQueued, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _graphicsRecoveryQueued, 0);
        }
    }

    private async Task RecoverGraphicsDeviceAsync()
    {
        if (_pageUnloaded || _graphicsRecoveryInProgress)
            return;

        _graphicsRecoveryInProgress = true;
        TimeSpan position = Preview.PlayheadPosition;
        try
        {
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

            DetachGraphicsDevice();
            AttachGraphicsDevice();
            await InitializePreviewCoreAsync();
            position = Preview.PlayheadPosition;
            _pendingRenderPosition = null;
            _pendingRenderForce = false;
        }
        finally
        {
            _graphicsRecoveryInProgress = false;
        }

        if (_pageUnloaded)
            return;

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

    private bool _suppressSpeedApply;

    /// <summary>Id of the currently selected primary-track segment (video or text slide).</summary>
    private string? _selectedPrimarySegmentId;

    // ── Text overlay track handlers ──

    private string? _selectedTextOverlayId;

    // ─── Transition boundary panel ──────────────────────────────────────

    /// <summary>Incoming segment Id of the currently-selected boundary chip, or null.</summary>
    private string? _selectedTransitionId;

    private void UpdateSpeedPanelVisibility()
    {
        if (SpeedComboBox is null) return;
        bool hasClipSelection = ViewModel.SelectedClipIndex is not null;
        SpeedComboBox.Visibility = hasClipSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSpeedApply) return;
        if (SpeedComboBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double speed))
        {
            ViewModel.SelectedSpeed = speed;

            if (ViewModel.SelectedClipIndex is not null)
            {
                ViewModel.ApplySpeedCommand.Execute(null);
            }
        }
    }

    // --- Zoom Segment Handlers ---

    private bool _suppressZoomPropertyUpdate;

    // --- Zoom Region Edit Mode ---

    private bool _zoomRegionEditMode;
    private string? _zoomRegionKeyframeId;
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

        // Pause playback and move to segment timestamp for context
        Preview.Pause();
        var pos = kf.Timestamp;
        Timeline.PlayheadPosition = pos;
        Preview.PlayheadPosition = pos;
        ViewModel.Model.PlayheadPosition = pos;

        // Re-render without compositor (raw frame for positioning)
        _ = UpdatePreviewFrameAsync(pos, force: true);

        ZoomRegionOverlay.Visibility = Visibility.Visible;
        UpdateZoomRegionRect();
    }

    private void ExitZoomRegionEditMode()
    {
        _zoomRegionEditMode = false;
        _isDraggingZoomRegion = false;
        _zoomDragMode = ZoomDragMode.None;
        _zoomRegionKeyframeId = null;
        ZoomRegionOverlay.Visibility = Visibility.Collapsed;
        UpdateSnapGuides(false, false);

        // Re-render with compositor
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void UpdateZoomRegionRect()
    {
        if (!_zoomRegionEditMode) return;

        double canvasW = ZoomRegionCanvas.ActualWidth;
        double canvasH = ZoomRegionCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        // Compute raw frame display rect (aspect-fit centered)
        double scale = Math.Min(canvasW / _zoomRegionSourceW, canvasH / _zoomRegionSourceH);
        _frameDisplayW = _zoomRegionSourceW * scale;
        _frameDisplayH = _zoomRegionSourceH * scale;
        _frameDisplayX = (canvasW - _frameDisplayW) / 2;
        _frameDisplayY = (canvasH - _frameDisplayH) / 2;

        // Compute zoom viewport in source pixels
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
        double rectX = _frameDisplayX + (vpX / _zoomRegionSourceW) * _frameDisplayW;
        double rectY = _frameDisplayY + (vpY / _zoomRegionSourceH) * _frameDisplayH;
        double rectW = (vpW / _zoomRegionSourceW) * _frameDisplayW;
        double rectH = (vpH / _zoomRegionSourceH) * _frameDisplayH;

        Canvas.SetLeft(ZoomRegionRect, rectX);
        Canvas.SetTop(ZoomRegionRect, rectY);
        ZoomRegionRect.Width = rectW;
        ZoomRegionRect.Height = rectH;

        // Position corner handles centered on each corner
        PositionHandle(HandleTL, rectX, rectY);
        PositionHandle(HandleTR, rectX + rectW, rectY);
        PositionHandle(HandleBL, rectX, rectY + rectH);
        PositionHandle(HandleBR, rectX + rectW, rectY + rectH);

        // Dim overlays constrained to frame display area
        double fX = _frameDisplayX, fY = _frameDisplayY;
        double fW = _frameDisplayW, fH = _frameDisplayH;

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

            (double halfW, double halfH) = GetNormalizedHalfExtents(_zoomRegionZoomLevel);

            double newCx = System.Math.Clamp(_dragStartCenterX + deltaNormX, halfW, 1.0 - halfW);
            double newCy = System.Math.Clamp(_dragStartCenterY + deltaNormY, halfH, 1.0 - halfH);

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
            double dispDx = point.Position.X - _dragAnchorDispX;
            double dispDy = point.Position.Y - _dragAnchorDispY;

            int signX = _zoomDragMode is ZoomDragMode.ResizeTR or ZoomDragMode.ResizeBR ? 1 : -1;
            int signY = _zoomDragMode is ZoomDragMode.ResizeBL or ZoomDragMode.ResizeBR ? 1 : -1;

            // Project pointer-from-anchor onto the rect's diagonal direction
            // (signX*W0, signY*H0). This makes resize feel natural in both
            // outward and inward directions while preserving aspect.
            double diagX = signX * _dragStartRectW;
            double diagY = signY * _dragStartRectH;
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

            double centerDispX = _dragAnchorDispX + signX * newRectW / 2.0;
            double centerDispY = _dragAnchorDispY + signY * newRectH / 2.0;

            double newCx = (centerDispX - _frameDisplayX) / _frameDisplayW;
            double newCy = (centerDispY - _frameDisplayY) / _frameDisplayH;

            (double rHalfW, double rHalfH) = GetNormalizedHalfExtents(newZoom);
            newCx = System.Math.Clamp(newCx, rHalfW, 1.0 - rHalfW);
            newCy = System.Math.Clamp(newCy, rHalfH, 1.0 - rHalfH);

            bool snappedX = false, snappedY = false;
            if (!shift)
            {
                if (System.Math.Abs(newCx - 0.5) < CenterSnapThreshold) { newCx = 0.5; snappedX = true; }
                if (System.Math.Abs(newCy - 0.5) < CenterSnapThreshold) { newCy = 0.5; snappedY = true; }
            }

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
        _suppressZoomPropertyUpdate = true;
        try { ZoomLevelCombo.Text = text; }
        finally { _suppressZoomPropertyUpdate = false; }
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
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            SuggestedFileName = SanitizeFileName(projectName),
        };
        picker.FileTypeChoices.Add("Musio project", [MusioPackage.FileExtension]);

        var window = App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        try
        {
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
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
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] Dialog failed: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Musio project";

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        return name;
    }
}
