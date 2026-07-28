namespace Musio.Core.Timeline;

/// <summary>
/// Computes automatic crossfade transitions around <see cref="TextSlideSegment"/>
/// boundaries so a text slide dissolves into (and out of) its neighbouring
/// segment instead of hard-cutting. The transition occupies the leading edge of
/// the <em>incoming</em> segment; the outgoing segment is held at its final
/// instant and cross-dissolved underneath.
///
/// This is a pure function of the timeline so it can be shared, identically,
/// between the live preview and the exporter (the two rendering paths that
/// otherwise diverge).
/// </summary>
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
        var segments = timeline.Segments;

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
            return Result.None;

        var current = segments[index];
        var previous = segments[index - 1];

        // Only soften boundaries that actually touch a text slide.
        if (current is not TextSlideSegment && previous is not TextSlideSegment)
            return Result.None;

        // Keep the window inside both neighbours so dissolves never overlap.
        var duration = Min(maxDuration, Half(current.Duration), Half(previous.Duration));
        if (duration <= TimeSpan.Zero)
            return Result.None;

        var local = outputTime - current.Start;
        if (local < TimeSpan.Zero || local >= duration)
            return Result.None;

        double progress = local.TotalSeconds / duration.TotalSeconds;

        // Hold the outgoing segment at its final instant for the whole dissolve.
        var outgoingTime = current.Start - TimeSpan.FromTicks(1);
        if (outgoingTime < previous.Start)
            outgoingTime = previous.Start;

        return new Result(true, Math.Clamp(progress, 0, 1), outgoingTime);
    }

    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);

    private static TimeSpan Min(TimeSpan a, TimeSpan b, TimeSpan c)
    {
        var m = a < b ? a : b;
        return m < c ? m : c;
    }
}
