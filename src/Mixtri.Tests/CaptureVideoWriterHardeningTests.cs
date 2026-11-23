using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

/// <summary>
/// Recording-writer behaviour that must hold end to end: reshaped frames still encode,
/// finalization reports progress through the session, and a writer that gives up on a
/// broken disk is reported rather than silently truncating the recording.
/// </summary>
[TestClass]
public class CaptureVideoWriterHardeningTests
{
    private const int Fps = 10;

    // Microsoft.UI.Colors needs WinUI activation, which the test host does not have.
    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;

    [TestInitialize]
    public void SetUp() => _tempDir = new TempDirectoryFixture("mixtri_capture_writer_");

    [TestCleanup]
    public void TearDown() => _tempDir?.Dispose();

    private RecordingSession CreateSession(List<string> errors)
    {
        var session = new RecordingSession(new RecordingSessionConfig
        {
            Target = new CaptureTarget(CaptureTargetType.Monitor, IntPtr.Zero, "test"),
            Fps = Fps,
            OutputFolder = _root,
        });

        session.Error += (_, message) => { lock (errors) { errors.Add(message); } };
        return session;
    }

    private VideoWriter CreateWriter(string folder, int width, int height)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        return new VideoWriter(Path.Combine(dir, "video.mp4"), width, height, Fps, captureDevice: null);
    }

    private static CanvasRenderTarget CreateFrame(int width, int height)
    {
        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var ds = target.CreateDrawingSession())
            ds.Clear(Grey);
        return target;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    [TestMethod]
    public async Task ResizedFrames_StillEncode_AndProgressReachesCompletion()
    {
        var errors = new List<string>();
        using var session = CreateSession(errors);
        using var writer = CreateWriter("resized", 320, 240);

        // The window was resized mid-recording: the later frames are a different shape.
        using var original = CreateFrame(320, 240);
        using var narrow = CreateFrame(160, 240);
        using var wide = CreateFrame(320, 100);

        for (int i = 0; i < 3; i++)
            writer.WriteFrame(original, TimeSpan.FromSeconds(i / (double)Fps));
        for (int i = 3; i < 6; i++)
            writer.WriteFrame(narrow, TimeSpan.FromSeconds(i / (double)Fps));
        for (int i = 6; i < 9; i++)
            writer.WriteFrame(wide, TimeSpan.FromSeconds(i / (double)Fps));

        var progress = new List<double>();
        session.FinalizationProgressChanged += (_, p) => { lock (progress) { progress.Add(p); } };

        await session.DrainFrameWritesAsync(writer, TimeSpan.FromSeconds(60), CancellationToken.None);
        await session.FinalizeCaptureAsync(writer, CancellationToken.None);

        Assert.IsTrue(writer.FinalizeSucceeded, "reshaped frames must be letterboxed, not fail the encode");
        Assert.IsNull(session.Fault, string.Join(" | ", errors));

        lock (progress)
        {
            Assert.IsTrue(progress.Count > 0, "finalization must report progress");
            Assert.AreEqual(1.0, progress[^1], "a successful encode ends at 100%");
            for (int i = 1; i < progress.Count; i++)
                Assert.IsTrue(progress[i] >= progress[i - 1], "progress never goes backwards");
            Assert.IsTrue(progress.All(p => p is >= 0 and <= 1));
        }
    }

    [TestMethod]
    public async Task RepeatedWriteFailures_AreReportedAsAbandonedWriting()
    {
        var errors = new List<string>();
        using var session = CreateSession(errors);
        using var writer = CreateWriter("abandoned", 160, 120);
        using var frame = CreateFrame(160, 120);

        bool abandonedEventRaised = false;
        writer.WritingAbandoned += (_, _) => abandonedEventRaised = true;

        // Replace the frames directory with a file: every write now fails, like a full disk.
        Directory.Delete(writer.FramesDirectory, recursive: true);
        File.WriteAllText(writer.FramesDirectory, "not a directory");

        for (int i = 0; i < 40 && !writer.IsWritingAbandoned; i++)
        {
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));
            await Task.Delay(5);
        }

        await WaitForAsync(() => writer.IsWritingAbandoned, "the writer to give up");
        Assert.IsTrue(abandonedEventRaised, "giving up must be announced, not silent");

        session.ReportRetainedWriteFailure(writer);

        lock (errors)
        {
            Assert.IsTrue(
                errors.Any(m => m.Contains("repeated disk write failures", StringComparison.Ordinal)),
                "the session must tell the user that the rest of the recording is being lost");
        }

        File.Delete(writer.FramesDirectory);
    }
}
