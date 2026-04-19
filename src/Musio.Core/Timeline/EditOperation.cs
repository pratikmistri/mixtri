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

        // Adjust zoom keyframes
        var newKeyframes = new List<ZoomKeyframe>();
        foreach (var kf in model.ZoomKeyframes)
        {
            if (kf.Timestamp < _cutStart)
                newKeyframes.Add(kf);
            else if (kf.Timestamp >= _cutEnd)
                newKeyframes.Add(kf with { Timestamp = kf.Timestamp - cutDuration });
            // else: within cut range — removed
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

    public string Description => "Split";

    public SplitOperation(TimeSpan splitPosition)
    {
        _splitPosition = splitPosition;
    }

    public void Execute(TimelineModel model)
    {
        for (int i = 0; i < model.Clips.Count; i++)
        {
            var clip = model.Clips[i];
            if (_splitPosition > clip.Start && _splitPosition < clip.End)
            {
                _splitClipIndex = i;
                var first = new TimelineClip(clip.Start, _splitPosition, clip.Label);
                var second = new TimelineClip(_splitPosition, clip.End, clip.Label);
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
        var merged = new TimelineClip(first.Start, second.End, first.Label);
        model.Clips[_splitClipIndex] = merged;
        model.Clips.RemoveAt(_splitClipIndex + 1);
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
