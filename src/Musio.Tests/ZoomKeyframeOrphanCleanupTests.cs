using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

/// <summary>
/// A zoom keyframe is anchored to a SOURCE time and finds its place on the output timeline
/// through whichever video segment shows that footage. Deleting the last segment that showed
/// one used to leave it behind in <see cref="TimelineModel.ZoomKeyframes"/>, where the zoom
/// track fell back to another segment cut from the same recording and extrapolated a position
/// outside that segment's range — a "ghost" zoom block over unrelated footage.
/// </summary>
[TestClass]
public sealed class ZoomKeyframeOrphanCleanupTests
{
    private const string PrimaryPath = "primary.mp4";
    private const string AppendedPath = "appended.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, string path = PrimaryPath)
        => TestTimelineBuilder.Video(path, srcStartSec, srcDurSec);

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    /// <summary>A manual zoom over the given SOURCE range of <paramref name="sourcePath"/>.</summary>
    private static ZoomKeyframe Zoom(double startSec, double endSec, string? sourcePath = null)
    {
        var kf = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec), 2.0);
        return kf with { SourceVideoFilePath = sourcePath };
    }

    // ── The reported bug ──

    [TestMethod]
    public void RemoveSegment_DropsTheZoomOnlyThatSegmentShowed()
    {
        // Head of the recording plays on the timeline; the tail plays as a second clip.
        var head = Video(0, 5);
        var tail = Video(5, 5);
        var model = ModelWith(head, tail);
        model.ZoomKeyframes.Add(Zoom(6, 8)); // lives in the tail's source range only

        new RemoveSegmentOperation(tail.Id).Execute(model);

        Assert.AreEqual(0, model.ZoomKeyframes.Count,
            "No remaining clip shows source 6s-8s, so the keyframe has nowhere to live");
    }

    [TestMethod]
    public void RemoveSegment_KeepsAZoomAnotherClipStillShows()
    {
        // The same source range appears twice (a duplicated take).
        var first = Video(5, 5);
        var second = Video(5, 5);
        var model = ModelWith(first, second);
        model.ZoomKeyframes.Add(Zoom(6, 8));

        new RemoveSegmentOperation(second.Id).Execute(model);

        Assert.AreEqual(1, model.ZoomKeyframes.Count,
            "The surviving occurrence still shows that footage");
    }

    [TestMethod]
    public void RemoveSegment_LeavesZoomsItNeverShowedAlone()
    {
        var head = Video(0, 5);
        var tail = Video(5, 5);
        var model = ModelWith(head, tail);
        var keptZoom = Zoom(1, 3); // in the head's range
        model.ZoomKeyframes.Add(keptZoom);

        new RemoveSegmentOperation(tail.Id).Execute(model);

        CollectionAssert.AreEqual(new[] { keptZoom }, model.ZoomKeyframes);
    }

    [TestMethod]
    public void RemoveSegment_OnlyTouchesZoomsOfItsOwnRecording()
    {
        var primary = Video(0, 5);
        var appended = Video(0, 5, AppendedPath);
        var model = ModelWith(primary, appended);
        var appendedZoom = Zoom(1, 3, AppendedPath);
        model.ZoomKeyframes.Add(appendedZoom);

        // Removing the PRIMARY clip must not disturb a keyframe authored against the
        // appended recording, whose own clip is still on the timeline.
        new RemoveSegmentOperation(primary.Id).Execute(model);

        CollectionAssert.AreEqual(new[] { appendedZoom }, model.ZoomKeyframes);
    }

    [TestMethod]
    public void RemoveTextSlide_LeavesZoomKeyframesAlone()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) };
        var video = Video(0, 5);
        var model = ModelWith(slide, video);
        var zoom = Zoom(1, 3);
        model.ZoomKeyframes.Add(zoom);

        new RemoveSegmentOperation(slide.Id).Execute(model);

        CollectionAssert.AreEqual(new[] { zoom }, model.ZoomKeyframes);
    }

    // ── Undo / redo ──

    [TestMethod]
    public void Undo_RestoresTheOrphanedZoomAtItsOriginalIndex()
    {
        var head = Video(0, 5);
        var tail = Video(5, 5);
        var model = ModelWith(head, tail);
        var headZoom = Zoom(1, 3);
        var tailZoom = Zoom(6, 8);
        model.ZoomKeyframes.Add(headZoom);
        model.ZoomKeyframes.Add(tailZoom);

        var op = new RemoveSegmentOperation(tail.Id);
        op.Execute(model);
        Assert.AreEqual(1, model.ZoomKeyframes.Count);

        op.Undo(model);

        CollectionAssert.AreEqual(new[] { headZoom, tailZoom }, model.ZoomKeyframes,
            "Undo must put the clip AND the zoom it carried back, in order");
        Assert.AreEqual(2, model.Segments.Count);
    }

    [TestMethod]
    public void Redo_RemovesTheOrphanedZoomAgainWithoutDuplicating()
    {
        var head = Video(0, 5);
        var tail = Video(5, 5);
        var model = ModelWith(head, tail);
        model.ZoomKeyframes.Add(Zoom(6, 8));

        var op = new RemoveSegmentOperation(tail.Id);
        op.Execute(model);
        op.Undo(model);
        op.Execute(model);

        Assert.AreEqual(0, model.ZoomKeyframes.Count);

        op.Undo(model);
        Assert.AreEqual(1, model.ZoomKeyframes.Count, "A second undo must not restore duplicates");
    }

    // ── The shared "is this still shown anywhere" rule ──

    [TestMethod]
    public void IsZoomKeyframeShown_TrueWhenSpanOverlapsAKeptRange()
    {
        var model = ModelWith(Video(0, 5));

        // Starts before the clip's source range but reaches into it.
        Assert.IsTrue(model.IsZoomKeyframeShown(Zoom(4, 7)));
    }

    [TestMethod]
    public void IsZoomKeyframeShown_FalseWhenSpanOnlyAbutsAKeptRange()
    {
        var model = ModelWith(Video(0, 5));

        Assert.IsFalse(model.IsZoomKeyframeShown(Zoom(5, 8)),
            "Touching the out-point shows none of the keyframe");
    }

    [TestMethod]
    public void SourceSpanIntersectsSegment_DegenerateSpanIsTestedAsAPoint()
    {
        var model = ModelWith(Video(2, 5));
        var segment = (VideoSegment)model.Segments[0];

        // The text-overlay create preview maps a zero-duration probe through this.
        Assert.IsTrue(model.SourceSpanIntersectsSegment(
            null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), segment));
        Assert.IsFalse(model.SourceSpanIntersectsSegment(
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), segment));
    }

    [TestMethod]
    public void SourceMatchesSegment_NullSourceMeansThePrimaryRecording()
    {
        var model = ModelWith(Video(0, 5), Video(0, 5, AppendedPath));
        var primary = (VideoSegment)model.Segments[0];
        var appended = (VideoSegment)model.Segments[1];

        Assert.IsTrue(model.SourceMatchesSegment(null, primary));
        Assert.IsFalse(model.SourceMatchesSegment(null, appended));
        Assert.IsTrue(model.SourceMatchesSegment(AppendedPath, appended));
    }
}
