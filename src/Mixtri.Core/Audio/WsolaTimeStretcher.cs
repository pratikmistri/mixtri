namespace Mixtri.Core.Audio;

/// <summary>
/// Receives one block of finished, interleaved output samples from
/// <see cref="WsolaTimeStretcher"/>.
/// </summary>
/// <remarks>
/// A push interface rather than a returned array: a speed-adjusted segment can be minutes
/// long (a 10-minute stereo mic take is ~57M samples), and the renderer writes each block
/// straight to a <c>WaveFileWriter</c> instead of ever holding the whole stretched signal.
/// The span is only valid for the duration of the call — copy anything you keep.
/// </remarks>
public delegate void WsolaOutputWriter(ReadOnlySpan<float> block);

/// <summary>
/// WSOLA (waveform-similarity overlap-add) time-stretching: changes how long a signal
/// lasts <b>without</b> changing its pitch.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a speed-adjusted <see cref="Timeline.VideoSegment"/> keep its recorded
/// audio. Naively resampling the audio (the "chipmunk" fix) would re-time it correctly but
/// transpose every frequency by the same factor; WSOLA instead re-uses the original samples
/// at their original rate and only changes how often successive grains are laid down,
/// choosing each grain's position by cross-correlating against the previous grain's tail so
/// the waveforms line up where they are spliced. Pitch is therefore preserved exactly —
/// see <c>WsolaTimeStretcherTests</c>, which pins a sine's frequency across the stretch.
/// </para>
/// <para>
/// <b>Pure by design — no file, WinRT or NAudio I/O lives here.</b> Samples in, samples out,
/// which is what makes it unit-testable on a host with no Media Foundation. The I/O layer
/// (reading a segment's source range, writing the stretched WAV, caching it) is
/// <see cref="SegmentAudioRenderer"/>.
/// </para>
/// <para>
/// <b>Streaming, stateful.</b> <see cref="Process"/> may be called repeatedly with
/// consecutive blocks of input; <see cref="Flush"/> emits the tail. State carried between
/// calls is the un-consumed input, the previous grain's tail (the correlation template) and
/// the fractional part of the analysis hop — so the average rate stays exactly
/// <c>1 / speed</c> however the caller happens to chunk its reads.
/// </para>
/// </remarks>
public sealed class WsolaTimeStretcher
{
    /// <summary>Speeds within this distance of 1.0 are passed through untouched.</summary>
    public const double SpeedEpsilon = 0.001;

    /// <summary>Grain length. 40ms at 48kHz is the classic WSOLA sequence size.</summary>
    private const double SequenceMs = 40.0;

    /// <summary>
    /// Cross-fade length between consecutive grains (50% of the grain), which is also the
    /// length of the correlation template.
    /// </summary>
    private const double OverlapMs = 20.0;

    /// <summary>How far either side of the nominal analysis position a grain may be nudged.</summary>
    private const double SeekMs = 10.0;

    /// <summary>
    /// Candidate stride for the first (coarse) correlation pass. A full 1-frame scan of a
    /// ±10ms window is ~960 candidates x 960 taps per grain, which is a second of CPU per
    /// ten seconds of audio for no audible gain; the coarse pass finds the right period and
    /// the refinement pass below lands on the exact sample.
    /// </summary>
    private const int CoarseStride = 4;

    private readonly int _channels;
    private readonly double _speed;
    private readonly int _sequence;
    private readonly int _overlap;
    private readonly int _seek;
    private readonly int _required;
    private readonly double _analysisHop;
    private readonly float[] _fadeIn;

    /// <summary>Un-consumed input, interleaved.</summary>
    private float[] _buffer;

    /// <summary>Channel-summed view of <see cref="_buffer"/>, used only for correlation.</summary>
    private float[] _mono;

    /// <summary>Frames currently held in <see cref="_buffer"/>.</summary>
    private int _frames;

    /// <summary>Tail of the previously emitted grain: the template the next grain matches.</summary>
    private readonly float[] _template;
    private readonly float[] _templateMono;

    /// <summary>Fractional remainder of the analysis hop, carried between iterations.</summary>
    private double _hopRemainder;

    /// <summary>
    /// Input the analysis hop still owes but that has not arrived yet. At high speeds one
    /// hop (e.g. 200ms at 10x) is longer than the whole buffer, so the skip has to survive
    /// until the next <see cref="Process"/> call instead of being silently truncated —
    /// truncating it would make the stretch run slower than the speed asked for.
    /// </summary>
    private int _skipDebt;

    private bool _primed;

    /// <summary>Scratch for the cross-faded head of a grain; reused to keep the loop allocation-free.</summary>
    private readonly float[] _blend;

    /// <summary>
    /// Samples of an incomplete frame left over by the last <see cref="Process"/> call, held
    /// until the next one completes it.
    /// </summary>
    private readonly float[] _partialFrame;
    private int _partialFrameLength;

    /// <param name="sampleRate">Sample rate of the signal, in Hz.</param>
    /// <param name="channels">Interleaved channel count (1 = mono, 2 = stereo).</param>
    /// <param name="speed">
    /// Playback speed. <c>2.0</c> halves the duration, <c>0.5</c> doubles it — i.e. the same
    /// number the segment's <see cref="Timeline.VideoSegment.SpeedFactor"/> carries.
    /// </param>
    public WsolaTimeStretcher(int sampleRate, int channels, double speed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be finite and positive.");

        _channels = channels;
        _speed = speed;

        // Every window is derived from the real sample rate: hard-coded sample counts would
        // silently become 3x too short on a 16kHz mic capture.
        _sequence = FramesFor(sampleRate, SequenceMs);
        _overlap = Math.Min(FramesFor(sampleRate, OverlapMs), _sequence / 2);
        _seek = FramesFor(sampleRate, SeekMs);
        _required = _sequence + (2 * _seek);
        _analysisHop = (_sequence - _overlap) * speed;

        _fadeIn = new float[_overlap];
        for (int i = 0; i < _overlap; i++)
        {
            // Raised cosine. Complementary with its own mirror (w + (1-w) == 1), so two
            // waveform-aligned grains sum back to unity gain instead of dipping mid-splice.
            double phase = (i + 0.5) / _overlap;
            _fadeIn[i] = (float)(0.5 * (1.0 - Math.Cos(Math.PI * phase)));
        }

        _template = new float[_overlap * channels];
        _templateMono = new float[_overlap];
        _blend = new float[_overlap * channels];
        _partialFrame = new float[channels];
        _buffer = new float[Math.Max(_required * 2, 1) * channels];
        _mono = new float[Math.Max(_required * 2, 1)];
    }

    /// <summary>Frames emitted for every <see cref="_analysisHop"/> frames consumed.</summary>
    public int SynthesisHop => _sequence - _overlap;

    /// <summary>
    /// Whether <paramref name="speed"/> is far enough from 1.0 to be worth stretching at all.
    /// </summary>
    public static bool IsStretchNeeded(double speed) =>
        !double.IsNaN(speed) && speed > 0 && Math.Abs(speed - 1.0) > SpeedEpsilon;

    /// <summary>
    /// Time-stretches a complete in-memory signal in one call.
    /// </summary>
    /// <param name="samples">Interleaved input samples.</param>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="speed">Playback speed; see the constructor.</param>
    /// <returns>
    /// The stretched signal, whose length is <c>samples.Length / speed</c> to within one
    /// synthesis hop. Callers needing an exact length (the exporter does — its placement
    /// duration is already fixed) should pad or trim.
    /// </returns>
    public static float[] Stretch(ReadOnlySpan<float> samples, int channels, int sampleRate, double speed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        if (samples.Length == 0) return [];
        if (!IsStretchNeeded(speed)) return samples.ToArray();

        var stretcher = new WsolaTimeStretcher(sampleRate, channels, speed);
        int expected = (int)Math.Round(samples.Length / speed);
        var output = new List<float>(Math.Max(expected, 16));

        void Collect(ReadOnlySpan<float> block)
        {
            foreach (float sample in block) output.Add(sample);
        }

        stretcher.Process(samples, Collect);
        stretcher.Flush(Collect);
        return [.. output];
    }

    /// <summary>
    /// Consumes a block of interleaved input, pushing whatever output it completes to
    /// <paramref name="writer"/>. Blocks may be any length: a trailing partial frame is
    /// carried over and completed by the next call.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, WsolaOutputWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (input.Length == 0) return;

        if (!IsStretchNeeded(_speed))
        {
            writer(input);
            return;
        }

        Append(input);
        PayDownSkipDebt();

        while (_frames >= _required)
        {
            EmitGrain(writer, 2 * _seek);
            PayDownSkipDebt();
        }
    }

    /// <summary>Drops whatever the last hop could not, now that more input has arrived.</summary>
    private void PayDownSkipDebt()
    {
        if (_skipDebt <= 0) return;
        int drop = Math.Min(_skipDebt, _frames);
        Consume(drop);
        _skipDebt -= drop;
    }

    /// <summary>
    /// Emits the tail: the input left over once no full seek window remains, and then
    /// resets the stretcher.
    /// </summary>
    /// <remarks>
    /// The last stretch of a signal is finished with a narrowed seek window (there is no
    /// longer a full ±<see cref="_seek"/> of room to search in) and the final scrap —
    /// under one grain — is passed through at its natural rate, because a grain cannot be
    /// cut from less than a grain's worth of samples. When slowing down, that leaves the
    /// output a few tens of milliseconds short of the ideal length, which is why
    /// <see cref="SegmentAudioRenderer"/> pads to the requested duration rather than
    /// trusting this to land on it exactly.
    /// </remarks>
    public void Flush(WsolaOutputWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsStretchNeeded(_speed) || _frames <= 0)
        {
            Reset();
            return;
        }

        PayDownSkipDebt();

        while (_skipDebt == 0 && _frames >= _sequence)
        {
            EmitGrain(writer, Math.Min(2 * _seek, _frames - _sequence));
            PayDownSkipDebt();
        }

        int available = Math.Max(0, _frames - _skipDebt);
        int tail = Math.Min(available, (int)Math.Round(available / _speed));
        if (tail > 0)
            writer(_buffer.AsSpan(_skipDebt * _channels, tail * _channels));

        Reset();
    }

    /// <summary>Drops all buffered state so the instance can stretch a new signal.</summary>
    public void Reset()
    {
        _frames = 0;
        _hopRemainder = 0;
        _skipDebt = 0;
        _partialFrameLength = 0;
        _primed = false;
        Array.Clear(_template);
        Array.Clear(_templateMono);
    }

    /// <summary>
    /// Lays down one grain: find where it fits best, cross-fade it onto the previous one,
    /// copy the rest of it straight through, then advance the input by the analysis hop.
    /// </summary>
    /// <param name="maxOffset">
    /// Widest grain start the buffer can currently serve. Normally the full seek window;
    /// narrowed by <see cref="Flush"/> as the input runs out.
    /// </param>
    private void EmitGrain(WsolaOutputWriter writer, int maxOffset)
    {
        int offset = _primed ? FindBestOffset(maxOffset) : Math.Min(_seek, maxOffset);

        // The first grain has no predecessor to blend with. Seeding the template from the
        // grain itself makes the cross-fade below an identity, so the output starts on the
        // real waveform instead of fading up out of silence.
        if (!_primed)
        {
            _buffer.AsSpan(offset * _channels, _overlap * _channels).CopyTo(_template);
            _mono.AsSpan(offset, _overlap).CopyTo(_templateMono);
            _primed = true;
        }

        // Cross-fade region: previous grain's tail out, this grain's head in. Both were
        // chosen to be waveform-similar, so this splice is phase-coherent.
        for (int frame = 0; frame < _overlap; frame++)
        {
            float w = _fadeIn[frame];
            int src = (offset + frame) * _channels;
            int dst = frame * _channels;
            for (int c = 0; c < _channels; c++)
                _blend[dst + c] = (_template[dst + c] * (1f - w)) + (_buffer[src + c] * w);
        }
        writer(_blend);

        // Flat region: the middle of the grain, untouched.
        int flatFrames = _sequence - (2 * _overlap);
        if (flatFrames > 0)
            writer(_buffer.AsSpan((offset + _overlap) * _channels, flatFrames * _channels));

        int templateStart = offset + _sequence - _overlap;
        _buffer.AsSpan(templateStart * _channels, _overlap * _channels).CopyTo(_template);
        _mono.AsSpan(templateStart, _overlap).CopyTo(_templateMono);

        // Consume the NOMINAL hop from the front, not the matched offset: the search only
        // nudges where a grain is cut from, it must never change the average rate, or the
        // stretch would drift away from the requested speed over a long segment.
        _hopRemainder += _analysisHop;
        int skip = (int)_hopRemainder;
        _hopRemainder -= skip;
        skip = Math.Max(skip, 1);
        int dropped = Math.Min(skip, _frames);
        Consume(dropped);
        _skipDebt += skip - dropped;
    }

    /// <summary>
    /// Finds the candidate grain start, within ±<see cref="_seek"/> of the nominal position,
    /// whose head best matches the previous grain's tail.
    /// </summary>
    /// <remarks>
    /// Correlation runs on the channel-summed mono view and the winning offset is then
    /// applied to every channel identically. Matching each channel separately would let a
    /// stereo pair be cut at different instants, which smears the stereo image.
    /// </remarks>
    private int FindBestOffset(int maxOffset)
    {
        int last = Math.Max(0, maxOffset);
        int best = Math.Min(_seek, last);
        double bestScore = double.NegativeInfinity;

        for (int candidate = 0; candidate <= last; candidate += CoarseStride)
        {
            double score = Correlate(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        int from = Math.Max(0, best - CoarseStride + 1);
        int to = Math.Min(last, best + CoarseStride - 1);
        for (int candidate = from; candidate <= to; candidate++)
        {
            if (candidate % CoarseStride == 0) continue;   // already scored in the coarse pass
            double score = Correlate(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Normalised cross-correlation of the template against the mono signal at
    /// <paramref name="offset"/>. Normalising by the candidate's own energy stops the search
    /// from simply preferring the loudest part of the seek window.
    /// </summary>
    private double Correlate(int offset)
    {
        double dot = 0;
        double energy = 0;
        var mono = _mono;
        var template = _templateMono;

        for (int i = 0; i < _overlap; i++)
        {
            float value = mono[offset + i];
            dot += template[i] * value;
            energy += (double)value * value;
        }

        return energy <= 1e-12 ? 0 : dot / Math.Sqrt(energy);
    }

    /// <summary>Appends interleaved input, growing the buffer and its mono view as needed.</summary>
    /// <remarks>
    /// A caller is free to chunk its reads anywhere, including mid-frame, so any samples left
    /// over after the last whole frame are held in <see cref="_partialFrame"/> and prepended
    /// to the next block. Dropping them instead would not merely lose a sample: every later
    /// frame would be interleaved one channel out of phase, swapping left and right for the
    /// rest of the signal.
    /// </remarks>
    private void Append(ReadOnlySpan<float> input)
    {
        int carried = _partialFrameLength;
        int total = carried + input.Length;
        int newFrames = total / _channels;
        int leftover = total - (newFrames * _channels);

        if (newFrames <= 0)
        {
            // Not even one whole frame between the carry and this block: keep accumulating.
            input.CopyTo(_partialFrame.AsSpan(carried));
            _partialFrameLength = total;
            return;
        }

        int needed = (_frames + newFrames) * _channels;
        if (_buffer.Length < needed)
        {
            int capacity = Math.Max(needed, _buffer.Length * 2);
            Array.Resize(ref _buffer, capacity);
            Array.Resize(ref _mono, capacity / _channels);
        }

        int written = _frames * _channels;
        if (carried > 0)
        {
            _partialFrame.AsSpan(0, carried).CopyTo(_buffer.AsSpan(written));
            written += carried;
        }

        int consumed = (newFrames * _channels) - carried;
        input[..consumed].CopyTo(_buffer.AsSpan(written));

        if (leftover > 0)
            input[consumed..].CopyTo(_partialFrame.AsSpan(0));
        _partialFrameLength = leftover;

        for (int frame = 0; frame < newFrames; frame++)
        {
            int baseIndex = (_frames + frame) * _channels;
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buffer[baseIndex + c];
            _mono[_frames + frame] = sum;
        }

        _frames += newFrames;
    }

    /// <summary>Drops <paramref name="frames"/> frames from the front of the buffer.</summary>
    private void Consume(int frames)
    {
        if (frames <= 0) return;
        if (frames >= _frames)
        {
            _frames = 0;
            return;
        }

        int remaining = _frames - frames;
        Array.Copy(_buffer, frames * _channels, _buffer, 0, remaining * _channels);
        Array.Copy(_mono, frames, _mono, 0, remaining);
        _frames = remaining;
    }

    private static int FramesFor(int sampleRate, double milliseconds) =>
        Math.Max(1, (int)Math.Round(sampleRate * milliseconds / 1000.0));
}
