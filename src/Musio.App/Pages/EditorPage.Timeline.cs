using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Media;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Musio_App.Pages;

public sealed partial class EditorPage
{
    private double _audioOffsetSeconds;

    /// <summary>
    /// Loads per-file cursor data and audio waveforms for every appended (non-primary)
    /// video segment, and registers them with the timeline so each segment renders its
    /// OWN cursor/click/audio markers (positioned relative to the segment, so they move
    /// with it). The primary recording uses the model-level data directly.
    /// </summary>
    private async Task LoadAppendedTrackVisualsAsync()
    {
        var model = ViewModel.Model;
        var primary = PrimaryVideoPath;

        var segs = model.Segments.OfType<VideoSegment>()
            .Where(v => !string.IsNullOrEmpty(v.VideoFilePath) &&
                        !string.Equals(v.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.VideoFilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var seg in segs)
        {
            // Per-segment isolation: this runs fire-and-forget, so letting one unreadable
            // recording throw used to abandon every remaining segment AND skip the Refresh
            // below, leaving those tracks blank for the rest of the session with nothing
            // logged and nothing retrying.
            try
            {
                var visual = new Musio_App.Controls.TimelineControl.SegmentTrackVisual
                {
                    MouseToVideoOffsetSeconds = seg.MouseToVideoOffsetSeconds,
                    HasCamera = !string.IsNullOrEmpty(seg.WebcamFilePath) && File.Exists(seg.WebcamFilePath!),
                };

                // Cursor + click data
                if (!string.IsNullOrEmpty(seg.CursorDataFilePath) && File.Exists(seg.CursorDataFilePath!))
                {
                    try { visual.Cursor = MouseHookRecorder.LoadFromFile(seg.CursorDataFilePath!); }
                    catch { /* no cursor data for this recording */ }
                }

                // Auto-zoom keyframes from this recording's clicks, tagged with its file so
                // they render on its segment and are available like the primary's.
                GenerateAppendedZoomKeyframes(seg, visual.Cursor);

                // Audio waveforms (system + mic), spanning the file's audio duration
                var (sys, mic, durSec) = await GenerateFileWaveformsAsync(seg.AudioFilePaths);
                visual.SystemWaveform = sys;
                visual.MicWaveform = mic;
                visual.WaveformDurationSeconds = durSec > 0
                    ? durSec
                    : (seg.SourceDuration.TotalSeconds > 0 ? seg.SourceDuration.TotalSeconds : seg.Duration.TotalSeconds);

                Timeline.SetSegmentTrackVisual(seg.VideoFilePath!, visual);
            }
            catch (Exception ex)
            {
                Musio.Core.Diagnostics.DiagLog.Write("Editor",
                    $"track visuals FAILED for '{seg.VideoFilePath}': {ex.Message}");
            }
        }
        Timeline.Refresh();
    }

    /// <summary>
    /// True when the model already has at least one zoom keyframe tagged with
    /// <paramref name="sourceVideoFilePath"/> (null means the primary recording).
    /// Shared by the primary and appended auto-zoom generation so both apply the
    /// same "generate once, then preserve" rule (see remarks on the primary call site
    /// in <see cref="InitializePreviewAsync"/> and on <see cref="GenerateAppendedZoomKeyframes"/>).
    /// </summary>
    private bool ZoomKeyframesExistForSource(string? sourceVideoFilePath) =>
        ViewModel.Model.ZoomKeyframes.Any(k =>
            string.Equals(k.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Generates auto-zoom keyframes for an appended video segment from its click
    /// events, tagged with the segment's source file so they map into its output range.
    /// Mirrors the primary recording's idempotent generation (see
    /// <see cref="InitializePreviewAsync"/>): <see cref="LoadAppendedTrackVisualsAsync"/>
    /// re-runs this on every editor page (re)construction — including additional
    /// "Record More" cycles and duplicate-path reloads — so this must NOT
    /// unconditionally replace existing keyframes for the file. Doing so previously wiped
    /// manual edits and resurrected auto-zooms the user deleted. Generate only when this
    /// source has no keyframes yet, and skip any click tracked in SuppressedClickTicks.
    /// </summary>
    private void GenerateAppendedZoomKeyframes(VideoSegment seg, MouseRecordingData? mouse)
    {
        // Only a source that came from the package carries saved zoom choices. A
        // recording appended after opening a project is new and still needs its
        // auto-zooms generated.
        if (ProjectService.Instance.IsRestoredSource(seg.VideoFilePath))
            return;

        if (ZoomKeyframesExistForSource(seg.VideoFilePath))
            return; // already generated (or user-edited) for this source — preserve as-is

        if (mouse is null || mouse.Clicks.Count == 0 || mouse.TickFrequency <= 0) return;

        int sw = seg.SourceWidth > 0 ? seg.SourceWidth : 1920;
        int sh = seg.SourceHeight > 0 ? seg.SourceHeight : 1080;
        int cox = seg.CropOffsetX;
        int coy = seg.CropOffsetY;
        double offset = seg.MouseToVideoOffsetSeconds;
        double maxSrc = seg.SourceDuration.TotalSeconds;
        var keyframes = ViewModel.Model.ZoomKeyframes;

        foreach (var click in mouse.Clicks.Where(c => c.IsDown))
        {
            // Respect deletions: never re-add an auto-zoom the user removed.
            if (ViewModel.Model.SuppressedClickTicks.Contains(click.TimestampTicks))
                continue;

            double t = (click.TimestampTicks - mouse.StartTimestampTicks) / mouse.TickFrequency - offset;
            if (t < 0) continue;
            if (maxSrc > 0 && t > maxSrc) continue;
            keyframes.Add(new Musio.Core.Timeline.ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(t),
                ZoomLevel = 2.0,
                CenterX = Math.Clamp((click.X - cox) / (double)sw, 0, 1),
                CenterY = Math.Clamp((click.Y - coy) / (double)sh, 0, 1),
                SourceClickTicks = click.TimestampTicks,
                SourceVideoFilePath = seg.VideoFilePath,
            });
        }
    }

    /// <summary>
    /// Generates system + mic waveform peak arrays for a recording's audio files,
    /// each spanning the full audio file, plus the (max) audio duration in seconds.
    /// </summary>
    private static async Task<(float[]? Sys, float[]? Mic, double DurationSec)> GenerateFileWaveformsAsync(
        IReadOnlyList<string> audioFilePaths)
    {
        const int peaks = 1000;
        return await Task.Run(() =>
        {
            float[]? sys = null, mic = null;
            double dur = 0;

            var valid = audioFilePaths.Where(File.Exists).ToList();
            if (valid.Count == 0) return (sys, mic, dur);

            var systemPath = valid.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("system_", StringComparison.OrdinalIgnoreCase));
            var micPath = valid.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("mic_", StringComparison.OrdinalIgnoreCase));

            if (systemPath is not null)
            {
                try { using var r = new NAudio.Wave.AudioFileReader(systemPath); dur = Math.Max(dur, r.TotalTime.TotalSeconds); } catch { }
                try { sys = AudioWaveformGenerator.GenerateWaveform(systemPath, peaks); } catch { }
            }
            if (micPath is not null)
            {
                try { using var r = new NAudio.Wave.AudioFileReader(micPath); dur = Math.Max(dur, r.TotalTime.TotalSeconds); } catch { }
                try { mic = AudioWaveformGenerator.GenerateWaveform(micPath, peaks); } catch { }
            }

            return (sys, mic, dur);
        });
    }

    /// <summary>
    /// Generates filmstrip thumbnails for the primary recording and then for each
    /// distinct appended recording's source file, so every video segment shows its
    /// own frames. Runs sequentially so the shared generation id never cancels an
    /// earlier still-running pass.
    /// </summary>
    private async Task GenerateAllTimelineThumbnailsAsync(string? primaryPath, int fps)
    {
        // Fire-and-forget, so anything thrown here would vanish and leave the track blank
        // with no indication why.
        try
        {
            if (!string.IsNullOrEmpty(primaryPath))
            {
                _thumbnailsInFlightForPath = primaryPath;
                try
                {
                    await GenerateTimelineThumbnailsAsync(primaryPath, isPrimary: true, fps);
                }
                finally
                {
                    if (string.Equals(_thumbnailsInFlightForPath, primaryPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _thumbnailsInFlightForPath = null;
                    }
                }
            }
            else
            {
                // No primary path means the primary pass is skipped entirely, so
                // TimelineControl never receives a primary strip and every segment cut from
                // the primary recording draws as a flat colour block. That used to be silent,
                // surfacing only as the downstream "no thumbnails ... (primary '')" miss.
                Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                    "primary pass skipped: the project has no VideoFilePath, so only appended " +
                    "sources can get a strip.");
            }

            await GenerateAppendedThumbnailsAsync(primaryPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Filmstrip generation failed for '{primaryPath}': {ex}");
            Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                $"generation failed for '{primaryPath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates per-file thumbnails for every appended (non-primary) video segment
    /// file referenced by the timeline.
    /// </summary>
    private async Task GenerateAppendedThumbnailsAsync(string? primaryPath)
    {
        var model = ViewModel.Model;
        var files = model.Segments.OfType<VideoSegment>()
            .Where(v => !string.IsNullOrEmpty(v.VideoFilePath) &&
                        !string.Equals(v.VideoFilePath, primaryPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.VideoFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Fps: g.Select(v => v.Fps).FirstOrDefault(f => f > 0)))
            .ToList();

        foreach (var (file, segmentFps) in files)
        {
            if (!File.Exists(file)) continue;

            // Claim the file before awaiting: two overlapping initialisations would
            // otherwise both build the same strip, and the loser's bitmaps would be handed
            // to the timeline after the winner's and leak the ones it replaced.
            if (!_thumbnailsDoneForFiles.Add(file)) continue;

            try
            {
                bool applied = await GenerateTimelineThumbnailsAsync(
                    file, isPrimary: false, segmentFps > 0 ? segmentFps : 30);

                // A pass cancelled by a newer generation applied nothing, so the claim must
                // be released or this source would never get a strip again.
                if (!applied) _thumbnailsDoneForFiles.Remove(file);
            }
            catch (Exception ex)
            {
                _thumbnailsDoneForFiles.Remove(file);
                System.Diagnostics.Debug.WriteLine(
                    $"[EditorPage] Appended thumbnail generation failed for {file}: {ex.Message}");
                Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                    $"appended generation failed for '{file}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Builds the filmstrip for one source video.
    /// </summary>
    /// <remarks>
    /// Thumbnails come from <see cref="VideoThumbnailExtractor"/> rather than the preview's
    /// frame reader. Sparse thumbnail access is the worst case for a single-position
    /// decoder — a seek per tile, measured around 334 ms each with roughly a quarter
    /// yielding no frame, while also competing with the preview for the same file. The
    /// batch extractor measured ~15 ms per tile with none missing.
    /// </remarks>
    /// <returns>
    /// <c>true</c> when a strip was handed to the timeline; <c>false</c> when the source was
    /// undecodable or the pass was superseded by a newer generation, so the caller can let
    /// it be retried.
    /// </returns>
    private async Task<bool> GenerateTimelineThumbnailsAsync(string filePath, bool isPrimary, int fps)
    {
        var generationId = ++_thumbnailGenerationId;

        // Thumbnail size: match video track height (60px row minus padding)
        const int thumbH = 52;

        var device = CanvasDevice.GetSharedDevice();
        var strip = await VideoThumbnailExtractor.ExtractAsync(filePath, thumbH, device);

        // The MP4 is unreadable — unfinalized, or finalization failed. The captured JPEGs
        // are still there in exactly that case and the preview is already using them, so
        // the filmstrip must not be the one surface that gives up.
        strip ??= await VideoThumbnailExtractor.ExtractFromCapturedFramesAsync(
            filePath, fps, thumbH, device);

        if (strip is null || generationId != _thumbnailGenerationId)
        {
            // Both outcomes leave the filmstrip empty, but for very different reasons:
            // a null strip means extraction failed, whereas a generation mismatch means a
            // newer pass superseded this one. Distinguishing them matters, because a
            // repeating mismatch indicates a rebuild storm rather than a decode problem.
            Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                strip is null
                    ? $"extraction returned nothing for '{filePath}' (isPrimary={isPrimary})"
                    : $"discarded {strip.Thumbnails.Length} tiles for '{filePath}': generation " +
                      $"{generationId} superseded by {_thumbnailGenerationId}");

            if (strip is not null)
                foreach (var t in strip.Thumbnails) t?.Dispose();
            return false;
        }

        // A tile that could not be decoded would leave a hole in the strip. Repeat the
        // nearest earlier tile instead — slightly stale footage reads as continuous, an
        // empty slot reads as a broken timeline.
        var thumbnails = strip.Thumbnails;
        int thumbW = Math.Max(1, (int)(thumbH * strip.AspectRatio));
        for (int i = 0; i < thumbnails.Length; i++)
        {
            if (thumbnails[i] is not null || i == 0) continue;
            if (thumbnails[i - 1] is not { } previous) continue;

            var repeat = Win2DUtils.CreateRenderTarget(device, thumbW, thumbH, 96, "timeline thumbnail repeat");
            using (var session = repeat.CreateDrawingSession())
                session.DrawImage(previous, new Rect(0, 0, thumbW, thumbH));
            thumbnails[i] = repeat;
        }

        var owned = new CanvasBitmap[thumbnails.Length];
        for (int i = 0; i < thumbnails.Length; i++) owned[i] = thumbnails[i]!;

        // TimelineControl takes ownership of the bitmaps.
        if (isPrimary)
        {
            Timeline.SetThumbnails(owned, strip.IntervalSeconds, strip.AspectRatio, filePath);
            // Only a pass that ran to completion may mark the strip done; a cancelled one
            // would otherwise pin a half-filled filmstrip permanently.
            _thumbnailsCompletedForPath = filePath;

            // Logged so a "no thumbnails ... (primary '')" line can be told apart from a real
            // failure: that miss is also emitted by the first draw, which legitimately happens
            // before this asynchronous pass finishes. A miss followed by this line is benign;
            // a miss with no matching install is the bug.
            Musio.Core.Diagnostics.DiagLog.Write("Filmstrip",
                $"primary strip installed for '{filePath}' ({owned.Length} tiles)");
        }
        else
        {
            Timeline.SetThumbnailsForFile(filePath, owned, strip.IntervalSeconds, strip.AspectRatio);
        }

        return true;
    }

    private TimelineMapper? EnsureTimelineMapper()
    {
        if (_timelineMapper is not null) return _timelineMapper;

        int fps = _frameReader?.Fps ?? ProjectService.Instance.CurrentProject?.Fps ?? 30;
        if (fps <= 0) fps = 30;

        _timelineMapper = new TimelineMapper(ViewModel.Model, fps);
        return _timelineMapper;
    }

    /// <summary>
    /// Returns the effective preview duration, accounting for speed segments.
    /// </summary>
    private TimeSpan GetMappedDuration()
    {
        var mapper = EnsureTimelineMapper();
        return mapper?.EffectiveDuration ?? ViewModel.Model.EffectiveDuration;
    }

    /// <summary>
    /// Invalidates the preview state after timeline edits (speed, zoom, cut, undo/redo).
    /// Rebuilds the timeline mapper, syncs zoom keyframes to the compositor, and forces a re-render.
    /// </summary>
    private void InvalidatePreview()
    {
        _timelineMapper = null;
        _lastRenderedFrameIndex = -1;

        Preview.Duration = GetMappedDuration();

        // Sync user-added zoom keyframes to the compositor
        if (_compositorReady && _previewRenderer is not null)
        {
            SyncZoomStateToRenderer();
        }

        // Appended recordings and imported videos each drive their own segment renderer, so
        // their zooms have to be synced separately from the primary compositor above.
        SyncSegmentZoomStateToRenderers();

        Timeline.Refresh();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    /// <summary>
    /// Pushes the model's zoom state — manual keyframes plus the suppressed auto-zoom click
    /// ticks — onto the primary preview renderer.
    /// </summary>
    /// <remarks>
    /// This must run after every renderer rebuild as well as on every model change. A rebuilt
    /// <see cref="FrameCompositor"/> regenerates auto-zoom from the raw mouse recording, so
    /// skipping the suppressed ticks brings back auto-zoom segments the user deleted, and
    /// skipping the manual list (in particular when it is empty) leaves the previous zooms in
    /// place. Both show up as zoom happening in the preview with no matching timeline segment.
    /// </remarks>
    private void SyncZoomStateToRenderer() =>
        EditorTimelineMediator.SyncZoomStateToRenderer(ViewModel.Model, _previewRenderer, PrimaryVideoPath);

    /// <summary>
    /// Pushes the text overlays that belong to <paramref name="sourceVideoFilePath"/>
    /// (null = the primary recording) onto <paramref name="renderer"/>'s compositor, using
    /// the exact same <see cref="SegmentFrameComposer.SelectTextOverlays"/> ownership rule
    /// export uses, so preview and export always agree on which overlays a source shows.
    /// Called alongside every zoom-keyframe sync above (renderer rebuilds, undo/redo, and
    /// per-segment renderer creation) since overlays need the identical refresh cadence.
    /// A no-op when there is no primary video to resolve the "primary" case against.
    /// </summary>
    private void SyncTextOverlaysToRenderer(PreviewRenderer renderer, string? sourceVideoFilePath) =>
        EditorTimelineMediator.SyncTextOverlaysToRenderer(ViewModel.Model, renderer, sourceVideoFilePath, PrimaryVideoPath);

    /// <summary>
    /// The manual zoom keyframes that belong to a single source: the primary recording when
    /// <paramref name="sourceVideoFilePath"/> is null, otherwise the appended recording or
    /// imported video with that path. Each source composites through its own renderer, so its
    /// keyframes must be matched by <see cref="ZoomKeyframe.SourceVideoFilePath"/> and never
    /// leak onto another source's compositor.
    /// </summary>
    private List<ZoomKeyframe> ManualKeyframesForSource(string? sourceVideoFilePath) =>
        EditorTimelineMediator.ManualKeyframesForSource(ViewModel.Model, sourceVideoFilePath);

    /// <summary>
    /// Pushes each already-built appended/imported segment renderer its OWN source's manual
    /// zoom keyframes plus the shared suppressed-click set. The primary renderer is handled by
    /// <see cref="SyncZoomStateToRenderer"/>; appended and imported segments composite through
    /// their own <see cref="PreviewRenderer"/>, so a zoom created or region-edited on one of
    /// them only shows in the preview once its keyframes reach that renderer.
    /// </summary>
    private void SyncSegmentZoomStateToRenderers() =>
        EditorTimelineMediator.SyncSegmentZoomStateToRenderers(
            ViewModel.Model,
            _segmentPreviews.Select(kv => (kv.Key, kv.Value.Renderer)),
            PrimaryVideoPath);

    /// <summary>
    /// Resolves the video segment that owns a zoom whose source path is
    /// <paramref name="sourceVideoFilePath"/> (null = the primary recording). Used to map a
    /// zoom's normalised centre against the correct source dimensions.
    /// </summary>
    private VideoSegment? SegmentForSource(string? sourceVideoFilePath) =>
        EditorTimelineMediator.SegmentForSource(ViewModel.Model, sourceVideoFilePath, PrimaryVideoPath);

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_zoomRegionEditMode)
                ExitZoomRegionEditMode();

            // Only clear zoom selection if the selected segment was removed
            if (Timeline.SelectedZoomKeyframeId is { } id &&
                !ViewModel.Model.ZoomKeyframes.Any(k => k.Id == id))
            {
                Timeline.ClearZoomSelection();
            }

            Timeline.ClearClipSelection();

            // Re-sync the transition pane to whatever the model holds post-undo/redo — without
            // this, Ctrl+Z after a transition edit leaves the pane showing the value that was
            // just undone, and any further edit from the pane would be computed from a value
            // the user isn't actually looking at. If the boundary itself no longer exists
            // (e.g. undoing/redoing a segment add/remove/reorder), clear the selection instead
            // of leaving the pane referencing a stale segment id.
            if (_selectedTransitionId is { } transitionId)
            {
                var (incoming, outgoing) = GetTransitionBoundarySegments(transitionId);
                if (incoming is null || outgoing is null)
                    Timeline.ClearTransitionSelection();
                else
                    SyncTransitionUI(transitionId);
            }

            UpdateZoomPanelVisibility();
            UpdateSpeedPanelVisibility();
            Timeline.Refresh();
            InvalidatePreview();
        });
    }

    private void OnVideoClipSelected(object? sender, int? clipIndex)
    {
        ViewModel.SelectedClipIndex = clipIndex;
        UpdateSpeedPanelVisibility();

        // Hide text slide panel when a video clip is selected
        if (clipIndex is not null)
        {
            _selectedTextSlideId = null;
            HideTextSlidePanel();
        }

        // Sync combo to match selected clip's current speed
        if (clipIndex is { } idx && idx >= 0 && idx < ViewModel.Model.Clips.Count)
        {
            var clip = ViewModel.Model.Clips[idx];
            _suppressSpeedApply = true;
            for (int i = 0; i < SpeedComboBox.Items.Count; i++)
            {
                if (SpeedComboBox.Items[i] is ComboBoxItem item &&
                    double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double s) &&
                    Math.Abs(s - clip.SpeedFactor) < 0.01)
                {
                    SpeedComboBox.SelectedIndex = i;
                    break;
                }
            }
            _suppressSpeedApply = false;
        }
        else
        {
            // Reset to 1x when deselected
            _suppressSpeedApply = true;
            SpeedComboBox.SelectedIndex = 2;
            _suppressSpeedApply = false;
        }
    }

    private void OnSegmentSelected(object? sender, string? segmentId)
    {
        _selectedPrimarySegmentId = segmentId;

        // Show the text slide panel only when a text slide is selected; for a
        // video (or no) selection, treat the slide id as cleared.
        var slide = segmentId is null
            ? null
            : ViewModel.Model.Segments.OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == segmentId);

        _selectedTextSlideId = slide?.Id;
        if (slide is not null)
            ShowTextSlidePanel(slide);
        else
            HideTextSlidePanel();

        // Reflect the selected video segment's effective frame style + cursor in the
        // flyout controls, so editing per-segment style starts from its current state.
        if (SelectedVideoSegment is { } vseg)
        {
            var global = ProjectService.Instance.CurrentComposition;
            if (global is not null)
            {
                SyncStyleControlsToConfig(vseg.FrameStyleOverride ?? global.Background);
                SyncCursorControlsToConfig(vseg.CursorStyleOverride ?? global.Cursor);
            }
        }
    }

    private void OnSegmentMoveRequested(object? sender, (string Id, int TargetIndex) e) =>
        EditorTimelineMediator.HandleSegmentMoveRequested(ViewModel, Timeline, e.Id, e.TargetIndex);

    private void OnSegmentTrimRequested(object? sender, (string Id, bool FromStart, TimeSpan NewDuration) e) =>
        EditorTimelineMediator.HandleSegmentTrimRequested(ViewModel, Timeline, e.Id, e.FromStart, e.NewDuration);

    // ── Camera track handlers ──

    private string? _selectedCameraSegmentId;

    private void OnCameraSegmentSelected(object? sender, string? segmentId)
    {
        // Selection is tracked by the control; property editing is via the model/ops.
        _selectedCameraSegmentId = segmentId;
        SyncCameraSegmentUI(segmentId);
    }

    private void SyncCameraSegmentUI(string? segmentId)
    {
        if (CameraFullscreenPanel is null) return;

        var seg = segmentId is null
            ? null
            : ViewModel.Model.CameraSegments.FirstOrDefault(s => s.Id == segmentId);

        if (seg is null)
        {
            CameraFullscreenPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressWebcamEvents = true;
        CameraFullscreenToggle.IsOn = seg.FullscreenEnabled;
        CameraFullscreenModeCombo.SelectedIndex = seg.FullscreenMode == CameraFullscreenMode.Reveal ? 1 : 0;
        _suppressWebcamEvents = false;
        CameraFullscreenPanel.Visibility = Visibility.Visible;
        UpdateCameraFullscreenModeUI(seg.FullscreenEnabled, seg.FullscreenMode);

        // Surface the video panel so the segment's settings are immediately editable.
        PropertiesPanel.ShowPane(PropertyPaneKind.Video);
    }

    private void UpdateCameraFullscreenModeUI(bool fullscreenEnabled, CameraFullscreenMode mode)
    {
        if (CameraFullscreenModeCombo is null || CameraFullscreenHint is null) return;
        CameraFullscreenModeCombo.Visibility = fullscreenEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!fullscreenEnabled)
        {
            CameraFullscreenHint.Text =
                "The camera fades in and out smoothly at the start and end of the segment.";
        }
        else if (mode == CameraFullscreenMode.Reveal)
        {
            CameraFullscreenHint.Text =
                "The camera stays full screen, then shrinks to the overlay at the end, revealing the video underneath.";
        }
        else
        {
            CameraFullscreenHint.Text =
                "The camera grows to fill the screen, holds, then shrinks back to the overlay.";
        }
    }

    private void CameraFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (_selectedCameraSegmentId is null) return;

        ViewModel.UndoRedoManager.Execute(new UpdateCameraSegmentPropertiesOperation(
            _selectedCameraSegmentId, fullscreenEnabled: CameraFullscreenToggle.IsOn));

        var mode = CameraFullscreenModeCombo.SelectedIndex == 1
            ? CameraFullscreenMode.Reveal : CameraFullscreenMode.Highlight;
        UpdateCameraFullscreenModeUI(CameraFullscreenToggle.IsOn, mode);

        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void CameraFullscreenModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (_selectedCameraSegmentId is null) return;
        if (CameraFullscreenModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        var mode = tag == "Reveal" ? CameraFullscreenMode.Reveal : CameraFullscreenMode.Highlight;
        ViewModel.UndoRedoManager.Execute(new UpdateCameraSegmentPropertiesOperation(
            _selectedCameraSegmentId, fullscreenMode: mode));

        UpdateCameraFullscreenModeUI(CameraFullscreenToggle.IsOn, mode);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void OnCameraSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End) e)
    {
        var operation = new AddCameraSegmentOperation(
            e.Start, e.End - e.Start, ProjectService.Instance.CurrentProject?.WebcamFilePath);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectedCameraSegmentId = operation.CreatedId;
        _selectedCameraSegmentId = operation.CreatedId;
        SyncCameraSegmentUI(operation.CreatedId);
    }

    private void OnCameraSegmentMoved(object? sender, (string Id, TimeSpan NewStart) e) =>
        EditorTimelineMediator.HandleCameraSegmentMoved(ViewModel, e.Id, e.NewStart);

    private void OnCameraSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e) =>
        EditorTimelineMediator.HandleCameraSegmentResized(ViewModel, e.Id, e.IsStartEdge, e.NewEdgeTime);

    private void OnCameraSegmentRemoveRequested(object? sender, string segmentId)
    {
        DeleteCameraSegment(segmentId);
    }

    private void DeleteCameraSegment(string segmentId)
    {
        ViewModel.UndoRedoManager.Execute(new RemoveCameraSegmentOperation(segmentId));
        Timeline.ClearCameraSelection();
        _selectedCameraSegmentId = null;
        SyncCameraSegmentUI(null);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void CameraDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCameraSegmentId is { } id)
            DeleteCameraSegment(id);
    }

    private void OnTextOverlaySelected(object? sender, string? overlayId)
    {
        // Finalize any in-flight drag/edit against whatever overlay WAS selected BEFORE
        // adopting the new selection. Both CommitPosition and CommitText re-sync the
        // properties pane for the overlay they belong to, so committing after the new
        // selection had been synced would repaint the pane with the OLD overlay's values
        // while the NEW one is selected — and the next property edit would then write
        // those stale values onto the new overlay.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        CommitActiveTextEditIfAny();

        // Selection is tracked by the control; property editing is via the model/ops.
        _selectedTextOverlayId = overlayId;
        SyncTextOverlayUI(overlayId);

        // Recompute which overlay (if any) the shared edit canvas should track RIGHT NOW,
        // rather than waiting for the next rendered frame (UpdateOverlayEditPreview
        // otherwise only runs from RenderFrameAtAsync) — otherwise the PREVIOUSLY selected
        // overlay stays draggable/double-click-editable on the preview until playback or a
        // seek happens to trigger a re-render.
        RefreshOverlayEditPreviewForCurrentPlayhead();
    }

    /// <summary>
    /// Resolves the video source + source-time under the current playhead the same way
    /// <see cref="RenderFrameAtAsync"/> does, then calls <see cref="UpdateOverlayEditPreview"/>
    /// with it — reusing (rather than duplicating) that method's decision of whether the
    /// selected overlay is actually active there. Used by <see cref="OnTextOverlaySelected"/>
    /// so a selection change updates the shared edit canvas immediately instead of waiting
    /// for the next rendered frame. A segment kind other than <see cref="VideoSegment"/>
    /// (e.g. a text slide) has no video — and therefore no overlay — underneath it.
    /// </summary>
    private void RefreshOverlayEditPreviewForCurrentPlayhead()
    {
        var position = Preview.PlayheadPosition;
        var model = ViewModel.Model;

        if (model.Segments.Count > 0)
        {
            var (segment, localOffset) = model.GetSegmentAtTime(position);
            if (segment is VideoSegment videoSeg)
            {
                var sourceInSeg = videoSeg.SourceStart +
                    TimeSpan.FromTicks((long)(localOffset.Ticks * videoSeg.SpeedFactor));
                UpdateOverlayEditPreview(videoSeg.VideoFilePath, sourceInSeg);
            }
            else
            {
                _previewOverlayId = null;
                ShowTextEditOverlay();
            }
            return;
        }

        UpdateOverlayEditPreview(PrimaryVideoPath, MapToSourceTime(position));
    }

    private void OnTextOverlayCreated(object? sender, (TimeSpan Start, TimeSpan End, string? SourceVideoFilePath) e)
    {
        var operation = new AddTextOverlayOperation(e.Start, e.End - e.Start, sourceVideoFilePath: e.SourceVideoFilePath);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.SelectedTextOverlayId = operation.CreatedId;
        _selectedTextOverlayId = operation.CreatedId;
        SyncTextOverlayUI(operation.CreatedId);

        // Same reason as the Insert-menu path: an overlay is invisible on its first frame,
        // so drop the playhead into the middle of the range the user just marked out.
        SeekPastOverlayEntrance(e.End - e.Start);

        RefreshOverlayPreview();
    }

    private void OnTextOverlayMoved(object? sender, (string Id, TimeSpan NewStart) e) =>
        EditorTimelineMediator.HandleTextOverlayMoved(ViewModel, e.Id, e.NewStart, RefreshOverlayPreview);

    private void OnTextOverlayResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e) =>
        EditorTimelineMediator.HandleTextOverlayResized(ViewModel, e.Id, e.IsStartEdge, e.NewEdgeTime, RefreshOverlayPreview);

    private void OnTextOverlayRemoveRequested(object? sender, string overlayId)
    {
        DeleteTextOverlay(overlayId);
    }

    private void DeleteTextOverlay(string overlayId)
    {
        // Finalize first: an in-flight drag or inline text edit has already mutated the
        // live overlay and is holding a pre-gesture snapshot to restore before it commits.
        // Removing the overlay out from under it would strand those mutations — the target
        // can no longer find the segment, while RemoveTextOverlayOperation captures the
        // already-mutated object, so undoing the delete would bring back an overlay at a
        // position or text the user never committed.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        CommitActiveTextEditIfAny();

        ViewModel.UndoRedoManager.Execute(new RemoveTextOverlayOperation(overlayId));
        Timeline.ClearTextOverlaySelection();
        _selectedTextOverlayId = null;
        SyncTextOverlayUI(null);
        RefreshOverlayPreview();
    }

    private void RemoveTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTextOverlayId is { } id)
            DeleteTextOverlay(id);
    }

    private void OnTransitionSelected(object? sender, string? incomingSegmentId)
    {
        _selectedTransitionId = incomingSegmentId;
        SyncTransitionUI(incomingSegmentId);
    }

    private void OnTransitionRemoveRequested(object? sender, string incomingSegmentId)
    {
        DeleteTransition(incomingSegmentId);
    }

    private void OnZoomSegmentSelected(object? sender, string? segmentId)
    {
        if (_zoomRegionEditMode)
            ExitZoomRegionEditMode();

        UpdateZoomPanelVisibility();

        if (segmentId is not null)
        {
            var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == segmentId);
            if (kf is not null)
            {
                _suppressZoomPropertyUpdate = true;

                bool matched = false;
                for (int i = 0; i < ZoomLevelCombo.Items.Count; i++)
                {
                    if (ZoomLevelCombo.Items[i] is ComboBoxItem item &&
                        double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z) &&
                        Math.Abs(z - kf.ZoomLevel) < 0.01)
                    {
                        ZoomLevelCombo.SelectedIndex = i;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    ZoomLevelCombo.SelectedIndex = -1;
                    ZoomLevelCombo.Text = kf.ZoomLevel.ToString("0.##", CultureInfo.InvariantCulture) + "x";
                }

                _suppressZoomPropertyUpdate = false;
            }
        }
    }

    private void OnZoomSegmentMoved(object? sender, (string Id, TimeSpan NewTimestamp) e) =>
        EditorTimelineMediator.HandleZoomSegmentMoved(ViewModel, e.Id, e.NewTimestamp);

    private void OnZoomSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e) =>
        EditorTimelineMediator.HandleZoomSegmentResized(ViewModel, e.Id, e.IsStartEdge, e.NewEdgeTime);

    private void OnZoomSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End, string? FilePath) e)
    {
        double zoomLevel = 2.0;
        if (ZoomLevelCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z))
            zoomLevel = z;

        // Use cursor position at segment midpoint as zoom center (primary recording
        // only; appended recordings default to centered).
        double cx = 0.5, cy = 0.5;
        var midpoint = e.Start + (e.End - e.Start) / 2;
        if (e.FilePath is null && ViewModel.Model.CursorData is { } cursorData && cursorData.Samples.Count > 0)
        {
            var project = ProjectService.Instance.CurrentProject;
            int sourceW = project?.Width > 0 ? project.Width : 1920;
            int sourceH = project?.Height > 0 ? project.Height : 1080;
            // Mouse hook coords are already physical (PerMonitorV2) — no DPI scaling
            float dpiX = 1.0f;
            float dpiY = 1.0f;
            int cropOffX = project?.CropOffsetX ?? 0;
            int cropOffY = project?.CropOffsetY ?? 0;

            double mouseOffset = project?.MouseToVideoOffsetSeconds ?? 0;
            double targetTime = midpoint.TotalSeconds + mouseOffset;
            double tickFreq = cursorData.TickFrequency;
            long startTick = cursorData.StartTimestampTicks;
            Musio.Core.Models.MouseSample closest = cursorData.Samples[0];
            double bestDist = double.MaxValue;
            foreach (var s in cursorData.Samples)
            {
                double sTime = (s.TimestampTicks - startTick) / tickFreq;
                double dist = Math.Abs(sTime - targetTime);
                if (dist < bestDist) { bestDist = dist; closest = s; }
            }
            cx = (closest.X * dpiX - cropOffX) / sourceW;
            cy = (closest.Y * dpiY - cropOffY) / sourceH;
        }

        // Build the keyframe tagged with the owning source file so it renders on the
        // correct clip and (for the primary) drives the zoom engine.
        var keyframe = Musio.Core.Timeline.ZoomKeyframe.FromRange(e.Start, e.End, zoomLevel,
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1)) with
        {
            SourceVideoFilePath = e.FilePath,
        };
        var operation = new AddZoomSegmentOperation(keyframe);
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the newly created segment and enter zoom region edit mode
        Timeline.SelectedZoomKeyframeId = operation.CreatedId;
        OnZoomSegmentSelected(this, operation.CreatedId);

        // Every source — the primary recording, an appended recording, or an imported
        // video — positions its zoom in its own source space, so region editing is offered
        // for all of them. EnterZoomRegionEditMode maps the overlay against the OWNING
        // segment's dimensions, so differently-sized clips still line up.
        var createdKf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == operation.CreatedId);
        if (createdKf is not null)
            EnterZoomRegionEditMode(operation.CreatedId, createdKf);
    }

    private void OnZoomSegmentRemoveRequested(object? sender, string keyframeId) =>
        EditorTimelineMediator.HandleZoomSegmentRemoveRequested(ViewModel, Timeline, keyframeId, UpdateZoomPanelVisibility);

    private void RemoveZoomSegment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
            OnZoomSegmentRemoveRequested(sender, selectedId);
    }

    private void ZoomLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomPropertyUpdate) return;
        if (Timeline is null) return;
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        if (ZoomLevelCombo.SelectedItem is not ComboBoxItem item) return;
        if (!double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double zoomLevel)) return;

        if (_zoomRegionEditMode)
        {
            // Update the overlay rectangle size without committing
            _zoomRegionZoomLevel = zoomLevel;
            UpdateZoomRegionRect();
            return;
        }

        var operation = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoomLevel);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void ZoomLevelCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (Timeline is null || Timeline.SelectedZoomKeyframeId is not { } selectedId)
        {
            args.Handled = true;
            return;
        }

        string text = (args.Text ?? string.Empty).Trim().TrimEnd('x', 'X');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
        {
            // Restore display from current zoom level
            SyncZoomLevelComboToFreeform(_zoomRegionEditMode ? _zoomRegionZoomLevel : GetSelectedKeyframeZoom() ?? 2.0);
            args.Handled = true;
            return;
        }

        zoom = Math.Clamp(zoom, MinZoomLevel, MaxZoomLevel);

        if (_zoomRegionEditMode)
        {
            _zoomRegionZoomLevel = zoom;
            // Re-clamp center for new zoom
            (double halfW, double halfH) = GetNormalizedHalfExtents(zoom);
            _zoomRegionCenterX = Math.Clamp(_zoomRegionCenterX, halfW, 1.0 - halfW);
            _zoomRegionCenterY = Math.Clamp(_zoomRegionCenterY, halfH, 1.0 - halfH);
            UpdateZoomRegionRect();
        }
        else
        {
            var op = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoom);
            ViewModel.UndoRedoManager.Execute(op);
        }

        SyncZoomLevelComboToFreeform(zoom);
        args.Handled = true;
    }

    private double? GetSelectedKeyframeZoom()
    {
        if (Timeline?.SelectedZoomKeyframeId is not { } id) return null;
        return ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == id)?.ZoomLevel;
    }

    // --- Audio waveform loading ---

    private async Task LoadAudioWaveformAsync(Project project, int initGeneration)
    {
        if (project.AudioFilePaths is not { Count: > 0 })
            return;

        const int targetSamples = 2000;

        try
        {
            var validPaths = project.AudioFilePaths.Where(File.Exists).ToList();
            if (validPaths.Count == 0) return;

            // Identify system vs mic files by naming convention
            var systemPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("system_", StringComparison.OrdinalIgnoreCase));
            var micPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("mic_", StringComparison.OrdinalIgnoreCase));

            double videoDuration = project.Duration.TotalSeconds;

            // Offset between audio and video start.
            // Positive: audio pre-roll (WAV starts before video frame 0).
            // Negative: audio started late (e.g. mic permission dialog delay).
            double audioOffset = project.AudioToVideoOffsetSeconds;

            var (systemWaveform, micWaveform) = await Task.Run(() =>
            {
                float[]? sysWf = null;
                float[]? micWf = null;

                double sysDuration = 0, micDuration = 0;
                if (systemPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(systemPath); sysDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }
                if (micPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(micPath); micDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }

                // Waveform alignment:
                // - skipSeconds: how much of the WAV to skip (pre-roll). 0 if offset <= 0.
                // - leadTime: silence at the start of the timeline before audio begins.
                //   Non-zero when audio started after video (negative offset).
                double skipSeconds = Math.Max(0, audioOffset);
                double leadTime = Math.Max(0, -audioOffset);

                sysWf = BuildAlignedWaveform(systemPath, sysDuration, skipSeconds, leadTime, videoDuration, targetSamples);
                micWf = BuildAlignedWaveform(micPath, micDuration, skipSeconds, leadTime, videoDuration, targetSamples);

                return (sysWf, micWf);
            });

            if (systemWaveform is { Length: > 0 })
                ViewModel.Model.SystemAudioWaveformSamples = systemWaveform;
            if (micWaveform is { Length: > 0 })
                ViewModel.Model.MicAudioWaveformSamples = micWaveform;

            // The audio/mic tracks are only shown once their waveform exists, and this
            // build runs in the background well after the timeline first drew — so the
            // tracks have to be told the samples arrived or they would stay collapsed.
            if (systemWaveform is { Length: > 0 } || micWaveform is { Length: > 0 })
                DispatcherQueue.TryEnqueue(() => Timeline?.Refresh());

            // The waveform build above is a multi-second background pass; if the page
            // unloaded or a newer preview init took over meanwhile, publishing a player
            // here would strand it (nothing disposes it again) or clobber the new run's.
            if (initGeneration != _previewInitGeneration) return;

            // At video time T, the audio file position is T + audioOffset
            _audioOffsetSeconds = audioOffset;
            _audioPlayer?.Dispose();
            _audioPlayer = new AudioPlaybackEngine();
            // No fade windows: T9 confirmed transition crossfades can't be wired here
            // without drifting once the timeline has real cuts — see
            // AudioPlaybackEngine's class remarks for the full reasoning.
            _audioPlayer.Load(validPaths);
        }
        catch
        {
            // Audio waveform generation failed — editor still works without it
        }
    }

    /// <summary>
    /// Converts an OUTPUT-timeline playhead position to the corresponding position in the
    /// primary recording's audio files, or <c>null</c> when there is no audio to play there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This must be segment-aware. The output timeline is not the recording's own timeline:
    /// inserting a text slide shifts every later segment, and trims, deletes, reorders and
    /// speed changes all remap it further. Mapping output time to file time with a single
    /// constant offset (which is what this did originally, from before segments existed) meant
    /// the audio played wherever the playhead happened to be rather than where the footage it
    /// belongs to actually sits — e.g. with a 3s title slide in front, the mic track was
    /// audible immediately at 0s instead of starting at 3s, even though the waveform drew in
    /// the right place because <c>TimelineControl.DrawSegmentWaveform</c> already mapped
    /// through the segments. The exporter was likewise already correct
    /// (<see cref="ExportAudioPlan.BuildFromSegments"/>), so this was preview-only.
    /// </para>
    /// <para>
    /// Returns <c>null</c> for anything with no primary-recording audio behind it: a text
    /// slide, a gap, an appended clip (only the primary recording's files are loaded into the
    /// preview engine), or a position that lands before the audio recording started. Callers
    /// treat <c>null</c> as "silence" and pause.
    /// </para>
    /// </remarks>
    private TimeSpan? AudioPositionForVideo(TimeSpan videoPosition)
    {
        var model = ViewModel.Model;

        // Legacy, pre-segment projects: output time IS the recording's own video time.
        if (model.Segments.Count == 0)
        {
            var legacy = videoPosition + TimeSpan.FromSeconds(_audioOffsetSeconds);
            return legacy >= TimeSpan.Zero ? legacy : null;
        }

        var (segment, localOffset) = model.GetSegmentAtTime(videoPosition);
        if (segment is not VideoSegment video)
            return null;

        // Appended recordings carry their own audio files, which the preview engine never
        // loads (it only opens the primary recording's tracks) — so there is nothing to
        // position for them, and playing the primary's audio there would be plainly wrong.
        if (!string.Equals(video.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase))
            return null;

        double speed = video.SpeedFactor > 0 ? video.SpeedFactor : 1.0;
        var sourceTime = video.SourceStart + TimeSpan.FromTicks((long)(localOffset.Ticks * speed));
        var position = sourceTime + TimeSpan.FromSeconds(_audioOffsetSeconds);
        return position >= TimeSpan.Zero ? position : null;
    }

    /// <summary>
    /// How far the audio may drift from its mapped position before it is re-seeked during
    /// playback. Preview audio plays as one continuous pass per file while the output timeline
    /// may cut, reorder or speed-shift underneath it, so drift is expected and has to be
    /// corrected — but correcting it every tick would stutter, and correcting it never would
    /// desynchronise. Roughly two frames at 30fps: past the ~50-100ms mark it reads as out of
    /// sync, below it the correction is inaudible.
    /// </summary>
    private static readonly TimeSpan AudioDriftTolerance = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Re-aligns preview audio with <paramref name="videoPosition"/>: seeks when it has drifted
    /// (or when the playhead has jumped to unrelated footage), starts it when the playhead
    /// enters footage that has audio, and pauses it over slides, gaps and appended clips.
    /// </summary>
    private void SyncAudioToPlayhead(TimeSpan videoPosition)
    {
        if (_audioPlayer is not { IsLoaded: true } player) return;

        if (AudioPositionForVideo(videoPosition) is not { } target)
        {
            // Nothing to play here (text slide, gap, appended clip, or before the audio
            // recording began). Pausing rather than letting it run on is the whole point:
            // otherwise the mic track is audible over a title card.
            if (player.IsPlaying) player.Pause();
            return;
        }

        var actual = player.CurrentPosition;
        bool drifted = actual is null || (actual.Value - target).Duration() > AudioDriftTolerance;

        if (drifted) player.Seek(target);
        if (!player.IsPlaying) player.Play();
    }

    /// <summary>
    /// Builds a waveform array aligned to the video timeline, handling both
    /// pre-roll (positive offset → skip WAV start) and late audio (negative
    /// offset → leading silence on the timeline).
    /// </summary>
    private static float[]? BuildAlignedWaveform(
        string? path, double fileDuration,
        double skipSeconds, double leadTime,
        double videoDuration, int targetSamples)
    {
        if (path is null || fileDuration <= 0) return null;

        try
        {
            // How much of the WAV maps to the video timeline
            double audioForVideo = Math.Max(0, fileDuration - skipSeconds);
            // How much of the video timeline this audio covers
            double coverageDuration = Math.Min(audioForVideo, videoDuration - leadTime);
            if (coverageDuration <= 0) return null;

            int leadPeaks = (int)(targetSamples * leadTime / videoDuration);
            int audioPeaks = (int)Math.Min(
                targetSamples - leadPeaks,
                Math.Ceiling(targetSamples * coverageDuration / videoDuration));
            if (audioPeaks < 1) audioPeaks = 1;

            var raw = AudioWaveformGenerator.GenerateWaveform(
                path, audioPeaks, startSeconds: skipSeconds);
            var wf = new float[targetSamples];
            Array.Copy(raw, 0, wf, leadPeaks, Math.Min(raw.Length, audioPeaks));
            return wf;
        }
        catch { return null; }
    }

    // --- Audio mute support ---

    /// <summary>
    /// Reloads the audio player with only the unmuted audio tracks.
    /// </summary>
    private void ReloadAudioPlayer()
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _audioPlayer?.Dispose();
        _audioPlayer = new AudioPlaybackEngine();

        var paths = GetUnmutedAudioPaths(project);
        if (paths.Count > 0)
            // No fade windows here either, for the same reason as the initial Load
            // above — see AudioPlaybackEngine's class remarks (T9).
            _audioPlayer.Load(paths);
    }

    /// <summary>
    /// Returns audio file paths filtered by current mute state.
    /// </summary>
    private List<string> GetUnmutedAudioPaths(Project project)
    {
        var model = ViewModel.Model;
        var paths = new List<string>();
        if (project.AudioFilePaths is null) return paths;

        foreach (var path in project.AudioFilePaths)
        {
            if (!File.Exists(path)) continue;
            var fileName = Path.GetFileName(path);
            if (model.IsSystemAudioMuted
                && fileName.StartsWith("system_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (model.IsMicAudioMuted
                && fileName.StartsWith("mic_", StringComparison.OrdinalIgnoreCase))
                continue;
            paths.Add(path);
        }
        return paths;
    }
}
