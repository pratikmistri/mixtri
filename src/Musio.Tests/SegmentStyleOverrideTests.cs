using Musio.Core.Processing;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Verifies that per-segment style overrides (frame style + cursor) are carried on
/// <see cref="VideoSegment"/> and preserved through split/trim operations (which
/// clone via record `with`). Frame style and cursor are per-segment; aspect ratio,
/// fit/cover mode, and zoom scope remain global (on CompositionConfig) and are not
/// stored on the segment.
/// </summary>
[TestClass]
public sealed class SegmentStyleOverrideTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment StyledVideo()
        => new()
        {
            VideoFilePath = PrimaryPath,
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(10),
            FrameStyleOverride = new BackgroundStyle { Padding = 64 },
            CursorStyleOverride = new CursorStyle { Scale = 2.5f },
        };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = PrimaryPath };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    [TestMethod]
    public void VideoSegment_DefaultOverrides_AreNull()
    {
        var seg = new VideoSegment();
        Assert.IsNull(seg.FrameStyleOverride);
        Assert.IsNull(seg.CursorStyleOverride);
    }

    [TestMethod]
    public void VideoSegment_StoresOverrides()
    {
        var seg = StyledVideo();
        Assert.AreEqual(64, seg.FrameStyleOverride!.Padding);
        Assert.AreEqual(2.5f, seg.CursorStyleOverride!.Scale);
    }

    [TestMethod]
    public void Split_PreservesStyleOverridesOnBothHalves()
    {
        var model = ModelWith(StyledVideo());

        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)).Execute(model);

        Assert.AreEqual(2, model.Segments.Count);
        foreach (var s in model.Segments.OfType<VideoSegment>())
        {
            Assert.IsNotNull(s.FrameStyleOverride, "Split half should keep frame style override");
            Assert.AreEqual(64, s.FrameStyleOverride!.Padding);
            Assert.AreEqual(2.5f, s.CursorStyleOverride!.Scale);
        }
    }

    [TestMethod]
    public void Trim_PreservesStyleOverrides()
    {
        var model = ModelWith(StyledVideo());

        new TrimSegmentEdgeOperation(model.Segments[0].Id, fromStart: false, TimeSpan.FromSeconds(6))
            .Execute(model);

        var seg = (VideoSegment)model.Segments[0];
        Assert.AreEqual(64, seg.FrameStyleOverride!.Padding);
        Assert.AreEqual(2.5f, seg.CursorStyleOverride!.Scale);
    }

    [TestMethod]
    public void Move_PreservesStyleOverrides()
    {
        var a = StyledVideo();
        var b = new VideoSegment
        {
            VideoFilePath = PrimaryPath,
            SourceStart = TimeSpan.FromSeconds(10),
            SourceDuration = TimeSpan.FromSeconds(5),
            Duration = TimeSpan.FromSeconds(5),
        };
        var model = ModelWith(a, b);

        new MoveSegmentOperation(a.Id, 2).Execute(model);

        var moved = model.Segments.OfType<VideoSegment>().First(s => s.Id == a.Id);
        Assert.AreEqual(64, moved.FrameStyleOverride!.Padding);
        Assert.AreEqual(2.5f, moved.CursorStyleOverride!.Scale);
    }
}
