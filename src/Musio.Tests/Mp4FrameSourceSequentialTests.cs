using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Processing;
using Musio.Tests.TestSupport;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Frame-accuracy guards for the MP4 decoder.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mp4FrameSource"/> completes each decoder command when
/// <c>VideoFrameAvailable</c> raises, but that event is not guaranteed to raise exactly
/// once per issued command. A late or duplicate raise from the previous command can
/// complete the next command's request without the decoder having advanced — handing back
/// frame N-1 labelled N and leaving the internal index permanently offset. The exporter
/// reads through this same class, so such a slip is baked silently into an exported file.
/// </para>
/// <para>
/// These tests decode a real MP4 whose every frame is a distinct colour, so a slip of a
/// single frame fails loudly instead of passing as plausible-looking video.
/// </para>
/// <para>
/// BOTH tests are currently <c>[Ignore]</c>d because they FAIL against real defects that
/// are deliberately not fixed on this branch — changing decode timing affects export
/// correctness and overlaps the encoder work in flight. They are committed in a failing-if-
/// enabled state on purpose: they are the ready-made proof for whoever fixes the decoder.
/// Re-enable both together.
/// </para>
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

    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("musio_seq_");
    }

    [TestCleanup]
    public void TearDown()
    {
        _tempDir?.Dispose();
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
    [Ignore("Known defect, CONFIRMED ON CI: a stale VideoFrameAvailable raise from the "
        + "priming seek satisfies the first step's request, so frame 1 decodes as frame 0. "
        + "Passes on a GPU decoder (the duplicate raise lands before the next command is "
        + "armed) and fails on the headless CI runner's software decoder, which is slower. "
        + "Run 30569822027: 'frame 1 decoded as cycle colour 0 (expected 1)'. The fix is to "
        + "make a raise attributable to the command that caused it, rather than completing "
        + "whichever request happens to be armed. Enable together with the random-access test.")]
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
        + "(gap <= MaxStepAhead) are exact on a GPU decoder, but every read served by "
        + "SeekToAsync returns frame i-1, isolating the fault to TimeForFrame's "
        + "index-to-time mapping. Pre-existing in the MP4-backed decode path, not introduced "
        + "by the preview fixes. Affects scrubbing accuracy and any export that seeks. "
        + "Enable together with the sequential test.")]
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
