using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Export;
using Mixtri.Tests.TestSupport;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class GifEncoderBufferTests
{
    [TestMethod]
    public void ReadbackReusesOneArrayAndMatchesFreshPixels()
    {
        using var pixels = new GifFramePixels();
        using var first = CreateFrame(1);
        using var second = CreateFrame(2);
        var buffer = pixels.Read(first);
        CollectionAssert.AreEqual(first.GetPixelBytes(), buffer);
        Assert.AreSame(buffer, pixels.Read(second));
        CollectionAssert.AreEqual(second.GetPixelBytes(), buffer);
        Assert.AreEqual(1, pixels.AllocationCount);
        Assert.AreEqual(64 * 48 * 4L, pixels.AllocatedPixelBytes);
        pixels.Dispose();
        Assert.AreEqual(0, pixels.CachedPixelBytes);
        Assert.ThrowsException<ObjectDisposedException>(() => pixels.Read(first));
        Assert.AreEqual(64 * 48 * 4, first.GetPixelBytes().Length);
    }

    [TestMethod]
    public void SizeAndFormatChangesUseCorrectlySizedArrays()
    {
        using var pixels = new GifFramePixels();
        using var small = CreateFrame(1, 32, 24);
        using var large = CreateFrame(2, 96, 64);
        using var floatFrame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 32, 24, 96,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        using (var ds = floatFrame.CreateDrawingSession()) ds.Clear(Color.FromArgb(170, 50, 100, 150));
        foreach (var frame in new[] { small, large, floatFrame, small })
        {
            var current = pixels.Read(frame);
            CollectionAssert.AreEqual(frame.GetPixelBytes(), current);
            Assert.AreSame(current, pixels.Read(frame));
        }
        Assert.AreEqual(4, pixels.AllocationCount);
        Assert.AreEqual(32 * 24 * 4, pixels.CachedPixelBytes);
    }

    [TestMethod]
    public void WarmReadbacksAvoidFullFrameManagedAllocations()
    {
        using var frame = CreateFrame(3, 480, 270);
        using var pixels = new GifFramePixels();
        for (int i = 0; i < 10; i++) pixels.Read(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long currentLength = 0;
        for (int i = 0; i < 60; i++) currentLength += pixels.Read(frame).Length;
        long currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        long previousLength = 0;
        for (int i = 0; i < 60; i++) previousLength += frame.GetPixelBytes().Length;
        long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(previousLength, currentLength);
        Assert.IsTrue(previousBytes >= 60L * 480 * 270 * 4);
        Assert.IsTrue(currentBytes < previousBytes / 100,
            $"Expected over 99% less managed allocation, got {currentBytes} vs {previousBytes} bytes.");
        Assert.AreEqual(1, pixels.AllocationCount);
        Console.WriteLine($"60 readbacks at 480x270: {previousBytes:N0} previous bytes, {currentBytes:N0} reused-buffer bytes.");
    }

    [TestMethod]
    [DataRow(false, 30)]
    [DataRow(true, 30)]
    [DataRow(false, 12)]
    [DataRow(true, 12)]
    public async Task BothEncoderPathsPreserveCompleteGifBytesAndFrameDelays(bool composed, int fps)
    {
        using var directory = new TempDirectoryFixture("mixtri_gif_buffer_");
        string expectedPath = Path.Combine(directory.Path, "previous.gif");
        string actualPath = Path.Combine(directory.Path, "current.gif");
        var source = Enumerable.Range(0, 5).Select(i => CreateFrame(i)).ToList();
        var produced = new List<CanvasRenderTarget>();
        var progress = new List<ExportProgress>();
        try
        {
            await EncodePreviousAsync(source, 1000 / fps, expectedPath);
            var encoder = new GifEncoder();
            if (composed)
            {
                await encoder.EncodeComposedFramesAsync(index =>
                {
                    var frame = CreateFrame(index);
                    produced.Add(frame);
                    return Task.FromResult(frame);
                }, source.Count, fps, actualPath, new ImmediateProgress(progress.Add));
                Assert.AreEqual(source.Count, produced.Count);
                foreach (var frame in produced) AssertClosed(() => _ = frame.SizeInPixels);
            }
            else
            {
                await encoder.EncodeAsync(source, 1000 / fps, actualPath, new ImmediateProgress(progress.Add));
                foreach (var frame in source) Assert.AreEqual(64 * 48 * 4, frame.GetPixelBytes().Length);
            }
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(expectedPath), await File.ReadAllBytesAsync(actualPath));
            Assert.AreEqual(source.Count, progress.Count);
            Assert.AreEqual(100d, progress[^1].PercentComplete);
            await CheckDecodedFramesAsync(actualPath, source.Count, (ushort)Math.Max(1, (1000 / fps) / 10));
        }
        finally
        {
            foreach (var frame in source) frame.Dispose();
            foreach (var frame in produced) frame.Dispose();
        }
    }

    [TestMethod]
    public async Task CancellationReleasesComposedFramesAndStopsBeforeTheNextOne()
    {
        using var directory = new TempDirectoryFixture("mixtri_gif_cancel_");
        using var cancellation = new CancellationTokenSource();
        var produced = new List<CanvasRenderTarget>();
        try
        {
            try
            {
                await new GifEncoder().EncodeComposedFramesAsync(index =>
                {
                    var frame = CreateFrame(index);
                    produced.Add(frame);
                    return Task.FromResult(frame);
                }, 4, 20, Path.Combine(directory.Path, "cancelled.gif"),
                    new ImmediateProgress(_ => cancellation.Cancel()), cancellation.Token);
                Assert.Fail("Export ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.AreEqual(1, produced.Count);
            AssertClosed(() => _ = produced[0].SizeInPixels);
        }
        finally { foreach (var frame in produced) frame.Dispose(); }
    }

    [TestMethod]
    public async Task ComposeFailurePreservesTheErrorAndReleasesEarlierFrames()
    {
        using var directory = new TempDirectoryFixture("mixtri_gif_failure_");
        CanvasRenderTarget? produced = null;
        try
        {
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                new GifEncoder().EncodeComposedFramesAsync(index =>
                {
                    if (index > 0) throw new InvalidOperationException("compose fixture failed");
                    produced = CreateFrame(index);
                    return Task.FromResult(produced);
                }, 3, 20, Path.Combine(directory.Path, "failed.gif")));
            Assert.AreEqual("compose fixture failed", error.Message);
            Assert.IsNotNull(produced);
            AssertClosed(() => _ = produced.SizeInPixels);
        }
        finally { produced?.Dispose(); }
    }

    [TestMethod]
    public async Task ConcurrentExportsDoNotSharePixelBuffers()
    {
        using var directory = new TempDirectoryFixture("mixtri_gif_concurrent_");
        var encoder = new GifEncoder();
        string first = Path.Combine(directory.Path, "first.gif");
        string second = Path.Combine(directory.Path, "second.gif");
        await Task.WhenAll(
            encoder.EncodeComposedFramesAsync(index => Task.FromResult(CreateFrame(index)), 4, 20, first),
            encoder.EncodeComposedFramesAsync(index => Task.FromResult(CreateFrame(index + 10)), 4, 20, second));
        foreach (var (seed, output) in new[] { (0, first), (10, second) })
        {
            var frames = Enumerable.Range(seed, 4).Select(index => CreateFrame(index)).ToList();
            try
            {
                string expected = Path.Combine(directory.Path, $"previous-{seed}.gif");
                await EncodePreviousAsync(frames, 50, expected);
                CollectionAssert.AreEqual(await File.ReadAllBytesAsync(expected), await File.ReadAllBytesAsync(output));
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }

    private static CanvasRenderTarget CreateFrame(int seed, int width = 64, int height = 48)
    {
        var frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            int alpha = x % 7 == 0 ? 80 : 255;
            pixels[offset] = (byte)(((x * 17 + y * 3 + seed * 71) & 255) * alpha / 255);
            pixels[offset + 1] = (byte)(((x * 7 + y * 19 + seed * 47) & 255) * alpha / 255);
            pixels[offset + 2] = (byte)(((x * 11 + y * 5 + seed * 23) & 255) * alpha / 255);
            pixels[offset + 3] = (byte)alpha;
        }
        frame.SetPixelBytes(pixels);
        return frame;
    }

    private static async Task EncodePreviousAsync(List<CanvasRenderTarget> frames, int delayMs, string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var data = frame.GetPixelBytes();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)frame.SizeInPixels.Width, (uint)frame.SizeInPixels.Height, 96, 96, data);
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "/grctlext/Delay", new BitmapTypedValue((ushort)Math.Max(1, delayMs / 10), PropertyType.UInt16) },
            });
            if (i < frames.Count - 1) await encoder.GoToNextFrameAsync();
        }
        await encoder.FlushAsync();
    }

    private static async Task CheckDecodedFramesAsync(string path, int count, ushort delay)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.AreEqual((uint)count, decoder.FrameCount);
        byte[]? previous = null;
        for (uint index = 0; index < decoder.FrameCount; index++)
        {
            var frame = await decoder.GetFrameAsync(index);
            Assert.AreEqual(64u, frame.PixelWidth);
            Assert.AreEqual(48u, frame.PixelHeight);
            var properties = await frame.BitmapProperties.GetPropertiesAsync(["/grctlext/Delay"]);
            Assert.AreEqual(delay, Convert.ToUInt16(properties["/grctlext/Delay"].Value));
            var pixels = (await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)).DetachPixelData();
            if (previous is not null) Assert.IsFalse(previous.SequenceEqual(pixels), "A later refill replaced another frame.");
            previous = pixels;
        }
    }

    private static void AssertClosed(Action read)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return; }
        Assert.Fail("The owned frame was not closed.");
    }

    private sealed class ImmediateProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
