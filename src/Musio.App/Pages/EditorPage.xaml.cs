using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    private VideoFrameReader? _frameReader;
    private PreviewRenderer? _previewRenderer;
    private bool _compositorReady;
    private int _lastRenderedFrameIndex = -1;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        InitializeComponent();

        Preview.Duration = ViewModel.Model.EffectiveDuration;

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
                Preview.Duration = ViewModel.Model.EffectiveDuration;
                Timeline.Refresh();
                ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
                _ = InitializePreviewAsync();
            });
        };
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

        // Build zoom keyframes from click events so they appear on the timeline
        int sourceW = project.Width > 0 ? project.Width : 1920;
        int sourceH = project.Height > 0 ? project.Height : 1080;
        float dpiScaleX = GetDpiScale(sourceW);
        float dpiScaleY = GetDpiScale(sourceH, isWidth: false);
        ViewModel.Model.ZoomKeyframes.Clear();
        foreach (var click in mouseData.Clicks.Where(c => c.IsDown))
        {
            double clickTime = (click.TimestampTicks - mouseData.StartTimestampTicks) / mouseData.TickFrequency;
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
                ClickHighlightEnabled = false,
                AutoHideEnabled = true,
                AutoHideDelaySeconds = 3.0f,
            },
            Zoom = new AutoZoomConfig { Enabled = true },
        };

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

    private async Task UpdatePreviewFrameAsync(TimeSpan position)
    {
        if (_frameReader is null) return;

        int frameIndex = _frameReader.GetFrameIndex(position);
        if (frameIndex == _lastRenderedFrameIndex) return;
        _lastRenderedFrameIndex = frameIndex;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(position);
        if (bitmap is null) return;

        try
        {
            if (_compositorReady && _previewRenderer is not null)
            {
                // Run frame through compositor — applies cursor, zoom, background
                var composed = _previewRenderer.RenderPreviewFrame(bitmap, frameIndex);
                bitmap.Dispose();

                if (composed is not null)
                {
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
            Preview.SetFrame(renderTarget);
        }
        catch
        {
            bitmap.Dispose();
        }
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Preview.Duration = ViewModel.Model.EffectiveDuration;
            Timeline.Refresh();
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
        ViewModel.DeleteSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
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
}
