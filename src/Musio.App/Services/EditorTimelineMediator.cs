using System;
using System.Collections.Generic;
using System.Linq;
using Musio.Core.Export;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.ViewModels;

namespace Musio_App.Services;

/// <summary>
/// Owns the timeline-control ↔ undo/redo ↔ renderer wiring that has no dependency on
/// EditorPage's XAML-bound property panels. This is a deliberately partial extraction:
/// <c>EditorPage.Timeline.cs</c> still owns every handler that reads or writes a named
/// XAML control (selection sync, zoom-region editing, camera/text-overlay property
/// panes, transition panes) because those need direct access to page-only state and
/// controls that cannot be passed here without turning this class into a stand-in for
/// the page itself. What moved here are the two genuinely decoupled pieces:
/// <list type="bullet">
/// <item>The "construct an edit operation, execute it, done" glue for the primary
/// track, camera track, zoom track and text-overlay track move/resize gestures — none
/// of these touch a XAML control beyond <see cref="TimelineControl"/> itself.</item>
/// <item>The model → renderer sync helpers (<see cref="SyncZoomStateToRenderer"/>,
/// <see cref="SyncTextOverlaysToRenderer"/>, <see cref="SyncSegmentZoomStateToRenderers"/>,
/// plus their shared <see cref="ManualKeyframesForSource"/>/<see cref="SegmentForSource"/>
/// helpers) — these are pure functions of the model and a renderer reference, so they
/// take both as parameters rather than owning <c>_previewRenderer</c>/<c>_segmentPreviews</c>,
/// which stay on <c>EditorPage</c> because they are read/written from
/// <c>EditorPage.Preview.cs</c> and <c>EditorPage.Style.cs</c> as well.</item>
/// </list>
/// </summary>
internal static class EditorTimelineMediator
{
    // ── Primary/camera/zoom/text-overlay move+resize glue ──
    //
    // Each of these is called directly from a TimelineControl event subscribed in
    // EditorPage.xaml.cs (e.g. `Timeline.SegmentMoveRequested += OnSegmentMoveRequested;`).
    // EditorPage keeps the `On*` method as a one-line forward so the subscription target
    // and its ordering relative to every other Timeline.* subscription are unchanged.

    public static void HandleSegmentMoveRequested(EditorViewModel viewModel, TimelineControl timeline, string id, int targetIndex)
    {
        viewModel.UndoRedoManager.Execute(new MoveSegmentOperation(id, targetIndex));
        timeline.SelectSegment(id);
    }

    public static void HandleSegmentTrimRequested(EditorViewModel viewModel, TimelineControl timeline, string id, bool fromStart, TimeSpan newDuration)
    {
        viewModel.UndoRedoManager.Execute(new TrimSegmentEdgeOperation(id, fromStart, newDuration));
        timeline.SelectSegment(id);
    }

    public static void HandleSegmentSpeedChangeRequested(EditorViewModel viewModel, TimelineControl timeline, string id, double speed)
    {
        viewModel.UndoRedoManager.Execute(new ChangeSegmentSpeedOperation(id, speed));
        timeline.SelectSegment(id);
    }

    public static void HandleSegmentAudioModeChangeRequested(
        EditorViewModel viewModel, TimelineControl timeline, string id, SegmentAudioMode mode)
    {
        viewModel.UndoRedoManager.Execute(new ChangeSegmentAudioModeOperation(id, mode));
        timeline.SelectSegment(id);
    }

    public static void HandleCameraSegmentMoved(EditorViewModel viewModel, string id, TimeSpan newStart) =>
        viewModel.UndoRedoManager.Execute(new MoveCameraSegmentOperation(id, newStart));

    public static void HandleCameraSegmentResized(EditorViewModel viewModel, string id, bool isStartEdge, TimeSpan newEdgeTime) =>
        viewModel.UndoRedoManager.Execute(new TrimCameraSegmentOperation(id, isStartEdge, newEdgeTime));

    public static void HandleZoomSegmentMoved(EditorViewModel viewModel, string id, TimeSpan newTimestamp, string? owningSegmentId = null) =>
        viewModel.UndoRedoManager.Execute(new MoveZoomKeyframeOperation(id, newTimestamp, owningSegmentId));

    public static void HandleZoomSegmentResized(EditorViewModel viewModel, string id, bool isStartEdge, TimeSpan newEdgeTime) =>
        viewModel.UndoRedoManager.Execute(new ResizeZoomSegmentOperation(id, isStartEdge, newEdgeTime));

    /// <summary>
    /// Shared by <c>OnZoomSegmentRemoveRequested</c> (the timeline's own right-click/delete
    /// gesture) and <c>RemoveZoomSegment_Click</c> (the properties-pane delete button), which
    /// previously duplicated this exact three-line body.
    /// </summary>
    public static void HandleZoomSegmentRemoveRequested(
        EditorViewModel viewModel, TimelineControl timeline, string keyframeId, Action updateZoomPanelVisibility)
    {
        viewModel.UndoRedoManager.Execute(new RemoveZoomKeyframeOperation(keyframeId));
        timeline.ClearZoomSelection();
        updateZoomPanelVisibility();
    }

    public static void HandleTextOverlayMoved(EditorViewModel viewModel, string id, TimeSpan newStart, Action refreshOverlayPreview)
    {
        viewModel.UndoRedoManager.Execute(new MoveTextOverlayOperation(id, newStart));
        refreshOverlayPreview();
    }

    public static void HandleTextOverlayResized(
        EditorViewModel viewModel, string id, bool isStartEdge, TimeSpan newEdgeTime, Action refreshOverlayPreview)
    {
        viewModel.UndoRedoManager.Execute(new TrimTextOverlayOperation(id, isStartEdge, newEdgeTime));
        refreshOverlayPreview();
    }

    // ── Model → renderer sync ──
    //
    // Called both after direct edits (so a renderer that is already built picks up the
    // change immediately) and after a renderer rebuild (so a fresh renderer starts from the
    // model's current state rather than whatever it auto-generated from raw mouse data).

    /// <summary>
    /// The manual zoom keyframes that belong to a single source: the primary recording when
    /// <paramref name="sourceVideoFilePath"/> is null, otherwise the appended recording or
    /// imported video with that path. Each source composites through its own renderer, so its
    /// keyframes must be matched by <see cref="ZoomKeyframe.SourceVideoFilePath"/> and never
    /// leak onto another source's compositor.
    /// </summary>
    public static List<ZoomKeyframe> ManualKeyframesForSource(TimelineModel model, string? sourceVideoFilePath) =>
        [.. model.ZoomKeyframes.Where(k =>
            k.IsManual &&
            string.Equals(k.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Pushes the text overlays that belong to <paramref name="sourceVideoFilePath"/>
    /// (null = the primary recording, resolved against <paramref name="primaryVideoPath"/>)
    /// onto <paramref name="renderer"/>'s compositor, using the exact same
    /// <see cref="SegmentFrameComposer.SelectTextOverlays"/> ownership rule export uses, so
    /// preview and export always agree on which overlays a source shows. A no-op when there
    /// is no primary video to resolve the "primary" case against.
    /// </summary>
    public static void SyncTextOverlaysToRenderer(
        TimelineModel model, PreviewRenderer renderer, string? sourceVideoFilePath, string? primaryVideoPath)
    {
        var videoFilePath = sourceVideoFilePath ?? primaryVideoPath;
        if (string.IsNullOrEmpty(videoFilePath)) return;

        renderer.UpdateTextOverlays(SegmentFrameComposer.SelectTextOverlays(model, videoFilePath));
    }

    /// <summary>
    /// Pushes the cursor anchors that belong to <paramref name="sourceVideoFilePath"/>
    /// (null = the primary recording, resolved against <paramref name="primaryVideoPath"/>)
    /// onto <paramref name="renderer"/>'s compositor, using the exact same
    /// <see cref="SegmentFrameComposer.SelectCursorAnchors"/> ownership rule export uses, so a
    /// repositioned cursor cannot warp the preview and not the exported file. A no-op when
    /// there is no primary video to resolve the "primary" case against.
    /// </summary>
    public static void SyncCursorAnchorsToRenderer(
        TimelineModel model, PreviewRenderer renderer, string? sourceVideoFilePath, string? primaryVideoPath)
    {
        var videoFilePath = sourceVideoFilePath ?? primaryVideoPath;
        if (string.IsNullOrEmpty(videoFilePath)) return;

        renderer.UpdateCursorAnchors(SegmentFrameComposer.SelectCursorAnchors(model, videoFilePath));
    }

    /// <summary>
    /// Pushes the model's zoom state — manual keyframes plus the suppressed auto-zoom click
    /// ticks — onto the primary preview renderer. Must run after every renderer rebuild as
    /// well as on every model change; skipping the suppressed ticks brings back auto-zoom
    /// segments the user deleted, and skipping the manual list (in particular when it is
    /// empty) leaves the previous zooms in place.
    /// </summary>
    public static void SyncZoomStateToRenderer(TimelineModel model, PreviewRenderer? renderer, string? primaryVideoPath)
    {
        if (renderer is null) return;

        // Only the PRIMARY recording's manual keyframes drive the primary renderer;
        // appended recordings' keyframes belong to their own segment/source space.
        renderer.UpdateZoomKeyframes(ManualKeyframesForSource(model, null));
        renderer.UpdateSuppressedClickTicks(model.SuppressedClickTicks);
        renderer.UpdateDisabledClickTicks(model.DisabledClickTicks);
        SyncTextOverlaysToRenderer(model, renderer, null, primaryVideoPath);
        SyncCursorAnchorsToRenderer(model, renderer, null, primaryVideoPath);
    }

    /// <summary>
    /// Pushes each already-built appended/imported segment renderer its OWN source's manual
    /// zoom keyframes plus the shared suppressed-click set. The primary renderer is handled by
    /// <see cref="SyncZoomStateToRenderer"/>; appended and imported segments composite through
    /// their own <see cref="PreviewRenderer"/>, so a zoom created or region-edited on one of
    /// them only shows in the preview once its keyframes reach that renderer.
    /// </summary>
    public static void SyncSegmentZoomStateToRenderers(
        TimelineModel model,
        IEnumerable<(string SegmentId, PreviewRenderer? Renderer)> segmentRenderers,
        string? primaryVideoPath)
    {
        foreach (var (segmentId, renderer) in segmentRenderers)
        {
            if (renderer is null) continue;
            var seg = model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == segmentId);
            if (seg is null) continue;

            renderer.UpdateZoomKeyframes(ManualKeyframesForSource(model, seg.VideoFilePath));
            renderer.UpdateSuppressedClickTicks(model.SuppressedClickTicks);
            renderer.UpdateDisabledClickTicks(model.DisabledClickTicks);
            SyncTextOverlaysToRenderer(model, renderer, seg.VideoFilePath, primaryVideoPath);
            SyncCursorAnchorsToRenderer(model, renderer, seg.VideoFilePath, primaryVideoPath);
        }
    }

    /// <summary>
    /// Resolves the video segment that owns a zoom whose source path is
    /// <paramref name="sourceVideoFilePath"/> (null = the primary recording, resolved against
    /// <paramref name="primaryVideoPath"/>). Used to map a zoom's normalised centre against
    /// the correct source dimensions.
    /// </summary>
    public static VideoSegment? SegmentForSource(TimelineModel model, string? sourceVideoFilePath, string? primaryVideoPath)
    {
        var target = sourceVideoFilePath ?? primaryVideoPath;
        if (target is null) return null;
        return model.Segments.OfType<VideoSegment>()
            .FirstOrDefault(v => string.Equals(v.VideoFilePath, target, StringComparison.OrdinalIgnoreCase));
    }
}
