namespace Musio.Core.Timeline;

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
    private readonly float? _newRotationY;
    private readonly float? _newRotationX;
    private ZoomKeyframe? _previousKeyframe;

    public string Description => "Update Zoom Properties";

    public UpdateZoomSegmentPropertiesOperation(string keyframeId,
        double? zoomLevel = null, double? centerX = null, double? centerY = null,
        float? rotationY = null, float? rotationX = null)
    {
        _keyframeId = keyframeId;
        _newZoomLevel = zoomLevel;
        _newCenterX = centerX;
        _newCenterY = centerY;
        _newRotationY = rotationY;
        _newRotationX = rotationX;
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
            RotationY = _newRotationY ?? kf.RotationY,
            RotationX = _newRotationX ?? kf.RotationX,
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
