using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

[TestClass]
public sealed class OwnedSourceTimeMappingTests
{
    [TestMethod]
    public void Mapping_CrossesAdjacentMixedSpeedPiecesPiecewise()
    {
        var first = TestTimelineBuilder.Video("primary.mp4", 0, 2);
        var typing = TestTimelineBuilder.Video("primary.mp4", 2, 3, speed: 1.5);
        var last = TestTimelineBuilder.Video("primary.mp4", 5, 5);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(
            "primary.mp4", first, typing, last);

        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            model.MapSourceTimeFromOwningSegment(typing, TimeSpan.FromSeconds(1)));
        Assert.AreEqual(
            TimeSpan.FromSeconds(10.0 / 3.0),
            model.MapSourceTimeFromOwningSegment(typing, TimeSpan.FromSeconds(4)));
        Assert.AreEqual(
            TimeSpan.FromSeconds(6),
            model.MapSourceTimeFromOwningSegment(typing, TimeSpan.FromSeconds(7)));
    }

    [TestMethod]
    public void Mapping_DoesNotJumpAcrossSlideToAnotherOccurrence()
    {
        var earlier = TestTimelineBuilder.Video("primary.mp4", 0, 5);
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(2) };
        var owner = TestTimelineBuilder.Video("primary.mp4", 5, 5, speed: 2);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(
            "primary.mp4", earlier, slide, owner);

        var mapped = model.MapSourceTimeFromOwningSegment(
            owner, TimeSpan.FromSeconds(4));

        Assert.AreEqual(
            TimeSpan.FromSeconds(6.5),
            mapped,
            "The edge must extrapolate through its owner, not jump to the earlier occurrence.");
    }

    [TestMethod]
    public void Mapping_DoesNotCrossSourceGap()
    {
        var previous = TestTimelineBuilder.Video("primary.mp4", 0, 2);
        var owner = TestTimelineBuilder.Video("primary.mp4", 4, 2, speed: 2);
        var model = TestTimelineBuilder.ModelWithPrimaryPath(
            "primary.mp4", previous, owner);

        var mapped = model.MapSourceTimeFromOwningSegment(
            owner, TimeSpan.FromSeconds(1));

        Assert.AreEqual(
            TimeSpan.FromSeconds(0.5),
            mapped,
            "A source gap breaks the chain, so mapping must remain anchored to the owner.");
    }

    [TestMethod]
    public void Mapping_TraversesContiguousOverlayPiecesAtAuthoredStart()
    {
        var baseVideo = TestTimelineBuilder.Video("primary.mp4", 0, 10);
        var first = TestTimelineBuilder.Video("append.mp4", 0, 2);
        first.TrackIndex = 1;
        first.Start = TimeSpan.FromSeconds(3);
        var typing = TestTimelineBuilder.Video("append.mp4", 2, 3, speed: 1.5);
        typing.TrackIndex = 1;
        typing.Start = first.End;
        var last = TestTimelineBuilder.Video("append.mp4", 5, 2);
        last.TrackIndex = 1;
        last.Start = typing.End;
        var model = TestTimelineBuilder.ModelWithPrimaryPath(
            "primary.mp4", baseVideo, first, typing, last);

        Assert.AreEqual(
            TimeSpan.FromSeconds(8),
            model.MapSourceTimeFromOwningSegment(typing, TimeSpan.FromSeconds(6)));
    }
}
