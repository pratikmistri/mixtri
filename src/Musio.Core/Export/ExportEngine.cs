using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// High-level orchestrator for the full export pipeline.
/// Coordinates encoder detection, video encoding, and output delivery.
/// </summary>
public class ExportEngine
{
    /// <summary>
    /// Runs the full export pipeline: detects hardware encoders, composites frames,
    /// and writes the final video to <paramref name="outputFolder"/>.
    /// </summary>
    /// <returns>The full path to the exported file.</returns>
    public async Task<string> ExportProjectAsync(
        Project project,
        ExportSettings settings,
        CompositionConfig composition,
        string outputFolder,
        TimelineModel? timeline = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        Directory.CreateDirectory(outputFolder);

        // Detect best encoder (informational for now; MediaComposition uses system defaults)
        var encoder = HardwareEncoderDetector.DetectBestEncoder();
        Debug.WriteLine(
            $"[ExportEngine] Using encoder: {encoder.Name} (HW={encoder.IsHardware}, Vendor={encoder.Vendor})");

        // Load cursor data using the canonical binary format from MouseHookRecorder
        var mouseData = LoadMouseData(project.CursorDataFilePath);

        // Load keyboard data if available and enrich composition config
        var enrichedComposition = EnrichCompositionWithOverlays(project, composition);

        // Resolve output dimensions from the source recording so the export
        // matches the editor preview's aspect ratio. Fixed resolution targets
        // (e.g. 1920x1080) would stretch non-16:9 recordings.
        int resW = project.Width > 0 ? project.Width : 1920;
        int resH = project.Height > 0 ? project.Height : 1080;

        // Override composition with export settings — match editor preview defaults.
        // The export video FPS must match the compositor FPS to keep cursor/click
        // animations perfectly aligned with source frames. The preview runs at 30fps
        // and so should the export.
        int exportFps = Math.Min(settings.Fps, 30);
        var exportComposition = enrichedComposition with
        {
            OutputFps = exportFps,
            AspectRatio = settings.AspectRatio,
            Cursor = enrichedComposition.Cursor with
            {
                Scale = Math.Max(enrichedComposition.Cursor.Scale, 2.0f),
                ClickHighlightEnabled = false,
            },
        };

        // Use the same FPS for the encoder so video duration matches compositor
        var exportSettings = settings with { Fps = exportFps };

        // Build timeline mapper if timeline edits are present
        TimelineMapper? timelineMapper = null;
        if (timeline is not null)
        {
            timelineMapper = new TimelineMapper(timeline, settings.Fps);
        }

        // Build output file path
        // WebM is not natively supported by Windows Media APIs — fall back to MP4
        var effectiveFormat = settings.Format;
        if (effectiveFormat == VideoFormat.WebM)
        {
            Debug.WriteLine("[ExportEngine] WebM is not natively supported; falling back to MP4 container.");
            effectiveFormat = VideoFormat.MP4;
        }

        string extension = GetFileExtension(effectiveFormat);
        string sanitizedName = SanitizeFileName(project.Name);
        string outputPath = Path.Combine(outputFolder, $"{sanitizedName}{extension}");
        outputPath = EnsureUniqueFilePath(outputPath);

        var effectiveSettings = new ExportSettings
        {
            Resolution = exportSettings.Resolution,
            Fps = exportSettings.Fps,
            Format = effectiveFormat,
            Quality = exportSettings.Quality,
            AspectRatio = exportSettings.AspectRatio,
        };

        if (effectiveFormat == VideoFormat.GIF)
        {
            await ExportGifAsync(
                project, mouseData, exportComposition,
                effectiveSettings, timelineMapper, outputPath, progress, ct);
        }
        else
        {
            using var videoEncoder = new VideoEncoder(effectiveSettings);
            await videoEncoder.ExportAsync(
                project,
                mouseData,
                exportComposition,
                resW, resH,
                outputPath,
                timelineMapper,
                progress,
                ct);
        }

        return outputPath;
    }

    private async Task ExportGifAsync(
        Project project,
        MouseRecordingData mouseData,
        CompositionConfig composition,
        ExportSettings settings,
        TimelineMapper? timelineMapper,
        string outputPath,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var compositor = new FrameCompositor(composition);

        var sourceFile = await StorageFile.GetFileFromPathAsync(project.VideoFilePath);
        var sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
        var sourceProps = sourceClip.GetVideoEncodingProperties();
        int sourceWidth = (int)sourceProps.Width;
        int sourceHeight = (int)sourceProps.Height;

        await compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, project.Duration);

        int totalFrames = timelineMapper?.TotalOutputFrames ?? compositor.TotalFrames;
        int fps = settings.Fps;

        // Fast path: read JPEG frames from .frames/ directory
        var frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, fps);

        // Slow path fallback: reuse a single MediaComposition for seeking
        MediaComposition? sourceComp = null;
        if (frameReader is null)
        {
            sourceComp = new MediaComposition();
            sourceComp.Clips.Add(sourceClip);
        }

        // Webcam source for overlay (opened once, reused per frame)
        MediaComposition? webcamComp = null;
        int webcamWidth = 0, webcamHeight = 0;
        if (!string.IsNullOrWhiteSpace(project.WebcamFilePath) && File.Exists(project.WebcamFilePath))
        {
            var webcamFile = await StorageFile.GetFileFromPathAsync(project.WebcamFilePath);
            var webcamClip = await MediaClip.CreateFromFileAsync(webcamFile);
            var webcamProps = webcamClip.GetVideoEncodingProperties();
            webcamWidth = (int)webcamProps.Width;
            webcamHeight = (int)webcamProps.Height;
            webcamComp = new MediaComposition();
            webcamComp.Clips.Add(webcamClip);
        }

        try
        {
            var gifEncoder = new GifEncoder();
            await gifEncoder.ExportGifAsync(
                compositor,
                async frameIndex =>
                {
                    double timeSeconds = timelineMapper is not null
                        ? timelineMapper.GetSourceTimeForOutputFrame(frameIndex)
                        : (double)frameIndex / fps;
                    var timeSpan = TimeSpan.FromSeconds(timeSeconds);

                    // Extract webcam frame and set on compositor
                    if (webcamComp is not null)
                    {
                        using var webcamFrame = await ExtractFrameFromCompositionAsync(
                            device, webcamComp, timeSpan, webcamWidth, webcamHeight);
                        compositor.SetWebcamFrame(webcamFrame);
                    }

                    // Fast path: JPEG frames; slow path: reusable composition
                    if (frameReader is not null)
                    {
                        return await frameReader.LoadFrameAtTimeAsync(timeSpan)
                            ?? await FallbackExtractFrameAsync(device, project.VideoFilePath, timeSpan, sourceWidth, sourceHeight);
                    }

                    return await ExtractFrameFromCompositionAsync(
                        device, sourceComp!, timeSpan, sourceWidth, sourceHeight);
                },
                totalFrames,
                fps,
                outputPath,
                progress,
                ct);
        }
        finally
        {
            frameReader?.Dispose();
        }
    }

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

    /// <summary>
    /// Copies the exported file to the system clipboard.
    /// </summary>
    public async Task CopyToClipboardAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Export file not found.", filePath);

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));

        var dataPackage = new DataPackage();
        dataPackage.SetStorageItems(new[] { file });
        dataPackage.RequestedOperation = DataPackageOperation.Copy;

        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
    }

    /// <summary>
    /// Returns the pixel dimensions for a <see cref="VideoResolution"/> enum value.
    /// </summary>
    public static (int Width, int Height) GetResolutionDimensions(VideoResolution resolution) =>
        resolution switch
        {
            VideoResolution.HD720 => (1280, 720),
            VideoResolution.HD1080 => (1920, 1080),
            VideoResolution.QHD => (2560, 1440),
            VideoResolution.UHD4K => (3840, 2160),
            _ => (1920, 1080),
        };

    /// <summary>
    /// Loads mouse recording data using <see cref="MouseHookRecorder.LoadFromFile"/>,
    /// which reads the canonical MCUR binary format. Returns empty data if the file
    /// is missing or not specified.
    /// </summary>
    private static MouseRecordingData LoadMouseData(string cursorDataFilePath)
    {
        if (string.IsNullOrWhiteSpace(cursorDataFilePath) || !File.Exists(cursorDataFilePath))
        {
            return new MouseRecordingData
            {
                Samples = [],
                Clicks = [],
                StartTimestampTicks = 0,
                EndTimestampTicks = 0,
                TickFrequency = TimeSpan.TicksPerSecond,
            };
        }

        return MouseHookRecorder.LoadFromFile(cursorDataFilePath);
    }

    private static string GetFileExtension(VideoFormat format) => format switch
    {
        VideoFormat.MP4 => ".mp4",
        VideoFormat.GIF => ".gif",
        VideoFormat.WebM => ".webm",
        _ => ".mp4",
    };

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "export";

        char[] invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name
            .Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "export" : sanitized;
    }

    private static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path)!;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>
    /// Enriches the composition config with overlay data from the project:
    /// keyboard events and subtitle segments, if their data files exist.
    /// </summary>
    private static CompositionConfig EnrichCompositionWithOverlays(
        Project project, CompositionConfig composition)
    {
        var keyboardEvents = composition.KeyboardEvents;
        var subtitles = composition.Subtitles;

        // Load keyboard data if the project has it and the config expects keyboard overlay
        if (composition.KeyboardStyle is not null &&
            !string.IsNullOrWhiteSpace(project.KeyboardDataFilePath) &&
            File.Exists(project.KeyboardDataFilePath))
        {
            try
            {
                var loadedEvents = RecordingSession.LoadKeyboardData(project.KeyboardDataFilePath);
                if (loadedEvents.Count > 0)
                    keyboardEvents = loadedEvents;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExportEngine] Failed to load keyboard data: {ex.Message}");
            }
        }

        // Return enriched config if anything changed
        if (keyboardEvents != composition.KeyboardEvents || subtitles != composition.Subtitles)
        {
            return composition with
            {
                KeyboardEvents = keyboardEvents,
                Subtitles = subtitles,
            };
        }

        return composition;
    }
}