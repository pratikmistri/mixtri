namespace Musio.Core.Timeline;

/// <summary>
/// Base class for edit operations that mutate <see cref="TimelineModel.Segments"/> and must
/// therefore re-flow the primary track afterwards
/// (<see cref="TimelineModel.RecalculateSegmentPositions"/>). Centralizing the call here means a
/// segment-mutating operation can no longer forget it — a missed call previously corrupted
/// segment Start/End positions silently, with nothing to catch it at compile time.
///
/// <see cref="ExecuteCore"/>/<see cref="UndoCore"/> return whether the base class still needs to
/// call <see cref="TimelineModel.RecalculateSegmentPositions"/> — NOT whether the segment list
/// was mutated. The two usually coincide, but deliberately diverge in two cases:
/// <list type="bullet">
/// <item><description>
/// A "nothing to do" guard clause (an id or index that no longer resolves) returns <c>false</c>
/// because nothing changed. This preserves each operation's pre-existing early-out exactly —
/// converting to this base class does not add a recalculate call on a path that previously
/// skipped it.
/// </description></item>
/// <item><description>
/// An override that restores through <see cref="SegmentListSnapshot.Restore"/> also returns
/// <c>false</c>, even though it replaced the whole list, because <c>Restore</c> recalculates
/// internally; returning <c>true</c> would recalculate a second time for no reason.
/// </description></item>
/// </list>
/// So read the result as "a recalculate is still outstanding", and return <c>false</c> whenever
/// positions are already correct — whether that is because nothing moved or because something
/// downstream has already re-flowed them.
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

    /// <returns>
    /// True if the base class must call <see cref="TimelineModel.RecalculateSegmentPositions"/>.
    /// False if positions are already correct — either nothing was changed, or the override
    /// already re-flowed them (e.g. via <see cref="SegmentListSnapshot.Restore"/>).
    /// </returns>
    protected abstract bool ExecuteCore(TimelineModel model);

    /// <returns>
    /// True if the base class must call <see cref="TimelineModel.RecalculateSegmentPositions"/>.
    /// False if positions are already correct — either nothing was restored, or the override
    /// already re-flowed them (e.g. via <see cref="SegmentListSnapshot.Restore"/>).
    /// </returns>
    protected abstract bool UndoCore(TimelineModel model);
}
