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
    public TimeSpan SourceDuration => TimeSpan.FromTicks((long)((End - Start).Ticks * SpeedFactor));
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
