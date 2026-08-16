namespace Musio.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Musio.Core.Timeline;

/// <summary>
/// Pins the four defects found in the code review of the multi-track / text-slide-window
/// work. Each of these was reachable through ordinary editing, and each was invisible to
/// the feature's own tests because those only exercised a pure base-track timeline — the
/// shape where list order, max-End and sum-of-durations all happen to agree.
/// </summary>
[TestClass]
public sealed class MultiTrackReviewRegressionTests
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

    private static TextSlideSegment Slide(int startSeconds, int durationSeconds, int track = 0) => new()
    {
        Start = TimeSpan.FromSeconds(startSeconds),
        Duration = TimeSpan.FromSeconds(durationSeconds),
        TrackIndex = track,
    };

    // ── Finding 3: the exact-end fallback used list position, not the latest end ──

    /// <summary>
    /// An overlay insert appends to <c>Segments</c>, so the list tail is routinely a short
    /// clip sitting in the middle of the timeline. Resolving the very end of the timeline
    /// by list position therefore returned that middle clip, and everything that asks
    /// "what is under the playhead" at the end — preview, per-segment frame style, the
    /// source-time lookups — silently answered about the wrong segment.
    /// </summary>
    [TestMethod]
    public void GetSegmentAtTime_PastTheEnd_ResolvesTheLatestEndingSegment_NotTheListTail()
    {
        var model = new TimelineModel();
        var baseClip = Video(0, 10);
        var overlay = Slide(3, 3, track: 1);
        model.Segments.Add(baseClip);
        model.Segments.Add(overlay); // list tail, but ends at 6s

        Assert.AreEqual(TimeSpan.FromSeconds(10), model.TotalSegmentsDuration);

        var (segment, offset) = model.GetSegmentAtTime(TimeSpan.FromSeconds(10));

        Assert.AreSame(baseClip, segment, "The end of the timeline must resolve to the clip that ends there.");
        Assert.AreEqual(baseClip.Duration, offset);
    }

    /// <summary>Two segments ending together tie-break to the higher track, matching the in-range rule.</summary>
    [TestMethod]
    public void GetSegmentAtTime_PastTheEnd_TieBreaksToTheHigherTrack()
    {
        var model = new TimelineModel();
        var baseClip = Video(0, 5);
        var overlay = Slide(0, 5, track: 2);
        model.Segments.Add(overlay);
        model.Segments.Add(baseClip);

        var (segment, _) = model.GetSegmentAtTime(TimeSpan.FromSeconds(9));

        Assert.AreSame(overlay, segment);
    }

    // ── Finding 2: an overlay dragged past the end left frames nothing covers ──

    /// <summary>
    /// The export frame count comes from <see cref="TimelineModel.TotalSegmentsDuration"/>
    /// (max-End), so an overlay starting after everything else finishes would stretch the
    /// timeline over a hole that no segment covers — and every frame in that hole has
    /// nothing to render. The move clamps instead, keeping "fully covered from 0 to the
    /// duration" an invariant rather than a per-consumer defence.
    /// </summary>
    [TestMethod]
    public void MoveSegmentOnTrack_CannotDragAnOverlayPastTheEndAndLeaveAHole()
    {
        var model = new TimelineModel();
        var baseClip = Video(0, 10);
        var overlay = Slide(2, 3, track: 1);
        model.Segments.Add(baseClip);
        model.Segments.Add(overlay);

        new MoveSegmentOnTrackOperation(overlay.Id, TimeSpan.FromSeconds(40), 1).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(10), overlay.Start,
            "The overlay should pin to the end of the rest of the timeline rather than open a gap.");

        // Every instant from zero to the timeline duration now resolves to a real segment.
        for (var t = TimeSpan.Zero; t < model.TotalSegmentsDuration; t += TimeSpan.FromMilliseconds(250))
            Assert.IsNotNull(model.GetSegmentAtTime(t).Segment, $"No segment covers {t}.");
    }

    /// <summary>A move that does not run past the end is left exactly where the user put it.</summary>
    [TestMethod]
    public void MoveSegmentOnTrack_WithinTheTimeline_KeepsTheRequestedStart()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 10));
        var overlay = Slide(2, 3, track: 1);
        model.Segments.Add(overlay);

        new MoveSegmentOnTrackOperation(overlay.Id, TimeSpan.FromSeconds(6), 1).Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(6), overlay.Start);
    }

    /// <summary>The clamp must not change behaviour for a pure base-track timeline.</summary>
    [TestMethod]
    public void MoveSegmentOnTrack_ToBaseTrack_StillReflowsContiguously()
    {
        var model = new TimelineModel();
        var a = Video(0, 4);
        var b = Video(4, 6);
        var overlay = Slide(1, 2, track: 1);
        model.Segments.Add(a);
        model.Segments.Add(b);
        model.Segments.Add(overlay);

        new MoveSegmentOnTrackOperation(overlay.Id, TimeSpan.Zero, 0).Execute(model);

        Assert.AreEqual(0, overlay.TrackIndex);
        Assert.AreEqual(TimeSpan.Zero, overlay.Start);
        Assert.AreEqual(overlay.Duration, a.Start);
        Assert.AreEqual(a.Start + a.Duration, b.Start);
    }

    // ── Finding 1: the overlay-visibility gate shared by preview and export ──

    /// <summary>
    /// The rule the exporter uses to suppress a dissolve hidden under an overlay, and which
    /// the preview now calls too. If these two ever disagree the pipelines stop being
    /// pixel-identical, which this codebase treats as a hard requirement.
    /// </summary>
    [TestMethod]
    public void IsCoveredByHigherTrack_ReportsCoverOnlyWhileTheOverlayIsOnScreen()
    {
        var model = new TimelineModel();
        var baseClip = Video(0, 10);
        var overlay = Slide(4, 2, track: 1);
        model.Segments.Add(baseClip);
        model.Segments.Add(overlay);

        Assert.IsFalse(model.IsCoveredByHigherTrack(baseClip, TimeSpan.FromSeconds(3.9)));
        Assert.IsTrue(model.IsCoveredByHigherTrack(baseClip, TimeSpan.FromSeconds(4)));
        Assert.IsTrue(model.IsCoveredByHigherTrack(baseClip, TimeSpan.FromSeconds(5.9)));
        Assert.IsFalse(model.IsCoveredByHigherTrack(baseClip, TimeSpan.FromSeconds(6)),
            "The range is half-open, so the instant the overlay ends is uncovered again.");
        Assert.IsFalse(model.IsCoveredByHigherTrack(overlay, TimeSpan.FromSeconds(5)),
            "Nothing sits above the overlay, so it is never covered.");
    }

    /// <summary>A pure base-track timeline is never covered — the legacy path is untouched.</summary>
    [TestMethod]
    public void IsCoveredByHigherTrack_BaseOnlyTimeline_IsNeverCovered()
    {
        var model = new TimelineModel();
        var a = Video(0, 5);
        var b = Video(5, 5);
        model.Segments.Add(a);
        model.Segments.Add(b);

        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(10); t += TimeSpan.FromMilliseconds(500))
        {
            Assert.IsFalse(model.IsCoveredByHigherTrack(a, t));
            Assert.IsFalse(model.IsCoveredByHigherTrack(b, t));
        }
    }

    // ── Head-trimming an overlay clip must move its head, not its tail ──

    /// <summary>
    /// Trimming rewrites Duration and lets <c>RecalculateSegmentPositions</c> re-derive the
    /// base chain's starts. An overlay start is authored and deliberately not re-flowed, so a
    /// left-edge trim used to hold the start still and pull the RIGHT edge in — dragging one
    /// end of the clip moved the other. When the overlay was the last-ending segment it also
    /// shortened the whole scene, because TotalSegmentsDuration is max-End.
    /// </summary>
    [TestMethod]
    public void TrimOverlaySegmentFromStart_MovesTheHeadAndKeepsTheOutPoint()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 6));
        var overlay = Video(6, 2, track: 1);
        model.Segments.Add(overlay);

        Assert.AreEqual(TimeSpan.FromSeconds(8), model.TotalSegmentsDuration);

        new TrimSegmentEdgeOperation(overlay.Id, fromStart: true, TimeSpan.FromSeconds(1.5)).Execute(model);

        var trimmed = model.Segments.First(s => s.Id == overlay.Id);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), trimmed.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(6.5), trimmed.Start, "The head should move right, not the tail left.");
        Assert.AreEqual(TimeSpan.FromSeconds(8), trimmed.End, "The out-point must not move.");
        Assert.AreEqual(TimeSpan.FromSeconds(8), model.TotalSegmentsDuration, "The scene must not get shorter.");
    }

    /// <summary>Growing an overlay's head backwards also holds the out-point.</summary>
    [TestMethod]
    public void TrimOverlaySegmentFromStart_Growing_AlsoKeepsTheOutPoint()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 6));
        var overlay = new VideoSegment
        {
            VideoFilePath = "video.mp4",
            Start = TimeSpan.FromSeconds(6),
            Duration = TimeSpan.FromSeconds(2),
            SourceStart = TimeSpan.FromSeconds(3),
            SourceDuration = TimeSpan.FromSeconds(2),
            TrackIndex = 1,
        };
        model.Segments.Add(overlay);

        new TrimSegmentEdgeOperation(overlay.Id, fromStart: true, TimeSpan.FromSeconds(3)).Execute(model);

        var trimmed = model.Segments.First(s => s.Id == overlay.Id);
        Assert.AreEqual(TimeSpan.FromSeconds(3), trimmed.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(5), trimmed.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(8), trimmed.End);
    }

    /// <summary>Trimming the out-edge of an overlay leaves the head alone, as before.</summary>
    [TestMethod]
    public void TrimOverlaySegmentFromEnd_LeavesTheStartAlone()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 6));
        var overlay = Video(6, 2, track: 1);
        model.Segments.Add(overlay);

        new TrimSegmentEdgeOperation(overlay.Id, fromStart: false, TimeSpan.FromSeconds(1.5)).Execute(model);

        var trimmed = model.Segments.First(s => s.Id == overlay.Id);
        Assert.AreEqual(TimeSpan.FromSeconds(6), trimmed.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), trimmed.Duration);
    }

    /// <summary>
    /// The base chain keeps its ripple behaviour: a head trim there shortens the timeline,
    /// because the re-flow closes the gap. Only overlay tracks anchor the out-point.
    /// </summary>
    [TestMethod]
    public void TrimBaseSegmentFromStart_StillRipples()
    {
        var model = new TimelineModel();
        var a = Video(0, 4);
        var b = Video(4, 6);
        model.Segments.Add(a);
        model.Segments.Add(b);

        new TrimSegmentEdgeOperation(b.Id, fromStart: true, TimeSpan.FromSeconds(4)).Execute(model);
        model.RecalculateSegmentPositions();

        var trimmed = model.Segments.First(s => s.Id == b.Id);
        Assert.AreEqual(TimeSpan.FromSeconds(4), trimmed.Start, "The base chain stays contiguous.");
        Assert.AreEqual(TimeSpan.FromSeconds(4), trimmed.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(8), model.TotalSegmentsDuration);
    }

    /// <summary>Undo restores the overlay's original head position, not just its duration.</summary>
    [TestMethod]
    public void TrimOverlaySegmentFromStart_Undo_RestoresStartAndDuration()
    {
        var model = new TimelineModel();
        model.Segments.Add(Video(0, 6));
        var overlay = Video(6, 2, track: 1);
        model.Segments.Add(overlay);

        var op = new TrimSegmentEdgeOperation(overlay.Id, fromStart: true, TimeSpan.FromSeconds(1.5));
        op.Execute(model);
        op.Undo(model);

        var restored = model.Segments.First(s => s.Id == overlay.Id);
        Assert.AreEqual(TimeSpan.FromSeconds(6), restored.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2), restored.Duration);
        Assert.AreEqual(1, restored.TrackIndex);
    }
}
