using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class FrameReaderOwnershipTests
{
    [TestMethod]
    public async Task OversizedFrame_IsReturnedAliveInsteadOfEvictedBeforeCopying()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var reader = new VideoFrameReader(new TestSource(device), 10, cacheBudgetBytes: 1);
        using var frame = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(frame);
        Assert.AreEqual((byte)180, frame.GetPixelColors()[0].R);
    }

    [TestMethod]
    public async Task CachedCopies_StayOnTheSourceDevice()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var reader = new VideoFrameReader(new TestSource(device), 10);
        using var first = await reader.LoadFrameAsync(0);
        using var cached = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(first);
        Assert.IsNotNull(cached);
        Assert.IsTrue(first.Device == device);
        Assert.IsTrue(cached.Device == device);
        Assert.AreNotSame(first, cached);
    }

    [TestMethod]
    public async Task UncachedExportReader_DoesNotOwnReturnedFrames()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var source = new TestSource(device);
        var reader = new VideoFrameReader(source, 10, cacheFrames: false);
        using var frame = await reader.LoadFrameAsync(0);
        Assert.AreSame(source.LastDecoded, frame);
        reader.Dispose();
        Assert.IsNotNull(frame);
        Assert.AreEqual((byte)180, frame.GetPixelColors()[0].R);
    }

    private sealed class TestSource(CanvasDevice device) : IFrameSource
    {
        public int Width => 64;
        public int Height => 48;
        public int FrameCount => 1;
        public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;
        public CanvasBitmap? LastDecoded { get; private set; }
        public Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
        {
            LastDecoded = CanvasBitmapTestHelpers.MakeBitmap(
                device, Color.FromArgb(255, 180, 60, 20), Width, Height);
            return Task.FromResult<CanvasBitmap?>(LastDecoded);
        }
        public void Dispose() { }
    }
}
