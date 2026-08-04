namespace Musio.Tests;

using Musio.Core.Timeline;

/// <summary>
/// T8 (feature/segment-transitions integration pass) — cross-cutting edge cases across split,
/// reorder, and ripple-delete for boundaries carrying a configured
/// <see cref="TimelineSegment.InTransition"/>. Each test reasons about a specific claim from the
/// T8 task brief; see the class-level report for which of these are coherent-by-design and which
/// are reported as genuinely broken (found here, not silently redesigned).
/// </summary>
[TestClass]
public sealed class TransitionCrossCuttingTests
{
    private static VideoSegment Video(double durSec, TransitionConfig? inTransition = null, double speedFactor = 1.0) => new()
    {
        VideoFilePath = "C:\\primary.mp4",
        SourceStart = TimeSpan.Zero,
        SourceDuration = TimeSpan.FromSeconds(durSec * speedFactor),
        Duration = TimeSpan.FromSeconds(durSec),
        SpeedFactor = speedFactor,
        InTransition = inTransition,
    };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel();
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    // ── Reorder ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A transition is identified by the INCOMING segment's own Id (see
    /// <see cref="UpdateTransitionOperation"/>'s remarks) and travels with that segment object
    /// through a reorder — <see cref="ReorderSegmentOperation"/> moves the same segment
    /// reference, never copying/clearing <see cref="TimelineSegment.InTransition"/>. This is
    /// coherent, not broken: the configured dissolve is a property of "how this clip enters",
    /// so after a reorder it simply dissolves from whatever NEW predecessor now precedes it.
    /// </summary>
    [TestMethod]
    public void Reorder_TransitionStaysAttachedToItsOwningSegment_AndAppliesToTheNewPredecessor()
    {
        var config = new TransitionConfig { Type = TransitionType.WipeUp, Duration = TimeSpan.FromMilliseconds(400) };
        var a = Video(4);
        var b = Video(4);
        var c = Video(4, config); // boundary B->C carries the transition.
        var model = ModelWith(a, b, c);

        // Sanity: the transition is active at the B->C boundary before any reorder.
        var before = TransitionResolver.Resolve(model, c.Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(before.Active);
        Assert.AreSame(b, before.OutgoingSegment);
        Assert.AreSame(c, before.IncomingSegment);

        // Move C to the middle: new order A, C, B.
        var op = new MoveSegmentOperation(c.Id, 1);
        op.Execute(model);

        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(c, model.Segments[1]);
        Assert.AreSame(b, model.Segments[2]);

        // The SAME TransitionConfig instance travels with C, so the boundary that now shows
        // it is A->C (C's new predecessor), not the original B->C.
        var afterMove = TransitionResolver.Resolve(model, model.Segments[1].Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(afterMove.Active);
        Assert.AreEqual(TransitionType.WipeUp, afterMove.Type);
        Assert.AreSame(a, afterMove.OutgoingSegment);
        Assert.AreSame(c, afterMove.IncomingSegment);

        // The old B->C position no longer exists as a boundary at all (B is now last, with
        // nothing after it) -- no dangling/duplicated transition is left behind.
        Assert.IsFalse(TransitionResolver.Resolve(model, model.Segments[2].Start + TimeSpan.FromMilliseconds(50)).Active);
    }

    /// <summary>
    /// Segment 0 can never carry an active transition (<see cref="TransitionResolver"/>'s own
    /// "index &lt;= 0 -&gt; None" rule) -- verified through a reorder that moves a
    /// transition-owning segment INTO index 0: the config is not cleared (it travels with the
    /// segment, unchanged), it simply goes dormant because there is no predecessor to dissolve
    /// from. Moving the same segment back away from index 0 reactivates the SAME config against
    /// whatever now precedes it -- documented, coherent behaviour (matches
    /// <see cref="UpdateTransitionOperation.Execute"/>'s own comment about this), not a crash or
    /// data loss.
    /// </summary>
    [TestMethod]
    public void Reorder_SegmentWithTransitionMovedToIndexZero_GoesDormant_ThenReactivatesWhenMovedAway()
    {
        var config = new TransitionConfig { Type = TransitionType.Glitch, Duration = TimeSpan.FromMilliseconds(300) };
        var a = Video(4);
        var b = Video(4, config); // boundary A->B carries the transition.
        var model = ModelWith(a, b);

        var moveToFront = new MoveSegmentOperation(b.Id, 0);
        moveToFront.Execute(model);
        Assert.AreSame(b, model.Segments[0]);
        Assert.AreSame(a, model.Segments[1]);

        // B is now index 0: TransitionResolver must never activate a transition there, even
        // though B.InTransition is still non-null.
        Assert.IsNotNull(model.Segments[0].InTransition);
        var atFront = TransitionResolver.Resolve(model, model.Segments[0].Start + TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(atFront.Active);
        // Nor does the A boundary (now at index 1, with no InTransition of its own) show one.
        Assert.IsFalse(TransitionResolver.Resolve(model, model.Segments[1].Start + TimeSpan.FromMilliseconds(50)).Active);

        // Move B back to the end: order becomes A, B again.
        var moveToEnd = new MoveSegmentOperation(b.Id, 2);
        moveToEnd.Execute(model);
        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(b, model.Segments[1]);

        var reactivated = TransitionResolver.Resolve(model, model.Segments[1].Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(reactivated.Active, "the dormant config must reactivate once B has a predecessor again");
        Assert.AreEqual(TransitionType.Glitch, reactivated.Type);
    }

    // ── Ripple-delete (RemoveSegmentOperation) ──────────────────────────────

    /// <summary>
    /// Removing the OUTGOING segment of a configured boundary leaves the incoming segment's
    /// config untouched -- it now dissolves from whatever became its new predecessor. Coherent:
    /// the config belongs to the incoming segment, independent of who used to precede it.
    /// </summary>
    [TestMethod]
    public void RemoveSegment_RemovingTheOutgoingSide_TransitionSurvivesAgainstTheNewPredecessor()
    {
        var config = new TransitionConfig { Type = TransitionType.PushLeft, Duration = TimeSpan.FromMilliseconds(350) };
        var a = Video(4);
        var b = Video(4); // to be removed
        var c = Video(4, config); // boundary B->C carries the transition.
        var model = ModelWith(a, b, c);

        new RemoveSegmentOperation(b.Id).Execute(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(c, model.Segments[1]);

        var resolved = TransitionResolver.Resolve(model, model.Segments[1].Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(resolved.Active);
        Assert.AreEqual(TransitionType.PushLeft, resolved.Type);
        Assert.AreSame(a, resolved.OutgoingSegment);
        Assert.AreSame(c, resolved.IncomingSegment);
    }

    /// <summary>
    /// Removing the segment that OWNS a configured transition deletes the config along with it
    /// -- no dangling reference, and the boundary simply ceases to exist (the two remaining
    /// neighbours get a plain, unconfigured boundary between them, subject to the usual legacy
    /// fallback rule).
    /// </summary>
    [TestMethod]
    public void RemoveSegment_RemovingTheSegmentThatOwnsTheTransition_DeletesItWithNoDanglingReference()
    {
        var config = new TransitionConfig { Type = TransitionType.DipToWhite, Duration = TimeSpan.FromMilliseconds(300) };
        var a = Video(4);
        var b = Video(4, config); // owns the A->B transition.
        var c = Video(4);
        var model = ModelWith(a, b, c);

        new RemoveSegmentOperation(b.Id).Execute(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(c, model.Segments[1]);

        // Plain video->video boundary now, no InTransition -- legacy hard cut.
        Assert.IsNull(model.Segments[1].InTransition);
        Assert.IsFalse(TransitionResolver.Resolve(model, model.Segments[1].Start + TimeSpan.FromMilliseconds(50)).Active);
    }

    // ── Split ────────────────────────────────────────────────────────────
    //
    // FIXED during T8 (originally reported as a bug, then corrected per follow-up instruction):
    // SplitSegmentAtTimeOperation/SplitAndInsertTextSlideOperation build both halves with
    // `segment with { Id = ..., ... }`, a record `with`-expression that copies every field it
    // doesn't explicitly override -- including InTransition. The FIRST half correctly inherits
    // the original boundary's config (it is now the segment that boundary's predecessor
    // dissolves into). The SECOND half must NOT -- the boundary between the two new halves is a
    // brand-new, purely-mechanical split point that never had a transition configured on it.
    // Both operations now explicitly null out the second half's InTransition; these tests pin
    // that fix (and would fail again if the null-out were ever reverted/lost).
    [TestMethod]
    public void Split_SegmentWithTransition_OnlyFirstHalfKeepsIt_SecondHalfIsUnconfigured()
    {
        var config = new TransitionConfig { Type = TransitionType.WipeUp, Duration = TimeSpan.FromMilliseconds(400) };
        var a = Video(4);
        var b = Video(4, config); // boundary A->B carries the transition.
        var model = ModelWith(a, b);

        // Split B at its local midpoint (global time 4s + 2s = 6s).
        var split = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(6));
        split.Execute(model);

        Assert.IsTrue(split.DidSplit);
        Assert.AreEqual(3, model.Segments.Count);
        var firstHalf = model.Segments[1];
        var secondHalf = model.Segments[2];

        // Correct/intentional: the ORIGINAL boundary (A -> first half) still shows the config.
        Assert.IsNotNull(firstHalf.InTransition);
        Assert.AreEqual(TransitionType.WipeUp, firstHalf.InTransition!.Type);
        var originalBoundary = TransitionResolver.Resolve(model, firstHalf.Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(originalBoundary.Active);

        // Fixed: the brand-new internal split boundary (first half -> second half) must have NO
        // transition at all -- neither an explicit hard cut nor a copy of the original config --
        // so it stays "unconfigured" (legacy-fallback-eligible, e.g. if a slide is later
        // inserted next to it) rather than explicitly suppressed.
        Assert.IsNull(secondHalf.InTransition,
            "the new split boundary must be unconfigured, not carry a copy of the original config");
        Assert.IsFalse(
            TransitionResolver.Resolve(model, secondHalf.Start + TimeSpan.FromMilliseconds(50)).Active,
            "a plain split of a video->video boundary must still hard-cut, exactly as if the " +
            "clip had never been split at all");
    }

    /// <summary>
    /// Splitting a segment that has NO configured transition behaves correctly: neither half
    /// gets one, matching the legacy (no InTransition) fallback rule at the new internal
    /// boundary exactly as it would have applied before the split existed.
    /// </summary>
    [TestMethod]
    public void Split_SegmentWithoutTransition_NeitherHalfGetsOne()
    {
        var a = Video(4);
        var b = Video(4); // no InTransition.
        var model = ModelWith(a, b);

        var split = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(6));
        split.Execute(model);

        Assert.IsTrue(split.DidSplit);
        Assert.IsNull(model.Segments[1].InTransition);
        Assert.IsNull(model.Segments[2].InTransition);
        Assert.IsFalse(TransitionResolver.Resolve(model, model.Segments[2].Start + TimeSpan.FromMilliseconds(50)).Active);
    }

    /// <summary>
    /// Undo must restore the ORIGINAL, pre-split segment exactly -- including its
    /// <see cref="TimelineSegment.InTransition"/> -- not a state where the fix's explicit
    /// <c>null</c> assignment on the second half has leaked backwards. Both split operations
    /// restore via a full snapshot of the previous segment list, so this is really pinning that
    /// snapshot/restore contract rather than any per-property undo logic.
    /// </summary>
    [TestMethod]
    public void Split_Undo_RestoresOriginalSegment_InTransitionIncluded()
    {
        var config = new TransitionConfig { Type = TransitionType.WipeUp, Duration = TimeSpan.FromMilliseconds(400) };
        var a = Video(4);
        var b = Video(4, config);
        var model = ModelWith(a, b);

        var split = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(6));
        split.Execute(model);
        Assert.IsTrue(split.DidSplit);
        Assert.AreEqual(3, model.Segments.Count);

        split.Undo(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(b, model.Segments[1]);
        Assert.IsNotNull(model.Segments[1].InTransition);
        Assert.AreEqual(TransitionType.WipeUp, model.Segments[1].InTransition!.Type);
    }

    /// <summary>
    /// Undo must also cleanly restore a segment that had NO transition before the split (the
    /// <c>null</c> case), not leave a stray non-null config behind from either half.
    /// </summary>
    [TestMethod]
    public void Split_Undo_RestoresOriginalSegment_WhenOriginalHadNoTransition()
    {
        var a = Video(4);
        var b = Video(4); // no InTransition.
        var model = ModelWith(a, b);

        var split = new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(6));
        split.Execute(model);
        Assert.IsTrue(split.DidSplit);

        split.Undo(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(b, model.Segments[1]);
        Assert.IsNull(model.Segments[1].InTransition);
    }

    /// <summary>
    /// <see cref="SplitAndInsertTextSlideOperation"/> has the identical shape (split a video
    /// segment via record `with`, insert a slide between the two halves) and needed the same
    /// fix: the video segment AFTER the inserted slide (the boundary the slide dissolves into)
    /// must not inherit the original video's InTransition either.
    /// </summary>
    [TestMethod]
    public void SplitAndInsertTextSlide_SecondHalfIsUnconfigured_FirstHalfKeepsOriginalTransition()
    {
        var config = new TransitionConfig { Type = TransitionType.PushLeft, Duration = TimeSpan.FromMilliseconds(300) };
        var a = Video(4);
        var b = Video(4, config); // boundary A->B carries the transition.
        var model = ModelWith(a, b);

        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(2) };
        var op = new SplitAndInsertTextSlideOperation(TimeSpan.FromSeconds(6), slide);
        op.Execute(model);

        Assert.AreEqual(4, model.Segments.Count);
        var firstHalf = model.Segments[1];
        var insertedSlide = model.Segments[2];
        var secondHalf = model.Segments[3];

        Assert.AreSame(slide, insertedSlide);

        // Correct/intentional: the original A->B boundary now shows up as A->firstHalf.
        Assert.IsNotNull(firstHalf.InTransition);
        Assert.AreEqual(TransitionType.PushLeft, firstHalf.InTransition!.Type);
        Assert.IsTrue(TransitionResolver.Resolve(model, firstHalf.Start + TimeSpan.FromMilliseconds(50)).Active);

        // Fixed: the new slide->secondHalf boundary must be unconfigured, not inherit a copy of
        // the original video's config -- it is eligible for the legacy slide-adjacent fallback
        // instead (verified separately below), never an inherited stylised effect.
        Assert.IsNull(secondHalf.InTransition);

        // Unconfigured + touches a TextSlideSegment -> legacy 500ms linear crossfade fallback,
        // not the inherited PushLeft.
        var slideBoundary = TransitionResolver.Resolve(model, secondHalf.Start + TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(slideBoundary.Active);
        Assert.AreEqual(TransitionType.CrossFade, slideBoundary.Type);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), slideBoundary.Duration);
    }

    /// <summary>Undo for the insert-slide operation restores the original, unsplit segment.</summary>
    [TestMethod]
    public void SplitAndInsertTextSlide_Undo_RestoresOriginalSegment_InTransitionIncluded()
    {
        var config = new TransitionConfig { Type = TransitionType.PushLeft, Duration = TimeSpan.FromMilliseconds(300) };
        var a = Video(4);
        var b = Video(4, config);
        var model = ModelWith(a, b);

        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(2) };
        var op = new SplitAndInsertTextSlideOperation(TimeSpan.FromSeconds(6), slide);
        op.Execute(model);
        Assert.AreEqual(4, model.Segments.Count);

        op.Undo(model);

        Assert.AreEqual(2, model.Segments.Count);
        Assert.AreSame(a, model.Segments[0]);
        Assert.AreSame(b, model.Segments[1]);
        Assert.IsNotNull(model.Segments[1].InTransition);
        Assert.AreEqual(TransitionType.PushLeft, model.Segments[1].InTransition!.Type);
    }

    // ── Speed-adjusted segments ──────────────────────────────────────────

    /// <summary>
    /// <see cref="TransitionResolver"/> operates purely on OUTPUT-timeline durations/positions
    /// (<see cref="TimelineSegment.Duration"/>, <see cref="TimelineSegment.Start"/>) and never
    /// reads <see cref="VideoSegment.SpeedFactor"/> -- speed only affects how a resolved
    /// <see cref="TransitionResolution.OutgoingLocalOffset"/> is later mapped into SOURCE time
    /// by the renderer (<c>SegmentFrameComposer.MapLocalOffsetToSourceTime</c> /
    /// <c>ClampSourceTime</c>, already covered by <c>SegmentFrameComposerTransitionTests</c>'
    /// speed-factor cases). This test pins that the resolver itself is agnostic: a boundary
    /// clamps/activates identically regardless of the neighbouring segments' speed.
    /// </summary>
    [TestMethod]
    public void Resolve_IsAgnosticToSpeedFactor_OnBothNeighbours()
    {
        var config = new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromMilliseconds(400) };

        var normalSpeed = ModelWith(Video(4, speedFactor: 1.0), Video(4, config, speedFactor: 1.0));
        var fastOutgoing = ModelWith(Video(4, speedFactor: 2.0), Video(4, config, speedFactor: 1.0));
        var fastIncoming = ModelWith(Video(4, speedFactor: 1.0), Video(4, config, speedFactor: 3.0));

        var probeTime = TimeSpan.FromSeconds(4.2); // 200ms into the 400ms window.

        var r1 = TransitionResolver.Resolve(normalSpeed, probeTime);
        var r2 = TransitionResolver.Resolve(fastOutgoing, probeTime);
        var r3 = TransitionResolver.Resolve(fastIncoming, probeTime);

        foreach (var r in new[] { r1, r2, r3 })
        {
            Assert.IsTrue(r.Active);
            Assert.AreEqual(TimeSpan.FromMilliseconds(400), r.Duration);
            Assert.AreEqual(0.5, r.RawProgress, 1e-6);
        }
    }
}
