using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

/// <summary>
/// Guards what a degraded recording is allowed to tell the user about its captured JPEGs.
/// </summary>
/// <remarks>
/// The frames are a write-ahead buffer: a successful MP4 finalization deletes them. A fault
/// raised while the recording is still running (a failed frame write, a drain that would not
/// finish) therefore cannot know whether they will survive, and must not promise a directory
/// that finalization is about to remove. Only the state settled after the keep/delete decision
/// may name a path.
/// </remarks>
[TestClass]
public class RecordingSessionFaultTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;

    private static readonly TimeSpan Quiescence = TimeSpan.FromSeconds(60);

    // Microsoft.UI.Colors needs WinUI activation, which the test host does not have.
    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_fault_");
    }

    [TestCleanup]
    public void TearDown()
    {
        _tempDir?.Dispose();
    }

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

    private VideoWriter CreateWriter(string folder, int width = Width, int height = Height)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        return new VideoWriter(
            Path.Combine(dir, "video.mp4"), width, height, Fps, captureDevice: null);
    }

    private static CanvasRenderTarget CreateFrame(int width = Width, int height = Height)
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

    /// <summary>Breaks the frames directory by replacing it with a file.</summary>
    private static void BreakFramesDirectory(VideoWriter writer)
    {
        Directory.Delete(writer.FramesDirectory, recursive: true);
        File.WriteAllText(writer.FramesDirectory, "not a directory");
    }

    private static void RepairFramesDirectory(VideoWriter writer)
    {
        File.Delete(writer.FramesDirectory);
        Directory.CreateDirectory(writer.FramesDirectory);
    }

    private static void AssertNoPreservationClaim(RecordingSession session, string context)
    {
        var fault = session.Fault;
        Assert.IsNotNull(fault, $"{context}: the degraded condition must still be reported");
        Assert.IsNull(fault!.PreservedFramesPath,
            $"{context}: released frames must not be advertised as preserved");
        Assert.IsFalse(fault.ToString().Contains("kept in", StringComparison.Ordinal),
            $"{context}: the message must not point at a deleted directory");
        Assert.IsNull(session.CapturedFramesPath,
            $"{context}: CapturedFramesPath must not name a deleted directory");
    }

    [TestMethod]
    public async Task DegradedWrite_ThenSuccessfulFinalization_DoesNotClaimPreservedFrames()
    {
        var errors = new List<string>();
        using var session = CreateSession(errors);
        using var writer = CreateWriter("write-fault");
        using var frame = CreateFrame();

        // A frame that cannot reach disk: the classic degraded-but-recoverable recording.
        BreakFramesDirectory(writer);
        writer.WriteFrame(frame, TimeSpan.Zero);
        await WaitForAsync(() => writer.HasWriteFailure, "the frame write to fail");

        // The rest of the recording lands normally, so there is something to encode.
        RepairFramesDirectory(writer);
        for (int i = 0; i < 6; i++)
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        await session.DrainFrameWritesAsync(writer, Quiescence, CancellationToken.None);
        session.ReportRetainedWriteFailure(writer);

        Assert.IsTrue(session.IsDegraded, "the lost frame must be retained as a fault");
        Assert.IsNull(session.Fault!.PreservedFramesPath,
            "while the recording is running nobody knows yet whether the frames survive");

        await session.FinalizeCaptureAsync(writer, CancellationToken.None);

        Assert.IsTrue(writer.FinalizeSucceeded, "the surviving frames must still encode");
        Assert.IsFalse(Directory.Exists(writer.FramesDirectory),
            "a successful MP4 releases the captured JPEGs");
        AssertNoPreservationClaim(session, "write failure + successful finalization");

        Assert.IsFalse(errors.Any(m => m.Contains("kept in", StringComparison.Ordinal)),
            "no message may have told the user about a directory that was then deleted");
    }

    [TestMethod]
    public async Task DegradedDrain_ThenSuccessfulFinalization_DoesNotClaimPreservedFrames()
    {
        var errors = new List<string>();
        using var session = CreateSession(errors);

        // 1080p JPEG encoding is far slower than enqueuing, so the writer is guaranteed to
        // still be busy when the drain gives up.
        using var writer = CreateWriter("drain-fault", 1920, 1080);
        using var frame = CreateFrame(1920, 1080);

        writer.WriteFrame(frame, TimeSpan.Zero);
        await WaitForAsync(() => writer.FrameCount >= 1, "the first frame to reach disk");

        for (int i = 1; i < 40; i++)
            writer.TryWriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        await session.DrainFrameWritesAsync(writer, TimeSpan.Zero, CancellationToken.None);

        Assert.IsTrue(session.IsDegraded, "an abandoned drain is a degraded recording");
        Assert.IsNull(session.Fault!.PreservedFramesPath,
            "the drain fault cannot know whether finalization will release the frames");

        await session.FinalizeCaptureAsync(writer, CancellationToken.None);

        Assert.IsTrue(writer.FinalizeSucceeded,
            "the frames that did land must still produce an MP4");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "drain-fault", "video.mp4")));
        Assert.IsFalse(Directory.Exists(writer.FramesDirectory),
            "a successful MP4 releases the captured JPEGs");
        AssertNoPreservationClaim(session, "drain timeout + successful finalization");
    }

    [TestMethod]
    public async Task FailedFinalization_KeepsFramesAndSaysWhereTheyAre()
    {
        var errors = new List<string>();
        using var session = CreateSession(errors);
        using var writer = CreateWriter("finalize-fault");
        using var frame = CreateFrame();

        for (int i = 0; i < 4; i++)
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

        await session.DrainFrameWritesAsync(writer, Quiescence, CancellationToken.None);

        // Remove a counted frame so encoding fails on the missing JPEG.
        File.Delete(Path.Combine(writer.FramesDirectory, "frame_00000002.jpg"));

        await session.FinalizeCaptureAsync(writer, CancellationToken.None);

        Assert.IsFalse(writer.FinalizeSucceeded, "the encode was sabotaged");
        Assert.IsTrue(Directory.Exists(writer.FramesDirectory),
            "the JPEGs are now the only copy of the recording");

        var fault = session.Fault;
        Assert.IsNotNull(fault);
        Assert.AreEqual(writer.FramesDirectory, fault!.PreservedFramesPath,
            "frames that really survive must be named");
        StringAssert.Contains(fault.ToString(), writer.FramesDirectory);
        Assert.AreEqual(writer.FramesDirectory, session.CapturedFramesPath);

        Assert.IsTrue(
            errors.Any(m => m.Contains(writer.FramesDirectory, StringComparison.Ordinal)),
            "the user must be told where the surviving frames are");
    }

    [TestMethod]
    public void CapturedFramesRemain_IsFalseForMissingEmptyOrUnreadableDirectories()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var notADirectory = Path.Combine(_root, "file");
        File.WriteAllText(notADirectory, "x");

        var populated = Path.Combine(_root, "populated");
        Directory.CreateDirectory(populated);
        File.WriteAllText(Path.Combine(populated, "frame_00000000.jpg"), "x");

        Assert.IsFalse(RecordingSession.CapturedFramesRemain(null));
        Assert.IsFalse(RecordingSession.CapturedFramesRemain(Path.Combine(_root, "missing")));
        Assert.IsFalse(RecordingSession.CapturedFramesRemain(empty));
        Assert.IsFalse(RecordingSession.CapturedFramesRemain(notADirectory));
        Assert.IsTrue(RecordingSession.CapturedFramesRemain(populated));
    }
}
