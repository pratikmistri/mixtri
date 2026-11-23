using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Export;

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
        await SetLoopForeverAsync(encoder);

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
    /// Writes the NETSCAPE2.0 application extension (loop count 0 = forever) so the GIF
    /// repeats instead of stopping after one pass. Must run before the first frame. Failure
    /// is non-fatal: the GIF is still valid, it just plays once.
    /// </summary>
    private static async Task SetLoopForeverAsync(BitmapEncoder encoder)
    {
        try
        {
            var properties = new BitmapPropertySet
            {
                {
                    "/appext/Application",
                    new BitmapTypedValue(
                        System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array)
                },
                {
                    // Sub-block: length 3, id 1, loop count 0 (little-endian) = infinite.
                    "/appext/Data",
                    new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array)
                },
            };
            await encoder.BitmapContainerProperties.SetPropertiesAsync(properties);
        }
        catch (Exception ex)
        {
            DiagLog.Write("Export", $"GIF loop extension could not be set; the GIF will play once: {ex.Message}");
        }
    }

    /// <summary>
    /// Encodes an animated GIF from frames composed on demand, one at a time.
    /// The composer produces each output frame (segments, text slides, transitions and
    /// all), and this method takes ownership of — and disposes — every frame it receives.
    /// </summary>
    public async Task EncodeComposedFramesAsync(
        Func<int, Task<CanvasRenderTarget>> composeFrame,
        int totalFrames,
        int fps,
        string outputPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(composeFrame);
        if (totalFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames));
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var stopwatch = Stopwatch.StartNew();

        var dir = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(dir);

        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(
            Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        bool succeeded = false;
        try
        {
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
                await SetLoopForeverAsync(encoder);

                // One CPU staging buffer for the whole export: every frame has the same
                // size, and SetPixelData copies the pixels into the encoder.
                byte[]? pixelBytes = null;

                for (int i = 0; i < totalFrames; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var composedFrame = await composeFrame(i);

                    uint width = (uint)composedFrame.SizeInPixels.Width;
                    uint height = (uint)composedFrame.SizeInPixels.Height;
                    int byteCount = checked((int)(width * height * 4));
                    if (pixelBytes is null || pixelBytes.Length != byteCount)
                        pixelBytes = new byte[byteCount];
                    composedFrame.GetPixelBytes(pixelBytes.AsBuffer());

                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        width,
                        height,
                        96, 96,
                        pixelBytes);

                    var properties = new BitmapPropertySet
                    {
                        {
                            "/grctlext/Delay",
                            new BitmapTypedValue(GetFrameDelayCentiseconds(i, fps), PropertyType.UInt16)
                        }
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
            succeeded = true;
        }
        finally
        {
            // Never leave a truncated GIF behind on cancellation or failure.
            if (!succeeded)
            {
                try { File.Delete(outputPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[GifEncoder] Could not delete partial output: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// GIF frame delay (centiseconds) for <paramref name="frameIndex"/> at
    /// <paramref name="fps"/>. GIF delays are whole centiseconds, so a fixed
    /// <c>100 / fps</c> truncates (30 fps → 3 cs → plays at 33.3 fps and drifts). The
    /// delay is instead the difference of the rounded-down cumulative presentation times,
    /// which carries the rounding error forward: 30 fps yields 3, 3, 4, 3, 3, 4, … and
    /// every <paramref name="fps"/> frames sum to exactly 100 cs.
    /// </summary>
    public static ushort GetFrameDelayCentiseconds(int frameIndex, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        long start = (long)frameIndex * 100 / fps;
        long end = ((long)frameIndex + 1) * 100 / fps;
        return (ushort)Math.Clamp(end - start, 1, ushort.MaxValue);
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
