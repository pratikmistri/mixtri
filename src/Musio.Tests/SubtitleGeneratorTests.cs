using Musio.Core.AI;

namespace Musio.Tests;

[TestClass]
public sealed class SubtitleGeneratorTests
{
    private static TranscriptionResult BuildTranscription(params (double startSec, double endSec, string text)[] segments)
    {
        return new TranscriptionResult
        {
            Segments = segments.Select(s => new SubtitleSegment(
                TimeSpan.FromSeconds(s.startSec),
                TimeSpan.FromSeconds(s.endSec),
                s.text)).ToList()
        };
    }

    #region ToSrt

    [TestMethod]
    public void ToSrt_SingleSegment_CorrectFormat()
    {
        var transcription = BuildTranscription((0, 2.5, "Hello world"));

        string srt = SubtitleGenerator.ToSrt(transcription);

        Assert.IsTrue(srt.Contains("1"), "Should have 1-based sequence number");
        Assert.IsTrue(srt.Contains("00:00:00,000 --> 00:00:02,500"), "Should have SRT timestamps with comma");
        Assert.IsTrue(srt.Contains("Hello world"), "Should contain the text");
    }

    [TestMethod]
    public void ToSrt_MultipleSegments_CorrectSequencing()
    {
        var transcription = BuildTranscription(
            (0, 2, "First"),
            (2, 4, "Second"),
            (4, 6, "Third"));

        string srt = SubtitleGenerator.ToSrt(transcription);
        var lines = srt.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Should have sequence numbers 1, 2, 3
        Assert.IsTrue(lines.Any(l => l.Trim() == "1"));
        Assert.IsTrue(lines.Any(l => l.Trim() == "2"));
        Assert.IsTrue(lines.Any(l => l.Trim() == "3"));
    }

    [TestMethod]
    public void ToSrt_EmptySegments_ReturnsEmpty()
    {
        var transcription = new TranscriptionResult { Segments = [] };

        string srt = SubtitleGenerator.ToSrt(transcription);

        Assert.AreEqual(string.Empty, srt);
    }

    [TestMethod]
    public void ToSrt_NullTranscription_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => SubtitleGenerator.ToSrt(null!));
    }

    [TestMethod]
    public void ToSrt_LongTimestamp_FormatsHoursCorrectly()
    {
        var transcription = BuildTranscription((3661.5, 3665, "Over an hour in"));

        string srt = SubtitleGenerator.ToSrt(transcription);

        Assert.IsTrue(srt.Contains("01:01:01,500"), "Should format hours correctly");
    }

    #endregion

    #region ToVtt

    [TestMethod]
    public void ToVtt_HasWebVTTHeader()
    {
        var transcription = BuildTranscription((0, 1, "Test"));

        string vtt = SubtitleGenerator.ToVtt(transcription);

        Assert.IsTrue(vtt.StartsWith("WEBVTT"), "VTT must start with WEBVTT header");
    }

    [TestMethod]
    public void ToVtt_UsesDotForMilliseconds()
    {
        var transcription = BuildTranscription((1.5, 3.75, "Hello"));

        string vtt = SubtitleGenerator.ToVtt(transcription);

        Assert.IsTrue(vtt.Contains("00:00:01.500"), "VTT uses dot separator for milliseconds");
        Assert.IsTrue(vtt.Contains("00:00:03.750"), "VTT uses dot separator for milliseconds");
    }

    [TestMethod]
    public void ToVtt_EmptySegments_ReturnsHeaderOnly()
    {
        var transcription = new TranscriptionResult { Segments = [] };

        string vtt = SubtitleGenerator.ToVtt(transcription);

        Assert.IsTrue(vtt.TrimEnd().StartsWith("WEBVTT"));
    }

    [TestMethod]
    public void ToVtt_NullTranscription_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => SubtitleGenerator.ToVtt(null!));
    }

    #endregion

    #region File I/O

    [TestMethod]
    [DoNotParallelize]
    public async Task SaveSrtAsync_WritesCorrectFile()
    {
        var transcription = BuildTranscription((0, 1, "Hello"), (1, 2, "World"));
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.srt");

        try
        {
            await SubtitleGenerator.SaveSrtAsync(transcription, tempFile);

            Assert.IsTrue(File.Exists(tempFile), "SRT file should be created");
            string content = await File.ReadAllTextAsync(tempFile);
            Assert.IsTrue(content.Contains("Hello"));
            Assert.IsTrue(content.Contains("World"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SaveVttAsync_WritesCorrectFile()
    {
        var transcription = BuildTranscription((0, 1, "Test subtitle"));
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.vtt");

        try
        {
            await SubtitleGenerator.SaveVttAsync(transcription, tempFile);

            Assert.IsTrue(File.Exists(tempFile), "VTT file should be created");
            string content = await File.ReadAllTextAsync(tempFile);
            Assert.IsTrue(content.StartsWith("WEBVTT"));
            Assert.IsTrue(content.Contains("Test subtitle"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task SaveSrtAsync_NullPath_ThrowsException()
    {
        var transcription = BuildTranscription((0, 1, "Test"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => SubtitleGenerator.SaveSrtAsync(transcription, ""));
    }

    #endregion
}
