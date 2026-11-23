using Mixtri.Core.Audio;

namespace Mixtri.Tests;

[TestClass]
public sealed class WaveformDiskCacheTests
{
    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mixtri-wfcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void HashKey_IsStableAndFileNameSafe()
    {
        var a = WaveformDiskCache.HashKey("C:\\A\\B.WAV|1|2|3|0|0");
        var b = WaveformDiskCache.HashKey("C:\\A\\B.WAV|1|2|3|0|0");
        var c = WaveformDiskCache.HashKey("C:\\A\\B.WAV|1|2|4|0|0");

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, c);
        Assert.AreEqual(32, a.Length);
        Assert.IsTrue(a.All(Uri.IsHexDigit));
    }

    [TestMethod]
    public void BuildPeakCacheKey_ChangesWithArgumentsAndFileContentStamp()
    {
        var folder = NewTempFolder();
        try
        {
            var file = Path.Combine(folder, "a.wav");
            File.WriteAllBytes(file, new byte[64]);

            var k1 = AudioWaveformGenerator.BuildPeakCacheKey(file, 100, 0, 0);
            Assert.AreEqual(k1, AudioWaveformGenerator.BuildPeakCacheKey(file, 100, 0, 0));
            Assert.AreNotEqual(k1, AudioWaveformGenerator.BuildPeakCacheKey(file, 200, 0, 0));
            Assert.AreNotEqual(k1, AudioWaveformGenerator.BuildPeakCacheKey(file, 100, 1.5, 0));

            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
            Assert.AreNotEqual(k1, AudioWaveformGenerator.BuildPeakCacheKey(file, 100, 0, 0));
            Assert.IsNull(AudioWaveformGenerator.BuildPeakCacheKey(Path.Combine(folder, "missing.wav"), 100, 0, 0));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void WriteThenRead_RoundTripsPeaks()
    {
        var folder = Path.Combine(NewTempFolder(), "nested");
        try
        {
            float[] peaks = [0f, 0.25f, 1f, 0.5f];
            var key = WaveformDiskCache.HashKey("k");

            Assert.IsNull(WaveformDiskCache.TryRead(folder, key));
            WaveformDiskCache.TryWrite(folder, key, peaks);

            CollectionAssert.AreEqual(peaks, WaveformDiskCache.TryRead(folder, key));
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.tmp").Length);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, true); }
    }

    [TestMethod]
    public void Read_CorruptOrTruncatedFile_IsAMiss()
    {
        var folder = NewTempFolder();
        try
        {
            var key = WaveformDiskCache.HashKey("k");
            WaveformDiskCache.TryWrite(folder, key, [0.1f, 0.2f, 0.3f]);
            var path = Directory.GetFiles(folder, "*.wfp").Single();

            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^2]);
            Assert.IsNull(WaveformDiskCache.TryRead(folder, key));

            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.IsNull(WaveformDiskCache.TryRead(folder, key));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void Prune_DeletesOldestFilesBeyondBudget()
    {
        var folder = NewTempFolder();
        try
        {
            var peaks = new float[250]; // 1012-byte files
            for (int i = 0; i < 5; i++)
            {
                var key = WaveformDiskCache.HashKey("k" + i);
                WaveformDiskCache.TryWrite(folder, key, peaks);
                File.SetLastWriteTimeUtc(
                    Path.Combine(folder, key + ".wfp"), DateTime.UtcNow.AddMinutes(-10 + i));
            }

            WaveformDiskCache.Prune(folder, 2500);

            var remaining = Directory.GetFiles(folder, "*.wfp");
            Assert.AreEqual(2, remaining.Length);
            Assert.IsNotNull(WaveformDiskCache.TryRead(folder, WaveformDiskCache.HashKey("k4")));
            Assert.IsNull(WaveformDiskCache.TryRead(folder, WaveformDiskCache.HashKey("k0")));
        }
        finally { Directory.Delete(folder, true); }
    }
}
