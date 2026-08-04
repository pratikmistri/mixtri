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

    private sealed class SegmentPreview : IDisposable
    {
        public VideoFrameReader? Reader;
        public PreviewRenderer? Renderer;
        public bool Ready;
        public long LastUsed;
        public Windows.Media.Editing.MediaComposition? Webcam;
        public int WebcamW, WebcamH;
        public CanvasBitmap? LastWebcamFrame;

        public void Dispose()
        {
            TryDispose(Reader);
            TryDispose(Renderer);
            try { Webcam?.Clips.Clear(); } catch { }
            TryDispose(LastWebcamFrame);
        }
    }

    /// <summary>
    /// Cache generation for <see cref="_segmentPreviews"/>. A build awaits a multi-second
    /// decoder open, during which any of the clear sites below can run — disposing the
    /// entry that build is still populating and then dropping it from the dictionary. The
    /// build would go on to assign a live decoder and compositor onto that orphan, which
    /// nothing would ever dispose. Each build captures this value and abandons once it
    /// moves on, the same way <see cref="_previewInitGeneration"/> guards the primary.
    /// </summary>
    private int _segmentPreviewGeneration;
    private long _segmentPreviewUseCounter;

    private const int MaxCachedSegmentPreviews = 2;
    private const long PrimaryPreviewCacheBudgetBytes = 96L * 1024 * 1024;
    private const long SegmentPreviewCacheBudgetBytes = 32L * 1024 * 1024;

    /// <summary>
    /// One alternate-style compositor for a PRIMARY-file segment, cached by its EFFECTIVE
    /// (<see cref="BackgroundStyle"/>, <see cref="CursorStyle"/>) pair. Exists solely so
    /// composing a transition's two sides (see
    /// <see cref="GetPrimaryTransitionCompositorAsync"/>) never mutates the singleton
    /// <see cref="_previewRenderer"/> that ordinary playback owns — see that method's
    /// remarks for the alternation-freeze this replaces.
    /// </summary>
    private sealed class PrimaryStyleRenderer : IDisposable
    {
        public required PreviewRenderer Renderer { get; init; }
        public long LastUsed;
        public void Dispose() => TryDispose(Renderer);
    }

    private readonly Dictionary<(BackgroundStyle Background, CursorStyle Cursor), PrimaryStyleRenderer>
        _primaryStyleRenderers = new();
    private long _primaryStyleRendererUseCounter;
    private const int MaxCachedPrimaryStyleRenderers = 3;

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

    /// <summary>
    /// Disposes and clears every cached primary alternate-style compositor.
    /// </summary>
    private void DisposePrimaryStyleRenderers()
    {
        _primaryPreviewStateGeneration++;
        foreach (var entry in _primaryStyleRenderers.Values)
            entry.Dispose();
        _primaryStyleRenderers.Clear();
    }

    private void TrimPrimaryStyleRendererCache()
    {
        while (_primaryStyleRenderers.Count >= MaxCachedPrimaryStyleRenderers)
        {
            var oldest = _primaryStyleRenderers.MinBy(pair => pair.Value.LastUsed);
            if (oldest.Value is null) return;

            _primaryStyleRenderers.Remove(oldest.Key);
            oldest.Value.Dispose();
        }
    }

    /// <summary>
    /// Disposes and clears every cached appended-segment preview.
    /// </summary>
    /// <remarks>
    /// Decoder teardown goes off the dispatcher: <see cref="VideoFrameReader.Dispose"/>
    /// blocks on its cache gate and <c>Mp4FrameSource</c> on its decode gate, so with
    /// several appended recordings this could otherwise freeze the UI for tens of seconds
    /// — and it runs from ordinary style edits, not just teardown.
    /// </remarks>
    private void DisposeSegmentPreviews()
    {
        _segmentPreviewGeneration++;

        foreach (var ctx in _segmentPreviews.Values)
        {
            DisposeOffUiThread(ctx.Reader);
            ctx.Reader = null;
            ctx.Dispose();
        }

        _segmentPreviews.Clear();
    }

    private void TrimSegmentPreviewCache()
    {
        while (_segmentPreviews.Count >= MaxCachedSegmentPreviews)
        {
            var oldest = _segmentPreviews.MinBy(pair => pair.Value.LastUsed);
            if (string.IsNullOrEmpty(oldest.Key))
                return;

            _segmentPreviews.Remove(oldest.Key);
            DisposeOffUiThread(oldest.Value.Reader);
            oldest.Value.Reader = null;
            oldest.Value.Dispose();
        }
    }

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
    private int _webcamWidth;
    private int _webcamHeight;
    private CanvasBitmap? _lastWebcamFrame;
    private int _lastRenderedFrameIndex = -1;
    private bool _isRendering;
    private TimeSpan? _pendingRenderPosition;
    private bool _pendingRenderForce;
    private bool _syncingTimelineFromPlayback;
    private AdaptivePreviewQuality? _adaptivePreviewQuality;
    private string? _adaptivePreviewVideoPath;
    private PreviewResolution _previewResolution = new(960, 540);
    private double _audioOffsetSeconds;
    private CanvasDevice? _graphicsDevice;
    private int _graphicsRecoveryQueued;
    private int _graphicsRecoveryRequested;
    private bool _graphicsRecoveryInProgress;
    private bool _pageUnloaded;

    // Background style editing state
    private DispatcherTimer? _styleDebounceTimer;
    private bool _suppressStyleEvents;
    private List<string>? _wallpaperPaths;

    // Motion (motion blur / camera drift) editing state — separate debounce timer so a
    // slider drag on these controls doesn't interact with the background-style debounce.
    private DispatcherTimer? _motionDebounceTimer;

    // Cursor style editing state
    private DispatcherTimer? _cursorDebounceTimer;
    private bool _suppressCursorEvents;

    // Text overlay editing state — separate debounce timer so a text-box keystroke never
    // interacts with the background-style / motion / cursor debounces above. The model is
    // still committed (through UndoRedoManager, for undo) on every keystroke; only the
    // (expensive) preview re-render is debounced.
    private DispatcherTimer? _overlayPreviewDebounceTimer;

    // Webcam overlay drag state
    private bool _webcamDragging;
    private Windows.Foundation.Point _webcamDragStart;
    private float _webcamNormX;
    private float _webcamNormY;
    private float _webcamNormSize;
    private float _webcamDragStartNormX;
    private float _webcamDragStartNormY;
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
                    var audioPos = AudioPositionForVideo(Timeline.PlayheadPosition);
                    if (audioPos >= TimeSpan.Zero)
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

            // Start audio when playhead reaches audio start point
            // (handles negative offset where audio started after video)
            if (_audioPlayer is not null && _audioPlayer.IsLoaded
                && Preview.IsPlaying && !_audioPlayer.IsPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
            }
        };

        // Sync audio play/pause with preview
        Preview.IsPlayingChanged += (_, isPlaying) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            if (isPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
                // else: audio hasn't started yet; PlaybackTick will start it
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
            var audioPos = AudioPositionForVideo(TimeSpan.Zero);
            if (audioPos >= TimeSpan.Zero)
            {
                _audioPlayer.Seek(audioPos);
                if (!_audioPlayer.IsPlaying)
                    _audioPlayer.Play();
            }
            else
            {
                // Audio starts later in the video — stop and let tick restart it
                _audioPlayer.Pause();
            }
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

    private async Task InitializePreviewAsync()
    {
        // Started fire-and-forget from ModelReloaded, so anything thrown here would be
        // swallowed and simply leave the cursor/zoom/camera/audio tracks blank with no
        // indication why.
        try
        {
            await InitializePreviewCoreAsync();
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"InitializePreview FAILED: {ex}");
        }
    }

    /// <summary>
    /// Disposes a decoder-backed resource off the dispatcher thread.
    /// <see cref="VideoFrameReader.Dispose"/> blocks on the decode gate (up to 5s, and
    /// <see cref="Mp4FrameSource"/> adds another 5s), which a single in-flight seek can
    /// hold for seconds. Doing that on the UI thread freezes the app while switching
    /// projects or navigating away mid-playback.
    /// </summary>
    private static void DisposeOffUiThread(IDisposable? resource)
    {
        if (resource is null) return;
        _ = Task.Run(() =>
        {
            try { resource.Dispose(); }
            catch (Exception ex)
            {
                Musio.Core.Diagnostics.DiagLog.Write("Editor", $"deferred dispose failed: {ex.Message}");
            }
        });
    }

    private static void TryDispose(IDisposable? resource)
    {
        if (resource is null) return;
        try { resource.Dispose(); }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write(
                "Editor", $"graphics resource disposal failed: {ex.Message}");
        }
    }

    private async Task InitializePreviewCoreAsync()
    {
        // Every await below is a point where this page can be unloaded or a newer init can
        // start. Anything built after the generation moves on is disposed, never published.
        int initGeneration = ++_previewInitGeneration;

        DisposeOffUiThread(_frameReader);
        _previewRenderer?.Dispose();
        _audioPlayer?.Dispose();
        _styleDebounceTimer?.Stop();
        _motionDebounceTimer?.Stop();
        _frameReader = null;
        _previewRenderer = null;
        _audioPlayer = null;
        _compositorReady = false;
        _lastRenderedFrameIndex = -1;
        _lastRenderedSegmentId = null;
        DisposeSegmentPreviews();
        DisposePrimaryStyleRenderers();
        Timeline.ClearSegmentTrackVisuals();

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
        {
            Preview.HideQualityIndicator();
            _thumbnailGenerationId++;
            Timeline.ClearThumbnails();
            _thumbnailsCompletedForPath = null;
            _thumbnailsDoneForFiles.Clear();
            return;
        }

        // This method re-runs on every ModelReloaded — which fires on project change AND
        // on every editor page reconstruction. Two overlapping runs used to cancel each
        // other's filmstrip pass, and because generation sat *after* the preview reader
        // opened, a run whose reader failed to open returned early and started no pass at
        // all — leaving the track a flat colour with nothing ever retrying. Thumbnails
        // depend only on the source file, so track them per path: generate once, never
        // restart a pass already running for that file, and never gate it on the preview.
        string videoPath = project.VideoFilePath;
        bool haveStrip = string.Equals(
            _thumbnailsCompletedForPath, videoPath, StringComparison.OrdinalIgnoreCase);
        bool alreadyGenerating = string.Equals(
            _thumbnailsInFlightForPath, videoPath, StringComparison.OrdinalIgnoreCase);

        int fps = project.Fps > 0 ? project.Fps : 30;
        int previewFps = Math.Min(fps, 30);
        Preview.PreviewFps = previewFps;

        if (!string.Equals(
                _adaptivePreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
        {
            _adaptivePreviewQuality = new AdaptivePreviewQuality(
                project.Width, project.Height, Environment.ProcessorCount);
            _adaptivePreviewVideoPath = videoPath;
            _previewResolution = _adaptivePreviewQuality.Current;
        }

        Preview.SetQualityIndicator(
            _previewResolution.MaxWidth,
            _previewResolution.MaxHeight,
            project.Width,
            project.Height);

        if (!haveStrip && !alreadyGenerating)
        {
            _thumbnailGenerationId++; // cancel any pass for a different source
            Timeline.ClearThumbnails();
            _thumbnailsCompletedForPath = null;
            _thumbnailsDoneForFiles.Clear();

            // Deliberately started before, and independently of, the preview reader:
            // the filmstrip must not depend on the preview decoder opening successfully.
            _ = GenerateAllTimelineThumbnailsAsync(videoPath, fps);
        }
        else
        {
            // The primary strip is already built, but appending a recording or importing a
            // video adds a NEW source to an unchanged primary — and the pass above is gated
            // entirely on the primary path, so it would never run for that source and the
            // clip would sit on the track as a flat colour until the project was reopened.
            // Sources are tracked individually, so this only builds what is actually missing.
            _ = GenerateAppendedThumbnailsAsync(videoPath);
        }

        var reader = await VideoFrameReader.OpenPreviewFromVideoPathAsync(
            videoPath,
            fps,
            _previewResolution.MaxWidth,
            _previewResolution.MaxHeight,
            PrimaryPreviewCacheBudgetBytes);
        if (initGeneration != _previewInitGeneration)
        {
            // Page unloaded or a newer init took over while the decoder was opening.
            DisposeOffUiThread(reader);
            return;
        }

        _frameReader = reader;
        if (_frameReader is null)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor",
                $"no decodable video at '{videoPath}'; preview unavailable");
            return;
        }

        // Load per-file cursor + audio track data for appended recordings so their
        // mouse/click and audio markers show on the tracks and move with the segment.
        _ = LoadAppendedTrackVisualsAsync();

        // Load audio waveform data for timeline visualization
        await LoadAudioWaveformAsync(project, initGeneration);
        if (initGeneration != _previewInitGeneration) return;

        // Load mouse data for cursor smoothing + click animations
        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch (Exception ex)
            {
                Musio.Core.Diagnostics.DiagLog.Write("Editor",
                    $"cursor load FAILED '{project.CursorDataFilePath}': {ex.Message}");
            }
        }

        if (mouseData is null)
        {
            // No cursor data — just show raw frames
            Timeline.Refresh();
            _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
            return;
        }

        // Feed cursor data to timeline for track visualization
        ViewModel.Model.CursorData = mouseData;
        ViewModel.Model.MouseToVideoOffsetSeconds = project.MouseToVideoOffsetSeconds;

        // Build zoom keyframes from click events so they appear on the timeline.
        // Subtract MouseToVideoOffsetSeconds to convert mouse-relative time to video time.
        int sourceW = project.Width > 0 ? project.Width : 1920;
        int sourceH = project.Height > 0 ? project.Height : 1080;
        // Mouse hook (WH_MOUSE_LL) in PerMonitorV2 reports physical pixels —
        // no DPI scaling needed for click coordinate transforms.
        float dpiScaleX = 1.0f;
        float dpiScaleY = 1.0f;
        int cropOffX = project.CropOffsetX;
        int cropOffY = project.CropOffsetY;
        double mouseOffset = project.MouseToVideoOffsetSeconds;
        // Only generate the PRIMARY recording's auto-zoom keyframes if none exist yet.
        // InitializePreviewAsync re-runs whenever the editor page is reconstructed (e.g.
        // after "Record More" navigates away and back). The TimelineModel is shared and
        // already carries the user's edits, so regenerating would (a) wipe manual zoom
        // segments (stored with SourceVideoFilePath == null) and (b) resurrect auto-zooms
        // the user deleted (tracked in SuppressedClickTicks). Generate once, then preserve.
        // (Appended recordings mirror this exact guard — see GenerateAppendedZoomKeyframes.)
        // A source restored from a package is never regenerated: an empty zoom track is a
        // saved choice there, not a not-yet-populated one.
        if (!ProjectService.Instance.IsRestoredSource(project.VideoFilePath)
            && !ZoomKeyframesExistForSource(null))
        {
            foreach (var click in mouseData.Clicks.Where(c => c.IsDown))
            {
                // Respect deletions: never re-add an auto-zoom the user removed.
                if (ViewModel.Model.SuppressedClickTicks.Contains(click.TimestampTicks))
                    continue;

                double clickTime = (click.TimestampTicks - mouseData.StartTimestampTicks) / mouseData.TickFrequency
                    - mouseOffset;
                if (clickTime < 0) continue; // skip pre-roll clicks before video started
                // ...and clicks after capture stopped. Their zoom-in ramp starts a second
                // early, so the segment would be drawn on the zoom track while
                // AutoZoomEngine (which applies the same range rule) refuses to render it,
                // leaving a visible segment that does nothing.
                if (project.Duration > TimeSpan.Zero && clickTime > project.Duration.TotalSeconds) continue;
                ViewModel.Model.ZoomKeyframes.Add(new Musio.Core.Timeline.ZoomKeyframe
                {
                    Timestamp = TimeSpan.FromSeconds(clickTime),
                    ZoomLevel = 2.0,
                    CenterX = (click.X * dpiScaleX - cropOffX) / sourceW,
                    CenterY = (click.Y * dpiScaleY - cropOffY) / sourceH,
                    SourceClickTicks = click.TimestampTicks,
                });
            }
        }

        Timeline.Refresh();

        // Build composition config with cursor effects enabled. Aspect-ratio fields
        // (AR/FitMode/CropAnchor/ZoomScope) live on Project and must be mirrored into
        // CompositionConfig so the preview renderer matches both the editor UI and
        // the export pipeline (which sources these from Project).
        var config = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
        bool restored = ProjectService.Instance.IsRestoredFromPackage;

        config = config with
        {
            OutputFps = previewFps,
            AspectRatio = project.AspectRatio,
            FitMode = project.FitMode,
            CropAnchorX = project.CropAnchorX,
            CropAnchorY = project.CropAnchorY,
            ZoomScope = project.ZoomScope,
        };

        if (!restored)
        {
            // First-open defaults for a freshly captured recording. These are deliberately
            // NOT applied to a restored project — they would overwrite the cursor, zoom and
            // smoothing choices the user saved with it.
            config = config with
            {
                // Zero-phase (forward-backward) spring: smooths trackpad stop-and-go like
                // Screen Studio with NO time lag (offline filtering uses future samples), so
                // the cursor stays smooth yet lands on target on time. De-stutter stays off.
                SmoothingAlgorithm = SmoothingAlgorithm.ZeroPhaseSpring,
                SmoothingStrength = SmoothingStrength.Smooth,
                Cursor = new CursorStyle
                {
                    Scale = 3.0f,
                    ClickAnimationEnabled = true,
                    AutoHideEnabled = true,
                    AutoHideDelaySeconds = 3.0f,
                },
                Zoom = new AutoZoomConfig { Enabled = true },
            };
        }

        // Capture-type-specific style defaults (e.g. zeroed padding/shadow
        // for full-screen Monitor captures) are applied once at project load
        // time in ProjectService.SetProject. User edits via the Style menu
        // are preserved across editor re-navigation.

        // Auto-enable webcam overlay if the project has a webcam recording
        if (!string.IsNullOrWhiteSpace(project.WebcamFilePath) &&
            File.Exists(project.WebcamFilePath))
        {
            config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };
        }

        // Persist so the export pipeline uses the same config
        ProjectService.Instance.CurrentComposition = config;

        try
        {
            var renderer = new PreviewRenderer();
            try
            {
                await renderer.InitializeAsync(
                    mouseData, config,
                    project.Width > 0 ? project.Width : 1920,
                    project.Height > 0 ? project.Height : 1080,
                    project.Duration,
                    project.MouseToVideoOffsetSeconds,
                    project.CropOffsetX,
                    project.CropOffsetY,
                    project.DpiScale);
            }
            catch
            {
                renderer.Dispose();
                throw;
            }

            if (initGeneration != _previewInitGeneration)
            {
                renderer.Dispose();
                return;
            }

            _previewRenderer = renderer;
            _compositorReady = true;

            // Push the model's zoom keyframes/suppressed-clicks AND text overlays onto the
            // freshly published renderer before the first frame draws — mirrors the same
            // call in RebuildPreviewRendererAsync. The timeline model is already fully
            // loaded by this point (project/ViewModel.Model set above), so this is safe.
            // Without this, opening an existing project shows no overlays in the preview
            // (though export still renders them, since export builds its own renderer
            // through SegmentFrameComposer) until some other edit happens to trigger a sync.
            SyncZoomStateToRenderer();
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"PreviewRenderer init failed: {ex}");
            // Compositor init failed — fall back to raw frames
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Load webcam composition for preview overlay
        await LoadWebcamCompositionAsync(project);
        if (initGeneration != _previewInitGeneration) return;

        // Initialize webcam overlay editing
        InitializeWebcamOverlay(config);

        // Show style controls for Window and Region captures
        InitializeStyleControls(project, config);

        // Show cursor controls when cursor data is available
        InitializeCursorControls(project, config);

        // Aspect ratio + fit + crop anchor controls (always visible)
        InitializeAspectRatioControls();

        _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
    }

    private async Task UpdatePreviewFrameAsync(TimeSpan position, bool force = false)
    {
        if (_frameReader is null) return;
        if (_graphicsRecoveryInProgress)
        {
            _pendingRenderPosition = position;
            _pendingRenderForce |= force;
            return;
        }

        // Coalesce overlapping render requests: if a render is already in
        // flight, record the latest requested position and let the active
        // render pick it up when it finishes.
        if (_isRendering)
        {
            _pendingRenderPosition = position;
            _pendingRenderForce |= force;
            return;
        }

        _isRendering = true;
        try
        {
            // Drain loop: render the current request, then check if a newer
            // position arrived while we were rendering and handle it too.
            TimeSpan currentPos = position;
            bool currentForce = force;
            do
            {
                // Wall-clock time is intentionally used as the user-visible load signal:
                // decode, composition and UI contention all reduce preview headroom.
                long renderStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await RenderFrameAtAsync(currentPos, currentForce);

                if (Preview.IsPlaying
                    && IsVideoPosition(currentPos)
                    && _adaptivePreviewQuality?.ObservePlaybackFrame(
                        System.Diagnostics.Stopwatch.GetElapsedTime(renderStarted),
                        Preview.PreviewFps) is { } resolution)
                {
                    if (await ApplyPreviewResolutionAsync(resolution))
                    {
                        _adaptivePreviewQuality.Commit(resolution);
                        currentForce = true;
                        continue;
                    }

                    _adaptivePreviewQuality.RejectChange();
                }

                // Consume any pending request that arrived during rendering
                if (_pendingRenderPosition.HasValue)
                {
                    currentPos = _pendingRenderPosition.Value;
                    currentForce = _pendingRenderForce;
                    _pendingRenderPosition = null;
                    _pendingRenderForce = false;
                }
                else
                {
                    break;
                }
            } while (true);
        }
        finally
        {
            _isRendering = false;
        }
    }

    /// <summary>
    /// Whether rendering <paramref name="position"/> costs video-decode work, for the
    /// adaptive-quality feedback loop (<see cref="UpdatePreviewFrameAsync"/>): the loop only
    /// samples elapsed time as a "video" data point when this is true, since a text slide by
    /// itself renders far more cheaply and would otherwise pull the observed average down.
    /// </summary>
    /// <remarks>
    /// An ACTIVE transition composes BOTH sides every tick regardless of which one the
    /// playhead nominally sits over (see <see cref="RenderFrameAtAsync"/>) — a video→slide
    /// dissolve therefore still recomposes a full, uniquely-offset video frame for its
    /// OUTGOING side (the rolling model, T1) even though <c>GetSegmentAtTime(position)</c>
    /// resolves to the (cheap) incoming slide. That per-tick video cost is new: the legacy
    /// crossfade cache composed that same outgoing side only ONCE per dissolve, so it was
    /// never expensive enough to need adaptive downgrading. Checking only the per-position
    /// segment made this the one case the feedback loop could never observe, however heavy
    /// the transition got — treat either transition side being a <see cref="VideoSegment"/>
    /// as video load too, so a slow video-involved dissolve still triggers a downgrade.
    /// </remarks>
    private bool IsVideoPosition(TimeSpan position)
    {
        var model = ViewModel.Model;
        if (model.Segments.Count == 0) return true;
        if (model.GetSegmentAtTime(position).Segment is VideoSegment) return true;

        var resolution = TransitionResolver.Resolve(model, position);
        return resolution.Active
            && (resolution.IncomingSegment is VideoSegment || resolution.OutgoingSegment is VideoSegment);
    }

    private async Task<bool> ApplyPreviewResolutionAsync(PreviewResolution resolution)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null
            || string.IsNullOrEmpty(project.VideoFilePath)
            || resolution == _previewResolution)
        {
            return false;
        }

        var videoPath = project.VideoFilePath;
        int fps = project.Fps > 0 ? project.Fps : 30;
        int initGeneration = _previewInitGeneration;

        var reader = await VideoFrameReader.OpenPreviewFromVideoPathAsync(
            videoPath,
            fps,
            resolution.MaxWidth,
            resolution.MaxHeight,
            PrimaryPreviewCacheBudgetBytes);

        if (!ReferenceEquals(project, ProjectService.Instance.CurrentProject)
            || initGeneration != _previewInitGeneration
            || reader is null)
        {
            DisposeOffUiThread(reader);
            return false;
        }

        using var probe = await reader.LoadFrameAtTimeAsync(TimeSpan.Zero);
        if (probe is null
            || !ReferenceEquals(project, ProjectService.Instance.CurrentProject)
            || initGeneration != _previewInitGeneration)
        {
            DisposeOffUiThread(reader);
            return false;
        }

        var oldReader = _frameReader;
        _frameReader = reader;
        _previewResolution = resolution;
        Preview.SetQualityIndicator(
            resolution.MaxWidth,
            resolution.MaxHeight,
            project.Width,
            project.Height);
        DisposeOffUiThread(oldReader);
        DisposeSegmentPreviews();

        // _frameReader just changed identity: a transition compose that read the OLD
        // reader before this ran and is still awaiting a decode on it (see the remarks on
        // _primaryPreviewStateGeneration) must not go on to composite that stale bitmap
        // against state resolved for the new one.
        _primaryPreviewStateGeneration++;

        _lastRenderedFrameIndex = -1;
        _lastRenderedSegmentId = null;
        return true;
    }

    private async Task RenderFrameAtAsync(TimeSpan position, bool force)
    {
        if (_frameReader is null) return;

        // Segment-aware rendering: when the timeline uses segments, check which
        // segment the playhead is over and render text slides directly.
        var model = ViewModel.Model;
        if (model.Segments.Count > 0)
        {
            // Soft cut: when the playhead is within a transition window on the leading
            // edge of a boundary, cross-dissolve (or apply whatever effect is configured
            // for) the outgoing neighbour into the incoming segment instead of hard
            // cutting. TransitionResolver is the single shared source of truth for "what
            // transition is active right now" — the exporter (SegmentFrameComposer, T2)
            // calls the exact same function, so both pipelines dissolve at identical
            // instants with identical effect/duration/easing. Fully guarded — any failure
            // falls back to the normal render.
            var resolution = TransitionResolver.Resolve(model, position);
            if (resolution.Active)
            {
                CanvasRenderTarget? incoming = null;
                CanvasRenderTarget? outgoing = null;
                try
                {
                    incoming = await ComposePreviewFrameAsync(position);
                    if (incoming is not null)
                    {
                        var (w, h) = GetPreviewCanvasSize();

                        // Must run BEFORE composing the outgoing frame: committing an
                        // in-place text edit changes the slide the outgoing frame is
                        // composed from. (There is no longer a crossfade cache for this to
                        // race with — see the remarks on why one isn't needed below — but
                        // the edit must still be committed first regardless.)
                        HideTextEditOverlay();

                        // Rolling model (T1): the outgoing segment keeps playing past its
                        // own cut point instead of freezing on a fixed instant (see
                        // TransitionResolution.OutgoingLocalOffset), so — unlike the legacy
                        // SlideTransitions crossfade this replaced — it composes to a
                        // DIFFERENT image on nearly every tick. That voids the premise a
                        // page-owned cache here relied on (compose once, reuse for ~15
                        // ticks per dissolve), so the cache was retired rather than
                        // rekeyed: composing fresh every tick is exactly what the incoming
                        // side already does, and the two remaining places that used to make
                        // repeated identical composes cheap still apply without it —
                        // VideoFrameReader's own decode LRU (keyed by frame index) hits
                        // whenever the offset holds at the reader's last frame because the
                        // available footage ran out before the (now possibly
                        // longer/configurable) dissolve did, and TextSlideRenderer's own
                        // render pass is cheap enough on its own (~1.5 ms/frame, measured
                        // and documented in learnings.md) not to need caching either.
                        //
                        // Same-source contiguous split: a plain split of one recording adds
                        // a transition at the existing cut without changing any footage, so
                        // the outgoing side's rolled offset can map to the exact source
                        // instant the incoming side already shows. Resolve the incoming
                        // side's own mapped source time (mirroring
                        // SegmentFrameComposer.ComposeFrameAsync in the exporter) so the
                        // outgoing compose can detect and collapse that case — see
                        // SegmentFrameComposer.CollapseContiguousSourceBoundary's remarks
                        // (shared with the exporter since T8's consolidation pass).
                        string? incomingVideoFilePath = null;
                        double? incomingSourceTimeSeconds = null;
                        if (resolution.IncomingSegment is VideoSegment incomingVideo
                            && resolution.OutgoingSegment is not null)
                        {
                            var incomingLocal = resolution.OutgoingLocalOffset
                                - resolution.OutgoingSegment.Duration;
                            double incomingSpeed = incomingVideo.SpeedFactor > 0
                                ? incomingVideo.SpeedFactor : 1.0;
                            incomingVideoFilePath = incomingVideo.VideoFilePath;
                            incomingSourceTimeSeconds = incomingVideo.SourceStart.TotalSeconds
                                + incomingLocal.TotalSeconds * incomingSpeed;
                        }

                        outgoing = resolution.OutgoingSegment is { } outgoingSegment
                            ? await ComposePreviewFrameAtOffsetAsync(
                                outgoingSegment, resolution.OutgoingLocalOffset,
                                incomingVideoFilePath, incomingSourceTimeSeconds)
                            : null;

                        // Force the normal path to fully redraw once the dissolve ends.
                        _lastRenderedFrameIndex = -1;
                        _lastRenderedSegmentId = null;

                        if (outgoing is not null)
                        {
                            // Blend at the composed canvas size. Using the raw capture size
                            // here made the picture jump to the capture aspect ratio for the
                            // length of the dissolve and snap back when it ended. Pass the
                            // resolved effect + eased progress (not a hardcoded CrossFade) so
                            // configured transition types/easing are honoured; unimplemented
                            // effect types degrade safely inside TransitionRenderer.
                            _transitionRenderer ??= new TransitionRenderer();
                            var blended = _transitionRenderer.Render(
                                outgoing, incoming, resolution.Type, resolution.EasedProgress, w, h);
                            Preview.SetFrame(blended);
                        }
                        else
                        {
                            // Only the outgoing side is missing: present the incoming frame
                            // un-dissolved rather than stranding the previous frame — the
                            // text slide — on screen while the playhead is over capture.
                            var present = incoming;
                            incoming = null; // ownership moves to the preview
                            Preview.SetFrame(present);
                        }
                        return;
                    }

                    // No incoming frame. Fall through to the normal render so it retries
                    // rather than leaving the outgoing text slide frozen on screen for the
                    // rest of the dissolve while the playhead runs on over the capture.
                    Musio.Core.Diagnostics.DiagLog.Write("Editor",
                        $"crossfade incoming frame unavailable at {position}; falling back to direct render");
                }
                catch (Exception ex)
                {
                    Musio.Core.Diagnostics.DiagLog.Write("Editor",
                        $"slide crossfade preview error at {position}: {ex.Message}");
                }
                finally
                {
                    incoming?.Dispose();
                    outgoing?.Dispose();
                }
                // Fall through to the normal render on any miss/failure.
            }

            var (segment, localOffset) = model.GetSegmentAtTime(position);

            if (segment is TextSlideSegment slide)
            {
                await RenderTextSlidePreviewAsync(slide, localOffset);
                return;
            }

            if (segment is VideoSegment videoSeg)
            {
                // A slide can't be showing while the playhead is over a video segment, so
                // commit any in-place slide edit and drop the stale slide id — but don't
                // collapse the shared canvas outright, since the selected text overlay (if
                // any) may be visible right here; UpdateOverlayEditPreview below decides
                // that once the frame is actually composed.
                CommitActiveTextEditIfAny();
                _previewSlideId = null;

                // Map the playhead within this video segment to its source time
                var sourceInSeg = videoSeg.SourceStart +
                    TimeSpan.FromTicks((long)(localOffset.Ticks * videoSeg.SpeedFactor));

                // Force a redraw when crossing a segment boundary (the frame-index
                // cache is shared across sources).
                if (_lastRenderedSegmentId != videoSeg.Id)
                {
                    _lastRenderedFrameIndex = -1;
                    _lastRenderedSegmentId = videoSeg.Id;
                    force = true;
                }

                // If this primary-file segment carries a frame style / cursor override
                // that differs from what the primary renderer was last built with,
                // rebuild it so per-segment styles apply across primary splits.
                await EnsurePrimaryRendererForSegmentAsync(videoSeg);

                await RenderSegmentVideoAsync(videoSeg, sourceInSeg, force);
                UpdateOverlayEditPreview(videoSeg.VideoFilePath, sourceInSeg);
                return;
            }
        }

        CommitActiveTextEditIfAny();
        _previewSlideId = null;
        // Legacy path: map output (playhead) time to source time
        TimeSpan sourcePosition = MapToSourceTime(position);
        await RenderVideoFrameAsync(sourcePosition, force);
        UpdateOverlayEditPreview(PrimaryVideoPath, sourcePosition);
    }

    /// <summary>
    /// Renders a standalone preview frame for an arbitrary output position WITHOUT
    /// presenting it or touching the frame cache. Used to obtain the INCOMING side of a
    /// transition (and, before the rolling model, both sides). Returns null on failure
    /// (the caller falls back to the normal render path). Thin wrapper around
    /// <see cref="ComposePreviewFrameAtOffsetAsync"/> that resolves the segment/local-offset
    /// pair the normal way — via <see cref="TimelineModel.GetSegmentAtTime"/> — which is valid
    /// for any INCOMING side or ordinary (non-transition) frame, but NOT for an outgoing
    /// transition side once it rolls past its own segment's duration (see the other overload).
    /// </summary>
    private async Task<CanvasRenderTarget?> ComposePreviewFrameAsync(TimeSpan outputPos)
    {
        var model = ViewModel.Model;
        var (segment, localOffset) = model.GetSegmentAtTime(outputPos);
        return await ComposePreviewFrameAtOffsetAsync(segment, localOffset);
    }

    /// <summary>
    /// Composes <paramref name="segment"/> at <paramref name="localOffset"/> into its own
    /// local timeline — the shared body behind both the normal per-position path
    /// (<see cref="ComposePreviewFrameAsync(TimeSpan)"/>, where the offset always lies within
    /// the segment) and the ROLLING transition path (<see cref="RenderFrameAtAsync"/>, where
    /// <paramref name="localOffset"/> is a
    /// <see cref="TransitionResolution.OutgoingLocalOffset"/> that deliberately EXCEEDS the
    /// segment's own <see cref="TimelineSegment.Duration"/> so the outgoing side of a dissolve
    /// keeps rolling past its own cut point instead of freezing — mirrors
    /// <c>SegmentFrameComposer.ComposeSegmentAtOffsetAsync</c> in the exporter so both pipelines
    /// dissolve the same footage). Text slides render at project resolution; video frames
    /// render at their source resolution and the transition renderer scales both to the output
    /// rect when blending.
    /// </summary>
    /// <param name="incomingVideoFilePath">
    /// Only meaningful (non-null) when composing the OUTGOING side of an active transition AND
    /// the transition's incoming segment is itself a <see cref="VideoSegment"/>: that segment's
    /// source file, used by the <see cref="VideoSegment"/> branch below to detect a same-source
    /// contiguous boundary (see <see cref="SegmentFrameComposer.CollapseContiguousSourceBoundary"/>).
    /// Left null (the default) for the normal per-position path, where there is no "incoming
    /// side" to collide with.
    /// </param>
    /// <param name="incomingSourceTimeSeconds">
    /// The incoming side's own mapped source-file time for this exact instant, alongside
    /// <paramref name="incomingVideoFilePath"/>.
    /// </param>
    /// <remarks>
    /// Each segment type is responsible for making an over-long offset degrade sensibly.
    /// <see cref="TextSlideSegment"/> already clamps its animation progress to [0, 1] below, so
    /// it naturally holds the slide's last animated frame. <see cref="VideoSegment"/> maps the
    /// offset into an absolute source-file time via <see cref="VideoFrameReader.LoadFrameAtTimeAsync"/>,
    /// whose <see cref="VideoFrameReader.GetFrameIndex"/> already clamps the resulting frame
    /// index to <c>[0, FrameCount - 1]</c> — i.e. holding the reader's own last decodable frame
    /// once the offset runs past it. That is the exact same bound
    /// <see cref="SegmentFrameComposer.ClampSourceTime"/> (T2) uses for the export side
    /// (<c>VideoFrameReader.Duration</c>), so this method reaches an equivalent result WITHOUT
    /// needing to call that helper -- which stayed <c>internal</c> to Musio.Core (only
    /// <c>Musio.Tests</c> has <c>InternalsVisibleTo</c>) even after the T8 integration pass,
    /// since this reader-clamp equivalence means there is no actual behavioural gap here to
    /// close, only an implicit-vs-explicit-clamp divergence -- which is what this remark exists
    /// to document prominently, per T8's own instructions, rather than leave unstated. The
    /// same is NOT true of the same-source contiguous-boundary policy below: that one had no
    /// equivalent already-existing safety net anywhere in this file, so a private copy of it
    /// was originally kept here deliberately (not merely noted as a gap) to avoid a visible
    /// preview/export divergence -- T8 has since PROMOTED that method to
    /// <see cref="SegmentFrameComposer.CollapseContiguousSourceBoundary"/> (now `public`) and
    /// deleted this file's copy, so both pipelines now call the exact same shared
    /// implementation instead of two independently-maintained ones.
    /// </remarks>
    private async Task<CanvasRenderTarget?> ComposePreviewFrameAtOffsetAsync(
        TimelineSegment? segment, TimeSpan localOffset,
        string? incomingVideoFilePath = null, double? incomingSourceTimeSeconds = null)
    {
        if (segment is TextSlideSegment slide)
        {
            _textSlideRenderer ??= new TextSlideRenderer();
            await _textSlideRenderer.EnsureBackgroundLoadedAsync(slide);
            var (w, h) = GetPreviewCanvasSize();
            double progress = slide.Duration.TotalSeconds > 0
                ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
                : 0;
            return _textSlideRenderer.RenderSlide(slide, progress, w, h);
        }

        if (segment is not VideoSegment seg)
            return null;

        // Unchanged from before this task: tick-precision mapping (matches the ordinary,
        // non-transition preview/export paths elsewhere in this file). Only converted to
        // double seconds afterwards, for the collapse check below and to match
        // incomingSourceTimeSeconds' own units — the conversion cannot change which frame
        // index LoadFrameAtTimeAsync resolves to, since that only depends on
        // TotalSeconds * fps rounded to an int.
        var rawSourceTime = seg.SourceStart +
            TimeSpan.FromTicks((long)(localOffset.Ticks * seg.SpeedFactor));
        double collapsedSourceSeconds = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            rawSourceTime.TotalSeconds, seg.VideoFilePath,
            incomingSourceTimeSeconds, incomingVideoFilePath, seg.Fps);
        var sourceTime = collapsedSourceSeconds == rawSourceTime.TotalSeconds
            ? rawSourceTime // No collapse applied: keep the tick-precision value as-is.
            : TimeSpan.FromSeconds(collapsedSourceSeconds);

        // Primary-recording segment → main reader/compositor.
        if (string.Equals(seg.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_frameReader is null) return null;
            var reader = _frameReader;

            // Resolves (building/caching if needed) the compositor for THIS segment's
            // effective style WITHOUT mutating the singleton _previewRenderer — see the
            // method's remarks for why EnsurePrimaryRendererForSegmentAsync (which the
            // ordinary, non-transition render path still uses) is unsafe to call from here.
            var compositor = await GetPrimaryTransitionCompositorAsync(seg);

            // Snapshot AFTER the awaits above (building an alt-style compositor can itself
            // await a multi-frame compositor init) so both checks below reflect state as of
            // right now, then re-validate after the frame decode await — see the remarks on
            // _primaryPreviewStateGeneration for why a rebuild/teardown that completes
            // during either await must not be silently composited past.
            if (!ReferenceEquals(reader, _frameReader)) return null;
            int stateGen = _primaryPreviewStateGeneration;

            var bitmap = await reader.LoadFrameAtTimeAsync(sourceTime);
            if (bitmap is null) return null;

            try
            {
                if (stateGen != _primaryPreviewStateGeneration || !ReferenceEquals(reader, _frameReader))
                    return null;

                if (compositor is not null && !_zoomRegionEditMode)
                {
                    // The webcam overlay only follows the singleton today
                    // (SetWebcamFrameForPreviewAsync always targets _previewRenderer): an
                    // alt-style compositor built for a differently-styled transition side
                    // therefore composes without a live webcam update for that one frame.
                    // Rare (needs a webcam AND a per-segment style override AND an active
                    // transition simultaneously) and never worse than before this cache
                    // existed — this whole code path is new; the alternative was an
                    // unbounded rebuild loop, not a correct webcam-carrying frame.
                    if (ReferenceEquals(compositor, _previewRenderer))
                    {
                        await SetWebcamFrameForPreviewAsync(sourceTime);
                        if (stateGen != _primaryPreviewStateGeneration) return null;
                    }

                    var composed = compositor.RenderPreviewFrame(bitmap, sourceTime);
                    if (composed is not null) return composed;
                }
                else
                {
                    var device = CanvasDevice.GetSharedDevice();
                    var rt = new CanvasRenderTarget(device,
                        bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
                    using (var ds = rt.CreateDrawingSession()) ds.DrawImage(bitmap);
                    return rt;
                }
                return null;
            }
            finally
            {
                // Every path above either already returned a NEW render target (never the
                // decoded bitmap itself) or fell through to null — the decoded bitmap is
                // always this method's to release, on every path including an exception
                // thrown while composing it.
                bitmap.Dispose();
            }
        }

        // Appended-recording segment → its own per-segment context.
        var ctx = await GetOrBuildSegmentPreviewAsync(seg);
        if (ctx?.Reader is null) return null;

        // Valid as of right now (GetOrBuildSegmentPreviewAsync only ever returns a context
        // it has just confirmed is not abandoned) — re-checked after the awaits below, so a
        // cache clear/eviction/style-rebuild that completes while this method is awaiting a
        // decode or a webcam extraction is detected instead of used past.
        int ctxGeneration = _segmentPreviewGeneration;
        var segReader = ctx.Reader;

        var segBitmap = await segReader.LoadFrameAtTimeAsync(sourceTime);
        if (segBitmap is null) return null;

        try
        {
            if (ctxGeneration != _segmentPreviewGeneration) return null;

            if (ctx.Ready && ctx.Renderer is not null && !_zoomRegionEditMode)
            {
                if (ctx.Webcam is not null)
                {
                    try
                    {
                        var wf = await ExtractWebcamFrameAsync(ctx.Webcam, sourceTime, ctx.WebcamW, ctx.WebcamH);
                        if (ctxGeneration != _segmentPreviewGeneration) return null;
                        if (wf is not null)
                        {
                            ctx.LastWebcamFrame?.Dispose();
                            ctx.LastWebcamFrame = wf;
                            ctx.Renderer.SetWebcamFrame(wf);
                        }
                    }
                    catch { }
                }
                var composed = ctx.Renderer.RenderPreviewFrame(segBitmap, sourceTime);
                if (composed is not null) return composed;
                return null;
            }

            var dev = CanvasDevice.GetSharedDevice();
            var fallback = new CanvasRenderTarget(dev,
                segBitmap.SizeInPixels.Width, segBitmap.SizeInPixels.Height, 96);
            using (var ds = fallback.CreateDrawingSession()) ds.DrawImage(segBitmap);
            return fallback;
        }
        finally
        {
            segBitmap.Dispose();
        }
    }

    /// <summary>
    /// Resolves the compositor to use for a PRIMARY-file segment while composing either
    /// side of an active transition, WITHOUT mutating the singleton <see cref="_previewRenderer"/>
    /// that ordinary (non-transition) playback owns via
    /// <see cref="EnsurePrimaryRendererForSegmentAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composing a dissolve calls this (via <see cref="ComposePreviewFrameAtOffsetAsync"/>)
    /// twice per tick — once for the incoming segment, once for the outgoing one — and the
    /// two can carry DIFFERENT per-segment frame-style/cursor overrides: a primary-file
    /// split with a style change at the very cut a transition now sits on.
    /// <see cref="EnsurePrimaryRendererForSegmentAsync"/> rebuilds the ONE singleton
    /// compositor to match whichever segment last asked for it, so composing both sides
    /// with THAT method alternated the singleton between the two segments' styles every
    /// tick, forever: incoming's compose rebuilds it for B, outgoing's compose then sees a
    /// mismatch and rebuilds it for A, incoming's NEXT compose sees a mismatch again and
    /// rebuilds for B again — an unbounded rebuild loop. Each rebuild reopens cursor data
    /// and reallocates render targets on the UI thread (the exact freeze
    /// <see cref="EnsurePrimaryRendererForSegmentAsync"/>'s own remarks already warn about
    /// for a single misbehaving caller), and the rolling model (T1) makes this happen on
    /// EVERY tick of the dissolve rather than once, which is what turns it from merely slow
    /// into an unbounded, UI-freezing loop.
    /// </para>
    /// <para>
    /// Caching a compositor per EFFECTIVE (<see cref="BackgroundStyle"/>,
    /// <see cref="CursorStyle"/>) pair — mirroring <c>SegmentFrameComposer</c>'s
    /// <c>SourceKey</c>-keyed context cache in the exporter — lets both sides compose
    /// without either clobbering shared state: composing outgoing never invalidates state
    /// incoming depends on (or vice versa), because neither one is ever mutated by the
    /// other's compose — each style gets its OWN compositor instance, looked up or built
    /// once and reused after. The common case — neither side carries an override, or the
    /// ordinary render path already left the singleton matching this segment's style — is
    /// short-circuited straight to the singleton with no new allocation, so an unconfigured
    /// project's transition costs exactly what it did before this cache existed.
    /// </para>
    /// </remarks>
    private async Task<PreviewRenderer?> GetPrimaryTransitionCompositorAsync(VideoSegment seg)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return null;

        var global = ProjectService.Instance.CurrentComposition;
        var wantBg = seg.FrameStyleOverride ?? global.Background;
        var wantCursor = seg.CursorStyleOverride ?? global.Cursor;

        if (_compositorReady && _previewRenderer is not null
            && Equals(wantBg, _primaryRenderBackground) && Equals(wantCursor, _primaryRenderCursor))
        {
            return _previewRenderer;
        }

        var key = (wantBg, wantCursor);
        if (_primaryStyleRenderers.TryGetValue(key, out var cached))
        {
            cached.LastUsed = ++_primaryStyleRendererUseCounter;
            return cached.Renderer;
        }

        int stateGen = _primaryPreviewStateGeneration;
        TrimPrimaryStyleRendererCache();

        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data */ }
        }
        mouseData ??= new MouseRecordingData();

        // Same style-layering RebuildPreviewRendererCoreAsync uses for the singleton, just
        // not written onto _primaryRenderBackground/_primaryRenderCursor — those track ONLY
        // what the singleton itself is currently built with.
        var effective = global with { Background = wantBg, Cursor = wantCursor };
        effective = HideCursorWhenNoSamples(effective, mouseData);

        PreviewRenderer renderer;
        try
        {
            renderer = new PreviewRenderer();
            await renderer.InitializeAsync(
                mouseData, effective,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);

            renderer.UpdateZoomKeyframes(ManualKeyframesForSource(null));
            renderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
            SyncTextOverlaysToRenderer(renderer, null);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor",
                $"transition alt-style compositor build failed for segment {seg.Id}: {ex.Message}");
            return null;
        }

        // A teardown/rebuild that ran to completion during InitializeAsync's await above
        // already disposed (or is about to dispose) whatever this project's state used to
        // be, and may have changed the dimensions/duration/mouse data this renderer was
        // just built from. Abandon it rather than caching a build that no longer matches —
        // the same principle GetOrBuildSegmentPreviewAsync's own Abandoned() check applies.
        if (stateGen != _primaryPreviewStateGeneration)
        {
            TryDispose(renderer);
            return null;
        }

        _primaryStyleRenderers[key] = new PrimaryStyleRenderer
        {
            Renderer = renderer,
            LastUsed = ++_primaryStyleRendererUseCounter,
        };
        return renderer;
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
    private ITextEditTarget? _editTarget;
    private ITextEditTarget? _dragTarget;
    private bool _textRegionDragging;
    private Point _textDragStart;
    private double _textDragStartX, _textDragStartY;

    // Whether the pointer has moved past TextDragThreshold since PointerPressed. A plain
    // click (no real movement) must be a total no-op — see FinalizeTextDrag/AbortDrag.
    private bool _textDragMoved;

    // Pixel threshold before a press is treated as an actual drag rather than a click,
    // mirroring TimelineControl's ZoomCreateDragThreshold convention.
    private const double TextDragThreshold = 5.0;

    // Per-frame positioning cache for GetActiveEditTarget (Preview.FrameLayoutChanged /
    // ShowTextEditOverlay run every drawn frame) — reused across frames as long as the
    // previewed slide/overlay id hasn't changed, to avoid allocating a new
    // SlideTextEditTarget/OverlayTextEditTarget and re-running the PreviewSlide()/
    // PreviewOverlay() LINQ lookups on every Win2D draw callback. NEVER reused for a
    // gesture that is just starting (PointerPressed/EnterTextEdit use
    // GetActiveEditTargetForNewGesture instead) — OverlayTextEditTarget captures its
    // pre-gesture original text/position/anchor at construction time, so reusing a cached
    // instance from an earlier frame could restore a stale "original" if anything (e.g. the
    // properties pane) changed the overlay since the cache was last built.
    private ITextEditTarget? _cachedEditTarget;
    private string? _cachedEditTargetId;

    /// <summary>
    /// Canvas size the preview composes to: the scene's aspect ratio, not the raw capture size.
    /// </summary>
    /// <remarks>
    /// Everything the preview composites — video, text slides, and the crossfade that
    /// blends them — must share one canvas. The exporter already does this
    /// (<c>SegmentFrameComposer</c> works at its output dimensions); the preview used
    /// <c>project.Width/Height</c>, so a scene set to anything other than the capture ratio
    /// rendered slides at the wrong shape and jumped ratio for the length of a dissolve.
    /// </remarks>
    private (int Width, int Height) GetPreviewCanvasSize()
    {
        if (_compositorReady && _previewRenderer is { OutputWidth: > 0, OutputHeight: > 0 })
            return (_previewRenderer.OutputWidth, _previewRenderer.OutputHeight);

        var project = ProjectService.Instance.CurrentProject;
        int w = project?.Width > 0 ? project.Width : 1920;
        int h = project?.Height > 0 ? project.Height : 1080;

        // Fallback for when the compositor has not initialised yet: derive the canvas from
        // the scene ratio at the source height, matching how the compositor sizes it.
        var ratio = ProjectService.Instance.CurrentComposition?.AspectRatio ?? AspectRatio.Auto;
        var (ratioW, ratioH) = AspectRatioHelper.GetRatio(ratio);
        if (ratioW == 0 || ratioH == 0)
            return (w, h);

        int outW = (int)Math.Round(h * (double)ratioW / ratioH);
        return (Math.Max(2, outW & ~1), Math.Max(2, h & ~1));
    }

    private async Task RenderTextSlidePreviewAsync(TextSlideSegment slide, TimeSpan localOffset)
    {
        _textSlideRenderer ??= new TextSlideRenderer();

        // Pre-load the (image) background off the UI thread so the synchronous
        // RenderSlide call below never blocks on file I/O + GPU decode.
        await _textSlideRenderer.EnsureBackgroundLoadedAsync(slide);

        var (width, height) = GetPreviewCanvasSize();

        _previewSlideId = slide.Id;
        _previewOverlayId = null; // a slide always wins the shared canvas — see GetActiveEditTarget
        _previewFrameW = width;
        _previewFrameH = height;

        double progress = slide.Duration.TotalSeconds > 0
            ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
            : 0;

        try
        {
            // While editing, render background only — the editable TextBox shows the text.
            bool drawText = _editingTextId != slide.Id;
            var frame = _textSlideRenderer.RenderSlide(slide, progress, width, height, drawText);
            _lastRenderedFrameIndex = -1; // force redraw next time
            Preview.SetFrame(frame);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Text slide preview error: {ex.Message}");
        }

        ShowTextEditOverlay();
    }

    private async Task RenderVideoFrameAsync(TimeSpan sourcePosition, bool force)
    {
        if (_frameReader is null) return;

        int frameIndex = _frameReader.GetFrameIndex(sourcePosition);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourcePosition);
        if (bitmap is null)
        {
            // The decoder produced nothing. Whatever was presented last — often a text
            // slide the playhead has already left — stays on screen, so make sure the
            // next tick retries even if the playhead has not moved, and leave a trace.
            _lastRenderedFrameIndex = -1;
            Musio.Core.Diagnostics.DiagLog.Write("Editor",
                $"no decoded frame at {sourcePosition} (index {frameIndex}); preview is stale");
            return;
        }

        try
        {
            if (_compositorReady && _previewRenderer is not null && !_zoomRegionEditMode)
            {
                // Extract webcam frame for overlay
                await SetWebcamFrameForPreviewAsync(sourcePosition);

                var composed = _previewRenderer.RenderPreviewFrame(bitmap, sourcePosition);
                if (composed is not null)
                {
                    bitmap.Dispose();
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
                // Compositor declined this frame — fall through and show the raw bitmap,
                // which means it must still be alive here.
            }

            // Fallback: show raw frame without effects
            var device = CanvasDevice.GetSharedDevice();
            var renderTarget = new CanvasRenderTarget(device,
                bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.DrawImage(bitmap);
            }
            bitmap.Dispose();
            _lastRenderedFrameIndex = frameIndex;
            Preview.SetFrame(renderTarget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Preview frame error at {sourcePosition}: {ex.Message}");
            bitmap.Dispose();
        }
    }

    private string? PrimaryVideoPath => ProjectService.Instance.CurrentProject?.VideoFilePath;

    /// <summary>
    /// Renders a video-segment frame. Segments from the primary recording use the
    /// main frame reader/compositor; appended recordings use their own per-segment
    /// context so their frames, cursor, and webcam play correctly.
    /// </summary>
    private async Task RenderSegmentVideoAsync(VideoSegment seg, TimeSpan sourceTime, bool force)
    {
        if (string.Equals(seg.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            await RenderVideoFrameAsync(sourceTime, force);
            return;
        }

        var ctx = await GetOrBuildSegmentPreviewAsync(seg);
        if (ctx?.Reader is null) return;

        int frameIndex = ctx.Reader.GetFrameIndex(sourceTime);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await ctx.Reader.LoadFrameAtTimeAsync(sourceTime);
        if (bitmap is null)
        {
            _lastRenderedFrameIndex = -1;
            Musio.Core.Diagnostics.DiagLog.Write("Editor",
                $"no decoded frame for appended segment at {sourceTime} (index {frameIndex}); preview is stale");
            return;
        }

        try
        {
            if (ctx.Ready && ctx.Renderer is not null && !_zoomRegionEditMode)
            {
                if (ctx.Webcam is not null)
                {
                    try
                    {
                        var wf = await ExtractWebcamFrameAsync(ctx.Webcam, sourceTime, ctx.WebcamW, ctx.WebcamH);
                        if (wf is not null)
                        {
                            ctx.LastWebcamFrame?.Dispose();
                            ctx.LastWebcamFrame = wf;
                            ctx.Renderer.SetWebcamFrame(wf);
                        }
                    }
                    catch { }
                }

                var composed = ctx.Renderer.RenderPreviewFrame(bitmap, sourceTime);
                if (composed is not null)
                {
                    bitmap.Dispose();
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
                // Fall through to the raw-frame path, which still needs the bitmap.
            }

            // Fallback: raw frame
            var device = CanvasDevice.GetSharedDevice();
            var rt = new CanvasRenderTarget(device, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = rt.CreateDrawingSession()) ds.DrawImage(bitmap);
            bitmap.Dispose();
            _lastRenderedFrameIndex = frameIndex;
            Preview.SetFrame(rt);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] Appended segment render error: {ex.Message}");
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Longest source extent used by <paramref name="videoFilePath"/> anywhere on the
    /// timeline, so one segment's compositor timelines cover every OTHER segment cut from
    /// the same source file too. Mirrors <c>SegmentFrameComposer.ResolveSourceDuration</c>
    /// in the exporter exactly (see the call site in <see cref="GetOrBuildSegmentPreviewAsync"/>
    /// for why this matters for the appended-recording preview). The PRIMARY recording
    /// needs no equivalent here: its compositor is already initialized with
    /// <c>project.Duration</c> (see <see cref="RebuildPreviewRendererCoreAsync"/>), which by
    /// construction spans every primary-file segment.
    /// </summary>
    private static TimeSpan ResolveSegmentSourceDuration(
        TimelineModel model, string videoFilePath, TimeSpan fallback)
    {
        var longest = fallback;
        foreach (var candidate in model.Segments.OfType<VideoSegment>())
        {
            if (!string.Equals(candidate.VideoFilePath, videoFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var end = candidate.SourceStart + candidate.SourceDuration;
            if (end > longest) longest = end;
        }
        return longest;
    }

    private async Task<SegmentPreview?> GetOrBuildSegmentPreviewAsync(VideoSegment seg)
    {
        if (_segmentPreviews.TryGetValue(seg.Id, out var existing))
        {
            existing.LastUsed = ++_segmentPreviewUseCounter;
            return existing;
        }

        TrimSegmentPreviewCache();
        var ctx = new SegmentPreview { LastUsed = ++_segmentPreviewUseCounter };
        _segmentPreviews[seg.Id] = ctx; // insert early to avoid duplicate builds
        int generation = _segmentPreviewGeneration;

        // True once this build's entry has been dropped or replaced — by a cache clear, a
        // per-segment style rebuild, or page teardown — while it was awaiting. Publishing
        // onto it after that would leak a decoder nothing owns.
        bool Abandoned() =>
            generation != _segmentPreviewGeneration
            || !_segmentPreviews.TryGetValue(seg.Id, out var current)
            || !ReferenceEquals(current, ctx);

        // Tears down whatever this build managed to create. The clear that abandoned it
        // disposed an empty context, so everything built after that point is this build's
        // to release.
        SegmentPreview? Abandon()
        {
            DisposeOffUiThread(ctx.Reader);
            ctx.Reader = null;
            ctx.Dispose();
            return null;
        }

        try
        {
            int fps = seg.Fps > 0 ? seg.Fps : 30;
            var reader = await VideoFrameReader.OpenPreviewFromVideoPathAsync(
                seg.VideoFilePath,
                fps,
                _previewResolution.MaxWidth,
                _previewResolution.MaxHeight,
                SegmentPreviewCacheBudgetBytes);
            if (Abandoned())
            {
                DisposeOffUiThread(reader);
                return Abandon();
            }

            ctx.Reader = reader;
            if (ctx.Reader is null) return ctx;

            MouseRecordingData? mouseData = null;
            if (!string.IsNullOrEmpty(seg.CursorDataFilePath) && File.Exists(seg.CursorDataFilePath))
            {
                try { mouseData = MouseHookRecorder.LoadFromFile(seg.CursorDataFilePath); }
                catch { }
            }

            // An imported video has no cursor recording. Rather than skip the compositor for it
            // (which would drop background styling and, more importantly, any manual zoom the
            // user creates on it), fall back to an empty recording: FrameCompositor synthesizes
            // a static centre position from it, and HideCursorWhenNoSamples stops a fictional
            // cursor from being drawn at that centre — matching the export path
            // (SegmentFrameComposer.ApplyCursorAvailability).
            mouseData ??= new MouseRecordingData();

            int w = seg.SourceWidth > 0 ? seg.SourceWidth : 1920;
            int h = seg.SourceHeight > 0 ? seg.SourceHeight : 1080;
            int previewFps = Math.Min(fps, 30);

            var global = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
            var config = BuildSegmentConfig(global, seg, previewFps);

            if (!ProjectService.Instance.IsRestoredSource(seg.VideoFilePath))
            {
                // Same rule as the primary preview: these are first-open defaults, and a
                // restored project already carries the user's saved smoothing and zoom.
                config = config with
                {
                    SmoothingAlgorithm = SmoothingAlgorithm.ZeroPhaseSpring,
                    SmoothingStrength = SmoothingStrength.Smooth,
                    Zoom = new AutoZoomConfig { Enabled = true },
                };
            }

            if (!string.IsNullOrWhiteSpace(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath))
                config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };

            config = HideCursorWhenNoSamples(config, mouseData);

            try
            {
                var renderer = new PreviewRenderer();
                // The compositor is driven with ABSOLUTE source times
                // (SourceStart + localOffset), so its timelines must span the end of
                // the clip's source extent, not just the clip's length. Passing the
                // bare SourceDuration would drop cursor samples and auto-zoom for the
                // visible tail of any clip trimmed from the front. This must also cover
                // every OTHER segment cut from the same source file, not just this one:
                // once a transition's rolled OutgoingLocalOffset (T1) can advance an
                // outgoing segment past its own cut, a same-source split's outgoing side
                // maps into the range the NEXT segment already trimmed off — passing only
                // seg's own extent would truncate the compositor's cursor/auto-zoom
                // timelines right at that cut, freezing them in preview while export (which
                // already resolves the longest shared extent) keeps them going. Mirrors
                // SegmentFrameComposer.ResolveSourceDuration on the export path exactly.
                var sourceDuration = ResolveSegmentSourceDuration(
                    ViewModel.Model, seg.VideoFilePath, seg.SourceStart + seg.SourceDuration);
                await renderer.InitializeAsync(
                    mouseData, config, w, h, sourceDuration,
                    seg.MouseToVideoOffsetSeconds, seg.CropOffsetX, seg.CropOffsetY, seg.DpiScale);

                // Push this source's manual zooms onto its own compositor. Auto-zoom is rebuilt
                // from the recording inside InitializeAsync, but manual keyframes live in the
                // editor model and are the only zooms an imported (cursorless) clip can have, so
                // this is what makes a zoom created on it actually render.
                renderer.UpdateZoomKeyframes(ManualKeyframesForSource(seg.VideoFilePath));
                renderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
                SyncTextOverlaysToRenderer(renderer, seg.VideoFilePath);

                if (Abandoned())
                {
                    renderer.Dispose();
                    return Abandon();
                }

                ctx.Renderer = renderer;
                ctx.Ready = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EditorPage] Segment compositor init failed: {ex.Message}");
                ctx.Renderer?.Dispose();
                ctx.Renderer = null;
                ctx.Ready = false;
            }

            if (!string.IsNullOrWhiteSpace(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(seg.WebcamFilePath);
                    var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
                    if (Abandoned())
                        return Abandon();

                    var props = clip.GetVideoEncodingProperties();
                    ctx.WebcamW = (int)props.Width;
                    ctx.WebcamH = (int)props.Height;
                    ctx.Webcam = new Windows.Media.Editing.MediaComposition();
                    ctx.Webcam.Clips.Add(clip);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] GetOrBuildSegmentPreview failed: {ex.Message}");
        }

        return Abandoned() ? Abandon() : ctx;
    }

    private static async Task<CanvasBitmap?> ExtractWebcamFrameAsync(
        Windows.Media.Editing.MediaComposition comp, TimeSpan position, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var clamped = comp.Duration > TimeSpan.Zero && position > comp.Duration ? comp.Duration : position;
        using var thumb = await comp.GetThumbnailAsync(
            clamped, width, height, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var device = CanvasDevice.GetSharedDevice();
        using var stream = thumb.AsStream();
        var ras = stream.AsRandomAccessStream();
        try { return await CanvasBitmap.LoadAsync(device, ras); }
        finally { try { ras.Dispose(); } catch { } }
    }

    private async Task LoadWebcamCompositionAsync(Project project)
    {
        // Clear previous webcam state so stale resources don't persist
        // when loading a new project or one without a webcam file.
        _webcamComposition?.Clips.Clear();
        _webcamComposition = null;
        _lastWebcamFrame?.Dispose();
        _lastWebcamFrame = null;

        if (string.IsNullOrWhiteSpace(project.WebcamFilePath) || !File.Exists(project.WebcamFilePath))
            return;

        try
        {
            var webcamFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(project.WebcamFilePath);
            var webcamClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(webcamFile);
            var props = webcamClip.GetVideoEncodingProperties();
            _webcamWidth = (int)props.Width;
            _webcamHeight = (int)props.Height;
            _webcamComposition = new Windows.Media.Editing.MediaComposition();
            _webcamComposition.Clips.Add(webcamClip);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Failed to load webcam video: {ex.Message}");
        }
    }

    private async Task SetWebcamFrameForPreviewAsync(TimeSpan position)
    {
        if (_webcamComposition is null || _previewRenderer is null) return;

        // Independent camera track: when camera segments exist, the webcam overlay
        // is only shown while the (source) playhead is inside an enabled segment,
        // and the active segment's style override is applied. With no camera
        // segments, the legacy always-on global overlay behaviour is preserved.
        var model = ViewModel.Model;
        float fullscreenFactor = 0f;
        float overlayOpacity = 1f;
        if (model.CameraSegments.Count > 0)
        {
            var active = model.GetCameraSegmentAtSourceTime(position);
            if (active is null)
            {
                _previewRenderer.SetWebcamFrame(null);
                return;
            }
            _previewRenderer.UpdateWebcamStyle(
                active.ResolveStyle(ProjectService.Instance.CurrentComposition?.WebcamStyle));
            fullscreenFactor = active.ComputeFullscreenFactor(position);
            overlayOpacity = model.GetCameraOverlayOpacity(active, position);
        }
        _previewRenderer.SetWebcamFullscreenFactor(fullscreenFactor);
        _previewRenderer.SetWebcamOverlayOpacity(overlayOpacity);

        CanvasBitmap? webcamFrame = null;
        try
        {
            var clamped = position;
            if (_webcamComposition.Duration > TimeSpan.Zero && position > _webcamComposition.Duration)
                clamped = _webcamComposition.Duration;

            // Cap extraction size for preview — full native resolution is
            // unnecessarily heavy for a ~300px overlay during editor scrubbing.
            // When animating toward fullscreen, raise the cap so the enlarged
            // webcam isn't blurry as it covers the screen.
            float previewCap = (ProjectService.Instance.CurrentComposition?.WebcamStyle?.Size ?? 300f) * 1.5f;
            if (fullscreenFactor > 0f)
            {
                float outMax = Math.Max(_previewRenderer.OutputWidth, _previewRenderer.OutputHeight);
                previewCap = Math.Max(previewCap, fullscreenFactor * outMax);
            }
            int extractW = _webcamWidth;
            int extractH = _webcamHeight;
            float minDim = Math.Min(_webcamWidth, _webcamHeight);
            if (minDim > previewCap)
            {
                float scale = previewCap / minDim;
                extractW = Math.Max((int)Math.Ceiling(_webcamWidth * scale), 1);
                extractH = Math.Max((int)Math.Ceiling(_webcamHeight * scale), 1);
            }

            var thumbnail = await _webcamComposition.GetThumbnailAsync(
                clamped, extractW, extractH,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);

            var device = CanvasDevice.GetSharedDevice();
            var stream = thumbnail.AsStream();
            var ras = stream.AsRandomAccessStream();
            webcamFrame = await CanvasBitmap.LoadAsync(device, ras);

            // Dispose intermediate streams — ignore errors from WinRT stream flush
            try { ras.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
            try { thumbnail.Dispose(); } catch { }
        }
        catch { /* frame extraction failed — keep previous frame */ }

        if (webcamFrame is not null)
        {
            _lastWebcamFrame?.Dispose();
            _lastWebcamFrame = webcamFrame;
            _previewRenderer.SetWebcamFrame(_lastWebcamFrame);
        }
    }

    /// <summary>
    /// Maps an output (playhead) time to source time using the current timeline mapper.
    /// Falls back to identity mapping when no speed/cut edits are present.
    /// </summary>
    private TimeSpan MapToSourceTime(TimeSpan outputPosition)
    {
        var mapper = EnsureTimelineMapper();
        if (mapper is null) return outputPosition;

        int fps = _frameReader?.Fps ?? 30;
        int outputFrame = (int)(outputPosition.TotalSeconds * fps);
        double sourceSeconds = mapper.GetSourceTimeForOutputFrame(outputFrame);
        return TimeSpan.FromSeconds(sourceSeconds);
    }

    /// <summary>
    /// Loads per-file cursor data and audio waveforms for every appended (non-primary)
    /// video segment, and registers them with the timeline so each segment renders its
    /// OWN cursor/click/audio markers (positioned relative to the segment, so they move
    /// with it). The primary recording uses the model-level data directly.
    /// </summary>
    private async Task LoadAppendedTrackVisualsAsync()
    {
        var model = ViewModel.Model;
        var primary = PrimaryVideoPath;

        var segs = model.Segments.OfType<VideoSegment>()
            .Where(v => !string.IsNullOrEmpty(v.VideoFilePath) &&
                        !string.Equals(v.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.VideoFilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var seg in segs)
        {
            var visual = new Musio_App.Controls.TimelineControl.SegmentTrackVisual
            {
                MouseToVideoOffsetSeconds = seg.MouseToVideoOffsetSeconds,
                HasCamera = !string.IsNullOrEmpty(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath!),
            };

            // Cursor + click data
            if (!string.IsNullOrEmpty(seg.CursorDataFilePath) && File.Exists(seg.CursorDataFilePath!))
            {
                try { visual.Cursor = MouseHookRecorder.LoadFromFile(seg.CursorDataFilePath!); }
                catch { /* no cursor data for this recording */ }
            }

            // Auto-zoom keyframes from this recording's clicks, tagged with its file so
            // they render on its segment and are available like the primary's.
            GenerateAppendedZoomKeyframes(seg, visual.Cursor);

            // Audio waveforms (system + mic), spanning the file's audio duration
            var (sys, mic, durSec) = await GenerateFileWaveformsAsync(seg.AudioFilePaths);
            visual.SystemWaveform = sys;
            visual.MicWaveform = mic;
            visual.WaveformDurationSeconds = durSec > 0
                ? durSec
                : (seg.SourceDuration.TotalSeconds > 0 ? seg.SourceDuration.TotalSeconds : seg.Duration.TotalSeconds);

            Timeline.SetSegmentTrackVisual(seg.VideoFilePath!, visual);
        }
        Timeline.Refresh();
    }

    /// <summary>
    /// True when the model already has at least one zoom keyframe tagged with
    /// <paramref name="sourceVideoFilePath"/> (null means the primary recording).
    /// Shared by the primary and appended auto-zoom generation so both apply the
    /// same "generate once, then preserve" rule (see remarks on the primary call site
    /// in <see cref="InitializePreviewAsync"/> and on <see cref="GenerateAppendedZoomKeyframes"/>).
    /// </summary>
    private bool ZoomKeyframesExistForSource(string? sourceVideoFilePath) =>
        ViewModel.Model.ZoomKeyframes.Any(k =>
            string.Equals(k.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Generates auto-zoom keyframes for an appended video segment from its click
    /// events, tagged with the segment's source file so they map into its output range.
    /// Mirrors the primary recording's idempotent generation (see
    /// <see cref="InitializePreviewAsync"/>): <see cref="LoadAppendedTrackVisualsAsync"/>
    /// re-runs this on every editor page (re)construction — including additional
    /// "Record More" cycles and duplicate-path reloads — so this must NOT
    /// unconditionally replace existing keyframes for the file. Doing so previously wiped
    /// manual edits and resurrected auto-zooms the user deleted. Generate only when this
    /// source has no keyframes yet, and skip any click tracked in SuppressedClickTicks.
    /// </summary>
    private void GenerateAppendedZoomKeyframes(VideoSegment seg, MouseRecordingData? mouse)
    {
        // Only a source that came from the package carries saved zoom choices. A
        // recording appended after opening a project is new and still needs its
        // auto-zooms generated.
        if (ProjectService.Instance.IsRestoredSource(seg.VideoFilePath))
            return;

        if (ZoomKeyframesExistForSource(seg.VideoFilePath))
            return; // already generated (or user-edited) for this source — preserve as-is

        if (mouse is null || mouse.Clicks.Count == 0 || mouse.TickFrequency <= 0) return;

        int sw = seg.SourceWidth > 0 ? seg.SourceWidth : 1920;
        int sh = seg.SourceHeight > 0 ? seg.SourceHeight : 1080;
        int cox = seg.CropOffsetX;
        int coy = seg.CropOffsetY;
        double offset = seg.MouseToVideoOffsetSeconds;
        double maxSrc = seg.SourceDuration.TotalSeconds;
        var keyframes = ViewModel.Model.ZoomKeyframes;

        foreach (var click in mouse.Clicks.Where(c => c.IsDown))
        {
            // Respect deletions: never re-add an auto-zoom the user removed.
            if (ViewModel.Model.SuppressedClickTicks.Contains(click.TimestampTicks))
                continue;

            double t = (click.TimestampTicks - mouse.StartTimestampTicks) / mouse.TickFrequency - offset;
            if (t < 0) continue;
            if (maxSrc > 0 && t > maxSrc) continue;
            keyframes.Add(new Musio.Core.Timeline.ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(t),
                ZoomLevel = 2.0,
                CenterX = Math.Clamp((click.X - cox) / (double)sw, 0, 1),
                CenterY = Math.Clamp((click.Y - coy) / (double)sh, 0, 1),
                SourceClickTicks = click.TimestampTicks,
                SourceVideoFilePath = seg.VideoFilePath,
            });
        }
    }

    /// <summary>
    /// Generates system + mic waveform peak arrays for a recording's audio files,
    /// each spanning the full audio file, plus the (max) audio duration in seconds.
    /// </summary>
    private static async Task<(float[]? Sys, float[]? Mic, double DurationSec)> GenerateFileWaveformsAsync(
        IReadOnlyList<string> audioFilePaths)
    {
        const int peaks = 1000;
        return await Task.Run(() =>
        {
            float[]? sys = null, mic = null;
            double dur = 0;

            var valid = audioFilePaths.Where(File.Exists).ToList();
            if (valid.Count == 0) return (sys, mic, dur);

            var systemPath = valid.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("system_", StringComparison.OrdinalIgnoreCase));
            var micPath = valid.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("mic_", StringComparison.OrdinalIgnoreCase));

            if (systemPath is not null)
            {
                try { using var r = new NAudio.Wave.AudioFileReader(systemPath); dur = Math.Max(dur, r.TotalTime.TotalSeconds); } catch { }
                try { sys = AudioWaveformGenerator.GenerateWaveform(systemPath, peaks); } catch { }
            }
            if (micPath is not null)
            {
                try { using var r = new NAudio.Wave.AudioFileReader(micPath); dur = Math.Max(dur, r.TotalTime.TotalSeconds); } catch { }
                try { mic = AudioWaveformGenerator.GenerateWaveform(micPath, peaks); } catch { }
            }

            return (sys, mic, dur);
        });
    }

    /// <summary>
    /// Generates filmstrip thumbnails for the primary recording and then for each
    /// distinct appended recording's source file, so every video segment shows its
    /// own frames. Runs sequentially so the shared generation id never cancels an
    /// earlier still-running pass.
    /// </summary>
    private async Task GenerateAllTimelineThumbnailsAsync(string? primaryPath, int fps)
    {
        // Fire-and-forget, so anything thrown here would vanish and leave the track blank
        // with no indication why.
        try
        {
            if (!string.IsNullOrEmpty(primaryPath))
            {
                _thumbnailsInFlightForPath = primaryPath;
                try
                {
                    await GenerateTimelineThumbnailsAsync(primaryPath, isPrimary: true, fps);
                }
                finally
                {
                    if (string.Equals(_thumbnailsInFlightForPath, primaryPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _thumbnailsInFlightForPath = null;
                    }
                }
            }

            await GenerateAppendedThumbnailsAsync(primaryPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Filmstrip generation failed for '{primaryPath}': {ex}");
        }
    }

    /// <summary>
    /// Generates per-file thumbnails for every appended (non-primary) video segment
    /// file referenced by the timeline.
    /// </summary>
    private async Task GenerateAppendedThumbnailsAsync(string? primaryPath)
    {
        var model = ViewModel.Model;
        var files = model.Segments.OfType<VideoSegment>()
            .Where(v => !string.IsNullOrEmpty(v.VideoFilePath) &&
                        !string.Equals(v.VideoFilePath, primaryPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.VideoFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Fps: g.Select(v => v.Fps).FirstOrDefault(f => f > 0)))
            .ToList();

        foreach (var (file, segmentFps) in files)
        {
            if (!File.Exists(file)) continue;

            // Claim the file before awaiting: two overlapping initialisations would
            // otherwise both build the same strip, and the loser's bitmaps would be handed
            // to the timeline after the winner's and leak the ones it replaced.
            if (!_thumbnailsDoneForFiles.Add(file)) continue;

            try
            {
                bool applied = await GenerateTimelineThumbnailsAsync(
                    file, isPrimary: false, segmentFps > 0 ? segmentFps : 30);

                // A pass cancelled by a newer generation applied nothing, so the claim must
                // be released or this source would never get a strip again.
                if (!applied) _thumbnailsDoneForFiles.Remove(file);
            }
            catch (Exception ex)
            {
                _thumbnailsDoneForFiles.Remove(file);
                System.Diagnostics.Debug.WriteLine(
                    $"[EditorPage] Appended thumbnail generation failed for {file}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Builds the filmstrip for one source video.
    /// </summary>
    /// <remarks>
    /// Thumbnails come from <see cref="VideoThumbnailExtractor"/> rather than the preview's
    /// frame reader. Sparse thumbnail access is the worst case for a single-position
    /// decoder — a seek per tile, measured around 334 ms each with roughly a quarter
    /// yielding no frame, while also competing with the preview for the same file. The
    /// batch extractor measured ~15 ms per tile with none missing.
    /// </remarks>
    /// <returns>
    /// <c>true</c> when a strip was handed to the timeline; <c>false</c> when the source was
    /// undecodable or the pass was superseded by a newer generation, so the caller can let
    /// it be retried.
    /// </returns>
    private async Task<bool> GenerateTimelineThumbnailsAsync(string filePath, bool isPrimary, int fps)
    {
        var generationId = ++_thumbnailGenerationId;

        // Thumbnail size: match video track height (60px row minus padding)
        const int thumbH = 52;

        var device = CanvasDevice.GetSharedDevice();
        var strip = await VideoThumbnailExtractor.ExtractAsync(filePath, thumbH, device);

        // The MP4 is unreadable — unfinalized, or finalization failed. The captured JPEGs
        // are still there in exactly that case and the preview is already using them, so
        // the filmstrip must not be the one surface that gives up.
        strip ??= await VideoThumbnailExtractor.ExtractFromCapturedFramesAsync(
            filePath, fps, thumbH, device);

        if (strip is null || generationId != _thumbnailGenerationId)
        {
            if (strip is not null)
                foreach (var t in strip.Thumbnails) t?.Dispose();
            return false;
        }

        // A tile that could not be decoded would leave a hole in the strip. Repeat the
        // nearest earlier tile instead — slightly stale footage reads as continuous, an
        // empty slot reads as a broken timeline.
        var thumbnails = strip.Thumbnails;
        int thumbW = Math.Max(1, (int)(thumbH * strip.AspectRatio));
        for (int i = 0; i < thumbnails.Length; i++)
        {
            if (thumbnails[i] is not null || i == 0) continue;
            if (thumbnails[i - 1] is not { } previous) continue;

            var repeat = new CanvasRenderTarget(device, thumbW, thumbH, 96);
            using (var session = repeat.CreateDrawingSession())
                session.DrawImage(previous, new Rect(0, 0, thumbW, thumbH));
            thumbnails[i] = repeat;
        }

        var owned = new CanvasBitmap[thumbnails.Length];
        for (int i = 0; i < thumbnails.Length; i++) owned[i] = thumbnails[i]!;

        // TimelineControl takes ownership of the bitmaps.
        if (isPrimary)
        {
            Timeline.SetThumbnails(owned, strip.IntervalSeconds, strip.AspectRatio, filePath);
            // Only a pass that ran to completion may mark the strip done; a cancelled one
            // would otherwise pin a half-filled filmstrip permanently.
            _thumbnailsCompletedForPath = filePath;
        }
        else
        {
            Timeline.SetThumbnailsForFile(filePath, owned, strip.IntervalSeconds, strip.AspectRatio);
        }

        return true;
    }

    private TimelineMapper? EnsureTimelineMapper()
    {
        if (_timelineMapper is not null) return _timelineMapper;

        int fps = _frameReader?.Fps ?? ProjectService.Instance.CurrentProject?.Fps ?? 30;
        if (fps <= 0) fps = 30;

        _timelineMapper = new TimelineMapper(ViewModel.Model, fps);
        return _timelineMapper;
    }

    /// <summary>
    /// Returns the effective preview duration, accounting for speed segments.
    /// </summary>
    private TimeSpan GetMappedDuration()
    {
        var mapper = EnsureTimelineMapper();
        return mapper?.EffectiveDuration ?? ViewModel.Model.EffectiveDuration;
    }

    /// <summary>
    /// Invalidates the preview state after timeline edits (speed, zoom, cut, undo/redo).
    /// Rebuilds the timeline mapper, syncs zoom keyframes to the compositor, and forces a re-render.
    /// </summary>
    private void InvalidatePreview()
    {
        _timelineMapper = null;
        _lastRenderedFrameIndex = -1;

        Preview.Duration = GetMappedDuration();

        // Sync user-added zoom keyframes to the compositor
        if (_compositorReady && _previewRenderer is not null)
        {
            SyncZoomStateToRenderer();
        }

        // Appended recordings and imported videos each drive their own segment renderer, so
        // their zooms have to be synced separately from the primary compositor above.
        SyncSegmentZoomStateToRenderers();

        Timeline.Refresh();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    /// <summary>
    /// Pushes the model's zoom state — manual keyframes plus the suppressed auto-zoom click
    /// ticks — onto the primary preview renderer.
    /// </summary>
    /// <remarks>
    /// This must run after every renderer rebuild as well as on every model change. A rebuilt
    /// <see cref="FrameCompositor"/> regenerates auto-zoom from the raw mouse recording, so
    /// skipping the suppressed ticks brings back auto-zoom segments the user deleted, and
    /// skipping the manual list (in particular when it is empty) leaves the previous zooms in
    /// place. Both show up as zoom happening in the preview with no matching timeline segment.
    /// </remarks>
    private void SyncZoomStateToRenderer()
    {
        if (_previewRenderer is null) return;

        // Only the PRIMARY recording's manual keyframes drive the primary renderer;
        // appended recordings' keyframes belong to their own segment/source space.
        _previewRenderer.UpdateZoomKeyframes(ManualKeyframesForSource(null));
        _previewRenderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
        SyncTextOverlaysToRenderer(_previewRenderer, null);
    }

    /// <summary>
    /// Pushes the text overlays that belong to <paramref name="sourceVideoFilePath"/>
    /// (null = the primary recording) onto <paramref name="renderer"/>'s compositor, using
    /// the exact same <see cref="SegmentFrameComposer.SelectTextOverlays"/> ownership rule
    /// export uses, so preview and export always agree on which overlays a source shows.
    /// Called alongside every zoom-keyframe sync above (renderer rebuilds, undo/redo, and
    /// per-segment renderer creation) since overlays need the identical refresh cadence.
    /// A no-op when there is no primary video to resolve the "primary" case against.
    /// </summary>
    private void SyncTextOverlaysToRenderer(PreviewRenderer renderer, string? sourceVideoFilePath)
    {
        var videoFilePath = sourceVideoFilePath ?? PrimaryVideoPath;
        if (string.IsNullOrEmpty(videoFilePath)) return;

        renderer.UpdateTextOverlays(SegmentFrameComposer.SelectTextOverlays(ViewModel.Model, videoFilePath));
    }

    /// <summary>
    /// The manual zoom keyframes that belong to a single source: the primary recording when
    /// <paramref name="sourceVideoFilePath"/> is null, otherwise the appended recording or
    /// imported video with that path. Each source composites through its own renderer, so its
    /// keyframes must be matched by <see cref="ZoomKeyframe.SourceVideoFilePath"/> and never
    /// leak onto another source's compositor.
    /// </summary>
    private List<ZoomKeyframe> ManualKeyframesForSource(string? sourceVideoFilePath) =>
        [.. ViewModel.Model.ZoomKeyframes.Where(k =>
            k.IsManual &&
            string.Equals(k.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Pushes each already-built appended/imported segment renderer its OWN source's manual
    /// zoom keyframes plus the shared suppressed-click set. The primary renderer is handled by
    /// <see cref="SyncZoomStateToRenderer"/>; appended and imported segments composite through
    /// their own <see cref="PreviewRenderer"/>, so a zoom created or region-edited on one of
    /// them only shows in the preview once its keyframes reach that renderer.
    /// </summary>
    private void SyncSegmentZoomStateToRenderers()
    {
        foreach (var (segmentId, ctx) in _segmentPreviews)
        {
            if (ctx.Renderer is null) continue;
            var seg = ViewModel.Model.Segments.OfType<VideoSegment>()
                .FirstOrDefault(v => v.Id == segmentId);
            if (seg is null) continue;

            ctx.Renderer.UpdateZoomKeyframes(ManualKeyframesForSource(seg.VideoFilePath));
            ctx.Renderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
            SyncTextOverlaysToRenderer(ctx.Renderer, seg.VideoFilePath);
        }
    }

    /// <summary>
    /// Resolves the video segment that owns a zoom whose source path is
    /// <paramref name="sourceVideoFilePath"/> (null = the primary recording). Used to map a
    /// zoom's normalised centre against the correct source dimensions.
    /// </summary>
    private VideoSegment? SegmentForSource(string? sourceVideoFilePath)
    {
        var target = sourceVideoFilePath ?? PrimaryVideoPath;
        if (target is null) return null;
        return ViewModel.Model.Segments.OfType<VideoSegment>()
            .FirstOrDefault(v => string.Equals(v.VideoFilePath, target, StringComparison.OrdinalIgnoreCase));
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_zoomRegionEditMode)
                ExitZoomRegionEditMode();

            // Only clear zoom selection if the selected segment was removed
            if (Timeline.SelectedZoomKeyframeId is { } id &&
                !ViewModel.Model.ZoomKeyframes.Any(k => k.Id == id))
            {
                Timeline.ClearZoomSelection();
            }

            Timeline.ClearClipSelection();

            // Re-sync the transition pane to whatever the model holds post-undo/redo — without
            // this, Ctrl+Z after a transition edit leaves the pane showing the value that was
            // just undone, and any further edit from the pane would be computed from a value
            // the user isn't actually looking at. If the boundary itself no longer exists
            // (e.g. undoing/redoing a segment add/remove/reorder), clear the selection instead
            // of leaving the pane referencing a stale segment id.
            if (_selectedTransitionId is { } transitionId)
            {
                var (incoming, outgoing) = GetTransitionBoundarySegments(transitionId);
                if (incoming is null || outgoing is null)
                    Timeline.ClearTransitionSelection();
                else
                    SyncTransitionUI(transitionId);
            }

            UpdateZoomPanelVisibility();
            UpdateSpeedPanelVisibility();
            Timeline.Refresh();
            InvalidatePreview();
        });
    }

    private bool _suppressSpeedApply;

    private void OnVideoClipSelected(object? sender, int? clipIndex)
    {
        ViewModel.SelectedClipIndex = clipIndex;
        UpdateSpeedPanelVisibility();

        // Hide text slide panel when a video clip is selected
        if (clipIndex is not null)
        {
            _selectedTextSlideId = null;
            HideTextSlidePanel();
        }

        // Sync combo to match selected clip's current speed
        if (clipIndex is { } idx && idx >= 0 && idx < ViewModel.Model.Clips.Count)
        {
            var clip = ViewModel.Model.Clips[idx];
            _suppressSpeedApply = true;
            for (int i = 0; i < SpeedComboBox.Items.Count; i++)
            {
                if (SpeedComboBox.Items[i] is ComboBoxItem item &&
                    double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double s) &&
                    Math.Abs(s - clip.SpeedFactor) < 0.01)
                {
                    SpeedComboBox.SelectedIndex = i;
                    break;
                }
            }
            _suppressSpeedApply = false;
        }
        else
        {
            // Reset to 1x when deselected
            _suppressSpeedApply = true;
            SpeedComboBox.SelectedIndex = 2;
            _suppressSpeedApply = false;
        }
    }

    private void OnSegmentSelected(object? sender, string? segmentId)
    {
        _selectedPrimarySegmentId = segmentId;

        // Show the text slide panel only when a text slide is selected; for a
        // video (or no) selection, treat the slide id as cleared.
        var slide = segmentId is null
            ? null
            : ViewModel.Model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == segmentId);

        _selectedTextSlideId = slide?.Id;
        if (slide is not null)
            ShowTextSlidePanel(slide);
        else
            HideTextSlidePanel();

        // Reflect the selected video segment's effective frame style + cursor in the
        // flyout controls, so editing per-segment style starts from its current state.
        if (SelectedVideoSegment is { } vseg)
        {
            var global = ProjectService.Instance.CurrentComposition;
            if (global is not null)
            {
                SyncStyleControlsToConfig(vseg.FrameStyleOverride ?? global.Background);
                SyncCursorControlsToConfig(vseg.CursorStyleOverride ?? global.Cursor);
            }
        }
    }

    /// <summary>Id of the currently selected primary-track segment (video or text slide).</summary>
    private string? _selectedPrimarySegmentId;

    private void OnSegmentMoveRequested(object? sender, (string Id, int TargetIndex) e)
    {
        var operation = new MoveSegmentOperation(e.Id, e.TargetIndex);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectSegment(e.Id);
    }

    private void OnSegmentTrimRequested(object? sender, (string Id, bool FromStart, TimeSpan NewDuration) e)
    {
        var operation = new TrimSegmentEdgeOperation(e.Id, e.FromStart, e.NewDuration);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectSegment(e.Id);
    }

    // ── Camera track handlers ──

    private string? _selectedCameraSegmentId;

    private void OnCameraSegmentSelected(object? sender, string? segmentId)
    {
        // Selection is tracked by the control; property editing is via the model/ops.
        _selectedCameraSegmentId = segmentId;
        SyncCameraSegmentUI(segmentId);
    }

    private void SyncCameraSegmentUI(string? segmentId)
    {
        if (CameraFullscreenPanel is null) return;

        var seg = segmentId is null
            ? null
            : ViewModel.Model.CameraSegments.FirstOrDefault(s => s.Id == segmentId);

        if (seg is null)
        {
            CameraFullscreenPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressWebcamEvents = true;
        CameraFullscreenToggle.IsOn = seg.FullscreenEnabled;
        CameraFullscreenModeCombo.SelectedIndex = seg.FullscreenMode == CameraFullscreenMode.Reveal ? 1 : 0;
        _suppressWebcamEvents = false;
        CameraFullscreenPanel.Visibility = Visibility.Visible;
        UpdateCameraFullscreenModeUI(seg.FullscreenEnabled, seg.FullscreenMode);

        // Surface the video panel so the segment's settings are immediately editable.
        PropertiesPanel.ShowPane(PropertyPaneKind.Video);
    }

    private void UpdateCameraFullscreenModeUI(bool fullscreenEnabled, CameraFullscreenMode mode)
    {
        if (CameraFullscreenModeCombo is null || CameraFullscreenHint is null) return;
        CameraFullscreenModeCombo.Visibility = fullscreenEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!fullscreenEnabled)
        {
            CameraFullscreenHint.Text =
                "The camera fades in and out smoothly at the start and end of the segment.";
        }
        else if (mode == CameraFullscreenMode.Reveal)
        {
            CameraFullscreenHint.Text =
                "The camera stays full screen, then shrinks to the overlay at the end, revealing the video underneath.";
        }
        else
        {
            CameraFullscreenHint.Text =
                "The camera grows to fill the screen, holds, then shrinks back to the overlay.";
        }
    }

    private void CameraFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (_selectedCameraSegmentId is null) return;

        ViewModel.UndoRedoManager.Execute(new UpdateCameraSegmentPropertiesOperation(
            _selectedCameraSegmentId, fullscreenEnabled: CameraFullscreenToggle.IsOn));

        var mode = CameraFullscreenModeCombo.SelectedIndex == 1
            ? CameraFullscreenMode.Reveal : CameraFullscreenMode.Highlight;
        UpdateCameraFullscreenModeUI(CameraFullscreenToggle.IsOn, mode);

        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void CameraFullscreenModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (_selectedCameraSegmentId is null) return;
        if (CameraFullscreenModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        var mode = tag == "Reveal" ? CameraFullscreenMode.Reveal : CameraFullscreenMode.Highlight;
        ViewModel.UndoRedoManager.Execute(new UpdateCameraSegmentPropertiesOperation(
            _selectedCameraSegmentId, fullscreenMode: mode));

        UpdateCameraFullscreenModeUI(CameraFullscreenToggle.IsOn, mode);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void OnCameraSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End) e)
    {
        var operation = new AddCameraSegmentOperation(
            e.Start, e.End - e.Start, ProjectService.Instance.CurrentProject?.WebcamFilePath);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectedCameraSegmentId = operation.CreatedId;
        _selectedCameraSegmentId = operation.CreatedId;
        SyncCameraSegmentUI(operation.CreatedId);
    }

    private void OnCameraSegmentMoved(object? sender, (string Id, TimeSpan NewStart) e)
    {
        ViewModel.UndoRedoManager.Execute(new MoveCameraSegmentOperation(e.Id, e.NewStart));
    }

    private void OnCameraSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e)
    {
        ViewModel.UndoRedoManager.Execute(new TrimCameraSegmentOperation(e.Id, e.IsStartEdge, e.NewEdgeTime));
    }

    private void OnCameraSegmentRemoveRequested(object? sender, string segmentId)
    {
        DeleteCameraSegment(segmentId);
    }

    private void DeleteCameraSegment(string segmentId)
    {
        ViewModel.UndoRedoManager.Execute(new RemoveCameraSegmentOperation(segmentId));
        Timeline.ClearCameraSelection();
        _selectedCameraSegmentId = null;
        SyncCameraSegmentUI(null);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void CameraDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCameraSegmentId is { } id)
            DeleteCameraSegment(id);
    }

    // ── Text overlay track handlers ──

    private string? _selectedTextOverlayId;
    private bool _suppressOverlayEvents;

    private void OnTextOverlaySelected(object? sender, string? overlayId)
    {
        // Finalize any in-flight drag/edit against whatever overlay WAS selected BEFORE
        // adopting the new selection. Both CommitPosition and CommitText re-sync the
        // properties pane for the overlay they belong to, so committing after the new
        // selection had been synced would repaint the pane with the OLD overlay's values
        // while the NEW one is selected — and the next property edit would then write
        // those stale values onto the new overlay.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        CommitActiveTextEditIfAny();

        // Selection is tracked by the control; property editing is via the model/ops.
        _selectedTextOverlayId = overlayId;
        SyncTextOverlayUI(overlayId);

        // Recompute which overlay (if any) the shared edit canvas should track RIGHT NOW,
        // rather than waiting for the next rendered frame (UpdateOverlayEditPreview
        // otherwise only runs from RenderFrameAtAsync) — otherwise the PREVIOUSLY selected
        // overlay stays draggable/double-click-editable on the preview until playback or a
        // seek happens to trigger a re-render.
        RefreshOverlayEditPreviewForCurrentPlayhead();
    }

    /// <summary>
    /// Resolves the video source + source-time under the current playhead the same way
    /// <see cref="RenderFrameAtAsync"/> does, then calls <see cref="UpdateOverlayEditPreview"/>
    /// with it — reusing (rather than duplicating) that method's decision of whether the
    /// selected overlay is actually active there. Used by <see cref="OnTextOverlaySelected"/>
    /// so a selection change updates the shared edit canvas immediately instead of waiting
    /// for the next rendered frame. A segment kind other than <see cref="VideoSegment"/>
    /// (e.g. a text slide) has no video — and therefore no overlay — underneath it.
    /// </summary>
    private void RefreshOverlayEditPreviewForCurrentPlayhead()
    {
        var position = Preview.PlayheadPosition;
        var model = ViewModel.Model;

        if (model.Segments.Count > 0)
        {
            var (segment, localOffset) = model.GetSegmentAtTime(position);
            if (segment is VideoSegment videoSeg)
            {
                var sourceInSeg = videoSeg.SourceStart +
                    TimeSpan.FromTicks((long)(localOffset.Ticks * videoSeg.SpeedFactor));
                UpdateOverlayEditPreview(videoSeg.VideoFilePath, sourceInSeg);
            }
            else
            {
                _previewOverlayId = null;
                ShowTextEditOverlay();
            }
            return;
        }

        UpdateOverlayEditPreview(PrimaryVideoPath, MapToSourceTime(position));
    }

    private TextOverlaySegment? SelectedTextOverlay() =>
        _selectedTextOverlayId is null ? null : ViewModel.Model.TextOverlays
            .FirstOrDefault(o => o.Id == _selectedTextOverlayId);

    /// <summary>
    /// Pushes every property of the overlay identified by <paramref name="overlayId"/> onto
    /// its pane controls (or hides the pane entirely if the overlay no longer exists), then
    /// reveals the pane. Mirrors <see cref="SyncCameraSegmentUI"/>/<see cref="ShowTextSlidePanel"/>.
    /// </summary>
    private void SyncTextOverlayUI(string? overlayId)
    {
        if (OverlayTextBox is null) return;

        var overlay = overlayId is null
            ? null
            : ViewModel.Model.TextOverlays.FirstOrDefault(o => o.Id == overlayId);

        if (overlay is null)
        {
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextOverlay, false);
            return;
        }

        BuildOverlayPresetsIfNeeded();

        _suppressOverlayEvents = true;

        // Presets are a one-shot action, not a persisted field — clear any lingering tile
        // selection so switching overlays (or editing controls directly) never leaves a
        // stale preset looking selected. Guarded above/below by _suppressOverlayEvents since
        // this re-raises OverlayPreset_SelectionChanged.
        OverlayPresets.SelectedItem = null;

        OverlayTextBox.Text = overlay.Text;
        OverlayDurationBox.Value = overlay.Duration.TotalSeconds;
        OverlayFontSizeBox.Value = overlay.FontSize;

        OverlayBoldToggle.IsChecked = overlay.IsBold;
        OverlayItalicToggle.IsChecked = overlay.IsItalic;
        OverlayAlignSegmented.SelectedIndex = overlay.TextAlignment switch
        {
            SlideTextAlignment.Left => 0,
            SlideTextAlignment.Right => 2,
            _ => 1,
        };

        SetOverlayFontSelection(overlay.FontFamily);

        var animName = overlay.Animation.ToString();
        for (int i = 0; i < OverlayAnimationCombo.Items.Count; i++)
        {
            if (OverlayAnimationCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == animName)
            {
                OverlayAnimationCombo.SelectedIndex = i;
                break;
            }
        }

        UpdateSlideColorSwatch(OverlayTextColorSwatch, OverlayTextColorText, OverlayTextColorPicker, overlay.TextColor);
        UpdateSlideColorSwatch(OverlayBgColorSwatch, OverlayBgColorText, OverlayBgColorPicker, overlay.BackgroundColor);
        UpdateSlideColorSwatch(OverlayOutlineColorSwatch, OverlayOutlineColorText, OverlayOutlineColorPicker, overlay.OutlineColor);
        UpdateSlideColorSwatch(OverlayAccentColorSwatch, OverlayAccentColorText, OverlayAccentColorPicker, overlay.AccentColor);

        // Anchor radios (nine-point grid) + the custom-position hint
        bool isCustom = overlay.Anchor == TextOverlayAnchor.Custom;
        var anchorName = overlay.Anchor.ToString();
        foreach (var child in OverlayAnchorGrid.Children)
        {
            if (child is RadioButton rb)
                rb.IsChecked = !isCustom && rb.Tag as string == anchorName;
        }
        OverlayCustomPositionHint.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        OverlayWidthSlider.Value = overlay.WidthFraction * 100.0;
        PropertiesPanel.TextOverlay.OverlayHeightSlider.Value = overlay.HeightFraction * 100.0;
        OverlayMarginSlider.Value = overlay.MarginFraction * 100.0;

        var bgTypeName = overlay.Background.ToString();
        for (int i = 0; i < OverlayBgTypeCombo.Items.Count; i++)
        {
            if (OverlayBgTypeCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == bgTypeName)
            {
                OverlayBgTypeCombo.SelectedIndex = i;
                break;
            }
        }

        OverlayBgOpacitySlider.Value = overlay.BackgroundOpacity * 100.0;
        OverlayCornerRadiusSlider.Value = overlay.CornerRadius;
        OverlayPaddingSlider.Value = overlay.PaddingScale * 100.0;

        OverlayBlurAmountSlider.Value = overlay.BlurAmount;
        OverlayBlurTintSlider.Value = overlay.BlurTintOpacity * 100.0;

        var scrimDirName = overlay.ScrimDirection.ToString();
        for (int i = 0; i < OverlayScrimDirectionCombo.Items.Count; i++)
        {
            if (OverlayScrimDirectionCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == scrimDirName)
            {
                OverlayScrimDirectionCombo.SelectedIndex = i;
                break;
            }
        }
        OverlayScrimStrengthSlider.Value = overlay.ScrimStrength * 100.0;

        OverlayOutlineWidthSlider.Value = overlay.OutlineWidth;
        OverlayShadowStrengthSlider.Value = overlay.ShadowStrength * 100.0;

        OverlayAccentThicknessSlider.Value = overlay.AccentThickness;
        var accentSideName = overlay.AccentSide.ToString();
        for (int i = 0; i < OverlayAccentSideCombo.Items.Count; i++)
        {
            if (OverlayAccentSideCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == accentSideName)
            {
                OverlayAccentSideCombo.SelectedIndex = i;
                break;
            }
        }

        OverlayEnabledToggle.IsOn = overlay.Enabled;

        UpdateOverlayBackgroundPanels(overlay.Background);

        _suppressOverlayEvents = false;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextOverlay, true);
        PropertiesPanel.ShowPane(PropertyPaneKind.TextOverlay);
    }

    /// <summary>
    /// Shows/hides each background sub-panel for the selected <see cref="TextOverlayBackground"/>.
    /// <see cref="OverlayBoxPanel"/> (color/opacity/corner-radius/padding) backs a filled box
    /// and is shared by Solid, Blur and AccentBar; the other panels each add their own
    /// mode-specific controls on top of it (GradientScrim/OutlineShadow stand alone since
    /// those modes draw no filled box).
    /// </summary>
    private void UpdateOverlayBackgroundPanels(TextOverlayBackground background)
    {
        OverlayBoxPanel.Visibility =
            background is TextOverlayBackground.Solid or TextOverlayBackground.Blur or TextOverlayBackground.AccentBar
                ? Visibility.Visible : Visibility.Collapsed;
        OverlayBlurPanel.Visibility = background == TextOverlayBackground.Blur ? Visibility.Visible : Visibility.Collapsed;
        OverlayScrimPanel.Visibility = background == TextOverlayBackground.GradientScrim ? Visibility.Visible : Visibility.Collapsed;
        OverlayOutlinePanel.Visibility = background == TextOverlayBackground.OutlineShadow ? Visibility.Visible : Visibility.Collapsed;
        OverlayAccentPanel.Visibility = background == TextOverlayBackground.AccentBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTextOverlayCreated(object? sender, (TimeSpan Start, TimeSpan End, string? SourceVideoFilePath) e)
    {
        var operation = new AddTextOverlayOperation(e.Start, e.End - e.Start, sourceVideoFilePath: e.SourceVideoFilePath);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectedTextOverlayId = operation.CreatedId;
        _selectedTextOverlayId = operation.CreatedId;
        SyncTextOverlayUI(operation.CreatedId);

        // Same reason as the Insert-menu path: an overlay is invisible on its first frame,
        // so drop the playhead into the middle of the range the user just marked out.
        SeekPastOverlayEntrance(e.End - e.Start);

        RefreshOverlayPreview();
    }

    private void OnTextOverlayMoved(object? sender, (string Id, TimeSpan NewStart) e)
    {
        ViewModel.UndoRedoManager.Execute(new MoveTextOverlayOperation(e.Id, e.NewStart));
        RefreshOverlayPreview();
    }

    private void OnTextOverlayResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e)
    {
        ViewModel.UndoRedoManager.Execute(new TrimTextOverlayOperation(e.Id, e.IsStartEdge, e.NewEdgeTime));
        RefreshOverlayPreview();
    }

    private void OnTextOverlayRemoveRequested(object? sender, string overlayId)
    {
        DeleteTextOverlay(overlayId);
    }

    private void DeleteTextOverlay(string overlayId)
    {
        // Finalize first: an in-flight drag or inline text edit has already mutated the
        // live overlay and is holding a pre-gesture snapshot to restore before it commits.
        // Removing the overlay out from under it would strand those mutations — the target
        // can no longer find the segment, while RemoveTextOverlayOperation captures the
        // already-mutated object, so undoing the delete would bring back an overlay at a
        // position or text the user never committed.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        CommitActiveTextEditIfAny();

        ViewModel.UndoRedoManager.Execute(new RemoveTextOverlayOperation(overlayId));
        Timeline.ClearTextOverlaySelection();
        _selectedTextOverlayId = null;
        SyncTextOverlayUI(null);
        RefreshOverlayPreview();
    }

    private void RemoveTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTextOverlayId is { } id)
            DeleteTextOverlay(id);
    }

    // ─── Transition boundary panel ──────────────────────────────────────

    /// <summary>Incoming segment Id of the currently-selected boundary chip, or null.</summary>
    private string? _selectedTransitionId;
    private bool _suppressTransitionEvents;

    private void OnTransitionSelected(object? sender, string? incomingSegmentId)
    {
        _selectedTransitionId = incomingSegmentId;
        SyncTransitionUI(incomingSegmentId);
    }

    private void OnTransitionRemoveRequested(object? sender, string incomingSegmentId)
    {
        DeleteTransition(incomingSegmentId);
    }

    /// <summary>
    /// Resolves the incoming/outgoing segments flanking the boundary owned by
    /// <paramref name="incomingSegmentId"/>. The first segment (index 0) has no leading
    /// boundary, matching <see cref="TransitionResolver"/>'s own "index &lt;= 0" rule.
    /// </summary>
    private (TimelineSegment? Incoming, TimelineSegment? Outgoing) GetTransitionBoundarySegments(string incomingSegmentId)
    {
        var segments = ViewModel.Model.Segments;
        int index = segments.FindIndex(s => s.Id == incomingSegmentId);
        if (index <= 0) return (null, null);
        return (segments[index], segments[index - 1]);
    }

    /// <summary>
    /// Maps a <see cref="TransitionType"/> to the pane's picker family, mirroring
    /// <see cref="TimelineControl"/>'s chip colour grouping (dissolve / slide+push / wipe /
    /// stylised) except that Slide and Push are kept as two separate families here — the pane
    /// has room to be more specific than a 20px chip glyph does.
    /// </summary>
    private static string TransitionFamilyTagFor(TransitionType type) => type switch
    {
        TransitionType.None => "None",
        TransitionType.Fade or TransitionType.CrossFade or TransitionType.DipToWhite => "Dissolve",
        TransitionType.SlideLeft or TransitionType.SlideRight or TransitionType.SlideUp or TransitionType.SlideDown => "Slide",
        TransitionType.PushLeft or TransitionType.PushRight or TransitionType.PushUp or TransitionType.PushDown => "Push",
        TransitionType.Wipe or TransitionType.WipeRight or TransitionType.WipeUp or TransitionType.WipeDown => "Wipe",
        TransitionType.ZoomBlur or TransitionType.WhipPanLeft or TransitionType.WhipPanRight or TransitionType.Glitch => "Stylised",
        _ => "Dissolve",
    };

    private static readonly (TransitionType Type, string Label)[] TransitionDissolveVariants =
    [
        (TransitionType.Fade, "Fade"),
        (TransitionType.CrossFade, "Cross Dissolve"),
        (TransitionType.DipToWhite, "Dip to White"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionSlideVariants =
    [
        (TransitionType.SlideLeft, "Slide Left"),
        (TransitionType.SlideRight, "Slide Right"),
        (TransitionType.SlideUp, "Slide Up"),
        (TransitionType.SlideDown, "Slide Down"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionPushVariants =
    [
        (TransitionType.PushLeft, "Push Left"),
        (TransitionType.PushRight, "Push Right"),
        (TransitionType.PushUp, "Push Up"),
        (TransitionType.PushDown, "Push Down"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionWipeVariants =
    [
        (TransitionType.Wipe, "Wipe Left \u2192 Right"),
        (TransitionType.WipeRight, "Wipe Right \u2192 Left"),
        (TransitionType.WipeUp, "Wipe Bottom \u2192 Top"),
        (TransitionType.WipeDown, "Wipe Top \u2192 Bottom"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionStylisedVariants =
    [
        (TransitionType.ZoomBlur, "Zoom Blur"),
        (TransitionType.WhipPanLeft, "Whip Pan Left"),
        (TransitionType.WhipPanRight, "Whip Pan Right"),
        (TransitionType.Glitch, "Glitch"),
    ];

    private static (TransitionType Type, string Label)[] TransitionVariantsForFamily(string familyTag) => familyTag switch
    {
        "Dissolve" => TransitionDissolveVariants,
        "Slide" => TransitionSlideVariants,
        "Push" => TransitionPushVariants,
        "Wipe" => TransitionWipeVariants,
        "Stylised" => TransitionStylisedVariants,
        _ => [],
    };

    private static void SelectComboItemByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    /// <summary>
    /// Ensures <see cref="TransitionVariantCombo"/> lists <paramref name="familyTag"/>'s variants
    /// (its contents depend on which family is selected, so — unlike the other panes' static
    /// XAML-declared combos — it is populated here) and selects <paramref name="selected"/>,
    /// defaulting to the family's first variant if that type isn't a member of it.
    /// </summary>
    /// <remarks>
    /// <b>The item collection is only rebuilt when it actually differs, and this is load-bearing
    /// rather than an optimisation.</b> Selecting a variant runs
    /// <c>TransitionVariantCombo_SelectionChanged</c> -> <c>UndoRedoManager.Execute</c> ->
    /// <c>OnUndoRedoStateChanged</c> -> <see cref="SyncTransitionUI"/> -> here, and that
    /// dispatcher callback lands while this ComboBox's own dropdown is still open or mid-close
    /// animation. Clearing and refilling <c>Items</c> in that state leaves WinUI unable to
    /// reconcile the live popup against the mutated collection, and it fail-fasts the process
    /// with a stowed <c>E_UNEXPECTED</c> (0x8000FFFF) inside <c>Microsoft.UI.Xaml.dll</c> —
    /// observed as an APPCRASH with exception code 0xC000027B. Picking a *family* never hit it
    /// because the popup that is open then belongs to the other combo.
    /// <para>
    /// So for the overwhelmingly common "same family, different variant" resync the collection
    /// is left completely untouched and only the selection moves, which is safe with the popup
    /// open. The destructive rebuild then only happens on a genuine family change, when this
    /// combo's own dropdown is closed.
    /// </para>
    /// </remarks>
    private void PopulateTransitionVariantCombo(string familyTag, TransitionType selected)
    {
        var variants = TransitionVariantsForFamily(familyTag);

        if (!TransitionVariantComboAlreadyLists(variants))
        {
            TransitionVariantCombo.Items.Clear();
            foreach (var (type, label) in variants)
            {
                // Selection is applied after the collection settles, never mid-population:
                // assigning SelectedItem while Items is still being mutated is the same class
                // of reconciliation hazard described above.
                TransitionVariantCombo.Items.Add(new ComboBoxItem { Content = label, Tag = type.ToString() });
            }
        }

        SelectComboItemByTag(TransitionVariantCombo, selected.ToString());
        if (TransitionVariantCombo.SelectedIndex < 0 && TransitionVariantCombo.Items.Count > 0)
            TransitionVariantCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Whether <see cref="TransitionVariantCombo"/> already lists exactly <paramref name="variants"/>,
    /// in order — i.e. whether a rebuild can be skipped. Compared by the items' <c>Tag</c>
    /// (the <see cref="TransitionType"/> name), which is what selection is driven by.
    /// </summary>
    private bool TransitionVariantComboAlreadyLists((TransitionType Type, string Label)[] variants)
    {
        if (TransitionVariantCombo.Items.Count != variants.Length)
            return false;

        for (int i = 0; i < variants.Length; i++)
        {
            if (TransitionVariantCombo.Items[i] is not ComboBoxItem item ||
                item.Tag as string != variants[i].Type.ToString())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Pushes the boundary's current <see cref="TransitionConfig"/> (or the "Automatic"/unset
    /// state) onto the pane controls, or hides the pane when there is no such boundary, then
    /// reveals it. Mirrors <see cref="SyncTextOverlayUI"/>.
    /// </summary>
    /// <remarks>
    /// Purely reads state onto the controls under <see cref="_suppressTransitionEvents"/> —
    /// it must never itself call <see cref="UndoRedoManager.Execute"/>, or simply selecting an
    /// unconfigured ("Automatic", <c>InTransition == null</c>) boundary would silently turn it
    /// into an explicit config the first time the pane happened to redraw.
    /// </remarks>
    private void SyncTransitionUI(string? incomingSegmentId)
    {
        if (TransitionFamilyCombo is null) return;

        TimelineSegment? incoming = null;
        TimelineSegment? outgoing = null;
        if (incomingSegmentId is not null)
            (incoming, outgoing) = GetTransitionBoundarySegments(incomingSegmentId);

        if (incoming is null || outgoing is null)
        {
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Transition, false);
            return;
        }

        _suppressTransitionEvents = true;

        var config = incoming.InTransition;
        string familyTag = config is null ? "Unset" : TransitionFamilyTagFor(config.Type);
        bool hasEffect = familyTag is not ("Unset" or "None");

        SelectComboItemByTag(TransitionFamilyCombo, familyTag);
        TransitionAutomaticHint.Visibility = familyTag == "Unset" ? Visibility.Visible : Visibility.Collapsed;

        TransitionVariantCombo.Visibility = hasEffect ? Visibility.Visible : Visibility.Collapsed;
        if (hasEffect)
            PopulateTransitionVariantCombo(familyTag, config!.Type);

        // Clamp the usable maximum LIVE to half of each neighbour's current duration, exactly
        // as TransitionResolver will clamp the actual dissolve — so the slider can never be
        // dragged to a value that would visibly lie about what will actually play.
        double halfIncoming = incoming.Duration.TotalSeconds / 2.0;
        double halfOutgoing = outgoing.Duration.TotalSeconds / 2.0;
        double clampSeconds = Math.Min(halfIncoming, halfOutgoing);
        double effectiveMax = Math.Min(2.0, clampSeconds);

        // A very short neighbouring segment (TrimSegmentEdgeOperation's own 100ms floor halves
        // to 50ms) can push effectiveMax below what the slider's Minimum can even express.
        // Raising Slider.Maximum back up to Minimum in that case would let the user drag to,
        // and persist, a value the resolver will silently shorten anyway — exactly the
        // "dial lies about what will play" bug the clamp exists to prevent. Disable the
        // slider instead and always state the true effectiveMax in the hint, never a
        // substituted slider maximum.
        bool tooShortForSlider = effectiveMax < TransitionDurationSlider.Minimum;
        double sliderMax = tooShortForSlider ? TransitionDurationSlider.Minimum : effectiveMax;

        TransitionDurationSlider.Maximum = sliderMax;
        bool isClamped = effectiveMax < 2.0;
        TransitionDurationClampHint.Visibility = isClamped ? Visibility.Visible : Visibility.Collapsed;
        if (tooShortForSlider)
        {
            TransitionDurationClampHint.Text = string.Format(CultureInfo.InvariantCulture,
                "This boundary is too short for an adjustable transition — a neighbouring segment limits it to {0:0.00}s, and it will render at exactly that length regardless of the value shown above.",
                effectiveMax);
        }
        else if (isClamped)
        {
            TransitionDurationClampHint.Text = string.Format(CultureInfo.InvariantCulture,
                "Limited to {0:0.00}s by a neighbouring segment's length — dragging further will not make the dissolve any longer.",
                effectiveMax);
        }

        double durationSeconds = config?.Duration.TotalSeconds ?? 0.5;
        TransitionDurationSlider.Value = Math.Clamp(durationSeconds, TransitionDurationSlider.Minimum, sliderMax);
        TransitionDurationSlider.IsEnabled = hasEffect && !tooShortForSlider;

        SelectComboItemByTag(TransitionEasingCombo, (config?.Easing ?? TransitionEasing.EaseInOut).ToString());
        TransitionEasingCombo.IsEnabled = hasEffect;

        RemoveTransitionButton.IsEnabled = config is not null;

        _suppressTransitionEvents = false;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Transition, true);
        PropertiesPanel.ShowPane(PropertyPaneKind.Transition);
    }

    private void TransitionFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionFamilyCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string familyTag) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming is null) return;

        TransitionConfig? newConfig = familyTag switch
        {
            "Unset" => null,
            "None" => (incoming.InTransition ?? new TransitionConfig()) with { Type = TransitionType.None },
            _ => ApplyTransitionFamilyDefaultVariant(incoming.InTransition, familyTag),
        };

        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Type"));
        SyncTransitionUI(id);
        InvalidatePreview();
    }

    private static TransitionConfig ApplyTransitionFamilyDefaultVariant(TransitionConfig? existing, string familyTag)
    {
        var variants = TransitionVariantsForFamily(familyTag);
        var type = variants.Length > 0 ? variants[0].Type : TransitionType.None;
        return existing is null ? new TransitionConfig { Type = type } : existing with { Type = type };
    }

    private void TransitionVariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionVariantCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string typeName) return;
        if (!Enum.TryParse<TransitionType>(typeName, out var type)) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Type = type };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Variant"));
        InvalidatePreview();
    }

    private void TransitionDurationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Duration = TimeSpan.FromSeconds(e.NewValue) };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Duration"));
        InvalidatePreview();
    }

    private void TransitionEasingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionEasingCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string easingName) return;
        if (!Enum.TryParse<TransitionEasing>(easingName, out var easing)) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Easing = easing };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Easing"));
        InvalidatePreview();
    }

    /// <summary>
    /// Applies the selected boundary's current config (which may legitimately be "Automatic" /
    /// null, clearing every other boundary) to every boundary on the timeline, as a single undo
    /// entry — see <see cref="ApplyTransitionToAllBoundariesOperation"/>.
    /// </summary>
    private void ApplyTransitionToAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTransitionId is not { } id) return;
        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming is null) return;

        var config = incoming.InTransition;
        ViewModel.UndoRedoManager.Execute(
            new ApplyTransitionToAllBoundariesOperation(config, "Apply Transition to All Boundaries"));
        InvalidatePreview();
    }

    private void RemoveTransitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTransitionId is { } id)
            DeleteTransition(id);
    }

    /// <summary>Clears a boundary's transition back to "Automatic" (null), keeping it selected
    /// so the pane re-syncs to show the now-unconfigured state rather than closing.</summary>
    private void DeleteTransition(string incomingSegmentId)
    {
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(incomingSegmentId, null, "Remove Transition"));
        if (_selectedTransitionId == incomingSegmentId)
            SyncTransitionUI(incomingSegmentId);
        InvalidatePreview();
    }

    /// <summary>
    /// Re-syncs every renderer's overlay list, then forces a preview re-render at the current
    /// playhead. <see cref="UndoRedoManager"/>'s state-changed event already drives the same
    /// re-sync through <see cref="InvalidatePreview"/>, but that path is skipped whenever
    /// <c>_compositorReady</c> is false or a segment renderer hasn't been (re)built yet, so this
    /// explicit call guarantees adding/editing/removing/undoing an overlay updates the preview
    /// immediately rather than only after a full renderer rebuild — property edits mutate the
    /// live segment in place, so a compositor that already has the list synced (see
    /// <see cref="SyncTextOverlaysToRenderer"/>) would pick the change up on its own next
    /// repaint anyway, but add/remove need the list itself re-fetched.
    /// </summary>
    private void RefreshOverlayPreview()
    {
        if (_previewRenderer is not null)
        {
            SyncTextOverlaysToRenderer(_previewRenderer, null);
        }
        foreach (var (segmentId, ctx) in _segmentPreviews)
        {
            if (ctx.Renderer is null) continue;
            var seg = ViewModel.Model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == segmentId);
            if (seg is null) continue;
            SyncTextOverlaysToRenderer(ctx.Renderer, seg.VideoFilePath);
        }

        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    /// <summary>
    /// Debounces <see cref="RefreshOverlayPreview"/> for the high-frequency
    /// <see cref="OverlayTextBox_TextChanged"/> handler: the model is committed on every
    /// keystroke (so undo/redo and export always see the latest text), but repainting the
    /// preview on every keystroke would be wasteful, so only that part is deferred — mirrors
    /// <see cref="ScheduleMotionPreviewRebuild"/>'s split between an immediate model commit
    /// and a debounced preview rebuild.
    /// </summary>
    private void ScheduleOverlayPreviewRefresh()
    {
        if (_overlayPreviewDebounceTimer is null)
        {
            _overlayPreviewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _overlayPreviewDebounceTimer.Tick += (_, _) =>
            {
                _overlayPreviewDebounceTimer.Stop();
                RefreshOverlayPreview();
            };
        }
        _overlayPreviewDebounceTimer.Stop();
        _overlayPreviewDebounceTimer.Start();
    }

    private void OverlayPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayPresets.SelectedItem is not Border { Tag: string presetName }) return;

        var preset = TextOverlayPresets.ByName(presetName);
        if (preset is null) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, preset.Apply, $"Apply {preset.Name} Preset"));

        // Re-sync every control (anchor grid, background sub-panels, etc.) so the pane
        // reflects the preset's full style rather than just the fields a targeted handler
        // would have touched. SyncTextOverlayUI clears OverlayPresets.SelectedItem again,
        // which is why this handler must be re-entrancy-guarded above.
        SyncTextOverlayUI(id);
        Timeline.InvalidateAllCanvases();
        RefreshOverlayPreview();
    }

    private void OverlayTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var text = OverlayTextBox.Text;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Text = text, "Change Overlay Text"));

        Timeline.InvalidateAllCanvases();
        ScheduleOverlayPreviewRefresh();
    }

    private void OverlayAnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAnimationCombo.SelectedItem is not ComboBoxItem item) return;
        if (!Enum.TryParse<TextSlideAnimation>(item.Tag?.ToString(), out var anim)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Animation = anim, "Change Overlay Animation"));
        RefreshOverlayPreview();
    }

    private void OverlayFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayFontCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string font ||
            string.IsNullOrWhiteSpace(font)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.FontFamily = font, "Change Overlay Font"));
        RefreshOverlayPreview();
    }

    private void OverlayDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (double.IsNaN(args.NewValue)) return;

        double seconds = Math.Max(args.NewValue, TrimTextOverlayOperation.MinDuration.TotalSeconds);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Duration = TimeSpan.FromSeconds(seconds), "Change Overlay Duration"));

        Timeline.InvalidateAllCanvases();
        RefreshOverlayPreview();
    }

    private void OverlayFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (double.IsNaN(args.NewValue)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.FontSize = args.NewValue, "Change Overlay Font Size"));
        RefreshOverlayPreview();
    }

    private void OverlayTextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.TextColor = hex, "Change Overlay Text Color"));

        OverlayTextColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayTextColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayBold_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool isBold = OverlayBoldToggle.IsChecked == true;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.IsBold = isBold, "Change Overlay Bold"));
        RefreshOverlayPreview();
    }

    private void OverlayItalic_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool isItalic = OverlayItalicToggle.IsChecked == true;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.IsItalic = isItalic, "Change Overlay Italic"));
        RefreshOverlayPreview();
    }

    private void OverlayAlignSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAlignSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item ||
            !Enum.TryParse<SlideTextAlignment>(item.Tag?.ToString(), out var align)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.TextAlignment = align, "Change Overlay Alignment"));
        RefreshOverlayPreview();
    }

    private void OverlayAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<TextOverlayAnchor>(tag, out var anchor)) return;

        var overlay = SelectedTextOverlay();
        if (overlay is null) return;

        // The box is an explicit rectangle now, so the anchor's true centre is exact
        // arithmetic over the authored width/height — no glyph measurement or estimate
        // needed. ResolveCenter ignores the passed-in X/Y for every non-Custom anchor, so
        // this stored value only matters once the user drags the overlay back to Custom,
        // where it gives it the right starting point.
        var (x, y) = TextOverlaySegment.ResolveCenter(
            anchor, overlay.X, overlay.Y, overlay.MarginFraction,
            overlay.WidthFraction, overlay.HeightFraction);

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(id, o =>
        {
            o.Anchor = anchor;
            o.X = x;
            o.Y = y;
        }, "Change Overlay Anchor"));

        OverlayCustomPositionHint.Visibility = Visibility.Collapsed;
        RefreshOverlayPreview();
    }

    private void OverlayWidthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.WidthFraction = fraction, "Change Overlay Width"));
        RefreshOverlayPreview();
    }

    private void OverlayHeightSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.HeightFraction = fraction, "Change Overlay Height"));
        RefreshOverlayPreview();
    }

    private void OverlayMarginSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.MarginFraction = fraction, "Change Overlay Margin"));
        RefreshOverlayPreview();
    }

    private void OverlayBgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayBgTypeCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<TextOverlayBackground>(item.Tag?.ToString(), out var background)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Background = background, "Change Overlay Background Type"));

        UpdateOverlayBackgroundPanels(background);
        RefreshOverlayPreview();
    }

    private void OverlayBgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.BackgroundColor = hex, "Change Overlay Background Color"));

        OverlayBgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayBgColorText.Text = hex;
        RefreshOverlayPreview();
    }

    /// <summary>
    /// Shared by <see cref="OverlayBgOpacitySlider"/>, <see cref="OverlayCornerRadiusSlider"/>
    /// and <see cref="OverlayPaddingSlider"/> — all three live in <see cref="OverlayBoxPanel"/>
    /// and drive the box's style regardless of which background type is selected (mirrors how
    /// Scene's <c>StyleSlider_ValueChanged</c> is shared by several sliders).
    /// </summary>
    private void OverlayBoxSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double value = e.NewValue;
        if (ReferenceEquals(sender, OverlayBgOpacitySlider))
        {
            double opacity = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BackgroundOpacity = opacity, "Change Overlay Background Opacity"));
        }
        else if (ReferenceEquals(sender, OverlayCornerRadiusSlider))
        {
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.CornerRadius = value, "Change Overlay Corner Radius"));
        }
        else if (ReferenceEquals(sender, OverlayPaddingSlider))
        {
            double padding = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.PaddingScale = padding, "Change Overlay Padding"));
        }
        else
        {
            return;
        }

        RefreshOverlayPreview();
    }

    /// <summary>Shared by <see cref="OverlayBlurAmountSlider"/> and <see cref="OverlayBlurTintSlider"/>.</summary>
    private void OverlayBlurSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double value = e.NewValue;
        if (ReferenceEquals(sender, OverlayBlurAmountSlider))
        {
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BlurAmount = value, "Change Overlay Blur Amount"));
        }
        else if (ReferenceEquals(sender, OverlayBlurTintSlider))
        {
            double tint = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BlurTintOpacity = tint, "Change Overlay Blur Tint"));
        }
        else
        {
            return;
        }

        RefreshOverlayPreview();
    }

    private void OverlayScrimDirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayScrimDirectionCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<ScrimDirection>(item.Tag?.ToString(), out var direction)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ScrimDirection = direction, "Change Overlay Scrim Direction"));
        RefreshOverlayPreview();
    }

    private void OverlayScrimStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double strength = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ScrimStrength = strength, "Change Overlay Scrim Strength"));
        RefreshOverlayPreview();
    }

    private void OverlayOutlineWidthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double width = e.NewValue;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.OutlineWidth = width, "Change Overlay Outline Width"));
        RefreshOverlayPreview();
    }

    private void OverlayOutlineColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.OutlineColor = hex, "Change Overlay Outline Color"));

        OverlayOutlineColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayOutlineColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayShadowStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double strength = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ShadowStrength = strength, "Change Overlay Shadow Strength"));
        RefreshOverlayPreview();
    }

    private void OverlayAccentColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentColor = hex, "Change Overlay Accent Color"));

        OverlayAccentColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayAccentColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayAccentThicknessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double thickness = e.NewValue;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentThickness = thickness, "Change Overlay Accent Thickness"));
        RefreshOverlayPreview();
    }

    private void OverlayAccentSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAccentSideCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<AccentSide>(item.Tag?.ToString(), out var side)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentSide = side, "Change Overlay Accent Side"));
        RefreshOverlayPreview();
    }

    private void OverlayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool enabled = OverlayEnabledToggle.IsOn;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Enabled = enabled, "Change Overlay Enabled"));

        Timeline.InvalidateAllCanvases();
        RefreshOverlayPreview();
    }

    /// <summary>
    /// Selects the combo item matching <paramref name="fontFamily"/>; if the overlay uses a
    /// font that isn't in the curated list, it is inserted at the top so the actual font is
    /// represented (and not silently changed). Mirrors <see cref="SetSlideFontSelection"/>.
    /// </summary>
    private void SetOverlayFontSelection(string fontFamily)
    {
        for (int i = 0; i < OverlayFontCombo.Items.Count; i++)
        {
            if (OverlayFontCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                OverlayFontCombo.SelectedIndex = i;
                return;
            }
        }

        var custom = new ComboBoxItem { Content = fontFamily, Tag = fontFamily };
        try { custom.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily); }
        catch { /* unknown family — fall back to default rendering */ }
        OverlayFontCombo.Items.Insert(0, custom);
        OverlayFontCombo.SelectedIndex = 0;
    }

    private bool _overlayPresetsBuilt;

    /// <summary>
    /// Populates <see cref="OverlayPresets"/> from <see cref="TextOverlayPresets.All"/>, one
    /// tile per preset showing its glyph and name — mirrors <see cref="BuildGradientPresetsIfNeeded"/>'s
    /// tile-construction approach for the text slide pane's gradient presets.
    /// </summary>
    private void BuildOverlayPresetsIfNeeded()
    {
        if (_overlayPresetsBuilt) return;
        _overlayPresetsBuilt = true;

        foreach (var preset in TextOverlayPresets.All)
        {
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = preset.Glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = preset.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var tile = new Border
            {
                Width = 84,
                Height = 64,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Tag = preset.Name,
                Child = stack,
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(tile, preset.Name);
            OverlayPresets.Items.Add(tile);
        }
    }

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

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        if (ViewModel.CanUndo)
        {
            ViewModel.UndoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        if (ViewModel.CanRedo)
        {
            ViewModel.RedoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void SplitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        ViewModel.SplitAtPlayheadCommand.Execute(null);
        args.Handled = true;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        // If a camera segment is selected, remove it.
        if (Timeline.SelectedCameraSegmentId is { } cameraSegId)
        {
            DeleteCameraSegment(cameraSegId);
            args.Handled = true;
            return;
        }

        // If a text overlay is selected, remove it.
        if (Timeline.SelectedTextOverlayId is { } overlayId)
        {
            DeleteTextOverlay(overlayId);
            args.Handled = true;
            return;
        }

        // If a zoom segment is selected, remove it instead of deleting a clip segment
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
            args.Handled = true;
            return;
        }

        // If a primary-track segment is selected, ripple-delete it.
        if (_selectedPrimarySegmentId is { } segId &&
            ViewModel.Model.Segments.Any(s => s.Id == segId))
        {
            var operation = new RemoveSegmentOperation(segId);
            ViewModel.UndoRedoManager.Execute(operation);
            _selectedPrimarySegmentId = null;
            Timeline.SelectSegment(null);
            HideTextSlidePanel();
            args.Handled = true;
            return;
        }

        ViewModel.DeleteSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
    }

    private async void SaveAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveProjectAsync(forcePrompt: false);
    }

    private async void SaveAsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveProjectAsync(forcePrompt: true);
    }

    private void PlayPauseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl())
        {
            return;
        }

        if (Preview.IsPlaying)
        {
            Preview.Pause();
        }
        else
        {
            Preview.Play();
        }
        args.Handled = true;
    }

    private bool IsFocusOnInteractiveControl()
    {
        DependencyObject? node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null)
        {
            switch (node)
            {
                case TextBox:
                case PasswordBox:
                case RichEditBox:
                case AutoSuggestBox:
                case ComboBox:
                case NumberBox:
                case Microsoft.UI.Xaml.Controls.Primitives.ButtonBase:
                case ToggleSwitch:
                case Slider:
                case ColorPicker:
                case FlyoutPresenter:
                case MenuFlyoutPresenter:
                    return true;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // --- Zoom Segment Handlers ---

    private bool _suppressZoomPropertyUpdate;

    private void OnZoomSegmentSelected(object? sender, string? segmentId)
    {
        if (_zoomRegionEditMode)
            ExitZoomRegionEditMode();

        UpdateZoomPanelVisibility();

        if (segmentId is not null)
        {
            var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == segmentId);
            if (kf is not null)
            {
                _suppressZoomPropertyUpdate = true;

                bool matched = false;
                for (int i = 0; i < ZoomLevelCombo.Items.Count; i++)
                {
                    if (ZoomLevelCombo.Items[i] is ComboBoxItem item &&
                        double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z) &&
                        Math.Abs(z - kf.ZoomLevel) < 0.01)
                    {
                        ZoomLevelCombo.SelectedIndex = i;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    ZoomLevelCombo.SelectedIndex = -1;
                    ZoomLevelCombo.Text = kf.ZoomLevel.ToString("0.##", CultureInfo.InvariantCulture) + "x";
                }

                _suppressZoomPropertyUpdate = false;
            }
        }
    }

    private void OnZoomSegmentMoved(object? sender, (string Id, TimeSpan NewTimestamp) e)
    {
        var operation = new MoveZoomKeyframeOperation(e.Id, e.NewTimestamp);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e)
    {
        var operation = new ResizeZoomSegmentOperation(e.Id, e.IsStartEdge, e.NewEdgeTime);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End, string? FilePath) e)
    {
        double zoomLevel = 2.0;
        if (ZoomLevelCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z))
            zoomLevel = z;

        // Use cursor position at segment midpoint as zoom center (primary recording
        // only; appended recordings default to centered).
        double cx = 0.5, cy = 0.5;
        var midpoint = e.Start + (e.End - e.Start) / 2;
        if (e.FilePath is null && ViewModel.Model.CursorData is { } cursorData && cursorData.Samples.Count > 0)
        {
            var project = ProjectService.Instance.CurrentProject;
            int sourceW = project?.Width > 0 ? project.Width : 1920;
            int sourceH = project?.Height > 0 ? project.Height : 1080;
            // Mouse hook coords are already physical (PerMonitorV2) — no DPI scaling
            float dpiX = 1.0f;
            float dpiY = 1.0f;
            int cropOffX = project?.CropOffsetX ?? 0;
            int cropOffY = project?.CropOffsetY ?? 0;

            double mouseOffset = project?.MouseToVideoOffsetSeconds ?? 0;
            double targetTime = midpoint.TotalSeconds + mouseOffset;
            double tickFreq = cursorData.TickFrequency;
            long startTick = cursorData.StartTimestampTicks;
            Musio.Core.Models.MouseSample closest = cursorData.Samples[0];
            double bestDist = double.MaxValue;
            foreach (var s in cursorData.Samples)
            {
                double sTime = (s.TimestampTicks - startTick) / tickFreq;
                double dist = Math.Abs(sTime - targetTime);
                if (dist < bestDist) { bestDist = dist; closest = s; }
            }
            cx = (closest.X * dpiX - cropOffX) / sourceW;
            cy = (closest.Y * dpiY - cropOffY) / sourceH;
        }

        // Build the keyframe tagged with the owning source file so it renders on the
        // correct clip and (for the primary) drives the zoom engine.
        var keyframe = Musio.Core.Timeline.ZoomKeyframe.FromRange(e.Start, e.End, zoomLevel,
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1)) with
        {
            SourceVideoFilePath = e.FilePath,
        };
        var operation = new AddZoomSegmentOperation(keyframe);
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the newly created segment and enter zoom region edit mode
        Timeline.SelectedZoomKeyframeId = operation.CreatedId;
        OnZoomSegmentSelected(this, operation.CreatedId);

        // Every source — the primary recording, an appended recording, or an imported
        // video — positions its zoom in its own source space, so region editing is offered
        // for all of them. EnterZoomRegionEditMode maps the overlay against the OWNING
        // segment's dimensions, so differently-sized clips still line up.
        var createdKf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == operation.CreatedId);
        if (createdKf is not null)
            EnterZoomRegionEditMode(operation.CreatedId, createdKf);
    }

    private void OnZoomSegmentRemoveRequested(object? sender, string keyframeId)
    {
        var operation = new RemoveZoomKeyframeOperation(keyframeId);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.ClearZoomSelection();
        UpdateZoomPanelVisibility();
    }

    private void RemoveZoomSegment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
        }
    }

    private void ZoomLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomPropertyUpdate) return;
        if (Timeline is null) return;
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        if (ZoomLevelCombo.SelectedItem is not ComboBoxItem item) return;
        if (!double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double zoomLevel)) return;

        if (_zoomRegionEditMode)
        {
            // Update the overlay rectangle size without committing
            _zoomRegionZoomLevel = zoomLevel;
            UpdateZoomRegionRect();
            return;
        }

        var operation = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoomLevel);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void ZoomLevelCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (Timeline is null || Timeline.SelectedZoomKeyframeId is not { } selectedId)
        {
            args.Handled = true;
            return;
        }

        string text = (args.Text ?? string.Empty).Trim().TrimEnd('x', 'X');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
        {
            // Restore display from current zoom level
            SyncZoomLevelComboToFreeform(_zoomRegionEditMode ? _zoomRegionZoomLevel : GetSelectedKeyframeZoom() ?? 2.0);
            args.Handled = true;
            return;
        }

        zoom = Math.Clamp(zoom, MinZoomLevel, MaxZoomLevel);

        if (_zoomRegionEditMode)
        {
            _zoomRegionZoomLevel = zoom;
            // Re-clamp center for new zoom
            (double halfW, double halfH) = GetNormalizedHalfExtents(zoom);
            _zoomRegionCenterX = Math.Clamp(_zoomRegionCenterX, halfW, 1.0 - halfW);
            _zoomRegionCenterY = Math.Clamp(_zoomRegionCenterY, halfH, 1.0 - halfH);
            UpdateZoomRegionRect();
        }
        else
        {
            var op = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoom);
            ViewModel.UndoRedoManager.Execute(op);
        }

        SyncZoomLevelComboToFreeform(zoom);
        args.Handled = true;
    }

    private double? GetSelectedKeyframeZoom()
    {
        if (Timeline?.SelectedZoomKeyframeId is not { } id) return null;
        return ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == id)?.ZoomLevel;
    }

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

    // --- Audio waveform loading ---

    private async Task LoadAudioWaveformAsync(Project project, int initGeneration)
    {
        if (project.AudioFilePaths is not { Count: > 0 })
            return;

        const int targetSamples = 2000;

        try
        {
            var validPaths = project.AudioFilePaths.Where(File.Exists).ToList();
            if (validPaths.Count == 0) return;

            // Identify system vs mic files by naming convention
            var systemPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("system_", StringComparison.OrdinalIgnoreCase));
            var micPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("mic_", StringComparison.OrdinalIgnoreCase));

            double videoDuration = project.Duration.TotalSeconds;

            // Offset between audio and video start.
            // Positive: audio pre-roll (WAV starts before video frame 0).
            // Negative: audio started late (e.g. mic permission dialog delay).
            double audioOffset = project.AudioToVideoOffsetSeconds;

            var (systemWaveform, micWaveform) = await Task.Run(() =>
            {
                float[]? sysWf = null;
                float[]? micWf = null;

                double sysDuration = 0, micDuration = 0;
                if (systemPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(systemPath); sysDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }
                if (micPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(micPath); micDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }

                // Waveform alignment:
                // - skipSeconds: how much of the WAV to skip (pre-roll). 0 if offset <= 0.
                // - leadTime: silence at the start of the timeline before audio begins.
                //   Non-zero when audio started after video (negative offset).
                double skipSeconds = Math.Max(0, audioOffset);
                double leadTime = Math.Max(0, -audioOffset);

                sysWf = BuildAlignedWaveform(systemPath, sysDuration, skipSeconds, leadTime, videoDuration, targetSamples);
                micWf = BuildAlignedWaveform(micPath, micDuration, skipSeconds, leadTime, videoDuration, targetSamples);

                return (sysWf, micWf);
            });

            if (systemWaveform is { Length: > 0 })
                ViewModel.Model.SystemAudioWaveformSamples = systemWaveform;
            if (micWaveform is { Length: > 0 })
                ViewModel.Model.MicAudioWaveformSamples = micWaveform;

            // The audio/mic tracks are only shown once their waveform exists, and this
            // build runs in the background well after the timeline first drew — so the
            // tracks have to be told the samples arrived or they would stay collapsed.
            if (systemWaveform is { Length: > 0 } || micWaveform is { Length: > 0 })
                DispatcherQueue.TryEnqueue(() => Timeline?.Refresh());

            // The waveform build above is a multi-second background pass; if the page
            // unloaded or a newer preview init took over meanwhile, publishing a player
            // here would strand it (nothing disposes it again) or clobber the new run's.
            if (initGeneration != _previewInitGeneration) return;

            // At video time T, the audio file position is T + audioOffset
            _audioOffsetSeconds = audioOffset;
            _audioPlayer?.Dispose();
            _audioPlayer = new AudioPlaybackEngine();
            // No fade windows: T9 confirmed transition crossfades can't be wired here
            // without drifting once the timeline has real cuts — see
            // AudioPlaybackEngine's class remarks for the full reasoning.
            _audioPlayer.Load(validPaths);
        }
        catch
        {
            // Audio waveform generation failed — editor still works without it
        }
    }

    /// <summary>
    /// Converts a video playhead position to the corresponding audio file position.
    /// Positive _audioOffsetSeconds = pre-roll (skip into WAV).
    /// Negative _audioOffsetSeconds = audio started late (silence before audio).
    /// Returns negative TimeSpan when video is before audio start — callers
    /// must treat negative results as silence (no audio to play).
    /// </summary>
    private TimeSpan AudioPositionForVideo(TimeSpan videoPosition)
    {
        return videoPosition + TimeSpan.FromSeconds(_audioOffsetSeconds);
    }

    /// <summary>
    /// Builds a waveform array aligned to the video timeline, handling both
    /// pre-roll (positive offset → skip WAV start) and late audio (negative
    /// offset → leading silence on the timeline).
    /// </summary>
    private static float[]? BuildAlignedWaveform(
        string? path, double fileDuration,
        double skipSeconds, double leadTime,
        double videoDuration, int targetSamples)
    {
        if (path is null || fileDuration <= 0) return null;

        try
        {
            // How much of the WAV maps to the video timeline
            double audioForVideo = Math.Max(0, fileDuration - skipSeconds);
            // How much of the video timeline this audio covers
            double coverageDuration = Math.Min(audioForVideo, videoDuration - leadTime);
            if (coverageDuration <= 0) return null;

            int leadPeaks = (int)(targetSamples * leadTime / videoDuration);
            int audioPeaks = (int)Math.Min(
                targetSamples - leadPeaks,
                Math.Ceiling(targetSamples * coverageDuration / videoDuration));
            if (audioPeaks < 1) audioPeaks = 1;

            var raw = AudioWaveformGenerator.GenerateWaveform(
                path, audioPeaks, startSeconds: skipSeconds);
            var wf = new float[targetSamples];
            Array.Copy(raw, 0, wf, leadPeaks, Math.Min(raw.Length, audioPeaks));
            return wf;
        }
        catch { return null; }
    }

    // --- Audio mute support ---

    /// <summary>
    /// Reloads the audio player with only the unmuted audio tracks.
    /// </summary>
    private void ReloadAudioPlayer()
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _audioPlayer?.Dispose();
        _audioPlayer = new AudioPlaybackEngine();

        var paths = GetUnmutedAudioPaths(project);
        if (paths.Count > 0)
            // No fade windows here either, for the same reason as the initial Load
            // above — see AudioPlaybackEngine's class remarks (T9).
            _audioPlayer.Load(paths);
    }

    /// <summary>
    /// Returns audio file paths filtered by current mute state.
    /// </summary>
    private List<string> GetUnmutedAudioPaths(Project project)
    {
        var model = ViewModel.Model;
        var paths = new List<string>();
        if (project.AudioFilePaths is null) return paths;

        foreach (var path in project.AudioFilePaths)
        {
            if (!File.Exists(path)) continue;
            var fileName = Path.GetFileName(path);
            if (model.IsSystemAudioMuted
                && fileName.StartsWith("system_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (model.IsMicAudioMuted
                && fileName.StartsWith("mic_", StringComparison.OrdinalIgnoreCase))
                continue;
            paths.Add(path);
        }
        return paths;
    }

    // --- Export flyout ---

    private async void ExportFlyout_Opened(object? sender, object e)
    {
        if (ExportVM.IsExporting)
        {
            ShowExportingState();
            return;
        }

        if (ExportVM.ExportSucceeded)
        {
            ShowExportedState();
            return;
        }

        if (ExportVM.ExportFailed)
        {
            ShowErrorState();
            return;
        }

        // Pause preview playback before export. The export pipeline composites
        // frames on the shared Win2D device; running it concurrently with the
        // preview's per-frame composition can corrupt output frames and crash
        // the encoder when both pull from the same source files at once.
        Preview.Pause();
        try { _audioPlayer?.Pause(); } catch { /* best-effort */ }

        // Start new export
        ExportVM.PrepareForExport();
        if (!ExportVM.ExportCommand.CanExecute(null))
        {
            ExportFlyout.Hide();
            EditorInfoBar.Message = "No recording available to export.";
            EditorInfoBar.Severity = InfoBarSeverity.Warning;
            EditorInfoBar.IsOpen = true;
            return;
        }

        ShowExportingState();
        await ExportVM.ExportCommand.ExecuteAsync(null);
    }

    private void ExportFlyout_Closed(object? sender, object e)
    {
        // Reset terminal state (Exported / Failed) so the next open starts a fresh
        // export. Without this, dismissing the flyout via click-outside leaves
        // ExportSucceeded=true; reopening the flyout would then short-circuit and
        // show the prior result instead of running a new export — which makes
        // subsequent style/edit changes appear to never apply to the output file.
        if (ExportVM.IsExporting) return;
        if (!ExportVM.ExportSucceeded && !ExportVM.ExportFailed) return;
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    private void ExportVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.IsExporting) && !ExportVM.IsExporting)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ExportVM.ExportSucceeded)
                    ShowExportedState();
                else if (ExportVM.ExportFailed)
                    ShowErrorState();
                else
                    ExportFlyout.Hide(); // Cancelled
            });
        }
    }

    private void ShowExportingState()
    {
        ExportingPanel.Visibility = Visibility.Visible;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowExportedState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Visible;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Visible;
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.OpenOutputFolderCommand.Execute(null);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.CancelExportCommand.Execute(null);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        ExportFlyout.Hide();
        // Reset state so next open starts a fresh export
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    // ─── Background Style Editing ───────────────────────────────────────

    // ─── Cursor Style Editing ───────────────────────────────────────────

    private void InitializeCursorControls(Project project, CompositionConfig config)
    {
        bool hasCursor = !string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath);
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Cursor, hasCursor);

        if (!hasCursor) return;

        SyncCursorControlsToConfig(config.Cursor);
    }

    private void SyncCursorControlsToConfig(CursorStyle cursor)
    {
        _suppressCursorEvents = true;
        try
        {
            // Cursor type
            CursorTypeMouse.IsChecked = cursor.Type != CursorType.Touch;
            CursorTypeTouch.IsChecked = cursor.Type == CursorType.Touch;

            // Size
            CursorSizeSlider.Value = cursor.Scale;

            // Tilt
            CursorTiltToggle.IsOn = cursor.TiltEnabled;
            CursorTiltToggle.Visibility = cursor.Type != CursorType.Touch
                ? Visibility.Visible : Visibility.Collapsed;

            // Color — find matching radio button by Tag
            string cursorColor = (cursor.Color ?? "#FFFFFF").ToUpperInvariant();
            bool found = false;
            foreach (var child in CursorColorPanel.Children)
            {
                if (child is RadioButton rb && rb.Tag is string tag)
                {
                    bool match = string.Equals(tag, cursorColor, StringComparison.OrdinalIgnoreCase);
                    rb.IsChecked = match;
                    if (match) found = true;
                }
            }
            if (!found && CursorColorPanel.Children.FirstOrDefault() is RadioButton first)
                first.IsChecked = true;
        }
        finally
        {
            _suppressCursorEvents = false;
        }
    }

    private void CursorType_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        // Show/hide tilt toggle based on cursor type
        bool isMouse = CursorTypeMouse.IsChecked == true;
        CursorTiltToggle.Visibility = isMouse ? Visibility.Visible : Visibility.Collapsed;
        ApplyCursorStyleFromControls();
    }

    private void CursorSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ScheduleCursorUpdate();
    }

    private void CursorTiltToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void CursorColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void ScheduleCursorUpdate()
    {
        if (_cursorDebounceTimer is null)
        {
            _cursorDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _cursorDebounceTimer.Tick += (_, _) =>
            {
                _cursorDebounceTimer.Stop();
                ApplyCursorStyleFromControls();
            };
        }
        _cursorDebounceTimer.Stop();
        _cursorDebounceTimer.Start();
    }

    private void ApplyCursorStyleFromControls()
    {
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        var cursorType = CursorTypeTouch.IsChecked == true ? CursorType.Touch : CursorType.Default;

        string color = "#FFFFFF";
        foreach (var child in CursorColorPanel.Children)
        {
            if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is string tag)
            {
                color = tag;
                break;
            }
        }

        var newCursor = config.Cursor with
        {
            Type = cursorType,
            Scale = (float)CursorSizeSlider.Value,
            Color = color,
            TiltEnabled = CursorTiltToggle.IsOn,
        };

        // Cursor style is per-segment: store on the selected segment when one is
        // selected, otherwise update the global config.
        if (SelectedVideoSegment is { } seg)
        {
            seg.CursorStyleOverride = newCursor;
            _ = RebuildForSegmentStyleChangeAsync(seg);
            return;
        }

        config = config with { Cursor = newCursor };
        ProjectService.Instance.CurrentComposition = config;
        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    // ─── Background Style Editing (continued) ───────────────────────────

    private void InitializeStyleControls(Project project, CompositionConfig config)
    {
        // Style panel is available for all capture types. Monitor (full-screen)
        // captures start with zeroed defaults (see ProjectService.SetProject) but
        // users can still customize padding, corner radius, shadow, border, etc.
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Scene, true);

        // Populate preset combo with built-in presets — each item is a small
        // swatch + label so users can identify gradients at a glance.
        PresetCombo.Items.Clear();
        PresetCombo.Items.Add(BuildPresetItem(new BrandPreset { Name = "(Custom)" }, isCustom: true));
        foreach (var preset in DefaultBrandPresets.All)
            PresetCombo.Items.Add(BuildPresetItem(preset, isCustom: false));

        // Load system wallpapers (async). Pass the project's currently-selected
        // background image so a custom path from a reopened project is merged
        // into the grid and selection survives the async load.
        _ = LoadSystemWallpapersAsync(config.Background.BackgroundImagePath);

        // Sync controls to current config, suppressing change events
        SyncStyleControlsToConfig(config.Background);
    }

    private async Task LoadSystemWallpapersAsync(string? initialCustomPath = null)
    {
        var wallpaperDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");

        // Enumerate and sort files on a background thread to avoid freezing the UI
        var systemPaths = await Task.Run(() =>
        {
            if (!Directory.Exists(wallpaperDir))
                return new List<string>();

            return Directory.GetFiles(wallpaperDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => new FileInfo(f).Length)
                .ToList();
        });

        // Preserve any custom (non-system) paths the user already picked while
        // the system load was in flight — otherwise we'd silently drop them.
        var existingCustom = (_wallpaperPaths ?? new List<string>())
            .Where(p => !p.StartsWith(wallpaperDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrEmpty(initialCustomPath)
            && !initialCustomPath.StartsWith(wallpaperDir, StringComparison.OrdinalIgnoreCase)
            && !existingCustom.Any(p => string.Equals(p, initialCustomPath, StringComparison.OrdinalIgnoreCase)))
        {
            existingCustom.Insert(0, initialCustomPath);
        }

        _wallpaperPaths = existingCustom.Concat(systemPaths).ToList();

        WallpaperGrid.Items.Clear();
        WallpaperGrid.Items.Add(BuildAddWallpaperTile());
        foreach (var path in _wallpaperPaths)
        {
            WallpaperGrid.Items.Add(BuildWallpaperTile(path));
        }

        // After the async load completes the synchronous SyncStyleControlsToConfig
        // call ran before the grid was populated; re-apply selection now that
        // the items exist so the user sees the active wallpaper highlighted.
        // Prefer the project's current background image (it may have changed
        // since the load started — e.g. the user picked a wallpaper while the
        // system enumeration was still running) and fall back to the initial
        // path passed in.
        var currentImagePath = ProjectService.Instance.CurrentComposition?.Background.BackgroundImagePath;
        var targetPath = !string.IsNullOrEmpty(currentImagePath) ? currentImagePath : initialCustomPath;
        if (!string.IsNullOrEmpty(targetPath))
        {
            int idx = _wallpaperPaths.FindIndex(p =>
                string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _suppressStyleEvents = true;
                try { WallpaperGrid.SelectedIndex = idx + 1; }
                finally { _suppressStyleEvents = false; }
            }
        }
    }

    // Sentinel tag used to identify the "+" tile in the wallpaper grid.
    private const string AddWallpaperTileTag = "__add_wallpaper__";

    // Wallpaper tiles are laid out in a fixed number of columns that stretch to fill the
    // pane, rather than at a fixed pixel size that leaves a ragged gap on the right.
    private const int WallpaperColumns = 3;
    private const double WallpaperTileAspect = 2.0 / 3.0; // height / width

    /// <summary>
    /// Divides the grid's width into <see cref="WallpaperColumns"/> equal columns so the
    /// tiles always span the full pane, re-running whenever the pane is resized.
    /// </summary>
    private void WallpaperGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WallpaperGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        double available = e.NewSize.Width;
        if (available <= 0) return;

        double itemWidth = Math.Floor(available / WallpaperColumns);
        if (itemWidth <= 0) return;

        panel.ItemWidth = itemWidth;
        panel.ItemHeight = Math.Round(itemWidth * WallpaperTileAspect);
    }

    private static Border BuildAddWallpaperTile()
    {
        var plus = new FontIcon
        {
            Glyph = "\uE710", // Add
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Child = plus,
            Tag = AddWallpaperTileTag,
        };
    }

    private static Border BuildWallpaperTile(string path)
    {
        var img = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
            {
                DecodePixelHeight = 96, // small thumbnails for perf
            },
        };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = img,
        };
    }

    private void SyncStyleControlsToConfig(BackgroundStyle bg)
    {
        _suppressStyleEvents = true;
        try
        {
            // Background type combo
            int typeIndex = bg.Type switch
            {
                BackgroundType.Gradient => 1,
                BackgroundType.Image => 2,
                BackgroundType.Blur => 3,
                _ => 0, // SolidColor
            };
            BgTypeCombo.SelectedIndex = typeIndex;

            // Primary color
            var primaryColor = ParseHexColor(bg.Color);
            BgColorPicker.Color = primaryColor;
            BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(primaryColor);
            BgColorText.Text = bg.Color;

            // Panel visibility based on type
            bool isGradient = bg.Type == BackgroundType.Gradient;
            bool isImage = bg.Type == BackgroundType.Image;
            GradientPanel.Visibility = isGradient ? Visibility.Visible : Visibility.Collapsed;
            WallpaperPanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            ColorPanel.Visibility = bg.Type is not BackgroundType.Blur and not BackgroundType.Image
                ? Visibility.Visible : Visibility.Collapsed;

            if (isGradient)
            {
                var endColor = ParseHexColor(bg.GradientEndColor);
                GradEndColorPicker.Color = endColor;
                GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(endColor);
                GradEndColorText.Text = bg.GradientEndColor;
                GradAngleSlider.Value = bg.GradientAngle;
            }

            if (isImage && _wallpaperPaths is not null && !string.IsNullOrEmpty(bg.BackgroundImagePath))
            {
                int wpIdx = _wallpaperPaths.FindIndex(p =>
                    string.Equals(p, bg.BackgroundImagePath, StringComparison.OrdinalIgnoreCase));
                // +1 because index 0 in the grid is the "+" add-tile.
                WallpaperGrid.SelectedIndex = wpIdx >= 0 ? wpIdx + 1 : -1;
            }

            // Sliders
            PaddingSlider.Value = bg.Padding;
            CornerRadiusSlider.Value = bg.CornerRadius;

            // Toggles
            ShadowToggle.IsOn = bg.ShadowEnabled;
            BorderToggle.IsOn = bg.BorderEnabled;

            // Motion blur / camera drift live on CompositionConfig, not on BackgroundStyle,
            // so they're read straight from the current composition (falling back to record
            // defaults) rather than from the `bg` parameter passed in here.
            var motionConfig = ProjectService.Instance.CurrentComposition;
            var motionBlur = motionConfig?.MotionBlur ?? new MotionBlurSettings();
            var cameraDrift = motionConfig?.CameraDrift ?? new CameraDriftSettings();
            MotionBlurToggle.IsOn = motionBlur.Enabled;
            MotionBlurSlider.Value = motionBlur.Strength * 100.0;
            MotionBlurSlider.IsEnabled = motionBlur.Enabled;
            CameraDriftToggle.IsOn = cameraDrift.Enabled;
            CameraDriftSlider.Value = cameraDrift.Strength * 50.0;
            CameraDriftSlider.IsEnabled = cameraDrift.Enabled;

            // Select matching preset or (Custom)
            PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        }
        finally
        {
            _suppressStyleEvents = false;
        }
    }

    private static ComboBoxItem BuildPresetItem(BrandPreset preset, bool isCustom)
    {
        var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 32,
            Height = 18,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isCustom)
        {
            swatch.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];
        }
        else
        {
            swatch.Fill = BuildPresetSwatchBrush(preset);
        }

        var label = new TextBlock
        {
            Text = preset.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        panel.Children.Add(swatch);
        panel.Children.Add(label);

        return new ComboBoxItem
        {
            Content = panel,
            Tag = preset,
        };
    }

    private static Microsoft.UI.Xaml.Media.Brush BuildPresetSwatchBrush(BrandPreset preset)
    {
        var start = ParseHexColor(preset.BackgroundColor);

        if (preset.BackgroundType != BackgroundType.Gradient
            || string.IsNullOrEmpty(preset.GradientEndColor))
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(start);
        }

        var end = ParseHexColor(preset.GradientEndColor);

        // Convert angle (degrees, 0° = →, 90° = ↓) to start/end points on the
        // unit square so the swatch preview matches the rendered background.
        var (sp, ep) = AngleToGradientEndpoints(preset.GradientAngle);

        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = sp,
            EndPoint = ep,
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = start, Offset = 0 });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = end, Offset = 1 });
        return brush;
    }

    private static (Point Start, Point End) AngleToGradientEndpoints(double angleDegrees)
    {
        // Normalize to [0, 360).
        double a = angleDegrees % 360.0;
        if (a < 0) a += 360.0;
        double rad = a * Math.PI / 180.0;

        // Direction vector for the gradient line.
        double dx = Math.Cos(rad);
        double dy = Math.Sin(rad);

        // Project from center (0.5, 0.5) to the box edge along ±direction.
        // Scale so the longer axis fills the unit square diagonally.
        double scale = 0.5 / Math.Max(Math.Abs(dx), Math.Abs(dy));
        double hx = dx * scale;
        double hy = dy * scale;

        return (new Point(0.5 - hx, 0.5 - hy), new Point(0.5 + hx, 0.5 + hy));
    }

    private int FindMatchingPresetIndex(BackgroundStyle bg)
    {
        var presets = DefaultBrandPresets.All;
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            if (p.BackgroundType == bg.Type &&
                string.Equals(p.BackgroundColor, bg.Color, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeHex(p.GradientEndColor), NormalizeHex(bg.GradientEndColor), StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.GradientAngle - bg.GradientAngle) < 0.5 &&
                p.Padding == bg.Padding &&
                p.CornerRadius == bg.CornerRadius &&
                p.ShadowEnabled == bg.ShadowEnabled &&
                p.ShadowBlur == bg.ShadowBlur &&
                Math.Abs(p.ShadowOpacity - bg.ShadowOpacity) < 0.001 &&
                string.Equals(NormalizeHex(p.ShadowColor), NormalizeHex(bg.ShadowColor), StringComparison.OrdinalIgnoreCase) &&
                p.BorderEnabled == bg.BorderEnabled &&
                p.BorderWidth == bg.BorderWidth &&
                string.Equals(NormalizeHex(p.BorderColor), NormalizeHex(bg.BorderColor), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BackgroundImagePath ?? string.Empty, bg.BackgroundImagePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1; // +1 for the "(Custom)" entry at index 0
            }
        }
        return 0; // Custom
    }

    private static string NormalizeHex(string? hex) => string.IsNullOrEmpty(hex) ? string.Empty : hex.Trim();

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        if (PresetCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not BrandPreset preset) return;
        if (preset.Name == "(Custom)") return;

        var bg = BrandPresetConverter.ToBackgroundStyle(preset);
        SyncStyleControlsToConfig(bg);
        ApplyBackgroundStyle(bg);
    }

    private void BgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        var selectedType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        GradientPanel.Visibility = selectedType == BackgroundType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        WallpaperPanel.Visibility = selectedType == BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = selectedType is not BackgroundType.Blur and not BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;

        ScheduleStyleUpdate();
    }

    private void BgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        BgColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void GradEndColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        GradEndColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void StyleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    private void StyleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    // Motion (motion blur / camera drift) — deliberately separate from StyleToggle_Toggled
    // / StyleSlider_ValueChanged: those funnel into ApplyBackgroundStyle, which routes to a
    // selected video segment's FrameStyleOverride. Motion settings are global-only (like
    // ZoomScope), so they must never take that per-segment path.
    private void MotionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        // Keep the paired slider's enabled state in sync with its toggle immediately.
        if (ReferenceEquals(sender, MotionBlurToggle))
            MotionBlurSlider.IsEnabled = MotionBlurToggle.IsOn;
        else if (ReferenceEquals(sender, CameraDriftToggle))
            CameraDriftSlider.IsEnabled = CameraDriftToggle.IsOn;

        CommitMotionSettings();
        ScheduleMotionPreviewRebuild();
    }

    private void MotionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        CommitMotionSettings();
        ScheduleMotionPreviewRebuild();
    }

    /// <summary>
    /// Writes the motion controls straight into the live composition. This is
    /// deliberately NOT debounced: the model must be current the instant the user
    /// lets go of a control, because saving, exporting, and re-syncing the pane on
    /// segment selection all read <see cref="ProjectService.CurrentComposition"/>
    /// directly. Deferring the write means a save or export landing inside the
    /// debounce window silently uses the previous settings, and a segment selection
    /// re-syncs the controls from the stale model and visually reverts the edit.
    /// Only the expensive part — rebuilding the preview renderer — is debounced.
    /// </summary>
    private void CommitMotionSettings()
    {
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        // Motion blur / camera drift are global (CompositionConfig-only) settings, mirroring
        // ZoomScope/AspectRatio/FitMode/CropAnchor — there is no per-project field to mirror
        // them onto. Rebuild `with` the existing sub-record so tuned values (shutter angle,
        // per-channel strengths, sample caps, etc.) survive; a `new` record would reset
        // them to their Musio.Core defaults.
        ProjectService.Instance.CurrentComposition = config with
        {
            MotionBlur = config.MotionBlur with
            {
                Enabled = MotionBlurToggle.IsOn,
                Strength = (float)(MotionBlurSlider.Value / 100.0),
            },
            CameraDrift = config.CameraDrift with
            {
                Enabled = CameraDriftToggle.IsOn,
                Strength = (float)(CameraDriftSlider.Value / 50.0),
            },
        };
    }

    private void ScheduleMotionPreviewRebuild()
    {
        // Separate timer/instance from _styleDebounceTimer so a motion slider drag never
        // races or gets coalesced with an in-flight background-style update.
        if (_motionDebounceTimer is null)
        {
            _motionDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _motionDebounceTimer.Tick += (_, _) =>
            {
                _motionDebounceTimer.Stop();
                RefreshPreviewForMotionSettings();
            };
        }
        _motionDebounceTimer.Stop();
        _motionDebounceTimer.Start();
    }

    private void RefreshPreviewForMotionSettings()
    {
        // Re-read rather than capturing at schedule time, so this always rebuilds from
        // the newest committed settings even if several edits coalesced into one tick.
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void WallpaperGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        // If user selected the "+" add tile, open a file picker.
        if (WallpaperGrid.SelectedItem is Border b && (b.Tag as string) == AddWallpaperTileTag)
        {
            int previousIndex = -1;
            if (e.RemovedItems.Count > 0)
            {
                previousIndex = WallpaperGrid.Items.IndexOf(e.RemovedItems[0]);
            }
            _ = PickCustomWallpaperAsync(previousIndex);
            return;
        }

        ScheduleStyleUpdate();
    }

    private async Task PickCustomWallpaperAsync(int previousIndex)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");

        var window = App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        Windows.Storage.StorageFile? file = null;
        try
        {
            file = await picker.PickSingleFileAsync();
        }
        catch
        {
            // Picker can throw on cancellation in some scenarios — treat as no-op.
        }

        _suppressStyleEvents = true;
        try
        {
            if (file is null)
            {
                // User cancelled — restore previous selection (or clear).
                WallpaperGrid.SelectedIndex = previousIndex >= 1 ? previousIndex : -1;
                return;
            }

            string path = file.Path;
            _wallpaperPaths ??= new List<string>();

            int existing = _wallpaperPaths.FindIndex(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                // Insert at the top of the list (right after the "+" tile).
                _wallpaperPaths.Insert(0, path);
                WallpaperGrid.Items.Insert(1, BuildWallpaperTile(path));
                WallpaperGrid.SelectedIndex = 1;
            }
            else
            {
                WallpaperGrid.SelectedIndex = existing + 1;
            }
        }
        finally
        {
            _suppressStyleEvents = false;
        }

        ScheduleStyleUpdate();
    }

    private void ScheduleStyleUpdate()
    {
        // Debounce rapid changes (e.g. slider drags) to avoid thrashing the renderer
        if (_styleDebounceTimer is null)
        {
            _styleDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _styleDebounceTimer.Tick += (_, _) =>
            {
                _styleDebounceTimer.Stop();
                var bg = BuildBackgroundStyleFromControls();
                ApplyBackgroundStyle(bg);
            };
        }
        _styleDebounceTimer.Stop();
        _styleDebounceTimer.Start();
    }

    private BackgroundStyle BuildBackgroundStyleFromControls()
    {
        var bgType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        string? imagePath = null;
        if (bgType == BackgroundType.Image)
        {
            // -1 because index 0 in the grid is the "+" add-tile.
            int idx = WallpaperGrid.SelectedIndex - 1;
            if (_wallpaperPaths is not null && idx >= 0 && idx < _wallpaperPaths.Count)
            {
                imagePath = _wallpaperPaths[idx];
            }
            else
            {
                // Wallpaper list hasn't finished loading yet (or selection was
                // cleared) — preserve the project's currently-applied image so a
                // background sync from another control doesn't blank it out.
                imagePath = ProjectService.Instance.CurrentComposition?.Background.BackgroundImagePath;
            }
        }

        return new BackgroundStyle
        {
            Type = bgType,
            Color = BgColorText.Text,
            GradientEndColor = GradEndColorText.Text,
            GradientAngle = GradAngleSlider.Value,
            BackgroundImagePath = imagePath,
            Padding = (int)PaddingSlider.Value,
            CornerRadius = (int)CornerRadiusSlider.Value,
            ShadowEnabled = ShadowToggle.IsOn,
            ShadowBlur = 24,
            ShadowOpacity = 0.5,
            ShadowColor = "#000000",
            BorderEnabled = BorderToggle.IsOn,
            BorderWidth = 1,
            BorderColor = "#333333",
        };

    }

    /// <summary>
    /// Builds the effective composition config for a specific video segment by
    /// layering its per-segment style overrides (frame style + cursor) on top of the
    /// global config. Global properties (aspect ratio, fit/cover mode, crop anchor,
    /// zoom scope) always come from the global config so they apply to every segment.
    /// </summary>
    private static CompositionConfig BuildSegmentConfig(CompositionConfig global, VideoSegment? seg, int previewFps)
    {
        var cfg = global with { OutputFps = previewFps };
        if (seg?.FrameStyleOverride is { } bg) cfg = cfg with { Background = bg };
        if (seg?.CursorStyleOverride is { } cur) cfg = cfg with { Cursor = cur };
        return cfg;
    }

    /// <summary>
    /// Hides the cursor when a source has no recorded cursor samples (an imported video, or a
    /// recording whose cursor log is missing). The compositor synthesizes a static centre
    /// position for such a source so it can still zoom and apply background styling, but drawing
    /// a cursor at that invented point would be pure fiction — a negative auto-hide delay keeps
    /// it hidden from the very first frame. Mirrors
    /// <c>SegmentFrameComposer.ApplyCursorAvailability</c> so preview and export agree.
    /// </summary>
    private static CompositionConfig HideCursorWhenNoSamples(CompositionConfig config, MouseRecordingData mouseData)
    {
        if (mouseData.Samples is { Count: > 0 })
            return config;

        return config with
        {
            Cursor = config.Cursor with
            {
                AutoHideEnabled = true,
                AutoHideDelaySeconds = -1f,
                AutoHideFadeDuration = 0f,
            },
        };
    }

    /// <summary>The currently selected primary-track video segment, if any.</summary>
    private VideoSegment? SelectedVideoSegment =>
        _selectedPrimarySegmentId is { } id
            ? ViewModel.Model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == id)
            : null;

    /// <summary>
    /// Returns the primary-file video segment under the playhead (for choosing the
    /// per-segment frame style/cursor the primary renderer should use), or the first
    /// primary segment as a fallback.
    /// </summary>
    private VideoSegment? ActivePrimaryVideoSegment()
    {
        var model = ViewModel.Model;
        if (model.Segments.Count == 0) return null;
        var primary = PrimaryVideoPath;

        bool IsPrimary(VideoSegment v) =>
            primary is null || string.Equals(v.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);

        var (seg, _) = model.GetSegmentAtTime(Timeline.PlayheadPosition);
        if (seg is VideoSegment under && IsPrimary(under)) return under;
        return model.Segments.OfType<VideoSegment>().FirstOrDefault(IsPrimary);
    }

    /// <summary>Disposes and clears cached appended-segment preview contexts so they
    /// rebuild with fresh (global or per-segment) config on next render.</summary>
    private void InvalidateSegmentPreviews()
    {
        DisposeSegmentPreviews();
        _lastRenderedSegmentId = null;
    }

    /// <summary>
    /// Ensures the primary renderer was built with the given primary-file segment's
    /// per-segment frame style / cursor override. Rebuilds it when the override
    /// differs (e.g. the playhead crossed into a differently-styled primary split).
    /// No-op for appended segments (they use their own renderers).
    /// </summary>
    private async Task EnsurePrimaryRendererForSegmentAsync(VideoSegment seg)
    {
        var primary = PrimaryVideoPath;
        bool isPrimary = primary is null ||
            string.Equals(seg.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);
        if (!isPrimary) return;

        var global = ProjectService.Instance.CurrentComposition;
        var wantBg = seg.FrameStyleOverride ?? global.Background;
        var wantCursor = seg.CursorStyleOverride ?? global.Cursor;

        if (Equals(wantBg, _primaryRenderBackground) && Equals(wantCursor, _primaryRenderCursor))
            return;

        // Rebuild FOR THIS SEGMENT. Letting the rebuild re-derive the style from the
        // playhead instead would not converge: whenever the playhead sits on a
        // different segment than the one being rendered (a text slide, a slide↔video
        // crossfade, or simply playback having advanced), the rebuild records segment
        // A's style while this guard keeps testing segment B's, so every frame rebuilt
        // the compositor again — reloading the cursor recording from disk and
        // reallocating render targets on the UI thread until the app froze.
        await RebuildPreviewRendererAsync(global, seg);
    }

    private void ApplyBackgroundStyle(BackgroundStyle bg)
    {
        // Mark preset as Custom if it no longer matches
        _suppressStyleEvents = true;
        PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        _suppressStyleEvents = false;

        // Frame style is per-segment: when a video segment is selected, store the
        // override on it and rebuild only that segment's renderer. Otherwise update
        // the global config so segments without an override follow it.
        if (SelectedVideoSegment is { } seg)
        {
            seg.FrameStyleOverride = bg;
            _ = RebuildForSegmentStyleChangeAsync(seg);
            return;
        }

        var config = ProjectService.Instance.CurrentComposition with { Background = bg };
        ProjectService.Instance.CurrentComposition = config;
        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    /// <summary>
    /// Rebuilds the preview after a per-segment style override change. If the edited
    /// segment is an appended recording, its cached preview is dropped so it rebuilds
    /// with the new override; if it is the primary recording (and currently active),
    /// the primary renderer is rebuilt.
    /// </summary>
    private async Task RebuildForSegmentStyleChangeAsync(VideoSegment seg)
    {
        var primary = PrimaryVideoPath;
        bool isPrimary = primary is null ||
            string.Equals(seg.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);

        if (isPrimary)
        {
            _primaryRenderBackground = null; // force primary rebuild to pick up the override
            await RebuildPreviewRendererAsync(ProjectService.Instance.CurrentComposition);
        }
        else
        {
            if (_segmentPreviews.Remove(seg.Id, out var ctx))
            {
                DisposeOffUiThread(ctx.Reader);
                ctx.Reader = null;
                ctx.Dispose();
            }
            _lastRenderedSegmentId = null;
            _lastRenderedFrameIndex = -1;
            await UpdatePreviewFrameAsync(Timeline.PlayheadPosition, force: true);
        }
    }

    /// <summary>
    /// Recreates only the PreviewRenderer with updated config, preserving
    /// frame reader, audio, and playhead position.
    /// </summary>
    /// <param name="forSegment">
    /// The primary segment whose per-segment style the renderer must be built for. When
    /// null the segment under the playhead is used. Callers that rebuild in order to
    /// satisfy a specific segment (see <see cref="EnsurePrimaryRendererForSegmentAsync"/>)
    /// must pass it explicitly, otherwise the recorded style and the style their guard
    /// re-tests can disagree forever.
    /// </param>
    private async Task RebuildPreviewRendererAsync(
        CompositionConfig config, VideoSegment? forSegment = null)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        // A rebuild ends by re-rendering, and rendering can ask for another rebuild, so
        // this must not recurse. Requests that arrive mid-rebuild are coalesced rather
        // than dropped: the style handlers call this fire-and-forget, so dropping would
        // lose the last value of a slider drag.
        if (_rebuildingPreviewRenderer)
        {
            _pendingRendererRebuild = (config, forSegment);
            return;
        }

        _rebuildingPreviewRenderer = true;
        try
        {
            await RebuildPreviewRendererCoreAsync(project, config, forSegment);

            // Drain coalesced requests. Bounded purely as a backstop — the segment-scoped
            // rebuild above converges — so a future regression degrades to a stale frame
            // rather than to a frozen app.
            for (int i = 0; i < MaxCoalescedRendererRebuilds && _pendingRendererRebuild is { } pending; i++)
            {
                _pendingRendererRebuild = null;
                if (ProjectService.Instance.CurrentProject is not { } current) break;
                await RebuildPreviewRendererCoreAsync(current, pending.Config, pending.Segment);
            }

            _pendingRendererRebuild = null;
        }
        finally
        {
            _rebuildingPreviewRenderer = false;
        }
    }

    private const int MaxCoalescedRendererRebuilds = 4;
    private bool _rebuildingPreviewRenderer;
    private (CompositionConfig Config, VideoSegment? Segment)? _pendingRendererRebuild;

    private async Task RebuildPreviewRendererCoreAsync(
        Project project, CompositionConfig config, VideoSegment? forSegment)
    {
        // Apply the active primary segment's per-segment frame style / cursor override
        // on top of the global config so the primary recording honors its own style.
        var activePrimary = forSegment ?? ActivePrimaryVideoSegment();
        var effective = config;
        if (activePrimary?.FrameStyleOverride is { } bg) effective = effective with { Background = bg };
        if (activePrimary?.CursorStyleOverride is { } cur) effective = effective with { Cursor = cur };
        _primaryRenderBackground = effective.Background;
        _primaryRenderCursor = effective.Cursor;
        _primaryRendererSegmentId = activePrimary?.Id;

        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data */ }
        }

        mouseData ??= new MouseRecordingData();

        // An imported video (or any source missing its cursor log) has no real cursor to draw;
        // the compositor still zooms and styles it, but the invented centre cursor must stay
        // hidden — same rule as the appended-segment and export paths.
        effective = HideCursorWhenNoSamples(effective, mouseData);

        // Every cached alt-style compositor (see GetPrimaryTransitionCompositorAsync) was
        // built by layering the OLD global config onto its own override, and this rebuild
        // is precisely a global-config (or active-segment-style) change — reusing one past
        // this point would composite against stale crop/zoom/aspect settings. Also bumps
        // _primaryPreviewStateGeneration so any transition compose already in flight
        // against the OLD singleton detects this rebuild instead of using it past disposal.
        DisposePrimaryStyleRenderers();

        _compositorReady = false;
        _previewRenderer?.Dispose();

        try
        {
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, effective,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);

            // Re-sync zoom state from the model. The new compositor has just regenerated
            // auto-zoom from the raw mouse data, so this has to run unconditionally —
            // an empty keyframe list and an empty suppression set are both meaningful.
            SyncZoomStateToRenderer();

            _compositorReady = true;
        }
        catch
        {
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Re-render at current playhead position
        _lastRenderedFrameIndex = -1;
        var position = Timeline.PlayheadPosition;
        await UpdatePreviewFrameAsync(position, force: true);
    }

    // Tracks the per-segment style the primary renderer was last built with, so it
    // can be rebuilt when the playhead crosses into a primary segment with a
    // different frame style / cursor override.
    private string? _primaryRendererSegmentId;
    private BackgroundStyle? _primaryRenderBackground;
    private CursorStyle? _primaryRenderCursor;

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
            byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
            byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
            return Color.FromArgb(255, r, g, b);
        }
        return Color.FromArgb(255, 26, 26, 46); // fallback
    }

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ─── Webcam Overlay Drag / Resize ──────────────────────────────────

    private void InitializeWebcamOverlay(CompositionConfig config)
    {        _hasWebcamOverlay = _webcamComposition is not null && config.WebcamStyle is not null;
        if (!_hasWebcamOverlay)
        {
            WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Video, false);
            return;
        }

        var style = config.WebcamStyle!;
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        int outH = _previewRenderer?.OutputHeight ?? 1080;

        // Determine initial normalized position from style
        _webcamNormSize = style.Size / outW;
        if (style.NormalizedX.HasValue && style.NormalizedY.HasValue)
        {
            _webcamNormX = style.NormalizedX.Value;
            _webcamNormY = style.NormalizedY.Value;
        }
        else
        {
            float margin = style.Margin;
            float size = style.Size;
            (float px, float py) = style.Position switch
            {
                WebcamPosition.TopLeft => (margin, margin),
                WebcamPosition.TopRight => (outW - size - margin, margin),
                WebcamPosition.BottomLeft => (margin, outH - size - margin),
                _ => (outW - size - margin, outH - size - margin),
            };
            _webcamNormX = px / outW;
            _webcamNormY = py / outH;
        }

        WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Video, true);
        SyncWebcamOverlayUI(style);
        UpdateWebcamOverlayPosition();
    }

    private void UpdateWebcamOverlayPosition()
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0) return;

        double screenX = layout.X + _webcamNormX * layout.Width;
        double screenY = layout.Y + _webcamNormY * layout.Height;
        double screenSize = _webcamNormSize * layout.Width;

        Canvas.SetLeft(WebcamOverlayRect, screenX);
        Canvas.SetTop(WebcamOverlayRect, screenY);
        WebcamOverlayRect.Width = screenSize;
        WebcamOverlayRect.Height = screenSize;
    }

    private void WebcamOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Layout updates are handled by Preview.FrameLayoutChanged
    }

    private void WebcamOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_hasWebcamOverlay) return;

        _webcamDragging = true;
        _webcamDragStart = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        _webcamDragStartNormX = _webcamNormX;
        _webcamDragStartNormY = _webcamNormY;

        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void WebcamOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        var pos = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0) return;

        double dx = pos.X - _webcamDragStart.X;
        double dy = pos.Y - _webcamDragStart.Y;

        _webcamNormX = (float)Math.Clamp(
            _webcamDragStartNormX + dx / layout.Width, 0, 1 - _webcamNormSize);
        _webcamNormY = (float)Math.Clamp(
            _webcamDragStartNormY + dy / layout.Height, 0, 1 - _webcamNormSize * layout.Width / layout.Height);

        UpdateWebcamOverlayPosition();

        // Live-update the compositor so the webcam video moves in real-time
        UpdateWebcamStyleLive();

        e.Handled = true;
    }

    private void WebcamOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        _webcamDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        ApplyWebcamOverlayChange();
    }

    private void UpdateWebcamStyleLive()
    {
        if (_previewRenderer is null) return;

        int outW = _previewRenderer.OutputWidth;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };

        // Lightweight update — just change the style, re-render current frame
        _previewRenderer.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void ApplyWebcamOverlayChange()
    {
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;
    }

    private void WebcamShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (WebcamShapeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        var shape = tag == "RoundedRect" ? WebcamShape.RoundedRect : WebcamShape.Circle;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Shape = shape };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void WebcamBorderSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { BorderWidth = (float)e.NewValue };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private bool _suppressWebcamEvents;

    private void SyncWebcamOverlayUI(WebcamOverlayStyle style)
    {
        _suppressWebcamEvents = true;
        WebcamShapeCombo.SelectedIndex = style.Shape == WebcamShape.RoundedRect ? 1 : 0;
        WebcamBorderSlider.Value = style.BorderWidth;
        WebcamMirrorToggle.IsOn = style.Mirrored;
        _suppressWebcamEvents = false;
    }

    private void WebcamMirrorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Mirrored = WebcamMirrorToggle.IsOn };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    // ─── Aspect Ratio / Fit / Crop Anchor ──────────────────────────────

    private bool _suppressAspectRatioEvents;

    // Set once the user picks a fit mode explicitly, so the Contain default applied when a
    // real aspect ratio is first chosen never overwrites a deliberate choice.
    private bool _fitModeChosenByUser;

    /// <summary>
    /// Initializes the aspect ratio flyout from the current project + composition state.
    /// Safe to call multiple times; uses a suppression flag so event handlers don't
    /// re-write state during initialization.
    /// </summary>
    private void InitializeAspectRatioControls()
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        _suppressAspectRatioEvents = true;
        try
        {
            SelectRatioRadio(project.AspectRatio);
            SelectFitModeRadio(project.FitMode);
            SelectZoomScopeRadio(project.ZoomScope);
            SelectCropAnchorRadio(project.CropAnchorX, project.CropAnchorY);
            UpdateFitAndAnchorVisibility(project.AspectRatio);
        }
        finally
        {
            _suppressAspectRatioEvents = false;
        }
    }

    private void SelectRatioRadio(AspectRatio ratio)
    {
        var target = ratio switch
        {
            AspectRatio.Landscape16x9 => Ratio16x9,
            AspectRatio.Portrait9x16 => Ratio9x16,
            AspectRatio.Square1x1 => Ratio1x1,
            AspectRatio.Instagram4x5 => Ratio4x5,
            AspectRatio.Classic4x3 => Ratio4x3,
            AspectRatio.Tall3x4 => Ratio3x4,
            AspectRatio.Cinematic21x9 => Ratio21x9,
            _ => RatioAuto,
        };
        target.IsChecked = true;
    }

    private void SelectFitModeRadio(FitMode fit)
        => SelectSegmentedByTag(FitModeSegmented, fit.ToString());

    /// <summary>
    /// Selects the segment carrying <paramref name="tag"/>. Matching on the tag rather
    /// than a hard-coded index means the segments can be reordered in XAML without
    /// silently remapping them to the wrong values.
    /// </summary>
    private static void SelectSegmentedByTag(CommunityToolkit.WinUI.Controls.Segmented segmented, string tag)
    {
        for (int i = 0; i < segmented.Items.Count; i++)
        {
            if (segmented.Items[i] is CommunityToolkit.WinUI.Controls.SegmentedItem item
                && item.Tag is string t && t == tag)
            {
                segmented.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>The fit mode currently shown in the segmented control.</summary>
    private FitMode CurrentFitModeSelection()
        => FitModeSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem item
           && item.Tag is string tag
           && Enum.TryParse<FitMode>(tag, out var fit)
            ? fit
            : FitMode.Contain;

    private void SelectZoomScopeRadio(ZoomScope scope)
    {
        ZoomScopeSegmented.SelectedIndex = scope == ZoomScope.Source ? 1 : 0;
    }

    private void SelectCropAnchorRadio(double anchorX, double anchorY)
    {
        // Snap to nearest 0 / 0.5 / 1 on each axis to find the matching radio.
        static double NearestSnap(double v) => v < 0.25 ? 0.0 : v > 0.75 ? 1.0 : 0.5;
        double sx = NearestSnap(anchorX);
        double sy = NearestSnap(anchorY);
        string tag = $"{sx.ToString(CultureInfo.InvariantCulture)},{sy.ToString(CultureInfo.InvariantCulture)}";
        foreach (var child in CropAnchorGrid.Children)
        {
            if (child is RadioButton rb && rb.Tag is string t && t == tag)
            {
                rb.IsChecked = true;
                return;
            }
        }
        CropAnchorCenter.IsChecked = true;
    }

    private void UpdateFitAndAnchorVisibility(AspectRatio ratio)
    {
        bool ratioActive = ratio != AspectRatio.Auto;
        FitModePanel.Visibility = ratioActive ? Visibility.Visible : Visibility.Collapsed;
        bool coverActive = ratioActive && CurrentFitModeSelection() == FitMode.Cover;
        CropAnchorPanel.Visibility = coverActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AspectRatioOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<AspectRatio>(tag, out var ratio)) return;
        ApplyAspectRatio(ratio);
    }

    private void FitModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (FitModeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<FitMode>(tag, out var fit)) return;

        // Programmatic syncs are suppressed above, so reaching here means the user picked
        // the fit themselves and it must survive an Auto round-trip (see ApplyAspectRatio).
        _fitModeChosenByUser = true;
        ApplyFitMode(fit);
    }

    private void ZoomScopeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (ZoomScopeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<ZoomScope>(tag, out var scope)) return;
        ApplyZoomScope(scope);
    }

    private void CropAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)) return;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)) return;
        ApplyCropAnchor(ax, ay);
    }

    private void ApplyAspectRatio(AspectRatio ratio)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        // Fit mode is ignored while the ratio is Auto, so a value carried over from Auto was
        // never a deliberate choice. Default it to Contain the first time a real ratio makes
        // the control meaningful, so switching ratio keeps the whole frame visible instead
        // of silently cropping it; Cover stays available as the explicit alternative.
        // Once the user has picked a fit themselves it is never overridden, so cycling
        // through Auto and back does not discard it.
        bool fitBecomesRelevant = !_fitModeChosenByUser
            && project.AspectRatio == AspectRatio.Auto
            && ratio != AspectRatio.Auto;

        project.AspectRatio = ratio;
        config = config with { AspectRatio = ratio };

        if (fitBecomesRelevant && project.FitMode != FitMode.Contain)
        {
            project.FitMode = FitMode.Contain;
            config = config with { FitMode = FitMode.Contain };

            _suppressAspectRatioEvents = true;
            try { SelectFitModeRadio(FitMode.Contain); }
            finally { _suppressAspectRatioEvents = false; }
        }

        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(ratio);

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyFitMode(FitMode fit)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.FitMode = fit;
        config = config with { FitMode = fit };
        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(project.AspectRatio);

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyCropAnchor(double anchorX, double anchorY)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.CropAnchorX = anchorX;
        project.CropAnchorY = anchorY;
        config = config with { CropAnchorX = anchorX, CropAnchorY = anchorY };
        ProjectService.Instance.CurrentComposition = config;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyZoomScope(ZoomScope scope)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.ZoomScope = scope;
        config = config with { ZoomScope = scope };
        ProjectService.Instance.CurrentComposition = config;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    // ─── Text Slide & Append Recording handlers ─────────────────────────

    private string? _selectedTextSlideId;

    private void AddTextSlide_Click(object sender, RoutedEventArgs e)
    {
        var slide = new TextSlideSegment
        {
            Text = "Title",
            Duration = TimeSpan.FromSeconds(3),
        };

        var playhead = ViewModel.Model.PlayheadPosition;

        // Use the split-and-insert operation which splits the video segment
        // at the playhead, keeping audio in sync
        var operation = new SplitAndInsertTextSlideOperation(playhead, slide);
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the new slide and show properties
        _selectedTextSlideId = slide.Id;
        Timeline.SelectSegment(slide.Id);
        ShowTextSlidePanel(slide);

        Timeline.InvalidateAllCanvases();
    }

    /// <summary>
    /// Creates a new text overlay at the playhead's current source time (mapping through
    /// the same output→source logic the live preview uses) and selects it so its pane opens
    /// immediately. A no-op when the playhead can't be mapped to any source (no primary
    /// video loaded yet).
    /// </summary>
    private void AddTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (GetPlayheadSourceTimeForOverlay() is not { } mapped) return;

        var operation = new AddTextOverlayOperation(
            mapped.SourceTime, TextOverlayDefaultDuration, sourceVideoFilePath: mapped.VideoFilePath);
        ViewModel.UndoRedoManager.Execute(operation);

        Timeline.SelectedTextOverlayId = operation.CreatedId;
        _selectedTextOverlayId = operation.CreatedId;
        SyncTextOverlayUI(operation.CreatedId);
        Timeline.InvalidateAllCanvases();

        // The overlay starts at the playhead, and every animation is fully transparent on
        // its very first frame - so the thing the user just inserted would render as
        // nothing at all until they scrubbed into it, which reads as "insert is broken".
        // Park the playhead past the entrance so the new overlay is visible, selected and
        // directly editable on the preview the moment it appears.
        SeekPastOverlayEntrance(TextOverlayDefaultDuration);

        RefreshOverlayPreview();
    }

    /// <summary>Duration a newly inserted text overlay is created with.</summary>
    private static readonly TimeSpan TextOverlayDefaultDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Moves the playhead from a new overlay's start to its middle, which is past the
    /// entrance animation and before the exit, so the overlay renders at full opacity.
    /// <paramref name="sourceDuration"/> is the overlay's source-time length; the offset is
    /// applied in output time, because a sped-up segment covers that source range in
    /// proportionally less output time.
    /// </summary>
    private void SeekPastOverlayEntrance(TimeSpan sourceDuration)
    {
        var model = ViewModel.Model;
        double speed = 1.0;
        VideoSegment? owning = null;

        if (model.Segments.Count > 0 &&
            model.GetSegmentAtTime(Timeline.PlayheadPosition).Segment is VideoSegment seg)
        {
            owning = seg;
            if (seg.SpeedFactor > 0) speed = seg.SpeedFactor;
        }

        var offset = TimeSpan.FromTicks((long)(sourceDuration.Ticks / 2 / speed));
        var target = Timeline.PlayheadPosition + offset;

        // Stay inside the clip the overlay belongs to. The overlay is only active over its
        // own recording, so a midpoint that spills past the clip boundary would land on the
        // next segment (another recording, or a text slide) where it renders nothing at all
        // — the very problem this seek exists to avoid. Back off just inside the end so the
        // playhead is still within the clip rather than exactly on its exclusive edge.
        if (owning is not null && target >= owning.End)
            target = owning.End - TimeSpan.FromMilliseconds(1);

        // Never run past the end of the timeline; the overlay is still partly visible
        // wherever we land, and seeking beyond the content would blank the preview.
        var end = model.TotalSegmentsDuration;
        if (end > TimeSpan.Zero && target > end)
            target = end;
        if (target < Timeline.PlayheadPosition) target = Timeline.PlayheadPosition;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;

        Preview.Pause();
        Timeline.PlayheadPosition = target;
        Preview.PlayheadPosition = target;
        model.PlayheadPosition = target;
        _ = UpdatePreviewFrameAsync(target, force: true);
    }

    /// <summary>
    /// Maps the playhead's output-time position to the source time (and owning recording)
    /// a newly-created text overlay should be authored against, mirroring the output→source
    /// mapping the live preview renders with. Returns null when there is nothing to author
    /// the overlay against (no primary video, or the playhead sits over a non-video segment
    /// such as a text slide).
    /// </summary>
    private (string? VideoFilePath, TimeSpan SourceTime)? GetPlayheadSourceTimeForOverlay()
    {
        var model = ViewModel.Model;
        var position = Timeline.PlayheadPosition;

        if (model.Segments.Count > 0)
        {
            var (segment, localOffset) = model.GetSegmentAtTime(position);
            if (segment is not VideoSegment videoSeg) return null;

            var sourceInSeg = videoSeg.SourceStart +
                TimeSpan.FromTicks((long)(localOffset.Ticks * videoSeg.SpeedFactor));

            // null SourceVideoFilePath means "the primary recording" (mirrors
            // ZoomKeyframe/TextOverlaySegment's convention), so map the primary video's own
            // segments to null rather than storing its path explicitly.
            bool isPrimary = string.Equals(videoSeg.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase);
            return (isPrimary ? null : videoSeg.VideoFilePath, sourceInSeg);
        }

        // Legacy (pre-segment) projects: the whole timeline is the primary recording.
        if (string.IsNullOrEmpty(PrimaryVideoPath)) return null;
        return (null, MapToSourceTime(position));
    }

    private void RecordMore_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to RecordingPage in append mode
        Preview?.Pause();
        _audioPlayer?.Stop();
        Frame.Navigate(typeof(RecordingPage), "append");
    }

    /// <summary>
    /// Lets the user pick an external video file and inserts it into the project. The file is
    /// normalised by <see cref="VideoImportService"/> (transcoded to a constant-frame-rate
    /// H.264 clip, its audio extracted to WAV) so the rest of the editor can treat it exactly
    /// like an appended recording — the only difference being that it carries no cursor, click
    /// or keystroke data.
    /// </summary>
    private async void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        foreach (var ext in VideoImportService.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        InitializePicker(picker);

        Windows.Storage.StorageFile? file = null;
        try { file = await picker.PickSingleFileAsync(); }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Import picker failed: {ex.Message}");
        }
        if (file is null) return;

        // Import transcodes the whole file, so it can run for many seconds; surface progress
        // and a cancel path rather than freezing the UI on a silent await.
        using var cts = new CancellationTokenSource();
        var (dialog, bar) = BuildImportProgressDialog(cts);
        var progress = new Progress<double>(p =>
        {
            // Progress arrives on a background thread; marshal the bound update onto the UI.
            DispatcherQueue.TryEnqueue(() => bar.Value = Math.Clamp(p, 0, 1) * 100);
        });

        var showTask = dialog.ShowAsync();
        string? errorMessage = null;
        try
        {
            var result = await VideoImportService.ImportAsync(file.Path, null, progress, cts.Token);

            // The import staying on this page means the editor must be told to reload; appending
            // fires ProjectService.ProjectChanged, which the EditorViewModel turns into a
            // ModelReloaded the page already handles (timeline swap + preview re-init).
            ProjectService.Instance.ImportVideo(result);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — nothing to report.
        }
        catch (VideoImportException ex)
        {
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Video import failed: {ex}");
            errorMessage = ex.Message;
        }
        finally
        {
            // Fully close the progress dialog before anything else opens one: only a single
            // ContentDialog may be shown at a time, so the error dialog below must wait for it.
            dialog.Hide();
            try { await showTask; } catch { /* dialog dismissed */ }
        }

        if (errorMessage is not null)
            await ShowProjectDialogAsync("Could not import video", errorMessage);
    }

    /// <summary>
    /// Builds the modal progress dialog shown while a video import runs, wiring its Cancel
    /// button to the supplied token source. Returns the dialog and the progress bar so the
    /// caller can drive it from an <see cref="IProgress{T}"/>.
    /// </summary>
    private (ContentDialog Dialog, ProgressBar Bar) BuildImportProgressDialog(CancellationTokenSource cts)
    {
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 260,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Transcoding and importing the video…",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(bar);

        var dialog = new ContentDialog
        {
            Title = "Importing video",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        // Closing (via the Cancel button or Esc) requests cancellation; ImportAsync observes
        // the token and throws OperationCanceledException, which the handler swallows.
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        return (dialog, bar);
    }

    private void RemoveTextSlide_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTextSlideId is null) return;
        var operation = new RemoveSegmentOperation(_selectedTextSlideId);
        ViewModel.UndoRedoManager.Execute(operation);
        _selectedTextSlideId = null;
        HideTextSlidePanel();
        Timeline.InvalidateAllCanvases();
    }

    private void SlideTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;
        slide.Text = SlideTextBox.Text;
        Timeline.InvalidateAllCanvases();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void SlideAnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null ||
            SlideAnimationCombo.SelectedItem is not ComboBoxItem item) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        if (Enum.TryParse<TextSlideAnimation>(item.Tag?.ToString(), out var anim))
            slide.Animation = anim;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void SlideDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.Duration = TimeSpan.FromSeconds(args.NewValue);
        ViewModel.Model.RecalculateSegmentPositions();
        Timeline.InvalidateAllCanvases();
        InvalidatePreview();
    }

    private void SlideFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;
        slide.FontSize = args.NewValue;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private bool _suppressSlideEvents;

    private void ShowTextSlidePanel(TextSlideSegment slide)
    {
        if (PropertiesPanel is null) return;

        _suppressSlideEvents = true;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextSlide, true);
        if (ZoomSegmentPanel is not null) ZoomSegmentPanel.Visibility = Visibility.Collapsed;

        SlideTextBox.Text = slide.Text;
        SlideDurationBox.Value = slide.Duration.TotalSeconds;
        SlideFontSizeBox.Value = slide.FontSize;

        // Formatting toggles
        SlideBoldToggle.IsChecked = slide.IsBold;
        SlideItalicToggle.IsChecked = slide.IsItalic;
        SlideAlignSegmented.SelectedIndex = slide.TextAlignment switch
        {
            SlideTextAlignment.Left => 0,
            SlideTextAlignment.Right => 2,
            _ => 1,
        };

        // Font dropdown
        SetSlideFontSelection(slide.FontFamily);

        // Set animation combo
        var animName = slide.Animation.ToString();
        for (int i = 0; i < SlideAnimationCombo.Items.Count; i++)
        {
            if (SlideAnimationCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == animName)
            {
                SlideAnimationCombo.SelectedIndex = i;
                break;
            }
        }

        // Color swatches + pickers
        UpdateSlideColorSwatch(SlideTextColorSwatch, SlideTextColorText, SlideTextColorPicker, slide.TextColor);
        UpdateSlideColorSwatch(SlideBgColorSwatch, SlideBgColorText, SlideBgColorPicker, slide.BackgroundColor);
        UpdateSlideColorSwatch(SlideGradEndColorSwatch, SlideGradEndColorText, SlideGradEndColorPicker, slide.GradientEndColor);
        SlideGradAngleSlider.Value = slide.GradientAngle;

        // Background type combo
        var bgTypeName = slide.BackgroundType.ToString();
        for (int i = 0; i < SlideBgTypeCombo.Items.Count; i++)
        {
            if (SlideBgTypeCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == bgTypeName)
            {
                SlideBgTypeCombo.SelectedIndex = i;
                break;
            }
        }

        SlideImagePathText.Text = string.IsNullOrEmpty(slide.BackgroundImagePath)
            ? "No image selected" : System.IO.Path.GetFileName(slide.BackgroundImagePath);

        BuildGradientPresetsIfNeeded();
        UpdateSlideBgPanels(slide.BackgroundType);

        _suppressSlideEvents = false;

        // Reveal the text slide panel so properties are immediately editable on selection.
        PropertiesPanel.ShowPane(PropertyPaneKind.TextSlide);
    }

    private void HideTextSlidePanel()
    {
        PropertiesPanel?.SetPaneAvailable(PropertyPaneKind.TextSlide, false);
    }

    private static void UpdateSlideColorSwatch(
        Border swatch, TextBlock text, ColorPicker picker, string hex)
    {
        var color = ParseHexColor(hex);
        swatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        text.Text = hex;
        picker.Color = color;
    }

    private void SlideTextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.TextColor = ColorToHex(args.NewColor);
        SlideTextColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideTextColorText.Text = slide.TextColor;
        Timeline.InvalidateAllCanvases();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void SlideBgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.BackgroundColor = ColorToHex(args.NewColor);
        SlideBgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideBgColorText.Text = slide.BackgroundColor;
        Timeline.InvalidateAllCanvases();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private TextSlideSegment? SelectedSlide() =>
        _selectedTextSlideId is null ? null : ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);

    private void SlideBold_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        slide.IsBold = SlideBoldToggle.IsChecked == true;
        RefreshSlidePreview();
    }

    private void SlideItalic_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        slide.IsItalic = SlideItalicToggle.IsChecked == true;
        RefreshSlidePreview();
    }

    private void SlideAlignSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        if (SlideAlignSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem item
            && Enum.TryParse<SlideTextAlignment>(item.Tag?.ToString(), out var align))
        {
            slide.TextAlignment = align;
            RefreshSlidePreview();
        }
    }

    private void SlideFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        if (SlideFontCombo.SelectedItem is ComboBoxItem item && item.Tag is string font
            && !string.IsNullOrWhiteSpace(font))
        {
            slide.FontFamily = font;
            RefreshSlidePreview();
        }
    }

    /// <summary>
    /// Selects the combo item matching <paramref name="fontFamily"/>; if the slide
    /// uses a font that isn't in the curated list, it is inserted at the top so the
    /// actual font is represented (and not silently changed).
    /// </summary>
    private void SetSlideFontSelection(string fontFamily)
    {
        for (int i = 0; i < SlideFontCombo.Items.Count; i++)
        {
            if (SlideFontCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                SlideFontCombo.SelectedIndex = i;
                return;
            }
        }

        var custom = new ComboBoxItem { Content = fontFamily, Tag = fontFamily };
        try { custom.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily); }
        catch { /* unknown family — fall back to default rendering */ }
        SlideFontCombo.Items.Insert(0, custom);
        SlideFontCombo.SelectedIndex = 0;
    }

    private void RefreshSlidePreview()
    {
        // Slide content/style just changed. This used to also release a page-owned
        // crossfade-outgoing cache composed from the pre-edit slide (T3 retired that cache:
        // under the rolling transition model the outgoing side composes a different image on
        // almost every tick, so a single cached frame no longer helps — see the remarks on
        // ComposePreviewFrameAtOffsetAsync). There is nothing left to invalidate here; the next
        // dissolve tick simply recomposes from the now-current slide state.
        Timeline.InvalidateAllCanvases();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void UpdateSlideBgPanels(SlideBackgroundType type)
    {
        SlideColorPanel.Visibility = type is SlideBackgroundType.Solid or SlideBackgroundType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        SlideColorLabel.Text = type == SlideBackgroundType.Gradient ? "Start Color" : "Color";
        SlideGradientPanel.Visibility = type == SlideBackgroundType.Gradient ? Visibility.Visible : Visibility.Collapsed;
        SlideImagePanel.Visibility = type == SlideBackgroundType.Image ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SlideBgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents || SlideBgTypeCombo.SelectedItem is not ComboBoxItem item) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        if (Enum.TryParse<SlideBackgroundType>(item.Tag?.ToString(), out var type))
        {
            slide.BackgroundType = type;
            UpdateSlideBgPanels(type);
            RefreshSlidePreview();
        }
    }

    private void SlideGradEndColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        slide.GradientEndColor = ColorToHex(args.NewColor);
        SlideGradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideGradEndColorText.Text = slide.GradientEndColor;
        RefreshSlidePreview();
    }

    private void SlideGradAngleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        slide.GradientAngle = e.NewValue;
        RefreshSlidePreview();
    }

    private bool _slideGradientPresetsBuilt;

    private void BuildGradientPresetsIfNeeded()
    {
        if (_slideGradientPresetsBuilt) return;
        _slideGradientPresetsBuilt = true;

        foreach (var preset in Musio.Core.Settings.DefaultBrandPresets.All)
        {
            var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = ParseHexColor(preset.BackgroundColor), Offset = 0 });
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = ParseHexColor(preset.GradientEndColor), Offset = 1 });

            var tile = new Border
            {
                Width = 56,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = brush,
                Tag = preset,
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(tile, preset.Name);
            SlideGradientPresets.Items.Add(tile);
        }
    }

    private void SlideGradientPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        if (SlideGradientPresets.SelectedItem is not Border { Tag: Musio.Core.Settings.BrandPreset preset }) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        _suppressSlideEvents = true;
        slide.BackgroundColor = preset.BackgroundColor;
        slide.GradientEndColor = preset.GradientEndColor;
        slide.GradientAngle = preset.GradientAngle;

        UpdateSlideColorSwatch(SlideBgColorSwatch, SlideBgColorText, SlideBgColorPicker, slide.BackgroundColor);
        UpdateSlideColorSwatch(SlideGradEndColorSwatch, SlideGradEndColorText, SlideGradEndColorPicker, slide.GradientEndColor);
        SlideGradAngleSlider.Value = slide.GradientAngle;
        _suppressSlideEvents = false;

        RefreshSlidePreview();
    }

    private async void ChooseSlideImage_Click(object sender, RoutedEventArgs e)
    {
        var slide = SelectedSlide();
        if (slide is null) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        InitializePicker(picker);

        Windows.Storage.StorageFile? file = null;
        try { file = await picker.PickSingleFileAsync(); } catch { }
        if (file is null) return;

        slide.BackgroundImagePath = file.Path;
        SlideImagePathText.Text = System.IO.Path.GetFileName(file.Path);
        RefreshSlidePreview();
    }

    private static void InitializePicker(Windows.Storage.Pickers.FileOpenPicker picker)
    {
        var window = App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }

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

    // ─── In-preview text editing & repositioning ────────────────────────
    //
    // Both full-screen text slides and text overlays support the same two preview
    // gestures — drag-to-reposition and double-click-to-edit — via ITextEditTarget, a
    // small adapter that lets the shared gesture code below (originally written only for
    // TextSlideSegment) work against either kind of segment without duplicating it. The
    // two kinds differ only in how a gesture is *persisted* (see SlideTextEditTarget vs.
    // OverlayTextEditTarget): slides mutate the live segment directly (no undo, matching
    // their pre-existing behaviour); overlays commit exactly once per gesture through
    // UpdateTextOverlayPropertiesOperation so drags/edits are each a single undo step.

    private TextSlideSegment? PreviewSlide() =>
        _previewSlideId is null ? null : ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _previewSlideId);

    private TextOverlaySegment? PreviewOverlay() =>
        _previewOverlayId is null ? null : ViewModel.Model.TextOverlays
            .FirstOrDefault(o => o.Id == _previewOverlayId);

    /// <summary>
    /// Resolves whichever segment the shared text-edit canvas should currently track: the
    /// previewed slide if one is showing, otherwise the previewed overlay. A slide always
    /// wins — the render pipeline never sets both _previewSlideId and _previewOverlayId at
    /// once (see RenderFrameAtAsync/RenderTextSlidePreviewAsync/UpdateOverlayEditPreview),
    /// but the priority keeps the two concerns cleanly separated regardless.
    ///
    /// Called every drawn frame (Preview.FrameLayoutChanged / ShowTextEditOverlay), so this
    /// reuses the cached target instance (see _cachedEditTarget) instead of re-running the
    /// PreviewSlide()/PreviewOverlay() LINQ lookups and allocating a fresh adapter each
    /// time — it trusts _previewSlideId/_previewOverlayId directly (both are recomputed
    /// every render, see RenderFrameAtAsync/UpdateOverlayEditPreview) and only rebuilds the
    /// cached instance when the id (or slide-vs-overlay kind) actually changes. A gesture
    /// that is just starting must NOT use this cached instance — see
    /// GetActiveEditTargetForNewGesture.
    /// </summary>
    private ITextEditTarget? GetActiveEditTarget()
    {
        if (_previewSlideId is { } slideId)
        {
            if (_cachedEditTarget is not SlideTextEditTarget || _cachedEditTargetId != slideId)
                _cachedEditTarget = new SlideTextEditTarget(this, slideId);
            _cachedEditTargetId = slideId;
            return _cachedEditTarget;
        }

        if (_previewOverlayId is { } overlayId)
        {
            if (_cachedEditTarget is not OverlayTextEditTarget || _cachedEditTargetId != overlayId)
                _cachedEditTarget = new OverlayTextEditTarget(this, overlayId);
            _cachedEditTargetId = overlayId;
            return _cachedEditTarget;
        }

        _cachedEditTarget = null;
        _cachedEditTargetId = null;
        return null;
    }

    /// <summary>
    /// Constructs a brand-new edit-target adapter for whatever <see cref="GetActiveEditTarget"/>
    /// currently resolves to, WITHOUT touching the per-frame cache above. Used only at the
    /// moment a gesture begins (<see cref="TextEditRegion_PointerPressed"/>/
    /// <see cref="EnterTextEdit"/>) — <see cref="OverlayTextEditTarget"/> captures its
    /// pre-gesture original text/position/anchor at construction time, so reusing the
    /// per-frame cached instance here could restore a stale "original" (e.g. if the
    /// properties pane changed the overlay's position/anchor since the cache was last
    /// (re)built) instead of what is actually in effect right now.
    /// </summary>
    private ITextEditTarget? GetActiveEditTargetForNewGesture()
    {
        if (PreviewSlide() is { } slide) return new SlideTextEditTarget(this, slide.Id);
        if (PreviewOverlay() is { } overlay) return new OverlayTextEditTarget(this, overlay.Id);
        return null;
    }

    /// <summary>
    /// Recomputes which text overlay (if any) is under the playhead and should show the
    /// shared drag/edit region: the *selected* overlay, but only while the playhead's
    /// source time actually falls inside its active range and it belongs to the source
    /// video on screen (mirrors <see cref="TimelineModel.GetActiveTextOverlays"/>'s
    /// per-recording ownership rule) — otherwise the region would let you drag an overlay
    /// that isn't actually visible. Called once per rendered video frame (see
    /// RenderFrameAtAsync's VideoSegment/legacy branches).
    /// </summary>
    private void UpdateOverlayEditPreview(string? videoFilePath, TimeSpan sourceTime)
    {
        _previewOverlayId = null;
        if (_selectedTextOverlayId is { } selectedId
            && ViewModel.Model.GetActiveTextOverlays(sourceTime, videoFilePath).Any(o => o.Id == selectedId))
        {
            _previewOverlayId = selectedId;
        }

        var (w, h) = GetPreviewCanvasSize();
        _previewFrameW = w;
        _previewFrameH = h;

        // Handles both cases: shows+positions the region when _previewOverlayId (above)
        // resolved to something, or hides it when it didn't.
        ShowTextEditOverlay();
    }

    /// <summary>Commits an in-progress WYSIWYG text edit, if any, without touching the
    /// shared canvas's Visibility — used where the caller will immediately decide the
    /// canvas's Show/Hide state itself (see UpdateOverlayEditPreview).</summary>
    private void CommitActiveTextEditIfAny()
    {
        if (_editingTextId is not null)
            CommitTextEdit();
    }

    /// <summary>
    /// Shows and positions the shared text-edit overlay over whatever <see cref="GetActiveEditTarget"/>
    /// currently resolves to. Hidden during playback or zoom-region edit mode. Safe to call
    /// from any thread.
    /// </summary>
    private void ShowTextEditOverlay()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ShowTextEditOverlay);
            return;
        }

        if (Preview.IsPlaying || _zoomRegionEditMode || GetActiveEditTarget() is not { } target)
        {
            HideTextEditOverlay();
            return;
        }

        TextEditCanvas.Visibility = Visibility.Visible;
        PositionTextEditControls(target);
    }

    private void HideTextEditOverlay()
    {
        if (TextEditCanvas is null) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(HideTextEditOverlay);
            return;
        }
        CommitActiveTextEditIfAny();
        FinalizeTextBoxResize();
        TextEditCanvas.Visibility = Visibility.Collapsed;
    }

    // Last text styling pushed onto the in-place editor. PositionTextEditControls runs
    // from Preview.FrameLayoutChanged, which fires inside the Win2D draw callback, so
    // assigning a fresh FontFamily/SolidColorBrush there allocated new XAML+COM objects on
    // every drawn frame and dirtied text layout each time. One cache serves both slide and
    // overlay targets since it's keyed on the resolved style values, not the target kind.
    private (string Family, double Size, bool Bold, bool Italic, string Color, SlideTextAlignment Align)? _textEditStyle;

    private void PositionTextEditControls(ITextEditTarget target)
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || _previewFrameW <= 0) return;

        double scaleX = layout.Width / _previewFrameW;
        double scaleY = layout.Height / _previewFrameH;

        var textRect = target.ComputeRect(_previewFrameW, _previewFrameH);
        double left = layout.X + textRect.X * scaleX;
        double top = layout.Y + textRect.Y * scaleY;
        double w = textRect.Width * scaleX;
        double h = textRect.Height * scaleY;

        Canvas.SetLeft(TextEditRegion, left);
        Canvas.SetTop(TextEditRegion, top);
        TextEditRegion.Width = w;
        TextEditRegion.Height = h;

        Canvas.SetLeft(TextEditBox, left);
        Canvas.SetTop(TextEditBox, top);
        TextEditBox.Width = w;
        TextEditBox.Height = h;

        PositionTextBoxHandles(target, left, top, w, h);

        // Match the target's text styling so editing looks WYSIWYG.
        double fontSize = Math.Max(8, target.FontSize * scaleY);
        var style = (target.FontFamily, fontSize, target.IsBold, target.IsItalic,
            target.TextColor, target.TextAlignment);
        if (_textEditStyle == style)
            return;
        _textEditStyle = style;

        TextEditBox.FontSize = fontSize;
        TextEditBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(target.FontFamily);
        TextEditBox.FontWeight = target.IsBold
            ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        TextEditBox.FontStyle = target.IsItalic
            ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        TextEditBox.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseHexColor(target.TextColor));
        TextEditBox.TextAlignment = target.TextAlignment switch
        {
            SlideTextAlignment.Left => TextAlignment.Left,
            SlideTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
    }

    /// <summary>
    /// Places the 8 corner/edge grabbers (TL/T/TR/L/R/BL/B/BR — same convention as
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s handles) around the box,
    /// and hides them for targets with no user-resizable box (text slides) or while the
    /// text is being edited in place — a handle overlapping the edit box would steal the
    /// pointer from text selection. Called every drawn frame (see
    /// <see cref="PositionTextEditControls"/>, which runs from the Win2D draw callback),
    /// so each write is guarded to avoid dirtying layout when nothing actually moved.
    /// </summary>
    private void PositionTextBoxHandles(ITextEditTarget target, double left, double top, double w, double h)
    {
        bool show = target.CanResizeBox && _editingTextId is null;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetHandleVisibility(TextEditHandleTL, visibility);
        SetHandleVisibility(TextEditHandleT, visibility);
        SetHandleVisibility(TextEditHandleTR, visibility);
        SetHandleVisibility(TextEditHandleL, visibility);
        SetHandleVisibility(TextEditHandleR, visibility);
        SetHandleVisibility(TextEditHandleBL, visibility);
        SetHandleVisibility(TextEditHandleB, visibility);
        SetHandleVisibility(TextEditHandleBR, visibility);
        if (!show) return;

        const double hh = 4; // half of the 8px handle, so it centres on its point
        SetHandlePosition(TextEditHandleTL, left - hh, top - hh);
        SetHandlePosition(TextEditHandleT, left + w / 2 - hh, top - hh);
        SetHandlePosition(TextEditHandleTR, left + w - hh, top - hh);
        SetHandlePosition(TextEditHandleL, left - hh, top + h / 2 - hh);
        SetHandlePosition(TextEditHandleR, left + w - hh, top + h / 2 - hh);
        SetHandlePosition(TextEditHandleBL, left - hh, top + h - hh);
        SetHandlePosition(TextEditHandleB, left + w / 2 - hh, top + h - hh);
        SetHandlePosition(TextEditHandleBR, left + w - hh, top + h - hh);
    }

    private static void SetHandleVisibility(Border handle, Visibility visibility)
    {
        if (handle.Visibility != visibility) handle.Visibility = visibility;
    }

    private static void SetHandlePosition(Border handle, double x, double y)
    {
        if (Canvas.GetLeft(handle) != x) Canvas.SetLeft(handle, x);
        if (Canvas.GetTop(handle) != y) Canvas.SetTop(handle, y);
    }

    // ── Box resize gesture ──

    private bool _textBoxResizing;
    private bool _textBoxResizeMoved;
    private ITextEditTarget? _resizeTarget;

    // Which handle is being dragged — "TL"/"T"/"TR"/"L"/"R"/"BL"/"B"/"BR", the same tag
    // convention RegionSelectorOverlay uses for HitTestHandle/ApplyResize/GetCursorForHandle.
    private string _resizeHandle = "";

    // The box being dragged, in FRAME pixels (not canvas pixels), mutated incrementally by
    // ApplyBoxResize on every PointerMoved tick — mirrors RegionSelectorOverlay's _selX/Y/W/H
    // working state. Frame space (rather than canvas space) is used because the final result
    // must convert directly into the segment's normalized X/Y/WidthFraction/HeightFraction.
    private double _resizeLeft, _resizeTop, _resizeWidth, _resizeHeight;

    // Canvas-space pointer position as of the last tick, used to compute this tick's delta
    // — mirrors RegionSelectorOverlay's incremental _resizeStart-updated-every-tick model.
    private Windows.Foundation.Point _resizeLastPointerCanvas;

    // The box's normalized fields as of gesture start (captured right after PrepareForDrag,
    // so it reflects any anchor-to-Custom snap), used only to decide whether the gesture
    // actually changed anything (see FinalizeTextBoxResize).
    private (double X, double Y, double W, double H) _resizeStartBox;

    /// <summary>
    /// Floor for a dragged box's width/height, as a fraction of the frame in each axis.
    /// Mirrors the old width-only handle's <c>MinTextWidthFraction</c> intent — below this
    /// the text wraps to almost nothing and the box's own handles start to collide, leaving
    /// no way to drag back out.
    /// </summary>
    private const double MinBoxFraction = 0.04;

    private void TextEditHandle_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return;
        if (sender is not Border { Tag: string tag }) return;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(GetCursorForBoxHandle(tag));
    }

    private void TextEditHandle_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textBoxResizing)
            ProtectedCursor = null;
    }

    /// <summary>Same TL/BR, TR/BL, T/B, L/R cursor mapping as
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s GetCursorForHandle.</summary>
    private static Microsoft.UI.Input.InputSystemCursorShape GetCursorForBoxHandle(string handle) => handle switch
    {
        "TL" or "BR" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
        "TR" or "BL" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
        "T" or "B" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
        "L" or "R" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        _ => Microsoft.UI.Input.InputSystemCursorShape.SizeAll,
    };

    private void TextEditHandle_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return;
        if (sender is not Border { Tag: string handleTag } handle) return;

        // A gesture is starting: build a FRESH target so its pre-gesture snapshot is the
        // state as of right now — the per-frame cached instance may predate other edits.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null || !target.CanResizeBox) return;

        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || _previewFrameW <= 0) return;

        // Snap an anchored box to Custom at its current on-screen centre (without moving
        // it) exactly like a position drag does — see PrepareForDrag's remarks. Otherwise
        // every tick below would be silently overridden by ResolveCenter re-deriving the
        // centre from the (still non-Custom) anchor instead of the edges being dragged.
        target.PrepareForDrag();

        var rect = target.ComputeRect(_previewFrameW, _previewFrameH);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _resizeHandle = handleTag;
        _resizeLeft = rect.X;
        _resizeTop = rect.Y;
        _resizeWidth = rect.Width;
        _resizeHeight = rect.Height;
        _resizeStartBox = target.Box;
        _resizeLastPointerCanvas = e.GetCurrentPoint(TextEditCanvas).Position;

        _resizeTarget = target;
        _textBoxResizing = true;
        _textBoxResizeMoved = false;
        handle.CapturePointer(e.Pointer);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(GetCursorForBoxHandle(handleTag));
        e.Handled = true;
    }

    private void TextEditHandle_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textBoxResizing || _resizeTarget is null) return;

        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0 || _previewFrameW <= 0 || _previewFrameH <= 0) return;

        var pos = e.GetCurrentPoint(TextEditCanvas).Position;
        double scaleX = layout.Width / _previewFrameW;
        double scaleY = layout.Height / _previewFrameH;

        // Convert this tick's incremental pointer movement from canvas space to frame-pixel
        // space — mirrors RegionSelectorOverlay's Grid_PointerMoved/ApplyResize incremental
        // delta model (dx/dy since the LAST tick, not since gesture start).
        double dx = (pos.X - _resizeLastPointerCanvas.X) / scaleX;
        double dy = (pos.Y - _resizeLastPointerCanvas.Y) / scaleY;
        _resizeLastPointerCanvas = pos;

        ApplyBoxResize(_resizeHandle, dx, dy);

        double newX = (_resizeLeft + _resizeWidth / 2.0) / _previewFrameW;
        double newY = (_resizeTop + _resizeHeight / 2.0) / _previewFrameH;
        double newW = _resizeWidth / _previewFrameW;
        double newH = _resizeHeight / _previewFrameH;

        if (!_textBoxResizeMoved &&
            (Math.Abs(newX - _resizeStartBox.X) > 0.001 ||
             Math.Abs(newY - _resizeStartBox.Y) > 0.001 ||
             Math.Abs(newW - _resizeStartBox.W) > 0.001 ||
             Math.Abs(newH - _resizeStartBox.H) > 0.001))
        {
            _textBoxResizeMoved = true;
        }

        _resizeTarget.Box = (newX, newY, newW, newH);
        _resizeTarget.OnLivePositionChanged();
        e.Handled = true;
    }

    /// <summary>
    /// Resizes the working frame-pixel rect for one handle, exactly mirroring
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s ApplyResize: a corner moves
    /// both axes, an edge moves one, and in every case the OPPOSITE edge/corner is left
    /// untouched so it stays put while the dragged one follows the pointer. Clamped to a
    /// minimum size per axis and then to stay fully inside the frame.
    /// </summary>
    private void ApplyBoxResize(string handle, double dx, double dy)
    {
        switch (handle)
        {
            case "TL": _resizeLeft += dx; _resizeTop += dy; _resizeWidth -= dx; _resizeHeight -= dy; break;
            case "T": _resizeTop += dy; _resizeHeight -= dy; break;
            case "TR": _resizeTop += dy; _resizeWidth += dx; _resizeHeight -= dy; break;
            case "L": _resizeLeft += dx; _resizeWidth -= dx; break;
            case "R": _resizeWidth += dx; break;
            case "BL": _resizeLeft += dx; _resizeWidth -= dx; _resizeHeight += dy; break;
            case "B": _resizeHeight += dy; break;
            case "BR": _resizeWidth += dx; _resizeHeight += dy; break;
        }

        double minW = MinBoxFraction * _previewFrameW;
        double minH = MinBoxFraction * _previewFrameH;
        if (_resizeWidth < minW) _resizeWidth = minW;
        if (_resizeHeight < minH) _resizeHeight = minH;

        _resizeLeft = Math.Clamp(_resizeLeft, 0, Math.Max(0, _previewFrameW - _resizeWidth));
        _resizeTop = Math.Clamp(_resizeTop, 0, Math.Max(0, _previewFrameH - _resizeHeight));
    }

    private void TextEditHandle_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border handle) handle.ReleasePointerCapture(e.Pointer);
        FinalizeTextBoxResize();
        e.Handled = true;
    }

    private void TextEditHandle_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextBoxResize();

    private void TextEditHandle_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextBoxResize();

    /// <summary>
    /// Ends a box resize exactly once, however it ended (release, capture loss or
    /// cancellation), committing a single undo entry only when the box actually changed
    /// past a small threshold and otherwise restoring the pre-gesture box.
    /// </summary>
    private void FinalizeTextBoxResize()
    {
        if (!_textBoxResizing) return;
        _textBoxResizing = false;
        ProtectedCursor = null;

        var target = _resizeTarget;
        _resizeTarget = null;
        if (target is null) return;

        if (_textBoxResizeMoved)
        {
            var (x, y, w, h) = target.Box;
            target.CommitBox(x, y, w, h);
        }
        else
        {
            target.AbortResize();
        }

        _textBoxResizeMoved = false;
    }

    private void TextEditRegion_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is null)
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
    }

    private void TextEditRegion_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging)
            ProtectedCursor = null;
    }

    private void TextEditRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return; // editing — let the textbox handle it
        // A gesture is starting: always build a FRESH target rather than reusing the
        // per-frame cache — see GetActiveEditTargetForNewGesture's remarks.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null) return;

        _textRegionDragging = true;
        _textDragMoved = false;
        _dragTarget = target; // pins the target (and, for overlays, its captured pre-drag
                               // original) for the whole gesture — see OverlayTextEditTarget

        // For an anchored overlay, snap it to Custom at its CURRENT on-screen centre
        // without visually moving it, so a live drag actually follows the pointer and a
        // plain click's release path never flips the anchor using stale raw X/Y — see
        // OverlayTextEditTarget.PrepareForDrag. A no-op for slides (they have no anchor).
        target.PrepareForDrag();

        _textDragStart = e.GetCurrentPoint(TextEditCanvas).Position;
        (_textDragStartX, _textDragStartY) = target.Center;
        TextEditRegion.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TextEditRegion_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging || _dragTarget is not { } target) return;
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0) return;

        var pos = e.GetCurrentPoint(TextEditCanvas).Position;

        // Only a real drag (past TextDragThreshold) is ever committed/undoable — see
        // FinalizeTextDrag. Sticky once set so a jittery pointer that crosses the
        // threshold and comes back still counts as a drag, not a click.
        if (!_textDragMoved &&
            (Math.Abs(pos.X - _textDragStart.X) >= TextDragThreshold ||
             Math.Abs(pos.Y - _textDragStart.Y) >= TextDragThreshold))
        {
            _textDragMoved = true;
        }

        double dx = (pos.X - _textDragStart.X) / layout.Width;
        double dy = (pos.Y - _textDragStart.Y) / layout.Height;

        // Live preview only — mutates the model directly with no undo entry. The overlay
        // target's CommitPosition (called once on release, below) restores this back to
        // the pre-drag position before committing a single undoable step; the slide target
        // just leaves it as the final, already-persisted value (matching its pre-refactor
        // direct-mutation behaviour).
        target.Center = (
            Math.Clamp(_textDragStartX + dx, 0.0, 1.0),
            Math.Clamp(_textDragStartY + dy, 0.0, 1.0));

        PositionTextEditControls(target);
        target.OnLivePositionChanged();
        e.Handled = true;
    }

    private void TextEditRegion_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging) return;
        TextEditRegion.ReleasePointerCapture(e.Pointer);
        FinalizeTextDrag();
        e.Handled = true;
    }

    /// <summary>
    /// Pointer capture can be lost without a matching PointerReleased — another window
    /// steals focus, a touch/pen gesture is cancelled by the system, or the preview
    /// re-layouts under the pointer. Without this handler the live model mutations made
    /// during PointerMoved would stay applied with no undo entry and a stale properties
    /// pane. Routes through the same idempotent <see cref="FinalizeTextDrag"/> as a normal
    /// release.
    /// </summary>
    private void TextEditRegion_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextDrag();

    /// <summary>Touch/pen gestures can be cancelled outright (distinct from capture loss);
    /// finalize the same way so an interrupted drag is never left uncommitted.</summary>
    private void TextEditRegion_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextDrag();

    /// <summary>
    /// Ends the in-progress text-region drag exactly once, whether it finished normally
    /// (PointerReleased) or was interrupted (PointerCaptureLost/PointerCanceled). Guarded by
    /// <see cref="_textRegionDragging"/>, so it is safe to call from more than one of those
    /// event handlers for the same gesture — only the first call does anything. A real
    /// drag (pointer moved past <see cref="TextDragThreshold"/>) is committed as one undo
    /// step; anything else — a plain click, or an interrupted gesture that never moved — is
    /// aborted, restoring the model to its exact pre-gesture state, so only a genuine move
    /// ever becomes an undo entry or a persisted change.
    /// </summary>
    private void FinalizeTextDrag()
    {
        if (!_textRegionDragging) return;
        _textRegionDragging = false;
        ProtectedCursor = null;

        if (_dragTarget is { } target)
        {
            if (_textDragMoved)
            {
                var (finalX, finalY) = target.Center;
                target.CommitPosition(finalX, finalY);
            }
            else
            {
                target.AbortDrag();
            }
        }
        _dragTarget = null;
        _textDragMoved = false;
    }

    private void TextEditRegion_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        EnterTextEdit();
        e.Handled = true;
    }

    private void EnterTextEdit()
    {
        // The taps that opened the editor may have started a region drag (and captured
        // the pointer) without a matching PointerReleased, because the region gets
        // collapsed below. Finalize that gesture first — via the same idempotent path a
        // normal release or an interrupted drag uses — so any live PrepareForDrag mutation
        // (e.g. an anchored overlay flipped to Custom by the opening press) is committed or
        // reverted instead of silently left in place, then release any capture outright.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        TextEditRegion.ReleasePointerCaptures();
        ProtectedCursor = null;

        // A gesture is starting: always build a FRESH target rather than reusing the
        // per-frame cache — see GetActiveEditTargetForNewGesture's remarks.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null) return;

        _editingTextId = target.Id;
        _editTarget = target; // pins the target (and, for overlays, its captured
                               // pre-edit original text) for the whole edit session
        TextEditRegion.Visibility = Visibility.Collapsed;
        SetHandleVisibility(TextEditHandleTL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleT, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleTR, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleR, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleBL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleB, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleBR, Visibility.Collapsed);
        TextEditBox.Visibility = Visibility.Visible;
        TextEditBox.Text = target.Text;
        TextEditBox.Focus(FocusState.Programmatic);
        TextEditBox.SelectAll();

        target.BeginTextEdit();
    }

    private void CommitTextEdit()
    {
        if (_editingTextId is null) return;
        _editingTextId = null;
        var target = _editTarget;
        _editTarget = null;

        TextEditBox.Visibility = Visibility.Collapsed;
        TextEditRegion.Visibility = Visibility.Visible;

        target?.CommitText(TextEditBox.Text);
    }

    private void TextEditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editTarget is not { } target) return;

        // Live model update; committed as one undoable op on exit — see CommitTextEdit.
        target.Text = TextEditBox.Text;

        // Keep whichever properties-pane textbox mirrors this target in sync. The slide
        // flyout's own TextChanged mutates the slide directly (idempotent, no undo) so it
        // needs no guard; the overlay pane's does push an undoable operation per keystroke,
        // so the programmatic assignment must be suppressed to avoid a second undo entry.
        if (target is SlideTextEditTarget)
        {
            if (SlideTextBox is not null && SlideTextBox.Text != TextEditBox.Text)
                SlideTextBox.Text = TextEditBox.Text;
        }
        else if (target is OverlayTextEditTarget)
        {
            if (OverlayTextBox is not null && OverlayTextBox.Text != TextEditBox.Text)
            {
                _suppressOverlayEvents = true;
                try { OverlayTextBox.Text = TextEditBox.Text; }
                finally { _suppressOverlayEvents = false; }
            }
        }

        // Re-measure so the edit box grows/shrinks to hug the wrapped text live
        // instead of staying at the height it had when editing started.
        PositionTextEditControls(target);
        target.OnLiveTextChanged();
        Timeline.InvalidateAllCanvases();
    }

    private void TextEditBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Enter commits (Shift+Enter inserts a newline); Esc commits too.
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if ((e.Key == Windows.System.VirtualKey.Enter && !shift)
            || e.Key == Windows.System.VirtualKey.Escape)
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private void TextEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
    }

    /// <summary>
    /// Unifies <see cref="TextSlideSegment"/> and <see cref="TextOverlaySegment"/> behind
    /// one shape so the shared drag/double-click-to-edit gesture code above (originally
    /// written only for slides) works for both without duplicating it. Implementations
    /// resolve the live segment by <see cref="Id"/> on every access rather than caching the
    /// record itself, since undo/redo and property-pane edits replace/mutate segments out
    /// from under any snapshot reference.
    /// </summary>
    private interface ITextEditTarget
    {
        string Id { get; }

        /// <summary>The segment's text. The setter is a live, non-undoable mutation used to
        /// preview keystrokes as they happen; see <see cref="CommitText"/> for the one-shot
        /// persisted change.</summary>
        string Text { get; set; }

        /// <summary>Normalized (0..1) centre of the text box. The setter is a live,
        /// non-undoable mutation used to preview a drag as it happens; see
        /// <see cref="CommitPosition"/> for the one-shot persisted change.</summary>
        (double X, double Y) Center { get; set; }

        string FontFamily { get; }
        double FontSize { get; }
        bool IsBold { get; }
        bool IsItalic { get; }
        string TextColor { get; }
        SlideTextAlignment TextAlignment { get; }

        /// <summary>The text box, in frame pixels, for a canvas of the given size.</summary>
        Rect ComputeRect(int frameWidth, int frameHeight);

        /// <summary>Called once when WYSIWYG editing begins (after <see cref="Text"/> has
        /// been read into the edit box, before any keystroke).</summary>
        void BeginTextEdit();

        /// <summary>Called once when a drag gesture begins, before <see cref="Center"/> is
        /// first read as the drag origin. A no-op for targets whose rendered position always
        /// matches <see cref="Center"/> (e.g. slides); for an anchored text overlay this
        /// snaps it to <see cref="Musio.Core.Timeline.TextOverlayAnchor.Custom"/> at its
        /// current on-screen centre WITHOUT visually moving it, so the subsequent live drag
        /// actually follows the pointer instead of being silently overridden by anchor-based
        /// rendering — see <see cref="OverlayTextEditTarget.PrepareForDrag"/>.</summary>
        void PrepareForDrag();

        /// <summary>Called after every keystroke, once <see cref="Text"/> has already been
        /// live-mutated, so implementations can nudge whatever preview needs it.</summary>
        void OnLiveTextChanged();

        /// <summary>Called after every drag tick, once <see cref="Center"/> has already been
        /// live-mutated, so implementations can repaint immediately (a drag must feel live).</summary>
        void OnLivePositionChanged();

        /// <summary>Persists <paramref name="newText"/> as the final value of a finished
        /// edit session (Enter/Esc/focus loss).</summary>
        void CommitText(string newText);

        /// <summary>Persists <paramref name="x"/>/<paramref name="y"/> as the final value of
        /// a finished drag that actually moved past the drag threshold.</summary>
        void CommitPosition(double x, double y);

        /// <summary>
        /// Whether the target's text box has a user-resizable rectangle, i.e. whether the
        /// preview should offer the 8 corner/edge grabbers. False for text slides, whose box
        /// is a fixed fraction of the slide.
        /// </summary>
        bool CanResizeBox { get; }

        /// <summary>
        /// The text box's normalized fields against the frame: <c>X</c>/<c>Y</c> are the
        /// box's centre (0..1, same semantics as <see cref="Center"/>) and <c>W</c>/<c>H</c>
        /// are its width/height fraction. The setter is a live, non-undoable mutation used
        /// to preview a resize as it happens; see <see cref="CommitBox"/> for the one-shot
        /// persisted change.
        /// </summary>
        (double X, double Y, double W, double H) Box { get; set; }

        /// <summary>Persists <paramref name="x"/>/<paramref name="y"/>/<paramref name="w"/>/
        /// <paramref name="h"/> as the final value of a finished resize that actually moved
        /// past the drag threshold.</summary>
        void CommitBox(double x, double y, double w, double h);

        /// <summary>Ends a resize gesture that never moved past the drag threshold by
        /// restoring the pre-gesture box, with no undo entry recorded.</summary>
        void AbortResize();

        /// <summary>Ends a drag gesture that never moved past the drag threshold (e.g. a
        /// plain click) by restoring the model to its exact pre-gesture state — undoing
        /// whatever <see cref="PrepareForDrag"/>/<see cref="Center"/>'s setter mutated live,
        /// with no undo entry recorded. A no-op for targets that had nothing to restore.</summary>
        void AbortDrag();
    }

    /// <summary>
    /// Adapts a <see cref="TextSlideSegment"/> to <see cref="ITextEditTarget"/>. Slides have
    /// no undo path for in-place edits today (see <see cref="SlideTextBox_TextChanged"/> et
    /// al., which all mutate the live segment directly) — every Commit* here is a plain
    /// assignment plus the same <see cref="RefreshSlidePreview"/> call the pre-refactor code
    /// used, so slide behaviour is unchanged by this abstraction.
    /// </summary>
    private sealed class SlideTextEditTarget : ITextEditTarget
    {
        private readonly EditorPage _page;
        public string Id { get; }

        public SlideTextEditTarget(EditorPage page, string id)
        {
            _page = page;
            Id = id;
        }

        private TextSlideSegment? Segment => _page.ViewModel.Model.Segments
            .OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == Id);

        public string Text
        {
            get => Segment?.Text ?? string.Empty;
            set { if (Segment is { } s) s.Text = value; }
        }

        public (double X, double Y) Center
        {
            get => Segment is { } s ? (s.TextX, s.TextY) : (0.5, 0.5);
            set { if (Segment is { } s) { s.TextX = value.X; s.TextY = value.Y; } }
        }

        public string FontFamily => Segment?.FontFamily ?? "Segoe UI";
        public double FontSize => Segment?.FontSize ?? 72;
        public bool IsBold => Segment?.IsBold ?? false;
        public bool IsItalic => Segment?.IsItalic ?? false;
        public string TextColor => Segment?.TextColor ?? "#FFFFFF";
        public SlideTextAlignment TextAlignment => Segment?.TextAlignment ?? SlideTextAlignment.Center;

        public Rect ComputeRect(int frameWidth, int frameHeight) =>
            Segment is { } s ? TextSlideRenderer.ComputeTextRect(s, frameWidth, frameHeight) : default;

        public void BeginTextEdit() => _page.RefreshSlidePreview();

        // A slide's text box is always a fixed fraction of the slide (see
        // TextSlideRenderer.ComputeTextRect), so there is no rectangle for the user to
        // define and no resize handles are offered.
        public bool CanResizeBox => false;
        public (double X, double Y, double W, double H) Box { get => (Center.X, Center.Y, 1.0, 1.0); set { } }
        public void CommitBox(double x, double y, double w, double h) { }
        public void AbortResize() { }

        // Slides have no anchor concept — Center is always authoritative, so there is
        // nothing to snap/restore around a drag.
        public void PrepareForDrag() { }

        // Slides render text directly onto the preview bitmap themselves (see
        // RenderTextSlidePreviewAsync's drawText suppression while _editingTextId is set),
        // so nothing further is needed per keystroke — matches pre-refactor behaviour.
        public void OnLiveTextChanged() { }

        public void OnLivePositionChanged() => _page.RefreshSlidePreview();

        public void CommitText(string newText)
        {
            if (Segment is { } s) s.Text = newText;
            _page.RefreshSlidePreview(); // switches drawText back on for the finished text
        }

        public void CommitPosition(double x, double y)
        {
            // Already fully persisted and repainted by the last PointerMoved tick (see
            // OnLivePositionChanged) — nothing more to do, matching the pre-refactor
            // PointerReleased handler, which did nothing beyond cursor/capture cleanup.
            if (Segment is { } s) { s.TextX = x; s.TextY = y; }
        }

        // Slides mutate TextX/TextY directly and undoably-never — a sub-threshold move is
        // already the final, persisted value (same as the pre-refactor PointerReleased
        // handler, which always applied whatever the last live tick left behind). Nothing
        // to restore here, matching pre-existing behaviour exactly.
        public void AbortDrag() { }
    }

    /// <summary>
    /// Adapts a <see cref="TextOverlaySegment"/> to <see cref="ITextEditTarget"/>. Unlike
    /// slides, every persisted change here goes through a single
    /// <see cref="UpdateTextOverlayPropertiesOperation"/> so drags and text edits are each
    /// exactly one undo step, even though the live <see cref="Text"/>/<see cref="Center"/>
    /// setters mutate the model on every keystroke/mouse-move tick for a live preview.
    /// </summary>
    private sealed class OverlayTextEditTarget : ITextEditTarget
    {
        private readonly EditorPage _page;
        public string Id { get; }

        // Captured once at construction — i.e. when a gesture begins, since
        // GetActiveEditTargetForNewGesture() is called fresh at TextEditRegion_PointerPressed
        // and EnterTextEdit, and the resulting instance is then pinned in
        // _dragTarget/_editTarget for the rest of that gesture — so Commit*/AbortDrag can
        // restore the model to its exact pre-gesture state (including the anchor, which
        // PrepareForDrag may live-flip to Custom) right before executing the single
        // undoable operation, regardless of how many live in-between mutations
        // Center/Text/PrepareForDrag made.
        private readonly (double X, double Y) _originalCenter;
        private readonly string _originalText;
        private readonly double _originalWidthFraction;
        private readonly double _originalHeightFraction;
        private readonly TextOverlayAnchor _originalAnchor;

        public OverlayTextEditTarget(EditorPage page, string id)
        {
            _page = page;
            Id = id;
            var seg = Segment;
            _originalCenter = seg is { } s ? (s.X, s.Y) : (0.5, 0.85);
            _originalText = seg?.Text ?? string.Empty;
            _originalWidthFraction = seg?.WidthFraction ?? 0.6;
            _originalHeightFraction = seg?.HeightFraction ?? 0.14;
            _originalAnchor = seg?.Anchor ?? TextOverlayAnchor.BottomCenter;
        }

        private TextOverlaySegment? Segment => _page.ViewModel.Model.TextOverlays.FirstOrDefault(o => o.Id == Id);

        public string Text
        {
            get => Segment?.Text ?? string.Empty;
            set { if (Segment is { } o) o.Text = value; } // live typing preview; see CommitText
        }

        public (double X, double Y) Center
        {
            get => Segment is { } o ? (o.X, o.Y) : (0.5, 0.85);
            set { if (Segment is { } o) { o.X = value.X; o.Y = value.Y; } } // live drag preview; see CommitPosition
        }

        public string FontFamily => Segment?.FontFamily ?? "Segoe UI";
        public double FontSize => Segment?.FontSize ?? 42;
        public bool IsBold => Segment?.IsBold ?? false;
        public bool IsItalic => Segment?.IsItalic ?? false;
        public string TextColor => Segment?.TextColor ?? "#FFFFFF";
        public SlideTextAlignment TextAlignment => Segment?.TextAlignment ?? SlideTextAlignment.Center;

        public Rect ComputeRect(int frameWidth, int frameHeight) =>
            Segment is { } o ? o.ComputeBox(frameWidth, frameHeight) : default;

        // Overlays render via the always-on compositor, which has no drawText-suppression
        // switch the way slides do — see this feature's completion report for the known
        // cosmetic follow-up (the rendered overlay text can show through under the edit box
        // while WYSIWYG editing). Nothing to do here.
        public void BeginTextEdit() { }

        public void PrepareForDrag()
        {
            if (Segment is not { } o) return;
            if (o.Anchor == TextOverlayAnchor.Custom) return; // X/Y already authoritative

            // An anchored overlay renders at TextOverlaySegment.ResolveCenter(...), not its
            // raw X/Y, so a drag must first snap it to Custom at its CURRENT on-screen
            // centre (computed exactly like the renderer/hit-region do — see
            // TextOverlaySegment.ComputeBox) before it can move at all. Skipping this would mean
            // (a) a plain click's release path flips Anchor to Custom using the stale raw
            // X/Y, visibly jumping the overlay, and (b) every drag tick's Center-setter
            // mutation is silently overridden by ResolveCenter re-deriving the centre from
            // the (still non-Custom) anchor instead of the live X/Y. This mutation is live/
            // non-undoable, exactly like every other live-preview mutation in this class —
            // CommitPosition/AbortDrag below decide whether it (and any further movement)
            // becomes permanent or is reverted.
            var rect = o.ComputeBox(_page._previewFrameW, _page._previewFrameH);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            double cx = (rect.X + rect.Width / 2.0) / _page._previewFrameW;
            double cy = (rect.Y + rect.Height / 2.0) / _page._previewFrameH;

            o.Anchor = TextOverlayAnchor.Custom;
            o.X = Math.Clamp(cx, 0.0, 1.0);
            o.Y = Math.Clamp(cy, 0.0, 1.0);
        }

        public void OnLiveTextChanged() => _page.ScheduleOverlayPreviewRefresh();
        public void OnLivePositionChanged() => _page.RefreshOverlayPreview();

        public void CommitText(string newText)
        {
            if (Segment is not { } o) return;

            // Restore-then-execute: the model currently holds whatever the last live
            // keystroke left behind (see Text's setter above); reset it to what it was
            // before the edit session started so UpdateTextOverlayPropertiesOperation's own
            // "before" snapshot (taken inside Execute) captures the true original rather
            // than an intermediate keystroke — giving one clean undo entry for the whole
            // edit instead of one per character typed.
            o.Text = _originalText;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov => ov.Text = newText, "Change Overlay Text"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        public void CommitPosition(double x, double y)
        {
            if (Segment is not { } o) return;

            // Same restore-then-execute trick as CommitText, for the drag case: put the
            // model back to exactly where it was before the gesture began — including the
            // anchor, which PrepareForDrag may have live-flipped to Custom — before
            // Execute() takes its "before" snapshot, giving one clean undo entry for the
            // whole drag instead of one per PointerMoved tick / the PrepareForDrag
            // mutation. A real drag is no longer edge-anchored, so this also flips the
            // overlay to Custom placement (mirrors OverlayAnchor_Checked's own comment
            // about Custom being "the user positioned this by hand").
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov => { ov.X = x; ov.Y = y; ov.Anchor = TextOverlayAnchor.Custom; }, "Move Text Overlay"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        // The overlay's box is what decides where its text wraps and sits, so it is worth
        // resizing directly on the preview rather than guessing with the pane's sliders.
        public bool CanResizeBox => true;

        public (double X, double Y, double W, double H) Box
        {
            get => Segment is { } o ? (o.X, o.Y, o.WidthFraction, o.HeightFraction)
                : (_originalCenter.X, _originalCenter.Y, _originalWidthFraction, _originalHeightFraction);
            set
            {
                // live resize preview; see CommitBox for the one-shot persisted change
                if (Segment is { } o)
                {
                    o.X = value.X;
                    o.Y = value.Y;
                    o.WidthFraction = value.W;
                    o.HeightFraction = value.H;
                }
            }
        }

        public void CommitBox(double x, double y, double w, double h)
        {
            if (Segment is not { } o) return;

            // Restore-then-execute, exactly as CommitPosition/CommitText do, so the whole
            // resize is one undo entry rather than one per PointerMoved tick. A hand-resized
            // box is no longer edge-anchored — mirrors CommitPosition flipping Anchor to
            // Custom once a real drag has happened.
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            o.WidthFraction = _originalWidthFraction;
            o.HeightFraction = _originalHeightFraction;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov =>
                {
                    ov.Anchor = TextOverlayAnchor.Custom;
                    ov.X = x;
                    ov.Y = y;
                    ov.WidthFraction = w;
                    ov.HeightFraction = h;
                }, "Resize Text Overlay"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        public void AbortResize()
        {
            if (Segment is not { } o) return;
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            o.WidthFraction = _originalWidthFraction;
            o.HeightFraction = _originalHeightFraction;
            _page.RefreshOverlayPreview();
        }

        public void AbortDrag()
        {
            if (Segment is not { } o) return;

            // No real movement happened (e.g. a plain click) — restore the exact
            // pre-gesture state, undoing whatever PrepareForDrag/Center's setter mutated
            // live, with NO undo entry. This is what keeps a plain click on an anchored
            // overlay from silently flipping it to Custom.
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            _page.RefreshOverlayPreview();
        }
    }
}

