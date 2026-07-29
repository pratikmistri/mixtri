using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Processing;
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

    // Blends slide↔neighbour crossfades in the preview (lazily created).
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
        public Windows.Media.Editing.MediaComposition? Webcam;
        public int WebcamW, WebcamH;
        public CanvasBitmap? LastWebcamFrame;

        public void Dispose()
        {
            Reader?.Dispose();
            Renderer?.Dispose();
            Webcam?.Clips.Clear();
            LastWebcamFrame?.Dispose();
        }
    }

    // Thumbnail generation versioning — prevents stale results
    private int _thumbnailGenerationId;

    // Webcam overlay for editor preview
    private Windows.Media.Editing.MediaComposition? _webcamComposition;
    private int _webcamWidth;
    private int _webcamHeight;
    private CanvasBitmap? _lastWebcamFrame;
    private int _lastRenderedFrameIndex = -1;
    private bool _isRendering;
    private TimeSpan? _pendingRenderPosition;
    private bool _pendingRenderForce;
    private double _audioOffsetSeconds;

    // Background style editing state
    private DispatcherTimer? _styleDebounceTimer;
    private bool _suppressStyleEvents;
    private List<string>? _wallpaperPaths;

    // Cursor style editing state
    private DispatcherTimer? _cursorDebounceTimer;
    private bool _suppressCursorEvents;

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
        WirePropertyPanels();

        Preview.Duration = GetMappedDuration();

        // Load frames and initialize compositor with cursor effects
        _ = InitializePreviewAsync();

        // Keep webcam overlay in sync with preview frame layout
        Preview.FrameLayoutChanged += (_, _) =>
        {
            if (_hasWebcamOverlay)
                UpdateWebcamOverlayPosition();

            // Keep the text-slide edit overlay aligned with the preview frame
            if (SlideEditCanvas.Visibility == Visibility.Visible && PreviewSlide() is { } s)
                PositionSlideEditControls(s);
        };

        // Hide the in-place text editor while playing
        Preview.IsPlayingChanged += (_, playing) =>
        {
            if (playing) HideSlideEditOverlay();
        };

        // Sync playhead: when timeline scrubs, update preview + audio
        Timeline.RegisterPropertyChangedCallback(
            Controls.TimelineControl.PlayheadPositionProperty,
            (_, _) =>
            {
                Preview.PlayheadPosition = Timeline.PlayheadPosition;
                ViewModel.Model.PlayheadPosition = Timeline.PlayheadPosition;
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
            Timeline.PlayheadPosition = Preview.PlayheadPosition;
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
                _timelineMapper = null;
                Timeline.ClearZoomSelection();
                Timeline.ClearClipSelection();
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

        // Export flyout state management
        ExportFlyout.Opened += ExportFlyout_Opened;
        ExportFlyout.Closed += ExportFlyout_Closed;
        ExportVM.PropertyChanged += ExportVM_PropertyChanged;

        // Clean up when page is unloaded to prevent leaks
        Unloaded += (_, _) =>
        {
            _styleDebounceTimer?.Stop();
            _styleDebounceTimer = null;
            _cursorDebounceTimer?.Stop();
            _cursorDebounceTimer = null;

            // Stop playback to halt timer ticks
            Preview.Pause();

            // Dispose owned resources
            _frameReader?.Dispose();
            _frameReader = null;
            _previewRenderer?.Dispose();
            _previewRenderer = null;
            _textSlideRenderer?.Dispose();
            _textSlideRenderer = null;
            _transitionRenderer?.Dispose();
            _transitionRenderer = null;
            foreach (var ctx in _segmentPreviews.Values) ctx.Dispose();
            _segmentPreviews.Clear();
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _webcamComposition?.Clips.Clear();
            _webcamComposition = null;
            _lastWebcamFrame?.Dispose();
            _lastWebcamFrame = null;
            _compositorReady = false;
            _thumbnailGenerationId++; // cancel any in-flight thumbnail generation
            Timeline.ClearThumbnails();

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

    private async Task InitializePreviewAsync()
    {
        _frameReader?.Dispose();
        _previewRenderer?.Dispose();
        _audioPlayer?.Dispose();
        _styleDebounceTimer?.Stop();
        _frameReader = null;
        _previewRenderer = null;
        _audioPlayer = null;
        _compositorReady = false;
        _lastRenderedFrameIndex = -1;
        _lastRenderedSegmentId = null;
        foreach (var ctx in _segmentPreviews.Values) ctx.Dispose();
        _segmentPreviews.Clear();
        _thumbnailGenerationId++; // cancel any in-flight generation
        Timeline.ClearThumbnails();
        Timeline.ClearSegmentTrackVisuals();

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
            return;

        int fps = project.Fps > 0 ? project.Fps : 30;
        int previewFps = Math.Min(fps, 30);
        Preview.PreviewFps = previewFps;

        _frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, fps);
        if (_frameReader is null)
            return;

        // Generate filmstrip thumbnails for the timeline video track (primary +
        // any appended recordings, each from their own source file).
        _ = GenerateAllTimelineThumbnailsAsync(_frameReader, project.VideoFilePath);

        // Load per-file cursor + audio track data for appended recordings so their
        // mouse/click and audio markers show on the tracks and move with the segment.
        _ = LoadAppendedTrackVisualsAsync();

        // Load audio waveform data for timeline visualization
        await LoadAudioWaveformAsync(project);

        // Load mouse data for cursor smoothing + click animations
        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data — still show raw frames */ }
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
        if (!ZoomKeyframesExistForSource(null))
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
        config = config with
        {
            OutputFps = previewFps,
            // Zero-phase (forward-backward) spring: smooths trackpad stop-and-go like
            // Screen Studio with NO time lag (offline filtering uses future samples), so
            // the cursor stays smooth yet lands on target on time. De-stutter stays off.
            SmoothingAlgorithm = SmoothingAlgorithm.ZeroPhaseSpring,
            SmoothingStrength = SmoothingStrength.Smooth,
            AspectRatio = project.AspectRatio,
            FitMode = project.FitMode,
            CropAnchorX = project.CropAnchorX,
            CropAnchorY = project.CropAnchorY,
            ZoomScope = project.ZoomScope,
            Cursor = new CursorStyle
            {
                Scale = 3.0f,
                ClickAnimationEnabled = true,
                AutoHideEnabled = true,
                AutoHideDelaySeconds = 3.0f,
            },
            Zoom = new AutoZoomConfig { Enabled = true },
        };

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
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, config,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);
            _compositorReady = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] PreviewRenderer init failed: {ex.Message}");
            // Compositor init failed — fall back to raw frames
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Load webcam composition for preview overlay
        await LoadWebcamCompositionAsync(project);

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
                await RenderFrameAtAsync(currentPos, currentForce);

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

    private async Task RenderFrameAtAsync(TimeSpan position, bool force)
    {
        if (_frameReader is null) return;

        // Segment-aware rendering: when the timeline uses segments, check which
        // segment the playhead is over and render text slides directly.
        var model = ViewModel.Model;
        if (model.Segments.Count > 0)
        {
            // Soft cut: when the playhead is within the dissolve window on the
            // leading edge of a boundary that touches a text slide, cross-dissolve
            // the outgoing neighbour into the incoming segment instead of hard
            // cutting. Fully guarded — any failure falls back to the normal render.
            var crossfade = Musio.Core.Timeline.SlideTransitions.Resolve(model, position);
            if (crossfade.Active)
            {
                CanvasRenderTarget? incoming = null, outgoing = null;
                try
                {
                    incoming = await ComposePreviewFrameAsync(position);
                    outgoing = await ComposePreviewFrameAsync(crossfade.OutgoingTime);
                    if (incoming is not null && outgoing is not null)
                    {
                        var project = ProjectService.Instance.CurrentProject;
                        int w = project?.Width > 0 ? project.Width : 1920;
                        int h = project?.Height > 0 ? project.Height : 1080;
                        _transitionRenderer ??= new TransitionRenderer();
                        var blended = _transitionRenderer.Render(
                            outgoing, incoming, TransitionType.CrossFade, crossfade.Progress, w, h);
                        HideSlideEditOverlay();
                        // Force the normal path to fully redraw once the dissolve ends.
                        _lastRenderedFrameIndex = -1;
                        _lastRenderedSegmentId = null;
                        Preview.SetFrame(blended);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EditorPage] Slide crossfade preview error: {ex.Message}");
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
                HideSlideEditOverlay();
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
                return;
            }
        }

        HideSlideEditOverlay();
        // Legacy path: map output (playhead) time to source time
        TimeSpan sourcePosition = MapToSourceTime(position);
        await RenderVideoFrameAsync(sourcePosition, force);
    }

    /// <summary>
    /// Renders a standalone preview frame for an arbitrary output position WITHOUT
    /// presenting it or touching the frame cache. Used to obtain both sides of a
    /// slide↔neighbour crossfade so they can be blended. Returns null on failure
    /// (the caller falls back to the normal render path). Text slides render at
    /// project resolution; video frames render at their source resolution and the
    /// transition renderer scales both to the output rect when blending.
    /// </summary>
    private async Task<CanvasRenderTarget?> ComposePreviewFrameAsync(TimeSpan outputPos)
    {
        var model = ViewModel.Model;
        var (segment, localOffset) = model.GetSegmentAtTime(outputPos);

        if (segment is TextSlideSegment slide)
        {
            _textSlideRenderer ??= new TextSlideRenderer();
            await _textSlideRenderer.EnsureBackgroundLoadedAsync(slide);
            var project = ProjectService.Instance.CurrentProject;
            int w = project?.Width > 0 ? project.Width : 1920;
            int h = project?.Height > 0 ? project.Height : 1080;
            double progress = slide.Duration.TotalSeconds > 0
                ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
                : 0;
            return _textSlideRenderer.RenderSlide(slide, progress, w, h);
        }

        if (segment is not VideoSegment seg)
            return null;

        var sourceTime = seg.SourceStart +
            TimeSpan.FromTicks((long)(localOffset.Ticks * seg.SpeedFactor));

        // Primary-recording segment → main reader/compositor.
        if (string.Equals(seg.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_frameReader is null) return null;
            await EnsurePrimaryRendererForSegmentAsync(seg);
            var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourceTime);
            if (bitmap is null) return null;

            if (_compositorReady && _previewRenderer is not null && !_zoomRegionEditMode)
            {
                await SetWebcamFrameForPreviewAsync(sourceTime);
                var composed = _previewRenderer.RenderPreviewFrame(bitmap, sourceTime);
                bitmap.Dispose();
                if (composed is not null) return composed;
            }
            else
            {
                var device = CanvasDevice.GetSharedDevice();
                var rt = new CanvasRenderTarget(device,
                    bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
                using (var ds = rt.CreateDrawingSession()) ds.DrawImage(bitmap);
                bitmap.Dispose();
                return rt;
            }
            return null;
        }

        // Appended-recording segment → its own per-segment context.
        var ctx = await GetOrBuildSegmentPreviewAsync(seg);
        if (ctx?.Reader is null) return null;
        var segBitmap = await ctx.Reader.LoadFrameAtTimeAsync(sourceTime);
        if (segBitmap is null) return null;

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
            var composed = ctx.Renderer.RenderPreviewFrame(segBitmap, sourceTime);
            segBitmap.Dispose();
            if (composed is not null) return composed;
            return null;
        }

        var dev = CanvasDevice.GetSharedDevice();
        var fallback = new CanvasRenderTarget(dev,
            segBitmap.SizeInPixels.Width, segBitmap.SizeInPixels.Height, 96);
        using (var ds = fallback.CreateDrawingSession()) ds.DrawImage(segBitmap);
        segBitmap.Dispose();
        return fallback;
    }

    // Text slide in-place editing state
    private string? _previewSlideId;
    private int _previewSlideW = 1920;
    private int _previewSlideH = 1080;
    private string? _editingSlideId;
    private bool _slideRegionDragging;
    private Point _slideDragStart;
    private double _slideDragStartX, _slideDragStartY;

    private async Task RenderTextSlidePreviewAsync(TextSlideSegment slide, TimeSpan localOffset)
    {
        _textSlideRenderer ??= new TextSlideRenderer();

        // Pre-load the (image) background off the UI thread so the synchronous
        // RenderSlide call below never blocks on file I/O + GPU decode.
        await _textSlideRenderer.EnsureBackgroundLoadedAsync(slide);

        var project = ProjectService.Instance.CurrentProject;
        int width = project?.Width > 0 ? project.Width : 1920;
        int height = project?.Height > 0 ? project.Height : 1080;

        _previewSlideId = slide.Id;
        _previewSlideW = width;
        _previewSlideH = height;

        double progress = slide.Duration.TotalSeconds > 0
            ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
            : 0;

        try
        {
            // While editing, render background only — the editable TextBox shows the text.
            bool drawText = _editingSlideId != slide.Id;
            var frame = _textSlideRenderer.RenderSlide(slide, progress, width, height, drawText);
            _lastRenderedFrameIndex = -1; // force redraw next time
            Preview.SetFrame(frame);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Text slide preview error: {ex.Message}");
        }

        UpdateSlideEditOverlay(slide);
    }

    private async Task RenderVideoFrameAsync(TimeSpan sourcePosition, bool force)
    {
        if (_frameReader is null) return;

        int frameIndex = _frameReader.GetFrameIndex(sourcePosition);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourcePosition);
        if (bitmap is null) return;

        try
        {
            if (_compositorReady && _previewRenderer is not null && !_zoomRegionEditMode)
            {
                // Extract webcam frame for overlay
                await SetWebcamFrameForPreviewAsync(sourcePosition);

                var composed = _previewRenderer.RenderPreviewFrame(bitmap, sourcePosition);
                bitmap.Dispose();

                if (composed is not null)
                {
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
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
        if (bitmap is null) return;

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
                bitmap.Dispose();
                if (composed is not null)
                {
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
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

    private async Task<SegmentPreview?> GetOrBuildSegmentPreviewAsync(VideoSegment seg)
    {
        if (_segmentPreviews.TryGetValue(seg.Id, out var existing))
            return existing;

        var ctx = new SegmentPreview();
        _segmentPreviews[seg.Id] = ctx; // insert early to avoid duplicate builds

        try
        {
            int fps = seg.Fps > 0 ? seg.Fps : 30;
            ctx.Reader = VideoFrameReader.OpenFromVideoPath(seg.VideoFilePath, fps);
            if (ctx.Reader is null) return ctx;

            MouseRecordingData? mouseData = null;
            if (!string.IsNullOrEmpty(seg.CursorDataFilePath) && File.Exists(seg.CursorDataFilePath))
            {
                try { mouseData = MouseHookRecorder.LoadFromFile(seg.CursorDataFilePath); }
                catch { }
            }

            int w = seg.SourceWidth > 0 ? seg.SourceWidth : 1920;
            int h = seg.SourceHeight > 0 ? seg.SourceHeight : 1080;
            int previewFps = Math.Min(fps, 30);

            var global = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
            var config = BuildSegmentConfig(global, seg, previewFps) with
            {
                SmoothingAlgorithm = SmoothingAlgorithm.ZeroPhaseSpring,
                SmoothingStrength = SmoothingStrength.Smooth,
                Zoom = new AutoZoomConfig { Enabled = true },
            };

            if (!string.IsNullOrWhiteSpace(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath))
                config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };

            if (mouseData is not null)
            {
                try
                {
                    ctx.Renderer = new PreviewRenderer();
                    // The compositor is driven with ABSOLUTE source times
                    // (SourceStart + localOffset), so its timelines must span the end of
                    // the clip's source extent, not just the clip's length. Passing the
                    // bare SourceDuration would drop cursor samples and auto-zoom for the
                    // visible tail of any clip trimmed from the front. Mirrors
                    // SegmentFrameComposer.ResolveSourceDuration on the export path.
                    await ctx.Renderer.InitializeAsync(
                        mouseData, config, w, h, seg.SourceStart + seg.SourceDuration,
                        seg.MouseToVideoOffsetSeconds, seg.CropOffsetX, seg.CropOffsetY, seg.DpiScale);
                    ctx.Ready = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EditorPage] Segment compositor init failed: {ex.Message}");
                    ctx.Renderer?.Dispose();
                    ctx.Renderer = null;
                    ctx.Ready = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(seg.WebcamFilePath);
                    var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
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

        return ctx;
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
    /// Generates pre-scaled thumbnails for the timeline video track filmstrip.
    /// Uses versioning to cancel stale generation when the project changes.
    /// </summary>
    private async Task GenerateTimelineThumbnailsAsync(VideoFrameReader reader)
    {
        var project = ProjectService.Instance.CurrentProject;
        await GenerateTimelineThumbnailsAsync(reader, project?.VideoFilePath, isPrimary: true);
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
    private async Task GenerateAllTimelineThumbnailsAsync(VideoFrameReader primaryReader, string? primaryPath)
    {
        await GenerateTimelineThumbnailsAsync(primaryReader, primaryPath, isPrimary: true);
        await GenerateAppendedThumbnailsAsync(primaryPath);
    }

    /// <summary>
    /// Generates per-file thumbnails for every appended (non-primary) video segment
    /// file referenced by the timeline.
    /// </summary>
    private async Task GenerateAppendedThumbnailsAsync(string? primaryPath)
    {
        var model = ViewModel.Model;
        var files = model.Segments.OfType<VideoSegment>()
            .Select(v => v.VideoFilePath)
            .Where(p => !string.IsNullOrEmpty(p) &&
                        !string.Equals(p, primaryPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;

            int segFps = model.Segments.OfType<VideoSegment>()
                .FirstOrDefault(v => string.Equals(v.VideoFilePath, file, StringComparison.OrdinalIgnoreCase))?.Fps ?? 30;
            if (segFps <= 0) segFps = 30;

            VideoFrameReader? reader = null;
            try
            {
                reader = VideoFrameReader.OpenFromVideoPath(file, segFps);
                if (reader is null) continue;
                await GenerateTimelineThumbnailsAsync(reader, file, isPrimary: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EditorPage] Appended thumbnail generation failed for {file}: {ex.Message}");
            }
            finally
            {
                reader?.Dispose();
            }
        }
    }

    private async Task GenerateTimelineThumbnailsAsync(VideoFrameReader reader, string? filePath, bool isPrimary)
    {
        var generationId = ++_thumbnailGenerationId;

        // Load first frame to determine aspect ratio
        var firstFrame = await reader.LoadFrameAsync(0);
        if (firstFrame is null || generationId != _thumbnailGenerationId)
        {
            firstFrame?.Dispose();
            return;
        }

        double aspectRatio = (double)firstFrame.SizeInPixels.Width / firstFrame.SizeInPixels.Height;
        double totalSeconds = reader.Duration.TotalSeconds;
        if (totalSeconds <= 0)
        {
            firstFrame.Dispose();
            return;
        }

        // Thumbnail size: match video track height (60px row minus padding)
        const int thumbH = 52;
        int thumbW = Math.Max(1, (int)(thumbH * aspectRatio));

        // Determine interval: aim for a reasonable density, cap total count
        double interval = Math.Max(0.5, totalSeconds / 200);
        int count = Math.Min(300, (int)(totalSeconds / interval) + 1);

        var device = CanvasDevice.GetSharedDevice();
        var thumbnails = new CanvasBitmap[count];

        for (int i = 0; i < count; i++)
        {
            if (generationId != _thumbnailGenerationId)
            {
                // Generation cancelled — clean up
                foreach (var t in thumbnails) t?.Dispose();
                firstFrame.Dispose();
                return;
            }

            CanvasBitmap? frame;
            if (i == 0)
            {
                frame = firstFrame;
            }
            else
            {
                double time = i * interval;
                frame = await reader.LoadFrameAtTimeAsync(TimeSpan.FromSeconds(time));
            }

            if (frame is null) continue;

            try
            {
                // Scale down to thumbnail size
                var renderTarget = new CanvasRenderTarget(device, thumbW, thumbH, 96);
                using (var session = renderTarget.CreateDrawingSession())
                {
                    session.DrawImage(frame,
                        new Rect(0, 0, thumbW, thumbH),
                        new Rect(0, 0, frame.SizeInPixels.Width, frame.SizeInPixels.Height));
                }
                thumbnails[i] = renderTarget;
            }
            finally
            {
                if (i > 0) frame.Dispose();
            }
        }

        firstFrame.Dispose();

        if (generationId != _thumbnailGenerationId)
        {
            foreach (var t in thumbnails) t?.Dispose();
            return;
        }

        // TimelineControl takes ownership of the bitmaps.
        if (isPrimary)
            Timeline.SetThumbnails(thumbnails, interval, aspectRatio, filePath);
        else if (!string.IsNullOrEmpty(filePath))
            Timeline.SetThumbnailsForFile(filePath, thumbnails, interval, aspectRatio);
        else
            foreach (var t in thumbnails) t?.Dispose();
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
        var manualKeyframes = ViewModel.Model.ZoomKeyframes
            .Where(k => k.IsManual && k.SourceVideoFilePath is null)
            .ToList();

        _previewRenderer.UpdateZoomKeyframes(manualKeyframes);
        _previewRenderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
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
        if (ViewModel.CanUndo)
        {
            ViewModel.UndoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanRedo)
        {
            ViewModel.RedoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void SplitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SplitAtPlayheadCommand.Execute(null);
        args.Handled = true;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // If a camera segment is selected, remove it.
        if (Timeline.SelectedCameraSegmentId is { } cameraSegId)
        {
            DeleteCameraSegment(cameraSegId);
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
        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
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

        var createdKf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == operation.CreatedId);
        if (createdKf is not null && e.FilePath is null)
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
        _zoomRegionSourceW = project.Width > 0 ? project.Width : 1920;
        _zoomRegionSourceH = project.Height > 0 ? project.Height : 1080;

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
        if (Timeline is null || ZoomSegmentPanel is null || ZoomHintText is null) return;
        bool hasSelection = Timeline.SelectedZoomKeyframeId is not null;
        ZoomSegmentPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ZoomHintText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
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

    private async Task LoadAudioWaveformAsync(Project project)
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

            // At video time T, the audio file position is T + audioOffset
            _audioOffsetSeconds = audioOffset;
            _audioPlayer?.Dispose();
            _audioPlayer = new AudioPlaybackEngine();
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
        foreach (var ctx in _segmentPreviews.Values) ctx.Dispose();
        _segmentPreviews.Clear();
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

        await RebuildPreviewRendererAsync(global);
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
            if (_segmentPreviews.TryGetValue(seg.Id, out var ctx))
            {
                ctx.Dispose();
                _segmentPreviews.Remove(seg.Id);
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
    private async Task RebuildPreviewRendererAsync(CompositionConfig config)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        // Apply the active primary segment's per-segment frame style / cursor override
        // on top of the global config so the primary recording honors its own style.
        var activePrimary = ActivePrimaryVideoSegment();
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

    private void RecordMore_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to RecordingPage in append mode
        Preview?.Pause();
        _audioPlayer?.Stop();
        Frame.Navigate(typeof(RecordingPage), "append");
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
        if (ZoomHintText is not null) ZoomHintText.Visibility = Visibility.Collapsed;

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
        if (ZoomHintText is not null)
            ZoomHintText.Visibility = Visibility.Visible;
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

    // ─── In-preview text editing & repositioning ────────────────────────

    private TextSlideSegment? PreviewSlide() =>
        _previewSlideId is null ? null : ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _previewSlideId);

    /// <summary>
    /// Shows and positions the text-edit overlay over the slide's text region.
    /// Hidden during playback. Safe to call from any thread.
    /// </summary>
    private void UpdateSlideEditOverlay(TextSlideSegment slide)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => UpdateSlideEditOverlay(slide));
            return;
        }

        if (Preview.IsPlaying || _zoomRegionEditMode)
        {
            HideSlideEditOverlay();
            return;
        }

        SlideEditCanvas.Visibility = Visibility.Visible;
        PositionSlideEditControls(slide);
    }

    private void HideSlideEditOverlay()
    {
        if (SlideEditCanvas is null) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(HideSlideEditOverlay);
            return;
        }
        if (_editingSlideId is not null)
            CommitSlideTextEdit();
        SlideEditCanvas.Visibility = Visibility.Collapsed;
    }

    private void PositionSlideEditControls(TextSlideSegment slide)
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || _previewSlideW <= 0) return;

        double scaleX = layout.Width / _previewSlideW;
        double scaleY = layout.Height / _previewSlideH;

        var textRect = TextSlideRenderer.ComputeTextRect(slide, _previewSlideW, _previewSlideH);
        double left = layout.X + textRect.X * scaleX;
        double top = layout.Y + textRect.Y * scaleY;
        double w = textRect.Width * scaleX;
        double h = textRect.Height * scaleY;

        Canvas.SetLeft(SlideTextRegion, left);
        Canvas.SetTop(SlideTextRegion, top);
        SlideTextRegion.Width = w;
        SlideTextRegion.Height = h;

        Canvas.SetLeft(SlideEditBox, left);
        Canvas.SetTop(SlideEditBox, top);
        SlideEditBox.Width = w;
        SlideEditBox.Height = h;

        // Match the slide's text styling so editing looks WYSIWYG.
        SlideEditBox.FontSize = Math.Max(8, slide.FontSize * scaleY);
        SlideEditBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(slide.FontFamily);
        SlideEditBox.FontWeight = slide.IsBold
            ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        SlideEditBox.FontStyle = slide.IsItalic
            ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        SlideEditBox.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseHexColor(slide.TextColor));
        SlideEditBox.TextAlignment = slide.TextAlignment switch
        {
            SlideTextAlignment.Left => TextAlignment.Left,
            SlideTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
    }

    private void SlideTextRegion_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingSlideId is null)
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
    }

    private void SlideTextRegion_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_slideRegionDragging)
            ProtectedCursor = null;
    }

    private void SlideTextRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingSlideId is not null) return; // editing — let the textbox handle it
        var slide = PreviewSlide();
        if (slide is null) return;

        _slideRegionDragging = true;
        _slideDragStart = e.GetCurrentPoint(SlideEditCanvas).Position;
        _slideDragStartX = slide.TextX;
        _slideDragStartY = slide.TextY;
        SlideTextRegion.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SlideTextRegion_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_slideRegionDragging) return;
        var slide = PreviewSlide();
        var layout = Preview.FrameLayoutRect;
        if (slide is null || layout.Width <= 0) return;

        var pos = e.GetCurrentPoint(SlideEditCanvas).Position;
        double dx = (pos.X - _slideDragStart.X) / layout.Width;
        double dy = (pos.Y - _slideDragStart.Y) / layout.Height;

        slide.TextX = Math.Clamp(_slideDragStartX + dx, 0.0, 1.0);
        slide.TextY = Math.Clamp(_slideDragStartY + dy, 0.0, 1.0);

        PositionSlideEditControls(slide);
        RefreshSlidePreview();
        e.Handled = true;
    }

    private void SlideTextRegion_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_slideRegionDragging) return;
        _slideRegionDragging = false;
        SlideTextRegion.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
        e.Handled = true;
    }

    private void SlideTextRegion_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        EnterSlideTextEdit();
        e.Handled = true;
    }

    private void EnterSlideTextEdit()
    {
        var slide = PreviewSlide();
        if (slide is null) return;

        // The taps that opened the editor may have started a region drag (and
        // captured the pointer) without a matching PointerReleased, because the
        // region gets collapsed below. Clear that state so the box doesn't follow
        // the mouse after editing finishes.
        _slideRegionDragging = false;
        SlideTextRegion.ReleasePointerCaptures();
        ProtectedCursor = null;

        _editingSlideId = slide.Id;
        SlideTextRegion.Visibility = Visibility.Collapsed;
        SlideEditBox.Visibility = Visibility.Visible;
        SlideEditBox.Text = slide.Text;
        SlideEditBox.Focus(FocusState.Programmatic);
        SlideEditBox.SelectAll();

        // Re-render background-only so the rendered text doesn't double up.
        RefreshSlidePreview();
    }

    private void CommitSlideTextEdit()
    {
        if (_editingSlideId is null) return;
        _editingSlideId = null;
        SlideEditBox.Visibility = Visibility.Collapsed;
        SlideTextRegion.Visibility = Visibility.Visible;
        RefreshSlidePreview();
    }

    private void SlideEditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editingSlideId is null) return;
        var slide = ViewModel.Model.Segments.OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _editingSlideId);
        if (slide is null) return;

        slide.Text = SlideEditBox.Text;
        if (SlideTextBox is not null && SlideTextBox.Text != slide.Text)
            SlideTextBox.Text = slide.Text; // keep flyout in sync

        // Re-measure so the edit box grows/shrinks to hug the wrapped text live
        // instead of staying at the height it had when editing started.
        PositionSlideEditControls(slide);
        Timeline.InvalidateAllCanvases();
    }

    private void SlideEditBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Enter commits (Shift+Enter inserts a newline); Esc commits too.
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if ((e.Key == Windows.System.VirtualKey.Enter && !shift)
            || e.Key == Windows.System.VirtualKey.Escape)
        {
            CommitSlideTextEdit();
            e.Handled = true;
        }
    }

    private void SlideEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitSlideTextEdit();
    }
}
