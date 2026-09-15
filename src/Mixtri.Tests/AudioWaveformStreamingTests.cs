using System.Diagnostics;
using System.Numerics;
using Mixtri.Core.Audio;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

[TestClass]
public class AudioWaveformStreamingTests
{
    [TestMethod]
    [DataRow(8, 1, false)]
    [DataRow(16, 1, false)]
    [DataRow(16, 2, false)]
    [DataRow(24, 2, false)]
    [DataRow(32, 2, false)]
    [DataRow(32, 3, true)]
    public void WaveFilesPreservePeaksAndTrimRounding(int bits, int channels, bool floating)
    {
        using var directory = new TempDirectoryFixture("mixtri_waveform_parity_");
        string path = Path.Combine(directory.Path, "samples.wav");
        var format = floating ? WaveFormat.CreateIeeeFloatWaveFormat(16000, channels) : new WaveFormat(16000, bits, channels);
        using (var writer = new WaveFileWriter(path, format))
        {
            if (floating)
            {
                var values = Enumerable.Range(0, 2003 * channels).Select(i => (i % 257 - 128) / 128f).ToArray();
                writer.WriteSamples(values, 0, values.Length);
            }
            else
            {
                var bytes = new byte[2003 * channels * bits / 8];
                new Random(1327).NextBytes(bytes);
                writer.Write(bytes, 0, bytes.Length);
            }
        }
        foreach (int target in new[] { 1, 31, 1500, 4096 })
        foreach (var (start, duration) in new[] { (0d, 0d), (.00137, .05519), (.09, 0), (5d, 0d) })
        {
            AssertBitsEqual(
                PreviousFile(path, target, start, duration),
                AudioWaveformGenerator.GenerateWaveform(path, target, start, duration));
        }
    }

    [TestMethod]
    public void VectorPeakPreservesFiniteSpecialAndNanBits()
    {
        var random = new Random(90210);
        foreach (int count in new[] { 0, 1, 3, Vector<float>.Count, Vector<float>.Count + 1, 37, 65539 })
        {
            var values = Enumerable.Range(0, count + 8).Select(_ => (random.NextSingle() * 2 - 1) * float.MaxValue).ToArray();
            for (int i = count; i < values.Length; i++) values[i] = float.NaN;
            AssertPeak(values, count);
        }
        int length = Vector<float>.Count * 3 + 1;
        foreach (int index in new[] { 0, Vector<float>.Count - 1, Vector<float>.Count, length - 2 })
        {
            var values = Enumerable.Range(0, length).Select(i => (i - 8) / 16f).ToArray();
            values[index] = BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345));
            values[index + 1] = BitConverter.Int32BitsToSingle(0x7fc54321);
            AssertPeak(values, values.Length);
            values[index] = float.NegativeInfinity;
            AssertPeak(values, values.Length);
        }
        AssertPeak([float.NegativeInfinity, float.PositiveInfinity, float.Epsilon, -float.Epsilon, -0f, 0f], 6);
        AssertPeak([1f, float.NaN], 2, BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345)));
    }

    [TestMethod]
    public void PartialBucketsAndShortFilesPreserveExistingOutputShape()
    {
        foreach (int channels in new[] { 1, 2, 3 })
        foreach (int target in new[] { 1, 3, 20 })
        foreach (int sampleCount in new[] { 0, 1, 5, 7, 11 })
        {
            float[] values = Enumerable.Range(0, sampleCount).Select(i => (i % 5 - 2) / 2f).ToArray();
            foreach (long available in new[] { sampleCount * 4L, (sampleCount + 5) * 4L, Math.Max(0, sampleCount - 2) * 4L })
            {
                var previous = new ArraySamples(values, channels);
                var current = new ArraySamples(values, channels);
                AssertBitsEqual(PreviousSamples(previous, available, target),
                    AudioWaveformGenerator.GenerateWaveform(current, available, target));
            }
        }
    }

    [TestMethod]
    public void LargeBucketsKeepChannelAlignedReadsBounded()
    {
        const long frames = 48000L * 90;
        foreach (int channels in new[] { 1, 2, 6 })
        {
            var source = new ConstantSamples(frames * channels, channels);
            var peaks = AudioWaveformGenerator.GenerateWaveform(source, frames * channels * sizeof(float), 1);
            CollectionAssert.AreEqual(new[] { .75f }, peaks);
            Assert.IsTrue(source.MaximumRequested <= AudioWaveformGenerator.MaximumReadSampleValues);
            Assert.AreEqual(0, source.MaximumRequested % channels);
            Assert.AreEqual(frames * channels, source.Position);
        }
    }

    [TestMethod]
    public void StopsReadingAfterAllReturnedBucketsAreComplete()
    {
        var source = new ConstantSamples(1000, 1);
        var peaks = AudioWaveformGenerator.GenerateWaveform(source, 1000 * sizeof(float), 600);
        Assert.AreEqual(600, peaks.Length);
        Assert.AreEqual(600L, source.Position);
        Assert.IsTrue(peaks.All(value => value == .75f));
    }

    [TestMethod]
    public void VeryLargeBucketsDoNotOverflowIntoSingleSampleBuckets()
    {
        var source = new ArraySamples([.125f, .75f], 1);
        long decodedBytes = ((long)int.MaxValue + 4096) * sizeof(float);
        CollectionAssert.AreEqual(new[] { .75f },
            AudioWaveformGenerator.GenerateWaveform(source, decodedBytes, 1));
    }

    [TestMethod]
    public void NanPropagationSurvivesReadChunkBoundaries()
    {
        var values = new float[AudioWaveformGenerator.MaximumReadSampleValues + 10];
        values[9] = BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345));
        values[^2] = BitConverter.Int32BitsToSingle(0x7fc54321);
        AssertBitsEqual(
            PreviousSamples(new ArraySamples(values, 2), values.LongLength * 4, 1),
            AudioWaveformGenerator.GenerateWaveform(new ArraySamples(values, 2), values.LongLength * 4, 1));
    }

    [TestMethod]
    public void LongStereoWaveUsesBoundedScratchMemory()
    {
        using var directory = new TempDirectoryFixture("mixtri_waveform_long_");
        string path = Path.Combine(directory.Path, "long.wav");
        const int values = 48000 * 90 * 2;
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
        {
            var block = new float[65536];
            Array.Fill(block, .75f);
            for (int written = 0; written < values; written += block.Length)
                writer.WriteSamples(block, 0, Math.Min(block.Length, values - written));
        }
        AudioWaveformGenerator.GenerateWaveform(path, 1);
        PreviousFile(path, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var current = AudioWaveformGenerator.GenerateWaveform(path, 1);
        timer.Stop();
        long currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        double currentMs = timer.Elapsed.TotalMilliseconds;
        before = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        var previous = PreviousFile(path, 1);
        timer.Stop();
        long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertBitsEqual(previous, current);
        Assert.IsTrue(currentBytes < 2_000_000, $"Scratch allocations grew to {currentBytes:N0} bytes.");
        Assert.IsTrue(previousBytes > 30_000_000, $"Reference fixture allocated only {previousBytes:N0} bytes.");
        Console.WriteLine($"90-second stereo waveform: {previousBytes:N0} -> {currentBytes:N0} allocated bytes; " +
            $"reference {timer.Elapsed.TotalMilliseconds:F1} ms, current {currentMs:F1} ms (timing informational).");
    }

    /// <summary>
    /// Pins the NAudio contract the file-based overload depends on: <c>AudioFileReader</c>
    /// normalises Length, Position and WaveFormat to decoded 32-bit float, whatever the
    /// source bit depth. <c>remainingBytes</c> is therefore already decoded float bytes, so
    /// dividing by <c>sizeof(float)</c> is correct and no encoded-to-decoded conversion is
    /// wanted. Asserted directly because the parity fixture divides by
    /// <c>BitsPerSample / 8</c>, which is also 4 here and so cannot detect a unit mismatch.
    /// </summary>
    [TestMethod]
    [DataRow(8, 1)]
    [DataRow(16, 1)]
    [DataRow(16, 2)]
    [DataRow(24, 2)]
    [DataRow(32, 2)]
    public void AudioFileReaderReportsDecodedFloatBytes(int bits, int channels)
    {
        using var directory = new TempDirectoryFixture("mixtri_waveform_units_");
        string path = Path.Combine(directory.Path, "units.wav");
        const int frames = 2003;
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, bits, channels)))
        {
            var bytes = new byte[frames * channels * bits / 8];
            new Random(7).NextBytes(bytes);
            writer.Write(bytes, 0, bytes.Length);
        }

        using var reader = new AudioFileReader(path);
        Assert.AreEqual(32, reader.WaveFormat.BitsPerSample);
        Assert.AreEqual(frames * (long)channels * sizeof(float), reader.Length,
            "Length must be decoded float bytes, not encoded source bytes");

        var buffer = new float[4096];
        long values = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) values += read;
        Assert.AreEqual(frames * (long)channels, values,
            "every decoded sample value must be accounted for by Length / sizeof(float)");
        Assert.AreEqual(reader.Length / sizeof(float), values);
    }

    private static void AssertPeak(float[] samples, int count, float initial = 0)
    {
        float previous = initial;
        for (int i = 0; i < count; i++) previous = Math.Max(previous, Math.Abs(samples[i]));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(previous),
            BitConverter.SingleToInt32Bits(AudioWaveformGenerator.FindPeak(samples, count, initial)));
    }

    private static void AssertBitsEqual(float[] previous, float[] current) =>
        CollectionAssert.AreEqual(previous.Select(BitConverter.SingleToInt32Bits).ToArray(),
            current.Select(BitConverter.SingleToInt32Bits).ToArray());

    private static float[] PreviousFile(string path, int target, double start = 0, double duration = 0)
    {
        using var reader = new AudioFileReader(path);
        if (start > 0)
        {
            long skip = (long)(start * reader.WaveFormat.AverageBytesPerSecond);
            skip -= skip % reader.WaveFormat.BlockAlign;
            if (skip >= reader.Length) return new float[target];
            if (skip > 0) reader.Position = skip;
        }
        long remaining = reader.Length - reader.Position;
        if (duration > 0)
        {
            long maximum = (long)(duration * reader.WaveFormat.AverageBytesPerSecond);
            maximum -= maximum % reader.WaveFormat.BlockAlign;
            if (maximum > 0 && maximum < remaining) remaining = maximum;
        }
        return PreviousSamples(reader, remaining, target);
    }

    private static float[] PreviousSamples(ISampleProvider reader, long remaining, int target)
    {
        long mono = remaining / (reader.WaveFormat.BitsPerSample / 8) / reader.WaveFormat.Channels;
        if (mono == 0) return [];
        int perBucket = Math.Max(1, (int)(mono / target));
        var result = new List<float>(target);
        var buffer = new float[perBucket * reader.WaveFormat.Channels];
        long consumed = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            consumed += read * sizeof(float);
            if (consumed > remaining) break;
            float peak = 0;
            for (int i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            result.Add(peak);
        }
        return result.Count > target ? result.Take(target).ToArray() : [.. result];
    }

    private sealed class ArraySamples(float[] samples, int channels) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }

    private sealed class ConstantSamples(long values, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        public long Position { get; private set; }
        public int MaximumRequested { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        {
            MaximumRequested = Math.Max(MaximumRequested, count);
            int read = (int)Math.Min(count, values - Position);
            Array.Fill(buffer, .75f, offset, read);
            Position += read;
            return read;
        }
    }
}
