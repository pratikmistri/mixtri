using System.Globalization;
using System.Text;

namespace Mixtri.Core.AI;

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
