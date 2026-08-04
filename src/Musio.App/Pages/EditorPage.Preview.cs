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

public sealed partial class EditorPage
{

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
    private int _webcamWidth;
    private int _webcamHeight;
    private AdaptivePreviewQuality? _adaptivePreviewQuality;
    private string? _adaptivePreviewVideoPath;
    private PreviewResolution _previewResolution = new(960, 540);

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
                        double incomingSourceStartSeconds = 0;
                        if (resolution.IncomingSegment is VideoSegment incomingVideo
                            && resolution.OutgoingSegment is not null)
                        {
                            var incomingLocal = resolution.OutgoingLocalOffset
                                - resolution.OutgoingSegment.Duration;
                            double incomingSpeed = incomingVideo.SpeedFactor > 0
                                ? incomingVideo.SpeedFactor : 1.0;
                            incomingVideoFilePath = incomingVideo.VideoFilePath;
                            incomingSourceStartSeconds = incomingVideo.SourceStart.TotalSeconds;
                            incomingSourceTimeSeconds = incomingVideo.SourceStart.TotalSeconds
                                + incomingLocal.TotalSeconds * incomingSpeed;
                        }

                        outgoing = resolution.OutgoingSegment is { } outgoingSegment
                            ? await ComposePreviewFrameAtOffsetAsync(
                                outgoingSegment, resolution.OutgoingLocalOffset,
                                incomingVideoFilePath, incomingSourceTimeSeconds,
                                incomingSourceStartSeconds)
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
        string? incomingVideoFilePath = null, double? incomingSourceTimeSeconds = null,
        double incomingSourceStartSeconds = 0)
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
            incomingSourceTimeSeconds, incomingVideoFilePath, seg.Fps,
            (seg.SourceStart + seg.SourceDuration).TotalSeconds,
            incomingSourceStartSeconds);
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
}
