using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Processing;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    private VideoFrameReader? _frameReader;
    private int _lastRenderedFrameIndex = -1;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        InitializeComponent();

        // Set initial duration on preview
        Preview.Duration = ViewModel.Model.EffectiveDuration;

        // Try to load recorded frames for preview
        LoadFrameReader();

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

        // When a new project is loaded, re-bind timeline and preview
        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Preview.Duration = ViewModel.Model.EffectiveDuration;
                Timeline.Refresh();
                ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
                LoadFrameReader();
            });
        };

        // Show first frame
        _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
    }

    private void LoadFrameReader()
    {
        _frameReader?.Dispose();
        _frameReader = null;
        _lastRenderedFrameIndex = -1;

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
            return;

        _frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, project.Fps > 0 ? project.Fps : 30);
    }

    private async Task UpdatePreviewFrameAsync(TimeSpan position)
    {
        if (_frameReader is null) return;

        int frameIndex = _frameReader.GetFrameIndex(position);
        if (frameIndex == _lastRenderedFrameIndex) return;
        _lastRenderedFrameIndex = frameIndex;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(position);
        if (bitmap is null) return;

        // Create a render target from the bitmap for the preview canvas
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
}
