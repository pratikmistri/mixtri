using Microsoft.Graphics.Canvas;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class LiveExportSnapshotTests
{
    [TestMethod]
    public async Task VideoAndTextContextsUseTheExplicitExportDevice()
    {
        using var directory = new TempDirectoryFixture("mixtri_export_device_");
        string frames = Path.Combine(directory.Path, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(frames);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using (var image = new CanvasRenderTarget(device, 96, 64, 96))
        {
            using (var drawing = image.CreateDrawingSession())
                drawing.Clear(Color.FromArgb(255, 200, 30, 40));
            await image.SaveAsync(Path.Combine(frames, "frame_00000000.jpg"), CanvasBitmapFileFormat.Jpeg);
        }
        var project = new Project
        {
            VideoFilePath = Path.Combine(directory.Path, "video.mp4"),
            Width = 96, Height = 64, Fps = 5, Duration = TimeSpan.FromSeconds(.2), DpiScale = 1,
        };
        var timeline = new TimelineModel { PrimaryVideoFilePath = project.VideoFilePath, Fps = 5 };
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = project.VideoFilePath, SourceWidth = 96, SourceHeight = 64,
            Duration = project.Duration, SourceDuration = project.Duration, Fps = 5, DpiScale = 1,
        });
        timeline.Segments.Add(new TextSlideSegment { Text = "Isolated", Duration = TimeSpan.FromSeconds(.6) });
        timeline.RecalculateSegmentPositions();
        using var composer = await SegmentFrameComposer.CreateAsync(
            project, new MouseRecordingData(),
            new CompositionConfig { OutputFps = 5, Background = new() { Padding = 0 } },
            timeline, new TimelineMapper(timeline, 5), 5, graphicsDevice: device);
        using var video = await composer.ComposeFrameAsync(0);
        using var text = await composer.ComposeFrameAsync(2);
        Assert.IsTrue(video.Device == device);
        Assert.IsTrue(text.Device == device);
    }

    [TestMethod]
    public void SnapshotDetachesNestedMediaAndEditableSegmentState()
    {
        var project = new Project
        {
            VideoFilePath = @"C:\original.mp4", AudioFilePaths = [@"C:\original.wav"],
            Sources = [new() { VideoFilePath = @"C:\other.mp4", AudioFilePaths = [@"C:\other.wav"] }],
        };
        var video = new VideoSegment
        {
            VideoFilePath = project.VideoFilePath, Duration = TimeSpan.FromSeconds(2),
            AudioFilePaths = [@"C:\segment.wav"], FrameStyleOverride = new() { Padding = 12 },
        };
        var slide = new TextSlideSegment { Text = "Original title", Duration = TimeSpan.FromSeconds(3) };
        var timeline = new TimelineModel { PrimaryVideoFilePath = project.VideoFilePath };
        timeline.Segments.AddRange([video, slide]);
        timeline.ZoomKeyframes.Add(new() { Timestamp = TimeSpan.FromSeconds(1), ZoomLevel = 2, IsManual = true });
        timeline.SuppressedClickTicks.Add(42);
        timeline.RecalculateSegmentPositions();
        var snapshot = ProjectStateSnapshot.Capture(project, new CompositionConfig(), timeline);

        project.VideoFilePath = @"C:\replacement.mp4";
        project.AudioFilePaths.Clear();
        project.Sources[0].AudioFilePaths.Clear();
        video.AudioFilePaths.Clear();
        video.Duration = TimeSpan.FromSeconds(20);
        video.FrameStyleOverride = new() { Padding = 50 };
        slide.Text = "Edited title";
        timeline.ZoomKeyframes.Clear();
        timeline.SuppressedClickTicks.Clear();
        timeline.Segments.Clear();

        Assert.AreEqual(@"C:\original.mp4", snapshot.Project.VideoFilePath);
        CollectionAssert.AreEqual(new[] { @"C:\original.wav" }, snapshot.Project.AudioFilePaths);
        CollectionAssert.AreEqual(new[] { @"C:\other.wav" }, snapshot.Project.Sources[0].AudioFilePaths);
        Assert.IsNotNull(snapshot.Timeline);
        var frozenVideo = snapshot.Timeline.Segments.OfType<VideoSegment>().Single();
        CollectionAssert.AreEqual(new[] { @"C:\segment.wav" }, frozenVideo.AudioFilePaths);
        Assert.AreEqual(TimeSpan.FromSeconds(2), frozenVideo.Duration);
        Assert.AreEqual(12f, frozenVideo.FrameStyleOverride!.Padding);
        Assert.AreEqual("Original title", snapshot.Timeline.Segments.OfType<TextSlideSegment>().Single().Text);
        Assert.AreEqual(1, snapshot.Timeline.ZoomKeyframes.Count);
        Assert.IsTrue(snapshot.Timeline.SuppressedClickTicks.Contains(42));
    }

    [TestMethod]
    public void SnapshotPreservesLegacyNullTimeline()
    {
        var snapshot = ProjectStateSnapshot.Capture(new Project(), new CompositionConfig(), null);
        Assert.IsNull(snapshot.Timeline);
    }

    [TestMethod]
    public async Task GifExportKeepsItsStartingTimelineWhileZoomAndTextAreInserted()
    {
        using var directory = new TempDirectoryFixture("mixtri_live_export_");
        string session = Path.Combine(directory.Path, "session");
        string frames = Path.Combine(session, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(frames);
        using (var image = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 96, 64, 96))
        {
            using (var drawing = image.CreateDrawingSession())
                drawing.Clear(Color.FromArgb(255, 240, 20, 20));
            for (int i = 0; i < 8; i++)
                await image.SaveAsync(Path.Combine(frames, $"frame_{i:D8}.jpg"), CanvasBitmapFileFormat.Jpeg);
        }

        var project = new Project
        {
            Name = "snapshot", VideoFilePath = Path.Combine(session, "video.mp4"),
            Width = 96, Height = 64, Fps = 5, Duration = TimeSpan.FromSeconds(1.6), DpiScale = 1,
        };
        var video = new VideoSegment
        {
            VideoFilePath = project.VideoFilePath, SourceWidth = 96, SourceHeight = 64,
            Fps = 5, DpiScale = 1, SourceDuration = project.Duration, Duration = project.Duration,
        };
        var timeline = new TimelineModel
        {
            PrimaryVideoFilePath = project.VideoFilePath, Duration = project.Duration,
            TrimEnd = project.Duration, Fps = 5,
        };
        timeline.Segments.Add(video);
        int edits = 0;
        var progress = new InlineProgress(_ =>
        {
            if (Interlocked.Exchange(ref edits, 1) != 0) return;
            timeline.ZoomKeyframes.Add(new()
            {
                Timestamp = TimeSpan.FromSeconds(.5), ZoomLevel = 3, IsManual = true,
            });
            timeline.Segments.Insert(0, new TextSlideSegment
            {
                Text = "", BackgroundColor = "#00FF00", Duration = TimeSpan.FromSeconds(2),
            });
            video.Duration = TimeSpan.FromSeconds(4);
            timeline.RecalculateSegmentPositions();
        });
        var engine = new ExportEngine();
        string output = await engine.ExportProjectAsync(
            project,
            new ExportSettings { Format = VideoFormat.GIF, Fps = 5, Resolution = VideoResolution.HD720 },
            new CompositionConfig
            {
                OutputFps = 5, Background = new BackgroundStyle { Padding = 0 },
                Zoom = new AutoZoomConfig { Enabled = false },
            },
            Path.Combine(directory.Path, "output"), timeline, progress);

        Assert.AreEqual(1, edits);
        Assert.AreEqual(2, timeline.Segments.Count, "The live edit must remain applied.");
        Assert.AreEqual(1, timeline.ZoomKeyframes.Count);
        var file = await StorageFile.GetFileFromPathAsync(output);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.AreEqual(8u, decoder.FrameCount, "Export duration must come from the starting timeline.");
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            byte[] pixels = (await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            int center = checked((int)((frame.PixelHeight / 2 * frame.PixelWidth + frame.PixelWidth / 2) * 4));
            Assert.IsTrue(pixels[center + 2] > 180 && pixels[center + 1] < 80,
                $"Frame {i} ({frame.PixelWidth}x{frame.PixelHeight}) is " +
                $"R={pixels[center + 2]}, G={pixels[center + 1]}, B={pixels[center]}, A={pixels[center + 3]} " +
                "instead of the starting video.");
        }
    }

    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
