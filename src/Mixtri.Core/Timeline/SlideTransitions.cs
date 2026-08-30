namespace Mixtri.Core.Timeline;

/// <summary>
/// Computes automatic crossfade transitions around <see cref="TextSlideSegment"/>
/// boundaries on the contiguous base track so a text slide dissolves into (and
/// out of) its neighbouring segment instead of hard-cutting. The transition
/// occupies the leading edge of the <em>incoming</em> segment; the outgoing
/// segment is held at its final instant and cross-dissolved underneath.
///
/// This is a pure function of the timeline so it can be shared, identically,
/// between the live preview and the exporter (the two rendering paths that
/// otherwise diverge).
/// </summary>
/// <remarks>
/// <b>This class is retained only until the preview (<c>EditorPage</c>) and export
/// (<c>SegmentFrameComposer</c>) paths migrate to <see cref="TransitionResolver"/> directly.</b>
/// Its logic is now a thin adapter over <see cref="TransitionResolver"/>: <see cref="Resolve"/>
/// delegates there and translates the general result back into this type's legacy shape,
/// including the frozen-outgoing-instant semantics those two call sites still depend on (as
/// opposed to <see cref="TransitionResolver"/>'s newer "roll past the cut" offset). It resolves
/// through <c>TransitionResolver.ResolveLegacyOnly</c>, which ignores
/// <see cref="TimelineSegment.InTransition"/> outright, and it re-applies the "only slide-touching
/// boundaries" filter explicitly — so this shim's behaviour cannot change even once other code
/// starts setting <see cref="TimelineSegment.InTransition"/>. Do not add new callers of this type;
/// use <see cref="TransitionResolver"/> instead.
/// </remarks>
[Obsolete("Use TransitionResolver. Retained until the preview and export paths migrate.")]
public static class SlideTransitions
{
    /// <summary>Default crossfade length when a boundary is adjacent to a slide.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>The resolved transition state for a given output time.</summary>
    /// <param name="Active">True when the output time is inside a crossfade window.</param>
    /// <param name="Progress">0 = fully outgoing, 1 = fully incoming.</param>
    /// <param name="OutgoingTime">
    /// Output time at which the outgoing (previous) segment should be sampled —
    /// its final frame, held for the duration of the dissolve.
    /// </param>
    public readonly record struct Result(bool Active, double Progress, TimeSpan OutgoingTime)
    {
        public static readonly Result None = new(false, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Resolves whether <paramref name="outputTime"/> lies within a crossfade
    /// window on the leading edge of a segment whose boundary touches a text
    /// slide. Returns <see cref="Result.None"/> otherwise.
    /// </summary>
    public static Result Resolve(TimelineModel timeline, TimeSpan outputTime)
        => Resolve(timeline, outputTime, DefaultDuration);

    /// <summary>
    /// Overload allowing the crossfade length to be specified (mainly for tests).
    /// </summary>
    public static Result Resolve(TimelineModel timeline, TimeSpan outputTime, TimeSpan maxDuration)
    {
        var resolution = TransitionResolver.ResolveLegacyOnly(timeline, outputTime, maxDuration);
        if (!resolution.Active)
            return Result.None;

        var incoming = resolution.IncomingSegment!;
        var outgoing = resolution.OutgoingSegment!;

        // This shim must keep responding only to boundaries that touch a text slide, exactly as
        // it always has, and must stay completely inert to TimelineSegment.InTransition — which
        // ResolveLegacyOnly guarantees by ignoring it. Both matter while the migration is
        // half-finished: an explicit config set by the new UI must NOT be rendered through this
        // legacy frozen-outgoing path, and video→video boundaries must keep hard-cutting here.
        if (incoming is not TextSlideSegment && outgoing is not TextSlideSegment)
            return Result.None;

        // Legacy semantics: hold the outgoing segment at its final instant for the whole
        // dissolve, rather than TransitionResolver's newer "roll past the cut" offset. The two
        // call sites still relying on this shim have not migrated to the rolling semantics yet.
        var outgoingTime = incoming.Start - TimeSpan.FromTicks(1);
        if (outgoingTime < outgoing.Start)
            outgoingTime = outgoing.Start;

        // Progress is explicitly the raw (un-eased) value: the legacy fallback configures Linear
        // easing, so raw and eased are numerically equal here, but this shim's contract has
        // always been "linear progress" and stays that way regardless of what
        // TransitionResolver's easing defaults are.
        return new Result(true, resolution.RawProgress, outgoingTime);
    }
}
