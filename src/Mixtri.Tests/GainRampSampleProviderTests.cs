namespace Mixtri.Tests;

using Mixtri.Core.Audio;
using NAudio.Wave;

/// <summary>Covers the per-input gain ramp that replaces instant volume steps.</summary>
[TestClass]
public sealed class GainRampSampleProviderTests
{
    /// <summary>Endless constant-valued mono source at 1 kHz (so 5 ms = 5 frames).</summary>
    private sealed class ConstantSource(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(1000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = value;
            return count;
        }
    }

    [TestMethod]
    public void InitialGain_IsAppliedWithoutRamp()
    {
        var gain = new GainRampSampleProvider(new ConstantSource(1f), 0.5f);
        var buffer = new float[4];
        gain.Read(buffer, 0, 4);
        CollectionAssert.AreEqual(new[] { 0.5f, 0.5f, 0.5f, 0.5f }, buffer);
    }

    [TestMethod]
    public void TargetChange_GlidesInsteadOfStepping()
    {
        var gain = new GainRampSampleProvider(new ConstantSource(1f), 1f);
        gain.SetTarget(0f);
        var buffer = new float[10];
        gain.Read(buffer, 0, 10);

        Assert.IsTrue(buffer[0] > 0f && buffer[0] < 1f, "first sample must not jump straight to the target");
        for (int i = 1; i < 5; i++)
            Assert.IsTrue(buffer[i] < buffer[i - 1], "gain must fall monotonically during the ramp");
        Assert.AreEqual(0f, buffer[9], 1e-6);
    }

    [TestMethod]
    public void Target_IsClamped()
    {
        var gain = new GainRampSampleProvider(new ConstantSource(1f), 1f);
        gain.SetTarget(5f);
        Assert.AreEqual(1f, gain.Target);
        gain.SetTarget(-1f);
        Assert.AreEqual(0f, gain.Target);
    }
}
