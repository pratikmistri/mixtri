using Mixtri.Core.AI;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public class ExportSnapshotTests
{
    [TestMethod]
    public void Capture_DetachesNestedMediaTimelineAndComposition()
    {
        var project = new Project
        {
            Name = "Original", VideoFilePath = "original.mp4",
            AudioFilePaths = ["original.wav"],
            Sources = [new RecordingSource { VideoFilePath = "appended.mp4", AudioFilePaths = ["appended.wav"] }],
        };
        var slide = new TextSlideSegment { Text = "Original title", Duration = TimeSpan.FromSeconds(2) };
        var video = new VideoSegment
        {
            VideoFilePath = project.VideoFilePath, Duration = TimeSpan.FromSeconds(2),
            AudioFilePaths = ["segment.wav"],
            FrameStyleOverride = new BackgroundStyle { Color = "#123456" },
        };
        var timeline = TestTimelineBuilder.ModelWithPrimaryPath(project.VideoFilePath, slide, video);
        timeline.ZoomKeyframes.Add(new ZoomKeyframe { ZoomLevel = 2 });
        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { Color = "#ABCDEF" },
            Subtitles = [new SubtitleSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Subtitle")],
        };

        var snapshot = ProjectStateSnapshot.Capture(project, composition, timeline);
        project.Name = "Edited";
        project.AudioFilePaths.Clear();
        project.Sources[0].AudioFilePaths.Clear();
        slide.Text = "Edited title";
        video.AudioFilePaths.Clear();
        timeline.ZoomKeyframes[0] = timeline.ZoomKeyframes[0] with { ZoomLevel = 4 };
        timeline.Segments.Clear();
        composition.Subtitles.Clear();

        Assert.AreEqual("Original", snapshot.Project.Name);
        Assert.AreEqual("original.wav", snapshot.Project.AudioFilePaths.Single());
        Assert.AreEqual("appended.wav", snapshot.Project.Sources.Single().AudioFilePaths.Single());
        Assert.AreEqual("Original title", ((TextSlideSegment)snapshot.Timeline!.Segments[0]).Text);
        Assert.AreEqual("segment.wav", ((VideoSegment)snapshot.Timeline.Segments[1]).AudioFilePaths.Single());
        Assert.AreEqual(2f, snapshot.Timeline.ZoomKeyframes.Single().ZoomLevel);
        Assert.AreEqual("Subtitle", snapshot.Composition.Subtitles!.Single().Text);
        Assert.AreNotSame(composition.Background, snapshot.Composition.Background);
    }

    [TestMethod]
    public void Capture_PreservesLegacyNullTimeline()
    {
        var snapshot = ProjectStateSnapshot.Capture(new Project(), new CompositionConfig(), null);
        Assert.IsNull(snapshot.Timeline);
    }

    [TestMethod]
    public void WorkerProtocol_RoundTripsPolymorphicSnapshotInOneLine()
    {
        var timeline = TestTimelineBuilder.ModelWith(
            new TextSlideSegment { Text = "Title\nSecond line", Duration = TimeSpan.FromSeconds(2) },
            TestTimelineBuilder.Video("video.mp4", 0, 3));
        var snapshot = ProjectStateSnapshot.Capture(new Project(), new CompositionConfig(), timeline);
        var json = ExportWorkerProtocol.Serialize(new ExportWorkerMessage(ExportWorkerMessageKind.Start)
        {
            Snapshot = snapshot, Settings = new ExportSettings { ForceSoftwareRendering = true },
            OutputFolder = @"C:\Output",
        });
        Assert.IsFalse(json.Contains('\n'));
        var restored = ExportWorkerProtocol.Deserialize(json);
        Assert.AreEqual(2, restored.Snapshot!.Timeline!.Segments.Count);
        Assert.AreEqual("Title\nSecond line", ((TextSlideSegment)restored.Snapshot.Timeline.Segments[0]).Text);
        Assert.IsInstanceOfType<VideoSegment>(restored.Snapshot.Timeline.Segments[1]);
        Assert.IsTrue(restored.Settings!.ForceSoftwareRendering);
    }
}
