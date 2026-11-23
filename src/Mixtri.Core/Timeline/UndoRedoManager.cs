namespace Mixtri.Core.Timeline;

public class UndoRedoManager
{
    /// <summary>
    /// Default number of undo steps retained. Every operation holds on to whatever it needs to
    /// reverse itself (segment snapshots, removed records), so an uncapped stack grows for the
    /// whole editing session; past this depth the OLDEST entry is dropped.
    /// </summary>
    public const int DefaultMaxUndoDepth = 200;

    // First = top of stack. LinkedList rather than Stack<T> so the oldest entry (Last) can be
    // dropped in O(1) when the cap is reached — Stack<T> only exposes its top.
    private readonly LinkedList<IEditOperation> _undoStack = new();
    private readonly LinkedList<IEditOperation> _redoStack = new();
    private readonly TimelineModel _model;
    private readonly int _maxUndoDepth;

    public UndoRedoManager(TimelineModel model, int maxUndoDepth = DefaultMaxUndoDepth)
    {
        _model = model;
        _maxUndoDepth = Math.Max(1, maxUndoDepth);
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? UndoDescription => _undoStack.First?.Value.Description;
    public string? RedoDescription => _redoStack.First?.Value.Description;

    /// <summary>Number of operations currently on the undo stack (bounded by the depth cap).</summary>
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Applies an operation and records it for undo — unless it turned out to be a no-op, in
    /// which case nothing is pushed, the redo stack is left intact (a pending redo is still
    /// valid, since the model did not move) and no edit is reported.
    /// </summary>
    /// <remarks>
    /// If the operation throws, neither stack is touched and the exception propagates. The
    /// operation is deliberately NOT asked to <see cref="IEditOperation.Undo"/> itself as a
    /// rollback: operations capture their undo state part-way through <c>Execute</c> (a stored
    /// index, a removed record), so undoing one that threw BEFORE it mutated anything would
    /// act on stale or default state — e.g. remove a segment at index 0 that it never
    /// inserted — and turn a failed edit into silent corruption. An edit is still reported,
    /// because the model may have been partially changed: dirty tracking must assume the
    /// worst rather than let unsaved state be discarded without a prompt.
    /// </remarks>
    public void Execute(IEditOperation operation)
    {
        try
        {
            operation.Execute(_model);
        }
        catch
        {
            RaiseEventsAfterFailure();
            throw;
        }

        if (!operation.ChangedModel) return;

        Push(_undoStack, operation);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Undoes the most recent operation. It moves to the redo stack only once its
    /// <see cref="IEditOperation.Undo"/> has returned; if that throws it stays where it was, so
    /// the stacks never claim an undo happened that did not.
    /// </summary>
    public void Undo()
    {
        if (_undoStack.First is not { } node) return;
        var operation = node.Value;

        try
        {
            operation.Undo(_model);
        }
        catch
        {
            RaiseEventsAfterFailure();
            throw;
        }

        _undoStack.RemoveFirst();
        _redoStack.AddFirst(operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-applies the most recently undone operation; see <see cref="Undo"/>.</summary>
    public void Redo()
    {
        if (_redoStack.First is not { } node) return;
        var operation = node.Value;

        try
        {
            operation.Execute(_model);
        }
        catch
        {
            RaiseEventsAfterFailure();
            throw;
        }

        _redoStack.RemoveFirst();
        Push(_undoStack, operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Push(LinkedList<IEditOperation> stack, IEditOperation operation)
    {
        stack.AddFirst(operation);
        while (stack.Count > _maxUndoDepth)
            stack.RemoveLast();
    }

    /// <summary>
    /// Reports a possibly-partial change after an operation threw. A throwing handler must not
    /// replace the original exception, which is the one worth diagnosing.
    /// </summary>
    private void RaiseEventsAfterFailure()
    {
        // Guarded independently: a failing UI-state handler must never suppress the edit
        // signal that dirty tracking relies on.
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UndoRedoManager] StateChanged handler failed after an edit operation threw: {ex.Message}");
        }

        try
        {
            EditPerformed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UndoRedoManager] EditPerformed handler failed after an edit operation threw: {ex.Message}");
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised when the user actually changed the timeline — an execute, an undo or a redo.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="StateChanged"/>, which also fires from
    /// <see cref="Clear"/> so the undo/redo buttons refresh when a project is loaded.
    /// Unsaved-changes tracking must not treat that as an edit, or every freshly opened
    /// project would immediately look dirty. Note an undo back to the original state still
    /// counts as an edit here: this is a "something happened" signal, not a content hash.
    /// Also raised when an operation throws part-way, since the model may then be partially
    /// changed.
    /// </remarks>
    public event EventHandler? EditPerformed;
}
