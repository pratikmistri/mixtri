using Mixtri.Core.Audio;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

[TestClass]
public class AudioWaveformCancellationTests
{
    [TestMethod]
    public void AlreadyCancelledWaveformDoesNotReadSamples()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new CancellingSamples(200000, cancellation);
        var error = Assert.ThrowsException<OperationCanceledException>(() =>
            AudioWaveformGenerator.GenerateWaveform(source, 200000 * 4L, 1, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(0, source.Reads);
    }

    [TestMethod]
    public void AlreadyCancelledFileRequestDoesNotOpenTheFile()
    {
        using var directory = new TempDirectoryFixture("mixtri_waveform_cancel_");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string missing = Path.Combine(directory.Path, "never-created.wav");
        Assert.ThrowsException<OperationCanceledException>(() =>
            AudioWaveformGenerator.GenerateWaveform(missing, 10, 0, 0, cancellation.Token));
        Assert.IsFalse(File.Exists(missing));
    }

    [TestMethod]
    [DataRow(200000)]
    [DataRow(32)]
    public void CancellationDuringAReadStopsBeforeAnotherReadOrACompletedResult(int values)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancellingSamples(values, cancellation);
        var error = Assert.ThrowsException<OperationCanceledException>(() =>
            AudioWaveformGenerator.GenerateWaveform(source, values * 4L, 1, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(1, source.Reads);
        Assert.IsTrue(source.MaximumRequested <= AudioWaveformGenerator.MaximumReadSampleValues);
    }

    [TestMethod]
    public void UncancelledFileOverloadPreservesPeaksAndClosesItsReader()
    {
        using var directory = new TempDirectoryFixture("mixtri_waveform_token_");
        string path = Path.Combine(directory.Path, "samples.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16000, 2)))
        {
            var values = Enumerable.Range(0, 4000).Select(i => (i % 101 - 50) / 50f).ToArray();
            writer.WriteSamples(values, 0, values.Length);
        }
        using var cancellation = new CancellationTokenSource();
        var expected = AudioWaveformGenerator.GenerateWaveform(path, 31, .00137, .075);
        var actual = AudioWaveformGenerator.GenerateWaveform(path, 31, .00137, .075, cancellation.Token);
        CollectionAssert.AreEqual(expected, actual);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0);
    }

    private sealed class CancellingSamples(int values, CancellationTokenSource cancellation) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Reads { get; private set; }
        public int MaximumRequested { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        {
            Reads++;
            MaximumRequested = Math.Max(MaximumRequested, count);
            int read = Math.Min(count, values - _position);
            Array.Fill(buffer, .75f, offset, read);
            _position += read;
            cancellation.Cancel();
            return read;
        }
    }
}
