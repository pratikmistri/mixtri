namespace Musio.Core.Timeline;

using Musio.Core.Processing;

public interface IEditOperation
{
    string Description { get; }
    void Execute(TimelineModel model);
    void Undo(TimelineModel model);
}

public class TrimOperation : IEditOperation
{
    private readonly TimeSpan? _newTrimStart;
    private readonly TimeSpan? _newTrimEnd;
    private TimeSpan _previousTrimStart;
    private TimeSpan _previousTrimEnd;

    public string Description => "Trim";

    public TrimOperation(TimeSpan? trimStart = null, TimeSpan? trimEnd = null)
    {
        _newTrimStart = trimStart;
        _newTrimEnd = trimEnd;
    }

    public void Execute(TimelineModel model)
    {
        _previousTrimStart = model.TrimStart;
        _previousTrimEnd = model.TrimEnd;

        if (_newTrimStart.HasValue)
            model.TrimStart = _newTrimStart.Value;
        if (_newTrimEnd.HasValue)
            model.TrimEnd = _newTrimEnd.Value;
    }

    public void Undo(TimelineModel model)
    {
        model.TrimStart = _previousTrimStart;
        model.TrimEnd = _previousTrimEnd;
    }
}

public class CutOperation : IEditOperation
{
    private readonly TimeSpan _cutStart;
    private readonly TimeSpan _cutEnd;

    private List<TimelineClip> _previousClips = [];
    private List<ZoomKeyframe> _previousKeyframes = [];
    private List<SpeedSegment> _previousSpeedSegments = [];
    private TimeSpan _previousDuration;
    private TimeSpan _previousTrimEnd;

    public string Description => "Cut";

    public CutOperation(TimeSpan cutStart, TimeSpan cutEnd)
    {
        if (cutEnd <= cutStart)
            throw new ArgumentException("Cut end must be after cut start.");
        _cutStart = cutStart;
        _cutEnd = cutEnd;
    }

    public void Execute(TimelineModel model)
    {
        // Snapshot for undo
        _previousClips = [.. model.Clips];
        _previousKeyframes = [.. model.ZoomKeyframes];
        _previousSpeedSegments = [.. model.SpeedSegments];
        _previousDuration = model.Duration;
        _previousTrimEnd = model.TrimEnd;

        var cutDuration = _cutEnd - _cutStart;
        var newClips = new List<TimelineClip>();

        foreach (var clip in model.Clips)
        {
            if (clip.End <= _cutStart)
            {
                // Entirely before cut — keep unchanged
                newClips.Add(clip);
            }
            else if (clip.Start >= _cutEnd)
            {
                // Entirely after cut — shift back
                newClips.Add(new TimelineClip(clip.Start - cutDuration, clip.End - cutDuration, clip.Label));
            }
            else if (clip.Start < _cutStart && clip.End > _cutEnd)
            {
                // Clip spans entire cut range — split into two, remove middle
                newClips.Add(new TimelineClip(clip.Start, _cutStart, clip.Label));
                newClips.Add(new TimelineClip(_cutStart, clip.End - cutDuration, clip.Label));
            }
            else if (clip.Start < _cutStart && clip.End <= _cutEnd)
            {
                // Overlaps start of cut — trim end
                newClips.Add(new TimelineClip(clip.Start, _cutStart, clip.Label));
            }
            else if (clip.Start >= _cutStart && clip.End > _cutEnd)
            {
                // Overlaps end of cut — trim start, shift back
                newClips.Add(new TimelineClip(_cutStart, clip.End - cutDuration, clip.Label));
            }
            // else: entirely within cut range — removed
        }

        model.Clips.Clear();
        model.Clips.AddRange(newClips);

        // Adjust zoom keyframes using full segment span (Start/End)
        var newKeyframes = new List<ZoomKeyframe>();
        foreach (var kf in model.ZoomKeyframes)
        {
            if (kf.End <= _cutStart)
            {
                // Entirely before cut — keep unchanged
                newKeyframes.Add(kf);
            }
            else if (kf.Start >= _cutEnd)
            {
                // Entirely after cut — shift back
                newKeyframes.Add(kf with { Timestamp = kf.Timestamp - cutDuration });
            }
            else if (kf.Start < _cutStart && kf.End > _cutEnd)
            {
                // Segment spans entire cut — shrink HoldDuration to absorb the cut
                var holdReduction = cutDuration;
                var newHold = kf.HoldDuration - holdReduction;
                if (newHold < TimeSpan.Zero) newHold = TimeSpan.Zero;
                newKeyframes.Add(kf with { HoldDuration = newHold });
            }
            else if (kf.Start < _cutStart && kf.End <= _cutEnd)
            {
                // Overlaps start of cut — trim the end (reduce HoldDuration/PostDuration)
                var newEnd = _cutStart;
                var totalAfterTimestamp = newEnd - kf.Timestamp;
                if (totalAfterTimestamp < TimeSpan.Zero) totalAfterTimestamp = TimeSpan.Zero;
                var newPost = TimeSpan.FromMilliseconds(Math.Min(kf.PostDuration.TotalMilliseconds,
                    totalAfterTimestamp.TotalMilliseconds * 0.4));
                var newHold = totalAfterTimestamp - newPost;
                if (newHold < TimeSpan.Zero) { newHold = TimeSpan.Zero; newPost = totalAfterTimestamp; }
                newKeyframes.Add(kf with { HoldDuration = newHold, PostDuration = newPost });
            }
            else if (kf.Start >= _cutStart && kf.End > _cutEnd)
            {
                // Overlaps end of cut — trim start, shift back
                var oldStart = kf.Start;
                var newStart = _cutStart;
                var overlap = _cutEnd - oldStart;
                var newPre = kf.PreDuration - overlap;
                if (newPre < TimeSpan.Zero) newPre = TimeSpan.Zero;
                var newTimestamp = newStart + newPre;
                newKeyframes.Add(kf with { Timestamp = newTimestamp, PreDuration = newPre });
            }
            // else: entirely within cut range — removed
        }
        model.ZoomKeyframes.Clear();
        model.ZoomKeyframes.AddRange(newKeyframes);

        // Adjust speed segments (same logic as clips)
        var newSpeedSegments = new List<SpeedSegment>();
        foreach (var seg in model.SpeedSegments)
        {
            if (seg.End <= _cutStart)
                newSpeedSegments.Add(seg);
            else if (seg.Start >= _cutEnd)
                newSpeedSegments.Add(new SpeedSegment(seg.Start - cutDuration, seg.End - cutDuration, seg.Speed));
            else if (seg.Start < _cutStart && seg.End > _cutEnd)
            {
                newSpeedSegments.Add(new SpeedSegment(seg.Start, _cutStart, seg.Speed));
                newSpeedSegments.Add(new SpeedSegment(_cutStart, seg.End - cutDuration, seg.Speed));
            }
            else if (seg.Start < _cutStart && seg.End <= _cutEnd)
                newSpeedSegments.Add(new SpeedSegment(seg.Start, _cutStart, seg.Speed));
            else if (seg.Start >= _cutStart && seg.End > _cutEnd)
                newSpeedSegments.Add(new SpeedSegment(_cutStart, seg.End - cutDuration, seg.Speed));
        }
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(newSpeedSegments);

        model.Duration -= cutDuration;
        if (model.TrimEnd > model.Duration)
            model.TrimEnd = model.Duration;
    }

    public void Undo(TimelineModel model)
    {
        model.Clips.Clear();
        model.Clips.AddRange(_previousClips);
        model.ZoomKeyframes.Clear();
        model.ZoomKeyframes.AddRange(_previousKeyframes);
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(_previousSpeedSegments);
        model.Duration = _previousDuration;
        model.TrimEnd = _previousTrimEnd;
    }
}

public class SplitOperation : IEditOperation
{
    private readonly TimeSpan _splitPosition;
    private int _splitClipIndex = -1;
    private bool _createdDefaultClip;

    public string Description => "Split";

    public SplitOperation(TimeSpan splitPosition)
    {
        _splitPosition = splitPosition;
    }

    public void Execute(TimelineModel model)
    {
        // If no clips exist, create a default clip covering the full timeline
        if (model.Clips.Count == 0)
        {
            model.Clips.Add(new TimelineClip(TimeSpan.Zero, model.Duration, "Clip 1"));
            _createdDefaultClip = true;
        }

        for (int i = 0; i < model.Clips.Count; i++)
        {
            var clip = model.Clips[i];
            if (_splitPosition > clip.Start && _splitPosition < clip.End)
            {
                _splitClipIndex = i;
                var first = new TimelineClip(clip.Start, _splitPosition, clip.Label)
                {
                    SpeedFactor = clip.SpeedFactor,
                    SourceStart = clip.SourceStart,
                };

                // Compute second clip's SourceStart for speed-adjusted clips
                TimeSpan? secondSourceStart = null;
                if (clip.SpeedFactor != 1.0 || clip.SourceStart.HasValue)
                {
                    secondSourceStart = clip.EffectiveSourceStart + TimeSpan.FromTicks(
                        (long)((_splitPosition - clip.Start).Ticks * clip.SpeedFactor));
                }

                var second = new TimelineClip(_splitPosition, clip.End, clip.Label)
                {
                    SpeedFactor = clip.SpeedFactor,
                    SourceStart = secondSourceStart,
                };

                model.Clips[i] = first;
                model.Clips.Insert(i + 1, second);
                return;
            }
        }
    }

    public void Undo(TimelineModel model)
    {
        if (_splitClipIndex < 0 || _splitClipIndex + 1 >= model.Clips.Count)
            return;

        var first = model.Clips[_splitClipIndex];
        var second = model.Clips[_splitClipIndex + 1];
        var merged = new TimelineClip(first.Start, second.End, first.Label)
        {
            SpeedFactor = first.SpeedFactor,
            SourceStart = first.SourceStart,
        };
        model.Clips[_splitClipIndex] = merged;
        model.Clips.RemoveAt(_splitClipIndex + 1);

        if (_createdDefaultClip)
        {
            model.Clips.Clear();
            _createdDefaultClip = false;
        }
    }
}

public class DeleteSegmentOperation : IEditOperation
{
    private readonly int _clipIndex;
    private TimelineClip? _deletedClip;

    public string Description => "Delete Segment";

    public DeleteSegmentOperation(int clipIndex)
    {
        _clipIndex = clipIndex;
    }

    public void Execute(TimelineModel model)
    {
        if (_clipIndex < 0 || _clipIndex >= model.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(_clipIndex));

        _deletedClip = model.Clips[_clipIndex];
        model.Clips.RemoveAt(_clipIndex);
    }

    public void Undo(TimelineModel model)
    {
        if (_deletedClip is not null)
            model.Clips.Insert(_clipIndex, _deletedClip);
    }
}

public class SpeedChangeOperation : IEditOperation
{
    private readonly SpeedSegment _newSegment;
    private List<SpeedSegment> _previousSegments = [];

    public string Description => "Speed Change";

    public SpeedChangeOperation(TimeSpan start, TimeSpan end, double speed)
    {
        _newSegment = new SpeedSegment(start, end, speed);
    }

    public void Execute(TimelineModel model)
    {
        _previousSegments = [.. model.SpeedSegments];

        // Remove any existing segments that overlap the new one, then add
        var result = new List<SpeedSegment>();
        foreach (var seg in model.SpeedSegments)
        {
            if (seg.End <= _newSegment.Start || seg.Start >= _newSegment.End)
            {
                // No overlap — keep
                result.Add(seg);
            }
            else
            {
                // Partial overlaps: keep non-overlapping portions
                if (seg.Start < _newSegment.Start)
                    result.Add(new SpeedSegment(seg.Start, _newSegment.Start, seg.Speed));
                if (seg.End > _newSegment.End)
                    result.Add(new SpeedSegment(_newSegment.End, seg.End, seg.Speed));
            }
        }

        result.Add(_newSegment);
        result.Sort((a, b) => a.Start.CompareTo(b.Start));

        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(result);
    }

    public void Undo(TimelineModel model)
    {
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(_previousSegments);
    }
}

public class MoveZoomKeyframeOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly TimeSpan _newTimestamp;
    private TimeSpan _previousTimestamp;
    private bool _previousIsManual;

    public string Description => "Move Zoom Point";

    public MoveZoomKeyframeOperation(string keyframeId, TimeSpan newTimestamp)
    {
        _keyframeId = keyframeId;
        _newTimestamp = newTimestamp;
    }

    public void Execute(TimelineModel model)
    {
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;
        _previousTimestamp = model.ZoomKeyframes[index].Timestamp;
        _previousIsManual = model.ZoomKeyframes[index].IsManual;
        model.ZoomKeyframes[index] = model.ZoomKeyframes[index] with { Timestamp = _newTimestamp, IsManual = true };

        // When converting an auto-generated keyframe to manual, suppress the
        // original auto-zoom click so it doesn't double-fire.
        if (!_previousIsManual && model.ZoomKeyframes[index].SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Add(ticks);
    }

    public void Undo(TimelineModel model)
    {
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state before reverting the keyframe
        if (!_previousIsManual && model.ZoomKeyframes[index].SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Remove(ticks);

        model.ZoomKeyframes[index] = model.ZoomKeyframes[index] with { Timestamp = _previousTimestamp, IsManual = _previousIsManual };
    }
}

public class RemoveZoomKeyframeOperation : IEditOperation
{
    private readonly string _keyframeId;
    private ZoomKeyframe? _removedKeyframe;
    private int _removedIndex;

    public string Description => "Remove Zoom Segment";

    public RemoveZoomKeyframeOperation(string keyframeId)
    {
        _keyframeId = keyframeId;
    }

    public void Execute(TimelineModel model)
    {
        _removedIndex = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (_removedIndex < 0) return;
        _removedKeyframe = model.ZoomKeyframes[_removedIndex];
        model.ZoomKeyframes.RemoveAt(_removedIndex);

        // Suppress the underlying auto-zoom click so the engine no longer
        // generates a zoom segment for it during preview or export.
        if (!_removedKeyframe.IsManual && _removedKeyframe.SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Add(ticks);
    }

    public void Undo(TimelineModel model)
    {
        if (_removedKeyframe is not null)
        {
            int insertIdx = Math.Min(_removedIndex, model.ZoomKeyframes.Count);
            model.ZoomKeyframes.Insert(insertIdx, _removedKeyframe);

            // Restore the auto-zoom click suppression
            if (!_removedKeyframe.IsManual && _removedKeyframe.SourceClickTicks is long ticks)
                model.SuppressedClickTicks.Remove(ticks);
        }
    }
}

public class AddZoomSegmentOperation : IEditOperation
{
    private readonly ZoomKeyframe _keyframe;

    public string Description => "Add Zoom Segment";

    public AddZoomSegmentOperation(TimeSpan start, TimeSpan end, double zoomLevel,
        double centerX = 0.5, double centerY = 0.5)
    {
        _keyframe = ZoomKeyframe.FromRange(start, end, zoomLevel, centerX, centerY);
    }

    public AddZoomSegmentOperation(ZoomKeyframe keyframe)
    {
        _keyframe = keyframe;
    }

    /// <summary>The Id of the created keyframe, available after Execute.</summary>
    public string CreatedId => _keyframe.Id;

    public void Execute(TimelineModel model)
    {
        model.ZoomKeyframes.Add(_keyframe);
        model.ZoomKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public void Undo(TimelineModel model)
    {
        model.ZoomKeyframes.RemoveAll(k => k.Id == _keyframe.Id);
    }
}

public class ResizeZoomSegmentOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly bool _resizeStart; // true = left edge, false = right edge
    private readonly TimeSpan _newEdgeTime;
    private ZoomKeyframe? _previousKeyframe;

    public string Description => "Resize Zoom Segment";

    public ResizeZoomSegmentOperation(string keyframeId, bool resizeStart, TimeSpan newEdgeTime)
    {
        _keyframeId = keyframeId;
        _resizeStart = resizeStart;
        _newEdgeTime = newEdgeTime;
    }

    public void Execute(TimelineModel model)
    {
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        _previousKeyframe = model.ZoomKeyframes[index];
        var kf = _previousKeyframe;

        // Suppress the auto-zoom click when converting auto→manual via resize
        if (!kf.IsManual && kf.SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Add(ticks);

        if (_resizeStart)
        {
            // Moving left edge: change Start → recalculate PreDuration and Timestamp
            var newStart = _newEdgeTime;
            if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;

            // Clamp: new Start can't go past the hold-end point
            var holdEnd = kf.Timestamp + kf.HoldDuration;
            if (newStart > holdEnd - ZoomKeyframe.MinSegmentDuration)
                newStart = holdEnd - ZoomKeyframe.MinSegmentDuration;

            var newPre = kf.Timestamp - newStart;
            if (newPre < TimeSpan.Zero)
            {
                // Dragged past the timestamp — shift timestamp and reduce hold
                var newTimestamp = newStart + TimeSpan.FromMilliseconds(50);
                var newHold = holdEnd - newTimestamp;
                if (newHold < TimeSpan.Zero) newHold = TimeSpan.Zero;
                model.ZoomKeyframes[index] = kf with
                {
                    Timestamp = newTimestamp,
                    PreDuration = TimeSpan.FromMilliseconds(50),
                    HoldDuration = newHold,
                    IsManual = true,
                };
            }
            else
            {
                model.ZoomKeyframes[index] = kf with { PreDuration = newPre, IsManual = true };
            }
        }
        else
        {
            // Moving right edge: change End → recalculate HoldDuration (keep PostDuration fixed)
            var newEnd = _newEdgeTime;

            // Clamp: new End can't go before Timestamp + minimum
            var minEnd = kf.Timestamp + ZoomKeyframe.MinSegmentDuration;
            if (newEnd < minEnd) newEnd = minEnd;

            // Clamp to timeline duration
            if (newEnd > model.Duration) newEnd = model.Duration;

            var totalAfterTimestamp = newEnd - kf.Timestamp;
            var newPost = kf.PostDuration;
            var newHold = totalAfterTimestamp - newPost;
            if (newHold < TimeSpan.Zero)
            {
                newHold = TimeSpan.Zero;
                newPost = totalAfterTimestamp;
            }

            model.ZoomKeyframes[index] = kf with { HoldDuration = newHold, PostDuration = newPost, IsManual = true };
        }
    }

    public void Undo(TimelineModel model)
    {
        if (_previousKeyframe is null) return;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state
        if (!_previousKeyframe.IsManual && _previousKeyframe.SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Remove(ticks);

        model.ZoomKeyframes[index] = _previousKeyframe;
    }
}

public class UpdateZoomSegmentPropertiesOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly double? _newZoomLevel;
    private readonly double? _newCenterX;
    private readonly double? _newCenterY;
    private ZoomKeyframe? _previousKeyframe;

    public string Description => "Update Zoom Properties";

    public UpdateZoomSegmentPropertiesOperation(string keyframeId,
        double? zoomLevel = null, double? centerX = null, double? centerY = null)
    {
        _keyframeId = keyframeId;
        _newZoomLevel = zoomLevel;
        _newCenterX = centerX;
        _newCenterY = centerY;
    }

    public void Execute(TimelineModel model)
    {
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        _previousKeyframe = model.ZoomKeyframes[index];
        var kf = _previousKeyframe;

        // Suppress the auto-zoom click when converting auto→manual via property edit
        if (!kf.IsManual && kf.SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Add(ticks);

        model.ZoomKeyframes[index] = kf with
        {
            ZoomLevel = _newZoomLevel ?? kf.ZoomLevel,
            CenterX = Math.Clamp(_newCenterX ?? kf.CenterX, 0, 1),
            CenterY = Math.Clamp(_newCenterY ?? kf.CenterY, 0, 1),
            IsManual = true,
        };
    }

    public void Undo(TimelineModel model)
    {
        if (_previousKeyframe is null) return;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state
        if (!_previousKeyframe.IsManual && _previousKeyframe.SourceClickTicks is long ticks)
            model.SuppressedClickTicks.Remove(ticks);

        model.ZoomKeyframes[index] = _previousKeyframe;
    }
}

public class ApplyClipSpeedOperation : IEditOperation
{
    private readonly int _clipIndex;
    private readonly double _newSpeed;

    private List<TimelineClip> _previousClips = [];
    private List<ZoomKeyframe> _previousKeyframes = [];
    private List<SpeedSegment> _previousSpeedSegments = [];
    private TimeSpan _previousDuration;
    private TimeSpan _previousTrimEnd;
    private TimeSpan _previousPlayhead;

    public string Description => "Change Speed";

    public ApplyClipSpeedOperation(int clipIndex, double newSpeed)
    {
        _clipIndex = clipIndex;
        _newSpeed = newSpeed;
    }

    public void Execute(TimelineModel model)
    {
        _previousClips = model.Clips.Select(c => new TimelineClip(c.Start, c.End, c.Label)
            { SpeedFactor = c.SpeedFactor, SourceStart = c.SourceStart }).ToList();
        _previousKeyframes = [.. model.ZoomKeyframes];
        _previousSpeedSegments = [.. model.SpeedSegments];
        _previousDuration = model.Duration;
        _previousTrimEnd = model.TrimEnd;
        _previousPlayhead = model.PlayheadPosition;

        if (_clipIndex < 0 || _clipIndex >= model.Clips.Count) return;

        var clip = model.Clips[_clipIndex];
        var oldEnd = clip.End;
        var oldOutputDuration = clip.End - clip.Start;

        // Source duration = output duration * current speed factor
        double sourceDurationTicks = oldOutputDuration.Ticks * clip.SpeedFactor;
        var newOutputDuration = TimeSpan.FromTicks((long)(sourceDurationTicks / _newSpeed));
        var newEnd = clip.Start + newOutputDuration;
        var delta = oldOutputDuration - newOutputDuration;

        model.Clips[_clipIndex] = new TimelineClip(clip.Start, newEnd, clip.Label)
        {
            SpeedFactor = _newSpeed,
            SourceStart = clip.SourceStart ?? clip.Start,
        };

        // Shift subsequent clips
        for (int i = _clipIndex + 1; i < model.Clips.Count; i++)
        {
            var c = model.Clips[i];
            model.Clips[i] = new TimelineClip(c.Start - delta, c.End - delta, c.Label)
            {
                SpeedFactor = c.SpeedFactor,
                SourceStart = c.SourceStart,
            };
        }

        // Scale zoom keyframes within the clip, shift those after
        for (int i = model.ZoomKeyframes.Count - 1; i >= 0; i--)
        {
            var kf = model.ZoomKeyframes[i];
            if (kf.Timestamp >= oldEnd)
            {
                model.ZoomKeyframes[i] = kf with { Timestamp = kf.Timestamp - delta };
            }
            else if (kf.Timestamp > clip.Start && kf.Timestamp < oldEnd)
            {
                var offsetInClip = kf.Timestamp - clip.Start;
                var scaledOffset = TimeSpan.FromTicks(
                    (long)(offsetInClip.Ticks * (newOutputDuration.Ticks / (double)oldOutputDuration.Ticks)));
                model.ZoomKeyframes[i] = kf with { Timestamp = clip.Start + scaledOffset };
            }
        }

        // Update speed segments
        var newSpeedSegs = new List<SpeedSegment>();
        foreach (var seg in model.SpeedSegments)
        {
            if (seg.End <= clip.Start)
                newSpeedSegs.Add(seg);
            else if (seg.Start >= oldEnd)
                newSpeedSegs.Add(new SpeedSegment(seg.Start - delta, seg.End - delta, seg.Speed));
        }
        if (Math.Abs(_newSpeed - 1.0) > 0.001)
            newSpeedSegs.Add(new SpeedSegment(clip.Start, newEnd, _newSpeed));
        newSpeedSegs.Sort((a, b) => a.Start.CompareTo(b.Start));
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(newSpeedSegs);

        model.Duration -= delta;
        if (model.TrimEnd > model.Duration)
            model.TrimEnd = model.Duration;

        if (model.PlayheadPosition > oldEnd)
            model.PlayheadPosition -= delta;
        else if (model.PlayheadPosition > clip.Start && model.PlayheadPosition < oldEnd)
        {
            var offset = model.PlayheadPosition - clip.Start;
            var scaled = TimeSpan.FromTicks(
                (long)(offset.Ticks * (newOutputDuration.Ticks / (double)oldOutputDuration.Ticks)));
            model.PlayheadPosition = clip.Start + scaled;
        }
        if (model.PlayheadPosition > model.Duration)
            model.PlayheadPosition = model.Duration;
    }

    public void Undo(TimelineModel model)
    {
        model.Clips.Clear();
        model.Clips.AddRange(_previousClips);
        model.ZoomKeyframes.Clear();
        model.ZoomKeyframes.AddRange(_previousKeyframes);
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(_previousSpeedSegments);
        model.Duration = _previousDuration;
        model.TrimEnd = _previousTrimEnd;
        model.PlayheadPosition = _previousPlayhead;
    }
}

public class RippleDeleteOperation : IEditOperation
{
    private readonly int _clipIndex;

    private List<TimelineClip> _previousClips = [];
    private List<ZoomKeyframe> _previousKeyframes = [];
    private List<SpeedSegment> _previousSpeedSegments = [];
    private TimeSpan _previousDuration;
    private TimeSpan _previousTrimEnd;
    private TimeSpan _previousPlayhead;

    public string Description => "Cut";

    public RippleDeleteOperation(int clipIndex)
    {
        _clipIndex = clipIndex;
    }

    public void Execute(TimelineModel model)
    {
        _previousClips = model.Clips.Select(c => new TimelineClip(c.Start, c.End, c.Label)
            { SpeedFactor = c.SpeedFactor, SourceStart = c.SourceStart }).ToList();
        _previousKeyframes = [.. model.ZoomKeyframes];
        _previousSpeedSegments = [.. model.SpeedSegments];
        _previousDuration = model.Duration;
        _previousTrimEnd = model.TrimEnd;
        _previousPlayhead = model.PlayheadPosition;

        if (_clipIndex < 0 || _clipIndex >= model.Clips.Count) return;

        var clip = model.Clips[_clipIndex];
        var clipDuration = clip.End - clip.Start;

        model.Clips.RemoveAt(_clipIndex);

        for (int i = _clipIndex; i < model.Clips.Count; i++)
        {
            var c = model.Clips[i];
            model.Clips[i] = new TimelineClip(c.Start - clipDuration, c.End - clipDuration, c.Label)
            {
                SpeedFactor = c.SpeedFactor,
                SourceStart = c.SourceStart,
            };
        }

        for (int i = model.ZoomKeyframes.Count - 1; i >= 0; i--)
        {
            var kf = model.ZoomKeyframes[i];
            if (kf.Timestamp >= clip.End)
                model.ZoomKeyframes[i] = kf with { Timestamp = kf.Timestamp - clipDuration };
            else if (kf.Timestamp >= clip.Start && kf.Timestamp < clip.End)
                model.ZoomKeyframes.RemoveAt(i);
        }

        var newSpeedSegs = new List<SpeedSegment>();
        foreach (var seg in model.SpeedSegments)
        {
            if (seg.End <= clip.Start)
                newSpeedSegs.Add(seg);
            else if (seg.Start >= clip.End)
                newSpeedSegs.Add(new SpeedSegment(seg.Start - clipDuration, seg.End - clipDuration, seg.Speed));
        }
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(newSpeedSegs);

        model.Duration -= clipDuration;
        if (model.TrimEnd > model.Duration)
            model.TrimEnd = model.Duration;

        model.PlayheadPosition = TimeSpan.FromTicks(
            Math.Min(clip.Start.Ticks, Math.Max(0, model.Duration.Ticks)));
    }

    public void Undo(TimelineModel model)
    {
        model.Clips.Clear();
        model.Clips.AddRange(_previousClips);
        model.ZoomKeyframes.Clear();
        model.ZoomKeyframes.AddRange(_previousKeyframes);
        model.SpeedSegments.Clear();
        model.SpeedSegments.AddRange(_previousSpeedSegments);
        model.Duration = _previousDuration;
        model.TrimEnd = _previousTrimEnd;
        model.PlayheadPosition = _previousPlayhead;
    }
}

// ─── Segment-based edit operations ───────────────────────────────────

public class AddTextSlideOperation : IEditOperation
{
    private readonly int _insertIndex;
    private readonly TextSlideSegment _slide;

    public string Description => "Add Text Slide";

    /// <param name="insertIndex">
    /// Index in <see cref="TimelineModel.Segments"/> to insert at.
    /// Use -1 to append at end.
    /// </param>
    public AddTextSlideOperation(TextSlideSegment slide, int insertIndex = -1)
    {
        _slide = slide ?? throw new ArgumentNullException(nameof(slide));
        _insertIndex = insertIndex;
    }

    public void Execute(TimelineModel model)
    {
        int idx = _insertIndex < 0 || _insertIndex > model.Segments.Count
            ? model.Segments.Count
            : _insertIndex;
        model.Segments.Insert(idx, _slide);
        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        model.Segments.Remove(_slide);
        model.RecalculateSegmentPositions();
    }
}

public class UpdateTextSlideOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly string _newText;
    private readonly string _newFontFamily;
    private readonly double _newFontSize;
    private readonly bool _newIsBold;
    private readonly bool _newIsItalic;
    private readonly string _newTextColor;
    private readonly string _newBackgroundColor;
    private readonly TimeSpan _newDuration;
    private readonly TextSlideAnimation _newAnimation;

    private string _oldText = "";
    private string _oldFontFamily = "";
    private double _oldFontSize;
    private bool _oldIsBold;
    private bool _oldIsItalic;
    private string _oldTextColor = "";
    private string _oldBackgroundColor = "";
    private TimeSpan _oldDuration;
    private TextSlideAnimation _oldAnimation;

    public string Description => "Update Text Slide";

    public UpdateTextSlideOperation(
        string segmentId,
        string text, string fontFamily, double fontSize,
        bool isBold, bool isItalic,
        string textColor, string backgroundColor,
        TimeSpan duration, TextSlideAnimation animation)
    {
        _segmentId = segmentId;
        _newText = text;
        _newFontFamily = fontFamily;
        _newFontSize = fontSize;
        _newIsBold = isBold;
        _newIsItalic = isItalic;
        _newTextColor = textColor;
        _newBackgroundColor = backgroundColor;
        _newDuration = duration;
        _newAnimation = animation;
    }

    public void Execute(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return;

        _oldText = slide.Text;
        _oldFontFamily = slide.FontFamily;
        _oldFontSize = slide.FontSize;
        _oldIsBold = slide.IsBold;
        _oldIsItalic = slide.IsItalic;
        _oldTextColor = slide.TextColor;
        _oldBackgroundColor = slide.BackgroundColor;
        _oldDuration = slide.Duration;
        _oldAnimation = slide.Animation;

        slide.Text = _newText;
        slide.FontFamily = _newFontFamily;
        slide.FontSize = _newFontSize;
        slide.IsBold = _newIsBold;
        slide.IsItalic = _newIsItalic;
        slide.TextColor = _newTextColor;
        slide.BackgroundColor = _newBackgroundColor;
        slide.Duration = _newDuration;
        slide.Animation = _newAnimation;

        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return;

        slide.Text = _oldText;
        slide.FontFamily = _oldFontFamily;
        slide.FontSize = _oldFontSize;
        slide.IsBold = _oldIsBold;
        slide.IsItalic = _oldIsItalic;
        slide.TextColor = _oldTextColor;
        slide.BackgroundColor = _oldBackgroundColor;
        slide.Duration = _oldDuration;
        slide.Animation = _oldAnimation;

        model.RecalculateSegmentPositions();
    }
}

public class RemoveSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private TimelineSegment? _removedSegment;
    private int _removedIndex;

    public string Description => "Remove Segment";

    public RemoveSegmentOperation(string segmentId)
    {
        _segmentId = segmentId;
    }

    public void Execute(TimelineModel model)
    {
        _removedIndex = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_removedIndex < 0) return;
        _removedSegment = model.Segments[_removedIndex];
        model.Segments.RemoveAt(_removedIndex);
        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        if (_removedSegment is null) return;
        model.Segments.Insert(_removedIndex, _removedSegment);
        model.RecalculateSegmentPositions();
    }
}

public class ReorderSegmentOperation : IEditOperation
{
    private readonly int _fromIndex;
    private readonly int _toIndex;
    private List<TimelineSegment> _previousSegments = [];

    public string Description => "Reorder Segment";

    public ReorderSegmentOperation(int fromIndex, int toIndex)
    {
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    public void Execute(TimelineModel model)
    {
        if (_fromIndex < 0 || _fromIndex >= model.Segments.Count) return;
        _previousSegments = [.. model.Segments];
        var segment = model.Segments[_fromIndex];
        model.Segments.RemoveAt(_fromIndex);
        var insertAt = Math.Min(_toIndex, model.Segments.Count);
        model.Segments.Insert(insertAt, segment);
        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        // Snapshot/restore so undo is always exact, even when Execute clamped an
        // append-past-end target (where index arithmetic could not be reversed).
        if (_previousSegments.Count == 0) return;
        model.Segments.Clear();
        model.Segments.AddRange(_previousSegments);
        model.RecalculateSegmentPositions();
    }
}

public class AppendVideoSegmentOperation : IEditOperation
{
    private readonly VideoSegment _segment;

    public string Description => "Append Recording";

    public AppendVideoSegmentOperation(VideoSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    public void Execute(TimelineModel model)
    {
        model.Segments.Add(_segment);
        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        model.Segments.Remove(_segment);
        model.RecalculateSegmentPositions();
    }
}

/// <summary>
/// Splits a <see cref="VideoSegment"/> at the playhead position and inserts
/// a <see cref="TextSlideSegment"/> between the two halves. This keeps video
/// and audio in sync by creating two video segments whose source ranges
/// correspond to the content before and after the split point.
/// </summary>
public class SplitAndInsertTextSlideOperation : IEditOperation
{
    private readonly TimeSpan _splitTime;
    private readonly TextSlideSegment _slide;
    private List<TimelineSegment> _previousSegments = [];

    public string Description => "Insert Text Slide";

    public SplitAndInsertTextSlideOperation(TimeSpan splitTime, TextSlideSegment slide)
    {
        _slide = slide ?? throw new ArgumentNullException(nameof(slide));
        _splitTime = splitTime;
    }

    public void Execute(TimelineModel model)
    {
        _previousSegments = [.. model.Segments];

        // Find the segment at the split time
        int splitIndex = -1;
        for (int i = 0; i < model.Segments.Count; i++)
        {
            var seg = model.Segments[i];
            if (_splitTime > seg.Start && _splitTime < seg.End)
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex >= 0 && model.Segments[splitIndex] is VideoSegment video)
        {
            // Split the video segment into two halves
            var localOffset = _splitTime - video.Start;
            var sourceOffset = TimeSpan.FromTicks(
                (long)(localOffset.Ticks * video.SpeedFactor));

            var firstHalf = video with
            {
                Id = Guid.NewGuid().ToString("N"),
                Duration = localOffset,
                SourceDuration = sourceOffset,
            };

            var secondHalf = video with
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceStart = video.SourceStart + sourceOffset,
                Duration = video.Duration - localOffset,
                SourceDuration = video.SourceDuration - sourceOffset,
            };

            model.Segments.RemoveAt(splitIndex);
            model.Segments.Insert(splitIndex, firstHalf);
            model.Segments.Insert(splitIndex + 1, _slide);
            model.Segments.Insert(splitIndex + 2, secondHalf);
        }
        else
        {
            // Not on a video segment — just insert at the right position
            int insertAt = 0;
            for (int i = 0; i < model.Segments.Count; i++)
            {
                if (_splitTime >= model.Segments[i].End)
                    insertAt = i + 1;
            }
            model.Segments.Insert(insertAt, _slide);
        }

        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        model.Segments.Clear();
        model.Segments.AddRange(_previousSegments);
        model.RecalculateSegmentPositions();
    }
}

/// <summary>
/// Splits the primary-track segment at a given output-timeline time into two
/// contiguous halves. Works for <see cref="VideoSegment"/> (splitting the source
/// range so audio/camera/clicks/zoom/cursor stay in sync) and
/// <see cref="TextSlideSegment"/> (splitting the duration). Total timeline
/// duration is unchanged, so nothing downstream shifts.
/// </summary>
public class SplitSegmentAtTimeOperation : IEditOperation
{
    /// <summary>Minimum half duration so a split never produces a degenerate segment.</summary>
    public static readonly TimeSpan MinHalf = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _splitTime;
    private List<TimelineSegment> _previousSegments = [];
    private bool _didSplit;

    public string Description => "Split";

    public SplitSegmentAtTimeOperation(TimeSpan splitTime)
    {
        _splitTime = splitTime;
    }

    /// <summary>True when the split actually occurred (a segment was divided).</summary>
    public bool DidSplit => _didSplit;

    public void Execute(TimelineModel model)
    {
        _didSplit = false;

        int splitIndex = -1;
        for (int i = 0; i < model.Segments.Count; i++)
        {
            var seg = model.Segments[i];
            if (_splitTime > seg.Start && _splitTime < seg.End)
            {
                splitIndex = i;
                break;
            }
        }
        if (splitIndex < 0) return;

        var segment = model.Segments[splitIndex];
        var localOffset = _splitTime - segment.Start;

        // Reject splits that would create a degenerate half.
        if (localOffset < MinHalf || segment.Duration - localOffset < MinHalf)
            return;

        _previousSegments = [.. model.Segments];

        TimelineSegment firstHalf;
        TimelineSegment secondHalf;

        if (segment is VideoSegment video)
        {
            var sourceOffset = TimeSpan.FromTicks((long)(localOffset.Ticks * video.SpeedFactor));
            firstHalf = video with
            {
                Id = Guid.NewGuid().ToString("N"),
                Duration = localOffset,
                SourceDuration = sourceOffset,
            };
            secondHalf = video with
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceStart = video.SourceStart + sourceOffset,
                Duration = video.Duration - localOffset,
                SourceDuration = video.SourceDuration - sourceOffset,
            };
        }
        else if (segment is TextSlideSegment slide)
        {
            firstHalf = slide with
            {
                Id = Guid.NewGuid().ToString("N"),
                Duration = localOffset,
            };
            secondHalf = slide with
            {
                Id = Guid.NewGuid().ToString("N"),
                Duration = slide.Duration - localOffset,
            };
        }
        else
        {
            return;
        }

        model.Segments.RemoveAt(splitIndex);
        model.Segments.Insert(splitIndex, firstHalf);
        model.Segments.Insert(splitIndex + 1, secondHalf);
        model.RecalculateSegmentPositions();
        _didSplit = true;
    }

    public void Undo(TimelineModel model)
    {
        if (!_didSplit) return;
        model.Segments.Clear();
        model.Segments.AddRange(_previousSegments);
        model.RecalculateSegmentPositions();
    }
}
/// mapping is driven by the segments' actual timeline order.
/// </summary>
public class MoveSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly int _targetIndex;
    private List<TimelineSegment> _previousSegments = [];

    public string Description => "Move Segment";

    public MoveSegmentOperation(string segmentId, int targetIndex)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _targetIndex = targetIndex;
    }

    public void Execute(TimelineModel model)
    {
        _previousSegments = [.. model.Segments];

        int fromIndex = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (fromIndex < 0) return;

        var segment = model.Segments[fromIndex];
        model.Segments.RemoveAt(fromIndex);

        // _targetIndex is expressed against the original list; once the segment is
        // removed, indices after it shift left by one.
        int insertAt = _targetIndex;
        if (insertAt > fromIndex) insertAt--;
        insertAt = Math.Clamp(insertAt, 0, model.Segments.Count);

        model.Segments.Insert(insertAt, segment);
        model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        model.Segments.Clear();
        model.Segments.AddRange(_previousSegments);
        model.RecalculateSegmentPositions();
    }
}

/// <summary>
/// Ripple-trims one edge of a primary-track segment to a new output duration.
/// For a <see cref="VideoSegment"/> the source in/out points are adjusted together
/// with the output duration so the remaining footage — and its linked audio,
/// camera, clicks, zoom, and cursor — stay in sync. For a
/// <see cref="TextSlideSegment"/> only the duration changes. Following segments
/// re-flow magnetically via <see cref="TimelineModel.RecalculateSegmentPositions"/>.
/// </summary>
public class TrimSegmentEdgeOperation : IEditOperation
{
    /// <summary>Minimum segment duration to prevent degenerate (zero-width) segments.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    private readonly string _segmentId;
    private readonly bool _fromStart;
    private readonly TimeSpan _requestedDuration;

    private int _index = -1;
    private TimelineSegment? _previous;

    public string Description => "Trim Segment";

    /// <param name="segmentId">Id of the segment to trim.</param>
    /// <param name="fromStart">True to trim the left (in) edge, false for the right (out) edge.</param>
    /// <param name="newDuration">Desired output duration of the segment after trimming.</param>
    public TrimSegmentEdgeOperation(string segmentId, bool fromStart, TimeSpan newDuration)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _fromStart = fromStart;
        _requestedDuration = newDuration;
    }

    public void Execute(TimelineModel model)
    {
        _index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_index < 0) return;

        _previous = model.Segments[_index];

        switch (_previous)
        {
            case VideoSegment video:
                model.Segments[_index] = TrimVideo(video);
                break;
            case TextSlideSegment slide:
                model.Segments[_index] = slide with { Duration = ClampDuration(_requestedDuration) };
                break;
            default:
                // Produce a new record rather than mutating _previous in place — _previous
                // is the snapshot Undo restores, so writing through it would make undo a no-op.
                model.Segments[_index] = _previous with { Duration = ClampDuration(_requestedDuration) };
                break;
        }

        model.RecalculateSegmentPositions();
    }

    private VideoSegment TrimVideo(VideoSegment video)
    {
        double speed = video.SpeedFactor != 0 ? video.SpeedFactor : 1.0;
        var newDuration = ClampDuration(_requestedDuration);

        if (!_fromStart)
        {
            // Right (out) edge: keep the in-point, change how much footage plays.
            var newSourceDuration = TimeSpan.FromTicks((long)(newDuration.Ticks * speed));
            return video with
            {
                Duration = newDuration,
                SourceDuration = newSourceDuration,
            };
        }

        // Left (in) edge: the head moves. Growing reveals earlier footage (limited by
        // SourceStart >= 0); shrinking advances the in-point. The out-point is fixed.
        var durationDelta = newDuration - video.Duration;
        var sourceDelta = TimeSpan.FromTicks((long)(durationDelta.Ticks * speed));
        var newSourceStart = video.SourceStart - sourceDelta;

        if (newSourceStart < TimeSpan.Zero)
        {
            // Not enough source before the in-point: clamp to the start of the file.
            sourceDelta = video.SourceStart;
            newSourceStart = TimeSpan.Zero;
            durationDelta = TimeSpan.FromTicks((long)(sourceDelta.Ticks / speed));
            newDuration = ClampDuration(video.Duration + durationDelta);
        }

        var trimmedSourceDuration = TimeSpan.FromTicks((long)(newDuration.Ticks * speed));
        return video with
        {
            SourceStart = newSourceStart,
            Duration = newDuration,
            SourceDuration = trimmedSourceDuration,
        };
    }

    private static TimeSpan ClampDuration(TimeSpan requested) =>
        requested < MinDuration ? MinDuration : requested;

    public void Undo(TimelineModel model)
    {
        if (_previous is null || _index < 0) return;
        if (_index >= model.Segments.Count) return;
        model.Segments[_index] = _previous;
        model.RecalculateSegmentPositions();
    }
}

// ── Camera (webcam) track operations ──
// Camera segments live on their own track; their Start/Duration are source-video
// time ranges (independent of the primary segment list), so these operations never
// call RecalculateSegmentPositions.

/// <summary>Adds a new camera overlay segment over a source-time range.</summary>
public class AddCameraSegmentOperation : IEditOperation
{
    private readonly CameraSegment _segment;

    public string CreatedId => _segment.Id;
    public string Description => "Add Camera Segment";

    public AddCameraSegmentOperation(CameraSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    public AddCameraSegmentOperation(TimeSpan sourceStart, TimeSpan sourceDuration,
        string? webcamFilePath = null, WebcamOverlayStyle? style = null)
    {
        _segment = new CameraSegment
        {
            Start = sourceStart,
            Duration = sourceDuration,
            WebcamFilePath = webcamFilePath,
            StyleOverride = style,
        };
    }

    public void Execute(TimelineModel model)
    {
        model.CameraSegments.Add(_segment);
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        model.CameraSegments.RemoveAll(s => s.Id == _segment.Id);
    }
}

/// <summary>Moves a camera segment to a new source-time start, preserving its duration.</summary>
public class MoveCameraSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly TimeSpan _newStart;
    private TimeSpan _previousStart;

    public string Description => "Move Camera Segment";

    public MoveCameraSegmentOperation(string segmentId, TimeSpan newStart)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        _previousStart = seg.Start;
        seg.Start = _newStart < TimeSpan.Zero ? TimeSpan.Zero : _newStart;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Start = _previousStart;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Trims one edge of a camera segment (source-time range).</summary>
public class TrimCameraSegmentOperation : IEditOperation
{
    /// <summary>Minimum camera segment duration.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    private readonly string _segmentId;
    private readonly bool _fromStart;
    private readonly TimeSpan _newEdge;
    private TimeSpan _previousStart;
    private TimeSpan _previousDuration;

    public string Description => "Trim Camera Segment";

    /// <param name="segmentId">Segment to trim.</param>
    /// <param name="fromStart">True for the left edge, false for the right edge.</param>
    /// <param name="newEdgeSourceTime">New source-time position of the dragged edge.</param>
    public TrimCameraSegmentOperation(string segmentId, bool fromStart, TimeSpan newEdgeSourceTime)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _fromStart = fromStart;
        _newEdge = newEdgeSourceTime;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;

        _previousStart = seg.Start;
        _previousDuration = seg.Duration;

        if (_fromStart)
        {
            var end = seg.End;
            var newStart = _newEdge < TimeSpan.Zero ? TimeSpan.Zero : _newEdge;
            if (newStart > end - MinDuration) newStart = end - MinDuration;
            seg.Start = newStart;
            seg.Duration = end - newStart;
        }
        else
        {
            var newEnd = _newEdge;
            if (newEnd < seg.Start + MinDuration) newEnd = seg.Start + MinDuration;
            seg.Duration = newEnd - seg.Start;
        }

        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Start = _previousStart;
        seg.Duration = _previousDuration;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Removes a camera segment.</summary>
public class RemoveCameraSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private CameraSegment? _removed;

    public string Description => "Remove Camera Segment";

    public RemoveCameraSegmentOperation(string segmentId)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (_removed is not null)
            model.CameraSegments.RemoveAll(s => s.Id == _segmentId);
    }

    public void Undo(TimelineModel model)
    {
        if (_removed is null) return;
        model.CameraSegments.Add(_removed);
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Updates the enabled flag and/or style override of a camera segment.</summary>
public class UpdateCameraSegmentPropertiesOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly bool? _enabled;
    private readonly bool? _fullscreenEnabled;
    private readonly CameraFullscreenMode? _fullscreenMode;
    private readonly WebcamOverlayStyle? _styleOverride;
    private readonly bool _setStyle;

    private bool _previousEnabled;
    private bool _previousFullscreenEnabled;
    private CameraFullscreenMode _previousFullscreenMode;
    private WebcamOverlayStyle? _previousStyle;

    public string Description => "Update Camera Segment";

    public UpdateCameraSegmentPropertiesOperation(string segmentId, bool? enabled = null,
        WebcamOverlayStyle? styleOverride = null, bool setStyle = false,
        bool? fullscreenEnabled = null, CameraFullscreenMode? fullscreenMode = null)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _enabled = enabled;
        _styleOverride = styleOverride;
        _setStyle = setStyle;
        _fullscreenEnabled = fullscreenEnabled;
        _fullscreenMode = fullscreenMode;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;

        _previousEnabled = seg.Enabled;
        _previousFullscreenEnabled = seg.FullscreenEnabled;
        _previousFullscreenMode = seg.FullscreenMode;
        _previousStyle = seg.StyleOverride;

        if (_enabled is bool en) seg.Enabled = en;
        if (_fullscreenEnabled is bool fs) seg.FullscreenEnabled = fs;
        if (_fullscreenMode is CameraFullscreenMode fm) seg.FullscreenMode = fm;
        if (_setStyle) seg.StyleOverride = _styleOverride;
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Enabled = _previousEnabled;
        seg.FullscreenEnabled = _previousFullscreenEnabled;
        seg.FullscreenMode = _previousFullscreenMode;
        seg.StyleOverride = _previousStyle;
    }
}

// ── Text overlay track operations ──
// Text overlays live on their own track; their Start/Duration are source-video time
// ranges (independent of the primary segment list), so these operations never call
// RecalculateSegmentPositions.

/// <summary>Adds a new animated text overlay segment over a source-time range.</summary>
public class AddTextOverlayOperation : IEditOperation
{
    private readonly TextOverlaySegment _segment;

    public string CreatedId => _segment.Id;
    public string Description => "Add Text Overlay";

    public AddTextOverlayOperation(TextOverlaySegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    public AddTextOverlayOperation(TimeSpan sourceStart, TimeSpan sourceDuration,
        string? text = null, string? sourceVideoFilePath = null)
    {
        _segment = new TextOverlaySegment
        {
            Start = sourceStart,
            Duration = sourceDuration,
            Text = text ?? "Text",
            SourceVideoFilePath = sourceVideoFilePath,
        };
    }

    public void Execute(TimelineModel model)
    {
        model.TextOverlays.Add(_segment);
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        model.TextOverlays.RemoveAll(o => o.Id == _segment.Id);
    }
}

/// <summary>Moves a text overlay to a new source-time start, preserving its duration.</summary>
public class MoveTextOverlayOperation : IEditOperation
{
    private readonly string _overlayId;
    private readonly TimeSpan _newStart;
    private TimeSpan _previousStart;

    public string Description => "Move Text Overlay";

    public MoveTextOverlayOperation(string overlayId, TimeSpan newStart)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        _previousStart = overlay.Start;
        overlay.Start = _newStart < TimeSpan.Zero ? TimeSpan.Zero : _newStart;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        overlay.Start = _previousStart;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Trims one edge of a text overlay (source-time range).</summary>
public class TrimTextOverlayOperation : IEditOperation
{
    /// <summary>Minimum text overlay duration — longer than the camera track's, since text needs time to be read.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(200);

    private readonly string _overlayId;
    private readonly bool _fromStart;
    private readonly TimeSpan _newEdge;
    private TimeSpan _previousStart;
    private TimeSpan _previousDuration;

    public string Description => "Trim Text Overlay";

    /// <param name="overlayId">Overlay to trim.</param>
    /// <param name="fromStart">True for the left edge, false for the right edge.</param>
    /// <param name="newEdgeSourceTime">New source-time position of the dragged edge.</param>
    public TrimTextOverlayOperation(string overlayId, bool fromStart, TimeSpan newEdgeSourceTime)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _fromStart = fromStart;
        _newEdge = newEdgeSourceTime;
    }

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        _previousStart = overlay.Start;
        _previousDuration = overlay.Duration;

        if (_fromStart)
        {
            var end = overlay.End;
            var newStart = _newEdge < TimeSpan.Zero ? TimeSpan.Zero : _newEdge;
            if (newStart > end - MinDuration) newStart = end - MinDuration;
            overlay.Start = newStart;
            overlay.Duration = end - newStart;
        }
        else
        {
            var newEnd = _newEdge;
            if (newEnd < overlay.Start + MinDuration) newEnd = overlay.Start + MinDuration;
            overlay.Duration = newEnd - overlay.Start;
        }

        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        overlay.Start = _previousStart;
        overlay.Duration = _previousDuration;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Removes a text overlay.</summary>
public class RemoveTextOverlayOperation : IEditOperation
{
    private readonly string _overlayId;
    private TextOverlaySegment? _removed;

    public string Description => "Remove Text Overlay";

    public RemoveTextOverlayOperation(string overlayId)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (_removed is not null)
            model.TextOverlays.RemoveAll(o => o.Id == _overlayId);
    }

    public void Undo(TimelineModel model)
    {
        if (_removed is null) return;
        model.TextOverlays.Add(_removed);
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>
/// Updates one or more properties of a text overlay via an <paramref name="apply"/> callback
/// that mutates the live segment. Rather than enumerating every editable property as a
/// nullable constructor parameter (there are nearly thirty of them, covering placement,
/// typography, and every background style's parameters), the caller supplies a delegate that
/// sets whatever it needs; a full snapshot of the segment is taken before <paramref name="apply"/>
/// runs so Undo can restore every field regardless of which ones changed.
/// </summary>
public class UpdateTextOverlayPropertiesOperation : IEditOperation
{
    private readonly string _overlayId;
    private readonly Action<TextOverlaySegment> _apply;
    private readonly string? _description;

    private TextOverlaySegment? _previous;

    public string Description => _description ?? "Update Text Overlay";

    /// <param name="overlayId">Overlay to update.</param>
    /// <param name="apply">Callback that mutates the live segment's properties.</param>
    /// <param name="description">Optional undo-stack label; defaults to "Update Text Overlay".</param>
    public UpdateTextOverlayPropertiesOperation(string overlayId, Action<TextOverlaySegment> apply,
        string? description = null)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _description = description;
    }

    /// <summary>Convenience for the common case of updating just the overlay's text.</summary>
    public static UpdateTextOverlayPropertiesOperation ForText(string overlayId, string text) =>
        new(overlayId, o => o.Text = text, "Update Text Overlay Text");

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        // Shallow record copy: captures every property's current value as the undo snapshot
        // before apply() mutates the live segment in place.
        _previous = overlay with { };
        _apply(overlay);

        if (overlay.Start != _previous.Start || overlay.Duration != _previous.Duration)
            model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        if (_previous is null) return;
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        // Restore every field individually onto the live instance rather than replacing the
        // list element with _previous (or writing through it) — either would leave the
        // snapshot object aliased with the live segment, so a later redo would mutate _previous
        // itself and turn undo into a no-op (see the TrimSegmentEdgeOperation fix above).
        overlay.Text = _previous.Text;
        overlay.Enabled = _previous.Enabled;
        overlay.Animation = _previous.Animation;
        overlay.Anchor = _previous.Anchor;
        overlay.X = _previous.X;
        overlay.Y = _previous.Y;
        overlay.WidthFraction = _previous.WidthFraction;
        overlay.MarginFraction = _previous.MarginFraction;
        overlay.FontFamily = _previous.FontFamily;
        overlay.FontSize = _previous.FontSize;
        overlay.IsBold = _previous.IsBold;
        overlay.IsItalic = _previous.IsItalic;
        overlay.TextColor = _previous.TextColor;
        overlay.TextAlignment = _previous.TextAlignment;
        overlay.Background = _previous.Background;
        overlay.BackgroundColor = _previous.BackgroundColor;
        overlay.BackgroundOpacity = _previous.BackgroundOpacity;
        overlay.CornerRadius = _previous.CornerRadius;
        overlay.PaddingScale = _previous.PaddingScale;
        overlay.BlurAmount = _previous.BlurAmount;
        overlay.BlurTintOpacity = _previous.BlurTintOpacity;
        overlay.ScrimDirection = _previous.ScrimDirection;
        overlay.ScrimStrength = _previous.ScrimStrength;
        overlay.OutlineWidth = _previous.OutlineWidth;
        overlay.OutlineColor = _previous.OutlineColor;
        overlay.ShadowStrength = _previous.ShadowStrength;
        overlay.AccentColor = _previous.AccentColor;
        overlay.AccentThickness = _previous.AccentThickness;
        overlay.AccentSide = _previous.AccentSide;
        overlay.Start = _previous.Start;
        overlay.Duration = _previous.Duration;

        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}