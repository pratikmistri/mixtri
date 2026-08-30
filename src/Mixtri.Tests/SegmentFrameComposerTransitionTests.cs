namespace Mixtri.Tests;

using Mixtri.Core.Export;

/// <summary>
/// Covers the pure source-time mapping/clamping/collapsing arithmetic
/// <see cref="SegmentFrameComposer"/> uses to let the outgoing side of a transition roll past
/// its segment's own edited cut point (per
/// <see cref="Mixtri.Core.Timeline.TransitionResolution.OutgoingLocalOffset"/>) while never
/// seeking negative, past whatever source footage actually exists
/// (<see cref="SegmentFrameComposer.ClampSourceTime"/>,
/// <see cref="SegmentFrameComposer.SelectAvailableSourceDuration"/>), or onto the exact same
/// source instant the incoming side is already showing
/// (<see cref="SegmentFrameComposer.CollapseContiguousSourceBoundary"/> -- the fix for a
/// same-source contiguous split otherwise rendering an invisible, no-op dissolve).
/// </summary>
/// <remarks>
/// <see cref="SegmentFrameComposer"/> itself needs a GPU <c>CanvasDevice</c> and real media
/// (a video file, a <see cref="Mixtri.Core.Processing.VideoFrameReader"/>, etc.) to build a
/// context and render a frame, so full end-to-end composition of
/// <see cref="SegmentFrameComposer.ComposeFrameAsync"/> /
/// <see cref="SegmentFrameComposer.ComposeSegmentAtOffsetAsync"/> is not unit-testable in this
/// project -- there is no fake/mocked <c>CanvasDevice</c> or media source anywhere in the
/// codebase to substitute. That end-to-end behaviour (does a real dissolve actually roll into
/// real footage past a cut, hold gracefully once the file runs out, and visibly render rather
/// than freeze on a same-source split) needs manual/GPU verification. What IS fully covered
/// here is every pure decision that feeds those outcomes: exactly which source-file instant
/// gets sampled for a given (segment start, local offset, speed, available footage) tuple,
/// whether it was held, whether a same-source contiguous boundary needed collapsing, and
/// which bound (<c>Reader.Duration</c> / <c>MediaComposition.Duration</c> / the segment's own
/// trimmed cut point) gets selected.
/// </remarks>
[TestClass]
public sealed class SegmentFrameComposerTransitionTests
{
    [TestMethod]
    public void ClampSourceTime_OffsetWithinSegment_MapsNormally()
    {
        // 2s into a segment that starts sourcing at t=10s, no speed change, plenty of
        // footage available (100s) -- should map straight through, unclamped.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 10,
            localOffsetSeconds: 2,
            speed: 1.0,
            availableDurationSeconds: 100);

        Assert.AreEqual(12.0, time, 1e-9);
        Assert.IsFalse(held);
    }

    [TestMethod]
    public void ClampSourceTime_OffsetPastCut_MapsPastCutWhenFootageAllows()
    {
        // The segment's own trimmed cut point is SourceStart + SourceDuration = 10 + 4 = 14s,
        // but the file actually has 20s of readable footage (e.g. a JPEG/MP4 reader whose
        // real frame count outruns the trim). A transition's rolling OutgoingLocalOffset of
        // 4.3s (0.3s past the segment's own 4s duration) should sample 14.3s, past the cut,
        // exactly as the rolling model intends -- not frozen at 14s.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 10,
            localOffsetSeconds: 4.3,
            speed: 1.0,
            availableDurationSeconds: 20);

        Assert.AreEqual(14.3, time, 1e-9);
        Assert.IsFalse(held);
    }

    [TestMethod]
    public void ClampSourceTime_OffsetPastAvailableFootage_ClampsAndReportsHeld()
    {
        // Same rolling offset as above, but this time the file itself only has 14s of
        // readable footage (the segment was trimmed to end-of-file, i.e. no handle left).
        // The raw mapped time (14.3s) must be held at the 14s boundary instead of seeking
        // past the end of the file -- reproducing today's frozen-last-frame behaviour.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 10,
            localOffsetSeconds: 4.3,
            speed: 1.0,
            availableDurationSeconds: 14);

        Assert.AreEqual(14.0, time, 1e-9);
        Assert.IsTrue(held);
    }

    [TestMethod]
    public void ClampSourceTime_UnknownAvailableDuration_OnlyFloorsAtZero()
    {
        // availableDurationSeconds <= 0 means "no reliable bound known" (documented on
        // ClampSourceTime): the upper clamp must not apply, only the zero floor.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 10,
            localOffsetSeconds: 500,
            speed: 1.0,
            availableDurationSeconds: 0);

        Assert.AreEqual(510.0, time, 1e-9);
        Assert.IsFalse(held);
    }

    [TestMethod]
    public void ClampSourceTime_SpeedFactor_IsApplied()
    {
        // A 2x-speed segment covers twice as much source time per unit of local (output)
        // offset -- matching the pre-existing
        // "video.SourceStart + localOffset * speed" convention this helper replaces inline.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 0,
            localOffsetSeconds: 3,
            speed: 2.0,
            availableDurationSeconds: 100);

        Assert.AreEqual(6.0, time, 1e-9);
        Assert.IsFalse(held);
    }

    [TestMethod]
    public void ClampSourceTime_SpeedFactor_CanStillBeHeldByAvailableFootage()
    {
        // A fast segment (3x) rolling past its cut can run into the available-footage clamp
        // just as easily as an unsped one -- the speed multiplier and the footage clamp are
        // independent concerns and must compose correctly.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 0,
            localOffsetSeconds: 10,
            speed: 3.0,
            availableDurationSeconds: 25);

        Assert.AreEqual(25.0, time, 1e-9);
        Assert.IsTrue(held);
    }

    [TestMethod]
    public void ClampSourceTime_NegativeRawTime_NeverProducesNegative()
    {
        // Defensive: even if a caller ever passed a negative local offset (never happens on
        // the real rolling path, since OutgoingLocalOffset is always >= OutgoingSegment's own
        // duration), the result must still be floored at zero, matching the pre-existing
        // Math.Max(0, sourceTimeSeconds) guard in ComposeWithContextAsync.
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 5,
            localOffsetSeconds: -20,
            speed: 1.0,
            availableDurationSeconds: 100);

        Assert.AreEqual(0.0, time, 1e-9);
        Assert.IsFalse(held);
    }

    [TestMethod]
    public void ClampSourceTime_ExactlyAtAvailableDuration_IsNotHeld()
    {
        // The boundary case: landing exactly on the available duration is a real, readable
        // instant (the last frame), not an overrun -- only strictly exceeding it counts as
        // "held" (this mirrors ResolveAvailableSourceDuration's own callers, which want to
        // know whether the sample is genuine rolling footage or a synthetic freeze).
        var (time, held) = SegmentFrameComposer.ClampSourceTime(
            sourceStartSeconds: 10,
            localOffsetSeconds: 4,
            speed: 1.0,
            availableDurationSeconds: 14);

        Assert.AreEqual(14.0, time, 1e-9);
        Assert.IsFalse(held);
    }

    // ---- CollapseContiguousSourceBoundary: the Bug 1 (invisible-transition) regression ----

    [TestMethod]
    public void CollapseContiguousSourceBoundary_ContiguousSameSourceSplit_CollapsesStrictlyEarlier()
    {
        // Regression for the "invisible transition" bug: an untrimmed split sets
        // incoming.SourceStart == outgoing.SourceStart + outgoing.SourceDuration, so the
        // outgoing side's rolled time maps to EXACTLY the incoming side's source time for
        // every instant of the window (here both land on 14.3s). Left uncollapsed the two
        // sides would sample identical footage -- a frame blended with itself -- for the
        // whole dissolve. The result must be collapsed to strictly earlier than the incoming
        // side's instant, one 30fps frame (1/30s) short of it.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.3,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 14.3,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 30,
            outgoingCutPointSeconds: 14.3,
            incomingSourceStartSeconds: 14.3);

        Assert.AreEqual(14.3 - 1.0 / 30, result, 1e-9);
        Assert.IsTrue(result < 14.3, "Collapsed outgoing time must be strictly earlier than incoming's.");
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_OutgoingOvertakingIncoming_StillCollapses()
    {
        // Not just exact equality -- an outgoing side that has rolled PAST the incoming's own
        // instant (e.g. differing speeds around the cut) must also collapse, not just the
        // exact-equal case. The ranges still meet at the cut, so this is a genuine split.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 20.0,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 14.3,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 30,
            outgoingCutPointSeconds: 14.3,
            incomingSourceStartSeconds: 14.3);

        Assert.AreEqual(14.3 - 1.0 / 30, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_TrimmedSplitWithRealGap_PreservesRolling()
    {
        // The user trimmed the incoming side so there IS a genuine gap in source time
        // (incoming's mapped instant, 20s, is well past the outgoing's rolled instant,
        // 14.3s) -- rolling is entirely legitimate here and must not be collapsed away.
        // The ranges still MEET at the cut (outgoing's cut point == incoming's in-point),
        // so this exercises the gap check specifically, not the contiguity check.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.3,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 20.0,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 30,
            outgoingCutPointSeconds: 20.0,
            incomingSourceStartSeconds: 20.0);

        Assert.AreEqual(14.3, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_ReorderedSameSourceRanges_PreservesOutgoingFrame()
    {
        // Regression: sharing a file is NOT proof of a contiguous split. Reordering one
        // recording's clips so [10s,14s] plays before [2s,6s] means the outgoing side is
        // legitimately at 14s while the incoming side is at 2s. The old timestamp-only test
        // ("outgoing >= incoming") saw that as a collision and yanked the outgoing image back
        // to ~1.967s -- a twelve-second jump backwards at the start of every such dissolve.
        // The ranges do not meet at the boundary (cut point 14s vs in-point 2s), so nothing
        // must be collapsed.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.0,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 2.0,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 30,
            outgoingCutPointSeconds: 14.0,
            incomingSourceStartSeconds: 2.0);

        Assert.AreEqual(14.0, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_DifferentSourceFiles_RollingUnaffected()
    {
        // Genuinely different sources: even though the two mapped times coincide (or the
        // outgoing side "overtakes"), there is no shared source-time space to collide in, so
        // the policy must not touch the outgoing time at all -- this is the whole point of
        // the feature (dissolving between two different recordings).
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.3,
            outgoingVideoFilePath: @"C:\a.mp4",
            incomingSourceTimeSeconds: 14.3,
            incomingVideoFilePath: @"C:\b.mp4",
            fps: 30,
            outgoingCutPointSeconds: 14.3,
            incomingSourceStartSeconds: 14.3);

        Assert.AreEqual(14.3, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_NoIncomingVideoContext_IsNoOp()
    {
        // The incoming side of the transition is not itself a VideoSegment (e.g. a text
        // slide) -- callers signal this with a null incomingSourceTimeSeconds, and there is
        // no source-time space to collide in, so this must be a pure no-op.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.3,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: null,
            incomingVideoFilePath: null,
            fps: 30,
            outgoingCutPointSeconds: 14.3,
            incomingSourceStartSeconds: 14.3);

        Assert.AreEqual(14.3, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_DifferingSpeedFactors_UsesCallerSuppliedMappedTimes()
    {
        // The policy itself is speed-agnostic -- it only ever sees the two sides' already
        // speed-mapped source times (computed by the caller via MapLocalOffsetToSourceTime,
        // itself covered by the SpeedFactor cases above). Here a 2x outgoing side and a 0.5x
        // incoming side both map to the same shared source instant (14.3s) despite using
        // different local offsets and speeds -- exercising that the collapse decision is
        // driven purely by the resulting mapped times, not by the speeds themselves.
        double outgoingMapped = SegmentFrameComposer.MapLocalOffsetToSourceTime(
            sourceStartSeconds: 10, localOffsetSeconds: 2.15, speed: 2.0);
        double incomingMapped = SegmentFrameComposer.MapLocalOffsetToSourceTime(
            sourceStartSeconds: 14, localOffsetSeconds: 0.6, speed: 0.5);

        Assert.AreEqual(14.3, outgoingMapped, 1e-9);
        Assert.AreEqual(14.3, incomingMapped, 1e-9);

        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingMapped, @"C:\rec.mp4", incomingMapped, @"C:\rec.mp4", fps: 30,
            outgoingCutPointSeconds: 14.3, incomingSourceStartSeconds: 14.3);

        Assert.IsTrue(result < incomingMapped, "Collapsed result must stay strictly earlier than incoming.");
        Assert.AreEqual(incomingMapped - 1.0 / 30, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_NonPositiveFps_FallsBackTo30Fps()
    {
        // Defensive guard: VideoSegment.Fps defaults to 30 and should never be <= 0 in
        // practice, but a bad value must not corrupt the frame-step math (e.g. divide by
        // zero producing NaN/Infinity) -- it should fall back to a 30fps step instead.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 14.3,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 14.3,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 0,
            outgoingCutPointSeconds: 14.3,
            incomingSourceStartSeconds: 14.3);

        Assert.AreEqual(14.3 - 1.0 / 30, result, 1e-9);
    }

    [TestMethod]
    public void CollapseContiguousSourceBoundary_NeverGoesNegative()
    {
        // A collapse very near source-file time zero must still floor at zero, matching
        // every other guard in this file against seeking negative.
        double result = SegmentFrameComposer.CollapseContiguousSourceBoundary(
            outgoingSourceTimeSeconds: 0.01,
            outgoingVideoFilePath: @"C:\rec.mp4",
            incomingSourceTimeSeconds: 0.01,
            incomingVideoFilePath: @"C:\rec.mp4",
            fps: 30,
            outgoingCutPointSeconds: 0.01,
            incomingSourceStartSeconds: 0.01);

        Assert.AreEqual(0.0, result, 1e-9);
    }

    // ---- SelectAvailableSourceDuration: the Bug 2 (Reader -> SourceComposition -> cut point) ----
    // bound-selection precedence ----

    [TestMethod]
    public void SelectAvailableSourceDuration_PrefersReaderDurationWhenPresent()
    {
        // Reader.Duration wins even when a composition duration is also supplied -- it's the
        // most reliable, cheapest bound (real decoded/captured frame count).
        double result = SegmentFrameComposer.SelectAvailableSourceDuration(
            readerDuration: TimeSpan.FromSeconds(30),
            compositionDuration: TimeSpan.FromSeconds(20),
            cutPoint: TimeSpan.FromSeconds(14),
            fps: 30);

        Assert.AreEqual(30.0, result, 1e-9);
    }

    [TestMethod]
    public void SelectAvailableSourceDuration_FallsBackToCompositionDuration_BackedOffOneFrame()
    {
        // No reader open (null) -- MediaComposition.Duration is used instead, but backed off
        // by one source frame (Bug 2's fix) since it is an EXCLUSIVE endpoint and sampling
        // exactly at it would be out of range.
        double result = SegmentFrameComposer.SelectAvailableSourceDuration(
            readerDuration: null,
            compositionDuration: TimeSpan.FromSeconds(20),
            cutPoint: TimeSpan.FromSeconds(14),
            fps: 25);

        Assert.AreEqual(20.0 - 1.0 / 25, result, 1e-9);
    }

    [TestMethod]
    public void SelectAvailableSourceDuration_FallsBackToCutPoint_WhenNeitherAvailable()
    {
        // Neither a reader nor a composition is open -- fall back to the segment's own
        // trimmed cut point, reproducing today's frozen-last-frame behaviour.
        double result = SegmentFrameComposer.SelectAvailableSourceDuration(
            readerDuration: null,
            compositionDuration: null,
            cutPoint: TimeSpan.FromSeconds(14),
            fps: 30);

        Assert.AreEqual(14.0, result, 1e-9);
    }

    [TestMethod]
    public void SelectAvailableSourceDuration_ZeroReaderDuration_FallsThroughToComposition()
    {
        // A zero reader duration (e.g. a degenerate zero-frame reader) must not be treated as
        // a usable bound -- it should fall through to the next preference, exactly like null.
        double result = SegmentFrameComposer.SelectAvailableSourceDuration(
            readerDuration: TimeSpan.Zero,
            compositionDuration: TimeSpan.FromSeconds(20),
            cutPoint: TimeSpan.FromSeconds(14),
            fps: 30);

        Assert.AreEqual(20.0 - 1.0 / 30, result, 1e-9);
    }
}
