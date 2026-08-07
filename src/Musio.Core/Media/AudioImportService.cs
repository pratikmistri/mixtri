using System.Diagnostics;
using Musio.Core.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Musio.Core.Media;

/// <summary>
/// Outcome of normalising an external audio file into Musio's working-media layout.
/// </summary>
public sealed record AudioImportResult
{
    /// <summary>Absolute path of the normalised <c>audio.wav</c>.</summary>
    public required string AudioFilePath { get; init; }

    /// <summary>Absolute path of the <c>import_*</c> folder holding the normalised media.</summary>
    public required string ImportFolder { get; init; }

    /// <summary>Duration of the normalised audio, measured from the produced file.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Original file name (with extension), used to name the inserted track.</summary>
    public string SourceFileName { get; init; } = "";
}

/// <summary>
/// Raised when an external audio file cannot be imported. Carries a message safe to show a
/// user; the technical cause, when there is one, is preserved as the inner exception.
/// </summary>
public sealed class AudioImportException : Exception
{
    public AudioImportException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Normalises an external audio file (MP3, M4A, WAV, …) into a PCM WAV the editor and
/// exporter already understand.
/// </summary>
/// <remarks>
/// <para>
/// Import always re-encodes, for the same reason <see cref="VideoImportService"/> does:
/// normalise once so nothing downstream needs a second code path.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Preview.</b> <c>AudioPlaybackEngine</c> mixes with NAudio and seeks by byte offset
/// (<c>position × AverageBytesPerSecond</c>, block-aligned). That arithmetic is only exact
/// for constant-bitrate PCM; on a compressed source it lands at approximately the right
/// place, which is precisely the drift a positioned voice-over must not have.
/// </description></item>
/// <item><description>
/// <b>Export.</b> The mux opens each source through
/// <c>BackgroundAudioTrack.CreateFromFileAsync</c>. That tolerates far more than
/// <c>MediaClip</c>, but VBR MP3s and files with encoder delay/padding report durations
/// that disagree with what the decoder actually produces, so a planned trim/take would cut
/// in the wrong place.
/// </description></item>
/// <item><description>
/// <b>Waveforms.</b> <c>AudioWaveformGenerator</c> reads PCM sample data directly.
/// </description></item>
/// </list>
/// <para>
/// Unlike a video import this writes no <c>finalized.marker</c>: the marker exists purely to
/// tell <c>RecordingMarker</c> a video is not a legacy, vertically flipped recording, and an
/// audio-only folder is never read down that path.
/// </para>
/// </remarks>
public static class AudioImportService
{
    /// <summary>File extensions offered in the audio import picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".mp3", ".m4a", ".wav", ".aac", ".wma", ".flac", ".ogg"];

    private const string AudioFileName = "audio.wav";
    private const string PartialSuffix = ".partial";

    /// <summary>Guard against a hung encoder leaving the UI waiting forever on a bad file.</summary>
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Imports <paramref name="sourcePath"/> by transcoding it into a fresh <c>import_*</c>
    /// folder as a PCM WAV, and returns the resulting media layout.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the external audio file to import.</param>
    /// <param name="destinationRoot">
    /// Root under which the <c>import_*</c> folder is created; <c>null</c> derives it from
    /// <see cref="SessionPaths.ImportsRoot"/>.
    /// </param>
    /// <param name="progress">Receives 0..1 progress across the transcode.</param>
    /// <param name="ct">Cancels the import, including any in-flight transcode.</param>
    /// <exception cref="AudioImportException">
    /// The file is missing, unreadable, has no audio stream, or the transcode failed. The
    /// message is safe to surface to a user.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled via <paramref name="ct"/>.</exception>
    public static async Task<AudioImportResult> ImportAsync(
        string sourcePath,
        string? destinationRoot,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new AudioImportException("No file was selected to import.");

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            throw new AudioImportException("The selected file path is not valid.", ex);
        }

        var sourceInfo = new FileInfo(fullSource);
        if (!sourceInfo.Exists)
            throw new AudioImportException("The selected audio file could not be found.");
        if (sourceInfo.Length == 0)
            throw new AudioImportException("The selected audio file is empty.");

        var extension = Path.GetExtension(fullSource);
        if (!SupportedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AudioImportException(
                $"'{extension}' files are not supported. Supported formats: " +
                string.Join(", ", SupportedExtensions) + ".");
        }

        ct.ThrowIfCancellationRequested();

        StorageFile sourceFile;
        try
        {
            sourceFile = await StorageFile.GetFileFromPathAsync(fullSource);
        }
        catch (Exception ex)
        {
            throw new AudioImportException("The selected audio file could not be opened.", ex);
        }

        progress?.Report(0.02);

        string importFolder;
        try
        {
            importFolder = SessionPaths.CreateImportFolder(destinationRoot);
        }
        catch (Exception ex)
        {
            throw new AudioImportException("Could not create a folder to hold the imported audio.", ex);
        }

        string audioPath = Path.Combine(importFolder, AudioFileName);
        string audioPartial = audioPath + PartialSuffix;

        try
        {
            await TranscodeAudioAsync(
                sourceFile, importFolder, audioPartial,
                p => progress?.Report(0.02 + (p * 0.93)), ct);

            File.Move(audioPartial, audioPath, overwrite: true);

            // Measured from the PRODUCED file for the same reason VideoImportService does:
            // the normalised PCM WAV's own duration is authoritative, where a compressed
            // source's container metadata may not match what its decoder emits. A wrong
            // duration here would misplace every later edit of this track.
            var duration = await MeasureDurationAsync(audioPath, ct);
            if (duration <= TimeSpan.Zero)
                throw new AudioImportException("The imported audio has no playable content.");

            progress?.Report(1.0);

            Debug.WriteLine(
                $"[AudioImportService] Imported '{Path.GetFileName(fullSource)}' " +
                $"to '{importFolder}' ({duration}).");

            return new AudioImportResult
            {
                AudioFilePath = audioPath,
                ImportFolder = importFolder,
                Duration = duration,
                SourceFileName = Path.GetFileName(fullSource),
            };
        }
        catch (OperationCanceledException)
        {
            CleanupImportFolder(importFolder, audioPartial);
            throw;
        }
        catch (AudioImportException)
        {
            CleanupImportFolder(importFolder, audioPartial);
            throw;
        }
        catch (Exception ex)
        {
            CleanupImportFolder(importFolder, audioPartial);
            throw new AudioImportException(
                "The audio could not be imported. It may be in an unsupported or damaged format.",
                ex);
        }
    }

    /// <summary>
    /// Transcodes <paramref name="sourceFile"/> into <paramref name="partialPath"/> as a PCM WAV.
    /// </summary>
    private static async Task TranscodeAudioAsync(
        StorageFile sourceFile,
        string importFolder,
        string partialPath,
        Action<double> progress,
        CancellationToken ct)
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        // A WAV container rejects the profile on some sources if a video stream is still
        // described — the same guard VideoImportService's audio extraction needs. An audio
        // file with cover art is a real instance of this, not a theoretical one.
        profile.Video = null;

        var folder = await StorageFolder.GetFolderFromPathAsync(importFolder);
        var destFile = await folder.CreateFileAsync(
            Path.GetFileName(partialPath), CreationCollisionOption.ReplaceExisting);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };

        PrepareTranscodeResult prep;
        try
        {
            prep = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile).AsTask(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AudioImportException(
                "The audio could not be prepared for import. Its format may be unsupported.", ex);
        }

        if (!prep.CanTranscode)
        {
            throw new AudioImportException(
                $"The audio cannot be converted for editing ({prep.FailureReason}).");
        }

        var op = prep.TranscodeAsync();
        op.Progress = (_, percent) => progress(Math.Clamp(percent / 100.0, 0.0, 1.0));

        using var registration = ct.Register(() =>
        {
            try { op.Cancel(); } catch { /* already completing */ }
        });

        var transcodeTask = op.AsTask();
        // Not linked to ct: cancellation flows through the registration above (which cancels
        // the op, completing transcodeTask), so the timeout must not itself complete on
        // cancel or a cancelled import would be misreported as a timeout.
        var timeoutTask = Task.Delay(TranscodeTimeout);

        var completed = await Task.WhenAny(transcodeTask, timeoutTask).ConfigureAwait(false);
        if (completed != transcodeTask)
        {
            try { op.Cancel(); } catch { }
            try { await transcodeTask.ConfigureAwait(false); } catch { }
            throw new AudioImportException($"Importing the audio timed out after {TranscodeTimeout}.");
        }

        await transcodeTask.ConfigureAwait(false);

        if (new FileInfo(partialPath).Length == 0)
            throw new AudioImportException("Importing the audio produced an empty file.");
    }

    /// <summary>
    /// Reads the true duration of the freshly produced WAV.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="MediaSource"/> rather than <c>MediaClip</c>/<c>BackgroundAudioTrack</c>
    /// deliberately: those release their file handle only when collected, and a pinned
    /// <c>audio.wav</c> would defeat <see cref="CleanupImportFolder"/> if a later step failed,
    /// leaking the folder permanently (the exact bug PR #63 review found in the video import).
    /// </remarks>
    private static async Task<TimeSpan> MeasureDurationAsync(string audioPath, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(audioPath));

            var source = MediaSource.CreateFromStorageFile(file);
            try
            {
                await source.OpenAsync().AsTask(ct);
                if (source.Duration is { } d && d > TimeSpan.Zero)
                    return d;
            }
            finally
            {
                source.Dispose();
            }

            var props = await file.Properties.GetMusicPropertiesAsync().AsTask(ct);
            if (props.Duration > TimeSpan.Zero)
                return props.Duration;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioImportService] Could not measure '{audioPath}': {ex.Message}");
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Removes any partials and the (now-incomplete) import folder on failure or cancel, so a
    /// half-written <c>audio.wav</c> can never be mistaken for a finished import.
    /// </summary>
    private static void CleanupImportFolder(string importFolder, params string[] partials)
    {
        foreach (var partial in partials)
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception ex) { Debug.WriteLine($"[AudioImportService] Could not delete partial '{partial}': {ex.Message}"); }
        }

        try
        {
            if (Directory.Exists(importFolder))
                Directory.Delete(importFolder, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioImportService] Could not remove import folder '{importFolder}': {ex.Message}");
        }
    }
}
