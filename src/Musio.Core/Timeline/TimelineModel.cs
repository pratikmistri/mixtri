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
    /// Time offset in seconds between mouse recording start and video frame 0.
    /// Used to align cursor-path and click visualizations with the video track.
    /// </summary>
    public double MouseToVideoOffsetSeconds { get; set; }

    // Audio waveform peak samples (normalized 0..1) for waveform rendering
    public float[]? AudioWaveformSamples { get; set; }

    // Get the effective (trimmed) duration
    public TimeSpan EffectiveDuration => TrimEnd - TrimStart;
}

public record TimelineClip(TimeSpan Start, TimeSpan End, string Label);

public record ZoomKeyframe
{
    public TimeSpan Timestamp { get; init; }
    public double ZoomLevel { get; init; } = 2.0;
    public double CenterX { get; init; } // normalized 0-1
    public double CenterY { get; init; } // normalized 0-1
    public TimeSpan PreDuration { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PostDuration { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// True for keyframes added by the user via the editor UI.
    /// False for keyframes auto-generated from click events (visualization only).
    /// </summary>
    public bool IsManual { get; init; }
}

public record SpeedSegment(TimeSpan Start, TimeSpan End, double Speed);
