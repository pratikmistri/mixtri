namespace Musio.Tests;

using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

[TestClass]
public sealed class SlideTransitionsTests
{
    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWith(segments);

    private static VideoSegment Video(double durSec) => TestTimelineBuilder.TransitionVideo("C:\\primary.mp4", durSec);

    private static TextSlideSegment Slide(double durSec) =>
        new() { Duration = TimeSpan.FromSeconds(durSec) };

    [TestMethod]
    public void Resolve_AtLeadingEdgeOfSlide_AfterVideo_IsActive()
    {
        // video [0,4) | slide [4,9)
        var model = ModelWith(Video(4), Slide(5));

        // 0.25s into the slide → halfway through a 0.5s crossfade.
        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(4.25));

        Assert.IsTrue(r.Active);
        Assert.AreEqual(0.5, r.Progress, 1e-6);
        // Outgoing held just before the boundary (within the video segment).
        Assert.IsTrue(r.OutgoingTime < TimeSpan.FromSeconds(4));
        Assert.IsTrue(r.OutgoingTime >= TimeSpan.Zero);
    }

    [TestMethod]
    public void Resolve_AtLeadingEdgeOfVideo_AfterSlide_IsActive()
    {
        // slide [0,5) | video [5,9)
        var model = ModelWith(Slide(5), Video(4));

        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(5.1));

        Assert.IsTrue(r.Active);
        Assert.AreEqual(0.2, r.Progress, 1e-6);
    }

    [TestMethod]
    public void Resolve_PastTransitionWindow_IsInactive()
    {
        var model = ModelWith(Video(4), Slide(5));

        // 1s into the slide — well past the 0.5s window.
        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(5.0));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_FirstSegment_HasNoTransition()
    {
        var model = ModelWith(Slide(5), Video(4));

        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(0.1));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_BoundaryBetweenTwoVideos_HasNoTransition()
    {
        // No text slide involved → no automatic crossfade.
        var model = ModelWith(Video(4), Video(4));

        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(4.1));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_ClampsWindowToHalfOfShorterNeighbour()
    {
        // Slide is only 0.4s, so the window is clamped to 0.2s (half of it).
        var model = ModelWith(Video(4), Slide(0.4));

        // 0.1s in → halfway through the clamped 0.2s window.
        var inside = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(4.1));
        Assert.IsTrue(inside.Active);
        Assert.AreEqual(0.5, inside.Progress, 1e-6);

        // 0.3s in → past the clamped window.
        var outside = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(4.3));
        Assert.IsFalse(outside.Active);
    }

    [TestMethod]
    public void Resolve_NoSegments_IsInactive()
    {
        var model = new TimelineModel();
        var r = SlideTransitions.Resolve(model, TimeSpan.FromSeconds(1));
        Assert.IsFalse(r.Active);
    }
}
