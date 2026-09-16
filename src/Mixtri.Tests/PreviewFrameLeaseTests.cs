using Microsoft.Graphics.Canvas;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class PreviewFrameLeaseTests
{
    private sealed class PatternSource(int width = 128, int height = 96, bool translucent = false) : IFrameSource
    {
        public int FrameCount => 120;
        public int Width => width;
        public int Height => height;
        public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;
        public int Loads { get; private set; }

        public Task<CanvasBitmap?> LoadFrameAsync(int index)
        {
            Loads++;
            var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), Width, Height, 96);
            using (var drawing = bitmap.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(translucent ? (byte)160 : (byte)255, (byte)(30 + index), 90, 150));
                drawing.FillRectangle(7, 9, 29, 21, Color.FromArgb(255, 210, (byte)(20 + index), 40));
            }
            return Task.FromResult<CanvasBitmap?>(bitmap);
        }

        public void Dispose() { }
    }

    [TestMethod]
    [DataRow(ZoomScope.Frame, false)]
    [DataRow(ZoomScope.Source, false)]
    [DataRow(ZoomScope.Frame, true)]
    [DataRow(ZoomScope.Source, true)]
    public async Task LeasedPreview_MatchesCopiedPreview_WithoutMutatingSource(ZoomScope zoomScope, bool translucent)
    {
        var copiedSource = new PatternSource(translucent: translucent);
        var leasedSource = new PatternSource(translucent: translucent);
        using var copiedReader = new VideoFrameReader(copiedSource, 30, cacheCapacity: 2);
        using var leasedReader = new VideoFrameReader(leasedSource, 30, cacheCapacity: 2);
        using var copiedRenderer = new PreviewRenderer();
        using var leasedRenderer = new PreviewRenderer();
        var mouse = TestMouseRecordingBuilder.WithPositions(100, 100, i => (20 + i % 40, 25 + i % 20));
        var config = new CompositionConfig
        {
            OutputFps = 30,
            ZoomScope = zoomScope,
            Background = new BackgroundStyle { Type = BackgroundType.Gradient, Padding = 12, CornerRadius = 8 },
        };
        await copiedRenderer.InitializeAsync(mouse, config, 128, 96, TimeSpan.FromSeconds(2));
        await leasedRenderer.InitializeAsync(mouse, config, 128, 96, TimeSpan.FromSeconds(2));
        ZoomKeyframe[] zooms =
        [
            new() { Timestamp = TimeSpan.FromSeconds(.3), ZoomLevel = 1.8, CenterX = .5, CenterY = .5, IsManual = true },
        ];
        copiedRenderer.UpdateZoomKeyframes(zooms);
        leasedRenderer.UpdateZoomKeyframes(zooms);
        int[] indices = [0, 0, 1, 2, 1, 0, 15, 15];
        foreach (int index in indices)
        {
            using var copied = await copiedReader.LoadFrameAsync(index);
            using var leased = await leasedReader.AcquireFrameAsync(index);
            Assert.IsNotNull(copied);
            Assert.IsNotNull(leased);
            var original = leased.Bitmap.GetPixelBytes();
            using var expected = copiedRenderer.RenderPreviewFrame(copied, TimeSpan.FromSeconds(index / 30.0));
            using var actual = leasedRenderer.RenderPreviewFrame(leased.Bitmap, TimeSpan.FromSeconds(index / 30.0));
            Assert.IsNotNull(expected);
            Assert.IsNotNull(actual);
            Assert.IsFalse(actual == leased.Bitmap, "The preview must own an independent output surface.");
            CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(), $"Frame {index} differs.");
            CollectionAssert.AreEqual(original, leased.Bitmap.GetPixelBytes(), "Composition mutated cached source pixels.");
        }
        Assert.AreEqual(copiedSource.Loads, leasedSource.Loads, "Leasing must not change decoder requests.");
        Assert.AreEqual(indices.Length * 128L * 96 * 4, copiedReader.ClonedPixelBytes);
        Assert.AreEqual(0L, leasedReader.ClonedPixelBytes);
    }

    [TestMethod]
    public async Task CachedFullHdPreview_RemovesOneFullSurfaceCopyPerRequest()
    {
        using var reader = new VideoFrameReader(new PatternSource(1920, 1080), 30);
        const int frames = 20;
        for (int index = 0; index < frames; index++)
        {
            using var lease = await reader.AcquireFrameAsync(0);
            Assert.IsNotNull(lease);
            Assert.AreEqual(1920u, lease.Bitmap.SizeInPixels.Width);
            Assert.AreEqual(1080u, lease.Bitmap.SizeInPixels.Height);
        }
        Assert.AreEqual(0L, reader.ClonedPixelBytes);
        for (int index = 0; index < frames; index++)
        {
            using var copy = await reader.LoadFrameAsync(0);
            Assert.IsNotNull(copy);
        }
        Assert.AreEqual(frames * 1920L * 1080 * 4, reader.ClonedPixelBytes);
    }

    [TestMethod]
    public async Task Lease_RemainsDrawableAcrossAnAwaitAndReaderTeardown()
    {
        using var reader = new VideoFrameReader(new PatternSource(), 30, cacheCapacity: 1);
        using var lease = await reader.AcquireFrameAsync(0);
        Assert.IsNotNull(lease);
        var pixels = lease.Bitmap.GetPixelBytes();
        await Task.Yield();
        using (var eviction = await reader.AcquireFrameAsync(1)) Assert.IsNotNull(eviction);
        reader.Dispose();
        using var output = new CanvasRenderTarget(lease.Bitmap.Device, 128, 96, 96);
        using (var drawing = output.CreateDrawingSession()) drawing.DrawImage(lease.Bitmap);
        CollectionAssert.AreEqual(pixels, output.GetPixelBytes());
    }
}
