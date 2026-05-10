using System.Diagnostics;

namespace Musio.Core.AI;

/// <summary>
/// A single subtitle segment with start/end timestamps and text.
/// </summary>
public record SubtitleSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Holds the full result of a speech-to-text transcription.
/// </summary>
public class TranscriptionResult
{
    public List<SubtitleSegment> Segments { get; init; } = [];

    public string FullText =>
        string.Join(" ", Segments.Select(s => s.Text));
}

/// <summary>
/// On-device speech-to-text using Windows.Media.SpeechRecognition.
/// Uses continuous recognition mode to transcribe audio files into
/// timestamped <see cref="SubtitleSegment"/> entries.
/// </summary>
public class SpeechToText : IDisposable
{
    private Windows.Media.SpeechRecognition.SpeechRecognizer? _recognizer;
    private bool _disposed;

    /// <summary>
    /// Transcribes an audio file to timestamped subtitle segments using the
    /// Windows on-device speech recognizer.
    /// </summary>
    /// <param name="audioFilePath">Full path to a WAV audio file.</param>
    /// <param name="language">BCP-47 language tag (e.g. "en-US").</param>
    /// <param name="progress">Optional progress reporter (0.0 – 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TranscriptionResult"/> containing timestamped segments.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath,
        string language = "en-US",
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);

        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Audio file not found.", audioFilePath);

        var segments = new List<SubtitleSegment>();
        var lang = new Windows.Globalization.Language(language);

        // Clear any stale instance-level recognizer before creating a new one
        this._recognizer?.Dispose();
        this._recognizer = new Windows.Media.SpeechRecognition.SpeechRecognizer(lang);

        // Use dictation mode for natural speech with pauses
        var dictationConstraint =
            new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(
                Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation,
                "dictation");
        _recognizer.Constraints.Add(dictationConstraint);

        var compileResult = await _recognizer.CompileConstraintsAsync();
        if (compileResult.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException(
                $"Speech recognizer failed to compile constraints: {compileResult.Status}");

        progress?.Report(0.1);

        var tcs = new TaskCompletionSource<bool>();
        var runningOffset = TimeSpan.Zero;

        _recognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
        {
            if (args.Result.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success
                && !string.IsNullOrWhiteSpace(args.Result.Text))
            {
                var duration = args.Result.PhraseDuration;
                var start = args.Result.PhraseStartTime.TimeOfDay;

                // If the recognizer provides timing, use it; otherwise estimate
                var segStart = start != TimeSpan.Zero ? start : runningOffset;
                var segEnd = duration != TimeSpan.Zero
                    ? segStart + duration
                    : segStart + EstimateDuration(args.Result.Text);

                segments.Add(new SubtitleSegment(segStart, segEnd, args.Result.Text.Trim()));
                runningOffset = segEnd;
            }
        };

        _recognizer.ContinuousRecognitionSession.Completed += (_, args) =>
        {
            tcs.TrySetResult(args.Status ==
                Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success);
        };

        progress?.Report(0.2);

        // Start continuous recognition from the audio file
        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(audioFilePath);
        var stream = await storageFile.OpenAsync(Windows.Storage.FileAccessMode.Read);

        try
        {
            await _recognizer.ContinuousRecognitionSession.StartAsync();

            progress?.Report(0.5);

            // Wait for recognition to complete, cancellation, or timeout
            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(10), ct);
            if (await Task.WhenAny(tcs.Task, timeoutTask) != tcs.Task)
            {
                tcs.TrySetCanceled();
                throw new TimeoutException("Speech recognition timed out after 10 minutes.");
            }
            await tcs.Task; // propagate any exception

            try
            {
                await _recognizer.ContinuousRecognitionSession.StopAsync();
            }
            catch (Exception)
            {
                // Session may already be stopped
            }
        }
        finally
        {
            stream.Dispose();
        }

        progress?.Report(1.0);

        return new TranscriptionResult { Segments = segments };
    }

    /// <summary>
    /// Rough estimate of speech duration based on word count (~150 words per minute).
    /// </summary>
    private static TimeSpan EstimateDuration(string text)
    {
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var seconds = wordCount / 2.5; // ~150 wpm ≈ 2.5 words/s
        return TimeSpan.FromSeconds(Math.Max(seconds, 1.0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _recognizer?.Dispose();
        _recognizer = null;

        GC.SuppressFinalize(this);
    }
}
