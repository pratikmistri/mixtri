using NAudio.Wave;

namespace Musio.Core.Audio;

/// <summary>
/// Generates downsampled peak values from audio data for waveform visualization.
/// </summary>
public static class AudioWaveformGenerator
{
    /// <summary>
    /// Generate waveform peak samples from a WAV file.
    /// Returns normalized peak values (0..1) suitable for rendering.
    /// </summary>
    /// <param name="startSeconds">Seconds to skip from the start (e.g. audio pre-roll before video).</param>
    /// <param name="maxDurationSeconds">Max seconds of audio to process (0 = read to end).</param>
    public static float[] GenerateWaveform(string wavFilePath, int targetSampleCount,
        double startSeconds = 0, double maxDurationSeconds = 0)
    {
        if (targetSampleCount <= 0) return [];

        using var reader = new AudioFileReader(wavFilePath);

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

        int bytesPerSample = reader.WaveFormat.BitsPerSample / 8;
        long totalSamples = remainingBytes / bytesPerSample;
        int channels = reader.WaveFormat.Channels;
        long monoSamples = totalSamples / channels;

        if (monoSamples == 0) return [];

        int samplesPerBucket = Math.Max(1, (int)(monoSamples / targetSampleCount));
        var result = new List<float>(targetSampleCount);
        var buffer = new float[samplesPerBucket * channels];
        long bytesRead = 0;

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytesRead += read * sizeof(float);
            if (bytesRead > remainingBytes) break;

            float peak = 0f;
            for (int i = 0; i < read; i++)
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            result.Add(peak);
        }

        // Trim or pad to targetSampleCount
        if (result.Count > targetSampleCount)
            return result.Take(targetSampleCount).ToArray();

        return [.. result];
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
