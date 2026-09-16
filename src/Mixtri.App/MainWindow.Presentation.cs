using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Interop;
using Mixtri.Core.Settings;
using Mixtri.Core.Shell;
using Windows.Graphics;

namespace Mixtri_App;

public sealed partial class MainWindow
{
    private Task? _firstPresentation;
    private FullWindowPlacement? _normalPlacement;
    private bool _restoreMaximized;
    private bool _lastWasMaximized;

    private void InitializePlacement()
    {
        if (App.Current.IsEditorProcess && ShellSettings.Instance.FullWindowPlacement is { } saved)
        {
            var area = DisplayArea.GetFromRect(
                new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest).WorkArea;
            var placement = saved.FitToWorkArea(area.X, area.Y, area.Width, area.Height);
            AppWindow.MoveAndResize(new(placement.X, placement.Y, placement.Width, placement.Height));
            _restoreMaximized = placement.Maximized;
        }
        TrackNormalPlacement();
        AppWindow.Changed += (_, args) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized } presenter)
                _lastWasMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (args.DidPositionChange || args.DidSizeChange) TrackNormalPlacement();
        };
    }

    private void TrackNormalPlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (size.Width > 0 && size.Height > 0)
            _normalPlacement = new(position.X, position.Y, size.Width, size.Height, false);
    }

    public void SavePlacement()
    {
        if (_normalPlacement is not { } placement || !App.Current.IsEditorProcess) return;
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }
            || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } && _lastWasMaximized;
        ShellSettings.Instance.FullWindowPlacement = placement with { Maximized = maximized };
    }

    public Task ShowPreparedAsync()
    {
        if (_firstPresentation is null) return _firstPresentation = PresentFirstFrameAsync();
        if (!_firstPresentation.IsCompleted) return _firstPresentation;
        if (_firstPresentation.IsFaulted) return _firstPresentation = PresentFirstFrameAsync();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.IsIconic(hwnd) ? 9 : 5);
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);
        return UpdateResourceVisibilityAsync(true);
    }

    private async Task PresentFirstFrameAsync()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int renderedFrames = 0;
        void OnRendering(object? sender, object args)
        {
            if (Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }
                && ++renderedFrames >= 2)
                ready.TrySetResult();
        }
        uint cloak = 1;
        bool shown = false;
        AppWindow.IsShownInSwitchers = false;
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(
            NativeMethods.DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int)));
        CompositionTarget.Rendering += OnRendering;
        try
        {
            // XAML renders behind the DWM cloak; only the populated first frame is revealed.
            NativeMethods.ShowWindow(hwnd, 5);
            Activate();
            if (_restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
            await UpdateResourceVisibilityAsync(true);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            cloak = 0;
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(
                NativeMethods.DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int)));
            AppWindow.IsShownInSwitchers = true;
            NativeMethods.SetForegroundWindow(hwnd);
            shown = true;
            DiagLog.Write("ShellProcess", $"PID {Environment.ProcessId}: populated full window presented");
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            if (!shown)
            {
                NativeMethods.ShowWindow(hwnd, 0);
                cloak = 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int));
            }
        }
    }
}
