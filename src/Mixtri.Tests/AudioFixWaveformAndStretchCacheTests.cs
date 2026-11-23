using Mixtri.Core.Audio;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

/// <summary>
/// Covers the file-based <see cref="AudioWaveformGenerator.GenerateWaveform(string, int, double, double)"/>
/// bucket arithmetic (ceiling bucket size, tail kept) and its cache, plus
/// <see cref="SegmentAudioRenderer.PruneCache(string, string?, TimeSpan, long)"/>.
/// </summary>
[TestClass]
public sealed class AudioFixWaveformAndStretchCacheTests
{
    private const int Rate = 48000;

    private TempDirectoryFixture? _tempDir;
    private string TempDirectory => _tempDir!.Path;

    [TestInitialize]
    public void CreateTempDirectory() => _tempDir = new TempDirectoryFixture("MixtriAudioFix_");

    [TestCleanup]
    public void DeleteTempDirectory() => _tempDir?.Dispose();

    /// <summary>Writes mono PCM16: silence, except a loud final <paramref name="loudTailSeconds"/>.</summary>
    private string WriteSilenceWithLoudTail(string name, double seconds, double loudTailSeconds)
    {
        string path = Path.Combine(TempDirectory, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1));

        int frames = (int)(seconds * Rate);
        int loudFrom = frames - (int)(loudTailSeconds * Rate);
        var sample = new float[1];
        for (int i = 0; i < frames; i++)
        {
            sample[0] = i >= loudFrom ? 0.8f : 0f;
            writer.WriteSamples(sample, 0, 1);
        }

        return path;
    }

    [TestMethod]
    public void GenerateWaveform_File_NeverExceedsTarget_AndKeepsTheTail()
    {
        // 1.01 s at 48 kHz = 48480 frames. With 100 buckets a floored bucket size (484) makes
        // 101 buckets and the old code cut the last one off — exactly where the audio is.
        string path = WriteSilenceWithLoudTail("tail.wav", 1.01, 0.004);

        var peaks = AudioWaveformGenerator.GenerateWaveform(path, 100);

        Assert.IsTrue(peaks.Length <= 100, $"Expected at most 100 peaks, got {peaks.Length}");
        Assert.IsTrue(peaks.Length >= 99, $"Expected about 100 peaks, got {peaks.Length}");
        Assert.IsTrue(peaks[^1] > 0.7f, $"The loud tail must land in the last bucket, got {peaks[^1]}");
        Assert.AreEqual(0f, peaks[0], 0.001f);
    }

    [TestMethod]
    public void GenerateWaveform_File_RepeatedCall_ReturnsEqualIndependentCopy()
    {
        string path = WriteSilenceWithLoudTail("cached.wav", 0.5, 0.1);

        var first = AudioWaveformGenerator.GenerateWaveform(path, 50);
        first[^1] = -1f; // a caller scribbling on its array must not poison the cache
        var second = AudioWaveformGenerator.GenerateWaveform(path, 50);

        Assert.AreEqual(50, second.Length);
        Assert.IsTrue(second[^1] > 0.7f);
    }

    [TestMethod]
    public void PruneCache_RemovesExpiredAndOverBudget_KeepsTheProtectedFile()
    {
        string folder = Path.Combine(TempDirectory, SegmentAudioRenderer.CacheFolderName);
        Directory.CreateDirectory(folder);

        string Make(string name, int bytes, TimeSpan age)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, new byte[bytes]);
            var when = DateTime.UtcNow - age;
            File.SetLastWriteTimeUtc(path, when);
            File.SetLastAccessTimeUtc(path, when);
            return path;
        }

        string expired = Make("stretch_old.wav", 10, TimeSpan.FromDays(30));
        string lru = Make("stretch_lru.wav", 600, TimeSpan.FromHours(3));
        string recent = Make("stretch_recent.wav", 600, TimeSpan.FromHours(1));
        string keep = Make("stretch_keep.wav", 600, TimeSpan.FromDays(60));
        string unrelated = Make("notes.txt", 10, TimeSpan.FromDays(60));

        // Budget fits two 600-byte files: the protected one plus the most recent.
        int removed = SegmentAudioRenderer.PruneCache(folder, keep, TimeSpan.FromDays(14), 1300);

        Assert.AreEqual(2, removed);
        Assert.IsFalse(File.Exists(expired), "expired entry should be removed");
        Assert.IsFalse(File.Exists(lru), "least recently used entry should go to meet the budget");
        Assert.IsTrue(File.Exists(recent));
        Assert.IsTrue(File.Exists(keep), "the protected file is never deleted, however old");
        Assert.IsTrue(File.Exists(unrelated), "only stretch_*.wav files are cache entries");
    }

    [TestMethod]
    public void PruneCache_MissingFolder_IsANoOp()
    {
        Assert.AreEqual(0, SegmentAudioRenderer.PruneCache(
            Path.Combine(TempDirectory, "nope"), null, TimeSpan.FromDays(1), 0));
    }
}
