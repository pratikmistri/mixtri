using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Core.Export;

/// <summary>Which kind of media a placement reads its audio from.</summary>
public enum AudioSourceKind
{
    /// <summary>The audio track embedded in a recorded video file.</summary>
    EmbeddedVideoTrack,

    /// <summary>A separately recorded audio file (system audio or microphone).</summary>
    AudioFile,
}

/// <summary>
/// One audio source positioned on the exported timeline.
/// </summary>
/// <param name="SourcePath">Video file (embedded track) or audio file to read from.</param>
/// <param name="Kind">How <paramref name="SourcePath"/> should be opened.</param>
/// <param name="TrimFromStart">Where playback starts inside the source file.</param>
/// <param name="TakeDuration">
/// How much of the source to play; <c>null</c> means "to the end of the file".
/// </param>
/// <param name="Delay">Where the trimmed audio starts on the output timeline.</param>
/// <param name="PlaysAtNativeRateOnSpeedAdjustedSegment">
/// <c>true</c> when the owning segment is speed-adjusted. Neither
/// <c>MediaComposition</c> nor <c>BackgroundAudioTrack</c> exposes a playback-rate or
/// time-scale API, so this audio is muxed at its native rate: it starts in sync with the
/// segment but drifts as the segment plays, and is cut at the segment boundary (see
/// <see cref="ExportAudioPlan"/>). Exposed so callers can report the limitation instead
/// of implying the export is fully synchronized.
/// </param>
public readonly record struct AudioPlacement(
    string SourcePath,
    AudioSourceKind Kind,
    TimeSpan TrimFromStart,
    TimeSpan? TakeDuration,
    TimeSpan Delay,
    bool PlaysAtNativeRateOnSpeedAdjustedSegment = false);

/// <summary>
/// Pure mapping from a project + timeline to the set of audio tracks the exporter must
/// mux, and where each one belongs on the output timeline.
///
/// <para>Whenever the timeline has video segments (every project edited in the segment
/// editor does), audio follows the segments: each segment contributes its own source
/// range, delayed to that segment's output position. This is what makes trims, splits,
/// deletes, reorders, inserted text slides, and appended recordings produce matching
/// audio — muxing the untouched source file would replay deleted or reordered content.
/// Projects with no segments keep the legacy trim-based placement.</para>
///
/// <para><b>Known limitation — speed-adjusted segments.</b> The mux pass is built on
/// <c>MediaComposition</c>/<c>BackgroundAudioTrack</c>, which expose trimming, delay,
/// volume and effects but <b>no playback-rate/time-scale API</b>; audio can therefore not
/// be re-timed to match a segment's <see cref="VideoSegment.SpeedFactor"/> (doing so would
/// require decoding, time-stretching and re-encoding every affected track offline). Such a
/// segment's audio is muxed at its native rate: it starts in sync with the segment, drifts
/// as the segment plays, and is cut at the segment boundary so it can never overlap the
/// next segment. Placements affected by this are flagged with
/// <see cref="AudioPlacement.PlaysAtNativeRateOnSpeedAdjustedSegment"/> so the exporter can
/// report the limitation rather than imply full A/V sync.</para>
/// </summary>
public static class ExportAudioPlan
{
    /// <summary>Speed factors within this distance of 1.0 are treated as unmodified.</summary>
    private const double SpeedEpsilon = 0.001;

    /// <summary>
    /// Builds the ordered audio placements for an export. Nothing here touches the file
    /// system: callers resolve which placements actually have media (see
    /// <see cref="VideoEncoder"/>) before muxing.
    /// </summary>
    public static IReadOnlyList<AudioPlacement> Build(
        Project project, TimelineModel? timeline, TimelineMapper? mapper)
    {
        ArgumentNullException.ThrowIfNull(project);

        var videoSegments = timeline?.Segments
            .OfType<VideoSegment>()
            .OrderBy(s => s.Start)
            .ToList();

        return videoSegments is { Count: > 0 }
            ? BuildFromSegments(project, timeline!, videoSegments)
            : BuildLegacy(project, mapper);
    }

    private static List<AudioPlacement> BuildFromSegments(
        Project project, TimelineModel timeline, List<VideoSegment> segments)
    {
        var placements = new List<AudioPlacement>();

        foreach (var segment in segments)
        {
            bool isPrimary = IsPrimarySource(segment.VideoFilePath, project, timeline);

            // Audio embedded in the recording is inherently aligned with its own video
            // frames, so it needs no extra offset.
            var embedded = BuildPlacement(
                segment.VideoFilePath, AudioSourceKind.EmbeddedVideoTrack, segment, offsetSeconds: 0);
            if (embedded is { } embeddedPlacement)
                placements.Add(embeddedPlacement);

            // Separately recorded audio: the primary recording's tracks live on the
            // project (the export view model filters muted ones there), appended
            // recordings carry their own.
            var audioPaths = isPrimary ? project.AudioFilePaths : segment.AudioFilePaths;
            if (audioPaths is null) continue;

            double offsetSeconds = isPrimary
                ? project.AudioToVideoOffsetSeconds
                : segment.AudioToVideoOffsetSeconds;

            foreach (var path in audioPaths)
            {
                var placement = BuildPlacement(path, AudioSourceKind.AudioFile, segment, offsetSeconds);
                if (placement is { } audioPlacement)
                    placements.Add(audioPlacement);
            }
        }

        return placements;
    }

    /// <summary>
    /// Places one source's audio for a single video segment.
    ///
    /// <para>The segment covers source-video times <c>[SourceStart, SourceStart +
    /// SourceDuration)</c>. For a separately recorded audio file, the position inside that
    /// file for source-video time <c>T</c> is <c>T + offsetSeconds</c>, so the aligned
    /// audio interval is <c>[SourceStart + offset, SourceStart + SourceDuration + offset)</c>
    /// — exactly as long as the segment's source range, never longer. That interval is then
    /// clipped to the file's own domain (it cannot start before 0), and the clipped head is
    /// converted back into an output delay through the segment's speed factor.</para>
    ///
    /// Returns <c>null</c> when the segment has no audible range (degenerate segment, or
    /// audio that only starts after the segment ends).
    /// </summary>
    private static AudioPlacement? BuildPlacement(
        string sourcePath, AudioSourceKind kind, VideoSegment segment, double offsetSeconds)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (segment.SourceDuration <= TimeSpan.Zero || segment.Duration <= TimeSpan.Zero) return null;

        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        bool speedAdjusted = Math.Abs(speed - 1.0) > SpeedEpsilon;

        var offset = TimeSpan.FromSeconds(offsetSeconds);
        var videoStart = segment.SourceStart;
        var videoEnd = segment.SourceStart + segment.SourceDuration;

        // The aligned interval inside the audio source, clipped to the file's domain.
        var audioStart = videoStart + offset;
        var audioEnd = videoEnd + offset;
        if (audioStart < TimeSpan.Zero) audioStart = TimeSpan.Zero;
        if (audioEnd <= audioStart) return null;

        // Source-video time at which audio becomes available, and the matching lead on
        // the output timeline (scaled by the segment's speed).
        var firstAudibleVideoTime = audioStart - offset;
        var sourceLead = firstAudibleVideoTime - videoStart;
        var outputLead = sourceLead > TimeSpan.Zero
            ? TimeSpan.FromTicks((long)(sourceLead.Ticks / speed))
            : TimeSpan.Zero;
        if (outputLead >= segment.Duration) return null;

        var available = audioEnd - audioStart;
        var outputRoom = segment.Duration - outputLead;

        // Audio is muxed at its native rate (no time-scale API exists), so a sped-up
        // segment consumes source audio faster than the output advances. Capping the take
        // at the remaining output room guarantees it can never bleed into the following
        // segment; the tail is dropped instead.
        var take = available < outputRoom ? available : outputRoom;
        if (take <= TimeSpan.Zero) return null;

        return new AudioPlacement(
            sourcePath, kind, audioStart, take, segment.Start + outputLead, speedAdjusted);
    }

    /// <summary>
    /// Legacy (no segments) placement: the whole recording trimmed to the timeline's
    /// trim range and played from the start of the output.
    /// </summary>
    private static List<AudioPlacement> BuildLegacy(Project project, TimelineMapper? mapper)
    {
        var trimStart = TimeSpan.Zero;
        TimeSpan? take = null;

        if (mapper is not null)
        {
            trimStart = mapper.TrimStart;
            var span = mapper.TrimEnd - mapper.TrimStart;
            if (span > TimeSpan.Zero) take = span;
        }

        var placements = new List<AudioPlacement>();

        if (!string.IsNullOrWhiteSpace(project.VideoFilePath))
        {
            placements.Add(new AudioPlacement(
                project.VideoFilePath, AudioSourceKind.EmbeddedVideoTrack, trimStart, take, TimeSpan.Zero));
        }

        if (project.AudioFilePaths is not null)
        {
            foreach (var path in project.AudioFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                placements.Add(new AudioPlacement(
                    path, AudioSourceKind.AudioFile, trimStart, take, TimeSpan.Zero));
            }
        }

        return placements;
    }

    private static bool IsPrimarySource(string videoFilePath, Project project, TimelineModel timeline)
        => string.Equals(videoFilePath, project.VideoFilePath, StringComparison.OrdinalIgnoreCase)
        || (timeline.PrimaryVideoFilePath is { } primary
            && string.Equals(videoFilePath, primary, StringComparison.OrdinalIgnoreCase));
}
