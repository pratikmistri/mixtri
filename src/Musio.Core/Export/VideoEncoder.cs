using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// Settings for video export.
/// </summary>
public record ExportSettings
{
    public VideoResolution Resolution { get; init; } = VideoResolution.HD1080;
    public int Fps { get; init; } = 60;
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

        // Open source video once for dimensions and audio tracks
        var sourceFile = await StorageFile.GetFileFromPathAsync(project.VideoFilePath);
        var sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
        var sourceProps = sourceClip.GetVideoEncodingProperties();
        int sourceWidth = (int)sourceProps.Width;
        int sourceHeight = (int)sourceProps.Height;

        // Initialize compositor (same pipeline as editor preview)
        using var compositor = new FrameCompositor(compositionConfig);
        await compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, project.Duration);

        int totalFrames = timelineMapper?.TotalOutputFrames ?? compositor.TotalFrames;
        int compositorWidth = compositor.OutputWidth;
        int compositorHeight = compositor.OutputHeight;
        bool needsScaling = compositorWidth != targetWidth || compositorHeight != targetHeight;

        // Load source frames from .frames/ JPEGs (same as editor preview)
        var frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, _settings.Fps);

        // Fallback: reuse single MediaComposition for seeking
        MediaComposition? sourceComp = null;
        if (frameReader is null)
        {
            sourceComp = new MediaComposition();
            sourceComp.Clips.Add(await MediaClip.CreateFromFileAsync(sourceFile));
        }

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
        bool hasAudio = sourceClip.EmbeddedAudioTracks.Count > 0
            || (project.AudioFilePaths is { Count: > 0 });

        // Video-only output path (audio muxed in second pass if needed)
        string videoOnlyPath = hasAudio
            ? Path.Combine(Path.GetDirectoryName(outputPath)!, $".musio_video_{Guid.NewGuid():N}.mp4")
            : outputPath;

        try
        {
            // ── Pass 1: Direct composited-frame encoding (no temp files) ──
            int currentFrame = 0;
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            // Create uncompressed video stream for the MediaStreamSource
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)targetWidth, (uint)targetHeight);
            videoProps.FrameRate.Numerator = (uint)_settings.Fps;
            videoProps.FrameRate.Denominator = 1;
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
                if (currentFrame >= totalFrames)
                {
                    args.Request.Sample = null; // end of stream
                    return;
                }

                var deferral = args.Request.GetDeferral();
                int frame = currentFrame;
                currentFrame++;
                _ = ProduceSampleAsync(
                    args.Request, deferral, frame, totalFrames,
                    compositor, frameReader, sourceComp, webcamComp,
                    device, project.VideoFilePath, sourceWidth, sourceHeight,
                    compositorWidth, compositorHeight, targetWidth, targetHeight,
                    needsScaling, timelineMapper, progress, stopwatch, ct);
            };

            // Transcode: composited BGRA8 frames → H.264 MP4
            var transcoder = new MediaTranscoder();
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
        try
        {
            ct.ThrowIfCancellationRequested();

            double timeSeconds = timelineMapper is not null
                ? timelineMapper.GetSourceTimeForOutputFrame(frameIndex)
                : (double)frameIndex / _settings.Fps;
            var timeSpan = TimeSpan.FromSeconds(timeSeconds);
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            int compositorFrameIndex = Math.Clamp(
                (int)(timeSeconds * _settings.Fps),
                0, Math.Max(0, compositor.TotalFrames - 1));

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

            // Composite (same as editor preview)
            using var composedFrame = compositor.ComposeFrame(sourceFrame, compositorFrameIndex);

            // Scale if needed
            CanvasRenderTarget outputFrame;
            bool disposeOutput = false;
            if (needsScaling)
            {
                outputFrame = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
                using (var ds = outputFrame.CreateDrawingSession())
                {
                    ds.DrawImage(composedFrame,
                        new Windows.Foundation.Rect(0, 0, targetWidth, targetHeight),
                        new Windows.Foundation.Rect(0, 0, compositorWidth, compositorHeight));
                }
                disposeOutput = true;
            }
            else
            {
                outputFrame = composedFrame;
            }

            try
            {
                // Get raw pixels — Win2D returns top-down row order, but
                // MediaStreamSource with Bgra8 expects bottom-up. Flip rows.
                var pixelBytes = outputFrame.GetPixelBytes();
                int frameW = (int)outputFrame.SizeInPixels.Width;
                int frameH = (int)outputFrame.SizeInPixels.Height;
                int stride = frameW * 4;

                byte[] flipped = new byte[pixelBytes.Length];
                for (int y = 0; y < frameH; y++)
                {
                    Buffer.BlockCopy(pixelBytes, y * stride, flipped, (frameH - 1 - y) * stride, stride);
                }

                var buffer = CryptographicBuffer.CreateFromByteArray(flipped);
                var timestamp = TimeSpan.FromSeconds((double)frameIndex / _settings.Fps);
                var sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);
                sample.Duration = frameDuration;

                request.Sample = sample;
            }
            finally
            {
                if (disposeOutput)
                    outputFrame.Dispose();
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
        MediaClip sourceClip, Project project,
        TimelineMapper? timelineMapper, CancellationToken ct)
    {
        var muxComp = new MediaComposition();

        // Add the video-only file as the video track
        var videoFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoOnlyPath));
        var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
        muxComp.Clips.Add(videoClip);

        // Add embedded audio from the source recording
        if (sourceClip.EmbeddedAudioTracks.Count > 0)
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
    }

    private MediaEncodingProfile CreateEncodingProfile(int width, int height)
    {
        var profile = MediaEncodingProfile.CreateMp4(GetProfileQuality());

        if (profile.Video is not null)
        {
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)_settings.Fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = GetBitrate();
            profile.Video.Subtype = "H264";
        }

        return profile;
    }

    private VideoEncodingQuality GetProfileQuality() => _settings.Quality switch
    {
        VideoQuality.Draft => VideoEncodingQuality.Wvga,
        VideoQuality.Standard => VideoEncodingQuality.HD720p,
        VideoQuality.High => VideoEncodingQuality.HD1080p,
        VideoQuality.Ultra => VideoEncodingQuality.Uhd2160p,
        _ => VideoEncodingQuality.HD1080p,
    };

    private uint GetBitrate() => _settings.Quality switch
    {
        VideoQuality.Draft => 5_000_000,
        VideoQuality.Standard => 10_000_000,
        VideoQuality.High => 20_000_000,
        VideoQuality.Ultra => 50_000_000,
        _ => 20_000_000,
    };

    private static async Task<CanvasBitmap> ExtractFrameFromCompositionAsync(
        CanvasDevice device, MediaComposition composition, TimeSpan position,
        int width, int height)
    {
        var clampedPosition = position;
        if (composition.Duration > TimeSpan.Zero && position > composition.Duration)
            clampedPosition = composition.Duration;

        var thumbnail = await composition.GetThumbnailAsync(
            clampedPosition, width, height, VideoFramePrecision.NearestFrame);

        var randomAccessStream = thumbnail.AsStream().AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(device, randomAccessStream);
    }

    private static async Task<CanvasBitmap> FallbackExtractFrameAsync(
        CanvasDevice device, string videoPath, int width, int height, TimeSpan position)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var comp = new MediaComposition();
        comp.Clips.Add(clip);

        var thumbnail = await comp.GetThumbnailAsync(
            position, width, height, VideoFramePrecision.NearestFrame);

        var randomAccessStream = thumbnail.AsStream().AsRandomAccessStream();
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
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
