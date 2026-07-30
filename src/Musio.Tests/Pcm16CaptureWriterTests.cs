using Musio.Core.Capture;
using NAudio.Wave;

namespace Musio.Tests;

[TestClass]
public class Pcm16CaptureWriterTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_pcm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Path_(string name) => System.IO.Path.Combine(_root, name);

    private static byte[] FloatBytes(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    [TestMethod]
    public void FloatInput_IsWrittenAs16BitPcm()
    {
        var path = Path_("out.wav");
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        using (var writer = new Pcm16CaptureWriter(path, source))
        {
            Assert.AreEqual(16, writer.OutputFormat.BitsPerSample);
            Assert.AreEqual(48000, writer.OutputFormat.SampleRate);
            Assert.AreEqual(2, writer.OutputFormat.Channels);
            Assert.AreEqual(WaveFormatEncoding.Pcm, writer.OutputFormat.Encoding);
        }

        using var reader = new WaveFileReader(path);
        Assert.AreEqual(16, reader.WaveFormat.BitsPerSample);
        Assert.AreEqual(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
    }

    [TestMethod]
    public void FloatInput_HalvesTheBytesWritten()
    {
        var path = Path_("halved.wav");
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var input = FloatBytes(new float[512]);

        using (var writer = new Pcm16CaptureWriter(path, source))
            writer.Write(input, input.Length);

        using var reader = new WaveFileReader(path);
        Assert.AreEqual(input.Length / 2, reader.Length);
    }

    [TestMethod]
    public void FloatSamples_AreScaledAndClamped()
    {
        var path = Path_("scaled.wav");
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        // Full scale, silence, negative full scale, and values that overshoot the
        // nominal -1..1 range (which WASAPI can produce after mixing).
        var input = FloatBytes(1.0f, 0f, -1.0f, 2.5f, -2.5f, 0.5f);

        using (var writer = new Pcm16CaptureWriter(path, source))
            writer.Write(input, input.Length);

        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);

        Assert.AreEqual(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.AreEqual(0, BitConverter.ToInt16(bytes, 2));
        Assert.AreEqual(-short.MaxValue, BitConverter.ToInt16(bytes, 4));
        Assert.AreEqual(short.MaxValue, BitConverter.ToInt16(bytes, 6), "overshoot must clamp, not wrap");
        Assert.AreEqual(-short.MaxValue, BitConverter.ToInt16(bytes, 8), "overshoot must clamp, not wrap");
        Assert.AreEqual(16384, BitConverter.ToInt16(bytes, 10));
    }

    [TestMethod]
    public void Pcm16Input_IsPassedThroughUnchanged()
    {
        var path = Path_("passthrough.wav");
        var source = new WaveFormat(44100, 16, 2);
        var input = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF, 0x7F, 0x00, 0x80 };

        using (var writer = new Pcm16CaptureWriter(path, source))
        {
            Assert.AreEqual(source, writer.OutputFormat);
            writer.Write(input, input.Length);
        }

        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        CollectionAssert.AreEqual(input, bytes);
    }

    [TestMethod]
    public void Pcm24Input_IsNarrowedToTheTop16Bits()
    {
        var path = Path_("pcm24.wav");
        var source = new WaveFormat(48000, 24, 1);
        // Little-endian 24-bit: 0x123456 and 0x000100.
        var input = new byte[] { 0x56, 0x34, 0x12, 0x00, 0x01, 0x00 };

        using (var writer = new Pcm16CaptureWriter(path, source))
            writer.Write(input, input.Length);

        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);

        Assert.AreEqual(4, bytes.Length);
        Assert.AreEqual(0x1234, BitConverter.ToInt16(bytes, 0));
        Assert.AreEqual(0x0001, BitConverter.ToInt16(bytes, 2));
    }

    [TestMethod]
    public void WriteOfZeroBytes_IsIgnored()
    {
        var path = Path_("empty.wav");
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        using (var writer = new Pcm16CaptureWriter(path, source))
        {
            writer.Write([], 0);
            writer.Write(new byte[16], 0);
        }

        using var reader = new WaveFileReader(path);
        Assert.AreEqual(0, reader.Length);
    }

    [TestMethod]
    public void PartialBuffer_OnlyConvertsTheReportedByteCount()
    {
        var path = Path_("partial.wav");
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var input = FloatBytes(1.0f, -1.0f, 0.5f, 0.25f);

        using (var writer = new Pcm16CaptureWriter(path, source))
            writer.Write(input, 8); // only the first two samples are valid

        using var reader = new WaveFileReader(path);
        Assert.AreEqual(4, reader.Length);
    }
}
