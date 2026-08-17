using Musio.Core.Processing;

namespace Musio.Core.Timeline;

/// <summary>
/// Splits one freshly recorded video segment around detected typing ranges and accelerates
/// only those source slices. Generated accelerated slices mute their bound recording audio
/// by default so system sound and narration do not acquire abrupt speed changes.
/// </summary>
public sealed class AutomaticTypingSpeedOperation : SegmentEditOperationBase
{
    public const double TypingSpeed = 1.5;

    private readonly string _segmentId;
    private readonly IReadOnlyList<TypingActivityRange> _ranges;
    private readonly bool _muteAcceleratedAudio;
    private readonly List<string> _generatedPieceIds = [];
    private SegmentListSnapshot? _snapshot;

    public override string Description => "Accelerate Typing";

    public AutomaticTypingSpeedOperation(
        string segmentId,
        IReadOnlyList<TypingActivityRange> ranges,
        bool muteAcceleratedAudio = true)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        _muteAcceleratedAudio = muteAcceleratedAudio;
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        int index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (index < 0 || model.Segments[index] is not VideoSegment video)
            return NothingToDo();

        double originalSpeed = video.SpeedFactor > 0 ? video.SpeedFactor : 1.0;
        if (Math.Abs(originalSpeed - 1.0) > 0.001 || video.SourceDuration <= TimeSpan.Zero)
            return NothingToDo();

        var ranges = NormalizeRanges(video);
        if (ranges.Count == 0)
            return NothingToDo();

        _snapshot = SegmentListSnapshot.Capture(model);

        var pieces = new List<TimelineSegment>();
        TimeSpan sourceCursor = video.SourceStart;
        TimeSpan outputCursor = video.Start;
        bool isFirst = true;
        int generatedIdIndex = 0;

        foreach (var range in ranges)
        {
            if (range.Start > sourceCursor)
                AddPiece(sourceCursor, range.Start, accelerated: false);

            AddPiece(range.Start, range.End, accelerated: true);
            sourceCursor = range.End;
        }

        var sourceEnd = video.SourceStart + video.SourceDuration;
        if (sourceCursor < sourceEnd)
            AddPiece(sourceCursor, sourceEnd, accelerated: false);

        model.Segments.RemoveAt(index);
        model.Segments.InsertRange(index, pieces);
        return true;

        void AddPiece(TimeSpan sourceStart, TimeSpan sourceEnd, bool accelerated)
        {
            var sourceDuration = sourceEnd - sourceStart;
            if (sourceDuration <= TimeSpan.Zero) return;

            double speed = accelerated ? TypingSpeed : originalSpeed;
            var outputDuration = TimeSpan.FromTicks((long)(sourceDuration.Ticks / speed));
            string pieceId;
            if (isFirst)
            {
                pieceId = video.Id;
            }
            else
            {
                if (generatedIdIndex == _generatedPieceIds.Count)
                    _generatedPieceIds.Add(Guid.NewGuid().ToString("N"));
                pieceId = _generatedPieceIds[generatedIdIndex++];
            }

            var piece = video with
            {
                Id = pieceId,
                Start = outputCursor,
                SourceStart = sourceStart,
                SourceDuration = sourceDuration,
                Duration = outputDuration,
                SpeedFactor = speed,
                AudioMode = accelerated && _muteAcceleratedAudio
                    ? SegmentAudioMode.Muted
                    : video.AudioMode,
                InTransition = isFirst ? video.InTransition : null,
            };

            pieces.Add(piece);
            outputCursor += outputDuration;
            isFirst = false;
        }
    }

    private List<TypingActivityRange> NormalizeRanges(VideoSegment video)
    {
        var sourceStart = video.SourceStart;
        var sourceEnd = video.SourceStart + video.SourceDuration;
        var minimumNormalSlice = SplitSegmentAtTimeOperation.MinHalf;
        var minimumAcceleratedSource = TimeSpan.FromTicks(
            (long)Math.Ceiling(SplitSegmentAtTimeOperation.MinHalf.Ticks * TypingSpeed));
        var normalized = new List<TypingActivityRange>();

        foreach (var candidate in _ranges.OrderBy(r => r.Start))
        {
            var start = candidate.Start < sourceStart ? sourceStart : candidate.Start;
            var end = candidate.End > sourceEnd ? sourceEnd : candidate.End;
            if (end <= start) continue;

            if (start - sourceStart < minimumNormalSlice)
                start = sourceStart;
            if (sourceEnd - end < minimumNormalSlice)
                end = sourceEnd;
            if (end - start < minimumAcceleratedSource)
                continue;

            if (normalized.Count > 0 && start - normalized[^1].End < minimumNormalSlice)
            {
                normalized[^1] = normalized[^1] with
                {
                    End = TimeSpan.FromTicks(Math.Max(normalized[^1].End.Ticks, end.Ticks)),
                };
            }
            else
            {
                normalized.Add(new TypingActivityRange(start, end));
            }
        }

        return normalized;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        if (_snapshot is null) return false;
        _snapshot.Restore(model);
        return false;
    }
}
