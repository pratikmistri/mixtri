using System.Diagnostics;
using NAudio.Wave;

namespace Mixtri.Core.Capture;

/// <summary>
/// Writes a WASAPI capture stream to a 16-bit PCM WAV file, converting from whatever
/// shared-mode mix format the endpoint reports.
/// </summary>
/// <remarks>
/// <para>
/// WASAPI hands back the device mix format, which on Windows is almost always 32-bit
/// IEEE float — twice the bytes of 16-bit PCM for no audible benefit on screen-capture
/// audio, whose sources are 16- or 24-bit to begin with.
/// </para>
/// <para>
/// PCM is used rather than a compressed codec on purpose. Every recording's audio/video
/// alignment is derived from the first captured sample
/// (<c>Project.AudioToVideoOffsetSeconds</c>), and compressed formats prepend encoder
/// priming samples that would silently shift that alignment. Staying uncompressed keeps
/// the sample clock exact; size is recovered by deflating the file inside the project
/// container instead.
/// </para>
/// </remarks>
internal sealed class Pcm16CaptureWriter : IDisposable
{
    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _sourceFormat;
    private readonly bool _passthrough;
    private byte[] _scratch = [];

    /// <summary>The format actually written to disk.</summary>
    public WaveFormat OutputFormat { get; }

    public Pcm16CaptureWriter(string path, WaveFormat sourceFormat)
    {
        _sourceFormat = sourceFormat;

        _passthrough = sourceFormat.Encoding == WaveFormatEncoding.Pcm
            && sourceFormat.BitsPerSample == 16;

        OutputFormat = _passthrough
            ? sourceFormat
            : new WaveFormat(sourceFormat.SampleRate, 16, sourceFormat.Channels);

        _writer = new WaveFileWriter(path, OutputFormat);
    }

    public void Write(byte[] buffer, int count)
    {
        if (count <= 0)
            return;

        if (_passthrough)
        {
            _writer.Write(buffer, 0, count);
            return;
        }

        int written = Convert(buffer, count);
        if (written > 0)
            _writer.Write(_scratch, 0, written);
    }

    /// <summary>
    /// Converts <paramref name="count"/> bytes of the source format into 16-bit PCM in
    /// <see cref="_scratch"/>, returning the number of bytes produced.
    /// </summary>
    private int Convert(byte[] buffer, int count)
    {
        int bytesPerSample = _sourceFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            return 0;

        int sampleCount = count / bytesPerSample;
        EnsureScratch(sampleCount * 2);

        var span = buffer.AsSpan(0, sampleCount * bytesPerSample);
        var dest = _scratch.AsSpan(0, sampleCount * 2);

        switch (_sourceFormat.Encoding, _sourceFormat.BitsPerSample)
        {
            case (WaveFormatEncoding.IeeeFloat, 32):
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BitConverter.ToSingle(span[(i * 4)..]);
                    WriteSample(dest, i, FloatToPcm16(sample));
                }
                return sampleCount * 2;

            case (WaveFormatEncoding.Pcm, 32):
                for (int i = 0; i < sampleCount; i++)
                {
                    int sample = BitConverter.ToInt32(span[(i * 4)..]);
                    WriteSample(dest, i, (short)(sample >> 16));
                }
                return sampleCount * 2;

            case (WaveFormatEncoding.Pcm, 24):
                for (int i = 0; i < sampleCount; i++)
                {
                    int b = i * 3;
                    // 24-bit little-endian; the top two bytes are the 16-bit value.
                    short sample = (short)(span[b + 1] | (span[b + 2] << 8));
                    WriteSample(dest, i, sample);
                }
                return sampleCount * 2;

            case (WaveFormatEncoding.Pcm, 8):
                for (int i = 0; i < sampleCount; i++)
                    WriteSample(dest, i, (short)((span[i] - 128) << 8));
                return sampleCount * 2;

            default:
                Debug.WriteLine(
                    $"[Pcm16CaptureWriter] Unsupported source format " +
                    $"{_sourceFormat.Encoding}/{_sourceFormat.BitsPerSample}-bit; writing silence.");
                dest.Clear();
                return sampleCount * 2;
        }
    }

    private static void WriteSample(Span<byte> dest, int index, short value)
        => BitConverter.TryWriteBytes(dest[(index * 2)..], value);

    private static short FloatToPcm16(float sample)
    {
        // WASAPI float samples are nominally -1..1 but can overshoot after mixing.
        sample = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(sample * short.MaxValue);
    }

    private void EnsureScratch(int required)
    {
        if (_scratch.Length < required)
            _scratch = new byte[required];
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}
