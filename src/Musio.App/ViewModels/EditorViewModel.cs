using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musio.Core.Timeline;
using Musio_App.Services;

namespace Musio_App.ViewModels;

public partial class EditorViewModel : ObservableObject
{
    private TimelineModel _model;
    private UndoRedoManager _undoRedoManager;

    public EditorViewModel()
    {
        var timeline = ProjectService.Instance.CurrentTimeline;
        _model = timeline ?? new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(30),
            TrimEnd = TimeSpan.FromSeconds(30)
        };
        _undoRedoManager = new UndoRedoManager(_model);
        _undoRedoManager.StateChanged += OnUndoRedoStateChanged;

        ProjectService.Instance.ProjectChanged += OnProjectChanged;
    }

    public TimelineModel Model => _model;
    public UndoRedoManager UndoRedoManager => _undoRedoManager;

    /// <summary>
    /// Raised when the underlying TimelineModel is swapped (e.g. new project loaded).
    /// </summary>
    public event EventHandler? ModelReloaded;

    [ObservableProperty]
    private double _selectedSpeed = 1.0;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private string _undoDescription = string.Empty;

    [ObservableProperty]
    private string _redoDescription = string.Empty;

    [ObservableProperty]
    private string _undoTooltip = "Nothing to undo (Ctrl+Z)";

    [ObservableProperty]
    private string _redoTooltip = "Nothing to redo (Ctrl+Y)";

    [RelayCommand]
    private void Undo()
    {
        _undoRedoManager.Undo();
    }

    [RelayCommand]
    private void Redo()
    {
        _undoRedoManager.Redo();
    }

    [RelayCommand]
    private void ApplySpeed()
    {
        var start = _model.TrimStart;
        var end = _model.TrimEnd > TimeSpan.Zero ? _model.TrimEnd : _model.Duration;
        if (end <= start) return;

        var operation = new SpeedChangeOperation(start, end, SelectedSpeed);
        _undoRedoManager.Execute(operation);
    }

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        var position = _model.PlayheadPosition;
        if (position <= TimeSpan.Zero || position >= _model.Duration) return;

        var operation = new SplitOperation(position);
        _undoRedoManager.Execute(operation);
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var playhead = _model.PlayheadPosition;
        int clipIndex = _model.Clips.FindIndex(c => playhead >= c.Start && playhead < c.End);
        if (clipIndex < 0) return;

        var operation = new DeleteSegmentOperation(clipIndex);
        _undoRedoManager.Execute(operation);
    }

    [RelayCommand]
    private void CutSelection()
    {
        var start = _model.TrimStart;
        var end = _model.TrimEnd > TimeSpan.Zero ? _model.TrimEnd : _model.Duration;
        if (end <= start) return;

        var operation = new CutOperation(start, end);
        _undoRedoManager.Execute(operation);
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        var timeline = ProjectService.Instance.CurrentTimeline;
        if (timeline is null) return;

        _undoRedoManager.StateChanged -= OnUndoRedoStateChanged;

        _model = timeline;
        _undoRedoManager = new UndoRedoManager(_model);
        _undoRedoManager.StateChanged += OnUndoRedoStateChanged;

        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(UndoRedoManager));
        OnUndoRedoStateChanged(this, EventArgs.Empty);
        ModelReloaded?.Invoke(this, EventArgs.Empty);
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        CanUndo = _undoRedoManager.CanUndo;
        CanRedo = _undoRedoManager.CanRedo;
        UndoDescription = _undoRedoManager.UndoDescription ?? string.Empty;
        RedoDescription = _undoRedoManager.RedoDescription ?? string.Empty;
        UndoTooltip = CanUndo ? $"Undo: {UndoDescription} (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)";
        RedoTooltip = CanRedo ? $"Redo: {RedoDescription} (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)";
    }
}
