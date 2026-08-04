using System.Text;
using Musio.Core.Export;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="VideoEncoder.TryGetAudioFileDurationAsync"/>, the probe
/// <see cref="VideoEncoder.ClampToSourceDuration"/> silently depends on: whenever it
/// returns <c>null</c>, the clamp becomes a no-op and a placement's take/fade metadata is
/// muxed exactly as the (I/O-free, therefore unbounded) planner computed it. Standalone
/// audio files are the only source kind with no other duration to fall back on, so these
/// assert an audio-only file — the format this path exists for — really does resolve.
/// </summary>
[TestClass]
public class AudioFileDurationProbeTests
{
    private string? _tempDirectory;

    [TestInitialize]
    public void CreateTempDirectory()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "MusioAudioProbeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void DeleteTempDirectory()
    {
        if (_tempDirectory is null || !Directory.Exists(_tempDirectory)) return;
        try { Directory.Delete(_tempDirectory, recursive: true); }
        catch (IOException) { /* a decoder may still hold the file; the temp dir is disposable */ }
        catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public async Task TryGetAudioFileDurationAsync_ResolvesTheDurationOfAnAudioOnlyFile()
    {
        var path = Path.Combine(_tempDirectory!, "tone.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(2));

        var duration = await VideoEncoder.TryGetAudioFileDurationAsync(path);

        if (duration is null)
            Assert.Inconclusive("Media Foundation audio decoding is unavailable on this host.");

        Assert.AreEqual(
            2.0, duration.Value.TotalSeconds, 0.05,
            "An audio-only file must report its real duration, or ClampToSourceDuration is silently skipped");
    }

    [TestMethod]
    public async Task TryGetAudioFileDurationAsync_ReturnsNull_ForAMissingFile()
    {
        var duration = await VideoEncoder.TryGetAudioFileDurationAsync(
            Path.Combine(_tempDirectory!, "does-not-exist.wav"));

        Assert.IsNull(duration, "A missing file has no duration to clamp against");
    }

    [TestMethod]
    public async Task TryGetAudioFileDurationAsync_ReturnsNull_ForAnUnreadableFile()
    {
        var path = Path.Combine(_tempDirectory!, "not-audio.wav");
        File.WriteAllText(path, "this is not a wav file");

        var duration = await VideoEncoder.TryGetAudioFileDurationAsync(path);

        Assert.IsNull(duration, "An undecodable file must degrade to null rather than throw");
    }

    /// <summary>
    /// Writes a valid 44.1 kHz 16-bit mono PCM WAV of exactly <paramref name="duration"/>
    /// silence — an audio-only file with no video track, i.e. the input shape this probe
    /// exists to measure.
    /// </summary>
    private static void WriteSilentWav(string path, TimeSpan duration)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int blockAlign = channels * bitsPerSample / 8;

        int sampleCount = (int)Math.Round(duration.TotalSeconds * sampleRate);
        int dataBytes = sampleCount * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                          // PCM fmt chunk size
        writer.Write((short)1);                    // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);     // byte rate
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }
}
