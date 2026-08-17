using Musio.Core.Audio;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="WsolaTimeStretcher"/>, the pure DSP that lets a speed-adjusted segment
/// keep its recorded audio.
/// </summary>
/// <remarks>
/// The load-bearing test here is <see cref="StretchedSine_KeepsItsOriginalFrequency"/>: it is
/// what distinguishes a real time-stretch from the naive fix (resampling), which produces the
/// correct duration and the wrong pitch. Everything else — lengths, gain, stereo coherence,
/// degenerate inputs — is about not shipping clicks, chipmunks or crashes.
/// </remarks>
[TestClass]
public sealed class WsolaTimeStretcherTests
{
    private const int Rate = 48000;

    /// <summary>Fundamental measured back out of the output by counting zero crossings.</summary>
    private static double FrequencyOf(float[] samples, int channels = 1, int channel = 0)
    {
        int frames = samples.Length / channels;
        Assert.IsTrue(frames > Rate / 4, "Need a reasonable window to measure a frequency over");

        int crossings = 0;
        for (int i = 1; i < frames; i++)
        {
            float previous = samples[((i - 1) * channels) + channel];
            float current = samples[(i * channels) + channel];
            if ((previous <= 0 && current > 0) || (previous >= 0 && current < 0)) crossings++;
        }

        // Two crossings per cycle.
        return crossings / (frames / (double)Rate) / 2.0;
    }

    private static float[] Sine(double seconds, double frequency, int channels = 1, float amplitude = 0.7f)
    {
        int frames = (int)(seconds * Rate);
        var samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
            for (int c = 0; c < channels; c++) samples[(i * channels) + c] = value;
        }
        return samples;
    }

    [TestMethod]
    [DataRow(0.25)]
    [DataRow(0.5)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    [DataRow(4.0)]
    public void OutputLength_MatchesTheRequestedSpeed(double speed)
    {
        var input = Sine(seconds: 3, frequency: 440);

        var output = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, speed);

        double expected = input.Length / speed;
        // The final scrap of input is shorter than one grain and is passed through at its
        // natural rate, so slowing down lands slightly short — bounded here, and padded to
        // the exact placement length by SegmentAudioRenderer.
        double toleranceSeconds = speed < 1 ? 0.25 : 0.05;
        Assert.AreEqual(
            expected / Rate, output.Length / (double)Rate, toleranceSeconds,
            $"A {speed}x stretch must last input/{speed}");
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(2.0)]
    [DataRow(4.0)]
    public void StretchedSine_KeepsItsOriginalFrequency(double speed)
    {
        var input = Sine(seconds: 2, frequency: 440);

        var output = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, speed);

        Assert.AreEqual(
            440.0, FrequencyOf(output), 8.0,
            "Time-stretching must not transpose the signal — that is what separates WSOLA " +
            "from resampling, which is exactly the chipmunk artefact this feature exists to avoid");
    }

    [TestMethod]
    public void NativeSpeed_IsIdentity()
    {
        var input = Sine(seconds: 1, frequency: 440);

        var output = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, 1.0);

        CollectionAssert.AreEqual(
            input, output,
            "A segment left at 1.0 must not be re-timed, re-sampled or otherwise touched");
    }

    [TestMethod]
    public void SpeedWithinEpsilonOfOne_IsIdentity()
    {
        var input = Sine(seconds: 1, frequency: 440);

        var output = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, 1.0005);

        CollectionAssert.AreEqual(input, output, "Sub-epsilon speeds are not worth stretching");
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(2.0)]
    public void Output_StaysInRangeAndFinite(double speed)
    {
        // Two tones summed to near full scale: overlap-adding two grains that happen to be
        // out of phase is where an unbounded implementation would clip.
        var input = Sine(seconds: 2, frequency: 440, amplitude: 0.5f);
        var second = Sine(seconds: 2, frequency: 660, amplitude: 0.49f);
        for (int i = 0; i < input.Length; i++) input[i] += second[i];

        var output = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, speed);

        foreach (float sample in output)
        {
            Assert.IsFalse(float.IsNaN(sample) || float.IsInfinity(sample), "Output must be finite");
            Assert.IsTrue(Math.Abs(sample) <= 1.0f, $"Output must not clip (saw {sample})");
        }
    }

    [TestMethod]
    public void Stereo_KeepsBothChannelsAligned()
    {
        // Identical channels: any per-channel difference in the chosen grain offset would
        // decorrelate them and smear the stereo image.
        var input = Sine(seconds: 2, frequency: 330, channels: 2);

        var output = WsolaTimeStretcher.Stretch(input, channels: 2, Rate, 2.0);

        Assert.AreEqual(0, output.Length % 2, "Stereo output must contain whole frames");
        for (int frame = 0; frame < output.Length / 2; frame++)
        {
            Assert.AreEqual(
                output[frame * 2], output[(frame * 2) + 1], 1e-6,
                "Both channels must be cut at the same instant");
        }
    }

    [TestMethod]
    public void ChunkedInput_ProducesTheSameOutputAsOneCall()
    {
        // The renderer streams a file in blocks; the block size must not be audible.
        var input = Sine(seconds: 2, frequency: 220);
        var oneShot = WsolaTimeStretcher.Stretch(input, channels: 1, Rate, 1.7);

        var chunked = new List<float>();
        void Collect(ReadOnlySpan<float> block)
        {
            foreach (float sample in block) chunked.Add(sample);
        }

        var stretcher = new WsolaTimeStretcher(Rate, channels: 1, speed: 1.7);
        for (int i = 0; i < input.Length; i += 999)
            stretcher.Process(input.AsSpan(i, Math.Min(999, input.Length - i)), Collect);
        stretcher.Flush(Collect);

        CollectionAssert.AreEqual(
            oneShot, chunked,
            "Streaming state must carry across calls — a block boundary is not a signal boundary");
    }

    [TestMethod]
    public void ChunksThatSplitAFrame_LoseNoSamples()
    {
        // An odd-sized stereo block ends mid-frame. Dropping that half-frame would not merely
        // lose a sample: every later frame would be one channel out of phase, swapping left
        // and right for the rest of the signal.
        var input = Sine(seconds: 1, frequency: 300, channels: 2);
        var oneShot = WsolaTimeStretcher.Stretch(input, channels: 2, Rate, 2.0);

        var chunked = new List<float>();
        void Collect(ReadOnlySpan<float> block)
        {
            foreach (float sample in block) chunked.Add(sample);
        }

        var stretcher = new WsolaTimeStretcher(Rate, channels: 2, speed: 2.0);
        for (int i = 0; i < input.Length; i += 777)   // odd: every block ends mid-frame
            stretcher.Process(input.AsSpan(i, Math.Min(777, input.Length - i)), Collect);
        stretcher.Flush(Collect);

        CollectionAssert.AreEqual(
            oneShot, chunked,
            "A block boundary inside a frame must be carried, not discarded");
    }

    [TestMethod]
    public void EmptyInput_ProducesEmptyOutput()
    {
        Assert.AreEqual(0, WsolaTimeStretcher.Stretch([], channels: 1, Rate, 2.0).Length);
    }

    [TestMethod]
    public void InputShorterThanOneGrain_IsHandledWithoutThrowing()
    {
        var single = WsolaTimeStretcher.Stretch(new float[] { 0.5f }, channels: 1, Rate, 2.0);
        Assert.IsTrue(single.Length <= 1, "A single sample cannot yield more than it started with");

        var tiny = WsolaTimeStretcher.Stretch(Sine(seconds: 0.002, frequency: 440), channels: 1, Rate, 2.0);
        Assert.IsTrue(tiny.Length > 0, "A sub-grain fragment still has to produce its (shortened) self");
        Assert.IsTrue(tiny.Length <= Rate * 0.002, "...and must not invent samples it never had");
    }

    [TestMethod]
    public void InvalidSpeeds_AreRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new WsolaTimeStretcher(Rate, 1, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new WsolaTimeStretcher(Rate, 1, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new WsolaTimeStretcher(Rate, 0, 2.0));
    }

    [TestMethod]
    public void IsStretchNeeded_TracksTheEpsilon()
    {
        Assert.IsFalse(WsolaTimeStretcher.IsStretchNeeded(1.0));
        Assert.IsFalse(WsolaTimeStretcher.IsStretchNeeded(1.0005));
        Assert.IsTrue(WsolaTimeStretcher.IsStretchNeeded(1.01));
        Assert.IsTrue(WsolaTimeStretcher.IsStretchNeeded(0.5));
    }

    [TestMethod]
    public void WindowSizes_ScaleWithTheSampleRate()
    {
        // Hard-coded sample counts would make a 16kHz mic capture use 3x-short grains.
        Assert.AreEqual(
            new WsolaTimeStretcher(48000, 1, 2.0).SynthesisHop,
            new WsolaTimeStretcher(16000, 1, 2.0).SynthesisHop * 3,
            "The synthesis hop is a duration, not a sample count");
    }
}
