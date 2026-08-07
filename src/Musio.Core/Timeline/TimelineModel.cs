using System.Text.Json.Serialization;
using Musio.Core.Models;

namespace Musio.Core.Timeline;

/// <summary>
/// The editable state of a project's timeline. Serialized verbatim into a
/// <c>.musio</c> package, so its collection properties are populated in place on
/// deserialization rather than replaced.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class TimelineModel
{
    public TimeSpan Duration { get; set; }
    public int Fps { get; set; } = 30;
    public TimeSpan PlayheadPosition { get; set; }
    public double ZoomLevel { get; set; } = 1.0; // 1.0 = fit to width
    public double ScrollOffset { get; set; } // horizontal scroll in seconds

    public List<TimelineClip> Clips { get; } = [];
    public List<ZoomKeyframe> ZoomKeyframes { get; } = [];
    public List<SpeedSegment> SpeedSegments { get; } = [];

    /// <summary>
    /// Ordered list of segments on the output timeline.
    /// Each segment is either a <see cref="VideoSegment"/> or a <see cref="TextSlideSegment"/>.
    /// When empty, the legacy <see cref="Clips"/> / trim-based pipeline is used.
    /// </summary>
    public List<TimelineSegment> Segments { get; } = [];

    /// <summary>
    /// Camera (webcam) overlay segments on the independent camera track. Each
    /// segment's <see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/>
    /// are interpreted as a <b>source-video</b> time range, so they stay aligned with
    /// the recording (and survive video reorder/trim) via the source↔output mapping.
    /// When empty, the legacy single global webcam overlay behaviour is used.
    /// </summary>
    public List<CameraSegment> CameraSegments { get; } = [];

    /// <summary>
    /// Animated text overlays drawn on top of the video on the independent text track.
    /// Each segment's <see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/>
    /// are interpreted as a <b>source-video</b> time range (same convention as
    /// <see cref="CameraSegments"/>), so they stay aligned with the recording (and survive
    /// video reorder/trim) via the source↔output mapping. When empty, no overlays are drawn.
    /// </summary>
    public List<TextOverlaySegment> TextOverlays { get; } = [];

    /// <summary>
    /// Inserted voice-over and music tracks (see <see cref="AudioTrack"/>) on the two
    /// independent audio lanes.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CameraSegments"/> and <see cref="TextOverlays"/>, whose ranges are
    /// SOURCE-video times, an inserted track's <see cref="AudioTrack.StartTime"/> is an
    /// OUTPUT-timeline time. That difference is the feature: a camera overlay decorates the
    /// footage and must follow it through trims and reorders, where a voice-over is pinned
    /// to a moment in the finished video and must NOT move when the footage under it changes.
    /// </remarks>
    public List<AudioTrack> AudioTracks { get; } = [];

    /// <summary>
    /// Returns every enabled text overlay whose source range contains <paramref name="sourceTime"/>
    /// and which belongs to <paramref name="videoFilePath"/>'s source-time space. Overlays are
    /// returned in timeline order so stacking is deterministic.
    /// </summary>
    public List<TextOverlaySegment> GetActiveTextOverlays(TimeSpan sourceTime, string? videoFilePath)
    {
        var result = new List<TextOverlaySegment>();
        foreach (var overlay in TextOverlays)
        {
            if (!overlay.Enabled) continue;
            if (sourceTime < overlay.Start || sourceTime >= overlay.End) continue;

            bool belongsToSource = overlay.SourceVideoFilePath is null
                ? PrimaryVideoFilePath is null
                  || string.Equals(videoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(videoFilePath, overlay.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);

            if (belongsToSource)
                result.Add(overlay);
        }
        return result;
    }

    /// <summary>
    /// Computes normalized progress (0..1) through <paramref name="overlay"/> at
    /// <paramref name="sourceTime"/>, used to drive its entrance/exit animation.
    /// Returns 0 for a zero-length overlay.
    /// </summary>
    public static double GetTextOverlayProgress(TextOverlaySegment overlay, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        double dur = overlay.Duration.TotalSeconds;
        if (dur <= 0) return 0.0;

        double t = (sourceTime - overlay.Start).TotalSeconds / dur;
        return Math.Clamp(t, 0.0, 1.0);
    }

    /// <summary>
    /// Returns the enabled camera segment whose source range contains
    /// <paramref name="sourceTime"/>, or null when none is active.
    /// </summary>
    public CameraSegment? GetCameraSegmentAtSourceTime(TimeSpan sourceTime)
    {
        foreach (var seg in CameraSegments)
        {
            if (seg.Enabled && sourceTime >= seg.Start && sourceTime < seg.End)
                return seg;
        }
        return null;
    }

    /// <summary>
    /// Computes the camera overlay opacity for the <paramref name="active"/> segment at
    /// <paramref name="sourceTime"/>, suppressing the start/end fade where it would cause
    /// a visible artifact: no fade-in when another enabled camera segment covers the
    /// instant just before this one (adjacent/overlapping → avoids a flash), no fade-out
    /// when one continues right after, and no fade-in when the camera starts fullscreen
    /// (→ avoids a fade from black).
    /// </summary>
    public float GetCameraOverlayOpacity(CameraSegment active, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(active);

        bool hasPredecessor = CameraSegments.Any(s => !ReferenceEquals(s, active) && s.Enabled
            && s.Start < active.Start && s.End >= active.Start);
        bool hasSuccessor = CameraSegments.Any(s => !ReferenceEquals(s, active) && s.Enabled
            && s.End > active.End && s.Start <= active.End);

        bool fadeIn = !hasPredecessor && !active.StartsFullscreen;
        bool fadeOut = !hasSuccessor;
        return active.ComputeAppearOpacity(sourceTime, fadeIn, fadeOut);
    }

    /// <summary>
    /// Path of the primary recording's video file. Used to identify which
    /// video segments carry the cursor/zoom data so source↔output time mapping
    /// only considers the primary recording's segments.
    /// </summary>
    public string? PrimaryVideoFilePath { get; set; }

    /// <summary>
    /// Total output duration computed from segments.
    /// Falls back to <see cref="EffectiveDuration"/> when no segments exist.
    /// </summary>
    [JsonIgnore]
    public TimeSpan TotalSegmentsDuration =>
        Segments.Count > 0
            ? Segments.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration)
            : EffectiveDuration;

    // Trim handles
    public TimeSpan TrimStart { get; set; }
    public TimeSpan TrimEnd { get; set; } // default = Duration

    // Cursor recording data for cursor-path visualization track.
    // Reloaded from the recording's .mcur file, so it is never persisted.
    [JsonIgnore]
    public MouseRecordingData? CursorData { get; set; }

    /// <summary>
    /// Source click ticks whose auto-zoom segments have been suppressed (deleted or
    /// converted to manual by the user). Persisted so the suppression survives
    /// undo/redo and is applied during both preview and export.
    /// </summary>
    public HashSet<long> SuppressedClickTicks { get; } = [];

    /// <summary>
    /// Time offset in seconds between mouse recording start and video frame 0.
    /// Used to align cursor-path and click visualizations with the video track.
    /// </summary>
    public double MouseToVideoOffsetSeconds { get; set; }

    // Audio waveform peak samples (normalized 0..1) for waveform rendering.
    // A regenerable render cache — recomputed from the WAV files on load rather than
    // bloating the project file with thousands of floats.
    [JsonIgnore]
    public float[]? SystemAudioWaveformSamples { get; set; }
    [JsonIgnore]
    public float[]? MicAudioWaveformSamples { get; set; }

    // Audio track mute state
    public bool IsSystemAudioMuted { get; set; }
    public bool IsMicAudioMuted { get; set; }

    // Get the effective (trimmed) duration
    [JsonIgnore]
    public TimeSpan EffectiveDuration => TrimEnd - TrimStart;

    /// <summary>
    /// The total display duration for the timeline ruler and coordinate system.
    /// Uses segment duration when segments exist, otherwise falls back to source Duration.
    /// </summary>
    [JsonIgnore]
    public TimeSpan DisplayDuration =>
        Segments.Count > 0 ? TotalSegmentsDuration : Duration;

    /// <summary>
    /// Recalculates <see cref="TimelineSegment.Start"/> for every segment so they
    /// are contiguous (no gaps, no overlaps). Call after insert, delete, or reorder.
    /// </summary>
    public void RecalculateSegmentPositions()
    {
        var position = TimeSpan.Zero;
        foreach (var segment in Segments)
        {
            segment.Start = position;
            position += segment.Duration;
        }
    }

    /// <summary>
    /// Finds the segment and local offset for a given output time position.
    /// </summary>
    public (TimelineSegment? Segment, TimeSpan LocalOffset) GetSegmentAtTime(TimeSpan outputTime)
    {
        foreach (var segment in Segments)
        {
            if (outputTime >= segment.Start && outputTime < segment.End)
                return (segment, outputTime - segment.Start);
        }
        // If at the exact end, return the last segment
        if (Segments.Count > 0 && outputTime >= Segments[^1].End)
            return (Segments[^1], Segments[^1].Duration);
        return (null, TimeSpan.Zero);
    }

    /// <summary>
    /// Primary recording's video segments (those carrying cursor/zoom data),
    /// in their actual order on the output timeline. The timeline order — not the
    /// recorded source order — is authoritative, so that reordering/moving video
    /// segments keeps the linked zoom, click, and cursor visualizations in sync.
    /// </summary>
    private IEnumerable<VideoSegment> PrimaryVideoSegments =>
        Segments.OfType<VideoSegment>()
            .Where(v => PrimaryVideoFilePath is null ||
                        string.Equals(v.VideoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a source-video time (e.g. a zoom keyframe or cursor timestamp) to its
    /// position on the output timeline, accounting for inserted text slides and
    /// reordered/trimmed video segments. Identity when no segments exist.
    /// Unlike <see cref="TrySourceToOutputTime"/>, this always returns a value,
    /// falling back to the nearest kept segment boundary when the source time has
    /// been trimmed out of the timeline.
    /// </summary>
    public TimeSpan SourceToOutputTime(TimeSpan sourceTime)
    {
        if (Segments.Count == 0) return sourceTime;
        if (TrySourceToOutputTime(sourceTime, out var output))
            return output;

        // Source time is not present in any kept segment (trimmed out). Map it to
        // the nearest kept primary segment boundary so visualizations clamp sanely.
        VideoSegment? nearest = null;
        double nearestDist = double.MaxValue;
        bool nearestBefore = false;
        foreach (var v in PrimaryVideoSegments)
        {
            var srcStart = v.SourceStart;
            var srcEnd = v.SourceStart + v.SourceDuration;
            bool before = sourceTime < srcStart;
            double dist = before
                ? (srcStart - sourceTime).TotalSeconds
                : (sourceTime - srcEnd).TotalSeconds;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = v;
                nearestBefore = before;
            }
        }

        if (nearest is null) return sourceTime;
        return nearestBefore ? nearest.Start : nearest.End;
    }

    /// <summary>
    /// Attempts to map a source-video time to its position on the output timeline,
    /// walking segments in their actual timeline order. Returns <c>false</c> when
    /// the source time falls outside every kept primary video segment (i.e. it was
    /// trimmed out and has no corresponding output position). Callers that draw
    /// source-time visualizations (zoom/click/cursor) should skip rendering when
    /// this returns <c>false</c>.
    /// </summary>
    public bool TrySourceToOutputTime(TimeSpan sourceTime, out TimeSpan outputTime)
    {
        if (Segments.Count == 0)
        {
            outputTime = sourceTime;
            return true;
        }

        foreach (var v in PrimaryVideoSegments)
        {
            var srcStart = v.SourceStart;
            var srcEnd = v.SourceStart + v.SourceDuration;

            if (sourceTime >= srcStart && sourceTime <= srcEnd)
            {
                var localSrc = sourceTime - srcStart;
                var localOut = v.SpeedFactor != 0
                    ? TimeSpan.FromTicks((long)(localSrc.Ticks / v.SpeedFactor))
                    : localSrc;
                outputTime = v.Start + localOut;
                return true;
            }
        }

        outputTime = sourceTime;
        return false;
    }

    /// <summary>
    /// Inverse of <see cref="SourceToOutputTime"/>: maps an output-timeline time
    /// to the corresponding source-video time. Returns null when the output time
    /// falls on a text slide (no underlying source frame).
    /// </summary>
    public TimeSpan? OutputToSourceTime(TimeSpan outputTime)
    {
        if (Segments.Count == 0) return outputTime;

        foreach (var segment in Segments)
        {
            if (outputTime >= segment.Start && outputTime < segment.End)
            {
                if (segment is VideoSegment v &&
                    (PrimaryVideoFilePath is null ||
                     string.Equals(v.VideoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var localOut = outputTime - segment.Start;
                    return v.SourceStart + TimeSpan.FromTicks((long)(localOut.Ticks * v.SpeedFactor));
                }
                return null; // text slide or non-primary segment
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the video segment among <paramref name="candidates"/> whose start or end
    /// (on the output timeline) is closest to <paramref name="outputTime"/>, and reports
    /// whether the nearer edge is the segment's start. Used by timeline-gesture code
    /// (camera/zoom create, move, resize) to clamp a pointer gesture onto a valid
    /// boundary when the output time under the pointer is unmappable for the relevant
    /// source/track (e.g. it lands on a text slide, or a video segment for a different
    /// recording) — instead of falling back to raw output time, which would mix output
    /// time into a source-time domain. Returns <c>(null, false)</c> when
    /// <paramref name="candidates"/> is empty.
    /// </summary>
    public static (VideoSegment? Segment, bool AtStart) NearestVideoSegmentEdge(
        IEnumerable<VideoSegment> candidates, TimeSpan outputTime)
    {
        VideoSegment? nearest = null;
        bool atStart = false;
        double bestDist = double.MaxValue;
        foreach (var seg in candidates)
        {
            double distToStart = Math.Abs((outputTime - seg.Start).TotalSeconds);
            if (distToStart < bestDist)
            {
                bestDist = distToStart;
                nearest = seg;
                atStart = true;
            }
            double distToEnd = Math.Abs((outputTime - seg.End).TotalSeconds);
            if (distToEnd < bestDist)
            {
                bestDist = distToEnd;
                nearest = seg;
                atStart = false;
            }
        }
        return (nearest, atStart);
    }
}

public record TimelineClip(TimeSpan Start, TimeSpan End, string Label)
{
    /// <summary>Speed factor applied to this clip. 1.0 = normal, 2.0 = 2x fast, 0.5 = half speed.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>
    /// Original source time position for the first frame of this clip.
    /// Used by the mapper when speed changes have shifted clip boundaries.
    /// When null, defaults to Start (no speed adjustment has occurred).
    /// </summary>
    public TimeSpan? SourceStart { get; init; }

    /// <summary>The effective source start position.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveSourceStart => SourceStart ?? Start;

    /// <summary>Source duration (how much source content this clip represents).</summary>
    [JsonIgnore]
    public TimeSpan SourceDuration
    {
        get
        {
            double ticks = (End - Start).Ticks * SpeedFactor;
            if (double.IsNaN(ticks) || double.IsInfinity(ticks))
                return End - Start;
            ticks = Math.Clamp(ticks, long.MinValue, long.MaxValue);
            return TimeSpan.FromTicks((long)ticks);
        }
    }
}

public record ZoomKeyframe
{
    /// <summary>Minimum total segment duration to prevent degenerate segments.</summary>
    public static readonly TimeSpan MinSegmentDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>Stable identity for selection and undo/redo tracking.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TimeSpan Timestamp { get; init; }
    public double ZoomLevel { get; init; } = 2.0;
    public double CenterX { get; init; } // normalized 0-1
    public double CenterY { get; init; } // normalized 0-1
    public TimeSpan PreDuration { get; init; } = TimeSpan.FromMilliseconds(1000);  // graceful anticipatory zoom-in
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMilliseconds(1333); // settled dwell on the focal point
    public TimeSpan PostDuration { get; init; } = TimeSpan.FromMilliseconds(1556); // slow, elegant release back to full frame

    /// <summary>
    /// True for keyframes added by the user via the editor UI.
    /// False for keyframes auto-generated from click events (visualization only).
    /// </summary>
    public bool IsManual { get; init; }

    /// <summary>
    /// The raw <see cref="ClickEvent.TimestampTicks"/> of the source click that
    /// generated this auto-zoom keyframe. Null for manually-added keyframes.
    /// Used as a stable identity to suppress the corresponding auto-zoom segment
    /// when the user deletes or edits this keyframe.
    /// </summary>
    public long? SourceClickTicks { get; init; }

    /// <summary>
    /// Source video file this keyframe belongs to. Null means the primary recording.
    /// Used to map the keyframe's source time into the correct (primary or appended)
    /// video segment's output range, so zoom segments show on the right clip and are
    /// available for appended recordings.
    /// </summary>
    public string? SourceVideoFilePath { get; init; }

    /// <summary>When the zoom-in animation begins.</summary>
    [JsonIgnore]
    public TimeSpan Start => Timestamp - PreDuration;

    /// <summary>When the zoom-out animation completes.</summary>
    [JsonIgnore]
    public TimeSpan End => Timestamp + HoldDuration + PostDuration;

    /// <summary>Total segment duration from Start to End.</summary>
    [JsonIgnore]
    public TimeSpan TotalDuration => PreDuration + HoldDuration + PostDuration;

    /// <summary>
    /// Creates a ZoomKeyframe from a segment start/end range, using default ease durations.
    /// </summary>
    public static ZoomKeyframe FromRange(TimeSpan start, TimeSpan end, double zoomLevel,
        double centerX = 0.5, double centerY = 0.5)
    {
        var total = end - start;
        if (total < MinSegmentDuration)
            total = MinSegmentDuration;

        var pre = TimeSpan.FromMilliseconds(Math.Min(1000, total.TotalMilliseconds * 0.2));
        var post = TimeSpan.FromMilliseconds(Math.Min(1556, total.TotalMilliseconds * 0.3));
        var hold = total - pre - post;
        if (hold < TimeSpan.Zero) hold = TimeSpan.Zero;

        return new ZoomKeyframe
        {
            Timestamp = start + pre,
            ZoomLevel = zoomLevel,
            CenterX = Math.Clamp(centerX, 0, 1),
            CenterY = Math.Clamp(centerY, 0, 1),
            PreDuration = pre,
            HoldDuration = hold,
            PostDuration = post,
            IsManual = true,
        };
    }
}

public record SpeedSegment(TimeSpan Start, TimeSpan End, double Speed);
