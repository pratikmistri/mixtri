using System.Globalization;
using System.Text;

namespace Mixtri.Core.AI;

/// <summary>
/// A single subtitle segment with start/end timestamps and text.
/// </summary>
public record SubtitleSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Holds the full result of a transcription: timestamped segments ready for
/// <see cref="SubtitleGenerator"/> and <see cref="SubtitleBurner"/>.
/// </summary>
/// <remarks>
/// These types used to live in <c>SpeechToText.cs</c>. That class was removed: nothing called
/// it, and it transcribed the LIVE microphone (not the audio file it was given) with
/// wall-clock timestamps. The data types survive here because the subtitle pipeline uses them.
/// </remarks>
public class TranscriptionResult
{
    public List<SubtitleSegment> Segments { get; init; } = [];

    public string FullText =>
        string.Join(" ", Segments.Select(s => s.Text));
}

/// <summary>
/// Generates subtitle files in SRT and WebVTT formats from a <see cref="TranscriptionResult"/>.
/// </summary>
public static class SubtitleGenerator
{
    /// <summary>
    /// Converts a transcription result to SRT (SubRip) format.
    /// </summary>
    public static string ToSrt(TranscriptionResult transcription)
    {
        ArgumentNullException.ThrowIfNull(transcription);

        var sb = new StringBuilder();

        for (int i = 0; i < transcription.Segments.Count; i++)
        {
            var seg = transcription.Segments[i];

            if (i > 0)
                sb.AppendLine();

            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatSrtTime(seg.Start)} --> {FormatSrtTime(seg.End)}");
            sb.AppendLine(seg.Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a transcription result to WebVTT format.
    /// </summary>
    public static string ToVtt(TranscriptionResult transcription)
    {
        ArgumentNullException.ThrowIfNull(transcription);

        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        for (int i = 0; i < transcription.Segments.Count; i++)
        {
            var seg = transcription.Segments[i];
            sb.AppendLine($"{FormatVttTime(seg.Start)} --> {FormatVttTime(seg.End)}");
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Saves the transcription as an SRT file.
    /// </summary>
    public static async Task SaveSrtAsync(TranscriptionResult transcription, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var srt = ToSrt(transcription);
        await File.WriteAllTextAsync(outputPath, srt, Encoding.UTF8);
    }

    /// <summary>
    /// Saves the transcription as a WebVTT file.
    /// </summary>
    public static async Task SaveVttAsync(TranscriptionResult transcription, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var vtt = ToVtt(transcription);
        await File.WriteAllTextAsync(outputPath, vtt, Encoding.UTF8);
    }

    /// <summary>
    /// Formats a TimeSpan as SRT timestamp: HH:MM:SS,mmm
    /// </summary>
    private static string FormatSrtTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

    /// <summary>
    /// Formats a TimeSpan as VTT timestamp: HH:MM:SS.mmm
    /// </summary>
    private static string FormatVttTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
}
