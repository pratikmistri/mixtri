using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Verifies <see cref="TimelineModel.NearestVideoSegmentEdge"/>, the pure clamping
/// helper camera/zoom timeline gestures use to land on a valid source-time boundary
/// when the pointer is over unmappable output time (a text slide, or a video segment
/// outside the relevant source/track scope) instead of mixing output time into a
/// source-time domain.
/// </summary>
[TestClass]
public sealed class TimelineMappingClampingTests
{
    private static VideoSegment Video(string path, double startSec, double durSec)
        => new()
        {
            VideoFilePath = path,
            Start = TimeSpan.FromSeconds(startSec),
            Duration = TimeSpan.FromSeconds(durSec),
            SourceStart = TimeSpan.FromSeconds(startSec),
            SourceDuration = TimeSpan.FromSeconds(durSec),
        };

    [TestMethod]
    public void EmptyCandidates_ReturnsNullSegment()
    {
        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(
            Array.Empty<VideoSegment>(), TimeSpan.FromSeconds(5));

        Assert.IsNull(segment);
        Assert.IsFalse(atStart);
    }

    [TestMethod]
    public void OutputTime_BeforeAllSegments_ClampsToEarliestStart()
    {
        var a = Video("a.mp4", 0, 10);   // output 0..10
        var b = Video("b.mp4", 10, 10);  // output 10..20

        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, TimeSpan.FromSeconds(-5));

        Assert.AreSame(a, segment);
        Assert.IsTrue(atStart);
    }

    [TestMethod]
    public void OutputTime_AfterAllSegments_ClampsToLatestEnd()
    {
        var a = Video("a.mp4", 0, 10);   // output 0..10
        var b = Video("b.mp4", 10, 10);  // output 10..20

        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, TimeSpan.FromSeconds(45));

        Assert.AreSame(b, segment);
        Assert.IsFalse(atStart);
    }

    [TestMethod]
    public void OutputTime_BetweenTwoSegments_ClampsToNearerEdge()
    {
        // a occupies output 0..10, a text slide occupies 10..13, b occupies 13..23.
        var a = Video("a.mp4", 0, 10);
        var b = Video("b.mp4", 13, 10);

        // 11s is 1s past a.End and 2s before b.Start => nearer to a.End.
        var (nearSegA, atStartA) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, TimeSpan.FromSeconds(11));
        Assert.AreSame(a, nearSegA);
        Assert.IsFalse(atStartA);

        // 12s is 2s past a.End and 1s before b.Start => nearer to b.Start.
        var (nearSegB, atStartB) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, TimeSpan.FromSeconds(12));
        Assert.AreSame(b, nearSegB);
        Assert.IsTrue(atStartB);
    }

    [TestMethod]
    public void OutputTime_InsideASegment_StillReportsItsNearerEdge()
    {
        // Inside-segment callers normally short-circuit before reaching this helper,
        // but it must still behave sanely (nearer edge, no throw) if invoked directly.
        var a = Video("a.mp4", 0, 10); // output 0..10

        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(new[] { a }, TimeSpan.FromSeconds(2));

        Assert.AreSame(a, segment);
        Assert.IsTrue(atStart); // 2s is closer to Start(0) than End(10)
    }

    [TestMethod]
    public void TieDistance_PicksFirstEncounteredCandidate()
    {
        // Two zero-length segments equidistant from the probe time; the first
        // candidate in iteration order should win (stable, deterministic tie-break).
        var a = Video("a.mp4", 0, 0);   // output [0,0]
        var b = Video("b.mp4", 10, 0);  // output [10,10]

        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, TimeSpan.FromSeconds(5));

        Assert.AreSame(a, segment);
        Assert.IsTrue(atStart);
    }

    [TestMethod]
    public void CrossSourceDrag_ClampingCandidatesPreFilteredByFile_NeverLeaksOtherSourceSegment()
    {
        // Regression coverage for a zoom-create drag that starts on source A and
        // crosses into source B's segment (or a slide past it): callers that resolve
        // a pointer X to a source time for a specific owning file (e.g.
        // TimelineControl.XToKeyframeFileTime) must pre-filter candidates to that
        // file's own segments before calling NearestVideoSegmentEdge, so the
        // clamped result can only ever land on that file's boundary — never on a
        // differently-sourced segment's timestamp, even when B is output-time-closer.
        var a = Video("a.mp4", 0, 5);    // output 0..5
        var b = Video("b.mp4", 8, 22);   // output 8..30 (much closer to the probe)
        var probe = TimeSpan.FromSeconds(20);

        // Unfiltered, the closer segment (B) wins — this is the behavior an
        // incorrect call site (resolving "whatever is under the pointer" instead of
        // the drag's owning file) would get.
        var (unfiltered, _) = TimelineModel.NearestVideoSegmentEdge(new[] { a, b }, probe);
        Assert.AreSame(b, unfiltered);

        // A caller correctly anchored to "a.mp4" pre-filters candidates to A's own
        // segments, so the same probe time can only ever clamp onto A — never B.
        var (segment, atStart) = TimelineModel.NearestVideoSegmentEdge(new[] { a }, probe);
        Assert.AreSame(a, segment);
        Assert.IsFalse(atStart); // clamps to A's end (5s), not anything from B
    }
}

