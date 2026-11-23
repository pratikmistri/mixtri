namespace Mixtri.Core.Timeline;

public interface IEditOperation
{
    string Description { get; }
    void Execute(TimelineModel model);
    void Undo(TimelineModel model);

    /// <summary>
    /// Whether the most recent <see cref="Execute"/> actually changed the model.
    /// </summary>
    /// <remarks>
    /// Almost every operation resolves its target by id or index and returns silently when
    /// that target no longer exists. Such a run leaves the model untouched, so
    /// <see cref="UndoRedoManager"/> must neither push it (an undo entry that undoes nothing
    /// is indistinguishable from a broken Ctrl+Z) nor report an edit (which would mark a
    /// project dirty and prompt to save work that was never done).
    /// <para>
    /// Defaulted to <c>true</c> so an operation only opts in if it can actually no-op — and
    /// so the safe answer, "assume something changed", is the one you get by saying nothing.
    /// Note this is NOT the same question as
    /// <c>SegmentEditOperationBase.ExecuteCore</c>'s return value, which asks whether a
    /// re-flow is still outstanding: an operation that changes only non-positional state
    /// (<see cref="SetTextSlideTextWindowOperation"/>) answers false there and true here.
    /// </para>
    /// </remarks>
    bool ChangedModel => true;
}


/// <summary>
/// Optional capability for an operation that can absorb a later, closely related operation so a
/// continuous gesture (a slider drag, a colour-picker sweep) collapses into ONE undo entry.
/// </summary>
/// <remarks>
/// <see cref="UndoRedoManager.Execute"/> executes the new operation first, then — if the top of
/// the undo stack is mergeable, the redo stack is empty and the new operation changed the model —
/// offers it to <see cref="TryMergeWith"/>. On <c>true</c> the new operation is NOT pushed:
/// the receiver must have folded it in so that a single Undo restores the state from before the
/// first operation and a Redo re-applies the final state.
/// </remarks>
public interface IMergeableEditOperation : IEditOperation
{
    bool TryMergeWith(IEditOperation next);
}
