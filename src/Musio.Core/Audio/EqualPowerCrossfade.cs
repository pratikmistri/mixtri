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
}
