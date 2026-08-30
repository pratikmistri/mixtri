using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Mixtri.Core.Export;

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
            AspectRatio = project.AspectRatio,
            FitMode = project.FitMode,
            CropAnchorX = project.CropAnchorX,
            CropAnchorY = project.CropAnchorY,
            ZoomScope = project.ZoomScope,
            Cursor = enrichedComposition.Cursor with
            {
                Scale = Math.Max(enrichedComposition.Cursor.Scale, 2.0f),
            },
        };

        // Use the same FPS for the encoder so video duration matches compositor
        var exportSettings = settings with { Fps = exportFps };

        // Build timeline mapper if timeline edits are present
        TimelineMapper? timelineMapper = null;
        if (timeline is not null)
        {
            timelineMapper = new TimelineMapper(timeline, exportFps);
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
                effectiveSettings, timelineMapper, timeline, outputPath, progress, ct);
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
                timeline,
                progress,
                ct);
        }

        return outputPath;
    }

    /// <summary>
    /// Exports an animated GIF. Frames come from the same segment-aware composer the
    /// MP4 exporter uses, so the GIF honours segment order, source selection, trims,
    /// speed, text slides, slide crossfades, and appended recordings identically.
    /// </summary>
    private async Task ExportGifAsync(
        Project project,
        MouseRecordingData mouseData,
        CompositionConfig composition,
        ExportSettings settings,
        TimelineMapper? timelineMapper,
        TimelineModel? timeline,
        string outputPath,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        using var composer = await SegmentFrameComposer.CreateAsync(
            project, mouseData, composition, timeline, timelineMapper, settings.Fps, ct);

        int totalFrames = timelineMapper?.TotalOutputFrames ?? composer.TotalFrames;

        // Honour the selected resolution exactly as the MP4 path does. Without this the
        // GIF was written at the compositor's native size, so a 2K/4K recording produced
        // a multi-gigabyte file no matter which resolution the user picked.
        var (gifWidth, gifHeight) = AspectRatioHelper.ComputeExportDimensions(
            composer.OutputWidth, composer.OutputHeight, settings.Resolution);

        var gifEncoder = new GifEncoder();
        await gifEncoder.EncodeComposedFramesAsync(
            frameIndex => ComposeGifFrameAsync(composer, frameIndex, gifWidth, gifHeight, ct),
            totalFrames,
            settings.Fps,
            outputPath,
            progress,
            ct);
    }

    /// <summary>
    /// Composes one output frame and downscales it to the GIF's target size when the
    /// compositor's native size differs. The composed frame is always disposed here;
    /// ownership of the returned frame passes to <see cref="GifEncoder"/>.
    /// </summary>
    private static async Task<CanvasRenderTarget> ComposeGifFrameAsync(
        SegmentFrameComposer composer,
        int frameIndex,
        int targetWidth,
        int targetHeight,
        CancellationToken ct)
    {
        var composed = await composer.ComposeFrameAsync(frameIndex, ct);

        if (composed.SizeInPixels.Width == targetWidth &&
            composed.SizeInPixels.Height == targetHeight)
        {
            return composed;
        }

        // Tracked in a local so a throw mid-draw (device loss is a real possibility here)
        // disposes the partially built target instead of leaking it, mirroring how
        // VideoEncoder hands off its output surface.
        CanvasRenderTarget? scaled = null;
        try
        {
            scaled = Win2DUtils.CreateRenderTarget(
                composed.Device, targetWidth, targetHeight, 96, "scaled gif frame");

            using (var ds = scaled.CreateDrawingSession())
            {
                ds.DrawImage(
                    composed,
                    new Rect(0, 0, targetWidth, targetHeight),
                    new Rect(0, 0, composed.SizeInPixels.Width, composed.SizeInPixels.Height));
            }

            var result = scaled;
            scaled = null; // ownership passes to the caller
            return result;
        }
        finally
        {
            scaled?.Dispose();
            composed.Dispose();
        }
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
        var webcamStyle = composition.WebcamStyle;

        // Auto-enable webcam overlay if the project has a webcam recording
        if (webcamStyle is null &&
            !string.IsNullOrWhiteSpace(project.WebcamFilePath) &&
            File.Exists(project.WebcamFilePath))
        {
            webcamStyle = new WebcamOverlayStyle();
        }

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
        if (keyboardEvents != composition.KeyboardEvents ||
            subtitles != composition.Subtitles ||
            webcamStyle != composition.WebcamStyle)
        {
            return composition with
            {
                KeyboardEvents = keyboardEvents,
                Subtitles = subtitles,
                WebcamStyle = webcamStyle,
            };
        }

        return composition;
    }

    /// <summary>
    /// Applies independent-camera-track state to the compositor for a given source
    /// time: gates webcam visibility, applies the active segment's style override, and
    /// sets the fullscreen-animation factor. Returns <c>true</c> when the webcam overlay
    /// should be shown for this frame. When the timeline has no camera segments the
    /// legacy always-on overlay behaviour is preserved (factor reset to 0).
    /// </summary>
    public static bool ApplyCameraSegmentState(
        FrameCompositor compositor, TimelineModel? timeline,
        WebcamOverlayStyle? baseStyle, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        if (timeline is null || timeline.CameraSegments.Count == 0)
        {
            compositor.SetWebcamFullscreenFactor(0f);
            compositor.SetWebcamOverlayOpacity(1f);
            return true;
        }

        var active = timeline.GetCameraSegmentAtSourceTime(sourceTime);
        if (active is null)
            return false;

        compositor.UpdateWebcamStyle(active.ResolveStyle(baseStyle));
        compositor.SetWebcamFullscreenFactor(active.ComputeFullscreenFactor(sourceTime));
        compositor.SetWebcamOverlayOpacity(timeline.GetCameraOverlayOpacity(active, sourceTime));
        return true;
    }

    /// <summary>
    /// Whether any enabled camera segment uses the fullscreen animation, meaning the
    /// webcam should be extracted at higher resolution so it stays sharp when enlarged.
    /// </summary>
    public static bool TimelineHasFullscreenCamera(TimelineModel? timeline)
        => timeline is not null
        && timeline.CameraSegments.Any(c => c.Enabled && c.FullscreenEnabled);
}