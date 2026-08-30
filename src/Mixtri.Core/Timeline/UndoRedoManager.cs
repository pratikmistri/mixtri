namespace Mixtri.Core.Timeline;

public class UndoRedoManager
{
    private readonly Stack<IEditOperation> _undoStack = new();
    private readonly Stack<IEditOperation> _redoStack = new();
    private readonly TimelineModel _model;

    public UndoRedoManager(TimelineModel model)
    {
        _model = model;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    /// <summary>
    /// Applies an operation and records it for undo — unless it turned out to be a no-op, in
    /// which case nothing is pushed, the redo stack is left intact (a pending redo is still
    /// valid, since the model did not move) and no edit is reported.
    /// </summary>
    public void Execute(IEditOperation operation)
    {
        operation.Execute(_model);
        if (!operation.ChangedModel) return;

        _undoStack.Push(operation);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var operation = _undoStack.Pop();
        operation.Undo(_model);
        _redoStack.Push(operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var operation = _redoStack.Pop();
        operation.Execute(_model);
        _undoStack.Push(operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
        EditPerformed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
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
    /// </remarks>
    public event EventHandler? EditPerformed;
}
