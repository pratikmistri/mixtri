using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;

namespace Mixtri.Tests;

[TestClass]
public sealed class VideoFrameReaderLifetimeTests
{
    private sealed class DelayedSource(FrameSourceKind kind) : IFrameSource
    {
        public int FrameCount => 10;
        public int Width => 32;
        public int Height => 24;
        public FrameSourceKind Kind => kind;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public int Active;
        public int Disposals;
        public bool DisposedDuringLoad;

        public async Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
        {
            Interlocked.Increment(ref Active);
            Started.TrySetResult();
            try
            {
                await Release.Task;
                if (Fail) throw new IOException("Decode failed.");
                return new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), Width, Height, 96);
            }
            finally { Interlocked.Decrement(ref Active); }
        }

        public void Dispose()
        {
            DisposedDuringLoad |= Volatile.Read(ref Active) != 0;
            Interlocked.Increment(ref Disposals);
        }
    }

    [TestMethod]
    [DataRow(FrameSourceKind.EncodedVideo, false)]
    [DataRow(FrameSourceKind.EncodedVideo, true)]
    [DataRow(FrameSourceKind.CapturedJpeg, false)]
    [DataRow(FrameSourceKind.CapturedJpeg, true)]
    public async Task DisposeDefersSourceTeardownUntilActiveLoadReturns(FrameSourceKind kind, bool legacy)
    {
        var source = new DelayedSource(kind);
        using var reader = new VideoFrameReader(source, 30);
        Task<CanvasBitmap?>? copied = legacy ? reader.LoadFrameAsync(0) : null;
        Task<FrameLease?>? leased = legacy ? null : reader.AcquireFrameAsync(0);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            reader.Dispose();
            reader.Dispose();
            Assert.AreEqual(0, source.Disposals, "The decoder is still in use.");
            Assert.IsNull(await reader.AcquireFrameAsync(1));
            Assert.IsNull(await reader.LoadFrameAsync(1));
        }
        finally { source.Release.TrySetResult(); }
        if (copied is not null)
        {
            using var frame = await copied;
            Assert.IsNotNull(frame);
            Assert.AreEqual(32 * 24 * 4, frame.GetPixelBytes().Length);
        }
        else
        {
            using var frame = await leased!;
            Assert.IsNotNull(frame);
            Assert.AreEqual(32 * 24 * 4, frame.Bitmap.GetPixelBytes().Length);
        }
        Assert.AreEqual(1, source.Disposals);
        Assert.IsFalse(source.DisposedDuringLoad);
    }

    [TestMethod]
    public async Task FailedDecodeStillReleasesItsReadAdmission()
    {
        var source = new DelayedSource(FrameSourceKind.EncodedVideo) { Fail = true };
        using var reader = new VideoFrameReader(source, 30);
        var read = reader.AcquireFrameAsync(0);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reader.Dispose();
        source.Release.TrySetResult();
        await Assert.ThrowsExceptionAsync<IOException>(() => read);
        Assert.AreEqual(1, source.Disposals);
        Assert.IsFalse(source.DisposedDuringLoad);
    }

    [TestMethod]
    public async Task ConcurrentReadsAllFinishBeforeSourceDisposal()
    {
        var source = new DelayedSource(FrameSourceKind.CapturedJpeg);
        using var reader = new VideoFrameReader(source, 30);
        var first = reader.AcquireFrameAsync(0);
        var second = reader.AcquireFrameAsync(1);
        Assert.AreEqual(2, source.Active);
        reader.Dispose();
        Assert.AreEqual(0, source.Disposals);
        source.Release.TrySetResult();
        using var a = await first;
        using var b = await second;
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreEqual(1, source.Disposals);
        Assert.IsFalse(source.DisposedDuringLoad);
    }
}
