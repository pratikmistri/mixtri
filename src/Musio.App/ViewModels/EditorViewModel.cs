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
        _model = timeline ?? TimelineModel.CreateEmpty();
        _undoRedoManager = new UndoRedoManager(_model);
        _undoRedoManager.StateChanged += OnUndoRedoStateChanged;
        _undoRedoManager.EditPerformed += OnEditPerformed;

        ProjectService.Instance.ProjectChanged += OnProjectChanged;
    }

    public TimelineModel Model => _model;
    public UndoRedoManager UndoRedoManager => _undoRedoManager;

    /// <summary>
    /// Raised when the underlying TimelineModel is swapped (e.g. new project loaded).
    /// </summary>
    public event EventHandler? ModelReloaded;

    [ObservableProperty]
    private int? _selectedClipIndex;

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
    private void SplitAtPlayhead()
    {
        var position = _model.PlayheadPosition;

        // Segment-based timeline: split the segment under the playhead.
        if (_model.Segments.Count > 0)
        {
            if (position <= TimeSpan.Zero || position >= _model.DisplayDuration) return;
            var splitOp = new SplitSegmentAtTimeOperation(position);
            _undoRedoManager.Execute(splitOp);
            return;
        }

        // Legacy clip-based timeline.
        if (position <= TimeSpan.Zero || position >= _model.Duration) return;
        var operation = new SplitOperation(position);
        _undoRedoManager.Execute(operation);
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_model.Clips.Count == 0) return;

        // If a clip is selected, delete it
        if (SelectedClipIndex is { } selectedIdx && selectedIdx >= 0 && selectedIdx < _model.Clips.Count)
        {
            var operation = new DeleteSegmentOperation(selectedIdx);
            _undoRedoManager.Execute(operation);
            SelectedClipIndex = null;
            return;
        }

        // Otherwise delete clip at playhead
        var playhead = _model.PlayheadPosition;
        int clipIndex = _model.Clips.FindIndex(c => playhead >= c.Start && playhead < c.End);
        if (clipIndex < 0) return;

        var op = new DeleteSegmentOperation(clipIndex);
        _undoRedoManager.Execute(op);
    }

    [RelayCommand]
    private void CutSelection()
    {
        // Legacy clip-based timeline only. The segment editor never populates Clips, and the
        // fabricated clip below would switch the timeline into legacy clip rendering — the
        // page routes a segment-timeline cut to the segment delete path instead.
        if (_model.Segments.Count > 0) return;

        // ...and an EMPTY timeline has nothing to cut. Fabricating a clip there spanned the
        // whole nominal ruler (TimelineModel.CreateEmpty's 30s), so the ripple delete that
        // followed took Duration to zero — and every track draw pass bails on a non-positive
        // DisplayDuration, blanking the ruler, the tracks and the "record or import" prompt.
        // Undo was worse: the snapshot is captured AFTER the fabrication, so it restored a
        // solid legacy clip block for footage that does not exist. The empty editor is a
        // first-class destination on this branch (fresh launch, last clip deleted, Close
        // project), so this is reachable from one click on the toolbar's Cut button.
        if (_model.Clips.Count == 0) return;

        var playhead = _model.PlayheadPosition;
        int clipIndex = _model.Clips.FindIndex(c => playhead >= c.Start && playhead < c.End);
        if (clipIndex < 0) return;

        var operation = new RippleDeleteOperation(clipIndex);
        _undoRedoManager.Execute(operation);
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        var timeline = ProjectService.Instance.CurrentTimeline;
        if (timeline is null) return;

        _undoRedoManager.StateChanged -= OnUndoRedoStateChanged;
        _undoRedoManager.EditPerformed -= OnEditPerformed;

        _model = timeline;
        _undoRedoManager = new UndoRedoManager(_model);
        _undoRedoManager.StateChanged += OnUndoRedoStateChanged;
        _undoRedoManager.EditPerformed += OnEditPerformed;

        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(UndoRedoManager));
        OnUndoRedoStateChanged(this, EventArgs.Empty);
        ModelReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every undoable timeline edit funnels through here, which is what makes the
    /// unsaved-changes prompt on window close reliable without each of the ~50 call sites
    /// having to remember to flag it.
    /// </summary>
    private void OnEditPerformed(object? sender, EventArgs e) =>
        ProjectService.Instance.MarkDirty();

    /// <summary>
    /// Unsubscribes from singleton and long-lived event sources to prevent
    /// this VM from being kept alive after the page is unloaded.
    /// </summary>
    public void Cleanup()
    {
        ProjectService.Instance.ProjectChanged -= OnProjectChanged;
        _undoRedoManager.StateChanged -= OnUndoRedoStateChanged;
        _undoRedoManager.EditPerformed -= OnEditPerformed;
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
