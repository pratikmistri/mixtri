using Mixtri.Core.Audio;

namespace Mixtri.Tests;

[TestClass]
public sealed class AudioWaveformGeneratorTests
{
    #region Empty / Invalid Input

    [TestMethod]
    public void GenerateWaveform_EmptyData_ReturnsEmpty()
    {
        var result = AudioWaveformGenerator.GenerateWaveform([], 1, 16, 100);
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void GenerateWaveform_ZeroTargetCount_ReturnsEmpty()
    {
        byte[] data = new byte[100];
        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 16, 0);
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void GenerateWaveform_ZeroChannels_ReturnsEmpty()
    {
        byte[] data = new byte[100];
        var result = AudioWaveformGenerator.GenerateWaveform(data, 0, 16, 10);
        Assert.AreEqual(0, result.Length);
    }

    #endregion

    #region 16-bit Mono

    [TestMethod]
    public void GenerateWaveform_16bit_Silence_AllZeros()
    {
        // 100 samples of silence (16-bit = 2 bytes per sample)
        byte[] data = new byte[200];
        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 16, 10);

        Assert.IsTrue(result.Length > 0);
        foreach (float v in result)
            Assert.AreEqual(0f, v, 0.001f, "Silence should produce zero peaks");
    }

    [TestMethod]
    public void GenerateWaveform_16bit_MaxAmplitude_PeaksAtOne()
    {
        // 100 samples of max positive amplitude (Int16.MaxValue = 32767)
        byte[] data = new byte[200];
        for (int i = 0; i < 100; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 2), short.MaxValue);
        }

        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 16, 10);

        Assert.IsTrue(result.Length > 0);
        float maxPeak = result.Max();
        Assert.IsTrue(maxPeak > 0.99f, $"Max amplitude should produce peak near 1.0, got {maxPeak}");
    }

    [TestMethod]
    public void GenerateWaveform_16bit_NegativeAmplitude_DetectsAbsValue()
    {
        // Samples with negative values (Int16.MinValue = -32768)
        byte[] data = new byte[200];
        for (int i = 0; i < 100; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 2), short.MinValue);
        }

        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 16, 10);

        float maxPeak = result.Max();
        Assert.IsTrue(maxPeak >= 0.99f, $"Negative amplitude should produce peak near 1.0 via abs, got {maxPeak}");
    }

    #endregion

    #region 8-bit

    [TestMethod]
    public void GenerateWaveform_8bit_Silence_IsNearZero()
    {
        // 8-bit silence is 128 (unsigned center)
        byte[] data = Enumerable.Repeat((byte)128, 100).ToArray();
        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 8, 10);

        Assert.IsTrue(result.Length > 0);
        foreach (float v in result)
            Assert.AreEqual(0f, v, 0.01f);
    }

    [TestMethod]
    public void GenerateWaveform_8bit_MaxPositive_PeaksCorrectly()
    {
        // 255 = max positive in unsigned 8-bit → (255-128)/128 ≈ 0.992
        byte[] data = Enumerable.Repeat((byte)255, 100).ToArray();
        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 8, 10);

        float maxPeak = result.Max();
        Assert.IsTrue(maxPeak > 0.9f, $"Expected peak > 0.9, got {maxPeak}");
    }

    #endregion

    #region Stereo

    [TestMethod]
    public void GenerateWaveform_Stereo16bit_ProcessesBothChannels()
    {
        // 50 stereo samples: left channel silent, right channel max
        byte[] data = new byte[200]; // 50 * 2 channels * 2 bytes
        for (int i = 0; i < 50; i++)
        {
            int offset = i * 4;
            BitConverter.TryWriteBytes(data.AsSpan(offset), (short)0);         // left = silent
            BitConverter.TryWriteBytes(data.AsSpan(offset + 2), short.MaxValue); // right = max
        }

        var result = AudioWaveformGenerator.GenerateWaveform(data, 2, 16, 5);

        Assert.IsTrue(result.Length > 0);
        float maxPeak = result.Max();
        Assert.IsTrue(maxPeak > 0.9f, "Should detect amplitude from right channel");
    }

    #endregion

    #region 24-bit

    [TestMethod]
    public void GenerateWaveform_24bit_Silence_IsNearZero()
    {
        // 24-bit silence: all zeros
        byte[] data = new byte[300]; // 100 samples × 3 bytes
        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 24, 10);

        Assert.IsTrue(result.Length > 0);
        foreach (float v in result)
            Assert.AreEqual(0f, v, 0.001f);
    }

    #endregion

    #region 32-bit

    [TestMethod]
    public void GenerateWaveform_32bit_MaxAmplitude()
    {
        byte[] data = new byte[400]; // 100 samples × 4 bytes
        for (int i = 0; i < 100; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 4), int.MaxValue);
        }

        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 32, 10);

        Assert.IsTrue(result.Length > 0);
        float maxPeak = result.Max();
        Assert.IsTrue(maxPeak > 0.9f, $"32-bit max should produce peak near 1.0, got {maxPeak}");
    }

    #endregion

    #region Bucket Count

    [TestMethod]
    public void GenerateWaveform_OutputLength_AtMostTargetCount()
    {
        // 1000 mono 16-bit samples, target 50
        byte[] data = new byte[2000];
        var rng = new Random(42);
        rng.NextBytes(data);

        var result = AudioWaveformGenerator.GenerateWaveform(data, 1, 16, 50);

        Assert.IsTrue(result.Length <= 51, $"Output length {result.Length} should be at most target+1");
        Assert.IsTrue(result.Length > 0, "Should produce at least one bucket");
    }

    #endregion
}
