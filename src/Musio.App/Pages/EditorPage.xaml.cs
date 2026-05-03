using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    public ExportViewModel ExportVM { get; }
    private VideoFrameReader? _frameReader;
    private PreviewRenderer? _previewRenderer;
    private TimelineMapper? _timelineMapper;
    private AudioPlaybackEngine? _audioPlayer;
    private bool _compositorReady;
    private int _lastRenderedFrameIndex = -1;
    private bool _isRendering;
    private TimeSpan? _pendingRenderPosition;
    private bool _pendingRenderForce;
    private double _audioOffsetSeconds;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        ExportVM = new ExportViewModel();
        InitializeComponent();

        Preview.Duration = GetMappedDuration();

        // Load frames and initialize compositor with cursor effects
        _ = InitializePreviewAsync();

        // Sync playhead: when timeline scrubs, update preview + audio
        Timeline.RegisterPropertyChangedCallback(
            Controls.TimelineControl.PlayheadPositionProperty,
            (_, _) =>
            {
                Preview.PlayheadPosition = Timeline.PlayheadPosition;
                ViewModel.Model.PlayheadPosition = Timeline.PlayheadPosition;
                _ = UpdatePreviewFrameAsync(Timeline.PlayheadPosition);
                // Play short audio burst at scrub position for editing feedback
                if (!Preview.IsPlaying)
                {
                    var audioPos = AudioPositionForVideo(Timeline.PlayheadPosition);
                    if (audioPos >= TimeSpan.Zero)
                        _audioPlayer?.ScrubTo(audioPos);
                }
            });

        // Sync playhead: when preview plays, update timeline
        Preview.PlaybackTick += (_, _) =>
        {
            Timeline.PlayheadPosition = Preview.PlayheadPosition;
            ViewModel.Model.PlayheadPosition = Preview.PlayheadPosition;
            _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition);

            // Start audio when playhead reaches audio start point
            // (handles negative offset where audio started after video)
            if (_audioPlayer is not null && _audioPlayer.IsLoaded
                && Preview.IsPlaying && !_audioPlayer.IsPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
            }
        };

        // Sync audio play/pause with preview
        Preview.IsPlayingChanged += (_, isPlaying) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            if (isPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
                // else: audio hasn't started yet; PlaybackTick will start it
            }
            else
            {
                _audioPlayer.Pause();
            }
        };

        // Re-seek audio when playback loops
        Preview.PlaybackLooped += (_, _) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            var audioPos = AudioPositionForVideo(TimeSpan.Zero);
            if (audioPos >= TimeSpan.Zero)
            {
                _audioPlayer.Seek(audioPos);
                if (!_audioPlayer.IsPlaying)
                    _audioPlayer.Play();
            }
            else
            {
                // Audio starts later in the video — stop and let tick restart it
                _audioPlayer.Pause();
            }
        };

        ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;

        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _timelineMapper = null;
                Timeline.ClearZoomSelection();
                Timeline.ClearClipSelection();
                UpdateSpeedPanelVisibility();
                Preview.Duration = GetMappedDuration();
                Timeline.Refresh();
                ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
                _ = InitializePreviewAsync();
            });
        };

        // Zoom segment interaction events
        Timeline.ZoomSegmentSelected += OnZoomSegmentSelected;
        Timeline.ZoomSegmentMoved += OnZoomSegmentMoved;
        Timeline.ZoomSegmentResized += OnZoomSegmentResized;
        Timeline.ZoomSegmentCreated += OnZoomSegmentCreated;
        Timeline.ZoomSegmentRemoveRequested += OnZoomSegmentRemoveRequested;

        // Video clip selection events
        Timeline.VideoClipSelected += OnVideoClipSelected;

        // Export flyout state management
        ExportFlyout.Opened += ExportFlyout_Opened;
        ExportVM.PropertyChanged += ExportVM_PropertyChanged;
    }

    private async Task InitializePreviewAsync()
    {
        _frameReader?.Dispose();
        _previewRenderer?.Dispose();
        _audioPlayer?.Dispose();
        _frameReader = null;
        _previewRenderer = null;
        _audioPlayer = null;
        _compositorReady = false;
        _lastRenderedFrameIndex = -1;

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
            return;

        int fps = project.Fps > 0 ? project.Fps : 30;
        int previewFps = Math.Min(fps, 30);
        Preview.PreviewFps = previewFps;

        _frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, fps);
        if (_frameReader is null)
            return;

        // Load audio waveform data for timeline visualization
        await LoadAudioWaveformAsync(project);

        // Load mouse data for cursor smoothing + click animations
        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data — still show raw frames */ }
        }

        if (mouseData is null)
        {
            // No cursor data — just show raw frames
            Timeline.Refresh();
            _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
            return;
        }

        // Feed cursor data to timeline for track visualization
        ViewModel.Model.CursorData = mouseData;
        ViewModel.Model.MouseToVideoOffsetSeconds = project.MouseToVideoOffsetSeconds;

        // Build zoom keyframes from click events so they appear on the timeline.
        // Subtract MouseToVideoOffsetSeconds to convert mouse-relative time to video time.
        int sourceW = project.Width > 0 ? project.Width : 1920;
        int sourceH = project.Height > 0 ? project.Height : 1080;
        // Mouse hook (WH_MOUSE_LL) in PerMonitorV2 reports physical pixels —
        // no DPI scaling needed for click coordinate transforms.
        float dpiScaleX = 1.0f;
        float dpiScaleY = 1.0f;
        int cropOffX = project.CropOffsetX;
        int cropOffY = project.CropOffsetY;
        double mouseOffset = project.MouseToVideoOffsetSeconds;
        ViewModel.Model.ZoomKeyframes.Clear();
        foreach (var click in mouseData.Clicks.Where(c => c.IsDown))
        {
            double clickTime = (click.TimestampTicks - mouseData.StartTimestampTicks) / mouseData.TickFrequency
                - mouseOffset;
            if (clickTime < 0) continue; // skip pre-roll clicks before video started
            ViewModel.Model.ZoomKeyframes.Add(new Musio.Core.Timeline.ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(clickTime),
                ZoomLevel = 2.0,
                CenterX = (click.X * dpiScaleX - cropOffX) / sourceW,
                CenterY = (click.Y * dpiScaleY - cropOffY) / sourceH,
                SourceClickTicks = click.TimestampTicks,
            });
        }

        Timeline.Refresh();

        // Build composition config with cursor effects enabled
        var config = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
        config = config with
        {
            OutputFps = previewFps,
            SmoothingAlgorithm = SmoothingAlgorithm.SpringPhysics,
            SmoothingStrength = SmoothingStrength.Smooth,
            Cursor = new CursorStyle
            {
                Scale = 2.0f,
                ClickAnimationEnabled = true,
                AutoHideEnabled = true,
                AutoHideDelaySeconds = 3.0f,
            },
            Zoom = new AutoZoomConfig { Enabled = true },
        };

        // Only apply background fill for window captures — region and
        // full-screen recordings render without padding/shadow.
        if (project.CaptureType != CaptureTargetType.Window)
        {
            config = config with
            {
                Background = config.Background with
                {
                    Padding = 0,
                    ShadowEnabled = false,
                    CornerRadius = 0,
                    BorderEnabled = false,
                },
            };
        }

        // Persist so the export pipeline uses the same config
        ProjectService.Instance.CurrentComposition = config;

        try
        {
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, config,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);
            _compositorReady = true;
        }
        catch
        {
            // Compositor init failed — fall back to raw frames
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
    }

    private async Task UpdatePreviewFrameAsync(TimeSpan position, bool force = false)
    {
        if (_frameReader is null) return;

        // Coalesce overlapping render requests: if a render is already in
        // flight, record the latest requested position and let the active
        // render pick it up when it finishes.
        if (_isRendering)
        {
            _pendingRenderPosition = position;
            _pendingRenderForce |= force;
            return;
        }

        _isRendering = true;
        try
        {
            // Drain loop: render the current request, then check if a newer
            // position arrived while we were rendering and handle it too.
            TimeSpan currentPos = position;
            bool currentForce = force;
            do
            {
                await RenderFrameAtAsync(currentPos, currentForce);

                // Consume any pending request that arrived during rendering
                if (_pendingRenderPosition.HasValue)
                {
                    currentPos = _pendingRenderPosition.Value;
                    currentForce = _pendingRenderForce;
                    _pendingRenderPosition = null;
                    _pendingRenderForce = false;
                }
                else
                {
                    break;
                }
            } while (true);
        }
        finally
        {
            _isRendering = false;
        }
    }

    private async Task RenderFrameAtAsync(TimeSpan position, bool force)
    {
        if (_frameReader is null) return;

        // Map output (playhead) time to source time, accounting for speed/cut/trim edits
        TimeSpan sourcePosition = MapToSourceTime(position);

        int frameIndex = _frameReader.GetFrameIndex(sourcePosition);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourcePosition);
        if (bitmap is null) return;

        try
        {
            if (_compositorReady && _previewRenderer is not null && !_zoomRegionEditMode)
            {
                var composed = _previewRenderer.RenderPreviewFrame(bitmap, sourcePosition);
                bitmap.Dispose();

                if (composed is not null)
                {
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
            }

            // Fallback: show raw frame without effects
            var device = CanvasDevice.GetSharedDevice();
            var renderTarget = new CanvasRenderTarget(device,
                bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.DrawImage(bitmap);
            }
            bitmap.Dispose();
            _lastRenderedFrameIndex = frameIndex;
            Preview.SetFrame(renderTarget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Preview frame error at {position}: {ex.Message}");
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Maps an output (playhead) time to source time using the current timeline mapper.
    /// Falls back to identity mapping when no speed/cut edits are present.
    /// </summary>
    private TimeSpan MapToSourceTime(TimeSpan outputPosition)
    {
        var mapper = EnsureTimelineMapper();
        if (mapper is null) return outputPosition;

        int fps = _frameReader?.Fps ?? 30;
        int outputFrame = (int)(outputPosition.TotalSeconds * fps);
        double sourceSeconds = mapper.GetSourceTimeForOutputFrame(outputFrame);
        return TimeSpan.FromSeconds(sourceSeconds);
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
            var manualKeyframes = ViewModel.Model.ZoomKeyframes
                .Where(k => k.IsManual)
                .ToList();
            _previewRenderer.UpdateZoomKeyframes(manualKeyframes);
            _previewRenderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
        }

        Timeline.Refresh();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

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
            UpdateZoomPanelVisibility();
            UpdateSpeedPanelVisibility();
            InvalidatePreview();
        });
    }

    private bool _suppressSpeedApply;

    private void OnVideoClipSelected(object? sender, int? clipIndex)
    {
        ViewModel.SelectedClipIndex = clipIndex;
        UpdateSpeedPanelVisibility();

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

    private void UpdateSpeedPanelVisibility()
    {
        if (SpeedComboBox is null) return;
        bool hasClipSelection = ViewModel.SelectedClipIndex is not null;
        SpeedComboBox.Visibility = hasClipSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSpeedApply) return;
        if (SpeedComboBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double speed))
        {
            ViewModel.SelectedSpeed = speed;

            if (ViewModel.SelectedClipIndex is not null)
            {
                ViewModel.ApplySpeedCommand.Execute(null);
            }
        }
    }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanUndo)
        {
            ViewModel.UndoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanRedo)
        {
            ViewModel.RedoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void SplitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SplitAtPlayheadCommand.Execute(null);
        args.Handled = true;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // If a zoom segment is selected, remove it instead of deleting a clip segment
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
            args.Handled = true;
            return;
        }

        ViewModel.DeleteSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
    }

    // --- Zoom Segment Handlers ---

    private bool _suppressZoomPropertyUpdate;

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

                // Update zoom level combo to match selected segment
                for (int i = 0; i < ZoomLevelCombo.Items.Count; i++)
                {
                    if (ZoomLevelCombo.Items[i] is ComboBoxItem item &&
                        double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z) &&
                        Math.Abs(z - kf.ZoomLevel) < 0.01)
                    {
                        ZoomLevelCombo.SelectedIndex = i;
                        break;
                    }
                }

                _suppressZoomPropertyUpdate = false;
            }
        }
    }

    private void OnZoomSegmentMoved(object? sender, (string Id, TimeSpan NewTimestamp) e)
    {
        var operation = new MoveZoomKeyframeOperation(e.Id, e.NewTimestamp);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e)
    {
        var operation = new ResizeZoomSegmentOperation(e.Id, e.IsStartEdge, e.NewEdgeTime);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End) e)
    {
        double zoomLevel = 2.0;
        if (ZoomLevelCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z))
            zoomLevel = z;

        // Use cursor position at segment midpoint as zoom center
        double cx = 0.5, cy = 0.5;
        var midpoint = e.Start + (e.End - e.Start) / 2;
        if (ViewModel.Model.CursorData is { } cursorData && cursorData.Samples.Count > 0)
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

        var operation = new AddZoomSegmentOperation(e.Start, e.End, zoomLevel,
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1));
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the newly created segment and enter zoom region edit mode
        Timeline.SelectedZoomKeyframeId = operation.CreatedId;
        OnZoomSegmentSelected(this, operation.CreatedId);

        var createdKf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == operation.CreatedId);
        if (createdKf is not null)
            EnterZoomRegionEditMode(operation.CreatedId, createdKf);
    }

    private void OnZoomSegmentRemoveRequested(object? sender, string keyframeId)
    {
        var operation = new RemoveZoomKeyframeOperation(keyframeId);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.ClearZoomSelection();
        UpdateZoomPanelVisibility();
    }

    private void RemoveZoomSegment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
        }
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

    // --- Zoom Region Edit Mode ---

    private bool _zoomRegionEditMode;
    private string? _zoomRegionKeyframeId;
    private double _zoomRegionCenterX, _zoomRegionCenterY;
    private double _zoomRegionZoomLevel;
    private int _zoomRegionSourceW, _zoomRegionSourceH;
    private double _frameDisplayX, _frameDisplayY, _frameDisplayW, _frameDisplayH;
    private bool _isDraggingZoomRegion;
    private Point _dragStartPoint;
    private double _dragStartCenterX, _dragStartCenterY;

    private void EditZoomRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == selectedId);
        if (kf is null) return;
        EnterZoomRegionEditMode(selectedId, kf);
    }

    private void EnterZoomRegionEditMode(string keyframeId, ZoomKeyframe kf)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _zoomRegionEditMode = true;
        _zoomRegionKeyframeId = keyframeId;
        _zoomRegionCenterX = kf.CenterX;
        _zoomRegionCenterY = kf.CenterY;
        _zoomRegionZoomLevel = kf.ZoomLevel;
        _zoomRegionSourceW = project.Width > 0 ? project.Width : 1920;
        _zoomRegionSourceH = project.Height > 0 ? project.Height : 1080;

        // Pause playback and move to segment timestamp for context
        Preview.Pause();
        var pos = kf.Timestamp;
        Timeline.PlayheadPosition = pos;
        Preview.PlayheadPosition = pos;
        ViewModel.Model.PlayheadPosition = pos;

        // Re-render without compositor (raw frame for positioning)
        _ = UpdatePreviewFrameAsync(pos, force: true);

        ZoomRegionOverlay.Visibility = Visibility.Visible;
        UpdateZoomRegionRect();
    }

    private void ExitZoomRegionEditMode()
    {
        _zoomRegionEditMode = false;
        _isDraggingZoomRegion = false;
        _zoomRegionKeyframeId = null;
        ZoomRegionOverlay.Visibility = Visibility.Collapsed;

        // Re-render with compositor
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void UpdateZoomRegionRect()
    {
        if (!_zoomRegionEditMode) return;

        double canvasW = ZoomRegionCanvas.ActualWidth;
        double canvasH = ZoomRegionCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        // Compute raw frame display rect (aspect-fit centered)
        double scale = Math.Min(canvasW / _zoomRegionSourceW, canvasH / _zoomRegionSourceH);
        _frameDisplayW = _zoomRegionSourceW * scale;
        _frameDisplayH = _zoomRegionSourceH * scale;
        _frameDisplayX = (canvasW - _frameDisplayW) / 2;
        _frameDisplayY = (canvasH - _frameDisplayH) / 2;

        // Compute zoom viewport in source pixels
        double vpW = _zoomRegionSourceW / _zoomRegionZoomLevel;
        double vpH = _zoomRegionSourceH / _zoomRegionZoomLevel;

        // Adjust for output aspect ratio if set
        var config = ProjectService.Instance.CurrentComposition;
        if (config is not null && config.AspectRatio != AspectRatio.Auto)
        {
            double contentRatio = GetAspectRatioValue(config.AspectRatio);
            if (contentRatio > 0)
            {
                double vpRatio = vpW / vpH;
                if (vpRatio > contentRatio)
                    vpW = vpH * contentRatio;
                else
                    vpH = vpW / contentRatio;
            }
        }

        double vpX = _zoomRegionCenterX * _zoomRegionSourceW - vpW / 2;
        double vpY = _zoomRegionCenterY * _zoomRegionSourceH - vpH / 2;

        // Clamp viewport to source bounds
        vpX = Math.Clamp(vpX, 0, Math.Max(0, _zoomRegionSourceW - vpW));
        vpY = Math.Clamp(vpY, 0, Math.Max(0, _zoomRegionSourceH - vpH));

        // Map to display coordinates
        double rectX = _frameDisplayX + (vpX / _zoomRegionSourceW) * _frameDisplayW;
        double rectY = _frameDisplayY + (vpY / _zoomRegionSourceH) * _frameDisplayH;
        double rectW = (vpW / _zoomRegionSourceW) * _frameDisplayW;
        double rectH = (vpH / _zoomRegionSourceH) * _frameDisplayH;

        Canvas.SetLeft(ZoomRegionRect, rectX);
        Canvas.SetTop(ZoomRegionRect, rectY);
        ZoomRegionRect.Width = rectW;
        ZoomRegionRect.Height = rectH;

        // Dim overlays constrained to frame display area
        double fX = _frameDisplayX, fY = _frameDisplayY;
        double fW = _frameDisplayW, fH = _frameDisplayH;

        Canvas.SetLeft(DimTop, fX);
        Canvas.SetTop(DimTop, fY);
        DimTop.Width = fW;
        DimTop.Height = Math.Max(0, rectY - fY);

        Canvas.SetLeft(DimBottom, fX);
        Canvas.SetTop(DimBottom, rectY + rectH);
        DimBottom.Width = fW;
        DimBottom.Height = Math.Max(0, (fY + fH) - (rectY + rectH));

        Canvas.SetLeft(DimLeft, fX);
        Canvas.SetTop(DimLeft, rectY);
        DimLeft.Width = Math.Max(0, rectX - fX);
        DimLeft.Height = rectH;

        Canvas.SetLeft(DimRight, rectX + rectW);
        Canvas.SetTop(DimRight, rectY);
        DimRight.Width = Math.Max(0, (fX + fW) - (rectX + rectW));
        DimRight.Height = rectH;
    }

    private static double GetAspectRatioValue(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Landscape16x9 => 16.0 / 9.0,
        AspectRatio.Portrait9x16 => 9.0 / 16.0,
        AspectRatio.Square1x1 => 1.0,
        AspectRatio.Classic4x3 => 4.0 / 3.0,
        AspectRatio.Tall3x4 => 3.0 / 4.0,
        _ => -1.0,
    };

    private void ZoomRegionCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_zoomRegionEditMode) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        double rectX = Canvas.GetLeft(ZoomRegionRect);
        double rectY = Canvas.GetTop(ZoomRegionRect);

        if (point.Position.X >= rectX && point.Position.X <= rectX + ZoomRegionRect.Width &&
            point.Position.Y >= rectY && point.Position.Y <= rectY + ZoomRegionRect.Height)
        {
            _isDraggingZoomRegion = true;
            _dragStartPoint = point.Position;
            _dragStartCenterX = _zoomRegionCenterX;
            _dragStartCenterY = _zoomRegionCenterY;
            ZoomRegionCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void ZoomRegionCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingZoomRegion) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        double deltaX = point.Position.X - _dragStartPoint.X;
        double deltaY = point.Position.Y - _dragStartPoint.Y;

        if (_frameDisplayW > 0 && _frameDisplayH > 0)
        {
            double deltaNormX = deltaX / _frameDisplayW;
            double deltaNormY = deltaY / _frameDisplayH;

            // Clamp center so the viewport stays within source bounds
            double halfW = 1.0 / (_zoomRegionZoomLevel * 2.0);
            double halfH = 1.0 / (_zoomRegionZoomLevel * 2.0);
            _zoomRegionCenterX = Math.Clamp(_dragStartCenterX + deltaNormX, halfW, 1.0 - halfW);
            _zoomRegionCenterY = Math.Clamp(_dragStartCenterY + deltaNormY, halfH, 1.0 - halfH);

            UpdateZoomRegionRect();
        }

        e.Handled = true;
    }

    private void ZoomRegionCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingZoomRegion)
        {
            _isDraggingZoomRegion = false;
            ZoomRegionCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void ZoomRegionCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomRegionEditMode)
            UpdateZoomRegionRect();
    }

    private void ZoomRegionApply_Click(object sender, RoutedEventArgs e)
    {
        if (_zoomRegionKeyframeId is null) return;

        var operation = new UpdateZoomSegmentPropertiesOperation(
            _zoomRegionKeyframeId,
            zoomLevel: _zoomRegionZoomLevel,
            centerX: _zoomRegionCenterX,
            centerY: _zoomRegionCenterY);
        ViewModel.UndoRedoManager.Execute(operation);

        ExitZoomRegionEditMode();
    }

    private void ZoomRegionCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitZoomRegionEditMode();
    }

    private void UpdateZoomPanelVisibility()
    {
        if (Timeline is null || ZoomSegmentPanel is null || ZoomHintText is null) return;
        bool hasSelection = Timeline.SelectedZoomKeyframeId is not null;
        ZoomSegmentPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ZoomHintText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static float GetDpiScale(int capturedDimension, bool isWidth = true)
    {
        try
        {
            int logical = GetSystemMetrics(isWidth ? 0 : 1); // SM_CXSCREEN=0, SM_CYSCREEN=1
            if (logical > 0 && capturedDimension > logical)
                return (float)capturedDimension / logical;
        }
        catch { }
        return 1.0f;
    }

    // --- Audio waveform loading ---

    private async Task LoadAudioWaveformAsync(Project project)
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

            // At video time T, the audio file position is T + audioOffset
            _audioOffsetSeconds = audioOffset;
            _audioPlayer?.Dispose();
            _audioPlayer = new AudioPlaybackEngine();
            _audioPlayer.Load(validPaths);
        }
        catch
        {
            // Audio waveform generation failed — editor still works without it
        }
    }

    /// <summary>
    /// Converts a video playhead position to the corresponding audio file position.
    /// Positive _audioOffsetSeconds = pre-roll (skip into WAV).
    /// Negative _audioOffsetSeconds = audio started late (silence before audio).
    /// Returns negative TimeSpan when video is before audio start — callers
    /// must treat negative results as silence (no audio to play).
    /// </summary>
    private TimeSpan AudioPositionForVideo(TimeSpan videoPosition)
    {
        return videoPosition + TimeSpan.FromSeconds(_audioOffsetSeconds);
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

    // --- Export flyout ---

    private async void ExportFlyout_Opened(object? sender, object e)
    {
        if (ExportVM.IsExporting)
        {
            ShowExportingState();
            return;
        }

        if (ExportVM.ExportSucceeded)
        {
            ShowExportedState();
            return;
        }

        if (ExportVM.ExportFailed)
        {
            ShowErrorState();
            return;
        }

        // Start new export
        ExportVM.PrepareForExport();
        if (!ExportVM.ExportCommand.CanExecute(null))
        {
            ExportFlyout.Hide();
            EditorInfoBar.Message = "No recording available to export.";
            EditorInfoBar.Severity = InfoBarSeverity.Warning;
            EditorInfoBar.IsOpen = true;
            return;
        }

        ShowExportingState();
        await ExportVM.ExportCommand.ExecuteAsync(null);
    }

    private void ExportVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.IsExporting) && !ExportVM.IsExporting)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ExportVM.ExportSucceeded)
                    ShowExportedState();
                else if (ExportVM.ExportFailed)
                    ShowErrorState();
                else
                    ExportFlyout.Hide(); // Cancelled
            });
        }
    }

    private void ShowExportingState()
    {
        ExportingPanel.Visibility = Visibility.Visible;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowExportedState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Visible;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Visible;
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.OpenOutputFolderCommand.Execute(null);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.CancelExportCommand.Execute(null);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        ExportFlyout.Hide();
        // Reset state so next open starts a fresh export
        ExportVM.PrepareForExport();
        ShowExportingState();
    }
}
