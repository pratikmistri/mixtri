namespace Musio.Core.Timeline;

/// <summary>
/// Base class for edit operations that mutate <see cref="TimelineModel.Segments"/> and must
/// therefore re-flow the primary track afterwards
/// (<see cref="TimelineModel.RecalculateSegmentPositions"/>). Centralizing the call here means a
/// segment-mutating operation can no longer forget it — a missed call previously corrupted
/// segment Start/End positions silently, with nothing to catch it at compile time.
///
/// <see cref="ExecuteCore"/>/<see cref="UndoCore"/> return whether they actually mutated the
/// segment list; the base class only recalculates when they did. This preserves each operation's
/// existing "nothing to do" guard clauses (e.g. an id/index that no longer resolves) exactly as
/// before — converting to this base class does not add a recalculate call on a path that
/// previously skipped it.
/// </summary>
public abstract class SegmentEditOperationBase : IEditOperation
{
    public abstract string Description { get; }

    public void Execute(TimelineModel model)
    {
        if (ExecuteCore(model))
            model.RecalculateSegmentPositions();
    }

    public void Undo(TimelineModel model)
    {
        if (UndoCore(model))
            model.RecalculateSegmentPositions();
    }

    /// <returns>True if the segment list was mutated and positions must be recalculated.</returns>
    protected abstract bool ExecuteCore(TimelineModel model);

    /// <returns>True if the segment list was restored and positions must be recalculated.</returns>
    protected abstract bool UndoCore(TimelineModel model);
}
