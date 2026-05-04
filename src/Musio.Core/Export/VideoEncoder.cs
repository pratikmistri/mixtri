using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// Settings for video export.
/// </summary>
public record ExportSettings
{
    public VideoResolution Resolution { get; init; } = VideoResolution.HD1080;
    public int Fps { get; init; } = 30;
    public VideoFormat Format { get; init; } = VideoFormat.MP4;
    public VideoQuality Quality { get; init; } = VideoQuality.High;
    public AspectRatio AspectRatio { get; init; } = AspectRatio.Auto;
}

/// <summary>
/// Progress information for an ongoing export operation.
/// </summary>
public record ExportProgress(
    int CurrentFrame,
    int TotalFrames,
    double PercentComplete,
    TimeSpan Elapsed,
    TimeSpan EstimatedRemaining);

/// <summary>
/// Exports composited video frames directly to an MP4 file using
/// <see cref="MediaStreamSource"/> + <see cref="MediaTranscoder"/>,
/// mirroring the editor preview pipeline. No intermediate temp files.
/// Audio is muxed from the source recording in a fast second pass.
/// </summary>
public class VideoEncoder : IDisposable
{
    private readonly ExportSettings _settings;
    private bool _disposed;

    // Serializes frame compositing so shared state (compositor, frameReader)
    // is never accessed concurrently by overlapping SampleRequested callbacks
    // from the MediaStreamSource pipeline.
    private readonly SemaphoreSlim _frameSemaphore = new(1, 1);

    public VideoEncoder(ExportSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Exports a recording by compositing each frame in real-time (like the editor
    /// preview) and streaming directly to the H.264 encoder via
    /// <see cref="MediaStreamSource"/>. No temp files are written.
    /// Audio is muxed from source recordings in a second pass.
    /// </summary>
    public async Task ExportAsync(
        Project project,
        MouseRecordingData mouseData,
        CompositionConfig compositionConfig,
        int targetWidth,
        int targetHeight,
        string outputPath,
        TimelineMapper? timelineMapper = null,
        TimelineModel? timeline = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.VideoFilePath);
        ArgumentNullException.ThrowIfNull(mouseData);
        ArgumentNullException.ThrowIfNull(compositionConfig);
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stopwatch = Stopwatch.StartNew();
        var device = CanvasDevice.GetSharedDevice();

        // Open source video for dimensions and audio tracks.
        // If the MP4 is missing or corrupt (FinalizeAsync failure during recording),
        // fall back to project metadata — JPEG frames can still provide visuals.
        MediaClip? sourceClip = null;
        int sourceWidth = project.Width > 0 ? project.Width : 1920;
        int sourceHeight = project.Height > 0 ? project.Height : 1080;

        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(project.VideoFilePath);
            sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
            var sourceProps = sourceClip.GetVideoEncodingProperties();
            sourceWidth = (int)sourceProps.Width;
            sourceHeight = (int)sourceProps.Height;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[VideoEncoder] Failed to open source video (will use JPEG frames): {ex.Message}");
        }

        // Initialize compositor (same pipeline as editor preview)
        using var compositor = new FrameCompositor(compositionConfig);
        await compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, project.Duration,
            project.MouseToVideoOffsetSeconds, project.CropOffsetX, project.CropOffsetY, project.DpiScale);

        // Sync timeline zoom state (manual keyframes + suppressed auto clicks)
        if (timeline is not null)
        {
            var manualKeyframes = timeline.ZoomKeyframes
                .Where(k => k.IsManual)
                .ToList();
            compositor.SyncManualZoomKeyframes(manualKeyframes);

            if (timeline.SuppressedClickTicks.Count > 0)
                compositor.SyncSuppressedClickTicks(timeline.SuppressedClickTicks);
        }

        // Total output frames based on the EXPORT fps, not the compositor's
        // internal fps (which is capped at 30 for cursor/click timing).
        int totalFrames = timelineMapper?.TotalOutputFrames
            ?? (int)(project.Duration.TotalSeconds * _settings.Fps);
        int compositorWidth = compositor.OutputWidth;
        int compositorHeight = compositor.OutputHeight;

        // Encode at compositor output dimensions to preserve aspect ratio.
        // The compositor already handles background padding and aspect-ratio
        // cropping, so its output size is the correct final frame size.
        // H.264 uses 16×16 macroblocks — round down to mod-16 to avoid
        // hardware encoder alignment issues that cause horizontal banding.
        targetWidth = (compositorWidth / 16) * 16;
        targetHeight = (compositorHeight / 16) * 16;
        if (targetWidth < 16) targetWidth = 16;
        if (targetHeight < 16) targetHeight = 16;
        bool needsScaling = targetWidth != compositorWidth || targetHeight != compositorHeight;

        // Load source frames from .frames/ JPEGs using the RECORDING FPS so
        // frame indices map correctly to the on-disk frame numbering.
        int sourceFps = project.Fps > 0 ? project.Fps : _settings.Fps;
        var frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, sourceFps);

        // Fallback: reuse single MediaComposition for seeking
        MediaComposition? sourceComp = null;
        if (frameReader is null && sourceClip is not null)
        {
            try
            {
                var fallbackFile = await StorageFile.GetFileFromPathAsync(project.VideoFilePath);
                sourceComp = new MediaComposition();
                sourceComp.Clips.Add(await MediaClip.CreateFromFileAsync(fallbackFile));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoEncoder] Fallback composition failed: {ex.Message}");
            }
        }

        if (frameReader is null && sourceComp is null)
            throw new InvalidOperationException(
                "Cannot export: no JPEG frames or valid source video found.");

        // Prepare webcam source (opened once, reused per frame)
        MediaComposition? webcamComp = null;
        int webcamWidth = 0, webcamHeight = 0;
        if (!string.IsNullOrWhiteSpace(project.WebcamFilePath) && File.Exists(project.WebcamFilePath))
        {
            try
            {
                var webcamFile = await StorageFile.GetFileFromPathAsync(project.WebcamFilePath);
                var webcamClip = await MediaClip.CreateFromFileAsync(webcamFile);
                var webcamProps = webcamClip.GetVideoEncodingProperties();
                webcamWidth = (int)webcamProps.Width;
                webcamHeight = (int)webcamProps.Height;
                webcamComp = new MediaComposition();
                webcamComp.Clips.Add(webcamClip);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoEncoder] Failed to load webcam video: {ex.Message}");
            }
        }

        // Determine if we need an audio mux pass
        bool hasAudio = (sourceClip?.EmbeddedAudioTracks.Count > 0)
            || (project.AudioFilePaths is { Count: > 0 });

        // Video-only output path (audio muxed in second pass if needed)
        string videoOnlyPath = hasAudio
            ? Path.Combine(Path.GetDirectoryName(outputPath)!, $".musio_video_{Guid.NewGuid():N}.mp4")
            : outputPath;

        try
        {
            // ── Pass 1: Direct composited-frame encoding (no temp files) ──
            int currentFrame = 0;
            var pendingSamples = new List<Task>();
            var pendingSamplesLock = new object();
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            // Create uncompressed video stream for the MediaStreamSource
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)targetWidth, (uint)targetHeight);
            videoProps.FrameRate.Numerator = (uint)_settings.Fps;
            videoProps.FrameRate.Denominator = 1;

            Debug.WriteLine($"[VideoEncoder] Dimensions: {targetWidth}x{targetHeight}");

            var videoDesc = new VideoStreamDescriptor(videoProps);

            var streamSource = new MediaStreamSource(videoDesc);
            streamSource.Duration = TimeSpan.FromSeconds((double)totalFrames / _settings.Fps);
            streamSource.BufferTime = TimeSpan.Zero;

            streamSource.Starting += (MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
            {
                args.Request.SetActualStartPosition(TimeSpan.Zero);
            };

            streamSource.SampleRequested += (MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args) =>
            {
                // Atomically reserve a frame index to avoid duplicate/skipped frames
                int frame = Interlocked.Increment(ref currentFrame) - 1;
                if (frame >= totalFrames)
                {
                    args.Request.Sample = null; // end of stream
                    return;
                }

                var deferral = args.Request.GetDeferral();
                var task = ProduceSampleAsync(
                    args.Request, deferral, frame, totalFrames,
                    compositor, frameReader, sourceComp, webcamComp,
                    device, project.VideoFilePath, sourceWidth, sourceHeight,
                    compositorWidth, compositorHeight, targetWidth, targetHeight,
                    needsScaling, timelineMapper, progress, stopwatch, ct);

                lock (pendingSamplesLock)
                {
                    // Remove completed tasks to prevent unbounded list growth
                    pendingSamples.RemoveAll(t => t.IsCompleted);
                    pendingSamples.Add(task);
                }
            };

            // Transcode: composited BGRA8 frames → H.264 MP4
            // Use software encoding to avoid hardware encoder quirks with
            // non-standard dimensions and D3D surface interop.
            var transcoder = new MediaTranscoder();
            transcoder.HardwareAccelerationEnabled = false;
            var profile = CreateEncodingProfile(targetWidth, targetHeight);

            // Remove audio from first-pass profile
            profile.Audio = null;

            var outputFile = await CreateOutputFileAsync(videoOnlyPath);
            using var outputStream = await outputFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);

            var prepResult = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                streamSource, outputStream, profile);

            if (!prepResult.CanTranscode)
                throw new InvalidOperationException(
                    $"Transcoder cannot encode: {prepResult.FailureReason}");

            await prepResult.TranscodeAsync().AsTask(ct);

            // Drain any still-running sample tasks before disposing shared state
            Task[] snapshot;
            lock (pendingSamplesLock)
            {
                snapshot = pendingSamples.ToArray();
            }
            await Task.WhenAll(snapshot).ConfigureAwait(false);

            // ── Pass 2: Mux audio (fast — no frame re-compositing) ──
            if (hasAudio)
            {
                progress?.Report(new ExportProgress(
                    totalFrames, totalFrames, 99, stopwatch.Elapsed, TimeSpan.FromSeconds(2)));

                await MuxAudioAsync(videoOnlyPath, outputPath, sourceClip, project, timelineMapper, ct);
            }
        }
        finally
        {
            frameReader?.Dispose();

            // Release MediaComposition/MediaClip native resources to avoid
            // holding video file handles and leaking memory.
            sourceComp?.Clips.Clear();
            webcamComp?.Clips.Clear();

            // Clean up temp video-only file
            if (hasAudio)
            {
                try { File.Delete(videoOnlyPath); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Produces a single composited video sample for the <see cref="MediaStreamSource"/>.
    /// Runs the same pipeline as the editor preview: load source JPEG → composite → pixel bytes.
    /// </summary>
    private async Task ProduceSampleAsync(
        MediaStreamSourceSampleRequest request,
        MediaStreamSourceSampleRequestDeferral deferral,
        int frameIndex, int totalFrames,
        FrameCompositor compositor,
        VideoFrameReader? frameReader,
        MediaComposition? sourceComp,
        MediaComposition? webcamComp,
        CanvasDevice device,
        string sourceVideoPath,
        int sourceWidth, int sourceHeight,
        int compositorWidth, int compositorHeight,
        int targetWidth, int targetHeight,
        bool needsScaling,
        TimelineMapper? timelineMapper,
        IProgress<ExportProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        CanvasRenderTarget? outputSurface = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // Serialize frame production: shared compositor/frame-reader/webcam
            // composition state is not thread-safe and can corrupt frames if
            // accessed concurrently by overlapping SampleRequested callbacks.
            bool semaphoreAcquired = false;
            try
            {
            await _frameSemaphore.WaitAsync(ct).ConfigureAwait(false);
            semaphoreAcquired = true;

            double timeSeconds = timelineMapper is not null
                ? timelineMapper.GetSourceTimeForOutputFrame(frameIndex)
                : (double)frameIndex / _settings.Fps;
            var timeSpan = TimeSpan.FromSeconds(timeSeconds);
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            // Webcam overlay
            if (webcamComp is not null)
            {
                try
                {
                    using var webcamFrame = await ExtractFrameFromCompositionAsync(
                        device, webcamComp, timeSpan,
                        compositorWidth > 0 ? compositorWidth : sourceWidth,
                        compositorHeight > 0 ? compositorHeight : sourceHeight);
                    compositor.SetWebcamFrame(webcamFrame);
                }
                catch
                {
                    compositor.SetWebcamFrame(null);
                }
            }

            // Load source frame (same as editor preview)
            using var sourceFrame = frameReader is not null
                ? await frameReader.LoadFrameAtTimeAsync(timeSpan)
                    ?? await FallbackExtractFrameAsync(device, sourceVideoPath, sourceWidth, sourceHeight, timeSpan)
                : await ExtractFrameFromCompositionAsync(device, sourceComp!, timeSpan, sourceWidth, sourceHeight);

            // Composite using the exact source time so cursor, click, and zoom
            // effects are precisely synchronized with the visual frame content.
            var composedFrame = compositor.ComposeFrame(sourceFrame, timeSeconds);

            // Build the output surface. For scaling we need a separate render
            // target; otherwise the composed frame IS the output surface.
            // Each frame gets its own surface so the encoder can read it async
            // after we release the semaphore.
            if (needsScaling)
            {
                outputSurface = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
                using (var ds = outputSurface.CreateDrawingSession())
                {
                    ds.DrawImage(composedFrame,
                        new Windows.Foundation.Rect(0, 0, targetWidth, targetHeight),
                        new Windows.Foundation.Rect(0, 0, compositorWidth, compositorHeight));
                }
                composedFrame.Dispose();
            }
            else
            {
                outputSurface = composedFrame;
            }

            // Give the D3D11 surface directly to the encoder — bypasses all
            // pixel extraction and stride alignment issues entirely.
            var timestamp = TimeSpan.FromSeconds((double)frameIndex / _settings.Fps);
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(outputSurface, timestamp);
            sample.Duration = frameDuration;

            // Dispose the GPU surface after the encoder has consumed it.
            sample.Processed += (s, e) => outputSurface.Dispose();

            request.Sample = sample;

            }
            finally
            {
                if (semaphoreAcquired)
                    _frameSemaphore.Release();
            }

            // Report progress
            if (progress is not null)
            {
                double percent = (double)(frameIndex + 1) / totalFrames * 100.0;
                var elapsed = stopwatch.Elapsed;
                var perFrame = elapsed / (frameIndex + 1);
                var remaining = perFrame * (totalFrames - frameIndex - 1);
                progress.Report(new ExportProgress(frameIndex + 1, totalFrames, percent, elapsed, remaining));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoEncoder] Frame {frameIndex} error: {ex.Message}");
            // Dispose GPU surface if it was created but never handed to the encoder
            outputSurface?.Dispose();
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Muxes audio from the source recording into the video-only MP4.
    /// Uses <see cref="MediaComposition"/> for reliable audio handling.
    /// </summary>
    private async Task MuxAudioAsync(
        string videoOnlyPath, string finalOutputPath,
        MediaClip? sourceClip, Project project,
        TimelineMapper? timelineMapper, CancellationToken ct)
    {
        var muxComp = new MediaComposition();

        // Add the video-only file as the video track
        var videoFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoOnlyPath));
        var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
        muxComp.Clips.Add(videoClip);

        // Add embedded audio from the source recording
        if (sourceClip is not null && sourceClip.EmbeddedAudioTracks.Count > 0)
        {
            var audioTrack = sourceClip.EmbeddedAudioTracks.First();
            var bgAudioTrack = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(audioTrack);

            if (timelineMapper is not null)
            {
                bgAudioTrack.TrimTimeFromStart = timelineMapper.TrimStart;
                var originalDuration = bgAudioTrack.OriginalDuration;
                if (timelineMapper.TrimEnd < originalDuration)
                    bgAudioTrack.TrimTimeFromEnd = originalDuration - timelineMapper.TrimEnd;
            }

            muxComp.BackgroundAudioTracks.Add(bgAudioTrack);
        }

        // Add separately recorded audio files
        if (project.AudioFilePaths is { Count: > 0 })
        {
            foreach (var audioPath in project.AudioFilePaths)
            {
                if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                    continue;

                try
                {
                    var audioFile = await StorageFile.GetFileFromPathAsync(audioPath);
                    var bgTrack = await BackgroundAudioTrack.CreateFromFileAsync(audioFile);

                    if (timelineMapper is not null)
                    {
                        bgTrack.TrimTimeFromStart = timelineMapper.TrimStart;
                        var originalDuration = bgTrack.OriginalDuration;
                        if (timelineMapper.TrimEnd < originalDuration)
                            bgTrack.TrimTimeFromEnd = originalDuration - timelineMapper.TrimEnd;
                    }

                    muxComp.BackgroundAudioTracks.Add(bgTrack);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VideoEncoder] Failed to add audio track '{audioPath}': {ex.Message}");
                }
            }
        }

        // Render final output with audio
        var profile = CreateEncodingProfile(
            (int)videoClip.GetVideoEncodingProperties().Width,
            (int)videoClip.GetVideoEncodingProperties().Height);
        var outputFile = await CreateOutputFileAsync(finalOutputPath);

        var renderOp = muxComp.RenderToFileAsync(outputFile, MediaTrimmingPreference.Fast, profile);
        var tcs = new TaskCompletionSource<object?>();
        using var reg = ct.Register(() => renderOp.Cancel());
        renderOp.Completed = (info, status) =>
        {
            if (status == Windows.Foundation.AsyncStatus.Completed)
                tcs.TrySetResult(null);
            else if (status == Windows.Foundation.AsyncStatus.Canceled)
                tcs.TrySetCanceled();
            else
                tcs.TrySetException(info.ErrorCode ?? new InvalidOperationException("Audio mux failed."));
        };
        await tcs.Task;

        // Release MediaComposition native resources after mux completes
        muxComp.Clips.Clear();
        muxComp.BackgroundAudioTracks.Clear();
    }

    private MediaEncodingProfile CreateEncodingProfile(int width, int height)
    {
        // Use HD1080p as a well-formed template with valid Video + Audio
        // properties. Override all resolution-dependent fields explicitly.
        // The encoder auto-selects the correct H.264 Level for the actual
        // dimensions (Level 5.1+ for 2.8K/4K). The scaled bitrate ensures
        // high-res exports have adequate quality.
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video!.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)_settings.Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = ComputeBitrate(width, height);
        profile.Video.Subtype = "H264";

        return profile;
    }

    /// <summary>
    /// Scales bitrate proportionally to pixel count relative to 1080p so
    /// high-resolution exports (2.8K, 4K) receive adequate bitrate.
    /// </summary>
    private uint ComputeBitrate(int width, int height)
    {
        uint baseBitrate = _settings.Quality switch
        {
            VideoQuality.Draft => 5_000_000,
            VideoQuality.Standard => 10_000_000,
            VideoQuality.High => 20_000_000,
            VideoQuality.Ultra => 50_000_000,
            _ => 20_000_000,
        };

        const double baselinePixels = 1920.0 * 1080.0;
        double actualPixels = (double)width * height;
        double scale = Math.Max(1.0, actualPixels / baselinePixels);

        return (uint)(baseBitrate * scale);
    }

    private static async Task<CanvasBitmap> ExtractFrameFromCompositionAsync(
        CanvasDevice device, MediaComposition composition, TimeSpan position,
        int width, int height)
    {
        var clampedPosition = position;
        if (composition.Duration > TimeSpan.Zero && position > composition.Duration)
            clampedPosition = composition.Duration;

        using var thumbnail = await composition.GetThumbnailAsync(
            clampedPosition, width, height, VideoFramePrecision.NearestFrame);

        using var stream = thumbnail.AsStream();
        using var randomAccessStream = stream.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(device, randomAccessStream);
    }

    private static async Task<CanvasBitmap> FallbackExtractFrameAsync(
        CanvasDevice device, string videoPath, int width, int height, TimeSpan position)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var comp = new MediaComposition();
        comp.Clips.Add(clip);

        using var thumbnail = await comp.GetThumbnailAsync(
            position, width, height, VideoFramePrecision.NearestFrame);

        // Release MediaComposition/MediaClip native resources immediately —
        // this method is called per-frame, so without cleanup each invocation
        // leaks a composition + clip holding a file handle to the video.
        comp.Clips.Clear();

        using var stream = thumbnail.AsStream();
        using var randomAccessStream = stream.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(device, randomAccessStream);
    }

    private static async Task<StorageFile> CreateOutputFileAsync(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string fileName = Path.GetFileName(outputPath);
        Directory.CreateDirectory(dir);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _frameSemaphore.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
