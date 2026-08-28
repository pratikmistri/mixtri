namespace Musio.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Musio.Core.Timeline;

/// <summary>
/// Pins the lane-exclusivity invariant: two full-frame segments may share an instant only by
/// sitting on DIFFERENT tracks, where the higher one covers the lower. Dropping a segment onto
/// time already claimed on the SAME track used to write straight through, leaving two segments
/// stacked in one row — the later one visually swallowed the other, and neither the model nor
/// the timeline gave any sign that a clip had been buried.
/// </summary>
[TestClass]
public sealed class SegmentTrackOverlapTests
{
    private static VideoSegment Video(int startSeconds, int durationSeconds, int track = 0) => new()
    {
        VideoFilePath = "video.mp4",
        Start = TimeSpan.FromSeconds(startSeconds),
        Duration = TimeSpan.FromSeconds(durationSeconds),
        SourceStart = TimeSpan.Zero,
        SourceDuration = TimeSpan.FromSeconds(durationSeconds),
        TrackIndex = track,
    };

    /// <summary>Asserts no two segments sharing a track intersect in time.</summary>
    private static void AssertNoOverlapWithinAnyTrack(TimelineModel model)
    {
        var segments = model.Segments;
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (segments[i].TrackIndex != segments[j].TrackIndex) continue;
                Assert.IsFalse(
                    segments[i].Start < segments[j].End && segments[j].Start < segments[i].End,
                    $"Segments on track {segments[i].TrackIndex} overlap: " +
                    $"[{segments[i].Start}, {segments[i].End}) and [{segments[j].Start}, {segments[j].End}).");
            }
        }
    }

    [TestMethod]
    public void MoveSegmentOnTrack_OntoAnOccupiedLane_DoesNotOverlapTheSegmentAlreadyThere()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 30));
        var resident = Video(0, 10, track: 1);
        var moving = Video(0, 5, track: 2);
        model.Segments.Add(resident);
        model.Segments.Add(moving);

        // Aim squarely at the middle of the resident clip.
        new MoveSegmentOnTrackOperation(moving.Id, TimeSpan.FromSeconds(5), 1).Execute(model);

        Assert.AreEqual(1, moving.TrackIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(10), moving.Start,
            "The drop should settle against the resident clip's out-point, not on top of it.");
        AssertNoOverlapWithinAnyTrack(model);
    }

    [TestMethod]
    public void MoveSegmentOnTrack_OntoFreeTimeOnAnOccupiedLane_KeepsTheRequestedStart()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 30));
        model.Segments.Add(Video(0, 5, track: 1));
        var moving = Video(0, 4, track: 2);
        model.Segments.Add(moving);

        new MoveSegmentOnTrackOperation(moving.Id, TimeSpan.FromSeconds(12), 1).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(12), moving.Start,
            "A drop into open space must not be nudged.");
        AssertNoOverlapWithinAnyTrack(model);
    }

    [TestMethod]
    public void MoveSegmentOnTrack_DraggedAlongItsOwnLaneIntoANeighbour_SettlesBesideIt()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 30));
        var neighbour = Video(10, 6, track: 1);
        var moving = Video(0, 4, track: 1);
        model.Segments.Add(neighbour);
        model.Segments.Add(moving);

        new MoveSegmentOnTrackOperation(moving.Id, TimeSpan.FromSeconds(11), 1).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(6), moving.Start,
            "The nearer free gap is the one in front of the neighbour.");
        AssertNoOverlapWithinAnyTrack(model);
    }

    [TestMethod]
    public void MoveSegmentOnTrack_FromTheBaseChainOntoAnOccupiedLane_DoesNotOverlap()
    {
        var model = new TimelineModel();
        var first = Video(0, 8);
        var second = Video(8, 6);
        model.Segments.Add(first);
        model.Segments.Add(second);
        model.Segments.Add(Video(6, 12, track: 1));

        new MoveSegmentOnTrackOperation(second.Id, TimeSpan.FromSeconds(8), 1).Execute(model);

        Assert.AreEqual(1, second.TrackIndex);
        AssertNoOverlapWithinAnyTrack(model);
        Assert.AreEqual(TimeSpan.Zero, first.Start, "The base chain still reflows behind the departing segment.");
    }

    [TestMethod]
    public void MoveSegmentOnTrack_ResolvedAwayFromTheDrop_IsStillUndoable()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 30));
        model.Segments.Add(Video(0, 10, track: 1));
        var moving = Video(3, 5, track: 2);
        model.Segments.Add(moving);

        var op = new MoveSegmentOnTrackOperation(moving.Id, TimeSpan.FromSeconds(4), 1);
        op.Execute(model);
        op.Undo(model);

        var restored = model.Segments.Single(s => s.Id == moving.Id);
        Assert.AreEqual(2, restored.TrackIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(3), restored.Start);
        AssertNoOverlapWithinAnyTrack(model);
    }

    [TestMethod]
    public void ResolveNonOverlappingStart_PicksTheNearerOfTwoCandidateGaps()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 60));
        model.Segments.Add(Video(20, 10, track: 1));
        var moving = Video(0, 5, track: 2);
        model.Segments.Add(moving);

        // 25s sits inside [20, 30). The gap before it can host 5s (ending at 20 => start 15,
        // 10s away); the tail after it starts at 30 (5s away) and wins.
        Assert.AreEqual(
            TimeSpan.FromSeconds(30),
            model.ResolveNonOverlappingStart(moving, 1, TimeSpan.FromSeconds(25)));

        // Nudged towards the head, the gap in front becomes the nearer landing spot.
        Assert.AreEqual(
            TimeSpan.FromSeconds(15),
            model.ResolveNonOverlappingStart(moving, 1, TimeSpan.FromSeconds(21)));
    }

    [TestMethod]
    public void ResolveNonOverlappingStart_SkipsAGapTooSmallToHostTheSegment()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 60));
        model.Segments.Add(Video(0, 10, track: 1));
        model.Segments.Add(Video(12, 10, track: 1));
        var moving = Video(0, 5, track: 2);
        model.Segments.Add(moving);

        // The 2s hole between the two residents cannot host a 5s segment.
        Assert.AreEqual(
            TimeSpan.FromSeconds(22),
            model.ResolveNonOverlappingStart(moving, 1, TimeSpan.FromSeconds(11)));
    }

    [TestMethod]
    public void InsertSegmentOnOverlayTrack_WithAnExplicitOccupiedLane_DoesNotOverlap()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 30));
        model.Segments.Add(Video(0, 10, track: 1));

        var inserted = Video(0, 4, track: 0);
        new InsertSegmentOnOverlayTrackOperation(inserted, TimeSpan.FromSeconds(5), trackIndex: 1)
            .Execute(model);

        Assert.AreEqual(1, inserted.TrackIndex);
        AssertNoOverlapWithinAnyTrack(model);
    }

    [TestMethod]
    public void TrackRangeIsFree_IgnoresTheSegmentBeingAskedAbout()
    {
        var model = new TimelineModel();
        var resident = Video(4, 6, track: 1);
        model.Segments.Add(resident);

        Assert.IsFalse(model.TrackRangeIsFree(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)));
        Assert.IsTrue(model.TrackRangeIsFree(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), resident));
        Assert.IsTrue(model.TrackRangeIsFree(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)));

        // Touching endpoints are not an overlap, so adjacent segments may abut exactly.
        Assert.IsTrue(model.TrackRangeIsFree(1, TimeSpan.Zero, TimeSpan.FromSeconds(4)));
        Assert.IsTrue(model.TrackRangeIsFree(1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14)));
    }
}
