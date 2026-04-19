namespace Musio.Core.Timeline;

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

    public void Execute(IEditOperation operation)
    {
        operation.Execute(_model);
        _undoStack.Push(operation);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var operation = _undoStack.Pop();
        operation.Undo(_model);
        _redoStack.Push(operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var operation = _redoStack.Pop();
        operation.Execute(_model);
        _undoStack.Push(operation);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StateChanged;
}
