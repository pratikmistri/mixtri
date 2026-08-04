namespace Musio.Core.Audio;

/// <summary>
/// The equal-power (a.k.a. constant-power) crossfade curve shared by every consumer of a
/// transition's audio fade (<see cref="Export.AudioPlacement.FadeInDuration"/>/
/// <see cref="Export.AudioPlacement.FadeOutDuration"/>): <see cref="AudioPlaybackEngine"/>
/// for the live preview, and documented (but not applied — see
/// <see cref="Export.ExportAudioPlan"/>'s remarks) for the export mux.
/// </summary>
/// <remarks>
/// A LINEAR gain ramp (<c>out = 1-t</c>, <c>in = t</c>) sums to a perceptibly quieter
/// signal in the middle of the fade: at <c>t = 0.5</c> each side is at 0.5 gain, so the
/// summed POWER is <c>0.5² + 0.5² = 0.5</c> (-3dB below either side alone), an audible
/// "dip". An equal-power curve (<c>out = cos(t·π/2)</c>, <c>in = sin(t·π/2)</c>) keeps
/// <c>out² + in² == 1</c> at every <c>t</c> — see <see cref="Musio.Tests"/>'s
/// <c>ExportAudioPlanTransitionTests</c> for a test asserting exactly that identity — so
/// the combined perceived loudness stays constant across the whole dissolve.
/// </remarks>
public static class EqualPowerCrossfade
{
    /// <summary>
    /// Gain for the side that is FADING OUT, at progress <paramref name="t"/> (0 = the
    /// first instant of the fade, 1 = the last). 1 at <c>t=0</c>, 0 at <c>t=1</c>.
    /// <paramref name="t"/> is clamped to <c>[0, 1]</c>.
    /// </summary>
    public static double OutGain(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Math.Cos(t * Math.PI / 2);
    }

    /// <summary>
    /// Gain for the side that is FADING IN, at progress <paramref name="t"/> (0 = the
    /// first instant of the fade, 1 = the last). 0 at <c>t=0</c>, 1 at <c>t=1</c>.
    /// <paramref name="t"/> is clamped to <c>[0, 1]</c>.
    /// </summary>
    public static double InGain(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Math.Sin(t * Math.PI / 2);
    }

    /// <summary>
    /// The combined gain that <paramref name="windows"/> imply at <paramref name="timeSeconds"/>,
    /// measured in the source file's own time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules, in order of precedence:
    /// a window that CONTAINS the instant governs it (overlapping windows multiply); otherwise
    /// the most recently STARTED window that has already finished sets the resting level — a
    /// completed fade-out rests at silence, a completed fade-in at full volume; and a window
    /// that has not started yet contributes nothing.
    /// </para>
    /// <para>
    /// A completed fade-out resting at silence is deliberate: the placement it represents has
    /// conceptually stopped playing there (mirroring how the exporter truncates
    /// <c>TakeDuration</c> at exactly that instant), so snapping back to full volume would be
    /// an audible pop straight after every crossfade. But that resting level must be
    /// SUPERSEDABLE — an earlier implementation latched the gain to zero for the remainder of
    /// the file, so a later fade-in multiplied into that zero and could never recover, making
    /// "fade out → silent gap → fade in" impossible to express even though multiple windows per
    /// file are explicitly supported (a reordered timeline can use one recording either side of
    /// another). Precedence is decided by window start time, not by list order, so callers need
    /// not pre-sort.
    /// </para>
    /// <para>
    /// Lives here rather than inside the playback provider so it is directly testable: it is
    /// pure arithmetic, and it runs once per audio FRAME on the real-time thread, so it is also
    /// written to avoid allocation (indexed loop, no <c>foreach</c> over the interface-typed
    /// list, which would box an enumerator on every call).
    /// </para>
    /// </remarks>
    public static double GainAt(IReadOnlyList<AudioFadeWindow> windows, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(windows);

        double gain = 1.0;
        bool insideAnyWindow = false;
        double latestCompletedStart = double.NegativeInfinity;
        bool restingSilent = false;

        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            double start = window.Start.TotalSeconds;
            double end = window.End.TotalSeconds;

            // Not started yet: unrelated (later) content in the same continuously-played file.
            if (timeSeconds < start) continue;

            if (timeSeconds >= end)
            {
                if (start >= latestCompletedStart)
                {
                    latestCompletedStart = start;
                    restingSilent = !window.IsFadeIn;
                }
                continue;
            }

            insideAnyWindow = true;
            double duration = window.Duration.TotalSeconds;
            double t = duration > 0 ? (timeSeconds - start) / duration : 1.0;
            gain *= window.IsFadeIn ? InGain(t) : OutGain(t);
        }

        // An in-progress window is what is shaping this instant, so it supersedes whatever an
        // earlier, already-finished window left behind.
        if (insideAnyWindow) return gain;

        return restingSilent ? 0.0 : gain;
    }
}
