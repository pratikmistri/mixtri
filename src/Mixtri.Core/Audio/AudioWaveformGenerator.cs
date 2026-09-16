using System.Numerics;
using NAudio.Wave;

namespace Mixtri.Core.Audio;

/// <summary>
/// Maps a whole-file peak array onto the slice of it a trimmed clip actually plays.
/// </summary>
/// <remarks>
/// <para>
/// Waveforms for inserted audio are generated once per FILE and cached by path, because a
/// per-clip array would be stale the moment the clip is trimmed. Drawing therefore has to
/// pick out the window the clip plays — <b>positioning every peak by its own source time</b>
/// — rather than stretching the array across the clip's width.
/// </para>
/// <para>
/// Getting this wrong is not subtle but it IS silent: stretching makes a trim look like the
/// whole track was COMPRESSED into the shorter block, so the waveform no longer tells you
/// which part of the audio survived — the one job it has. That was a real shipped bug, which
/// is why this arithmetic lives here, pure and tested, instead of inline in a draw handler.
/// </para>
/// </remarks>
/// <param name="FirstIndex">First peak index touching the window.</param>
/// <param name="LastIndex">Last peak index touching the window (inclusive).</param>
/// <param name="PeakCount">Length of the peak array the indices refer to.</param>
/// <param name="FileSeconds">Source-time span the whole peak array covers.</param>
/// <param name="WindowStartSeconds">Where the clip starts inside the file.</param>
/// <param name="WindowDurationSeconds">How much of the file the clip plays.</param>
public readonly record struct WaveformWindow(
    int FirstIndex,
    int LastIndex,
    int PeakCount,
    double FileSeconds,
    double WindowStartSeconds,
    double WindowDurationSeconds)
{
    /// <summary>Whether this window contains anything to draw.</summary>
    public bool IsEmpty => PeakCount <= 0 || WindowDurationSeconds <= 0 || FileSeconds <= 0;

    /// <summary>
    /// Resolves the peak-index range covering <paramref name="windowStartSeconds"/> ..
    /// <c>+ windowDurationSeconds</c>. Returns an empty window for nonsensical inputs rather
    /// than throwing — a waveform is decoration, and a bad probe must not break the timeline.
    /// </summary>
    public static WaveformWindow Resolve(
        int peakCount, double fileSeconds, double windowStartSeconds, double windowDurationSeconds)
    {
        if (peakCount <= 0 || fileSeconds <= 0 || windowDurationSeconds <= 0)
            return new WaveformWindow(0, -1, 0, 0, 0, 0);

        if (windowStartSeconds < 0) windowStartSeconds = 0;

        int first = (int)(windowStartSeconds / fileSeconds * peakCount);
        // Ceiling on the far edge so the last partially covered peak is still drawn; without
        // it a short window can round down to nothing and render blank.
        int last = (int)Math.Ceiling(
            (windowStartSeconds + windowDurationSeconds) / fileSeconds * peakCount);

        first = Math.Clamp(first, 0, peakCount - 1);
        last = Math.Clamp(last, first, peakCount - 1);

        return new WaveformWindow(
            first, last, peakCount, fileSeconds, windowStartSeconds, windowDurationSeconds);
    }

    /// <summary>
    /// Where peak <paramref name="index"/> sits in the source file, in seconds.
    /// </summary>
    /// <remarks>
    /// Consumers position peaks by this ABSOLUTE source time rather than as a fraction of a
    /// clip's width. The difference is invisible at rest and decisive while trimming: with a
    /// fraction, dragging an edge rescales the whole window into the changing width, so the
    /// waveform appears to slide and squash instead of being clipped by the edge sweeping
    /// over it.
    /// </remarks>
    public double SecondsFor(int index)
    {
        if (IsEmpty) return double.NaN;
        return (double)index / PeakCount * FileSeconds;
    }

    /// <summary>
    /// Where peak <paramref name="index"/> sits inside the window, as a 0..1 fraction of the
    /// clip's width. Values outside 0..1 fall outside the played window — they exist because
    /// the index range is inclusive of the peaks straddling both edges.
    /// </summary>
    public double FractionFor(int index)
    {
        if (IsEmpty) return double.NaN;
        return (SecondsFor(index) - WindowStartSeconds) / WindowDurationSeconds;
    }
}

/// <summary>
/// Generates downsampled peak values from audio data for waveform visualization.
/// </summary>
public static class AudioWaveformGenerator
{
    internal const int MaximumReadSampleValues = 64 * 1024;

    /// <summary>
    /// Generate waveform peak samples from a WAV file.
    /// Returns normalized peak values (0..1) suitable for rendering.
    /// </summary>
    /// <param name="startSeconds">Seconds to skip from the start (e.g. audio pre-roll before video).</param>
    /// <param name="maxDurationSeconds">Max seconds of audio to process (0 = read to end).</param>
    public static float[] GenerateWaveform(string wavFilePath, int targetSampleCount,
        double startSeconds = 0, double maxDurationSeconds = 0)
        => GenerateWaveform(wavFilePath, targetSampleCount, startSeconds, maxDurationSeconds, CancellationToken.None);

    /// <summary>Generates WAV peaks while observing cancellation between bounded reads.</summary>
    public static float[] GenerateWaveform(string wavFilePath, int targetSampleCount,
        double startSeconds, double maxDurationSeconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (targetSampleCount <= 0) return [];

        using var reader = new AudioFileReader(wavFilePath);
        ct.ThrowIfCancellationRequested();

        if (startSeconds > 0)
        {
            long skipBytes = (long)(startSeconds * reader.WaveFormat.AverageBytesPerSecond);
            skipBytes -= skipBytes % reader.WaveFormat.BlockAlign;
            if (skipBytes >= reader.Length)
                return new float[targetSampleCount];
            if (skipBytes > 0)
                reader.Position = skipBytes;
        }

        long remainingBytes = reader.Length - reader.Position;

        // Cap to maxDurationSeconds to exclude post-roll
        if (maxDurationSeconds > 0)
        {
            long maxBytes = (long)(maxDurationSeconds * reader.WaveFormat.AverageBytesPerSecond);
            maxBytes -= maxBytes % reader.WaveFormat.BlockAlign;
            if (maxBytes > 0 && maxBytes < remainingBytes)
                remainingBytes = maxBytes;
        }

        return GenerateWaveform(reader, remainingBytes, targetSampleCount, ct);
    }

    internal static float[] GenerateWaveform(ISampleProvider reader, long remainingBytes, int targetSampleCount,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        if (targetSampleCount <= 0) return [];
        int channels = reader.WaveFormat.Channels;
        if (channels <= 0) throw new ArgumentException("Audio must have at least one channel.", nameof(reader));
        long monoSamples = remainingBytes / sizeof(float) / channels;
        if (monoSamples == 0) return [];

        long valuesPerBucket = Math.Max(1, monoSamples / targetSampleCount) * channels;
        int readCapacity = Math.Max(1, MaximumReadSampleValues / channels) * channels;
        var buffer = new float[(int)Math.Min(valuesPerBucket, readCapacity)];
        var result = new float[targetSampleCount];
        int bucketCount = 0;
        long bytesRead = 0;
        bool endOfStream = false;

        while (bucketCount < targetSampleCount && bytesRead < remainingBytes && !endOfStream)
        {
            float peak = 0f;
            long valuesRead = 0;
            bool exceededWindow = false;
            while (valuesRead < valuesPerBucket)
            {
                ct.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, valuesPerBucket - valuesRead);
                int read = reader.Read(buffer, 0, requested);
                ct.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    endOfStream = true;
                    break;
                }
                bytesRead += (long)read * sizeof(float);
                if (bytesRead > remainingBytes)
                {
                    exceededWindow = true;
                    break;
                }
                valuesRead += read;
                peak = FindPeak(buffer, read, peak);
            }
            if (exceededWindow || valuesRead == 0) break;
            result[bucketCount++] = peak;
        }

        ct.ThrowIfCancellationRequested();
        if (bucketCount < result.Length) Array.Resize(ref result, bucketCount);
        return result;
    }

    internal static float FindPeak(float[] samples, int count, float peak = 0f)
    {
        if (float.IsNaN(peak)) return peak;
        int index = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
        {
            int lanes = Vector<float>.Count;
            var maximum = new Vector<float>(peak);
            var infinity = new Vector<float>(float.PositiveInfinity);
            for (; index <= count - lanes; index += lanes)
            {
                var values = Vector.Abs(new Vector<float>(samples, index));
                // Preserve non-finite propagation; NaN sign/payload is not a portable Math.Max contract.
                if (!Vector.LessThanAll(values, infinity)) break;
                maximum = Vector.Max(maximum, values);
            }
            for (int lane = 0; lane < lanes; lane++)
                peak = Math.Max(peak, maximum[lane]);
        }
        for (; index < count; index++)
            peak = Math.Max(peak, Math.Abs(samples[index]));
        return peak;
    }

    /// <summary>
    /// Generate waveform peak samples from raw PCM audio bytes.
    /// Supports 8-bit, 16-bit, 24-bit, and 32-bit samples.
    /// Returns normalized peak values (0..1).
    /// </summary>
    public static float[] GenerateWaveform(byte[] audioData, int channels, int bitsPerSample, int targetSampleCount)
    {
        if (targetSampleCount <= 0 || audioData.Length == 0 || channels <= 0 || bitsPerSample <= 0 || bitsPerSample % 8 != 0) return [];

        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = audioData.Length / bytesPerSample;
        int monoSamples = totalSamples / channels;
        if (monoSamples == 0) return [];

        int samplesPerBucket = Math.Max(1, monoSamples / targetSampleCount);
        var result = new float[Math.Min(targetSampleCount, monoSamples / samplesPerBucket + 1)];

        int bucketIndex = 0;
        int sampleIndex = 0;

        while (sampleIndex < monoSamples && bucketIndex < result.Length)
        {
            float peak = 0f;
            int bucketEnd = Math.Min(sampleIndex + samplesPerBucket, monoSamples);

            for (int s = sampleIndex; s < bucketEnd; s++)
            {
                // Read all channels, take max
                for (int ch = 0; ch < channels; ch++)
                {
                    int byteOffset = (s * channels + ch) * bytesPerSample;
                    if (byteOffset + bytesPerSample > audioData.Length) break;

                    float value = ReadSampleNormalized(audioData, byteOffset, bitsPerSample);
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }

            result[bucketIndex++] = peak;
            sampleIndex = bucketEnd;
        }

        if (bucketIndex < result.Length)
            Array.Resize(ref result, bucketIndex);

        return result;
    }

    private static float ReadSampleNormalized(byte[] data, int offset, int bitsPerSample)
    {
        return bitsPerSample switch
        {
            8 => (data[offset] - 128) / 128f,
            16 => BitConverter.ToInt16(data, offset) / 32768f,
            24 => ((data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16)) / 8388608f),
            32 => BitConverter.ToInt32(data, offset) / 2147483648f,
            _ => 0f
        };
    }
}
