using Musio.Core.Models;

namespace Musio.Core.Timeline;

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
    /// Path of the primary recording's video file. Used to identify which
    /// video segments carry the cursor/zoom data so source↔output time mapping
    /// only considers the primary recording's segments.
    /// </summary>
    public string? PrimaryVideoFilePath { get; set; }

    /// <summary>
    /// Total output duration computed from segments.
    /// Falls back to <see cref="EffectiveDuration"/> when no segments exist.
    /// </summary>
    public TimeSpan TotalSegmentsDuration =>
        Segments.Count > 0
            ? Segments.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration)
            : EffectiveDuration;

    // Trim handles
    public TimeSpan TrimStart { get; set; }
    public TimeSpan TrimEnd { get; set; } // default = Duration

    // Cursor recording data for cursor-path visualization track
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

    // Audio waveform peak samples (normalized 0..1) for waveform rendering
    public float[]? SystemAudioWaveformSamples { get; set; }
    public float[]? MicAudioWaveformSamples { get; set; }

    // Audio track mute state
    public bool IsSystemAudioMuted { get; set; }
    public bool IsMicAudioMuted { get; set; }

    // Get the effective (trimmed) duration
    public TimeSpan EffectiveDuration => TrimEnd - TrimStart;

    /// <summary>
    /// The total display duration for the timeline ruler and coordinate system.
    /// Uses segment duration when segments exist, otherwise falls back to source Duration.
    /// </summary>
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
    /// ordered by their position in the source video.
    /// </summary>
    private IEnumerable<VideoSegment> PrimaryVideoSegments =>
        Segments.OfType<VideoSegment>()
            .Where(v => PrimaryVideoFilePath is null || v.VideoFilePath == PrimaryVideoFilePath)
            .OrderBy(v => v.SourceStart);

    /// <summary>
    /// Maps a source-video time (e.g. a zoom keyframe or cursor timestamp) to its
    /// position on the output timeline, accounting for inserted text slides that
    /// shift later video content to the right. Identity when no segments exist.
    /// </summary>
    public TimeSpan SourceToOutputTime(TimeSpan sourceTime)
    {
        if (Segments.Count == 0) return sourceTime;

        VideoSegment? last = null;
        foreach (var v in PrimaryVideoSegments)
        {
            var srcStart = v.SourceStart;
            var srcEnd = v.SourceStart + v.SourceDuration;

            if (sourceTime < srcStart)
                return v.Start; // before this segment's source range

            if (sourceTime < srcEnd)
            {
                var localSrc = sourceTime - srcStart;
                var localOut = v.SpeedFactor != 0
                    ? TimeSpan.FromTicks((long)(localSrc.Ticks / v.SpeedFactor))
                    : localSrc;
                return v.Start + localOut;
            }
            last = v;
        }

        // Past the end of all primary video segments
        return last?.End ?? sourceTime;
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
                    (PrimaryVideoFilePath is null || v.VideoFilePath == PrimaryVideoFilePath))
                {
                    var localOut = outputTime - segment.Start;
                    return v.SourceStart + TimeSpan.FromTicks((long)(localOut.Ticks * v.SpeedFactor));
                }
                return null; // text slide or non-primary segment
            }
        }
        return null;
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
    public TimeSpan EffectiveSourceStart => SourceStart ?? Start;

    /// <summary>Source duration (how much source content this clip represents).</summary>
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
    public TimeSpan PreDuration { get; init; } = TimeSpan.FromMilliseconds(345);   // 300ms + 15%
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMilliseconds(575);  // 500ms + 15%
    public TimeSpan PostDuration { get; init; } = TimeSpan.FromMilliseconds(575);  // 500ms + 15%

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

    /// <summary>When the zoom-in animation begins.</summary>
    public TimeSpan Start => Timestamp - PreDuration;

    /// <summary>When the zoom-out animation completes.</summary>
    public TimeSpan End => Timestamp + HoldDuration + PostDuration;

    /// <summary>Total segment duration from Start to End.</summary>
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

        var pre = TimeSpan.FromMilliseconds(Math.Min(345, total.TotalMilliseconds * 0.2));
        var post = TimeSpan.FromMilliseconds(Math.Min(575, total.TotalMilliseconds * 0.3));
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
