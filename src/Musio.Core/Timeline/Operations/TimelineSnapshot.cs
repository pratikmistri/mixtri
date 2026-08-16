namespace Musio.Core.Timeline;

/// <summary>
/// Captures a full-timeline undo snapshot (clips, zoom keyframes, speed segments,
/// duration, and trim-end) so operations that need to restore the whole legacy
/// clip-based timeline don't each hand-roll the same capture/restore pair.
/// Used by <see cref="CutOperation"/>, <see cref="ApplyClipSpeedOperation"/>, and
/// <see cref="RippleDeleteOperation"/>.
/// </summary>
internal sealed class TimelineSnapshot
{
    private readonly List<TimelineClip> _clips;
    private readonly List<ZoomKeyframe> _keyframes;
    private readonly List<SpeedSegment> _speedSegments;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _trimEnd;
    private readonly TimeSpan? _playhead;

    private TimelineSnapshot(
        List<TimelineClip> clips,
        List<ZoomKeyframe> keyframes,
        List<SpeedSegment> speedSegments,
        TimeSpan duration,
        TimeSpan trimEnd,
        TimeSpan? playhead)
    {
        _clips = clips;
        _keyframes = keyframes;
        _speedSegments = speedSegments;
        _duration = duration;
        _trimEnd = trimEnd;
        _playhead = playhead;
    }

    /// <summary>
    /// Captures the current state of <paramref name="model"/>. Snapshots are shallow list
    /// copies (same element references) — the collections are never deep-cloned.
    /// </summary>
    /// <param name="includePlayhead">
    /// True to also capture (and later restore) <see cref="TimelineModel.PlayheadPosition"/>.
    /// This is opt-in because only some operations reposition the playhead as part of their
    /// edit (e.g. <see cref="ApplyClipSpeedOperation"/>, <see cref="RippleDeleteOperation"/>);
    /// others (e.g. <see cref="CutOperation"/>) leave it untouched, so restoring it there would
    /// be a behavior change.
    /// </param>
    public static TimelineSnapshot Capture(TimelineModel model, bool includePlayhead = false) => new(
        [.. model.Clips],
        [.. model.ZoomKeyframes],
        [.. model.SpeedSegments],
        model.Duration,
        model.TrimEnd,
        includePlayhead ? model.PlayheadPosition : null);

    /// <summary>Restores <paramref name="model"/> to the state captured by <see cref="Capture"/>.</summary>
    public void Restore(TimelineModel model)
    {
        model.Clips.Clear();
        model.Clips.AddRange(_clips);
        model.ZoomKeyframes.Clear();
        model.ZoomKeyframes.AddRange(_keyframes);
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(_speedSegments);
        model.Duration = _duration;
        model.TrimEnd = _trimEnd;
        if (_playhead is { } playhead)
            model.PlayheadPosition = playhead;
    }
}

/// <summary>
/// Lighter-weight undo snapshot of just <see cref="TimelineModel.Segments"/>, for operations
/// that reorder/split/insert on the primary output track and restore by replacing the whole
/// list rather than tracking individual index deltas. Restoring always re-flows positions via
/// <see cref="TimelineModel.RecalculateSegmentPositions"/>, matching what every call site did
/// before this type existed.
/// </summary>
/// <remarks>
/// The snapshot is shallow — it holds the same segment instances the model does — so the list
/// order alone is not enough to restore state. <see cref="TimelineModel.RecalculateSegmentPositions"/>
/// re-derives <see cref="TimelineSegment.Start"/> for the base track only; an overlay segment's
/// <see cref="TimelineSegment.Start"/> and its <see cref="TimelineSegment.TrackIndex"/> are
/// authored values that nothing would put back, so an operation that moved a clip between tracks
/// or along an overlay track would survive its own undo. Both are therefore captured per segment
/// and reapplied before the re-flow.
/// </remarks>
internal sealed class SegmentListSnapshot
{
    private readonly List<TimelineSegment> _segments;
    private readonly TimeSpan[] _starts;
    private readonly int[] _trackIndices;

    private SegmentListSnapshot(List<TimelineSegment> segments)
    {
        _segments = segments;
        _starts = new TimeSpan[segments.Count];
        _trackIndices = new int[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            _starts[i] = segments[i].Start;
            _trackIndices[i] = segments[i].TrackIndex;
        }
    }

    /// <summary>Captures a shallow copy (same element references) of <paramref name="model"/>'s segments.</summary>
    public static SegmentListSnapshot Capture(TimelineModel model) => new([.. model.Segments]);

    /// <summary>Replaces <paramref name="model"/>'s segments with the captured list and recalculates positions.</summary>
    public void Restore(TimelineModel model)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            _segments[i].Start = _starts[i];
            _segments[i].TrackIndex = _trackIndices[i];
        }

        model.Segments.Clear();
        model.Segments.AddRange(_segments);
        model.RecalculateSegmentPositions();
    }
}
