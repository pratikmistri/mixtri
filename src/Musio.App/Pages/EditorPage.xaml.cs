using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    public ExportViewModel ExportVM { get; }
    private VideoFrameReader? _frameReader;
    private PreviewRenderer? _previewRenderer;
    private TimelineMapper? _timelineMapper;
    private bool _compositorReady;
    private int _lastRenderedFrameIndex = -1;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        ExportVM = new ExportViewModel();
        InitializeComponent();

        Preview.Duration = GetMappedDuration();

        // Load frames and initialize compositor with cursor effects
        _ = InitializePreviewAsync();

        // Sync playhead: when timeline scrubs, update preview
        Timeline.RegisterPropertyChangedCallback(
            Controls.TimelineControl.PlayheadPositionProperty,
            (_, _) =>
            {
                Preview.PlayheadPosition = Timeline.PlayheadPosition;
                ViewModel.Model.PlayheadPosition = Timeline.PlayheadPosition;
                _ = UpdatePreviewFrameAsync(Timeline.PlayheadPosition);
            });

        // Sync playhead: when preview plays, update timeline
        Preview.PlaybackTick += (_, _) =>
        {
            Timeline.PlayheadPosition = Preview.PlayheadPosition;
            ViewModel.Model.PlayheadPosition = Preview.PlayheadPosition;
            _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition);
        };

        ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;

        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _timelineMapper = null;
                Timeline.ClearZoomSelection();
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

        // Export flyout state management
        ExportFlyout.Opened += ExportFlyout_Opened;
        ExportVM.PropertyChanged += ExportVM_PropertyChanged;
    }

    private async Task InitializePreviewAsync()
    {
        _frameReader?.Dispose();
        _previewRenderer?.Dispose();
        _frameReader = null;
        _previewRenderer = null;
        _compositorReady = false;
        _lastRenderedFrameIndex = -1;

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
            return;

        int fps = project.Fps > 0 ? project.Fps : 30;
        _frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, fps);
        if (_frameReader is null)
            return;

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
        float dpiScaleX = GetDpiScale(sourceW);
        float dpiScaleY = GetDpiScale(sourceH, isWidth: false);
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
                CenterX = (click.X * dpiScaleX) / sourceW,
                CenterY = (click.Y * dpiScaleY) / sourceH,
            });
        }

        Timeline.Refresh();

        // Build composition config with cursor effects enabled
        var config = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
        config = config with
        {
            OutputFps = Math.Min(fps, 30),
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
                project.MouseToVideoOffsetSeconds);
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

        // Map output (playhead) time to source time, accounting for speed/cut/trim edits
        TimeSpan sourcePosition = MapToSourceTime(position);

        int frameIndex = _frameReader.GetFrameIndex(sourcePosition);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourcePosition);
        if (bitmap is null) return;

        try
        {
            if (_compositorReady && _previewRenderer is not null)
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
        }

        Timeline.Refresh();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
            InvalidatePreview();
        });
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double speed))
        {
            ViewModel.SelectedSpeed = speed;
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

                // Update center sliders
                ZoomCenterXSlider.Value = kf.CenterX * 100;
                ZoomCenterYSlider.Value = kf.CenterY * 100;

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
            float dpiX = GetDpiScale(sourceW);
            float dpiY = GetDpiScale(sourceH, isWidth: false);

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
            cx = (closest.X * dpiX) / sourceW;
            cy = (closest.Y * dpiY) / sourceH;
        }

        var operation = new AddZoomSegmentOperation(e.Start, e.End, zoomLevel,
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1));
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the newly created segment
        Timeline.SelectedZoomKeyframeId = operation.CreatedId;
        OnZoomSegmentSelected(this, operation.CreatedId);
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

        var operation = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoomLevel);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void ZoomCenterSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressZoomPropertyUpdate) return;
        if (Timeline is null) return;
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;

        double cx = ZoomCenterXSlider.Value / 100.0;
        double cy = ZoomCenterYSlider.Value / 100.0;

        var operation = new UpdateZoomSegmentPropertiesOperation(selectedId, centerX: cx, centerY: cy);
        ViewModel.UndoRedoManager.Execute(operation);
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
