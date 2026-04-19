using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Processing;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Musio.Core.Export;

/// <summary>
/// Encodes composed frames into an animated GIF using <see cref="BitmapEncoder"/>
/// with the GIF container format and multi-frame support.
/// </summary>
public class GifEncoder
{
    /// <summary>
    /// Encodes a list of pre-rendered frames into an animated GIF file.
    /// </summary>
    public async Task EncodeAsync(
        List<CanvasRenderTarget> frames,
        int frameDelayMs,
        string outputPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (frameDelayMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDelayMs));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var stopwatch = Stopwatch.StartNew();
        int totalFrames = frames.Count;

        // GIF frame delay is in units of 10ms
        ushort delayHundredths = (ushort)Math.Max(1, frameDelayMs / 10);

        var dir = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(dir);

        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(
            Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);

        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();

            var frame = frames[i];
            var pixelBytes = frame.GetPixelBytes();
            uint width = (uint)frame.SizeInPixels.Width;
            uint height = (uint)frame.SizeInPixels.Height;

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                width,
                height,
                96, 96,
                pixelBytes);

            // Set frame delay via properties
            var properties = new BitmapPropertySet
            {
                { "/grctlext/Delay", new BitmapTypedValue(delayHundredths, PropertyType.UInt16) }
            };

            await encoder.BitmapProperties.SetPropertiesAsync(properties);

            if (i < totalFrames - 1)
            {
                await encoder.GoToNextFrameAsync();
            }

            ReportProgress(progress, i + 1, totalFrames, stopwatch);
        }

        await encoder.FlushAsync();
    }

    /// <summary>
    /// Exports directly from a compositor, composing each frame on the fly.
    /// More memory-efficient than pre-rendering all frames.
    /// </summary>
    public async Task ExportGifAsync(
        FrameCompositor compositor,
        Func<int, Task<CanvasBitmap>> getSourceFrame,
        int totalFrames,
        int fps,
        string outputPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(getSourceFrame);
        if (totalFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames));
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var stopwatch = Stopwatch.StartNew();
        int frameDelayMs = 1000 / fps;
        ushort delayHundredths = (ushort)Math.Max(1, frameDelayMs / 10);

        var dir = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(dir);

        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(
            Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);

        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var sourceFrame = await getSourceFrame(i);
            using var composedFrame = compositor.ComposeFrame(sourceFrame, i);

            var pixelBytes = composedFrame.GetPixelBytes();
            uint width = (uint)composedFrame.SizeInPixels.Width;
            uint height = (uint)composedFrame.SizeInPixels.Height;

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                width,
                height,
                96, 96,
                pixelBytes);

            var properties = new BitmapPropertySet
            {
                { "/grctlext/Delay", new BitmapTypedValue(delayHundredths, PropertyType.UInt16) }
            };

            await encoder.BitmapProperties.SetPropertiesAsync(properties);

            if (i < totalFrames - 1)
            {
                await encoder.GoToNextFrameAsync();
            }

            ReportProgress(progress, i + 1, totalFrames, stopwatch);
        }

        await encoder.FlushAsync();
    }

    private static void ReportProgress(
        IProgress<ExportProgress>? progress,
        int completedFrames,
        int totalFrames,
        Stopwatch stopwatch)
    {
        if (progress is null) return;

        double percent = (double)completedFrames / totalFrames * 100.0;
        var elapsed = stopwatch.Elapsed;
        var perFrame = elapsed / completedFrames;
        var remaining = perFrame * (totalFrames - completedFrames);

        progress.Report(new ExportProgress(
            completedFrames, totalFrames, percent, elapsed, remaining));
    }
}
