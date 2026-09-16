using System.Reflection;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class ExportSourceIdentityTests
{
    [TestMethod]
    public void ContextIdentityIncludesEveryEffectiveSourceInput()
    {
        var project = new Project { VideoFilePath = @"C:\primary.mp4", Fps = 10 };
        var config = new CompositionConfig();
        var segment = new VideoSegment
        {
            VideoFilePath = @"C:\other.mp4", CursorDataFilePath = @"C:\a.mcur",
            WebcamFilePath = @"C:\a-camera.mp4", SourceWidth = 192, SourceHeight = 108,
            SourceDuration = TimeSpan.FromSeconds(2), Fps = 10, DpiScale = 1,
        };
        var original = SegmentFrameComposer.CreateSourceKey(project, config, null, 10, segment);
        VideoSegment[] variants =
        [
            segment with { VideoFilePath = @"C:\different.mp4" },
            segment with { CursorDataFilePath = @"C:\b.mcur" },
            segment with { WebcamFilePath = @"C:\b-camera.mp4" },
            segment with { SourceWidth = 384 },
            segment with { SourceHeight = 216 },
            segment with { SourceDuration = TimeSpan.FromSeconds(3) },
            segment with { MouseToVideoOffsetSeconds = .25 },
            segment with { CropOffsetX = 12 },
            segment with { CropOffsetY = -6 },
            segment with { DpiScale = 2 },
            segment with { Fps = 20 },
            segment with { FrameStyleOverride = config.Background with { Padding = 23 } },
            segment with { CursorStyleOverride = config.Cursor with { Scale = 2 } },
        ];
        foreach (var variant in variants)
            Assert.AreNotEqual(original, SegmentFrameComposer.CreateSourceKey(project, config, null, 10, variant));
        Assert.AreEqual(original, SegmentFrameComposer.CreateSourceKey(project, config, null, 10, segment with
        {
            VideoFilePath = segment.VideoFilePath.ToUpperInvariant(),
            CursorDataFilePath = segment.CursorDataFilePath.ToUpperInvariant(),
            WebcamFilePath = segment.WebcamFilePath.ToUpperInvariant(),
        }));
    }

    [TestMethod]
    public void PrimarySegmentsInheritDefaultsButHonorExplicitAuxiliarySources()
    {
        var project = new Project
        {
            VideoFilePath = @"C:\primary.mp4", CursorDataFilePath = @"C:\primary.mcur",
            WebcamFilePath = @"C:\primary-camera.mp4", Width = 192, Height = 108,
            Fps = 10, Duration = TimeSpan.FromSeconds(2), MouseToVideoOffsetSeconds = .1, DpiScale = 1,
        };
        var config = new CompositionConfig();
        var segment = new VideoSegment { VideoFilePath = project.VideoFilePath };
        var primary = SegmentFrameComposer.CreateSourceKey(project, config, null, 10);
        Assert.AreEqual(primary, SegmentFrameComposer.CreateSourceKey(project, config, null, 10, segment));

        var custom = SegmentFrameComposer.CreateSourceKey(project, config, null, 10, segment with
        {
            CursorDataFilePath = @"C:\other.mcur", WebcamFilePath = @"C:\other-camera.mp4",
            MouseToVideoOffsetSeconds = .2, DpiScale = 2, CropOffsetX = 7,
        });
        Assert.IsTrue(custom.IsPrimaryVideo);
        Assert.IsFalse(custom.UsePrimaryMouseData);
        Assert.AreEqual(@"c:\other.mcur", custom.CursorPath);
        Assert.AreEqual(@"c:\other-camera.mp4", custom.WebcamPath);
        Assert.AreEqual(.2, custom.MouseOffset);
        Assert.AreEqual(2f, custom.DpiScale);
        Assert.AreEqual(7, custom.CropOffsetX);
        Assert.AreNotEqual(primary, custom);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CursorVariantsRenderIndependently_AndRetireByTheirOwnLastUse(bool primaryVideo)
    {
        using var directory = new TempDirectoryFixture("mixtri_export_identity_");
        string primary = await WriteFramesAsync(Path.Combine(directory.Path, "primary"));
        string video = primaryVideo ? primary : await WriteFramesAsync(Path.Combine(directory.Path, "other"));
        string cursorA = Path.Combine(directory.Path, "a.mcur");
        string cursorB = Path.Combine(directory.Path, "b.mcur");
        var mouseA = TestMouseRecordingBuilder.WithPositions(200, 100, _ => (30, 30));
        var mouseB = TestMouseRecordingBuilder.WithPositions(200, 100, _ => (125, 65));
        var save = typeof(MouseHookRecorder).GetMethod("SaveDataToFile", BindingFlags.Static | BindingFlags.NonPublic)!;
        save.Invoke(null, [cursorA, mouseA]);
        save.Invoke(null, [cursorB, mouseB]);
        Project ProjectFor(string cursor) => new()
        {
            VideoFilePath = primary, CursorDataFilePath = cursor,
            Width = 192, Height = 108, Fps = 10, Duration = TimeSpan.FromSeconds(2), DpiScale = 1,
        };
        var timeline = new TimelineModel { PrimaryVideoFilePath = primary, Fps = 10 };
        var first = new VideoSegment
        {
            VideoFilePath = video, CursorDataFilePath = cursorA, SourceWidth = 192, SourceHeight = 108,
            Fps = 10, DpiScale = 1, Duration = TimeSpan.FromSeconds(1), SourceDuration = TimeSpan.FromSeconds(1),
        };
        timeline.Segments.Add(first);
        timeline.Segments.Add(first with { Id = Guid.NewGuid().ToString(), CursorDataFilePath = cursorB, SourceStart = TimeSpan.FromSeconds(1) });
        timeline.Segments.Add(new TextSlideSegment { Text = "End", Duration = TimeSpan.FromSeconds(2) });
        timeline.RecalculateSegmentPositions();
        var config = new CompositionConfig
        {
            OutputFps = 10, Zoom = new AutoZoomConfig { Enabled = false },
            Cursor = new CursorStyle { AutoHideEnabled = false, TiltEnabled = false, Scale = 1 },
        };
        using var composer = await SegmentFrameComposer.CreateAsync(
            ProjectFor(cursorA), mouseA, config, timeline, new TimelineMapper(timeline, 10), 10);
        byte[] firstPixels;
        using (var frame = await composer.ComposeFrameAsync(0)) firstPixels = frame.GetPixelBytes();
        using var second = await composer.ComposeFrameAsync(12);
        Assert.AreEqual(2, composer.ActiveContextCount, "Different cursor sources must not share a context.");
        using var reference = await SegmentFrameComposer.CreateAsync(
            ProjectFor(cursorB), mouseB, config, timeline, new TimelineMapper(timeline, 10), 10);
        using var expected = await reference.ComposeFrameAsync(12);
        CollectionAssert.AreEqual(expected.GetPixelBytes(), second.GetPixelBytes());
        using (var later = await composer.ComposeFrameAsync(17)) Assert.IsNotNull(later);
        Assert.AreEqual(1, composer.ActiveContextCount, "The first cursor variant must retire independently.");
        using (var slide = await composer.ComposeFrameAsync(32)) Assert.IsNotNull(slide);
        Assert.AreEqual(0, composer.ActiveContextCount);
        using var reopened = await composer.ComposeFrameAsync(0);
        CollectionAssert.AreEqual(firstPixels, reopened.GetPixelBytes());
        Assert.AreEqual(1, composer.ActiveContextCount);
    }

    private static async Task<string> WriteFramesAsync(string folder)
    {
        string frames = Path.Combine(folder, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(frames);
        using var frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 192, 108, 96);
        using (var drawing = frame.CreateDrawingSession())
            drawing.Clear(Color.FromArgb(255, 70, 90, 110));
        for (int i = 0; i < 20; i++)
            await frame.SaveAsync(Path.Combine(frames, $"frame_{i:D8}.jpg"), CanvasBitmapFileFormat.Jpeg);
        return Path.Combine(folder, "video.mp4");
    }
}
