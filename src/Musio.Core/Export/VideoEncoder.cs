using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
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
/// Frame-by-frame video encoder that reads source video frames, composites them
/// via <see cref="FrameCompositor"/>, and writes the result to an output video file
/// using <see cref="MediaComposition"/> APIs.
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
    /// Exports a full recording project by compositing each frame and encoding the result.
    /// Uses JPEG frames from the recording session's .frames/ directory when available
    /// (fast path), falling back to MP4 frame extraction via a single reusable
    /// MediaComposition (slow path). Composited frames are saved as temporary JPEGs
    /// instead of PNGs for faster encoding.
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

        string sourceVideoPath = project.VideoFilePath;
        var stopwatch = Stopwatch.StartNew();
        var device = CanvasDevice.GetSharedDevice();

        // Create the compositor and initialize
        using var compositor = new FrameCompositor(compositionConfig);

        // Open source video once for dimensions and audio tracks
        var sourceFile = await StorageFile.GetFileFromPathAsync(sourceVideoPath);
        var sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
        var sourceProps = sourceClip.GetVideoEncodingProperties();
        int sourceWidth = (int)sourceProps.Width;
        int sourceHeight = (int)sourceProps.Height;

        // Fast path: read JPEG frames from .frames/ directory (avoids per-frame MP4 decode)
        var frameReader = VideoFrameReader.OpenFromVideoPath(sourceVideoPath, _settings.Fps);

        // Slow path fallback: reuse a single MediaComposition for seeking
        MediaComposition? sourceComp = null;
        if (frameReader is null)
        {
            sourceComp = new MediaComposition();
            sourceComp.Clips.Add(await MediaClip.CreateFromFileAsync(sourceFile));
        }

        try
        {
            // Use project duration as the authoritative duration for frame count
            await compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, project.Duration);

            // Use timeline mapper frame count if available, otherwise compositor's count
            int totalFrames = timelineMapper?.TotalOutputFrames ?? compositor.TotalFrames;
            int compositorWidth = compositor.OutputWidth;
            int compositorHeight = compositor.OutputHeight;

            // Determine if we need to scale frames to match the target export resolution
            bool needsScaling = compositorWidth != targetWidth || compositorHeight != targetHeight;

            // Prepare webcam source if available (opened once, reused per frame)
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

            // Build output MediaComposition frame by frame
            var mediaComposition = new MediaComposition();
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            for (int i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Map output frame to source time (applies trim/speed/cut if timeline present)
                double timeSeconds = timelineMapper is not null
                    ? timelineMapper.GetSourceTimeForOutputFrame(i)
                    : (double)i / _settings.Fps;
                var timeSpan = TimeSpan.FromSeconds(timeSeconds);

                // Determine compositor frame index (clamped to available frames)
                int compositorFrameIndex = Math.Clamp(
                    (int)(timeSeconds * _settings.Fps),
                    0, Math.Max(0, compositor.TotalFrames - 1));

                // Extract and set webcam frame if available
                if (webcamComp is not null)
                {
                    try
                    {
                        using var webcamFrame = await ExtractFrameFromCompositionAsync(
                            device, webcamComp, timeSpan, webcamWidth, webcamHeight);
                        compositor.SetWebcamFrame(webcamFrame);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[VideoEncoder] Webcam frame extraction failed at {timeSpan}: {ex.Message}");
                        compositor.SetWebcamFrame(null);
                    }
                }

                // Load source frame: JPEG fast path or MP4 fallback
                using var sourceFrame = frameReader is not null
                    ? await frameReader.LoadFrameAtTimeAsync(timeSpan)
                        ?? await FallbackExtractFrameAsync(device, sourceVideoPath, timeSpan, sourceWidth, sourceHeight)
                    : await ExtractFrameFromCompositionAsync(device, sourceComp!, timeSpan, sourceWidth, sourceHeight);

                // Compose through the full pipeline (background, zoom, cursor, overlays)
                using var composedFrame = compositor.ComposeFrame(sourceFrame, compositorFrameIndex);

                // Scale to target resolution if necessary
                CanvasRenderTarget frameToSave;
                if (needsScaling)
                {
                    frameToSave = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
                    using (var ds = frameToSave.CreateDrawingSession())
                    {
                        ds.DrawImage(composedFrame,
                            new Windows.Foundation.Rect(0, 0, targetWidth, targetHeight),
                            new Windows.Foundation.Rect(0, 0, compositorWidth, compositorHeight));
                    }
                }
                else
                {
                    frameToSave = composedFrame;
                }

                try
                {
                    // Save composited frame as temp JPEG (much faster than PNG)
                    var tempFile = await SaveFrameToTempFileAsync(frameToSave, i, outputPath);
                    var frameClip = await MediaClip.CreateFromImageFileAsync(tempFile, frameDuration);
                    mediaComposition.Clips.Add(frameClip);
                }
                finally
                {
                    if (needsScaling)
                        frameToSave.Dispose();
                }

                // Report progress
                if (progress is not null)
                {
                    double percent = (double)(i + 1) / totalFrames * 100.0;
                    var elapsed = stopwatch.Elapsed;
                    var perFrame = elapsed / (i + 1);
                    var remaining = perFrame * (totalFrames - i - 1);

                    progress.Report(new ExportProgress(i + 1, totalFrames, percent, elapsed, remaining));
                }
            }

            // Copy audio from source clip via embedded audio tracks (apply timeline trim)
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

                mediaComposition.BackgroundAudioTracks.Add(bgAudioTrack);
            }

            // Add separately recorded audio files from the project (apply timeline trim)
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

                        mediaComposition.BackgroundAudioTracks.Add(bgTrack);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[VideoEncoder] Failed to add audio track '{audioPath}': {ex.Message}");
                    }
                }
            }

            // Render to output file using the target resolution for the encoding profile
            var encodingProfile = CreateEncodingProfile(targetWidth, targetHeight);
            var outputFile = await CreateOutputFileAsync(outputPath);

            var renderOp = mediaComposition.RenderToFileAsync(outputFile, MediaTrimmingPreference.Precise, encodingProfile);

            // Wait for rendering with cancellation support
            var tcs = new TaskCompletionSource<object?>();
            using var reg = ct.Register(() => renderOp.Cancel());
            renderOp.Completed = (info, status) =>
            {
                if (status == Windows.Foundation.AsyncStatus.Completed)
                    tcs.TrySetResult(null);
                else if (status == Windows.Foundation.AsyncStatus.Canceled)
                    tcs.TrySetCanceled();
                else
                    tcs.TrySetException(info.ErrorCode ?? new InvalidOperationException("Render failed."));
            };

            await tcs.Task;

            // Cleanup temp frame files
            await CleanupTempFramesAsync(outputPath, totalFrames);
        }
        finally
        {
            frameReader?.Dispose();
        }
    }

    private MediaEncodingProfile CreateEncodingProfile(int width, int height)
    {
        // Always use H.264/MP4 — WebM should already be resolved to MP4 by ExportEngine
        var profile = MediaEncodingProfile.CreateMp4(GetProfileQuality());

        if (profile.Video is not null)
        {
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)_settings.Fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = GetBitrate();

            // Ensure H.264 codec (CodecId for H.264 AVC)
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

    /// <summary>
    /// Extracts a frame from a pre-built <see cref="MediaComposition"/> at the given position.
    /// Reusing the same composition avoids re-opening the source file on every frame.
    /// </summary>
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

    /// <summary>
    /// Last-resort fallback: opens the video file from scratch to extract a single frame.
    /// Only used when both the JPEG frame reader and composition-based extraction fail.
    /// </summary>
    private static async Task<CanvasBitmap> FallbackExtractFrameAsync(
        CanvasDevice device, string videoPath, TimeSpan position,
        int width, int height)
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

    private static async Task<StorageFile> SaveFrameToTempFileAsync(
        CanvasRenderTarget frame, int frameIndex, string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string tempDir = Path.Combine(dir, ".musio_export_temp");
        Directory.CreateDirectory(tempDir);

        string framePath = Path.Combine(tempDir, $"frame_{frameIndex:D6}.jpg");
        using var stream = new FileStream(framePath, FileMode.Create, FileAccess.Write);
        await frame.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.92f);

        return await StorageFile.GetFileFromPathAsync(framePath);
    }

    private static async Task<StorageFile> CreateOutputFileAsync(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string fileName = Path.GetFileName(outputPath);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
    }

    private static async Task CleanupTempFramesAsync(string outputPath, int totalFrames)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string tempDir = Path.Combine(dir, ".musio_export_temp");

        try
        {
            if (Directory.Exists(tempDir))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(tempDir);
                await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
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
