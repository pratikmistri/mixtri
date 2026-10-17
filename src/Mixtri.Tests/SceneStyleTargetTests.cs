using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public class SceneStyleTargetTests
{
    private static VideoSegment Video(string path) => new()
    {
        VideoFilePath = path,
        Duration = TimeSpan.FromSeconds(5),
        SourceDuration = TimeSpan.FromSeconds(5),
        FrameStyleOverride = new BackgroundStyle { Color = "#123456" },
    };

    [TestMethod]
    public void DeselectedClipWithOverrideReceivesSceneEdit()
    {
        var video = Video("primary.mp4");
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", video);
        var target = SceneStyleTarget.Resolve(model, null, TimeSpan.FromSeconds(2));
        Assert.AreSame(video, target);
        target!.FrameStyleOverride = new BackgroundStyle { Color = "#ABCDEF" };
        Assert.AreEqual("#ABCDEF", video.FrameStyleOverride.Color);
    }

    [TestMethod]
    public void SelectedClipTakesPrecedenceOverPlayhead()
    {
        var first = Video("primary.mp4");
        var second = Video("appended.mp4");
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", first, second);
        Assert.AreSame(second, SceneStyleTarget.Resolve(model, second.Id, TimeSpan.FromSeconds(2)));
        Assert.AreSame(second, SceneStyleTarget.Resolve(model, null, TimeSpan.FromSeconds(7)));
    }

    [TestMethod]
    public void TitleSlideAndEmptyTimelineKeepGlobalSceneTarget()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(5) };
        var model = TestTimelineBuilder.ModelWithPrimaryPath("primary.mp4", slide);
        Assert.IsNull(SceneStyleTarget.Resolve(model, null, TimeSpan.FromSeconds(2)));
        Assert.IsNull(SceneStyleTarget.Resolve(new TimelineModel(), null, TimeSpan.Zero));
    }
}
