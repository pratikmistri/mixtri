using Mixtri.Core.Audio;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

/// <summary>
/// Covers the file-based <see cref="AudioWaveformGenerator.GenerateWaveform(string, int, double, double)"/>
/// bucket arithmetic (ceiling bucket size, tail kept) and its cache, plus
/// <see cref="SegmentAudioRenderer.PruneCache(string, IEnumerable{string}, TimeSpan, long)"/>.
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
        int removed = SegmentAudioRenderer.PruneCache(folder, new[] { keep }, TimeSpan.FromDays(14), 1300);

        Assert.AreEqual(2, removed);
        Assert.IsFalse(File.Exists(expired), "expired entry should be removed");
        Assert.IsFalse(File.Exists(lru), "least recently used entry should go to meet the budget");
        Assert.IsTrue(File.Exists(recent));
        Assert.IsTrue(File.Exists(keep), "the protected file is never deleted, however old");
        Assert.IsTrue(File.Exists(unrelated), "only stretch_*.wav files are cache entries");
    }

    [TestMethod]
    public void PruneCache_KeepsEveryFileOfTheBatch_EvenOverBudget()
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

        // A batch of three: the first two were rendered (or hit) earlier and are the oldest
        // entries — exactly what an LRU pass picks first — but the batch has not opened them.
        string batchFirst = Make("stretch_batch1.wav", 600, TimeSpan.FromDays(30));
        string batchSecond = Make("stretch_batch2.wav", 600, TimeSpan.FromHours(5));
        string batchNewest = Make("stretch_batch3.wav", 600, TimeSpan.Zero);
        string stale = Make("stretch_stale.wav", 600, TimeSpan.FromHours(2));

        int removed = SegmentAudioRenderer.PruneCache(
            folder, new[] { batchFirst, batchSecond, batchNewest }, TimeSpan.FromDays(14), 1000);

        Assert.AreEqual(1, removed);
        Assert.IsTrue(File.Exists(batchFirst), "a batch file is kept even when expired");
        Assert.IsTrue(File.Exists(batchSecond), "a batch file is kept even when over budget");
        Assert.IsTrue(File.Exists(batchNewest));
        Assert.IsFalse(File.Exists(stale), "only files outside the batch are evicted");
    }

    [TestMethod]
    public void PruneCacheAfterBatch_PrunesEachBatchFolder_KeepingTheBatch_IgnoringFailedRenders()
    {
        string folder = Path.Combine(TempDirectory, SegmentAudioRenderer.CacheFolderName);
        Directory.CreateDirectory(folder);
        string kept = Path.Combine(folder, "stretch_kept.wav");
        string expired = Path.Combine(folder, "stretch_expired.wav");
        foreach (var path in new[] { kept, expired })
        {
            File.WriteAllBytes(path, new byte[10]);
            var when = DateTime.UtcNow - TimeSpan.FromDays(30);
            File.SetLastWriteTimeUtc(path, when);
            File.SetLastAccessTimeUtc(path, when);
        }

        int removed = SegmentAudioRenderer.PruneCacheAfterBatch(new string?[] { kept, null, "" });

        Assert.AreEqual(1, removed);
        Assert.IsTrue(File.Exists(kept));
        Assert.IsFalse(File.Exists(expired));
    }

    [TestMethod]
    public void Render_DoesNotPrune_SoEarlierBatchPathsSurvive()
    {
        string source = WriteSilenceWithLoudTail("render_src.wav", 1.0, 0.1);
        string folder = Path.Combine(TempDirectory, SegmentAudioRenderer.CacheFolderName);
        Directory.CreateDirectory(folder);
        string earlier = Path.Combine(folder, "stretch_earlier.wav");
        File.WriteAllBytes(earlier, new byte[10]);
        var old = DateTime.UtcNow - TimeSpan.FromDays(30);
        File.SetLastWriteTimeUtc(earlier, old);
        File.SetLastAccessTimeUtc(earlier, old);

        string? rendered = new SegmentAudioRenderer().Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(1), speed: 2.0, TimeSpan.FromSeconds(0.5));

        Assert.IsNotNull(rendered);
        Assert.AreEqual(folder, Path.GetDirectoryName(rendered));
        Assert.IsTrue(File.Exists(earlier),
            "Render must not prune: a batch may already hold this path without having opened it");
    }

    [TestMethod]
    public void PruneCache_MissingFolder_IsANoOp()
    {
        Assert.AreEqual(0, SegmentAudioRenderer.PruneCache(
            Path.Combine(TempDirectory, "nope"), null, TimeSpan.FromDays(1), 0));
    }
}
