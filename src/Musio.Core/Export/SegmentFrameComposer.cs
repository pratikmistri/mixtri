using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// Composes output frames for the export pipeline directly from the timeline's
/// <see cref="TimelineModel.Segments"/>: each output frame is routed to the segment
/// that owns it, video segments read from <em>their own</em> source recording, and
/// text slides are rendered by <see cref="TextSlideRenderer"/>.
///
/// <para>This is the single segment-composition path shared by the MP4
/// (<see cref="VideoEncoder"/>) and GIF (<see cref="ExportEngine"/>) exporters, so the
/// two formats cannot drift apart in segment ordering, source selection, trims, speed,
/// text slides, or slide crossfades.</para>
///
/// <para>Per-source render state (frame reader, <see cref="FrameCompositor"/>, webcam
/// composition) is built lazily and cached, keyed by source file plus the effective
/// per-segment frame/cursor style. That keeps per-segment
/// <see cref="VideoSegment.FrameStyleOverride"/> / <see cref="VideoSegment.CursorStyleOverride"/>
/// honoured without mutating a shared compositor, and never rebuilds the expensive
/// cursor-smoothing pipeline per frame.</para>
///
/// <para>Instances are <b>not</b> thread-safe: callers must serialize
/// <see cref="ComposeFrameAsync"/> (the MP4 encoder does so with its frame semaphore,
/// the GIF encoder composes frames sequentially).</para>
/// </summary>
public sealed class SegmentFrameComposer : IDisposable
{
    private readonly Project _project;
    private readonly CompositionConfig _composition;
    private readonly MouseRecordingData _primaryMouseData;
    private readonly TimelineModel? _timeline;
    private readonly TimelineMapper? _mapper;
    private readonly int _fps;
    private readonly CanvasDevice _device;
    private readonly Dictionary<SourceKey, SourceContext> _contexts = [];
    private readonly SourceContext _primaryContext;

    private TextSlideRenderer? _textSlideRenderer;
    private TransitionRenderer? _transitionRenderer;
    private bool _disposed;

    /// <summary>Width of every frame returned by <see cref="ComposeFrameAsync"/>.</summary>
    public int OutputWidth { get; }

    /// <summary>Height of every frame returned by <see cref="ComposeFrameAsync"/>.</summary>
    public int OutputHeight { get; }

    /// <summary>
    /// Number of output frames this composer can produce: the timeline mapper's total
    /// when a timeline is present, otherwise the primary recording's frame count.
    /// </summary>
    public int TotalFrames { get; }

    private SegmentFrameComposer(
        Project project,
        MouseRecordingData primaryMouseData,
        CompositionConfig composition,
        TimelineModel? timeline,
        TimelineMapper? mapper,
        int fps,
        CanvasDevice device,
        SourceKey primaryKey,
        SourceContext primaryContext)
    {
        _project = project;
        _primaryMouseData = primaryMouseData;
        _composition = composition;
        _timeline = timeline;
        _mapper = mapper;
        _fps = fps;
        _device = device;
        _primaryContext = primaryContext;
        _contexts[primaryKey] = primaryContext;

        OutputWidth = primaryContext.Compositor.OutputWidth;
        OutputHeight = primaryContext.Compositor.OutputHeight;
        TotalFrames = mapper?.TotalOutputFrames ?? primaryContext.Compositor.TotalFrames;
    }

    /// <summary>
    /// Builds a composer and eagerly initializes the primary recording's render context
    /// (which defines the canonical output dimensions).
    /// </summary>
    /// <param name="fps">Output frame rate; output frame N maps to output time N/fps.</param>
    public static async Task<SegmentFrameComposer> CreateAsync(
        Project project,
        MouseRecordingData primaryMouseData,
        CompositionConfig composition,
        TimelineModel? timeline,
        TimelineMapper? mapper,
        int fps,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(primaryMouseData);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.VideoFilePath);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        ct.ThrowIfCancellationRequested();

        var device = CanvasDevice.GetSharedDevice();

        // The key is built from the *unmodified* composition styles so that primary
        // segments without an override resolve back to this same context.
        var primaryKey = new SourceKey(
            NormalizePath(project.VideoFilePath), composition.Background, composition.Cursor);

        SourceContext? primary = null;
        try
        {
            primary = await BuildContextAsync(
                device,
                project.VideoFilePath,
                ApplyCursorAvailability(composition, primaryMouseData),
                primaryMouseData,
                sourceWidth: project.Width,
                sourceHeight: project.Height,
                duration: ResolveSourceDuration(timeline, project.VideoFilePath, project.Duration),
                mouseToVideoOffsetSeconds: project.MouseToVideoOffsetSeconds,
                cropOffsetX: project.CropOffsetX,
                cropOffsetY: project.CropOffsetY,
                dpiScale: project.DpiScale,
                webcamFilePath: project.WebcamFilePath,
                recordingFps: project.Fps > 0 ? project.Fps : fps,
                timeline: timeline,
                ct);

            return new SegmentFrameComposer(
                project, primaryMouseData, composition, timeline, mapper, fps,
                device, primaryKey, primary);
        }
        catch
        {
            primary?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Composes the output frame at <paramref name="frameIndex"/> at
    /// <see cref="OutputWidth"/>×<see cref="OutputHeight"/>. The caller owns (and must
    /// dispose) the returned render target.
    /// </summary>
    public async Task<CanvasRenderTarget> ComposeFrameAsync(int frameIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);

        var frame = await ComposeSegmentFrameAsync(frameIndex, ct);

        if (_timeline is null || _timeline.Segments.Count == 0)
            return frame;

        // Soft cut: dissolve a text slide into its neighbouring segment instead of
        // hard-cutting. Identical resolution to the preview (SlideTransitions is the
        // shared pure function), so both pipelines dissolve at the same instants.
        var outputTime = TimeSpan.FromSeconds((double)frameIndex / _fps);
        var crossfade = SlideTransitions.Resolve(_timeline, outputTime);
        if (!crossfade.Active)
            return frame;

        try
        {
            // Floor (not Round): the outgoing time is current.Start - 1 tick, and
            // rounding would snap back onto the incoming segment's frame whenever the
            // boundary is frame-aligned, blending the incoming frame with itself.
            int outgoingIndex = (int)Math.Floor(crossfade.OutgoingTime.TotalSeconds * _fps);
            outgoingIndex = Math.Clamp(outgoingIndex, 0, Math.Max(0, TotalFrames - 1));

            using var outgoing = await ComposeSegmentFrameAsync(outgoingIndex, ct);
            _transitionRenderer ??= new TransitionRenderer(_device);
            var blended = _transitionRenderer.Render(
                outgoing, frame, TransitionType.CrossFade, crossfade.Progress,
                OutputWidth, OutputHeight);
            frame.Dispose();
            return blended;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Selects the manual zoom keyframes that belong to <paramref name="videoFilePath"/>.
    /// A keyframe with no <see cref="ZoomKeyframe.SourceVideoFilePath"/> belongs to the
    /// primary recording; every other keyframe belongs to the file it names. This keeps
    /// zooms created on an appended clip off the primary recording's compositor (and vice
    /// versa), matching how the editor maps zoom segments onto clips.
    /// </summary>
    public static List<ZoomKeyframe> SelectManualZoomKeyframes(TimelineModel timeline, string videoFilePath)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        return timeline.ZoomKeyframes
            .Where(k => k.IsManual && BelongsToSource(k, videoFilePath, timeline.PrimaryVideoFilePath))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="keyframe"/> was authored against
    /// <paramref name="videoFilePath"/>'s source-time space.
    /// </summary>
    public static bool BelongsToSource(
        ZoomKeyframe keyframe, string videoFilePath, string? primaryVideoFilePath)
    {
        ArgumentNullException.ThrowIfNull(keyframe);

        return keyframe.SourceVideoFilePath is null
            ? primaryVideoFilePath is null
              || string.Equals(videoFilePath, primaryVideoFilePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(videoFilePath, keyframe.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);
    }

    #region Frame composition

    private async Task<CanvasRenderTarget> ComposeSegmentFrameAsync(int frameIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_timeline is null || _timeline.Segments.Count == 0)
        {
            // Legacy (pre-segment) projects: the whole export is the primary recording,
            // mapped through trim/cut/speed by the timeline mapper.
            double legacyTime = _mapper?.GetSourceTimeForOutputFrame(frameIndex)
                ?? (double)frameIndex / _fps;
            return await ComposeWithContextAsync(_primaryContext, legacyTime, applyCameraSegments: true, ct);
        }

        var outputTime = TimeSpan.FromSeconds((double)frameIndex / _fps);
        var (segment, localOffset) = _timeline.GetSegmentAtTime(outputTime);

        switch (segment)
        {
            case TextSlideSegment slide:
            {
                double progress = slide.Duration.TotalSeconds > 0
                    ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
                    : 0;
                _textSlideRenderer ??= new TextSlideRenderer(_device);
                return _textSlideRenderer.RenderSlide(slide, progress, OutputWidth, OutputHeight);
            }

            case VideoSegment video:
            {
                double speed = video.SpeedFactor > 0 ? video.SpeedFactor : 1.0;
                double sourceTime = video.SourceStart.TotalSeconds + localOffset.TotalSeconds * speed;
                var context = await GetOrCreateContextAsync(video, ct);
                bool isPrimary = IsPrimarySource(video.VideoFilePath);
                return await ComposeWithContextAsync(context, sourceTime, isPrimary, ct);
            }

            case null:
                throw new InvalidOperationException(
                    $"No timeline segment covers output frame {frameIndex} ({outputTime}).");

            default:
                throw new InvalidOperationException(
                    $"Export does not support timeline segments of type '{segment?.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Renders one frame of a source recording through its own compositor.
    /// <paramref name="applyCameraSegments"/> is only set for the primary recording:
    /// camera segments are expressed in the primary recording's source time.
    /// </summary>
    private async Task<CanvasRenderTarget> ComposeWithContextAsync(
        SourceContext context, double sourceTimeSeconds, bool applyCameraSegments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        double clampedTime = Math.Max(0, sourceTimeSeconds);
        var position = TimeSpan.FromSeconds(clampedTime);
        var compositor = context.Compositor;

        CanvasBitmap? webcamFrame = null;
        try
        {
            if (context.WebcamComposition is not null)
            {
                bool showWebcam = !applyCameraSegments
                    || ExportEngine.ApplyCameraSegmentState(
                        compositor, _timeline, _composition.WebcamStyle, position);

                if (!showWebcam)
                {
                    compositor.SetWebcamFrame(null);
                }
                else
                {
                    try
                    {
                        webcamFrame = await ExtractFrameFromCompositionAsync(
                            _device, context.WebcamComposition, position,
                            context.WebcamExtractWidth, context.WebcamExtractHeight);
                        compositor.SetWebcamFrame(webcamFrame);
                    }
                    catch (Exception ex)
                    {
                        // A single unreadable webcam thumbnail must not fail the export;
                        // the frame is composited without the overlay.
                        Debug.WriteLine(
                            $"[SegmentFrameComposer] Webcam frame at {position} unavailable: {ex.Message}");
                        compositor.SetWebcamFrame(null);
                    }
                }
            }

            using var sourceFrame = await LoadSourceFrameAsync(context, position);
            return EnsureCanonicalSize(compositor.ComposeFrame(sourceFrame, clampedTime));
        }
        finally
        {
            // The compositor never owns the webcam bitmap — detach before disposing.
            compositor.SetWebcamFrame(null);
            webcamFrame?.Dispose();
        }
    }

    private async Task<CanvasBitmap> LoadSourceFrameAsync(SourceContext context, TimeSpan position)
    {
        if (context.Reader is not null)
        {
            var frame = await context.Reader.LoadFrameAtTimeAsync(position);
            if (frame is not null)
                return frame;
        }

        if (context.SourceComposition is not null)
        {
            return await ExtractFrameFromCompositionAsync(
                _device, context.SourceComposition, position,
                context.SourceWidth, context.SourceHeight);
        }

        // The JPEG frame could not be loaded and there is no open composition —
        // decode the frame straight from the video file.
        return await FallbackExtractFrameAsync(
            _device, context.VideoFilePath, position, context.SourceWidth, context.SourceHeight);
    }

    /// <summary>
    /// Appended recordings can have a different resolution than the primary one, but a
    /// video stream has a single frame size. Frames from such a source are letterboxed
    /// (aspect preserved) into the canonical output size instead of being stretched.
    /// Takes ownership of <paramref name="frame"/>.
    /// </summary>
    private CanvasRenderTarget EnsureCanonicalSize(CanvasRenderTarget frame)
    {
        if (frame.SizeInPixels.Width == (uint)OutputWidth &&
            frame.SizeInPixels.Height == (uint)OutputHeight)
        {
            return frame;
        }

        try
        {
            double sourceWidth = frame.SizeInPixels.Width;
            double sourceHeight = frame.SizeInPixels.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new InvalidOperationException("Composed frame has no pixels.");

            var target = new CanvasRenderTarget(_device, OutputWidth, OutputHeight, 96);
            try
            {
                double scale = Math.Min(OutputWidth / sourceWidth, OutputHeight / sourceHeight);
                double width = sourceWidth * scale;
                double height = sourceHeight * scale;

                using var ds = target.CreateDrawingSession();
                ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                ds.DrawImage(
                    frame,
                    new Rect((OutputWidth - width) / 2, (OutputHeight - height) / 2, width, height),
                    new Rect(0, 0, sourceWidth, sourceHeight));
            }
            catch
            {
                target.Dispose();
                throw;
            }

            return target;
        }
        finally
        {
            frame.Dispose();
        }
    }

    #endregion

    #region Source contexts

    private async Task<SourceContext> GetOrCreateContextAsync(VideoSegment segment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(segment.VideoFilePath))
            throw new InvalidOperationException("A video segment on the timeline has no source file.");

        var background = segment.FrameStyleOverride ?? _composition.Background;
        var cursor = segment.CursorStyleOverride ?? _composition.Cursor;
        var key = new SourceKey(NormalizePath(segment.VideoFilePath), background, cursor);

        if (_contexts.TryGetValue(key, out var cached))
            return cached;

        bool isPrimary = IsPrimarySource(segment.VideoFilePath);
        var mouseData = isPrimary
            ? _primaryMouseData
            : LoadMouseData(segment.CursorDataFilePath);

        var config = _composition with { Background = background, Cursor = cursor };
        if (!isPrimary)
        {
            // Keyboard events and subtitles are timestamped in the primary recording's
            // timebase, so they must not be replayed over an appended recording.
            config = config with
            {
                KeyboardStyle = null,
                KeyboardEvents = null,
                SubtitleStyle = null,
                Subtitles = null,
            };

            // An appended recording can carry its own camera even when the primary
            // recording has none; the compositor only builds its webcam layer when a
            // style is present.
            if (!string.IsNullOrWhiteSpace(segment.WebcamFilePath) && File.Exists(segment.WebcamFilePath))
                config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };
        }
        config = ApplyCursorAvailability(config, mouseData);

        int recordingFps = isPrimary ? _project.Fps : segment.Fps;
        if (recordingFps <= 0) recordingFps = _fps;

        var context = await BuildContextAsync(
            _device,
            segment.VideoFilePath,
            config,
            mouseData,
            sourceWidth: isPrimary ? _project.Width : segment.SourceWidth,
            sourceHeight: isPrimary ? _project.Height : segment.SourceHeight,
            duration: ResolveSourceDuration(
                _timeline,
                segment.VideoFilePath,
                isPrimary ? _project.Duration : segment.SourceStart + segment.SourceDuration),
            mouseToVideoOffsetSeconds: isPrimary
                ? _project.MouseToVideoOffsetSeconds
                : segment.MouseToVideoOffsetSeconds,
            cropOffsetX: isPrimary ? _project.CropOffsetX : segment.CropOffsetX,
            cropOffsetY: isPrimary ? _project.CropOffsetY : segment.CropOffsetY,
            dpiScale: isPrimary ? _project.DpiScale : segment.DpiScale,
            webcamFilePath: isPrimary ? _project.WebcamFilePath : segment.WebcamFilePath,
            recordingFps: recordingFps,
            timeline: _timeline,
            ct);

        // Disposal can only race a build if the caller broke the "serialize composition"
        // contract (or cancelled without awaiting); release rather than orphan the
        // context's compositor and file handles.
        if (_disposed)
        {
            context.Dispose();
            throw new ObjectDisposedException(nameof(SegmentFrameComposer));
        }

        _contexts[key] = context;
        return context;
    }

    private static async Task<SourceContext> BuildContextAsync(
        CanvasDevice device,
        string videoFilePath,
        CompositionConfig config,
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        TimeSpan duration,
        double mouseToVideoOffsetSeconds,
        int cropOffsetX,
        int cropOffsetY,
        float dpiScale,
        string? webcamFilePath,
        int recordingFps,
        TimelineModel? timeline,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Captured JPEG frames are the preferred source and are indexed with the
        // RECORDING fps.
        var reader = VideoFrameReader.OpenFromVideoPath(
            videoFilePath, recordingFps > 0 ? recordingFps : 30);

        // Only open the video file when it is actually needed: as the frame source when
        // no captured frames exist, or to recover dimensions the recording metadata does
        // not carry. `MediaClip` is not closable — it releases its file handle only when
        // collected — so an unnecessary open would hold a handle for the whole export.
        // Recorded dimensions match the video exactly (both come from the capture size,
        // already forced even), so reading them from the file adds nothing here.
        MediaClip? sourceClip = null;
        if (reader is null || sourceWidth <= 0 || sourceHeight <= 0)
        {
            try
            {
                var sourceFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoFilePath));
                sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
                var props = sourceClip.GetVideoEncodingProperties();
                if (props.Width > 0 && props.Height > 0)
                {
                    sourceWidth = (int)props.Width;
                    sourceHeight = (int)props.Height;
                }
            }
            catch (Exception ex)
            {
                // The MP4 can be missing or unfinalized; captured JPEG frames can still
                // provide the visuals, so fall back to the recorded dimensions.
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Could not open '{videoFilePath}' (using captured frames): {ex.Message}");
            }
        }

        if (sourceWidth <= 0) sourceWidth = 1920;
        if (sourceHeight <= 0) sourceHeight = 1080;

        var context = new SourceContext
        {
            VideoFilePath = videoFilePath,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Reader = reader,
        };

        try
        {
            if (context.Reader is null)
            {
                if (sourceClip is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot export: no captured frames and no readable video for '{videoFilePath}'.");
                }

                var composition = new MediaComposition();
                composition.Clips.Add(sourceClip);
                context.SourceComposition = composition;
            }

            var compositor = new FrameCompositor(config);
            context.Compositor = compositor;
            await compositor.InitializeAsync(
                mouseData, sourceWidth, sourceHeight, EnsureRenderableDuration(duration),
                mouseToVideoOffsetSeconds, cropOffsetX, cropOffsetY, dpiScale);

            SyncZoomState(compositor, timeline, videoFilePath);
            await AttachWebcamAsync(context, webcamFilePath, config, timeline);

            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static async Task AttachWebcamAsync(
        SourceContext context, string? webcamFilePath, CompositionConfig config, TimelineModel? timeline)
    {
        if (string.IsNullOrWhiteSpace(webcamFilePath) || !File.Exists(webcamFilePath))
            return;

        try
        {
            var webcamFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(webcamFilePath));
            var webcamClip = await MediaClip.CreateFromFileAsync(webcamFile);
            var props = webcamClip.GetVideoEncodingProperties();
            int nativeWidth = (int)props.Width;
            int nativeHeight = (int)props.Height;

            var composition = new MediaComposition();
            composition.Clips.Add(webcamClip);
            context.WebcamComposition = composition;

            // Extract at ~1.5x the overlay display size instead of full resolution.
            // When a camera segment animates to fullscreen, extract at native
            // resolution so the enlarged webcam stays sharp.
            float displaySize = (config.WebcamStyle?.Size ?? 300f) * 1.5f;
            float minDimension = Math.Min(nativeWidth, nativeHeight);
            if (ExportEngine.TimelineHasFullscreenCamera(timeline) || minDimension <= displaySize)
            {
                context.WebcamExtractWidth = nativeWidth;
                context.WebcamExtractHeight = nativeHeight;
            }
            else
            {
                float scale = displaySize / minDimension;
                context.WebcamExtractWidth = Math.Max((int)Math.Ceiling(nativeWidth * scale), 1);
                context.WebcamExtractHeight = Math.Max((int)Math.Ceiling(nativeHeight * scale), 1);
            }
        }
        catch (Exception ex)
        {
            // Without the overlay the export is still correct, just camera-less.
            Debug.WriteLine($"[SegmentFrameComposer] Failed to load webcam '{webcamFilePath}': {ex.Message}");
            context.WebcamComposition?.Clips.Clear();
            context.WebcamComposition = null;
        }
    }

    private static void SyncZoomState(FrameCompositor compositor, TimelineModel? timeline, string videoFilePath)
    {
        if (timeline is null) return;

        compositor.SyncManualZoomKeyframes(SelectManualZoomKeyframes(timeline, videoFilePath));

        // Suppressed ticks are raw click timestamps from the recording that produced
        // them, so they can only ever match that recording's auto-zoom segments.
        if (timeline.SuppressedClickTicks.Count > 0)
            compositor.SyncSuppressedClickTicks(timeline.SuppressedClickTicks);
    }

    private bool IsPrimarySource(string videoFilePath)
    {
        if (string.Equals(videoFilePath, _project.VideoFilePath, StringComparison.OrdinalIgnoreCase))
            return true;

        return _timeline?.PrimaryVideoFilePath is { } primary
            && string.Equals(videoFilePath, primary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Longest source extent used by <paramref name="videoFilePath"/> on the timeline, so
    /// the compositor's cursor/zoom timelines cover every segment cut from that file.
    /// </summary>
    private static TimeSpan ResolveSourceDuration(
        TimelineModel? timeline, string videoFilePath, TimeSpan fallback)
    {
        var longest = fallback;

        if (timeline is not null)
        {
            foreach (var segment in timeline.Segments.OfType<VideoSegment>())
            {
                if (!string.Equals(segment.VideoFilePath, videoFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var end = segment.SourceStart + segment.SourceDuration;
                if (end > longest) longest = end;
            }
        }

        return longest;
    }

    /// <summary>
    /// A compositor builds one cursor sample per output frame from this duration, and
    /// composing a frame requires at least one. Sub-second (or degenerate) source extents
    /// are therefore floored to one second; the extra samples merely hold the last cursor
    /// position and are never rendered.
    /// </summary>
    private static TimeSpan EnsureRenderableDuration(TimeSpan duration)
    {
        var minimum = TimeSpan.FromSeconds(1);
        return duration > minimum ? duration : minimum;
    }

    /// <summary>
    /// When a recording has no cursor samples the compositor synthesizes a static
    /// centre position; drawing a cursor there would be pure fiction, so it is hidden
    /// (a negative auto-hide delay hides it from the very first frame).
    /// </summary>
    private static CompositionConfig ApplyCursorAvailability(
        CompositionConfig config, MouseRecordingData mouseData)
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

    private static MouseRecordingData LoadMouseData(string? cursorDataFilePath)
    {
        if (!string.IsNullOrWhiteSpace(cursorDataFilePath) && File.Exists(cursorDataFilePath))
        {
            try
            {
                return MouseHookRecorder.LoadFromFile(cursorDataFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException)
            {
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Unreadable cursor data '{cursorDataFilePath}': {ex.Message}");
            }
        }

        return new MouseRecordingData
        {
            Samples = [],
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = 0,
            TickFrequency = TimeSpan.TicksPerSecond,
        };
    }

    private static string NormalizePath(string path) => path.ToLowerInvariant();

    #endregion

    #region Frame extraction helpers

    private static async Task<CanvasBitmap> ExtractFrameFromCompositionAsync(
        CanvasDevice device, MediaComposition composition, TimeSpan position, int width, int height)
    {
        var clampedPosition = position;
        if (composition.Duration > TimeSpan.Zero && position > composition.Duration)
            clampedPosition = composition.Duration;

        using var thumbnail = await composition.GetThumbnailAsync(
            clampedPosition, width, height, VideoFramePrecision.NearestFrame);

        // ImageStream already implements IRandomAccessStream — pass directly
        return await CanvasBitmap.LoadAsync(device, thumbnail);
    }

    private static async Task<CanvasBitmap> FallbackExtractFrameAsync(
        CanvasDevice device, string videoPath, TimeSpan position, int width, int height)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        try
        {
            return await ExtractFrameFromCompositionAsync(device, composition, position, width, height);
        }
        finally
        {
            // Called per frame — release the clip's file handle immediately.
            composition.Clips.Clear();
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release every context even if one of them throws while disposing, so a single
        // failure cannot orphan the remaining compositors and file handles.
        foreach (var context in _contexts.Values)
        {
            try
            {
                context.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Failed to release '{context.VideoFilePath}': {ex.Message}");
            }
        }
        _contexts.Clear();

        _textSlideRenderer?.Dispose();
        _textSlideRenderer = null;
        _transitionRenderer?.Dispose();
        _transitionRenderer = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Identity of a render context: the same source file rendered with different
    /// per-segment styles needs its own compositor, and segments sharing a file and
    /// style share one.
    /// </summary>
    private readonly record struct SourceKey(string Path, BackgroundStyle Background, CursorStyle Cursor);

    /// <summary>All render state owned for one (source file, style) combination.</summary>
    private sealed class SourceContext : IDisposable
    {
        public required string VideoFilePath { get; init; }
        public required int SourceWidth { get; init; }
        public required int SourceHeight { get; init; }

        /// <summary>Set before the context is published; never null once built.</summary>
        public FrameCompositor Compositor { get; set; } = null!;

        public VideoFrameReader? Reader { get; set; }

        /// <summary>Only used when no captured JPEG frames exist for this source.</summary>
        public MediaComposition? SourceComposition { get; set; }

        public MediaComposition? WebcamComposition { get; set; }
        public int WebcamExtractWidth { get; set; }
        public int WebcamExtractHeight { get; set; }

        /// <summary>
        /// Idempotent: releases the compositor's GPU resources, the frame reader, and the
        /// media compositions' clips (which is what frees the underlying file handles).
        /// The compositions are always cleared, even if disposing the compositor throws.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Compositor?.Dispose();
            }
            finally
            {
                // Null out so a second Dispose is a no-op (the property is non-nullable
                // only because it is always assigned before the context is published).
                Compositor = null!;

                Reader?.Dispose();
                Reader = null;

                // Clearing the clips releases the MediaClips' native resources and the
                // video file handles they hold.
                SourceComposition?.Clips.Clear();
                SourceComposition = null;
                WebcamComposition?.Clips.Clear();
                WebcamComposition = null;
            }
        }
    }
}
