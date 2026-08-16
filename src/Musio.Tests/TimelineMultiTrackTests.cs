using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

[TestClass]
public sealed class TimelineMultiTrackTests
{
    private const string PrimaryPath = "primary.mp4";

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static VideoSegment Video(double sourceStartSeconds, double durationSeconds) =>
        TestTimelineBuilder.Video(PrimaryPath, sourceStartSeconds, durationSeconds);

    private static TextSlideSegment Slide(double durationSeconds) =>
        new() { Duration = S(durationSeconds) };

    private static TimelineModel ModelWith(params TimelineSegment[] segments) =>
        TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    [TestMethod]
    public void GetSegmentAtTime_PicksTopmostCoveringSegmentAndLaterTie()
    {
        var baseSegment = Video(0, 10);
        var lowerOverlay = Slide(6) with { TrackIndex = 1, Start = S(1) };
        var laterSameTrackOverlay = Slide(4) with { TrackIndex = 1, Start = S(2) };
        var higherOverlay = Slide(2) with { TrackIndex = 2, Start = S(3) };
        var model = ModelWith(baseSegment, lowerOverlay, laterSameTrackOverlay, higherOverlay);

        Assert.AreSame(higherOverlay, model.GetSegmentAtTime(S(3.5)).Segment);
        Assert.AreSame(laterSameTrackOverlay, model.GetSegmentAtTime(S(2.5)).Segment);
    }

    [TestMethod]
    public void GetSegmentAtTime_ExactEndFallbackReturnsLastSegment()
    {
        var first = Video(0, 4);
        var last = Video(4, 3);
        var model = ModelWith(first, last);

        var result = model.GetSegmentAtTime(S(7));

        Assert.AreSame(last, result.Segment);
        Assert.AreEqual(last.Duration, result.LocalOffset);
    }

    [TestMethod]
    public void GetBaseSegmentAtTime_IgnoresOverlays()
    {
        var first = Video(0, 5);
        var second = Video(5, 5);
        var overlay = Slide(3) with { TrackIndex = 1, Start = S(1) };
        var model = ModelWith(first, second, overlay);

        Assert.AreSame(overlay, model.GetSegmentAtTime(S(2)).Segment);
        Assert.AreSame(first, model.GetBaseSegmentAtTime(S(2)).Segment);
    }

    [TestMethod]
    public void RecalculateSegmentPositions_ReflowsBaseOnlyAndLeavesOverlayStartsUntouched()
    {
        var first = Video(0, 3) with { Start = S(99) };
        var overlay = Slide(4) with { TrackIndex = 1, Start = S(8) };
        var second = Video(3, 2) with { Start = S(99) };
        var model = new TimelineModel();
        model.Segments.AddRange([first, overlay, second]);

        model.RecalculateSegmentPositions();

        Assert.AreEqual(TimeSpan.Zero, first.Start);
        Assert.AreEqual(S(3), second.Start);
        Assert.AreEqual(S(8), overlay.Start);
    }

    [TestMethod]
    public void TotalSegmentsDuration_UsesMaxEndAcrossTracks()
    {
        var baseSegment = Video(0, 5);
        var overlay = Slide(2) with { TrackIndex = 1, Start = S(7) };
        var model = ModelWith(baseSegment, overlay);

        Assert.AreEqual(S(9), model.TotalSegmentsDuration);
    }

    [TestMethod]
    public void FindFreeOverlayTrack_ReturnsFirstNonCollidingTrackAndAllowsTouchingRanges()
    {
        var first = Slide(2) with { TrackIndex = 1, Start = S(0) };
        var second = Slide(2) with { TrackIndex = 1, Start = S(4) };
        var model = ModelWith(first, second);

        Assert.AreEqual(1, model.FindFreeOverlayTrack(S(2), S(2)),
            "Half-open ranges that only touch should share the same overlay lane.");
        Assert.AreEqual(2, model.FindFreeOverlayTrack(S(1), S(2)));
    }

    [TestMethod]
    public void VisibleRanges_UncoveredSegmentReturnsFullSpan()
    {
        var segment = Video(0, 10);
        var model = ModelWith(segment);

        AssertRanges(model.VisibleRanges(segment), (S(0), S(10)));
    }

    [TestMethod]
    public void VisibleRanges_FullyCoveredSegmentReturnsEmpty()
    {
        var segment = Video(0, 10);
        var cover = Slide(10) with { TrackIndex = 1, Start = S(0) };
        var model = ModelWith(segment, cover);

        Assert.AreEqual(0, model.VisibleRanges(segment).Count);
    }

    [TestMethod]
    public void VisibleRanges_PartiallyCoveredSegmentReturnsRemainingSubRanges()
    {
        var segment = Video(0, 10);
        var cover = Slide(2) with { TrackIndex = 1, Start = S(2) };
        var model = ModelWith(segment, cover);

        AssertRanges(model.VisibleRanges(segment), (S(0), S(2)), (S(4), S(10)));
    }

    /// <summary>
    /// Overlapping higher-track covers must be merged before subtraction; otherwise the base
    /// clip can appear to re-emerge between two covers that are visually one continuous mask.
    /// </summary>
    [TestMethod]
    public void VisibleRanges_MultipleOverlappingCoversCoalesceBeforeSubtraction()
    {
        var segment = Video(0, 10);
        var firstCover = Slide(4) with { TrackIndex = 1, Start = S(2) };
        var overlappingCover = Slide(4) with { TrackIndex = 2, Start = S(4) };
        var model = ModelWith(segment, firstCover, overlappingCover);

        AssertRanges(model.VisibleRanges(segment), (S(0), S(2)), (S(8), S(10)));
    }

    /// <summary>
    /// Pure base-track edits are the compatibility floor: the new track model must leave the
    /// historical contiguous lookup, duration, and slide-adjacent transition fallback intact.
    /// </summary>
    [TestMethod]
    public void PureBaseTrackTimeline_BehavesLikeLegacySegmentChain()
    {
        var slide = Slide(2);
        var video = Video(0, 3);
        var model = ModelWith(slide, video);

        Assert.AreSame(slide, model.GetSegmentAtTime(S(1)).Segment);
        Assert.AreSame(video, model.GetSegmentAtTime(S(2.1)).Segment);
        Assert.AreEqual(S(5), model.TotalSegmentsDuration);

        var transition = TransitionResolver.Resolve(model, S(2.1));
        Assert.IsTrue(transition.Active);
        Assert.AreEqual(TransitionType.CrossFade, transition.Type);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), transition.Duration);
    }

    private static void AssertRanges(
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> actual,
        params (TimeSpan Start, TimeSpan End)[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count, "range count");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].Start, actual[i].Start, $"range {i} start");
            Assert.AreEqual(expected[i].End, actual[i].End, $"range {i} end");
        }
    }
}
