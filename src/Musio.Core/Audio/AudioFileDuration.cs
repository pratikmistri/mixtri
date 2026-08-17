using NAudio.Wave;

namespace Musio.Core.Audio;

/// <summary>
/// Reads how long an audio file is, without decoding it.
/// </summary>
/// <remarks>
/// Deliberately synchronous and NAudio-based, unlike
/// <c>VideoEncoder.TryGetAudioFileDurationAsync</c> (WinRT
/// <c>BackgroundAudioTrack</c>): the callers here are editor-side, already off the UI thread,
/// and need the answer for a recorded <c>.wav</c> — which <see cref="AudioFileReader"/> reads
/// through <c>WaveFileReader</c>, i.e. with no Media Foundation involvement at all. The
/// export path keeps its own probe because it must agree with the decoder that will actually
/// mux the file.
/// </remarks>
public static class AudioFileDuration
{
    /// <summary>
    /// The file's total duration, or <c>null</c> if it cannot be read (missing, locked, or a
    /// format with no decoder here). Never throws — callers treat <c>null</c> as "unknown"
    /// and fall back to bounding the audio some other way.
    /// </summary>
    public static TimeSpan? TryGet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime;
        }
        catch (Exception ex)
        {
            Diagnostics.DiagLog.Write("Audio",
                $"could not read duration of '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }
}
