using Mixtri.Core.Capture;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

/// <summary>
/// A crash mid-recording must leave a readable WAV: NAudio only writes the RIFF/data sizes
/// on Flush/Dispose, so the capture engine flushes periodically.
/// </summary>
[TestClass]
public class Pcm16CaptureWriterFlushTests
{
    [TestMethod]
    public void Flush_WithoutDispose_WritesNonZeroDataChunkSize()
    {
        using var dir = new TempDirectoryFixture("mixtri_pcm_flush_");
        string path = Path.Combine(dir.Path, "flush.wav");

        using var writer = new Pcm16CaptureWriter(path, new WaveFormat(48000, 16, 2));
        var buffer = new byte[4800 * 4];
        writer.Write(buffer, buffer.Length);
        writer.Flush();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[128];
        int read = fs.Read(header, 0, header.Length);
        Assert.IsTrue(read >= 44, "header must be on disk");

        uint riffSize = BitConverter.ToUInt32(header, 4);
        Assert.IsTrue(riffSize > 0, "RIFF size must be written by Flush.");

        // Walk the chunks rather than assume a 44-byte header: NAudio may write an 18-byte
        // fmt body (with cbSize), which moves the data chunk to offset 38.
        int pos = 12;
        while (pos + 8 <= read
            && System.Text.Encoding.ASCII.GetString(header, pos, 4) != "data")
        {
            pos += 8 + (int)BitConverter.ToUInt32(header, pos + 4);
        }
        Assert.IsTrue(pos + 8 <= read, "data chunk not found in header");
        Assert.AreEqual((uint)buffer.Length, BitConverter.ToUInt32(header, pos + 4));
    }
}
