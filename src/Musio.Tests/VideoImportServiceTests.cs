using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Media;
using Musio.Tests.TestSupport;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Behavioural guards for <see cref="VideoImportService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The suite encodes a real short MP4 with <see cref="VideoWriter"/> (the same technique
/// <c>Mp4FrameSourceSequentialTests</c> uses) so import is exercised end-to-end against a
/// genuine H.264 file rather than a stub. The generated file is video-only, which also makes
/// it the fixture for the no-audio path.
/// </para>
/// <para>
/// The orientation assertion is the load-bearing one: an external video with no marker would
/// be treated as legacy and flipped for preview AND export, so
/// <see cref="RecordingMarker.NeedsVerticalFlip"/> returning <c>false</c> after import is the
/// whole point of the normalisation.
/// </para>
/// </remarks>
[TestClass]
public class VideoImportServiceTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;
    private const int FrameCount = 24;

    private static readonly Color[] Cycle =
    [
        Color.FromArgb(255, 220, 20, 20),
        Color.FromArgb(255, 20, 200, 20),
        Color.FromArgb(255, 20, 20, 220),
        Color.FromArgb(255, 230, 230, 230),
    ];

    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("musio_import_");
    }

    [TestCleanup]
    public void TearDown()
    {
        _tempDir?.Dispose();
    }

    /// <summary>Writes a real, finalized, video-only MP4 to act as an "external" source file.</summary>
    private async Task<string> WriteSourceMp4Async(
        string name = "source.mp4", int frameCount = FrameCount, int fps = Fps)
    {
        var device = CanvasDevice.GetSharedDevice();
        var sourceDir = Path.Combine(_root, "src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var videoPath = Path.Combine(sourceDir, "video.mp4");

        using var writer = new VideoWriter(videoPath, Width, Height, fps, captureDevice: null);
        for (int i = 0; i < frameCount; i++)
        {
            using var frame = new CanvasRenderTarget(device, Width, Height, 96);
            using (var ds = frame.CreateDrawingSession())
                ds.Clear(Cycle[i % Cycle.Length]);
            writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)fps));
        }

        writer.StopAcceptingFrames();
        await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await writer.FinalizeAsync();
        Assert.IsTrue(writer.FinalizeSucceeded, "the source MP4 should have finalized");

        // A raw external file has no marker beside it — the very condition that would flip it.
        // Rename so the source is not literally named video.mp4 in the same folder as its marker.
        var external = Path.Combine(sourceDir, name);
        File.Move(videoPath, external, overwrite: true);
        File.Delete(Path.Combine(sourceDir, RecordingMarker.SessionMarkerName));
        return external;
    }

    [TestMethod]
    public async Task Import_ProducesUprightNonEmptyVideo()
    {
        var source = await WriteSourceMp4Async();

        // Sanity: the untouched external file WOULD be flipped (no marker beside it).
        Assert.IsTrue(
            RecordingMarker.NeedsVerticalFlip(source),
            "a marker-less external file is legacy and would be flipped — the precondition import fixes");

        var result = await VideoImportService.ImportAsync(source, _root, null, CancellationToken.None);

        Assert.IsTrue(File.Exists(result.VideoFilePath), "import should produce a video.mp4");
        Assert.AreEqual("video.mp4", Path.GetFileName(result.VideoFilePath));
        Assert.IsTrue(new FileInfo(result.VideoFilePath).Length > 0, "the imported video must be non-empty");
        Assert.IsTrue(result.WasTranscoded, "import always re-encodes");
        Assert.IsFalse(
            RecordingMarker.NeedsVerticalFlip(result.VideoFilePath),
            "the imported video must be marked orientation-correct so it is not flipped");
        Assert.IsTrue(result.ImportFolder.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Import_ReportsSaneMetadataMatchingSource()
    {
        var source = await WriteSourceMp4Async();

        var result = await VideoImportService.ImportAsync(source, _root, null, CancellationToken.None);

        Assert.AreEqual(Width, result.Width, "width should be preserved (already even)");
        Assert.AreEqual(Height, result.Height, "height should be preserved (already even)");
        Assert.AreEqual(0, result.Width % 2, "width must be H.264-safe even");
        Assert.AreEqual(0, result.Height % 2, "height must be H.264-safe even");

        Assert.IsTrue(result.Fps is > 0 and <= 60, $"fps {result.Fps} should be sane");

        double expected = FrameCount / (double)Fps;
        Assert.IsTrue(
            Math.Abs(result.Duration.TotalSeconds - expected) < 0.75,
            $"duration {result.Duration.TotalSeconds:0.###}s should be near source {expected:0.###}s");

        Assert.AreEqual("source.mp4", result.SourceFileName);
    }

    [TestMethod]
    public async Task Import_SourceWithoutAudio_YieldsNoAudioWithoutThrowing()
    {
        // VideoWriter emits a video-only MP4, so this is a genuine no-audio source.
        var source = await WriteSourceMp4Async();

        var result = await VideoImportService.ImportAsync(source, _root, null, CancellationToken.None);

        Assert.IsFalse(result.HasAudio, "a silent source must import as video-only");
        Assert.AreEqual(0, result.AudioFilePaths.Count, "no audio side-car should be registered");
        Assert.IsFalse(
            File.Exists(Path.Combine(result.ImportFolder, "audio.wav")),
            "no audio.wav should be written for a silent source");
    }

    [TestMethod]
    public async Task Import_MissingFile_ThrowsVideoImportException()
    {
        var missing = Path.Combine(_root, "does_not_exist.mp4");

        await Assert.ThrowsExceptionAsync<VideoImportException>(
            () => VideoImportService.ImportAsync(missing, _root, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Import_UnsupportedExtension_ThrowsVideoImportException()
    {
        var txt = Path.Combine(_root, "not_a_video.txt");
        await File.WriteAllTextAsync(txt, "hello");

        await Assert.ThrowsExceptionAsync<VideoImportException>(
            () => VideoImportService.ImportAsync(txt, _root, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Import_NonVideoFileWithVideoExtension_ThrowsVideoImportException()
    {
        // Right extension, junk contents: must surface a friendly VideoImportException,
        // never a raw COM/ArgumentException from the media stack.
        var fake = Path.Combine(_root, "corrupt.mp4");
        await File.WriteAllBytesAsync(fake, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });

        await Assert.ThrowsExceptionAsync<VideoImportException>(
            () => VideoImportService.ImportAsync(fake, _root, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Import_CancelledMidTranscode_CleansUpItsImportFolder()
    {
        // A longer source so the transcode is still running when we cancel: the point is to
        // enter (and prove) the OperationCanceledException → CleanupImportFolder path, which a
        // pre-cancelled token would skip entirely (it throws before the folder is even created).
        var source = await WriteSourceMp4Async("source.mp4", frameCount: 240, fps: 30);

        using var cts = new CancellationTokenSource();
        string? observedFolder = null;
        var gate = new object();

        // Progress crosses into the transcode band (0.08..0.85) only after the folder exists,
        // so when we see a transcode-phase value we can both capture the live folder and cancel.
        var progress = new Progress<double>(p =>
        {
            if (p < 0.12) return;
            lock (gate)
            {
                observedFolder ??= Directory
                    .EnumerateDirectories(_root, SessionPaths.ImportFolderPrefix + "*")
                    .FirstOrDefault();
            }
            cts.Cancel();
        });

        Exception? caught = null;
        try
        {
            await VideoImportService.ImportAsync(source, _root, progress, cts.Token);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // OperationCanceledException OR its TaskCanceledException subclass are both correct —
        // the point is that cancellation surfaced, not the exact leaf type.
        Assert.IsInstanceOfType(caught, typeof(OperationCanceledException),
            $"expected a cancellation exception, got: {caught?.GetType().Name ?? "no exception"}");

        // (a) The cancellation genuinely happened mid-import: an import folder had been created.
        Assert.IsNotNull(observedFolder,
            "expected an import_* folder to exist mid-transcode — the cancel path was not exercised");

        // (b) That folder — and any video.mp4 / partial under the root — was cleaned up. If
        //     CleanupImportFolder were removed, the folder (with its .partial) would remain.
        Assert.IsFalse(Directory.Exists(observedFolder!),
            $"the cancelled import's folder must be removed, but '{observedFolder}' still exists");

        var strays = Directory.EnumerateFiles(_root, "video.mp4", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(_root, "*.partial", SearchOption.AllDirectories))
            .Where(p => p.Contains(SessionPaths.ImportFolderPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.AreEqual(0, strays.Count,
            "a cancelled import must leave no video.mp4 or partial in an import folder: "
            + string.Join(", ", strays));
    }
}
