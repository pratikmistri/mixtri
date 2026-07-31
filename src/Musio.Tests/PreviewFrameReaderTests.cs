using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Processing;
using Windows.UI;

namespace Musio.Tests;

[TestClass]
public class PreviewFrameReaderTests
{
    [DataTestMethod]
    [DataRow(1920, 1080, 960, 540)]
    [DataRow(3840, 2160, 960, 540)]
    [DataRow(1080, 1920, 304, 540)]
    [DataRow(640, 360, 640, 360)]
    public void ComputeOutputDimensions_BoundsAndPreservesAspect(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var actual = Mp4FrameSource.ComputeOutputDimensions(
            sourceWidth, sourceHeight, 960, 540);

        Assert.AreEqual(expectedWidth, actual.Width);
        Assert.AreEqual(expectedHeight, actual.Height);
    }

    [TestMethod]
    public void PreviewOptions_BoundSeekRecovery()
    {
        var options = FrameSourceOptions.CreatePreview(1920, 1080);

        Assert.AreEqual(1920, options.MaxWidth);
        Assert.AreEqual(1080, options.MaxHeight);
        Assert.AreEqual(1, options.SeekAttempts);
        Assert.IsFalse(options.EnableSeekRecovery);
    }

    [TestMethod]
    public void EstimateBytes_UsesBgraSurfaceSize()
    {
        using var bitmap = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(), 960, 540, 96);

        Assert.AreEqual(960L * 540 * 4, VideoFrameReader.EstimateBytes(bitmap));
    }

    [TestMethod]
    public async Task PreviewSource_DecodesIntoReducedSurface()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "musio_preview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            const int fps = 10;
            string videoPath = Path.Combine(root, "video.mp4");
            var device = CanvasDevice.GetSharedDevice();

            using (var writer = new VideoWriter(
                       videoPath, 1280, 720, fps, captureDevice: null))
            {
                for (int i = 0; i < 4; i++)
                {
                    using var frame = new CanvasRenderTarget(device, 1280, 720, 96);
                    using (var ds = frame.CreateDrawingSession())
                        ds.Clear(Color.FromArgb(255, (byte)(40 + i * 20), 80, 120));

                    writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)fps));
                }

                writer.StopAcceptingFrames();
                await writer.WaitForQuiescenceAsync(
                    TimeSpan.FromSeconds(30), CancellationToken.None);
                await writer.FinalizeAsync();
                Assert.IsTrue(writer.FinalizeSucceeded);
            }

            var reducedSurfaceOptions = new FrameSourceOptions
            {
                MaxWidth = 960,
                MaxHeight = 540,
            };

            using var source = await Mp4FrameSource.OpenAsync(
                videoPath,
                fps,
                device,
                RecordingMarker.NeedsVerticalFlip(videoPath),
                reducedSurfaceOptions);

            Assert.IsNotNull(source);
            Assert.AreEqual(960, source.Width);
            Assert.AreEqual(540, source.Height);

            using var decoded = await source.LoadFrameAsync(0);
            Assert.IsNotNull(decoded);
            Assert.AreEqual(960u, decoded.SizeInPixels.Width);
            Assert.AreEqual(540u, decoded.SizeInPixels.Height);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [DataTestMethod]
    [DataRow(3840, 2160, 16, 1920, 1080)]
    [DataRow(3840, 2160, 6, 1600, 900)]
    [DataRow(3840, 2160, 4, 1280, 720)]
    [DataRow(1280, 720, 16, 1280, 720)]
    public void AdaptiveQuality_SelectsMachineAndSourceBoundedInitialTier(
        int sourceWidth,
        int sourceHeight,
        int processorCount,
        int expectedWidth,
        int expectedHeight)
    {
        var resolution = AdaptivePreviewQuality.SelectInitial(
            sourceWidth, sourceHeight, processorCount);

        Assert.AreEqual(expectedWidth, resolution.MaxWidth);
        Assert.AreEqual(expectedHeight, resolution.MaxHeight);
    }

    [TestMethod]
    public void AdaptiveQuality_DowngradesQuicklyAndUpgradesWithSustainedHeadroom()
    {
        var quality = new AdaptivePreviewQuality(3840, 2160, 16);

        PreviewResolution? changed = null;
        for (int i = 0; i < 20 && changed is null; i++)
            changed = quality.ObservePlaybackFrame(TimeSpan.FromMilliseconds(50), 30);

        Assert.AreEqual(new PreviewResolution(1600, 900), changed);
        Assert.AreEqual(new PreviewResolution(1920, 1080), quality.Current);
        Assert.IsTrue(changed.HasValue);
        quality.Commit(changed.GetValueOrDefault());
        Assert.AreEqual(new PreviewResolution(1600, 900), quality.Current);

        changed = null;
        for (int i = 0; i < 300 && changed is null; i++)
            changed = quality.ObservePlaybackFrame(TimeSpan.FromMilliseconds(5), 30);

        Assert.AreEqual(new PreviewResolution(1920, 1080), changed);
        Assert.IsTrue(changed.HasValue);
        quality.Commit(changed.GetValueOrDefault());
        Assert.AreEqual(new PreviewResolution(1920, 1080), quality.Current);
    }

    [TestMethod]
    public void AdaptiveQuality_RejectedChangeDoesNotAdvanceTier()
    {
        var quality = new AdaptivePreviewQuality(3840, 2160, 16);

        PreviewResolution? proposed = null;
        for (int i = 0; i < 20 && proposed is null; i++)
            proposed = quality.ObservePlaybackFrame(TimeSpan.FromMilliseconds(50), 30);

        Assert.AreEqual(new PreviewResolution(1600, 900), proposed);
        quality.RejectChange();
        Assert.AreEqual(new PreviewResolution(1920, 1080), quality.Current);
    }
}
