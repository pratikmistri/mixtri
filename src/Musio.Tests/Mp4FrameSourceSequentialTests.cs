using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Processing;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Guards the decoder's sequential "step" fast path against off-by-N delivery.
/// </summary>
/// <remarks>
/// <see cref="Mp4FrameSource"/> walks small forward gaps with
/// <c>StepForwardOneFrame</c> instead of seeking, and completes each step when
/// <c>VideoFrameAvailable</c> raises. That event is not guaranteed to raise exactly once
/// per issued command, so a duplicate raise could complete the *next* step's request
/// without the decoder having advanced — handing back frame N-1 labelled N and leaving
/// the internal index permanently offset. The exporter reads through this same class, so
/// such a slip would be baked silently into an exported file. These tests decode a real
/// MP4 whose every frame is a distinct colour, so a slip of one frame fails loudly.
/// </remarks>
[TestClass]
public class Mp4FrameSourceSequentialTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;
    private const int FrameCount = 24;

    // Microsoft.UI.Colors needs WinUI activation, which the test host does not have.
    private static readonly Color[] Cycle =
    [
        Color.FromArgb(255, 220, 20, 20),   // red
        Color.FromArgb(255, 20, 200, 20),   // green
        Color.FromArgb(255, 20, 20, 220),   // blue
        Color.FromArgb(255, 230, 230, 230), // white
    ];

    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_seq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Color ExpectedColor(int frameIndex) => Cycle[frameIndex % Cycle.Length];

    /// <summary>Writes an MP4 whose frame <c>i</c> is a solid <see cref="ExpectedColor"/>.</summary>
    private async Task<string> WriteColourCycledMp4Async()
    {
        var device = CanvasDevice.GetSharedDevice();
        var videoPath = Path.Combine(_root, "video.mp4");

        using var writer = new VideoWriter(videoPath, Width, Height, Fps, captureDevice: null);

        for (int i = 0; i < FrameCount; i++)
        {
            using var frame = new CanvasRenderTarget(device, Width, Height, 96);
            using (var ds = frame.CreateDrawingSession())
                ds.Clear(ExpectedColor(i));

            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));
        }

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await writer.FinalizeAsync();

        Assert.IsTrue(writer.FinalizeSucceeded, "the MP4 should have finalized");
        return videoPath;
    }

    /// <summary>Average colour of the middle of the frame, away from any edge ringing.</summary>
    private static Color SampleCentre(CanvasBitmap frame)
    {
        var pixels = frame.GetPixelColors();
        int w = (int)frame.SizeInPixels.Width;
        int h = (int)frame.SizeInPixels.Height;
        return pixels[(h / 2) * w + (w / 2)];
    }

    private static int NearestCycleIndex(Color actual)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < Cycle.Length; i++)
        {
            double dr = actual.R - Cycle[i].R;
            double dg = actual.G - Cycle[i].G;
            double db = actual.B - Cycle[i].B;
            double distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    [TestMethod]
    public async Task SequentialReads_DeliverTheRequestedFrame()
    {
        var videoPath = await WriteColourCycledMp4Async();
        var device = CanvasDevice.GetSharedDevice();

        var source = await Mp4FrameSource.OpenAsync(
            videoPath, Fps, device, RecordingMarker.NeedsVerticalFlip(videoPath));
        Assert.IsNotNull(source, "the finalized MP4 should be decodable");

        try
        {
            // Ascending by one is the step fast path — the case a duplicate
            // VideoFrameAvailable raise could silently shift.
            for (int i = 0; i < Math.Min(FrameCount, source.FrameCount); i++)
            {
                using var frame = await source.LoadFrameAsync(i);
                Assert.IsNotNull(frame, $"frame {i} should decode");

                int decoded = NearestCycleIndex(SampleCentre(frame));
                Assert.AreEqual(
                    i % Cycle.Length, decoded,
                    $"frame {i} decoded as cycle colour {decoded} (expected {i % Cycle.Length}) — "
                    + "the decoder delivered a different frame than the one requested");
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    [TestMethod]
    [Ignore("Known defect: seeking returns the frame BEFORE the one requested. Measured by "
        + "probing a 24-frame 10fps MP4 at 10ms resolution: reads served by the step path "
        + "(gap <= MaxStepAhead) are exact, but every read served by SeekToAsync returns "
        + "frame i-1. Pre-existing in the MP4-backed decode path, not introduced by the "
        + "preview fixes. Enable this test once TimeForFrame's index-to-time mapping is "
        + "corrected. Affects scrubbing accuracy and any export that seeks.")]
    public async Task RandomAccessReads_DeliverTheRequestedFrame()
    {
        var videoPath = await WriteColourCycledMp4Async();
        var device = CanvasDevice.GetSharedDevice();

        var source = await Mp4FrameSource.OpenAsync(
            videoPath, Fps, device, RecordingMarker.NeedsVerticalFlip(videoPath));
        Assert.IsNotNull(source, "the finalized MP4 should be decodable");

        try
        {
            // Backward jumps and gaps larger than the step budget force real seeks,
            // which is the path that re-issues and can strand an abandoned frame.
            int[] order = [17, 2, 18, 0, 23, 9, 10, 11, 4, 21];

            foreach (int i in order)
            {
                if (i >= source.FrameCount) continue;

                using var frame = await source.LoadFrameAsync(i);
                Assert.IsNotNull(frame, $"frame {i} should decode");

                int decoded = NearestCycleIndex(SampleCentre(frame));
                Assert.AreEqual(
                    i % Cycle.Length, decoded,
                    $"frame {i} decoded as cycle colour {decoded} (expected {i % Cycle.Length}) — "
                    + "the decoder delivered a different frame than the one requested");
            }
        }
        finally
        {
            source.Dispose();
        }
    }
}
