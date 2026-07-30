using System.Diagnostics;
using Musio.Core.Capture;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Musio.Core.Media;

/// <summary>
/// Outcome of normalising an external video into Musio's working-media layout.
/// </summary>
/// <remarks>
/// The shape mirrors what a freshly recorded session hands the editor: a single
/// <c>video.mp4</c> plus a list of side-car audio files. That is deliberate — the whole
/// point of the import is to produce media the existing decode, orientation and export code
/// already trusts, so nothing downstream needs a second "imported video" code path.
/// </remarks>
public sealed record VideoImportResult
{
    /// <summary>Absolute path of the normalised, orientation-correct <c>video.mp4</c>.</summary>
    public required string VideoFilePath { get; init; }

    /// <summary>Absolute path of the <c>import_*</c> folder holding the normalised media.</summary>
    public required string ImportFolder { get; init; }

    /// <summary>
    /// Absolute paths of extracted audio tracks, empty when the source had no audio. These go
    /// down the export's <see cref="Windows.Media.Editing.BackgroundAudioTrack"/> path rather
    /// than the embedded-audio mux, which rejects fragmented MP4s and files with edit lists.
    /// </summary>
    public List<string> AudioFilePaths { get; init; } = [];

    /// <summary>Duration of the normalised video.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Pixel width of the normalised video (even, H.264-safe).</summary>
    public int Width { get; init; }

    /// <summary>Pixel height of the normalised video (even, H.264-safe).</summary>
    public int Height { get; init; }

    /// <summary>Constant frame rate the video was normalised to.</summary>
    public int Fps { get; init; }

    /// <summary>Original file name (without directory), used to name the created project.</summary>
    public string SourceFileName { get; init; } = "";

    /// <summary>Always true today: import always re-encodes to guarantee CFR + orientation.</summary>
    public bool WasTranscoded { get; init; }

    /// <summary>True when an <c>audio.wav</c> was extracted.</summary>
    public bool HasAudio { get; init; }
}

/// <summary>
/// Raised when an external video cannot be imported. Carries a message safe to show a user;
/// the technical cause, when there is one, is preserved as the inner exception.
/// </summary>
public sealed class VideoImportException : Exception
{
    public VideoImportException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Normalises an external (not Musio-recorded) video into the working-media layout the
/// editor and exporter already understand.
/// </summary>
/// <remarks>
/// <para>
/// Three established constraints make a straight copy unsafe, so import always re-encodes:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Orientation.</b> A bare MP4 has no <c>finalized.marker</c>, so
/// <see cref="RecordingMarker.ReadEncoderVersion"/> reports it as legacy and every read path
/// would flip it vertically for both preview and export. Import writes a marker declaring the
/// current <see cref="VideoWriter.EncoderVersion"/> so
/// <see cref="RecordingMarker.NeedsVerticalFlip"/> returns <c>false</c>.
/// </description></item>
/// <item><description>
/// <b>Audio mux.</b> Export muxes embedded audio through <c>MediaClip.CreateFromFileAsync</c>,
/// which rejects fragmented MP4s and files carrying edit lists — exactly what phone, screen-
/// recorder and stock footage tend to be. Import instead extracts audio to a stand-alone WAV
/// registered in <see cref="VideoImportResult.AudioFilePaths"/>, which exports via the far more
/// tolerant <c>BackgroundAudioTrack.CreateFromFileAsync</c> path.
/// </description></item>
/// <item><description>
/// <b>Variable frame rate.</b> The timeline maps frame index to time assuming a constant fps.
/// VFR sources (phones, screen recorders) drift under that assumption, so import transcodes to
/// a constant frame rate.
/// </description></item>
/// </list>
/// </remarks>
public static class VideoImportService
{
    /// <summary>File extensions offered in the import picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv", ".webm"];

    /// <summary>Default constant frame rate used when the source rate cannot be read.</summary>
    private const int DefaultFps = 30;

    /// <summary>Upper bound on the normalised frame rate — high-refresh sources are capped here.</summary>
    private const int MaxFps = 60;

    /// <summary>Base video bitrate at 1080p; scaled by pixel count like the recorder/exporter.</summary>
    private const uint BaseBitrate1080p = 20_000_000;

    private const string VideoFileName = "video.mp4";
    private const string AudioFileName = "audio.wav";
    private const string PartialSuffix = ".partial";

    /// <summary>Guard against a hung encoder leaving the UI waiting forever on a bad file.</summary>
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Imports <paramref name="sourcePath"/> by normalising it into a fresh <c>import_*</c>
    /// folder and returns the resulting media layout.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the external video to import.</param>
    /// <param name="destinationRoot">
    /// Root under which the <c>import_*</c> folder is created; <c>null</c> derives it from
    /// <see cref="SessionPaths.ImportsRoot"/>.
    /// </param>
    /// <param name="progress">Receives 0..1 progress across the probe, transcode and audio phases.</param>
    /// <param name="ct">Cancels the import, including any in-flight transcode.</param>
    /// <exception cref="VideoImportException">
    /// The file is missing, unreadable, has no video stream, or the transcode failed. The message
    /// is safe to surface to a user.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled via <paramref name="ct"/>.</exception>
    public static async Task<VideoImportResult> ImportAsync(
        string sourcePath,
        string? destinationRoot,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new VideoImportException("No file was selected to import.");

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            throw new VideoImportException("The selected file path is not valid.", ex);
        }

        var sourceInfo = new FileInfo(fullSource);
        if (!sourceInfo.Exists)
            throw new VideoImportException("The selected video file could not be found.");
        if (sourceInfo.Length == 0)
            throw new VideoImportException("The selected video file is empty.");

        var extension = Path.GetExtension(fullSource);
        if (!SupportedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VideoImportException(
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
            throw new VideoImportException("The selected video file could not be opened.", ex);
        }

        progress?.Report(0.02);

        // ── Phase 1: probe ────────────────────────────────────────────────────────────────
        // Resilient by design: no single probe is trusted for everything, and a probe failure
        // never fails the import so long as we can still recover dimensions to encode against.
        var probe = await ProbeAsync(sourceFile, ct);
        if (probe.Width <= 0 || probe.Height <= 0)
        {
            throw new VideoImportException(
                "The selected file does not contain a readable video stream.");
        }

        // H.264 requires even dimensions; the recorder uses the same mod-2 rule.
        int width = probe.Width & ~1;
        int height = probe.Height & ~1;
        if (width < 2) width = 2;
        if (height < 2) height = 2;

        int fps = probe.Fps > 0 ? Math.Min(probe.Fps, MaxFps) : DefaultFps;

        Debug.WriteLine(
            $"[VideoImportService] Probed '{Path.GetFileName(fullSource)}': " +
            $"{width}x{height} @ {fps}fps, {probe.Duration}, hasAudio(probe)={probe.HasAudio}");

        progress?.Report(0.08);

        // ── Working folder ────────────────────────────────────────────────────────────────
        string importFolder;
        try
        {
            importFolder = SessionPaths.CreateImportFolder(destinationRoot);
        }
        catch (Exception ex)
        {
            throw new VideoImportException("Could not create a folder to hold the imported video.", ex);
        }

        string videoPath = Path.Combine(importFolder, VideoFileName);
        string videoPartial = videoPath + PartialSuffix;
        string audioPath = Path.Combine(importFolder, AudioFileName);
        string audioPartial = audioPath + PartialSuffix;

        try
        {
            // ── Phase 2: transcode video to CFR, video-only H.264 ─────────────────────────
            // Audio is stripped here (profile.Audio = null) and extracted separately below,
            // because export re-adds it through the BackgroundAudioTrack path — muxing the
            // embedded track would hit MediaClip's fragmented-MP4 rejection.
            await TranscodeVideoAsync(
                sourceFile, importFolder, videoPartial, width, height, fps,
                p => progress?.Report(0.08 + (p * 0.77)), ct);

            // Move into place and stamp the marker together: a `video.mp4` existing must imply
            // a complete, playable, orientation-correct file (the same invariant the recorder
            // upholds), because RecordingMarker/SessionCleanupService key off its presence.
            File.Move(videoPartial, videoPath, overwrite: true);
            WriteSessionMarker(importFolder);

            progress?.Report(0.86);

            // Duration comes from the PRODUCED file, not the source probe. Fragmented MP4s
            // (empty `moov`) and some containers report no usable duration up front — the
            // source probe returned ~0.067s for the fragmented sample — but the normalised
            // output is a clean CFR MP4 whose real, playable duration is authoritative. A
            // wrong duration here would desync the entire timeline, so this is not optional.
            var duration = await MeasureOutputDurationAsync(videoPath, probe.Duration, ct);

            // ── Phase 3: extract audio (best effort) ──────────────────────────────────────
            // Source is the ORIGINAL file: the normalised MP4 above is intentionally video-
            // only, and re-decoding the untouched original avoids a needless second generation
            // of audio loss. A source with no audio is not an error — extraction simply yields
            // nothing and import continues.
            bool hasAudio = await TryExtractAudioAsync(
                sourceFile, importFolder, audioPartial, audioPath,
                p => progress?.Report(0.86 + (p * 0.13)), ct);

            progress?.Report(1.0);

            var result = new VideoImportResult
            {
                VideoFilePath = videoPath,
                ImportFolder = importFolder,
                AudioFilePaths = hasAudio ? [audioPath] : [],
                Duration = duration,
                Width = width,
                Height = height,
                Fps = fps,
                SourceFileName = Path.GetFileName(fullSource),
                WasTranscoded = true,
                HasAudio = hasAudio,
            };

            Debug.WriteLine(
                $"[VideoImportService] Imported to '{importFolder}' " +
                $"(hasAudio={hasAudio}, needsFlip={RecordingMarker.NeedsVerticalFlip(videoPath)}).");

            return result;
        }
        catch (OperationCanceledException)
        {
            CleanupImportFolder(importFolder, videoPartial, audioPartial);
            throw;
        }
        catch (VideoImportException)
        {
            CleanupImportFolder(importFolder, videoPartial, audioPartial);
            throw;
        }
        catch (Exception ex)
        {
            CleanupImportFolder(importFolder, videoPartial, audioPartial);
            throw new VideoImportException(
                "The video could not be imported. It may be in an unsupported or damaged format.",
                ex);
        }
    }

    /// <summary>Dimensions, duration, frame rate and audio presence gathered from a source file.</summary>
    private readonly record struct ProbeResult(
        int Width, int Height, TimeSpan Duration, int Fps, bool HasAudio);

    /// <summary>
    /// Gathers metadata using several tolerant paths, each guarded so one failing never denies
    /// the others. Dimensions/duration come first from <see cref="Windows.Storage.FileProperties.VideoProperties"/>
    /// (which reads container metadata and tolerates fragmented MP4s), then any gaps — and the
    /// frame rate, which those properties do not expose — are filled from <c>MediaClip</c> and
    /// finally a <c>MediaPlaybackItem</c>.
    /// </summary>
    private static async Task<ProbeResult> ProbeAsync(StorageFile file, CancellationToken ct)
    {
        int width = 0, height = 0, fps = 0;
        var duration = TimeSpan.Zero;
        bool hasAudio = false;

        // (a) Container properties — most tolerant, but no frame rate.
        try
        {
            var props = await file.Properties.GetVideoPropertiesAsync().AsTask(ct);
            width = (int)props.Width;
            height = (int)props.Height;
            duration = props.Duration;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoImportService] VideoProperties probe failed: {ex.Message}");
        }

        // (b) MediaClip — gives an exact frame rate and audio-track count, but throws on
        //     fragmented MP4s / edit lists, hence the guard.
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct);
            var vprops = clip.GetVideoEncodingProperties();
            if (width <= 0) width = (int)vprops.Width;
            if (height <= 0) height = (int)vprops.Height;
            if (duration <= TimeSpan.Zero) duration = clip.OriginalDuration;
            if (vprops.FrameRate.Denominator > 0)
                fps = (int)Math.Round(vprops.FrameRate.Numerator / (double)vprops.FrameRate.Denominator);
            hasAudio = clip.EmbeddedAudioTracks.Count > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoImportService] MediaClip probe failed (expected for fragmented MP4s): {ex.Message}");
        }

        // (c) MediaPlaybackItem — the tolerant frame-server path. Only worth trying when the
        //     cheaper probes left something unknown (the common fragmented-MP4 case).
        if (fps <= 0 || width <= 0 || height <= 0)
        {
            try
            {
                var (pbW, pbH, pbFps, pbHasAudio) = await ProbePlaybackItemAsync(file, ct);
                if (width <= 0) width = pbW;
                if (height <= 0) height = pbH;
                if (fps <= 0) fps = pbFps;
                hasAudio = hasAudio || pbHasAudio;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoImportService] MediaPlaybackItem probe failed: {ex.Message}");
            }
        }

        return new ProbeResult(width, height, duration, fps, hasAudio);
    }

    /// <summary>
    /// Resolves a <see cref="MediaPlaybackItem"/>'s tracks to read frame rate and audio
    /// presence without decoding a frame. The item populates its track lists asynchronously,
    /// so we await <see cref="MediaSource.OpenAsync"/> before reading them.
    /// </summary>
    private static async Task<(int Width, int Height, int Fps, bool HasAudio)> ProbePlaybackItemAsync(
        StorageFile file, CancellationToken ct)
    {
        var source = MediaSource.CreateFromStorageFile(file);
        try
        {
            await source.OpenAsync().AsTask(ct);
            var item = new MediaPlaybackItem(source);

            int width = 0, height = 0, fps = 0;
            if (item.VideoTracks.Count > 0)
            {
                var vprops = item.VideoTracks[0].GetEncodingProperties();
                width = (int)vprops.Width;
                height = (int)vprops.Height;
                if (vprops.FrameRate.Denominator > 0)
                    fps = (int)Math.Round(vprops.FrameRate.Numerator / (double)vprops.FrameRate.Denominator);
            }

            return (width, height, fps, item.AudioTracks.Count > 0);
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// Reads the true duration of the freshly produced <paramref name="videoPath"/>. Because the
    /// output is a clean CFR MP4 (unlike a fragmented or edit-list source), its own metadata is
    /// authoritative; only if that somehow fails do we fall back to the source-probe value.
    /// </summary>
    private static async Task<TimeSpan> MeasureOutputDurationAsync(
        string videoPath, TimeSpan fallback, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));

            try
            {
                var props = await file.Properties.GetVideoPropertiesAsync().AsTask(ct);
                if (props.Duration > TimeSpan.Zero)
                    return props.Duration;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoImportService] Output VideoProperties duration failed: {ex.Message}");
            }

            try
            {
                var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct);
                if (clip.OriginalDuration > TimeSpan.Zero)
                    return clip.OriginalDuration;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoImportService] Output MediaClip duration failed: {ex.Message}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoImportService] Could not open output to measure duration: {ex.Message}");
        }

        return fallback > TimeSpan.Zero ? fallback : TimeSpan.Zero;
    }

    /// <summary>
    /// Transcodes <paramref name="sourceFile"/> into <paramref name="partialPath"/> as a
    /// constant-frame-rate, video-only H.264 MP4.
    /// </summary>
    /// <remarks>
    /// The profile follows the export playbook: never hand-build a <see cref="MediaEncodingProfile"/>
    /// and never use <see cref="VideoEncodingQuality.Auto"/> (its incomplete properties corrupt the
    /// mux). We start from a well-formed <c>CreateMp4(HD1080p)</c> template and override
    /// Width/Height/Bitrate/FrameRate/Subtype, letting the encoder pick the correct H.264 Level from
    /// the real dimensions. Hardware acceleration is off to dodge the AMD H.264 corruption both other
    /// pipelines avoid.
    /// </remarks>
    private static async Task TranscodeVideoAsync(
        StorageFile sourceFile,
        string importFolder,
        string partialPath,
        int width,
        int height,
        int fps,
        Action<double> progress,
        CancellationToken ct)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video!.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = ComputeBitrate(width, height);
        profile.Video.Subtype = "H264";
        // Video-only: audio travels as a side-car WAV (see class remarks).
        profile.Audio = null;

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
            throw new VideoImportException(
                "The video could not be prepared for import. Its format may be unsupported.", ex);
        }

        if (!prep.CanTranscode)
        {
            throw new VideoImportException(
                $"The video cannot be converted for editing ({prep.FailureReason}).");
        }

        await RunTranscodeAsync(prep, progress, ct);

        if (new FileInfo(partialPath).Length == 0)
            throw new VideoImportException("Importing the video produced an empty file.");
    }

    /// <summary>
    /// Extracts the source's audio to a WAV side-car. Returns <c>false</c> — without throwing —
    /// when the source has no audio, so a silent clip imports as video-only rather than failing.
    /// </summary>
    private static async Task<bool> TryExtractAudioAsync(
        StorageFile sourceFile,
        string importFolder,
        string partialPath,
        string finalPath,
        Action<double> progress,
        CancellationToken ct)
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        // Drop the video stream so the transcoder emits audio only; leaving it in makes a WAV
        // container reject the profile on some sources.
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
            // No audio stream (or one the transcoder rejects): not fatal, import stays video-only.
            Debug.WriteLine($"[VideoImportService] Audio prepare failed, treating as no audio: {ex.Message}");
            await SafeDeleteAsync(destFile);
            return false;
        }

        if (!prep.CanTranscode)
        {
            Debug.WriteLine(
                $"[VideoImportService] No extractable audio ({prep.FailureReason}); importing video-only.");
            await SafeDeleteAsync(destFile);
            return false;
        }

        try
        {
            await RunTranscodeAsync(prep, progress, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoImportService] Audio extraction failed, importing video-only: {ex.Message}");
            await SafeDeleteAsync(destFile);
            return false;
        }

        if (new FileInfo(partialPath).Length == 0)
        {
            await SafeDeleteAsync(destFile);
            return false;
        }

        File.Move(partialPath, finalPath, overwrite: true);
        return true;
    }

    /// <summary>
    /// Runs a prepared transcode with progress, cancellation and a watchdog timeout so a hung
    /// encoder cannot wedge the import forever.
    /// </summary>
    private static async Task RunTranscodeAsync(
        PrepareTranscodeResult prep, Action<double> progress, CancellationToken ct)
    {
        var op = prep.TranscodeAsync();
        op.Progress = (_, percent) => progress(Math.Clamp(percent / 100.0, 0.0, 1.0));

        using var registration = ct.Register(() =>
        {
            try { op.Cancel(); } catch { /* already completing */ }
        });

        var transcodeTask = op.AsTask();
        // Not linked to ct: cancellation flows through the registration above (which cancels
        // the op, completing transcodeTask), so the timeout must not itself complete on cancel
        // or a cancelled import could be misreported as a timeout.
        var timeoutTask = Task.Delay(TranscodeTimeout);

        var completed = await Task.WhenAny(transcodeTask, timeoutTask).ConfigureAwait(false);
        if (completed != transcodeTask)
        {
            try { op.Cancel(); } catch { }
            try { await transcodeTask.ConfigureAwait(false); } catch { }
            throw new VideoImportException($"Importing the video timed out after {TranscodeTimeout}.");
        }

        await transcodeTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the bare session-layout <c>finalized.marker</c> declaring the current encoder
    /// version, so <see cref="RecordingMarker.NeedsVerticalFlip"/> returns <c>false</c> for the
    /// import and neither preview nor export flips it upside down.
    /// </summary>
    private static void WriteSessionMarker(string importFolder)
    {
        try
        {
            var markerPath = Path.Combine(importFolder, RecordingMarker.SessionMarkerName);
            File.WriteAllText(markerPath, RecordingMarker.BuildContent(VideoWriter.EncoderVersion));
        }
        catch (Exception ex)
        {
            // Unlike the recorder — which retains orientation-correct JPEGs and can treat the
            // marker as best effort — an import has no fallback copy, so a missing marker would
            // silently preview and export upside down. Surface it instead.
            Debug.WriteLine($"[VideoImportService] Could not write finalized marker: {ex.Message}");
            throw new VideoImportException(
                "The imported video could not be marked as correctly oriented.", ex);
        }
    }

    /// <summary>Scales the base bitrate by pixel count, mirroring the recorder and exporter.</summary>
    private static uint ComputeBitrate(int width, int height)
    {
        const double ReferencePixels = 1920.0 * 1080.0;
        double scale = Math.Max(1.0, width * (double)height / ReferencePixels);
        return (uint)(BaseBitrate1080p * scale);
    }

    private static async Task SafeDeleteAsync(StorageFile file)
    {
        try { await file.DeleteAsync(StorageDeleteOption.PermanentDelete); }
        catch (Exception ex) { Debug.WriteLine($"[VideoImportService] Could not delete '{file.Path}': {ex.Message}"); }
    }

    /// <summary>
    /// Removes any partials and the (now-incomplete) import folder on failure or cancel, so a
    /// half-written <c>video.mp4</c> can never be mistaken for a finished import.
    /// </summary>
    private static void CleanupImportFolder(string importFolder, params string[] partials)
    {
        foreach (var partial in partials)
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception ex) { Debug.WriteLine($"[VideoImportService] Could not delete partial '{partial}': {ex.Message}"); }
        }

        try
        {
            if (Directory.Exists(importFolder))
                Directory.Delete(importFolder, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoImportService] Could not remove import folder '{importFolder}': {ex.Message}");
        }
    }
}
