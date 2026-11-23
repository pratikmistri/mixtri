namespace Mixtri.Core.Capture;

/// <summary>
/// Keeps a captured audio stream on the wall clock by working out how much silence has to
/// be written where the endpoint delivered nothing.
/// </summary>
/// <remarks>
/// <para>
/// WASAPI loopback delivers no packets at all while nothing is playing, so a WAV built only
/// from <c>DataAvailable</c> buffers collapses every silent stretch to zero length and all
/// later audio drifts early against the video. The same happens to any stream while the
/// recording is paused, because the screen capture keeps its clock running across a pause
/// (the paused span is replayed as duplicate frames).
/// </para>
/// <para>
/// Padding is only inserted after a real delivery gap (no buffer written for
/// <see cref="ArrivalGapSeconds"/>), never between back-to-back buffers. That keeps normal
/// buffer jitter and the slow drift between the device clock and the performance counter
/// from injecting audible drop-outs into continuous audio: any accumulated drift is
/// corrected at the next silent point instead, where the inserted silence is inaudible.
/// </para>
/// <para>Not thread-safe; callers serialize access.</para>
/// </remarks>
internal sealed class AudioClockAligner
{
    /// <summary>Delivery gap after which the stream is considered to have gone silent.</summary>
    internal const double ArrivalGapSeconds = 0.2;

    /// <summary>
    /// A computed pad longer than this is treated as a clock error and skipped, so a bogus
    /// timestamp can never write gigabytes of silence.
    /// </summary>
    internal const double MaxPadSeconds = 2 * 3600;

    private readonly int _sampleRate;
    private readonly long _ticksPerSecond;
    private long _framesWritten;
    private long _lastWriteTicks;

    public AudioClockAligner(int sampleRate, long ticksPerSecond)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));

        _sampleRate = sampleRate;
        _ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Audio frames (samples per channel) accounted for so far, including padding.</summary>
    public long FramesWritten => _framesWritten;

    /// <summary>
    /// Frames of silence to write before a buffer of <paramref name="incomingFrames"/> that
    /// arrived at <paramref name="nowTicks"/>, so that the buffer ends at the wall-clock
    /// position it was captured at. <paramref name="originTicks"/> is the timestamp that
    /// WAV position 0 corresponds to (0 when not yet known).
    /// </summary>
    public long ComputeLeadingSilence(long originTicks, long nowTicks, long incomingFrames)
    {
        if (originTicks == 0 || nowTicks <= originTicks)
            return 0;

        bool afterGap = _lastWriteTicks == 0
            || nowTicks - _lastWriteTicks > (long)(ArrivalGapSeconds * _ticksPerSecond);
        if (!afterGap)
            return 0;

        long deficit = ExpectedFrames(originTicks, nowTicks) - _framesWritten - Math.Max(0, incomingFrames);
        return ClampPad(deficit);
    }

    /// <summary>Frames of silence needed so the stream reaches <paramref name="nowTicks"/>.</summary>
    public long ComputeTrailingSilence(long originTicks, long nowTicks)
    {
        if (originTicks == 0 || nowTicks <= originTicks)
            return 0;

        return ClampPad(ExpectedFrames(originTicks, nowTicks) - _framesWritten);
    }

    /// <summary>Accounts for frames that were actually written.</summary>
    public void Commit(long silenceFrames, long incomingFrames, long nowTicks)
    {
        _framesWritten += Math.Max(0, silenceFrames) + Math.Max(0, incomingFrames);
        _lastWriteTicks = nowTicks;
    }

    private long ExpectedFrames(long originTicks, long nowTicks)
        => (long)((nowTicks - originTicks) * (double)_sampleRate / _ticksPerSecond);

    private long ClampPad(long deficit)
    {
        if (deficit <= 0)
            return 0;

        long max = (long)(MaxPadSeconds * _sampleRate);
        return deficit > max ? 0 : deficit;
    }
}
