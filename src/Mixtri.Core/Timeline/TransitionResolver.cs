namespace Mixtri.Core.Timeline;

using Mixtri.Core.Processing;

/// <summary>
/// The resolved transition state for a given output time, produced by
/// <see cref="TransitionResolver.Resolve(TimelineModel, TimeSpan)"/>.
/// </summary>
/// <param name="Active">True when <c>outputTime</c> lies inside an active transition window.</param>
/// <param name="Type">The transition effect to render. Meaningless when <paramref name="Active"/> is false.</param>
/// <param name="Duration">
/// The clamped, effective duration of the transition — i.e. the configured duration after being
/// shrunk to fit inside half of each neighbouring segment (and any <c>maxDurationOverride</c>).
/// This is the denominator used to compute <see cref="RawProgress"/>, not the segment's configured
/// (pre-clamp) duration.
/// </param>
/// <param name="RawProgress">
/// Linear progress through the transition window, 0 at the first instant of the window and
/// approaching (never reaching) 1 at its end. 0 = fully outgoing, 1 = fully incoming.
/// </param>
/// <param name="EasedProgress"><see cref="RawProgress"/> passed through the configured
/// <see cref="TransitionConfig.Easing"/> curve. Renderers should use this value, not
/// <see cref="RawProgress"/>, so the perceived speed of the dissolve matches what was configured.</param>
/// <param name="IncomingSegment">The segment being transitioned into (the one that owns
/// <see cref="TimelineSegment.InTransition"/>, or would if the legacy fallback applied).</param>
/// <param name="OutgoingSegment">The segment being transitioned away from (the previous segment
/// on the timeline).</param>
/// <param name="OutgoingLocalOffset">
/// <para>
/// The time, measured from <see cref="OutgoingSegment"/>'s own start, at which the outgoing
/// segment should be sampled for this instant of the transition.
/// </para>
/// <para>
/// <b>This is the single most important field for callers to understand.</b> Unlike the legacy
/// <see cref="SlideTransitions"/> shim — which freezes the outgoing segment on its last frame for
/// the whole dissolve — <see cref="TransitionResolver"/> keeps the outgoing segment ROLLING PAST
/// its own cut point: <c>OutgoingLocalOffset = OutgoingSegment.Duration + local</c>, where
/// <c>local</c> is how far <c>outputTime</c> is into the transition window. That means this value
/// deliberately EXCEEDS <c>OutgoingSegment.Duration</c> by up to <see cref="Duration"/> — it is
/// <em>not</em> a valid index into the outgoing segment's own output-timeline duration.
/// </para>
/// <para>
/// Callers (the preview and export renderers) must map this offset into outgoing-segment source
/// time exactly as they map any other local offset within that segment, and then clamp against
/// whatever source handle/frame range is actually available for it (e.g. the source video's
/// decoded duration), holding the last available frame once the source runs out. This lets a
/// transition dip into footage that exists past the edited cut point instead of freezing on a
/// single stale frame — but only when that footage exists; when it doesn't, clamping degrades
/// gracefully back to a frozen last frame, which is never worse than the legacy behaviour.
/// </para>
/// </param>
public readonly record struct TransitionResolution(
    bool Active,
    TransitionType Type,
    TimeSpan Duration,
    double RawProgress,
    double EasedProgress,
    TimelineSegment? IncomingSegment,
    TimelineSegment? OutgoingSegment,
    TimeSpan OutgoingLocalOffset)
{
    /// <summary>The inactive/no-transition result. All fields at their defaults.</summary>
    public static readonly TransitionResolution None = new(
        Active: false,
        Type: TransitionType.None,
        Duration: TimeSpan.Zero,
        RawProgress: 0,
        EasedProgress: 0,
        IncomingSegment: null,
        OutgoingSegment: null,
        OutgoingLocalOffset: TimeSpan.Zero);
}

/// <summary>
/// Resolves the transition (if any) active at a given output-timeline instant. This is the single
/// shared source of truth for "what transition, if any, is happening right now" so the live
/// preview and the exporter — two entirely separate rendering code paths in this repo that have
/// historically diverged (see <see cref="SlideTransitions"/>'s remarks) — dissolve at identical
/// instants using identical parameters.
/// Transitions are resolved only across the contiguous base track; overlay tracks are absolute
/// full-frame covers, not neighbouring edits that can own a boundary.
/// </summary>
/// <remarks>
/// This class is deliberately pure: no I/O, no Win2D, no UI types. It only reads
/// <see cref="TimelineModel"/> and <see cref="TimelineSegment"/> data and does arithmetic.
/// <para>
/// <b>Legacy fallback.</b> Today's hardcoded behaviour — a 500&#160;ms linear crossfade at any
/// boundary touching a <see cref="TextSlideSegment"/>, and a hard cut everywhere else — predates
/// <see cref="TimelineSegment.InTransition"/> actually being populated by anything. To keep every
/// existing project (and every existing test) behaving identically, <see cref="Resolve"/>
/// reproduces that exact fallback whenever <see cref="TimelineSegment.InTransition"/> is null.
/// Once producers start writing <see cref="TimelineSegment.InTransition"/> (later tasks), the
/// fallback stops applying for that boundary because an explicit config always wins — including
/// an explicit <see cref="TransitionType.None"/>, which must suppress even the legacy fallback.
/// </para>
/// </remarks>
public static class TransitionResolver
{
    /// <summary>
    /// Resolves the active transition (if any) at <paramref name="outputTime"/>.
    /// </summary>
    public static TransitionResolution Resolve(TimelineModel timeline, TimeSpan outputTime)
        => Resolve(timeline, outputTime, maxDurationOverride: null);

    /// <summary>
    /// Overload allowing an additional cap on the transition duration (mainly for tests). When
    /// supplied, the effective duration is clamped against it in addition to the usual clamping
    /// against half of each neighbouring segment's duration.
    /// </summary>
    public static TransitionResolution Resolve(TimelineModel timeline, TimeSpan outputTime, TimeSpan? maxDurationOverride)
        => Resolve(timeline, outputTime, maxDurationOverride, ignoreExplicitConfig: false, legacyDuration: null);

    /// <summary>
    /// Resolves using ONLY the legacy fallback rule (a linear crossfade at any boundary touching
    /// a <see cref="TextSlideSegment"/>), ignoring <see cref="TimelineSegment.InTransition"/>
    /// entirely. <paramref name="maxDuration"/> is the legacy <em>configured</em> length, still
    /// clamped to half of each neighbour as always.
    /// </summary>
    /// <remarks>
    /// Exists solely for the <see cref="SlideTransitions"/> shim, whose own duration overload has
    /// always meant "use this as the crossfade length" — not "cap the 500&#160;ms default" — so it
    /// is threaded in as the configured duration rather than as an extra upper bound. Anything
    /// else would silently stop that overload from ever producing a window longer than the
    /// default. Delete this along with <see cref="SlideTransitions"/> once nothing calls it.
    /// </remarks>
    internal static TransitionResolution ResolveLegacyOnly(
        TimelineModel timeline, TimeSpan outputTime, TimeSpan maxDuration)
        => Resolve(timeline, outputTime, maxDurationOverride: null, ignoreExplicitConfig: true, legacyDuration: maxDuration);

    private static TransitionResolution Resolve(
        TimelineModel timeline,
        TimeSpan outputTime,
        TimeSpan? maxDurationOverride,
        bool ignoreExplicitConfig,
        TimeSpan? legacyDuration)
    {
        var segments = timeline.BaseSegments.ToList();

        int index = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (outputTime >= segments[i].Start && outputTime < segments[i].End)
            {
                index = i;
                break;
            }
        }

        // No current segment, or it's the very first one (nothing to dissolve from).
        if (index <= 0)
            return TransitionResolution.None;

        var incoming = segments[index];
        var outgoing = segments[index - 1];

        TransitionConfig config;
        bool isLegacyFallback;
        if (!ignoreExplicitConfig && incoming.InTransition is { } explicitConfig)
        {
            // An explicit config always wins over the legacy fallback — including an explicit
            // "None", which must be able to silence the fallback for slide boundaries too.
            if (explicitConfig.Type == TransitionType.None)
                return TransitionResolution.None;

            config = explicitConfig;
            isLegacyFallback = false;
        }
        else
        {
            // Legacy fallback: reproduce SlideTransitions' exact pre-existing behaviour so nothing
            // changes for boundaries nobody has configured yet. Today's crossfade renderer
            // (TransitionRenderer.RenderCrossFade) uses raw, un-eased progress, so the fallback
            // must use Linear easing to stay pixel-identical.
            if (incoming is not TextSlideSegment && outgoing is not TextSlideSegment)
                return TransitionResolution.None;

            config = new TransitionConfig
            {
                Type = TransitionType.CrossFade,
#pragma warning disable CS0618 // Intentional: the fallback duration must stay pinned to
                                // SlideTransitions.DefaultDuration exactly, even though that type
                                // is otherwise obsolete — this is the one place the dependency is
                                // permanent, not a leftover from an unfinished migration.
                Duration = legacyDuration ?? SlideTransitions.DefaultDuration,
#pragma warning restore CS0618
                Easing = TransitionEasing.Linear,
            };
            isLegacyFallback = true;
        }

        var duration = Half(incoming.Duration) < Half(outgoing.Duration)
            ? Half(incoming.Duration)
            : Half(outgoing.Duration);
        duration = duration < config.Duration ? duration : config.Duration;
        if (maxDurationOverride is { } max && max < duration)
            duration = max;
        if (duration <= TimeSpan.Zero)
            return TransitionResolution.None;

        var local = outputTime - incoming.Start;
        if (local < TimeSpan.Zero || local >= duration)
            return TransitionResolution.None;

        // Computed as an exact tick ratio rather than dividing TotalSeconds: both operands are
        // integer tick counts, so this avoids the double-rounding of converting each to seconds
        // first, and keeps the window's exclusive upper bound exact at its final tick.
        double rawProgress = Math.Clamp((double)local.Ticks / duration.Ticks, 0, 1);
        double easedProgress = Ease(config.Easing, rawProgress);

        // An EXPLICITLY configured transition rolls the outgoing segment past its own cut point
        // for the length of the dissolve — see the OutgoingLocalOffset doc comment.
        //
        // The LEGACY fallback must not. Before this feature existed, an unconfigured
        // slide-adjacent boundary froze the outgoing side on its final frame
        // (SlideTransitions pinned it to incoming.Start - 1 tick). Rolling it instead would
        // sample at and past the cut, so a video the user had TRIMMED would start revealing
        // the footage they trimmed away — silently changing how every already-saved project
        // renders, which is exactly what the fallback exists to prevent. Handles are opt-in:
        // you get them by choosing a transition, not by never having touched one.
        var outgoingLocalOffset = isLegacyFallback
            ? FrozenOutgoingOffset(outgoing)
            : outgoing.Duration + local;

        return new TransitionResolution(
            Active: true,
            Type: config.Type,
            Duration: duration,
            RawProgress: rawProgress,
            EasedProgress: easedProgress,
            IncomingSegment: incoming,
            OutgoingSegment: outgoing,
            OutgoingLocalOffset: outgoingLocalOffset);
    }

    /// <summary>
    /// The outgoing segment's final instant, expressed as an offset from its own start — the
    /// pre-feature "hold the last frame" sampling point, kept for the legacy fallback.
    /// </summary>
    private static TimeSpan FrozenOutgoingOffset(TimelineSegment outgoing)
    {
        var offset = outgoing.Duration - TimeSpan.FromTicks(1);
        return offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
    }

    /// <summary>
    /// Applies an easing curve to linear progress <paramref name="t"/> (clamped to 0..1).
    /// </summary>
    public static double Ease(TransitionEasing easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return easing switch
        {
            TransitionEasing.Linear => t,
            TransitionEasing.EaseIn => CubicBezierEasing.EaseIn((float)t),
            TransitionEasing.EaseOut => CubicBezierEasing.EaseOut((float)t),
            TransitionEasing.EaseInOut => CubicBezierEasing.EaseInOut((float)t),
            _ => t,
        };
    }

    /// <summary>Truncates a duration in half, matching <see cref="SlideTransitions"/>' exact
    /// tick-truncating behaviour so clamping stays identical between the two.</summary>
    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);
}
