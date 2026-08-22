using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.Services;

namespace Musio_App.Pages;

/// <summary>
/// Cursor reposition mode: drag the rendered pointer to a new place at the current moment.
/// </summary>
/// <remarks>
/// <para>
/// The whole interaction authors a single <see cref="CursorAnchor"/>. The recorded journey is
/// never rewritten — <see cref="CursorPathWarp"/> displaces it around the anchor and blends
/// back to the recording before the neighbouring clicks, so everything outside that window
/// plays exactly as it was captured.
/// </para>
/// <para>
/// Two rules this file exists to keep:
/// </para>
/// <list type="bullet">
///   <item><b>One undo entry per gesture.</b> The drag live-updates the renderer directly and
///   commits to <see cref="Musio.Core.Timeline.UndoRedoManager"/> once, on release. Pushing an
///   operation per pointer sample would bury the timeline in undo steps.</item>
///   <item><b>Never re-derive the pointer's position here.</b> The handle starts from
///   <see cref="PreviewRenderer.TryGetCursorPosition"/>, which is the same value the frame is
///   drawn with, so the handle cannot drift from the cursor under it.</item>
/// </list>
/// </remarks>
public sealed partial class EditorPage
{
    private bool _cursorAnchorEditMode;
    private bool _isDraggingCursorAnchor;

    /// <summary>Source recording the anchor belongs to; null means the primary recording.</summary>
    private string? _cursorAnchorSourceFile;

    private double _cursorAnchorSourceW;
    private double _cursorAnchorSourceH;

    /// <summary>The moment being edited, in the owning recording's SOURCE time.</summary>
    private TimeSpan _cursorAnchorSourceTime;

    /// <summary>Output time the playhead sat at when the mode was entered, for re-rendering.</summary>
    private TimeSpan _cursorAnchorOutputTime;

    /// <summary>
    /// Id of the anchor being edited, or null when this moment has no anchor yet — in which
    /// case the first completed drag creates one.
    /// </summary>
    private string? _cursorAnchorId;

    /// <summary>Live (uncommitted) normalized position while a drag is in flight.</summary>
    private double _cursorAnchorNormX;
    private double _cursorAnchorNormY;

    /// <summary>
    /// How close, in source time, an existing anchor has to be to the playhead before entering
    /// the mode edits it rather than creating a second one beside it. Anchors closer together
    /// than this would fight each other for the same frame.
    /// </summary>
    private static readonly TimeSpan CursorAnchorPickTolerance = TimeSpan.FromMilliseconds(120);

    // ─── Entering / leaving the mode ────────────────────────────────────

    private void CursorAnchorEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_cursorAnchorEditMode)
        {
            ExitCursorAnchorEditMode();
            return;
        }

        EnterCursorAnchorEditMode();
    }

    private void EnterCursorAnchorEditMode()
    {
        if (CursorAnchorUnavailableText is not null)
            CursorAnchorUnavailableText.Visibility = Visibility.Collapsed;

        // The two preview overlays share a grid cell and both take pointer input, so the one
        // declared later would silently swallow the other's drags. Only one can be live.
        if (_zoomRegionEditMode) ExitZoomRegionEditMode();

        if (!TryBindCursorAnchorToPlayhead(out string? reason))
        {
            ShowCursorAnchorUnavailable(reason!);
            return;
        }

        _cursorAnchorEditMode = true;

        Preview.Pause();
        CursorAnchorOverlay.Visibility = Visibility.Visible;
        UpdateCursorAnchorChrome();
        UpdateCursorAnchorHandle();
    }

    /// <summary>
    /// Points the overlay at whatever moment the playhead is on now: the owning recording, its
    /// source time, its dimensions, and the anchor (if any) that already claims that moment.
    /// </summary>
    /// <remarks>
    /// This runs on entry AND every time the playhead moves while the mode is open. Snapshotting
    /// it once would make the sticky "Reposition cursor" toggle actively dangerous: after
    /// scrubbing, the next drag would relocate the FIRST anchor instead of creating one at the
    /// new moment, and the forced re-render would yank the preview back to the old position
    /// while the playhead stayed where the user put it.
    /// </remarks>
    private bool TryBindCursorAnchorToPlayhead(out string? unavailableReason)
    {
        unavailableReason = null;

        if (GetPlayheadSourceTimeForOverlay() is not { } placement)
        {
            unavailableReason = "Move the playhead over a recording to reposition its cursor.";
            return false;
        }

        var (sourceFile, sourceTime) = placement;

        // An imported clip carries no cursor data, so there is no path to displace. The mode
        // would open onto a frame with no pointer on it and a handle pinned at the origin.
        if (RendererForSource(sourceFile) is not { } renderer
            || !renderer.TryGetCursorPosition(sourceTime, out double px, out double py))
        {
            unavailableReason = "This clip has no recorded cursor to reposition.";
            return false;
        }

        var project = ProjectService.Instance.CurrentProject;
        var owning = SegmentForSource(sourceFile);
        _cursorAnchorSourceW = owning is { SourceWidth: > 0 }
            ? owning.SourceWidth
            : (project?.Width > 0 ? project.Width : 1920);
        _cursorAnchorSourceH = owning is { SourceHeight: > 0 }
            ? owning.SourceHeight
            : (project?.Height > 0 ? project.Height : 1080);

        _cursorAnchorSourceFile = sourceFile;
        _cursorAnchorSourceTime = sourceTime;
        _cursorAnchorOutputTime = Timeline.PlayheadPosition;

        // Editing an anchor that already claims this moment, rather than stacking a second one
        // on top of it — two anchors on the same frame would each try to own the displacement.
        var existing = FindCursorAnchorNear(sourceFile, sourceTime);
        _cursorAnchorId = existing?.Id;

        if (existing is not null)
        {
            _cursorAnchorNormX = existing.X;
            _cursorAnchorNormY = existing.Y;
        }
        else
        {
            _cursorAnchorNormX = Math.Clamp(px / _cursorAnchorSourceW, 0, 1);
            _cursorAnchorNormY = Math.Clamp(py / _cursorAnchorSourceH, 0, 1);
        }

        return true;
    }

    /// <summary>
    /// Re-binds the overlay after a scrub. Called from the preview's per-draw layout hook, so it
    /// stays cheap: everything below the playhead comparison is skipped while the playhead is
    /// where the overlay already thinks it is (including the forced re-renders this file issues
    /// itself, which always ask for <see cref="_cursorAnchorOutputTime"/>).
    /// </summary>
    private void RebindCursorAnchorIfPlayheadMoved()
    {
        if (!_cursorAnchorEditMode || _isDraggingCursorAnchor) return;
        if (Timeline.PlayheadPosition == _cursorAnchorOutputTime) return;

        if (!TryBindCursorAnchorToPlayhead(out string? reason))
        {
            // Scrubbed onto a text slide or an imported clip: there is nothing to reposition
            // there, so close rather than leave a handle floating over unrelated footage.
            ExitCursorAnchorEditMode();
            ShowCursorAnchorUnavailable(reason!);
            return;
        }

        UpdateCursorAnchorChrome();
    }

    private void ExitCursorAnchorEditMode()
    {
        _cursorAnchorEditMode = false;
        _isDraggingCursorAnchor = false;
        _cursorAnchorId = null;

        if (CursorAnchorOverlay is not null)
            CursorAnchorOverlay.Visibility = Visibility.Collapsed;

        // The renderer may still be holding the provisional anchor list from a drag that was
        // cancelled rather than committed, so put the committed model back in charge. This has
        // to happen BEFORE the source file is cleared — the sync resolves its renderer from it.
        SyncCursorAnchorsForEditedSource();
        _cursorAnchorSourceFile = null;

        UpdateCursorAnchorToggleState();
    }

    private void CursorAnchorDone_Click(object sender, RoutedEventArgs e) => ExitCursorAnchorEditMode();

    private void CursorAnchorRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_cursorAnchorId is not { } id) return;

        ViewModel.UndoRedoManager.Execute(new RemoveCursorAnchorOperation(id));
        _cursorAnchorId = null;

        SyncCursorAnchorsForEditedSource();
        RefreshCursorAnchorPreview();

        // Back to wherever the recording actually put the pointer, which is now the position a
        // fresh drag would start from.
        ResetCursorAnchorPositionFromRecording();

        UpdateCursorAnchorChrome();
        UpdateCursorAnchorHandle();
    }

    // ─── Dragging ───────────────────────────────────────────────────────

    private void CursorAnchorCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_cursorAnchorEditMode) return;

        _isDraggingCursorAnchor = true;
        CursorAnchorCanvas.CapturePointer(e.Pointer);

        // Pressing anywhere moves the cursor there: the handle is a target, not a grab-only
        // widget, and requiring a precise grab on a 34px ring makes small corrections fiddly.
        ApplyCursorAnchorDrag(e.GetCurrentPoint(CursorAnchorCanvas).Position);
        e.Handled = true;
    }

    private void CursorAnchorCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cursorAnchorEditMode || !_isDraggingCursorAnchor) return;

        ApplyCursorAnchorDrag(e.GetCurrentPoint(CursorAnchorCanvas).Position);
        e.Handled = true;
    }

    private void CursorAnchorCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_cursorAnchorEditMode) return;

        CursorAnchorCanvas.ReleasePointerCapture(e.Pointer);
        CommitCursorAnchorDrag();
        e.Handled = true;
    }

    /// <summary>
    /// A lost capture (a system gesture, the window deactivating) has to commit too — the
    /// renderer is already showing the dragged position, so dropping it here would leave the
    /// preview and the model disagreeing until the next rebuild.
    /// </summary>
    private void CursorAnchorCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_cursorAnchorEditMode) return;

        CommitCursorAnchorDrag();
    }

    private void CursorAnchorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_cursorAnchorEditMode) UpdateCursorAnchorHandle();
    }

    /// <summary>
    /// Live-updates the renderer as the pointer moves, WITHOUT touching the model. The
    /// provisional anchor list is what makes the drag feel direct; the undo entry is deferred
    /// to <see cref="CommitCursorAnchorDrag"/>.
    /// </summary>
    private void ApplyCursorAnchorDrag(Windows.Foundation.Point point)
    {
        if (!TryCanvasPointToNormalizedSource(point, out double nx, out double ny)) return;

        _cursorAnchorNormX = nx;
        _cursorAnchorNormY = ny;

        if (RendererForSource(_cursorAnchorSourceFile) is { } renderer)
            renderer.UpdateCursorAnchors(BuildProvisionalCursorAnchors());

        UpdateCursorAnchorHandle();
        RefreshCursorAnchorPreview();
    }

    /// <summary>
    /// Turns the in-flight drag into exactly one undo entry.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent on purpose.</b> <c>ReleasePointerCapture</c> raises
    /// <c>PointerCaptureLost</c> synchronously, so a normal release reaches this method twice —
    /// once from the capture-lost handler and once from the release handler. Without the guard
    /// living HERE (rather than in the callers) the second pass would see the id the first pass
    /// just created and push a redundant <see cref="MoveCursorAnchorOperation"/>, leaving every
    /// drag with two undo entries and a first Ctrl+Z that appears to do nothing. This mirrors
    /// <c>FinalizeTextDrag</c>, which is guarded the same way for the same reason.
    /// </remarks>
    private void CommitCursorAnchorDrag()
    {
        if (!_isDraggingCursorAnchor) return;
        _isDraggingCursorAnchor = false;

        if (_cursorAnchorId is { } id)
        {
            ViewModel.UndoRedoManager.Execute(
                new MoveCursorAnchorOperation(id, _cursorAnchorNormX, _cursorAnchorNormY));
        }
        else
        {
            var add = new AddCursorAnchorOperation(
                _cursorAnchorSourceTime, _cursorAnchorNormX, _cursorAnchorNormY, _cursorAnchorSourceFile);
            ViewModel.UndoRedoManager.Execute(add);
            _cursorAnchorId = add.CreatedId;
        }

        // Hand the renderer back to the committed model, so undo/redo and any later rebuild
        // resolve the same anchors this drag just produced.
        SyncCursorAnchorsForEditedSource();
        RefreshCursorAnchorPreview();
        UpdateCursorAnchorChrome();
    }

    // ─── Geometry ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps a point on the overlay canvas to normalized source coordinates through the
    /// compositor's own transform, so a drag lands where the user aimed under every
    /// aspect-ratio, fit-mode and padding combination.
    /// </summary>
    private bool TryCanvasPointToNormalizedSource(Windows.Foundation.Point point, out double nx, out double ny)
    {
        nx = ny = 0;

        if (!TryComputeComposedSourceLayout(
                _cursorAnchorSourceFile, _cursorAnchorSourceW, _cursorAnchorSourceH,
                out double fx, out double fy, out double fw, out double fh)
            || fw <= 0 || fh <= 0)
        {
            return false;
        }

        nx = Math.Clamp((point.X - fx) / fw, 0, 1);
        ny = Math.Clamp((point.Y - fy) / fh, 0, 1);
        return true;
    }

    private void UpdateCursorAnchorHandle()
    {
        if (!_cursorAnchorEditMode || CursorAnchorHandle is null || CursorAnchorHandleDot is null) return;

        if (!TryComputeComposedSourceLayout(
                _cursorAnchorSourceFile, _cursorAnchorSourceW, _cursorAnchorSourceH,
                out double fx, out double fy, out double fw, out double fh)
            || fw <= 0 || fh <= 0)
        {
            // The compositor has not drawn yet. Hiding rather than parking the handle at the
            // origin avoids pointing at a place the cursor is not.
            CursorAnchorHandle.Visibility = Visibility.Collapsed;
            CursorAnchorHandleDot.Visibility = Visibility.Collapsed;
            return;
        }

        double x = fx + (_cursorAnchorNormX * fw);
        double y = fy + (_cursorAnchorNormY * fh);

        Canvas.SetLeft(CursorAnchorHandle, x - (CursorAnchorHandle.Width / 2));
        Canvas.SetTop(CursorAnchorHandle, y - (CursorAnchorHandle.Height / 2));
        Canvas.SetLeft(CursorAnchorHandleDot, x - (CursorAnchorHandleDot.Width / 2));
        Canvas.SetTop(CursorAnchorHandleDot, y - (CursorAnchorHandleDot.Height / 2));

        CursorAnchorHandle.Visibility = Visibility.Visible;
        CursorAnchorHandleDot.Visibility = Visibility.Visible;
    }

    // ─── Model helpers ──────────────────────────────────────────────────

    private CursorAnchor? FindCursorAnchorNear(string? sourceFile, TimeSpan sourceTime)
    {
        CursorAnchor? best = null;
        var bestGap = CursorAnchorPickTolerance;

        foreach (var anchor in ViewModel.Model.CursorAnchors)
        {
            if (!string.Equals(anchor.SourceVideoFilePath, sourceFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var gap = anchor.Timestamp - sourceTime;
            if (gap < TimeSpan.Zero) gap = gap.Negate();
            if (gap > bestGap) continue;

            bestGap = gap;
            best = anchor;
        }

        return best;
    }

    /// <summary>
    /// The committed anchors for the edited source, with the in-flight drag substituted in.
    /// </summary>
    private List<CursorAnchor> BuildProvisionalCursorAnchors()
    {
        var anchors = new List<CursorAnchor>();

        foreach (var anchor in ViewModel.Model.CursorAnchors)
        {
            if (!string.Equals(anchor.SourceVideoFilePath, _cursorAnchorSourceFile, StringComparison.OrdinalIgnoreCase))
                continue;

            anchors.Add(anchor.Id == _cursorAnchorId
                ? anchor with { X = _cursorAnchorNormX, Y = _cursorAnchorNormY }
                : anchor);
        }

        if (_cursorAnchorId is null)
        {
            anchors.Add(new CursorAnchor
            {
                Timestamp = _cursorAnchorSourceTime,
                X = _cursorAnchorNormX,
                Y = _cursorAnchorNormY,
                SourceVideoFilePath = _cursorAnchorSourceFile,
            });
        }

        anchors.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return anchors;
    }

    private void SyncCursorAnchorsForEditedSource()
    {
        if (RendererForSource(_cursorAnchorSourceFile) is { } renderer)
            SyncCursorAnchorsToRenderer(renderer, _cursorAnchorSourceFile);
    }

    /// <summary>
    /// Re-reads the edited anchor from the model. Called from the undo/redo handler.
    /// </summary>
    /// <remarks>
    /// Undo/redo does not change the anchor SELECTION, so nothing else pushes the reverted
    /// value back onto the overlay — the same gap <c>SyncZoomUI</c> exists to close. Leaving it
    /// stale is worse than cosmetic here: the handle would sit where the undone drag put it,
    /// and the next drag would commit from a position the user is not looking at.
    /// <para>
    /// Note this deliberately does NOT leave the mode. A commit raises
    /// <c>UndoRedoManager.StateChanged</c> too, so exiting here would slam the overlay shut at
    /// the end of every successful drag.
    /// </para>
    /// </remarks>
    private void SyncCursorAnchorUI()
    {
        if (!_cursorAnchorEditMode) return;

        if (_cursorAnchorId is { } id)
        {
            var anchor = ViewModel.Model.CursorAnchors.FirstOrDefault(a => a.Id == id);
            if (anchor is null)
            {
                // Undone out of existence: fall back to the recorded path, which is what a
                // fresh drag would now start from.
                _cursorAnchorId = null;
                ResetCursorAnchorPositionFromRecording();
            }
            else
            {
                _cursorAnchorNormX = anchor.X;
                _cursorAnchorNormY = anchor.Y;
            }
        }
        else
        {
            // A redo can bring back an anchor at this exact moment; adopt it rather than
            // creating a second one on top of it with the next drag.
            _cursorAnchorId = FindCursorAnchorNear(_cursorAnchorSourceFile, _cursorAnchorSourceTime)?.Id;
        }

        UpdateCursorAnchorChrome();
        UpdateCursorAnchorHandle();
    }

    /// <summary>
    /// Moves the handle back to wherever the pipeline currently puts the cursor at this moment.
    /// </summary>
    private void ResetCursorAnchorPositionFromRecording()
    {
        if (RendererForSource(_cursorAnchorSourceFile) is not { } renderer) return;
        if (!renderer.TryGetCursorPosition(_cursorAnchorSourceTime, out double px, out double py)) return;
        if (_cursorAnchorSourceW <= 0 || _cursorAnchorSourceH <= 0) return;

        _cursorAnchorNormX = Math.Clamp(px / _cursorAnchorSourceW, 0, 1);
        _cursorAnchorNormY = Math.Clamp(py / _cursorAnchorSourceH, 0, 1);
    }

    private void RefreshCursorAnchorPreview()
    {
        // Force: the playhead has not moved, so the cached-frame check would skip the redraw
        // and the cursor would only jump on the next unrelated invalidation.
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(_cursorAnchorOutputTime, force: true);
    }

    // ─── Timeline marker interaction ────────────────────────────────────

    /// <summary>
    /// Clicking an anchor marker on the Cursor track seeks to it and opens the reposition
    /// overlay on that anchor, so the marker is a way back into the edit rather than just a
    /// read-only indicator.
    /// </summary>
    private void OnCursorAnchorClicked(object? sender, CursorAnchor anchor)
    {
        if (_cursorAnchorEditMode) ExitCursorAnchorEditMode();

        // Same source-time → output-time mapping the zoom region editor uses, so the playhead
        // lands on the frame the marker is drawn over even with slides, trims and speed changes
        // in front of it.
        var position = ResolveSourceOutputTime(anchor.SourceVideoFilePath, anchor.Timestamp);

        Preview.Pause();
        Timeline.PlayheadPosition = position;
        Preview.PlayheadPosition = position;
        ViewModel.Model.PlayheadPosition = position;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Cursor, true);
        PropertiesPanel.ShowPane(PropertyPaneKind.Cursor);

        EnterCursorAnchorEditMode();
    }

    // ─── Pane chrome ────────────────────────────────────────────────────

    private void UpdateCursorAnchorChrome()
    {
        if (CursorAnchorRemoveButton is not null)
            CursorAnchorRemoveButton.IsEnabled = _cursorAnchorId is not null;

        if (CursorAnchorHintText is not null)
        {
            CursorAnchorHintText.Text = _cursorAnchorId is null
                ? "Drag the cursor to move it at this moment"
                : "Drag to adjust — the recorded path eases back before the next click";
        }

        UpdateCursorAnchorToggleState();
    }

    /// <summary>
    /// Keeps the pane's toggle in step with the mode, and disables it entirely for cursor types
    /// that do not draw the recorded path: Touch renders at raw click points and Hidden renders
    /// nothing, so an anchor would be authored against something invisible.
    /// </summary>
    private void UpdateCursorAnchorToggleState()
    {
        if (CursorAnchorEditButton is null) return;

        var config = ProjectService.Instance.CurrentComposition;
        var type = (SelectedVideoSegment?.CursorStyleOverride ?? config?.Cursor)?.Type ?? CursorType.Default;
        bool drawsPath = type is not (CursorType.Touch or CursorType.Hidden);

        CursorAnchorEditButton.IsEnabled = drawsPath;
        CursorAnchorEditButton.Content = _cursorAnchorEditMode ? "Done repositioning" : "Reposition cursor";

        if (!drawsPath && _cursorAnchorEditMode)
            ExitCursorAnchorEditMode();
    }

    private void ShowCursorAnchorUnavailable(string message)
    {
        if (CursorAnchorUnavailableText is null) return;

        CursorAnchorUnavailableText.Text = message;
        CursorAnchorUnavailableText.Visibility = Visibility.Visible;
    }
}
