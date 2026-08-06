namespace Musio.Core.Timeline;

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
                // See SplitSegmentAtTimeOperation's identical fix: the record `with` above
                // otherwise copies the original video's InTransition onto this brand-new
                // (slide -> secondHalf) boundary, which was never configured by the user.
                InTransition = null,
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
                // The boundary this InTransition described (predecessor -> original segment)
                // now belongs entirely to firstHalf, which correctly keeps it via the `with`
                // above. The record `with` expression otherwise copies EVERY property it
                // doesn't override -- including InTransition -- so secondHalf must explicitly
                // null it out, or the brand-new, purely-mechanical firstHalf -> secondHalf
                // boundary this split just created would incorrectly inherit and play the
                // original config (see the T8 "known bug" report in learnings.md).
                InTransition = null,
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
                // Same reasoning as the VideoSegment branch above: this is a new, unconfigured
                // boundary, not the original one.
                InTransition = null,
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

/// <summary>
/// Reorders a primary-track segment to a new index in <see cref="TimelineModel.Segments"/>.
/// <c>targetIndex</c> is expressed against the pre-move list, so it is shifted left by one when
/// the segment moves later in the list. <see cref="TimelineModel.RecalculateSegmentPositions"/>
/// then re-derives every segment's timeline position, because segments hold no absolute start
/// time of their own — output-time mapping is driven by the segments' actual timeline order.
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
