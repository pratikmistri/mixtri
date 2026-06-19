using Musio.Core.Timeline;

namespace Musio.Core.Export;

/// <summary>
/// Identifies which segment and source time a given output frame maps to.
/// </summary>
public readonly struct SegmentFrameRef
{
    /// <summary>The segment this frame belongs to.</summary>
    public TimelineSegment Segment { get; init; }

    /// <summary>For video segments: the source time in seconds within that video file.</summary>
    public double SourceTimeSeconds { get; init; }

    /// <summary>Normalized progress within the segment (0..1), useful for text slide animation.</summary>
    public double Progress { get; init; }

    /// <summary>True if this frame falls within a transition overlap period.</summary>
    public bool IsInTransition { get; init; }

    /// <summary>The previous segment (for crossfade transitions).</summary>
    public TimelineSegment? OutgoingSegment { get; init; }

    /// <summary>Progress of the outgoing segment source time during transition.</summary>
    public double OutgoingSourceTimeSeconds { get; init; }

    /// <summary>Transition progress (0..1) from outgoing to incoming.</summary>
    public double TransitionProgress { get; init; }
}

/// <summary>
/// Maps output frame indices to source timestamps, accounting for timeline edits
/// (trim, speed changes, and cut/deleted segments).
/// </summary>
public class TimelineMapper
{
    private readonly TimelineModel _timeline;
    private readonly int _fps;
    private readonly List<OutputSegment> _outputSegments;

    public int TotalOutputFrames { get; }

    /// <summary>Source-time position where the trim starts.</summary>
    public TimeSpan TrimStart => _timeline.TrimStart;

    /// <summary>Source-time position where the trim ends.</summary>
    public TimeSpan TrimEnd => _timeline.TrimEnd;

    /// <summary>Total output duration after all edits (trim, cuts, speed).</summary>
    public TimeSpan EffectiveDuration =>
        _timeline.Segments.Count > 0
            ? _timeline.TotalSegmentsDuration
            : TimeSpan.FromSeconds(_outputSegments.Sum(s => s.OutputDuration));

    public TimelineMapper(TimelineModel timeline, int fps)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        _timeline = timeline;
        _fps = fps;
        _outputSegments = BuildOutputSegments();
        TotalOutputFrames = ComputeTotalOutputFrames();
    }

    /// <summary>
    /// Maps an output frame index to a <see cref="SegmentFrameRef"/> when the
    /// timeline uses the segment-based pipeline.
    /// </summary>
    public SegmentFrameRef GetSegmentFrameRef(int outputFrame)
    {
        if (_timeline.Segments.Count == 0)
        {
            // Legacy fallback: wrap the old source-time result
            var sourceTime = GetSourceTimeForOutputFrame(outputFrame);
            return new SegmentFrameRef
            {
                Segment = null!,
                SourceTimeSeconds = sourceTime,
                Progress = 0,
            };
        }

        double outputTimeSec = (double)outputFrame / _fps;
        var outputTime = TimeSpan.FromSeconds(outputTimeSec);

        foreach (var segment in _timeline.Segments)
        {
            if (outputTime < segment.End || segment == _timeline.Segments[^1])
            {
                if (outputTime < segment.Start) break;

                var localOffset = outputTime - segment.Start;
                var progress = segment.Duration.TotalSeconds > 0
                    ? Math.Clamp(localOffset.TotalSeconds / segment.Duration.TotalSeconds, 0, 1)
                    : 0;

                // Check for transition overlap
                bool isInTransition = false;
                TimelineSegment? outgoing = null;
                double outgoingSource = 0;
                double transitionProgress = 0;

                if (segment.InTransition is { Type: not TransitionType.None } transition)
                {
                    var transitionDuration = transition.Duration;
                    if (localOffset < transitionDuration)
                    {
                        isInTransition = true;
                        transitionProgress = transitionDuration.TotalSeconds > 0
                            ? localOffset.TotalSeconds / transitionDuration.TotalSeconds
                            : 1.0;

                        int segIdx = _timeline.Segments.IndexOf(segment);
                        if (segIdx > 0)
                        {
                            outgoing = _timeline.Segments[segIdx - 1];
                            if (outgoing is VideoSegment outVid)
                            {
                                // The outgoing frame is at the tail of the previous segment
                                var outgoingLocal = outgoing.Duration - transitionDuration + localOffset;
                                outgoingSource = outVid.SourceStart.TotalSeconds
                                    + outgoingLocal.TotalSeconds * outVid.SpeedFactor;
                            }
                        }
                    }
                }

                double sourceTimeSec = 0;
                if (segment is VideoSegment vid)
                {
                    sourceTimeSec = vid.SourceStart.TotalSeconds
                        + localOffset.TotalSeconds * vid.SpeedFactor;
                }

                return new SegmentFrameRef
                {
                    Segment = segment,
                    SourceTimeSeconds = sourceTimeSec,
                    Progress = progress,
                    IsInTransition = isInTransition,
                    OutgoingSegment = outgoing,
                    OutgoingSourceTimeSeconds = outgoingSource,
                    TransitionProgress = transitionProgress,
                };
            }
        }

        // Past all segments — return last
        var last = _timeline.Segments[^1];
        return new SegmentFrameRef
        {
            Segment = last,
            SourceTimeSeconds = last is VideoSegment lv
                ? lv.SourceStart.TotalSeconds + lv.SourceDuration.TotalSeconds
                : 0,
            Progress = 1.0,
        };
    }

    /// <summary>
    /// Maps an output frame index to the corresponding source time in seconds.
    /// Returns the source time accounting for trim, speed changes, and cuts.
    /// Used by the legacy (non-segment) pipeline.
    /// </summary>
    public double GetSourceTimeForOutputFrame(int outputFrame)
    {
        if (outputFrame < 0) return _timeline.TrimStart.TotalSeconds;
        if (_outputSegments.Count == 0) return _timeline.TrimStart.TotalSeconds;

        double outputTimeSeconds = (double)outputFrame / _fps;

        double accumulatedOutputTime = 0;
        foreach (var segment in _outputSegments)
        {
            double segmentOutputDuration = segment.OutputDuration;
            if (outputTimeSeconds < accumulatedOutputTime + segmentOutputDuration)
            {
                // The frame falls within this segment
                double offsetInSegment = outputTimeSeconds - accumulatedOutputTime;
                // Convert output offset to source offset using the speed multiplier
                double sourceOffset = offsetInSegment * segment.Speed;
                return segment.SourceStart + sourceOffset;
            }
            accumulatedOutputTime += segmentOutputDuration;
        }

        // Past the end — return the last source time
        var lastSeg = _outputSegments[^1];
        return lastSeg.SourceStart + lastSeg.SourceDuration;
    }

    /// <summary>
    /// Checks if a given source time (in seconds) falls within a gap between kept clips.
    /// When clips exist, only content within clips is kept; everything else is deleted.
    /// When no clips exist, nothing is deleted (full video plays).
    /// </summary>
    public bool IsDeleted(double sourceTimeSeconds)
    {
        if (_timeline.Clips.Count == 0)
            return false;

        // Content is kept only if it falls within a clip's source range
        foreach (var clip in _timeline.Clips)
        {
            double sourceStart = clip.EffectiveSourceStart.TotalSeconds;
            double sourceEnd = sourceStart + clip.SourceDuration.TotalSeconds;
            if (sourceTimeSeconds >= sourceStart && sourceTimeSeconds < sourceEnd)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the output segments after trim/cut/speed edits.
    /// Each tuple contains (SourceStart, SourceEnd, Speed) in source time.
    /// </summary>
    public List<(TimeSpan Start, TimeSpan End, double Speed)> GetOutputSegments()
    {
        return _outputSegments
            .Select(s => (
                TimeSpan.FromSeconds(s.SourceStart),
                TimeSpan.FromSeconds(s.SourceStart + s.SourceDuration),
                s.Speed))
            .ToList();
    }

    /// <summary>
    /// Builds an ordered list of output segments by splitting the trimmed source range
    /// into regions, removing deleted clips, and applying speed multipliers.
    /// </summary>
    private List<OutputSegment> BuildOutputSegments()
    {
        // When clips exist, use them directly as kept segments
        if (_timeline.Clips.Count > 0)
            return BuildFromClips();

        return BuildFromTrimRange();
    }

    /// <summary>
    /// Builds output segments from explicit clips with their SpeedFactor.
    /// Each clip defines an output segment; gaps between clips are skipped.
    /// </summary>
    private List<OutputSegment> BuildFromClips()
    {
        var result = new List<OutputSegment>();
        foreach (var clip in _timeline.Clips.OrderBy(c => c.Start))
        {
            double outputDuration = (clip.End - clip.Start).TotalSeconds;
            if (outputDuration <= 0) continue;

            double sourceStart = clip.EffectiveSourceStart.TotalSeconds;
            double sourceDuration = outputDuration * clip.SpeedFactor;

            result.Add(new OutputSegment
            {
                SourceStart = sourceStart,
                SourceDuration = sourceDuration,
                Speed = clip.SpeedFactor,
                OutputDuration = outputDuration,
            });
        }
        return result;
    }

    /// <summary>
    /// Original approach: builds segments from trim range + speed segments (no clips).
    /// </summary>
    private List<OutputSegment> BuildFromTrimRange()
    {
        double trimStartSec = _timeline.TrimStart.TotalSeconds;
        double trimEndSec = _timeline.TrimEnd.TotalSeconds;

        if (trimEndSec <= trimStartSec)
            return [];

        // Collect all boundary points in the trimmed range
        var boundaries = new SortedSet<double> { trimStartSec, trimEndSec };

        // Add speed segment boundaries
        foreach (var seg in _timeline.SpeedSegments)
        {
            double ss = Math.Max(seg.Start.TotalSeconds, trimStartSec);
            double se = Math.Min(seg.End.TotalSeconds, trimEndSec);
            if (ss < se)
            {
                boundaries.Add(ss);
                boundaries.Add(se);
            }
        }

        var boundaryList = boundaries.ToList();
        var result = new List<OutputSegment>();

        for (int i = 0; i < boundaryList.Count - 1; i++)
        {
            double segStart = boundaryList[i];
            double segEnd = boundaryList[i + 1];
            double sourceDuration = segEnd - segStart;

            if (sourceDuration <= 0) continue;

            // Determine speed for this sub-range
            double speed = 1.0;
            foreach (var speedSeg in _timeline.SpeedSegments)
            {
                double ss = speedSeg.Start.TotalSeconds;
                double se = speedSeg.End.TotalSeconds;
                if (segStart >= ss && segEnd <= se)
                {
                    speed = speedSeg.Speed;
                    break;
                }
            }

            if (speed <= 0) speed = 1.0;

            result.Add(new OutputSegment
            {
                SourceStart = segStart,
                SourceDuration = sourceDuration,
                Speed = speed,
                OutputDuration = sourceDuration / speed,
            });
        }

        return result;
    }

    private int ComputeTotalOutputFrames()
    {
        // Segment-based timelines (with text slides) define their own total
        // duration that includes non-video segments.
        if (_timeline.Segments.Count > 0)
            return Math.Max(1, (int)(_timeline.TotalSegmentsDuration.TotalSeconds * _fps));

        double totalOutputSeconds = 0;
        foreach (var seg in _outputSegments)
            totalOutputSeconds += seg.OutputDuration;

        return Math.Max(1, (int)(totalOutputSeconds * _fps));
    }

    private struct OutputSegment
    {
        public double SourceStart;
        public double SourceDuration;
        public double Speed;
        public double OutputDuration;
    }
}
