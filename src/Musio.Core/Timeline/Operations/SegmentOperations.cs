namespace Musio.Core.Timeline;

// ─── Segment-based edit operations ───────────────────────────────────

public class AddTextSlideOperation : SegmentEditOperationBase
{
    private readonly int _insertIndex;
    private readonly TextSlideSegment _slide;

    public override string Description => "Add Text Slide";

    /// <param name="insertIndex">
    /// Index in <see cref="TimelineModel.Segments"/> to insert at.
    /// Use -1 to append at end.
    /// </param>
    public AddTextSlideOperation(TextSlideSegment slide, int insertIndex = -1)
    {
        _slide = slide ?? throw new ArgumentNullException(nameof(slide));
        _insertIndex = insertIndex;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        int idx = _insertIndex < 0 || _insertIndex > model.Segments.Count
            ? model.Segments.Count
            : _insertIndex;
        model.Segments.Insert(idx, _slide);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        model.Segments.Remove(_slide);
        return true;
    }
}

public class UpdateTextSlideOperation : SegmentEditOperationBase
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
    private readonly bool _updatesTextWindow;
    private readonly TimeSpan _newTextInStart;
    private readonly TimeSpan? _newTextInDuration;
    private readonly TimeSpan? _newTextOutEnd;
    private readonly TimeSpan? _newTextOutDuration;

    private string _oldText = "";
    private string _oldFontFamily = "";
    private double _oldFontSize;
    private bool _oldIsBold;
    private bool _oldIsItalic;
    private string _oldTextColor = "";
    private string _oldBackgroundColor = "";
    private TimeSpan _oldDuration;
    private TextSlideAnimation _oldAnimation;
    private TimeSpan _oldTextInStart;
    private TimeSpan? _oldTextInDuration;
    private TimeSpan? _oldTextOutEnd;
    private TimeSpan? _oldTextOutDuration;

    public override string Description => "Update Text Slide";

    public UpdateTextSlideOperation(
        string segmentId,
        string text, string fontFamily, double fontSize,
        bool isBold, bool isItalic,
        string textColor, string backgroundColor,
        TimeSpan duration, TextSlideAnimation animation)
        : this(segmentId, text, fontFamily, fontSize, isBold, isItalic, textColor, backgroundColor,
            duration, animation, default, null, null, null, false)
    {
    }

    public UpdateTextSlideOperation(
        string segmentId,
        string text, string fontFamily, double fontSize,
        bool isBold, bool isItalic,
        string textColor, string backgroundColor,
        TimeSpan duration, TextSlideAnimation animation,
        TimeSpan textInStart, TimeSpan? textInDuration,
        TimeSpan? textOutEnd, TimeSpan? textOutDuration)
        : this(segmentId, text, fontFamily, fontSize, isBold, isItalic, textColor, backgroundColor,
            duration, animation, textInStart, textInDuration, textOutEnd, textOutDuration, true)
    {
    }

    private UpdateTextSlideOperation(
        string segmentId,
        string text, string fontFamily, double fontSize,
        bool isBold, bool isItalic,
        string textColor, string backgroundColor,
        TimeSpan duration, TextSlideAnimation animation,
        TimeSpan textInStart, TimeSpan? textInDuration,
        TimeSpan? textOutEnd, TimeSpan? textOutDuration,
        bool updatesTextWindow)
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
        _newTextInStart = textInStart;
        _newTextInDuration = textInDuration;
        _newTextOutEnd = textOutEnd;
        _newTextOutDuration = textOutDuration;
        _updatesTextWindow = updatesTextWindow;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return false;

        _oldText = slide.Text;
        _oldFontFamily = slide.FontFamily;
        _oldFontSize = slide.FontSize;
        _oldIsBold = slide.IsBold;
        _oldIsItalic = slide.IsItalic;
        _oldTextColor = slide.TextColor;
        _oldBackgroundColor = slide.BackgroundColor;
        _oldDuration = slide.Duration;
        _oldAnimation = slide.Animation;
        _oldTextInStart = slide.TextInStart;
        _oldTextInDuration = slide.TextInDuration;
        _oldTextOutEnd = slide.TextOutEnd;
        _oldTextOutDuration = slide.TextOutDuration;

        slide.Text = _newText;
        slide.FontFamily = _newFontFamily;
        slide.FontSize = _newFontSize;
        slide.IsBold = _newIsBold;
        slide.IsItalic = _newIsItalic;
        slide.TextColor = _newTextColor;
        slide.BackgroundColor = _newBackgroundColor;
        slide.Duration = _newDuration;
        slide.Animation = _newAnimation;
        if (_updatesTextWindow)
        {
            slide.TextInStart = _newTextInStart;
            slide.TextInDuration = _newTextInDuration;
            slide.TextOutEnd = _newTextOutEnd;
            slide.TextOutDuration = _newTextOutDuration;
        }

        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return false;

        slide.Text = _oldText;
        slide.FontFamily = _oldFontFamily;
        slide.FontSize = _oldFontSize;
        slide.IsBold = _oldIsBold;
        slide.IsItalic = _oldIsItalic;
        slide.TextColor = _oldTextColor;
        slide.BackgroundColor = _oldBackgroundColor;
        slide.Duration = _oldDuration;
        slide.Animation = _oldAnimation;
        if (_updatesTextWindow)
        {
            slide.TextInStart = _oldTextInStart;
            slide.TextInDuration = _oldTextInDuration;
            slide.TextOutEnd = _oldTextOutEnd;
            slide.TextOutDuration = _oldTextOutDuration;
        }

        return true;
    }
}

public class RemoveSegmentOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private TimelineSegment? _removedSegment;
    private int _removedIndex;

    public override string Description => "Remove Segment";

    public RemoveSegmentOperation(string segmentId)
    {
        _segmentId = segmentId;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _removedIndex = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_removedIndex < 0) return false;
        _removedSegment = model.Segments[_removedIndex];
        model.Segments.RemoveAt(_removedIndex);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        if (_removedSegment is null) return false;
        model.Segments.Insert(_removedIndex, _removedSegment);
        return true;
    }
}

public class ReorderSegmentOperation : SegmentEditOperationBase
{
    private readonly int _fromIndex;
    private readonly int _toIndex;
    private SegmentListSnapshot? _snapshot;

    public override string Description => "Reorder Segment";

    public ReorderSegmentOperation(int fromIndex, int toIndex)
    {
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        if (_fromIndex < 0 || _fromIndex >= model.Segments.Count) return false;
        _snapshot = SegmentListSnapshot.Capture(model);
        var segment = model.Segments[_fromIndex];
        model.Segments.RemoveAt(_fromIndex);
        var insertAt = Math.Min(_toIndex, model.Segments.Count);
        model.Segments.Insert(insertAt, segment);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        // Snapshot/restore so undo is always exact, even when Execute clamped an
        // append-past-end target (where index arithmetic could not be reversed).
        // SegmentListSnapshot.Restore already recalculates positions, so signal "no
        // recalculate needed" here to avoid a redundant second call.
        _snapshot?.Restore(model);
        return false;
    }
}

public class AppendVideoSegmentOperation : SegmentEditOperationBase
{
    private readonly VideoSegment _segment;

    public override string Description => "Append Recording";

    public AppendVideoSegmentOperation(VideoSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        model.Segments.Add(_segment);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        model.Segments.Remove(_segment);
        return true;
    }
}

/// <summary>
/// Inserts a full-frame segment on an absolute overlay track so adding a title card, imported
/// video, or appended recording covers the base edit instead of splitting and shifting it.
/// </summary>
public class InsertSegmentOnOverlayTrackOperation : SegmentEditOperationBase
{
    private readonly TimelineSegment _segment;
    private readonly TimeSpan _start;
    private readonly int _trackIndex;

    public override string Description => "Insert Segment on Overlay Track";

    /// <summary>
    /// Creates an overlay insert. A <paramref name="trackIndex"/> of -1 asks the model for the
    /// first non-colliding overlay lane; any other value is clamped to the overlay range.
    /// </summary>
    public InsertSegmentOnOverlayTrackOperation(TimelineSegment segment, TimeSpan start, int trackIndex = -1)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _start = start;
        _trackIndex = trackIndex;
    }

    /// <summary>
    /// Track used by the last successful execute, so UI code can select the lane that auto
    /// placement resolved without re-running collision logic.
    /// </summary>
    public int ResolvedTrackIndex { get; private set; }

    protected override bool ExecuteCore(TimelineModel model)
    {
        var start = ClampNonNegative(_start);
        int track = _trackIndex < 1
            ? model.FindFreeOverlayTrack(start, _segment.Duration)
            : Math.Max(1, _trackIndex);

        _segment.TrackIndex = track;
        _segment.Start = start;
        ResolvedTrackIndex = track;
        model.Segments.Add(_segment);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        model.Segments.Remove(_segment);
        return true;
    }

    private static TimeSpan ClampNonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

/// <summary>
/// Moves a full-frame segment between absolute overlay lanes and the contiguous base chain.
/// Undo restores a full segment-list snapshot because base reflow and overlay z-order make a
/// hand-written inverse easy to get subtly wrong.
/// </summary>
public class MoveSegmentOnTrackOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private readonly TimeSpan _newStart;
    private readonly int _newTrackIndex;
    private SegmentListSnapshot? _snapshot;
    private bool _didMove;

    public override string Description => "Move Segment on Track";

    /// <summary>
    /// Creates a track move. Moving to track 0 inserts into the base chain at the position
    /// implied by <paramref name="newStart"/>; moving off track 0 pins the current absolute
    /// start so the segment leaves the base chain without jumping.
    /// </summary>
    public MoveSegmentOnTrackOperation(string segmentId, TimeSpan newStart, int newTrackIndex)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _newStart = newStart;
        _newTrackIndex = newTrackIndex;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _didMove = false;

        int index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (index < 0) return false;

        _snapshot = SegmentListSnapshot.Capture(model);
        var segment = model.Segments[index];
        int targetTrack = Math.Max(TimelineModel.BaseTrackIndex, _newTrackIndex);

        if (targetTrack == TimelineModel.BaseTrackIndex)
        {
            model.Segments.RemoveAt(index);
            segment.TrackIndex = TimelineModel.BaseTrackIndex;
            model.RecalculateSegmentPositions();
            int insertAt = FindBaseInsertionIndex(model, ClampNonNegative(_newStart));
            model.Segments.Insert(insertAt, segment);
        }
        else
        {
            var start = segment.TrackIndex == TimelineModel.BaseTrackIndex
                ? segment.Start
                : ClampNonNegative(_newStart);
            segment.TrackIndex = targetTrack;
            segment.Start = ClampToNoGap(model, segment, start);
        }

        _didMove = true;
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        if (!_didMove) return false;
        _snapshot?.Restore(model);
        return false;
    }

    /// <summary>
    /// Clamps an overlay segment's start so it can never begin after everything else has
    /// finished. A gap would extend <see cref="TimelineModel.TotalSegmentsDuration"/> (now
    /// max-End) past the last covered instant, and the export composer resolves every frame
    /// in that range through <see cref="TimelineModel.GetSegmentAtTime"/> — an uncovered
    /// frame has no segment to render. Pinning the start to the end of the rest of the
    /// timeline keeps "the timeline is fully covered from 0 to its duration" an invariant
    /// rather than something each consumer has to defend against.
    /// </summary>
    private static TimeSpan ClampToNoGap(TimelineModel model, TimelineSegment moving, TimeSpan start)
    {
        var latestEnd = TimeSpan.Zero;
        foreach (var other in model.Segments)
        {
            if (ReferenceEquals(other, moving)) continue;
            if (other.End > latestEnd) latestEnd = other.End;
        }

        return start > latestEnd ? latestEnd : ClampNonNegative(start);
    }

    private static int FindBaseInsertionIndex(TimelineModel model, TimeSpan newStart)
    {
        for (int i = 0; i < model.Segments.Count; i++)
        {
            var segment = model.Segments[i];
            if (segment.TrackIndex == TimelineModel.BaseTrackIndex && newStart <= segment.Start)
                return i;
        }

        return model.Segments.Count;
    }

    private static TimeSpan ClampNonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

/// <summary>
/// Changes a text slide's editable animation window without touching the slide duration, so
/// timeline drag handles get undo/redo while the base track does not reflow unnecessarily.
/// </summary>
public class SetTextSlideTextWindowOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private readonly TimeSpan _inStart;
    private readonly TimeSpan _outEnd;
    private TimeSpan _oldInStart;
    private TimeSpan? _oldOutEnd;

    /// <summary>Smallest authored text window allowed by drag handles when the slide is long enough.</summary>
    public static readonly TimeSpan MinTextWindow = TimeSpan.FromMilliseconds(200);

    public override string Description => "Set Text Slide Text Window";

    /// <summary>
    /// Creates a text-window edit. Inputs are clamped during execute because drag gestures may
    /// temporarily cross over or leave the segment bounds.
    /// </summary>
    public SetTextSlideTextWindowOperation(string segmentId, TimeSpan inStart, TimeSpan outEnd)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _inStart = inStart;
        _outEnd = outEnd;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return false;

        _oldInStart = slide.TextInStart;
        _oldOutEnd = slide.TextOutEnd;

        var window = ClampWindow(_inStart, _outEnd, slide.Duration);
        slide.TextInStart = window.Start;
        slide.TextOutEnd = window.End;
        return false;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        var slide = model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == _segmentId);
        if (slide is null) return false;

        slide.TextInStart = _oldInStart;
        slide.TextOutEnd = _oldOutEnd;
        return false;
    }

    private static (TimeSpan Start, TimeSpan End) ClampWindow(TimeSpan inStart, TimeSpan outEnd, TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration <= TimeSpan.Zero)
            return (TimeSpan.Zero, TimeSpan.Zero);

        if (duration <= MinTextWindow)
            return (TimeSpan.Zero, duration);

        var start = Clamp(inStart, TimeSpan.Zero, duration);
        var end = Clamp(outEnd, TimeSpan.Zero, duration);
        if (end < start)
            end = start;

        if (end - start < MinTextWindow)
        {
            end = start + MinTextWindow;
            if (end > duration)
            {
                end = duration;
                start = end - MinTextWindow;
            }
        }

        return (start, end);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}

/// <summary>
/// Splits a <see cref="VideoSegment"/> at the playhead position and inserts
/// a <see cref="TextSlideSegment"/> between the two halves. This keeps video
/// and audio in sync by creating two video segments whose source ranges
/// correspond to the content before and after the split point.
/// </summary>
public class SplitAndInsertTextSlideOperation : SegmentEditOperationBase
{
    private readonly TimeSpan _splitTime;
    private readonly TextSlideSegment _slide;
    private SegmentListSnapshot? _snapshot;

    public override string Description => "Insert Text Slide";

    public SplitAndInsertTextSlideOperation(TimeSpan splitTime, TextSlideSegment slide)
    {
        _slide = slide ?? throw new ArgumentNullException(nameof(slide));
        _splitTime = splitTime;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _snapshot = SegmentListSnapshot.Capture(model);

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

        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        // SegmentListSnapshot.Restore already recalculates positions, so signal "no
        // recalculate needed" here to avoid a redundant second call.
        _snapshot?.Restore(model);
        return false;
    }
}

/// <summary>
/// Splits the primary-track segment at a given output-timeline time into two
/// contiguous halves. Works for <see cref="VideoSegment"/> (splitting the source
/// range so audio/camera/clicks/zoom/cursor stay in sync) and
/// <see cref="TextSlideSegment"/> (splitting the duration). Total timeline
/// duration is unchanged, so nothing downstream shifts.
/// </summary>
public class SplitSegmentAtTimeOperation : SegmentEditOperationBase
{
    /// <summary>Minimum half duration so a split never produces a degenerate segment.</summary>
    public static readonly TimeSpan MinHalf = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _splitTime;
    private SegmentListSnapshot? _snapshot;
    private bool _didSplit;

    public override string Description => "Split";

    public SplitSegmentAtTimeOperation(TimeSpan splitTime)
    {
        _splitTime = splitTime;
    }

    /// <summary>True when the split actually occurred (a segment was divided).</summary>
    public bool DidSplit => _didSplit;

    protected override bool ExecuteCore(TimelineModel model)
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
        if (splitIndex < 0) return false;

        var segment = model.Segments[splitIndex];
        var localOffset = _splitTime - segment.Start;

        // Reject splits that would create a degenerate half.
        if (localOffset < MinHalf || segment.Duration - localOffset < MinHalf)
            return false;

        _snapshot = SegmentListSnapshot.Capture(model);

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
            return false;
        }

        model.Segments.RemoveAt(splitIndex);
        model.Segments.Insert(splitIndex, firstHalf);
        model.Segments.Insert(splitIndex + 1, secondHalf);
        _didSplit = true;
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        if (!_didSplit) return false;
        // SegmentListSnapshot.Restore already recalculates positions, so signal "no
        // recalculate needed" here to avoid a redundant second call.
        _snapshot?.Restore(model);
        return false;
    }
}

/// <summary>
/// Reorders a primary-track segment to a new index in <see cref="TimelineModel.Segments"/>.
/// <c>targetIndex</c> is expressed against the pre-move list, so it is shifted left by one when
/// the segment moves later in the list. <see cref="TimelineModel.RecalculateSegmentPositions"/>
/// then re-derives every segment's timeline position, because segments hold no absolute start
/// time of their own — output-time mapping is driven by the segments' actual timeline order.
/// </summary>
public class MoveSegmentOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private readonly int _targetIndex;
    private SegmentListSnapshot? _snapshot;

    public override string Description => "Move Segment";

    public MoveSegmentOperation(string segmentId, int targetIndex)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _targetIndex = targetIndex;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _snapshot = SegmentListSnapshot.Capture(model);

        int fromIndex = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (fromIndex < 0) return false;

        var segment = model.Segments[fromIndex];
        model.Segments.RemoveAt(fromIndex);

        // _targetIndex is expressed against the original list; once the segment is
        // removed, indices after it shift left by one.
        int insertAt = _targetIndex;
        if (insertAt > fromIndex) insertAt--;
        insertAt = Math.Clamp(insertAt, 0, model.Segments.Count);

        model.Segments.Insert(insertAt, segment);
        return true;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        // SegmentListSnapshot.Restore already recalculates positions, so signal "no
        // recalculate needed" here to avoid a redundant second call.
        _snapshot?.Restore(model);
        return false;
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
public class TrimSegmentEdgeOperation : SegmentEditOperationBase
{
    /// <summary>Minimum segment duration to prevent degenerate (zero-width) segments.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    private readonly string _segmentId;
    private readonly bool _fromStart;
    private readonly TimeSpan _requestedDuration;

    private int _index = -1;
    private TimelineSegment? _previous;

    public override string Description => "Trim Segment";

    /// <param name="segmentId">Id of the segment to trim.</param>
    /// <param name="fromStart">True to trim the left (in) edge, false for the right (out) edge.</param>
    /// <param name="newDuration">Desired output duration of the segment after trimming.</param>
    public TrimSegmentEdgeOperation(string segmentId, bool fromStart, TimeSpan newDuration)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _fromStart = fromStart;
        _requestedDuration = newDuration;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_index < 0) return false;

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

        AnchorOverlayOutPoint(model.Segments[_index], _previous);
        return true;
    }

    /// <summary>
    /// Keeps a head-trimmed OVERLAY segment's out-point where it was, by moving its
    /// <see cref="TimelineSegment.Start"/> forward to absorb the duration change.
    /// </summary>
    /// <remarks>
    /// Trimming only ever rewrites <see cref="TimelineSegment.Duration"/> (plus the source
    /// range). On the base chain that is enough, because
    /// <see cref="TimelineModel.RecalculateSegmentPositions"/> re-derives every base
    /// <see cref="TimelineSegment.Start"/> afterwards and the timeline ripples. An overlay
    /// segment's start is authored, so the re-flow deliberately leaves it alone — which meant
    /// a left-edge trim held the start still and pulled the RIGHT edge in instead. When that
    /// overlay was the last-ending segment it also shortened
    /// <see cref="TimelineModel.TotalSegmentsDuration"/>, so the whole scene got shorter
    /// while the user was dragging the other end of the clip.
    /// Start is clamped at zero; a head pulled out past the timeline origin can only extend
    /// the out-point, since there is nowhere further left to go.
    /// </remarks>
    private void AnchorOverlayOutPoint(TimelineSegment trimmed, TimelineSegment previous)
    {
        if (!_fromStart || previous.TrackIndex == TimelineModel.BaseTrackIndex) return;

        var outPoint = previous.Start + previous.Duration;
        var newStart = outPoint - trimmed.Duration;
        trimmed.Start = newStart < TimeSpan.Zero ? TimeSpan.Zero : newStart;
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

    protected override bool UndoCore(TimelineModel model)
    {
        if (_previous is null || _index < 0) return false;
        if (_index >= model.Segments.Count) return false;
        model.Segments[_index] = _previous;
        return true;
    }
}
