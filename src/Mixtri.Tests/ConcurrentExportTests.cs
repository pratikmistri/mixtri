using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Export;
using Mixtri.Core.Capture;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.Media.Editing;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
[DoNotParallelize]
public class ConcurrentExportTests
{
    private const int Width = 320;
    private const int Height = 192;
    private const int Fps = 10;

    private static ExportSettings Settings(int fps = Fps) => new()
    {
        Fps = fps,
        ForceSoftwareRendering = Environment.GetEnvironmentVariable("MIXTRI_TEST_SOFTWARE_RENDERING") == "1",
    };

    private static CompositionConfig Config() => new()
    {
        OutputFps = Fps,
        Cursor = new CursorStyle { Type = CursorType.Hidden },
        Background = new BackgroundStyle { Padding = 0, CornerRadius = 0, ShadowEnabled = false },
        MotionBlur = new MotionBlurSettings { Enabled = false },
    };

    private static async Task<Project> WriteFramesAsync(string root, int frames = 20)
    {
        Directory.CreateDirectory(root);
        var frameDirectory = Path.Combine(root, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(frameDirectory);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        for (int i = 0; i < frames; i++)
        {
            using var frame = new CanvasRenderTarget(device, Width, Height, 96);
            using (var ds = frame.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(255, 20, (byte)(60 + i % 4 * 40), 220));
                ds.FillRectangle(0, 0, Width / 2, Height / 2, Color.FromArgb(255, 220, 30, 20));
                ds.FillRectangle(Width / 2, Height / 2, Width / 2, Height / 2, Color.FromArgb(255, 20, 210, 30));
            }
            await frame.SaveAsync(
                Path.Combine(frameDirectory, $"frame_{i:D8}.jpg"), CanvasBitmapFileFormat.Jpeg, 1);
        }
        return new Project
        {
            Name = "Concurrent", VideoFilePath = Path.Combine(root, "video.mp4"),
            Width = Width, Height = Height, Fps = Fps,
            Duration = TimeSpan.FromSeconds(frames / (double)Fps),
        };
    }

    private static TimelineModel Timeline(Project project)
    {
        var video = TestTimelineBuilder.Video(project.VideoFilePath, 0, 1) with { Fps = Fps };
        var slide = new TextSlideSegment
        {
            Text = "Isolated", Duration = TimeSpan.FromSeconds(1),
            BackgroundColor = "#123456", Animation = TextSlideAnimation.None,
            InTransition = new TransitionConfig
            {
                Type = TransitionType.CrossFade, Duration = TimeSpan.FromMilliseconds(200),
            },
        };
        var timeline = TestTimelineBuilder.ModelWithPrimaryPath(project.VideoFilePath, video, slide);
        timeline.Fps = Fps;
        timeline.Duration = project.Duration;
        timeline.TrimEnd = project.Duration;
        return timeline;
    }

    [TestMethod]
    public async Task PrivateDevices_KeepReaderAndCompositorResourcesOnTheirOwner()
    {
        using var temp = new TempDirectoryFixture("mixtri_device_isolation_");
        var project = await WriteFramesAsync(temp.Path);
        using var exportDevice = GpuContext.CreateExportDevice();
        Assert.IsTrue(exportDevice.LowPriority);
        Assert.IsTrue(exportDevice != GpuContext.GetSharedDevice());
        using var reader = await VideoFrameReader.OpenFromVideoPathAsync(
            project.VideoFilePath, Fps, exportDevice, cacheFrames: false);
        Assert.IsNotNull(reader);
        using var source = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(source);
        Assert.IsTrue(source.Device == exportDevice);
        using var compositor = new FrameCompositor(Config(), exportDevice);
        await compositor.InitializeAsync(new MouseRecordingData(), Width, Height, project.Duration);
        using var frame = compositor.ComposeFrame(source, 0.0);
        Assert.IsTrue(frame.Device == exportDevice);
        AssertVideoPixels(frame);
    }

    [TestMethod]
    public async Task SourceContexts_RemainBoundedAcrossManyRecordings()
    {
        using var temp = new TempDirectoryFixture("mixtri_context_budget_");
        var primary = await WriteFramesAsync(Path.Combine(temp.Path, "primary"));
        var segments = new List<TimelineSegment>();
        for (int i = 0; i < 5; i++)
        {
            var project = await WriteFramesAsync(Path.Combine(temp.Path, $"source_{i}"));
            segments.Add(new VideoSegment
            {
                VideoFilePath = project.VideoFilePath, Duration = TimeSpan.FromSeconds(1),
                SourceDuration = TimeSpan.FromSeconds(1), SourceWidth = Width, SourceHeight = Height, Fps = Fps,
            });
        }
        var timeline = TestTimelineBuilder.ModelWithPrimaryPath(primary.VideoFilePath, segments.ToArray());
        using var composer = await SegmentFrameComposer.CreateAsync(
            primary, new MouseRecordingData(), Config(), timeline, new TimelineMapper(timeline, Fps), Fps);
        for (int i = 0; i < segments.Count; i++)
        {
            using var frame = await composer.ComposeFrameAsync(i * Fps + 5);
            Assert.IsTrue(composer.CachedContextCount <= SegmentFrameComposer.MaxSourceContexts);
            AssertVideoPixels(frame);
        }
        using var revisited = await composer.ComposeFrameAsync(5);
        AssertVideoPixels(revisited);
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task WorkerSnapshot_KeepsPixelsAndOutputStableWhileLiveInputsChange()
    {
        var workerPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_WORKER_PATH");
        if (string.IsNullOrEmpty(workerPath))
            Assert.Inconclusive("Set MIXTRI_TEST_WORKER_PATH to exercise the production export boundary.");
        using var temp = new TempDirectoryFixture("mixtri_concurrent_export_");
        var project = await WriteFramesAsync(Path.Combine(temp.Path, "source"));
        var timeline = Timeline(project);
        var config = Config();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = Settings();
        var snapshot = ProjectStateSnapshot.Capture(project, config, timeline);
        var client = new ExportWorkerClient(workerPath);
        var baseline = await client.ExportAsync(
            snapshot, settings, Path.Combine(temp.Path, "baseline"), null, cancellation.Token);

        client.SetPlaybackActive(true);
        client.ReportPreviewFrame(TimeSpan.FromMilliseconds(40));
        int completedFrames = 0;
        var exporting = client.ExportAsync(
            snapshot, settings, Path.Combine(temp.Path, "concurrent"),
            new FrameProgress(value => Volatile.Write(ref completedFrames, value.CurrentFrame)),
            cancellation.Token);
        timeline.Segments.Clear();
        project.Name = "Edited during export";
        int observed;
        try { observed = await ExercisePreviewAsync(project.VideoFilePath, exporting, cancellation.Token); }
        catch
        {
            Console.WriteLine($"Concurrent export stopped after {Volatile.Read(ref completedFrames)} frames.");
            try { await exporting.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { Console.WriteLine($"Export drain: {ex.GetType().Name}: {ex.Message}"); }
            throw;
        }
        var actual = await exporting;
        Assert.IsTrue(observed >= 5, "Preview must render while export is in progress.");
        Assert.AreEqual("Concurrent.mp4", Path.GetFileName(actual), "Export must use the captured project name.");
        await AssertVideosMatchAsync(baseline, actual);
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task WorkerProcess_ExportsAlongsidePreviewAndCancelsWithoutOrphans()
    {
        var workerPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_WORKER_PATH");
        if (string.IsNullOrEmpty(workerPath))
            Assert.Inconclusive("Set MIXTRI_TEST_WORKER_PATH to the built app to exercise process isolation.");
        using var temp = new TempDirectoryFixture("mixtri_worker_export_");
        var project = await WriteFramesAsync(Path.Combine(temp.Path, "source"));
        var snapshot = ProjectStateSnapshot.Capture(project, Config(), Timeline(project));
        var client = new ExportWorkerClient(workerPath);
        client.SetPlaybackActive(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int completedFrames = 0;
        var export = client.ExportAsync(snapshot, Settings(),
            Path.Combine(temp.Path, "output"),
            new FrameProgress(value => Volatile.Write(ref completedFrames, value.CurrentFrame)), timeout.Token);
        int observed;
        try { observed = await ExercisePreviewAsync(project.VideoFilePath, export, timeout.Token); }
        catch
        {
            Console.WriteLine($"Worker {client.ActiveProcessId} stopped after {Volatile.Read(ref completedFrames)} frames.");
            try { await export.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { Console.WriteLine($"Worker drain: {ex.GetType().Name}: {ex.Message}"); }
            throw;
        }
        var path = await export;
        Assert.IsTrue(observed >= 5);
        Assert.IsTrue(File.Exists(path));
        Assert.IsNull(client.ActiveProcessId);
        client.SetPlaybackActive(false);
        var baseline = await client.ExportAsync(
            snapshot, Settings(),
            Path.Combine(temp.Path, "baseline"), null, timeout.Token);
        await AssertVideosMatchAsync(baseline, path);

        client.SetPlaybackActive(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        int framesBeforeCancellation = 0;
        var cancelled = client.ExportAsync(snapshot, Settings(),
            Path.Combine(temp.Path, "cancelled"), new FrameProgress(value =>
            {
                Volatile.Write(ref framesBeforeCancellation, value.CurrentFrame);
                cancellation.Cancel();
            }), cancellation.Token);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await cancelled.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsTrue(Volatile.Read(ref framesBeforeCancellation) > 0, "Cancel after rendering has begun.");
        Assert.IsNull(client.ActiveProcessId);

        var gifSettings = Settings() with { Format = Mixtri.Core.Settings.VideoFormat.GIF };
        var gif = await client.ExportAsync(
            snapshot, gifSettings, Path.Combine(temp.Path, "gif"), null, timeout.Token);
        using (var gifStream = await (await StorageFile.GetFileFromPathAsync(gif)).OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(gifStream);
            Assert.AreEqual(20u, decoder.FrameCount);
            Assert.AreEqual((uint)Width, decoder.PixelWidth);
            Assert.AreEqual((uint)Height, decoder.PixelHeight);
            var lastFrame = await decoder.GetFrameAsync(19);
            var pixels = (await lastFrame.GetPixelDataAsync()).DetachPixelData();
            Assert.IsTrue(pixels.Any(value => value != 0), "The last GIF frame must be retained through flush.");
        }
        Assert.IsNull(client.ActiveProcessId);

        using var failedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var invalid = ProjectStateSnapshot.Capture(new Project
        {
            VideoFilePath = Path.Combine(temp.Path, "missing", "video.mp4"), Duration = TimeSpan.FromSeconds(1),
        }, Config(), null);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await client.ExportAsync(invalid, Settings(), temp.Path, null, failedTimeout.Token));
        Assert.IsNull(client.ActiveProcessId);

        var crashed = client.ExportAsync(snapshot, Settings(),
            Path.Combine(temp.Path, "crashed"), null, failedTimeout.Token);
        while (client.ActiveProcessId is null)
            await Task.Delay(10, failedTimeout.Token);
        using (var worker = Process.GetProcessById(client.ActiveProcessId.Value))
            worker.Kill(entireProcessTree: true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await crashed.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNull(client.ActiveProcessId);
    }

    [TestMethod]
    [Timeout(240000)]
    public async Task LongWorkerExport_AdvancesPast288FramesAtReportedNativeResolution()
    {
        var workerPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_WORKER_PATH");
        if (string.IsNullOrEmpty(workerPath))
            Assert.Inconclusive("Set MIXTRI_TEST_WORKER_PATH to exercise sustained native export.");
        using var temp = new TempDirectoryFixture("mixtri_long_native_export_");
        const int width = 2304, height = 1456, fps = 30, totalFrames = 450;
        var sourceRoot = Path.Combine(temp.Path, "source");
        var framesRoot = Path.Combine(sourceRoot, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(framesRoot);
        using (var device = new CanvasDevice(forceSoftwareRenderer: true))
        {
            for (int i = 0; i < 4; i++)
            {
                using var frame = new CanvasRenderTarget(device, width, height, 96);
                using (var ds = frame.CreateDrawingSession())
                {
                    ds.Clear(Color.FromArgb(255, (byte)(20 + 50 * i), 80, 200));
                    ds.FillRectangle(0, 0, width / 2, height / 2, Color.FromArgb(255, 220, 30, 20));
                    ds.FillRectangle(width / 2, height / 2, width / 2, height / 2, Color.FromArgb(255, 20, 210, 30));
                }
                await frame.SaveAsync(Path.Combine(framesRoot, $"frame_{i:D8}.jpg"),
                    CanvasBitmapFileFormat.Jpeg, 1);
            }
        }
        for (int i = 4; i < fps; i++)
            File.Copy(Path.Combine(framesRoot, $"frame_{i % 4:D8}.jpg"),
                Path.Combine(framesRoot, $"frame_{i:D8}.jpg"));
        var project = new Project
        {
            Name = "Sustained", VideoFilePath = Path.Combine(sourceRoot, "video.mp4"),
            Width = width, Height = height, Fps = fps, Duration = TimeSpan.FromSeconds(1),
        };
        var segments = Enumerable.Range(0, totalFrames / fps).Select(_ => (TimelineSegment)new VideoSegment
        {
            VideoFilePath = project.VideoFilePath, Fps = fps,
            Duration = TimeSpan.FromSeconds(1), SourceDuration = TimeSpan.FromSeconds(1),
        }).ToArray();
        var timeline = TestTimelineBuilder.ModelWithPrimaryPath(project.VideoFilePath, segments);
        timeline.Fps = fps;
        timeline.Duration = timeline.TotalSegmentsDuration;
        timeline.TrimEnd = timeline.Duration;
        var snapshot = ProjectStateSnapshot.Capture(
            project, Config() with { OutputFps = fps }, timeline);
        var client = new ExportWorkerClient(workerPath);
        int lastFrame = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var path = await client.ExportAsync(snapshot, Settings(fps) with
        {
            Fps = fps, Resolution = Mixtri.Core.Settings.VideoResolution.UHD4K,
        }, Path.Combine(temp.Path, "output"),
            new FrameProgress(progress => Volatile.Write(ref lastFrame, progress.CurrentFrame)), cancellation.Token);
        Assert.AreEqual(totalFrames, Volatile.Read(ref lastFrame));
        Assert.IsNull(client.ActiveProcessId);
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        Assert.AreEqual(TimeSpan.FromSeconds(totalFrames / (double)fps), clip.OriginalDuration);
        var encoding = clip.GetVideoEncodingProperties();
        Assert.AreEqual((uint)width, encoding.Width);
        Assert.AreEqual((uint)height, encoding.Height);
        var composition = new MediaComposition();
        try
        {
            composition.Clips.Add(clip);
            using var stream = await composition.GetThumbnailAsync(
                TimeSpan.FromSeconds(12), Width, Height, VideoFramePrecision.NearestFrame);
            using var decoded = await CanvasBitmap.LoadAsync(GpuContext.GetSharedDevice(), stream);
            AssertVideoPixels(decoded, "Native-resolution export beyond the stalled frame");
        }
        finally { composition.Clips.Clear(); }
    }

    [TestMethod]
    [Timeout(600000)]
    public async Task SavedProjectExport_CompletesFromAnIsolatedCopy()
    {
        var workerPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_WORKER_PATH");
        var projectPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_PROJECT_PATH");
        if (string.IsNullOrEmpty(workerPath) || string.IsNullOrEmpty(projectPath))
            Assert.Inconclusive("Set worker and project paths for an opt-in saved-project regression.");
        using var temp = new TempDirectoryFixture("mixtri_saved_export_");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var original = new FileInfo(projectPath);
        long originalLength = original.Length;
        var originalWritten = original.LastWriteTimeUtc;
        var opened = await MixtriPackageService.OpenAsync(
            projectPath, Path.Combine(temp.Path, "working"), ct: cancellation.Token);
        var snapshot = ProjectStateSnapshot.Capture(opened.Project, opened.Composition, opened.Timeline);
        const int fps = 30;
        int expectedFrames = new TimelineMapper(opened.Timeline, fps).TotalOutputFrames;
        int completed = 0;
        var client = new ExportWorkerClient(workerPath);
        var output = await client.ExportAsync(snapshot, Settings(fps) with
        {
            Fps = fps, Resolution = Mixtri.Core.Settings.VideoResolution.UHD4K,
        }, Path.Combine(temp.Path, "output"),
            new FrameProgress(value => Volatile.Write(ref completed, value.CurrentFrame)),
            cancellation.Token);
        Assert.IsTrue(expectedFrames > 288, "The regression project must extend beyond the stalled frame.");
        Assert.AreEqual(expectedFrames, Volatile.Read(ref completed));
        Assert.IsNull(client.ActiveProcessId);
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(output));
        Assert.IsTrue(Math.Abs(clip.OriginalDuration.TotalSeconds - expectedFrames / (double)fps) < .2);
        original.Refresh();
        Assert.AreEqual(originalLength, original.Length);
        Assert.AreEqual(originalWritten, original.LastWriteTimeUtc);
        Console.WriteLine($"Saved-project export completed all {expectedFrames} frames.");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task MediaPlayerInterop_ConcurrentCopiesAndTeardownRemainSafe()
    {
        var workerPath = Environment.GetEnvironmentVariable("MIXTRI_TEST_WORKER_PATH");
        if (string.IsNullOrEmpty(workerPath))
            Assert.Inconclusive("Set MIXTRI_TEST_WORKER_PATH to generate the native decoder fixture.");
        using var temp = new TempDirectoryFixture("mixtri_decoder_interop_");
        var project = await WriteFramesAsync(Path.Combine(temp.Path, "source"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var client = new ExportWorkerClient(workerPath);
        var path = await client.ExportAsync(
            ProjectStateSnapshot.Capture(project, Config(), null), Settings(),
            Path.Combine(temp.Path, "encoded"), null, cancellation.Token);
        var device = GpuContext.GetSharedDevice();
        var reference = new MediaComposition();
        try
        {
            reference.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path)));
            using var stream = await reference.GetThumbnailAsync(
                TimeSpan.FromMilliseconds(50), Width, Height, VideoFramePrecision.NearestFrame);
            using var decoded = await CanvasBitmap.LoadAsync(device, stream);
            AssertVideoPixels(decoded, "Independent decode of encoded reference");
        }
        finally { reference.Clips.Clear(); }
        using var first = await Mp4FrameSource.OpenAsync(path, Fps, device, flipVertical: false);
        using var second = await Mp4FrameSource.OpenAsync(path, Fps, device, flipVertical: false);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);

        async Task ReadFrames(Mp4FrameSource source, string name)
        {
            // Frame zero reuses the startup/priming surface. Exercise twelve fresh
            // native copies on each decoder instead of asserting that cached surface.
            for (int i = 1; i <= 12; i++)
            {
                using var frame = await source.LoadFrameAsync(i).WaitAsync(cancellation.Token);
                Assert.IsNotNull(frame, $"Decoder did not deliver frame {i}.");
                AssertVideoPixels(frame, $"{name} decoder frame {i}");
            }
        }
        await Task.WhenAll(ReadFrames(first, "first"), ReadFrames(second, "second"));
        var pending = second.LoadFrameAsync(17);
        await Task.Run(second.Dispose);
        using var last = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        if (last is not null) AssertVideoPixels(last);
    }

    private static async Task<int> ExercisePreviewAsync(string videoPath, Task exporting, CancellationToken ct)
    {
        var device = GpuContext.GetSharedDevice();
        using var reader = await VideoFrameReader.OpenFromVideoPathAsync(videoPath, Fps);
        Assert.IsNotNull(reader);
        using var compositor = new FrameCompositor(Config());
        await compositor.InitializeAsync(new MouseRecordingData(), Width, Height, TimeSpan.FromSeconds(2));
        int observed = 0;
        while (!exporting.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            using var source = await reader.LoadFrameAsync((observed * 7) % 20);
            Assert.IsNotNull(source);
            using var frame = compositor.ComposeFrame(source, (observed % 20) / (double)Fps);
            Assert.IsTrue(frame.Device == device);
            AssertVideoPixels(frame);
            observed++;
            await Task.Delay(TimeSpan.FromMilliseconds(1000.0 / 30), ct);
        }
        return observed;
    }

    private static void AssertVideoPixels(CanvasBitmap frame, string context = "Frame")
    {
        var pixels = frame.GetPixelColors();
        int width = (int)frame.SizeInPixels.Width;
        int height = (int)frame.SizeInPixels.Height;
        var red = pixels[(height / 4) * width + width / 4];
        var green = pixels[(height * 3 / 4) * width + width * 3 / 4];
        Assert.IsTrue(red.R > 180 && red.G < 60 && red.B < 60, $"{context}: corrupted red quadrant: {red}");
        Assert.IsTrue(green.G > 180 && green.R < 60 && green.B < 60, $"{context}: corrupted green quadrant: {green}");
    }

    private static async Task AssertVideosMatchAsync(string expectedPath, string actualPath)
    {
        var expected = new MediaComposition();
        var actual = new MediaComposition();
        try
        {
            expected.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(expectedPath)));
            actual.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(actualPath)));
            Assert.AreEqual(expected.Duration, actual.Duration);
            foreach (double seconds in new[] { .25, .75, 1.1, 1.5, 1.9 })
            {
                using var firstStream = await expected.GetThumbnailAsync(
                    TimeSpan.FromSeconds(seconds), Width, Height, VideoFramePrecision.NearestFrame);
                using var secondStream = await actual.GetThumbnailAsync(
                    TimeSpan.FromSeconds(seconds), Width, Height, VideoFramePrecision.NearestFrame);
                using var first = await CanvasBitmap.LoadAsync(GpuContext.GetSharedDevice(), firstStream);
                using var second = await CanvasBitmap.LoadAsync(GpuContext.GetSharedDevice(), secondStream);
                var a = first.GetPixelBytes();
                var b = second.GetPixelBytes();
                double meanError = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
                Assert.IsTrue(meanError < 2, $"Concurrent export changed pixels at {seconds}s: error {meanError}.");
            }
        }
        finally
        {
            expected.Clips.Clear();
            actual.Clips.Clear();
        }
    }

    private sealed class FrameProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
