using Musio.Core.Audio;
using Musio.Tests.TestSupport;
using NAudio.Wave;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="SegmentAudioRenderer"/>: the I/O layer that turns a segment's source
/// range into a stretched WAV of exactly the duration that segment occupies on the output
/// timeline, and caches it so a re-export (or a preview that already rendered it) is free.
/// </summary>
[TestClass]
public sealed class SegmentAudioRendererTests
{
    private const int Rate = 48000;

    private TempDirectoryFixture? _tempDir;
    private string TempDirectory => _tempDir!.Path;

    [TestInitialize]
    public void CreateTempDirectory() => _tempDir = new TempDirectoryFixture("MusioSegmentAudioRenderer_");

    [TestCleanup]
    public void DeleteTempDirectory() => _tempDir?.Dispose();

    /// <summary>Writes a mono 48kHz PCM16 tone, the shape the recorder itself captures.</summary>
    private string WriteTone(string name, double seconds, double frequency = 440, int channels = 1)
    {
        string path = Path.Combine(TempDirectory, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, channels));

        int frames = (int)(seconds * Rate);
        var block = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(0.6 * Math.Sin(2 * Math.PI * frequency * i / Rate));
            for (int c = 0; c < channels; c++) block[c] = value;
            writer.WriteSamples(block, 0, channels);
        }

        return path;
    }

    private static TimeSpan DurationOf(string path)
    {
        using var reader = new AudioFileReader(path);
        return reader.TotalTime;
    }

    [TestMethod]
    public void SpedUpSegment_RendersExactlyTheSegmentsOutputDuration()
    {
        string source = WriteTone("mic_0.wav", seconds: 4);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? rendered = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(4), speed: 2.0, TimeSpan.FromSeconds(2));

        Assert.IsNotNull(rendered, "A plain PCM16 WAV must be renderable");
        Assert.AreEqual(
            2.0, DurationOf(rendered!).TotalSeconds, 0.01,
            "4s of source at 2x must fill the segment's 2s of output exactly");
    }

    [TestMethod]
    public void SlowedSegment_FillsTheWholeSegmentWithNoTrailingGap()
    {
        string source = WriteTone("mic_1.wav", seconds: 2);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? rendered = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(2), speed: 0.5, TimeSpan.FromSeconds(4));

        Assert.IsNotNull(rendered);
        Assert.AreEqual(
            4.0, DurationOf(rendered!).TotalSeconds, 0.01,
            "2s of source at 0.5x must be stretched across the full 4s, not stop halfway");
    }

    [TestMethod]
    public void SourceInterval_IsHonoured()
    {
        string source = WriteTone("mic_2.wav", seconds: 10);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? rendered = renderer.Render(
            source, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(4), speed: 2.0, TimeSpan.FromSeconds(2));

        Assert.IsNotNull(rendered);
        Assert.AreEqual(2.0, DurationOf(rendered!).TotalSeconds, 0.01,
            "Only the segment's own slice of the recording is rendered");
    }

    [TestMethod]
    public void StereoSource_KeepsItsChannelCount()
    {
        string source = WriteTone("system_0.wav", seconds: 2, channels: 2);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? rendered = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(2), speed: 2.0, TimeSpan.FromSeconds(1));

        Assert.IsNotNull(rendered);
        using var reader = new AudioFileReader(rendered!);
        Assert.AreEqual(2, reader.WaveFormat.Channels, "System audio is stereo and must stay stereo");
        Assert.AreEqual(Rate, reader.WaveFormat.SampleRate, "Re-timing must not resample");
    }

    [TestMethod]
    public void SecondCall_ReusesTheCachedFile()
    {
        string source = WriteTone("mic_3.wav", seconds: 3);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? first = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(3), speed: 1.5, TimeSpan.FromSeconds(2));
        Assert.IsNotNull(first);
        var writtenAt = File.GetLastWriteTimeUtc(first!);

        string? second = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(3), speed: 1.5, TimeSpan.FromSeconds(2));

        Assert.AreEqual(first, second, "The same request must resolve to the same cache entry");
        Assert.AreEqual(
            writtenAt, File.GetLastWriteTimeUtc(second!),
            "A cache hit must not re-render — that is what makes re-export and preview free");
    }

    [TestMethod]
    public void DifferentSpeedOrRange_MissesTheCache()
    {
        string source = WriteTone("mic_4.wav", seconds: 4);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? baseline = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(4), speed: 2.0, TimeSpan.FromSeconds(2));
        string? faster = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(4), speed: 4.0, TimeSpan.FromSeconds(1));
        string? trimmed = renderer.Render(
            source, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), speed: 2.0, TimeSpan.FromSeconds(1.5));

        Assert.AreNotEqual(baseline, faster, "Speed is part of the cache key");
        Assert.AreNotEqual(baseline, trimmed, "The source range is part of the cache key");
    }

    [TestMethod]
    public void NativeSpeed_RendersNothing()
    {
        string source = WriteTone("mic_5.wav", seconds: 2);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        Assert.IsNull(
            renderer.Render(source, TimeSpan.Zero, TimeSpan.FromSeconds(2), 1.0, TimeSpan.FromSeconds(2)),
            "A segment at native speed must not produce a cache entry at all");
    }

    [TestMethod]
    public void MissingOrUnreadableSource_FailsSoft()
    {
        var renderer = new SegmentAudioRenderer(TempDirectory);

        Assert.IsNull(renderer.Render(
            Path.Combine(TempDirectory, "does-not-exist.wav"),
            TimeSpan.Zero, TimeSpan.FromSeconds(2), 2.0, TimeSpan.FromSeconds(1)));

        string garbage = Path.Combine(TempDirectory, "not-audio.wav");
        File.WriteAllText(garbage, "this is not a wave file");
        Assert.IsNull(
            renderer.Render(garbage, TimeSpan.Zero, TimeSpan.FromSeconds(2), 2.0, TimeSpan.FromSeconds(1)),
            "An unreadable source must degrade to native-rate placement, never throw into the export");
    }

    [TestMethod]
    public void SourceShorterThanTheRequest_StillFillsTheSegment()
    {
        // A recording that stopped early: the placement's duration is already fixed, so the
        // rendered file has to fill it rather than come up short.
        string source = WriteTone("mic_6.wav", seconds: 1);
        var renderer = new SegmentAudioRenderer(TempDirectory);

        string? rendered = renderer.Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(4), speed: 2.0, TimeSpan.FromSeconds(2));

        Assert.IsNotNull(rendered);
        Assert.AreEqual(2.0, DurationOf(rendered!).TotalSeconds, 0.01);
    }

    [TestMethod]
    public void CachedFile_LandsInTheStretchedFolderBesideTheRecording()
    {
        string source = WriteTone("mic_7.wav", seconds: 2);

        string? rendered = new SegmentAudioRenderer().Render(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(2), speed: 2.0, TimeSpan.FromSeconds(1));

        Assert.IsNotNull(rendered);
        Assert.AreEqual(
            Path.Combine(TempDirectory, SegmentAudioRenderer.CacheFolderName),
            Path.GetDirectoryName(rendered),
            "Rendered audio belongs with the recording it came from, so it is cleaned up with it");
    }
}
