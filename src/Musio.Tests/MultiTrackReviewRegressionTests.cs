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
}
