using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Tests.TestSupport;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class CaptureFrameDecoderTests
{
    private const int Width = 128;
    private const int Height = 96;

    private static async Task WriteImageAsync(string path, Color top, Color bottom)
    {
        using var image = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), Width, Height, 96);
        using (var drawing = image.CreateDrawingSession())
        {
            drawing.Clear(top);
            drawing.FillRectangle(0, Height / 2, Width, Height / 2, bottom);
        }
        using var stream = File.Create(path);
        await image.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, .95f);
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> PreviousDecodeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = Width, ScaledHeight = Height,
                InterpolationMode = BitmapInterpolationMode.Fant, Flip = BitmapFlip.Vertical,
            },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var buffer = new Windows.Storage.Streams.Buffer(Width * Height * 4);
        bitmap.CopyToBuffer(buffer);
        return Bytes(buffer);
    }

    [TestMethod]
    public async Task LinkedHolds_DecodeOnce_WithIdenticalPixelsAndIndependentOutputBuffers()
    {
        using var directory = new TempDirectoryFixture("mixtri_decode_hold_");
        if (new DriveInfo(Path.GetPathRoot(directory.Path)!).DriveFormat != "NTFS")
            Assert.Inconclusive("Linked-frame decoding coverage requires NTFS.");
        string first = Path.Combine(directory.Path, "first.jpg");
        string held = Path.Combine(directory.Path, "held.jpg");
        await WriteImageAsync(first, Color.FromArgb(255, 230, 20, 10), Color.FromArgb(255, 10, 20, 230));
        new CaptureFrameFiles().Duplicate(first, held);
        var expected = await PreviousDecodeAsync(first);
        using var decoder = new CaptureFrameDecoder(Width, Height, cacheLinkedFrames: true);
        var buffers = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => decoder.ReadAsync(index % 2 == 0 ? first : held, CancellationToken.None)));
        Assert.AreEqual(1L, decoder.DecodeCount);
        Assert.AreEqual(Width * Height * 4L, decoder.MaximumCachedBytes);
        foreach (var buffer in buffers) CollectionAssert.AreEqual(expected, Bytes(buffer));
        for (int index = 1; index < buffers.Length; index++)
            Assert.AreNotSame(buffers[0], buffers[index], "Each encoder sample must own its own buffer.");
        decoder.Dispose();
        CollectionAssert.AreEqual(expected, Bytes(buffers[0]), "Disposing the source cache invalidated an encoder buffer.");
    }

    [TestMethod]
    public async Task ReplacementOrDifferentFrame_DoesNotReuseOldPixels()
    {
        using var directory = new TempDirectoryFixture("mixtri_decode_replace_");
        string source = Path.Combine(directory.Path, "source.jpg");
        string held = Path.Combine(directory.Path, "held.jpg");
        string replacement = Path.Combine(directory.Path, "replacement.jpg");
        await WriteImageAsync(source, Color.FromArgb(255, 220, 10, 20), Color.FromArgb(255, 10, 20, 220));
        new CaptureFrameFiles().Duplicate(source, held);
        using var decoder = new CaptureFrameDecoder(Width, Height, cacheLinkedFrames: true);
        var first = Bytes(await decoder.ReadAsync(source, CancellationToken.None));
        await WriteImageAsync(replacement, Color.FromArgb(255, 20, 220, 10), Color.FromArgb(255, 200, 200, 20));
        CaptureFrameFiles.Publish(replacement, source);
        var changed = Bytes(await decoder.ReadAsync(source, CancellationToken.None));
        CollectionAssert.AreEqual(await PreviousDecodeAsync(source), changed);
        Assert.IsFalse(first.SequenceEqual(changed));
        CollectionAssert.AreEqual(first, Bytes(await decoder.ReadAsync(held, CancellationToken.None)));
    }

    [TestMethod]
    public async Task UnlinkedFramesAndDisabledCache_KeepIndependentDecoding()
    {
        using var directory = new TempDirectoryFixture("mixtri_decode_uncached_");
        string source = Path.Combine(directory.Path, "source.jpg");
        await WriteImageAsync(source, Color.FromArgb(255, 40, 80, 120), Color.FromArgb(255, 120, 80, 40));
        foreach (bool cache in new[] { false, true })
        {
            using var decoder = new CaptureFrameDecoder(Width, Height, cache);
            for (int index = 0; index < 3; index++)
                _ = await decoder.ReadAsync(source, CancellationToken.None);
            Assert.AreEqual(3L, decoder.DecodeCount);
            Assert.AreEqual(0L, decoder.MaximumCachedBytes);
        }
    }

    [TestMethod]
    public async Task MissingFrame_IsNotHiddenByTheCachedIdentity()
    {
        using var directory = new TempDirectoryFixture("mixtri_decode_missing_");
        string source = Path.Combine(directory.Path, "source.jpg");
        string held = Path.Combine(directory.Path, "held.jpg");
        await WriteImageAsync(source, Color.FromArgb(255, 40, 80, 120), Color.FromArgb(255, 120, 80, 40));
        new CaptureFrameFiles().Duplicate(source, held);
        using var decoder = new CaptureFrameDecoder(Width, Height, cacheLinkedFrames: true);
        _ = await decoder.ReadAsync(held, CancellationToken.None);
        File.Delete(held);
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => decoder.ReadAsync(held, CancellationToken.None));
    }
}
