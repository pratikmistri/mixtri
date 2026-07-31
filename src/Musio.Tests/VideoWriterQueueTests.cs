using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Guards for the recording writer's bounded producer/consumer queue.
/// </summary>
/// <remarks>
/// Frames arrive on a free-threaded frame-pool callback that must never block on JPEG
/// encoding or disk. The queue is what makes that possible, so its contract is load-bearing:
/// accepted frames always reach disk before quiescence returns, refused frames are replayed
/// as CFR duplicates so the recording keeps wall-clock length, and a write that fails is
/// retained and reported instead of surfacing later as a missing JPEG.
/// </remarks>
[TestClass]
public class VideoWriterQueueTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;

    // Microsoft.UI.Colors needs WinUI activation, which the test host does not have.
    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    private static readonly TimeSpan Quiescence = TimeSpan.FromSeconds(60);

    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_queue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private VideoWriter CreateWriter(string folder = "", int width = Width, int height = Height)
    {
        var dir = string.IsNullOrEmpty(folder) ? _root : Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        return new VideoWriter(Path.Combine(dir, "video.mp4"), width, height, Fps, captureDevice: null);
    }

    private static CanvasRenderTarget CreateFrame(int width = Width, int height = Height)
    {
        var device = CanvasDevice.GetSharedDevice();
        var target = new CanvasRenderTarget(device, width, height, 96);
        using (var ds = target.CreateDrawingSession())
            ds.Clear(Grey);
        return target;
    }

    private static int CountFrameFiles(VideoWriter writer)
        => Directory.EnumerateFiles(writer.FramesDirectory, "frame_*.jpg").Count();

    [TestMethod]
    public void QueueCapacity_IsBoundedByFrameSize()
    {
        int hd = VideoWriter.ComputeQueueCapacity(1920, 1080);
        int uhd = VideoWriter.ComputeQueueCapacity(3840, 2160);
        int absurd = VideoWriter.ComputeQueueCapacity(15360, 8640);

        Assert.AreEqual(8, hd, "1080p frames are small enough for the full queue depth");
        Assert.IsTrue(uhd < hd, "a 4K frame must buy fewer queue slots than a 1080p one");
        Assert.IsTrue(uhd >= 2, "the queue must always absorb at least a little jitter");
        Assert.AreEqual(2, absurd, "even an absurd frame size keeps the minimum depth");
    }

    [TestMethod]
    public void QueueOptions_AllowBoundedShutdownToDrainConcurrently()
    {
        var options = VideoWriter.CreateQueueOptions(queueCapacity: 4);

        Assert.IsFalse(options.SingleReader,
            "abort and dispose may drain pending frames while the writer loop is still stopping");
        Assert.IsFalse(options.SingleWriter,
            "capture callbacks may enqueue frames concurrently");
        Assert.AreEqual(5, options.Capacity,
            "the extra slot is reserved for the final gap-only marker");
    }

    [TestMethod]
    public async Task Quiescence_GuaranteesEveryCountedFrameIsOnDisk()
    {
        // FrameCount is the finalizer's contract: frame_{i}.jpg must exist for every
        // i < FrameCount, or finalization fails on a missing JPEG.
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        const int Frames = 20;
        for (int i = 0; i < Frames; i++)
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.AreEqual(Frames, writer.FrameCount, "blocking writes must never be dropped");
        Assert.AreEqual(0, writer.DroppedFrames);
        Assert.AreEqual(Frames, CountFrameFiles(writer));

        for (int i = 0; i < Frames; i++)
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(writer.FramesDirectory, $"frame_{i:D8}.jpg")),
                $"frame {i} was counted but is not on disk");
        }
    }

    [TestMethod]
    public async Task SkippedSlots_AreFilledBeforeTheFrameThatReportsThem()
    {
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        writer.WriteFrame(frame, TimeSpan.Zero);
        writer.WriteFrame(frame, TimeSpan.FromSeconds(3 / (double)Fps), skippedSlots: 2);

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.AreEqual(4, writer.FrameCount,
            "two missed slots must be duplicated so frame N still lands at N/fps");
        Assert.AreEqual(4, CountFrameFiles(writer));
    }

    [TestMethod]
    public async Task OwedGapSlots_AreFlushedWhenTheGateCloses()
    {
        // A recording whose tail was dropped still has to end at the right wall-clock time.
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        writer.WriteFrame(frame, TimeSpan.Zero);
        writer.FillGapFrames(3);

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.AreEqual(4, writer.FrameCount, "slots owed at stop must still be written");
        Assert.AreEqual(4, CountFrameFiles(writer));
        Assert.AreEqual(TimeSpan.FromSeconds(4 / (double)Fps), writer.CfrDuration);
    }

    [TestMethod]
    public async Task FramesOfferedAfterTheGateCloses_AreRefusedNotWritten()
    {
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        writer.WriteFrame(frame, TimeSpan.Zero);
        writer.StopAcceptingFrames();

        Assert.IsFalse(writer.TryWriteFrame(frame, TimeSpan.FromSeconds(1)),
            "a frame delivered after the gate closed must be refused");

        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.AreEqual(1, writer.FrameCount);
        Assert.AreEqual(1, CountFrameFiles(writer));
    }

    [TestMethod]
    public async Task SaturatedWriter_DropsFramesButKeepsWallClockLength()
    {
        // 1080p JPEG encoding is far slower than the copy-and-enqueue the callback does,
        // so a tight offer loop is guaranteed to saturate the queue. The callback must
        // keep running (drops, not stalls) and every dropped slot must come back as a
        // duplicate, or the recording would silently play faster than it was captured.
        using var writer = CreateWriter(width: 1920, height: 1080);
        using var frame = CreateFrame(1920, 1080);

        const int Offered = 90;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Offered; i++)
            writer.TryWriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));
        sw.Stop();

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.IsTrue(writer.DroppedFrames > 0,
            "the writer queue should have saturated; otherwise this test proves nothing");
        Assert.IsTrue(writer.QueuedFrames == 0, "quiescence must leave the queue empty");
        Assert.AreEqual(Offered, writer.FrameCount,
            $"every offered slot must be accounted for ({writer.DroppedFrames} dropped)");
        Assert.AreEqual(Offered, CountFrameFiles(writer));
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"offering frames must not block on encoding, but took {sw.Elapsed}");
    }

    [TestMethod]
    public async Task FirstWriteFailure_IsRetainedAndReportedOnce()
    {
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        int reported = 0;
        FrameWriteFailedEventArgs? args = null;
        writer.WriteFailed += (_, e) => { Interlocked.Increment(ref reported); args = e; };

        // Replace the frames directory with a file so every JPEG write fails.
        Directory.Delete(writer.FramesDirectory, recursive: true);
        File.WriteAllText(writer.FramesDirectory, "not a directory");

        for (int i = 0; i < 3; i++)
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);

        Assert.IsTrue(writer.HasWriteFailure, "the writer must remember that frames were lost");
        Assert.IsNotNull(writer.FirstWriteError);
        Assert.AreEqual(0, writer.FirstWriteErrorFrameIndex);
        Assert.AreEqual(3, writer.FailedFrameWrites);
        Assert.AreEqual(0, writer.FrameCount,
            "a frame that never reached disk must not be counted, or finalize breaks on the hole");

        Assert.AreEqual(1, reported, "only the first failure is reported, not one per frame");
        Assert.IsNotNull(args);
        Assert.AreEqual(writer.FramesDirectory, args!.FramesDirectory,
            "the report must say where the surviving frames are");
    }

    [TestMethod]
    public async Task WriteFailure_SurvivesFinalization()
    {
        // Finalization must not be able to conceal a lost frame.
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        Directory.Delete(writer.FramesDirectory, recursive: true);
        File.WriteAllText(writer.FramesDirectory, "not a directory");

        writer.WriteFrame(frame, TimeSpan.Zero);
        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);
        await writer.FinalizeAsync();

        Assert.IsFalse(writer.FinalizeSucceeded, "no frames were written, so there is no MP4");
        Assert.IsNotNull(writer.FirstWriteError, "the original failure must still be available");
    }

    [TestMethod]
    public async Task Finalize_DrainsTheWriterEvenWithoutAnExplicitQuiescence()
    {
        using var writer = CreateWriter();
        using var frame = CreateFrame();

        for (int i = 0; i < 6; i++)
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        // No StopAcceptingFrames/WaitForQuiescenceAsync: finalize has to close the gate
        // and drain the queue itself, or it would encode a moving target.
        await writer.FinalizeAsync();

        Assert.IsTrue(writer.FinalizeSucceeded);
        Assert.AreEqual(6, writer.FrameCount);
    }

    [TestMethod]
    public async Task CroppedCapture_StillWritesOnlyTheCropRegion()
    {
        // Crop moved from a single reusable render target to a per-frame buffer so the
        // writer thread cannot encode a target the capture thread is redrawing. The crop
        // itself must be unchanged.
        const int SourceWidth = 400;
        const int SourceHeight = 300;
        const int CropWidth = 200;

        var device = CanvasDevice.GetSharedDevice();
        var dir = Path.Combine(_root, "crop");
        Directory.CreateDirectory(dir);

        using var source = new CanvasRenderTarget(device, SourceWidth, SourceHeight, 96);
        using (var ds = source.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 220, 20, 20));
            ds.FillRectangle(CropWidth, 0, CropWidth, SourceHeight, Color.FromArgb(255, 20, 20, 220));
        }

        // Right half only.
        var crop = new Windows.Foundation.Rect(CropWidth, 0, CropWidth, SourceHeight);

        using (var writer = new VideoWriter(
            Path.Combine(dir, "video.mp4"), CropWidth, SourceHeight, Fps,
            captureDevice: null, cropRect: crop))
        {
            writer.WriteFrame(source, TimeSpan.Zero);
            writer.StopAcceptingFrames();
            await writer.WaitForQuiescenceAsync(Quiescence, CancellationToken.None);
            Assert.AreEqual(1, writer.FrameCount);
        }

        var frames = Musio.Core.Processing.JpegFrameSource.Open(dir, device);
        Assert.IsNotNull(frames, "the cropped frame should have been captured");

        try
        {
            using var captured = await frames!.LoadFrameAsync(0);
            Assert.IsNotNull(captured);
            Assert.AreEqual(CropWidth, (int)captured!.SizeInPixels.Width);
            Assert.AreEqual(SourceHeight, (int)captured.SizeInPixels.Height);

            var pixels = captured.GetPixelColors();
            var centre = pixels[(SourceHeight / 2) * CropWidth + (CropWidth / 2)];

            Assert.IsTrue(centre.B > 120 && centre.R < 90,
                $"the cropped frame should be the blue half of the source, but was {centre}");
        }
        finally
        {
            frames!.Dispose();
        }
    }

    [TestMethod]
    public void Dispose_StopsTheWriterPromptly()
    {
        var writer = CreateWriter();
        using (var frame = CreateFrame())
        {
            for (int i = 0; i < 4; i++)
                writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));
        }

        var sw = Stopwatch.StartNew();
        writer.Dispose();
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"dispose must be bounded, but took {sw.Elapsed}");
        Assert.AreEqual(0, writer.QueuedFrames, "dispose must release every queued frame");
    }
}
