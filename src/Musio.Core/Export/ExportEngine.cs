using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
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

        // Resolve output dimensions from settings
        var (resW, resH) = GetResolutionDimensions(settings.Resolution);

        // Override composition with export settings
        var exportComposition = composition with
        {
            OutputFps = settings.Fps,
            AspectRatio = settings.AspectRatio,
        };

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
            Resolution = settings.Resolution,
            Fps = settings.Fps,
            Format = effectiveFormat,
            Quality = settings.Quality,
            AspectRatio = settings.AspectRatio,
        };

        if (effectiveFormat == VideoFormat.GIF)
        {
            await ExportGifAsync(
                project.VideoFilePath, mouseData, exportComposition,
                effectiveSettings, outputPath, progress, ct);
        }
        else
        {
            using var videoEncoder = new VideoEncoder(effectiveSettings);
            await videoEncoder.ExportAsync(
                project.VideoFilePath,
                mouseData,
                exportComposition,
                resW, resH,
                outputPath,
                progress,
                ct);
        }

        return outputPath;
    }

    private async Task ExportGifAsync(
        string sourceVideoPath,
        MouseRecordingData mouseData,
        CompositionConfig composition,
        ExportSettings settings,
        string outputPath,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var compositor = new FrameCompositor(composition);

        var sourceFile = await StorageFile.GetFileFromPathAsync(sourceVideoPath);
        var sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
        var sourceProps = sourceClip.GetVideoEncodingProperties();
        int sourceWidth = (int)sourceProps.Width;
        int sourceHeight = (int)sourceProps.Height;

        await compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight);

        int totalFrames = compositor.TotalFrames;
        int fps = settings.Fps;

        var gifEncoder = new GifEncoder();
        await gifEncoder.ExportGifAsync(
            compositor,
            async frameIndex =>
            {
                double timeSeconds = (double)frameIndex / fps;
                var timeSpan = TimeSpan.FromSeconds(timeSeconds);
                return await ExtractFrameAsync(device, sourceVideoPath, timeSpan, sourceWidth, sourceHeight);
            },
            totalFrames,
            fps,
            outputPath,
            progress,
            ct);
    }

    private static async Task<CanvasBitmap> ExtractFrameAsync(
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
}
