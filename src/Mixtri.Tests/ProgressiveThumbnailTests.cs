using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class ProgressiveThumbnailTests
{
    [TestMethod]
    public void SamplingPlan_BatchesPreserveAllSourceTimes()
    {
        var plan = ThumbnailSamplingPlan.Create(TimeSpan.FromSeconds(90), 300, 0.5);
        Assert.AreEqual(181, plan.Count);
        Assert.AreEqual(4, plan.BatchCountAt(0, true));
        Assert.AreEqual(16, plan.BatchCountAt(4, true));
        Assert.AreEqual(89.999, plan.TimeAt(180).TotalSeconds, 0.00001);
        var indices = new List<int>();
        for (int start = 0; start < plan.Count;)
        {
            int count = plan.BatchCountAt(start, true);
            indices.AddRange(Enumerable.Range(start, count));
            start += count;
        }
        CollectionAssert.AreEqual(Enumerable.Range(0, plan.Count).ToArray(), indices.ToArray());
        Assert.AreEqual(2, ThumbnailSamplingPlan.LoadedSourceEnd(4, 0.5, 90));
        Assert.AreEqual(90, ThumbnailSamplingPlan.LoadedSourceEnd(181, 0.5, 90));
        CollectionAssert.AreEqual(new[] { 0, 60, 120, 180 }, plan.OverviewIndices());
        CollectionAssert.AreEqual(new[] { 2, 6 }, plan.OverviewIndices(
            [TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(3.3), TimeSpan.FromSeconds(3.3)]));
    }

    [TestMethod]
    public void Reveal_IsContinuousAndReachesExactEndpoints()
    {
        Assert.AreEqual(0f, ThumbnailReveal.Progress(-1));
        Assert.AreEqual(0f, ThumbnailReveal.Progress(0));
        Assert.AreEqual(0.5f, ThumbnailReveal.Progress(ThumbnailReveal.DurationSeconds / 2), 0.0001);
        Assert.AreEqual(1f, ThumbnailReveal.Progress(ThumbnailReveal.DurationSeconds));
        Assert.AreEqual(1f, ThumbnailReveal.Progress(10));
    }

    [TestMethod]
    public void RevealShader_BlendsWithoutChangingCompletedPixels()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var previous = new CanvasRenderTarget(device, 32, 32, 96);
        using var next = new CanvasRenderTarget(device, 32, 32, 96);
        using var output = new CanvasRenderTarget(device, 32, 32, 96);
        using (var ds = previous.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 0, 0, 0));
        using (var ds = next.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 200, 100, 50));
        var rect = new Windows.Foundation.Rect(0, 0, 32, 32);
        using (var ds = output.CreateDrawingSession())
            ThumbnailReveal.Draw(ds, next, previous, rect, rect, 0.5f);
        var middle = output.GetPixelColors()[0];
        Assert.AreEqual(100, middle.R, 1);
        Assert.AreEqual(50, middle.G, 1);
        Assert.AreEqual(25, middle.B, 1);
        using (var ds = output.CreateDrawingSession())
            ThumbnailReveal.Draw(ds, next, previous, rect, rect, 1);
        CollectionAssert.AreEqual(next.GetPixelBytes(), output.GetPixelBytes());
    }

    private static async Task<string> WriteJpegsAsync(string root)
    {
        string video = Path.Combine(root, "video.mp4");
        string frames = Path.Combine(root, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(frames);
        using var frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 96, 64, 96);
        for (int i = 0; i < 20; i++)
        {
            using (var ds = frame.CreateDrawingSession())
                ds.Clear(Color.FromArgb(255, (byte)(i * 10), 100, 160));
            await frame.SaveAsync(Path.Combine(frames, $"frame_{i:D8}.jpg"), CanvasBitmapFileFormat.Jpeg);
        }
        return video;
    }

    [TestMethod]
    public async Task JpegBatches_MatchCompleteStrip_AndTransferOwnership()
    {
        using var directory = new TempDirectoryFixture("mixtri_progressive_");
        string video = await WriteJpegsAsync(directory.Path);
        var device = CanvasDevice.GetSharedDevice();
        var expected = await VideoThumbnailExtractor.ExtractFromCapturedFramesAsync(video, 2, 16, device);
        Assert.IsNotNull(expected);
        var actual = new List<CanvasBitmap?>();
        var counts = new List<int>();
        try
        {
            bool success = await VideoThumbnailExtractor.ExtractCapturedFramesProgressivelyAsync(
                video, 2, 16, device, batch =>
                {
                    if (batch.IsOverview)
                    {
                        CollectionAssert.AreEqual(new[] { 0, 7, 13, 20 },
                            Enumerable.Range(0, batch.Count).Select(batch.FrameIndexAt).ToArray());
                        return;
                    }
                    Assert.AreEqual(actual.Count, batch.StartIndex);
                    Assert.AreEqual(expected.Thumbnails.Length, batch.TotalCount);
                    counts.Add(batch.Count);
                    actual.AddRange(batch.TakeFrames());
                });
            Assert.IsTrue(success);
            CollectionAssert.AreEqual(new[] { 4, 16, 1 }, counts);
            Assert.AreEqual(expected.Thumbnails.Length, actual.Count);
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.IsNotNull(actual[i]);
                Assert.IsNotNull(expected.Thumbnails[i]);
                CollectionAssert.AreEqual(expected.Thumbnails[i]!.GetPixelBytes(), actual[i]!.GetPixelBytes(),
                    $"thumbnail {i}: expected {expected.Thumbnails[i]!.GetPixelColors()[0]}, actual {actual[i]!.GetPixelColors()[0]}");
            }
        }
        finally
        {
            foreach (var frame in expected.Thumbnails) frame?.Dispose();
            foreach (var frame in actual) frame?.Dispose();
        }
    }

    [TestMethod]
    public async Task Cancellation_DoesNotDisposeAlreadyPublishedFrames()
    {
        using var directory = new TempDirectoryFixture("mixtri_progressive_cancel_");
        string video = await WriteJpegsAsync(directory.Path);
        using var cts = new CancellationTokenSource();
        CanvasBitmap?[] published = [];
        try
        {
            bool cancelled = false;
            try
            {
                await VideoThumbnailExtractor.ExtractCapturedFramesProgressivelyAsync(
                    video, 2, 16, CanvasDevice.GetSharedDevice(), batch =>
                    {
                        published = batch.TakeFrames();
                        cts.Cancel();
                    }, ct: cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled);
            Assert.AreEqual(4, published.Length);
            Assert.IsNotNull(published[0]);
            Assert.IsTrue(published[0]!.GetPixelBytes().Length > 0);
        }
        finally
        {
            foreach (var frame in published) frame?.Dispose();
        }
    }

    [TestMethod]
    public async Task Mp4_PublishesBeforeCompletion_WithTheSameFinalThumbnails()
    {
        using var directory = new TempDirectoryFixture("mixtri_progressive_mp4_");
        string video = Path.Combine(directory.Path, "video.mp4");
        var device = CanvasDevice.GetSharedDevice();
        using (var writer = new VideoWriter(video, 96, 64, 10))
        using (var frame = new CanvasRenderTarget(device, 96, 64, 96))
        {
            for (int i = 0; i < 24; i++)
            {
                using (var ds = frame.CreateDrawingSession())
                    ds.Clear(Color.FromArgb(255, (byte)(i * 10), 80, 120));
                writer.WriteFrame(frame, TimeSpan.FromSeconds(i / 10.0));
            }
            await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await writer.FinalizeAsync();
        }
        var expected = await VideoThumbnailExtractor.ExtractAsync(video, 32, device);
        Assert.IsNotNull(expected);
        var actual = new List<CanvasBitmap?>();
        bool firstWasPartial = false;
        try
        {
            bool success = await VideoThumbnailExtractor.ExtractProgressivelyAsync(video, 32, device, batch =>
            {
                if (actual.Count == 0) firstWasPartial = !batch.IsComplete;
                if (batch.IsOverview) return;
                actual.AddRange(batch.TakeFrames());
            });
            Assert.IsTrue(success);
            Assert.IsTrue(firstWasPartial);
            Assert.AreEqual(expected.Thumbnails.Length, actual.Count);
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.IsNotNull(actual[i]);
                Assert.IsNotNull(expected.Thumbnails[i]);
                CollectionAssert.AreEqual(expected.Thumbnails[i]!.GetPixelBytes(), actual[i]!.GetPixelBytes(),
                    $"thumbnail {i}: expected {expected.Thumbnails[i]!.GetPixelColors()[0]}, actual {actual[i]!.GetPixelColors()[0]}");
            }
        }
        finally
        {
            foreach (var frame in expected.Thumbnails) frame?.Dispose();
            foreach (var frame in actual) frame?.Dispose();
        }
    }
}
