using NAudio.Wave;

namespace Mixtri.Core.Audio;

/// <summary>
/// Applies a gain to <paramref name="source"/> that glides linearly toward its target instead
/// of stepping, so changing a level (a mute button, a segment crossing) never produces the
/// click of a discontinuity.
/// </summary>
/// <remarks>
/// The target is set from any thread; the ramp itself advances on the reading (audio) thread,
/// one step per frame, so a full 0..1 swing takes <c>rampSeconds</c>. The initial gain equals
/// the initial target, i.e. there is no fade-in on construction.
/// </remarks>
public sealed class GainRampSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _step;
    private volatile float _target;
    private float _gain;

    public GainRampSampleProvider(ISampleProvider source, float initialGain, double rampSeconds = 0.005)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        int rampFrames = Math.Max(1, (int)(source.WaveFormat.SampleRate * Math.Max(0, rampSeconds)));
        _step = 1f / rampFrames;
        _target = _gain = Math.Clamp(initialGain, 0f, 1f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The level the output is gliding toward (or already at).</summary>
    public float Target => _target;

    /// <summary>Sets the level to glide toward; clamped to 0..1.</summary>
    public void SetTarget(float gain) => _target = Math.Clamp(gain, 0f, 1f);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        float target = _target;
        float gain = _gain;

        // Steady unity: leave the samples untouched.
        if (gain == 1f && target == 1f) return read;

        int frames = read / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            if (gain < target) gain = Math.Min(target, gain + _step);
            else if (gain > target) gain = Math.Max(target, gain - _step);

            int baseIndex = offset + frame * _channels;
            for (int ch = 0; ch < _channels; ch++)
                buffer[baseIndex + ch] *= gain;
        }

        // A trailing partial frame (never expected) takes the current gain.
        for (int i = frames * _channels; i < read; i++)
            buffer[offset + i] *= gain;

        _gain = gain;
        return read;
    }
}
