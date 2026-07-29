using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Musio.Core.Capture;
using Musio.Core.Settings;
using Musio.Core.Shell;
using Musio_App.Pages;
using Musio_App.ViewModels;

namespace Musio_App.Services;

/// <summary>
/// Owns the app's window shell: the compact <see cref="MiniWindow"/>, the full
/// <see cref="MainWindow"/>, and the recording overlay. Exactly one surface is on
/// screen at a time, decided by <see cref="AppShellStateMachine"/>.
/// </summary>
/// <remarks>
/// Recording lifecycle used to live in <see cref="RecordingPage"/>, but Mini mode
/// can start a recording without that page ever being loaded, so ownership moved
/// here. Both surfaces drive the one shared <see cref="RecordingViewModel"/>.
/// </remarks>
public sealed class ShellCoordinator : IDisposable
{
    private static ShellCoordinator? _instance;

    /// <summary>The live coordinator. Null until <see cref="App"/> creates it on launch.</summary>
    public static ShellCoordinator? Instance => _instance;

    private readonly AppShellStateMachine _stateMachine;
    private readonly RecordingViewModel _viewModel = RecordingViewModel.Shared;

    private MainWindow? _mainWindow;
    private MiniWindow? _miniWindow;
    private RecordingOverlayWindow? _overlay;
    private RegionBorderHighlight? _regionBorder;

    private bool _isDisposed;

    public ShellCoordinator(MainWindow mainWindow, StartupMode startupMode)
    {
        _mainWindow = mainWindow;
        _stateMachine = new AppShellStateMachine(
            startupMode == StartupMode.Mini ? AppShellState.Mini : AppShellState.Full);

        // The view model raises property changes from background capture threads;
        // point it at the shell's UI thread once, here, so both surfaces are safe.
        _viewModel.SetDispatcher(mainWindow.DispatcherQueue);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ErrorRaised += OnViewModelErrorRaised;

        _instance = this;
    }

    /// <summary>The surface that should currently be visible.</summary>
    public AppShellState CurrentState => _stateMachine.CurrentState;

    /// <summary>Shows whichever surface the configured startup mode calls for.</summary>
    public void Start()
    {
        if (_stateMachine.CurrentState == AppShellState.Mini)
            ShowMiniSurface();
        else
            ShowFullSurface();
    }

    #region Transitions

    /// <summary>Mini → Full, triggered by the Expand button on the pill.</summary>
    public void ExpandToFull() => Apply(AppShellTrigger.Expand);

    /// <summary>Full → Mini, triggered by the Collapse button in the app title bar.</summary>
    public void CollapseToMini() => Apply(AppShellTrigger.Collapse);

    /// <summary>Tray icon click: bring back the Mini pill (ignored mid-recording).</summary>
    public void ActivateFromTray()
    {
        if (!Apply(AppShellTrigger.TrayActivated))
        {
            // Already in the target state — re-show it anyway, since the user may
            // have parked the pill in the tray and is asking for it back.
            if (_stateMachine.CurrentState == AppShellState.Mini)
                ShowMiniSurface();
        }
    }

    /// <summary>Parks the shell in the tray without changing which surface is "current".</summary>
    public void HideToTray()
    {
        _miniWindow?.HideMini();
        HideMainWindow();
    }

    /// <summary>
    /// Clears whichever surface is on screen so a full-screen picker overlay can
    /// take a clean desktop screenshot.
    /// </summary>
    /// <remarks>
    /// The pickers used to minimise <see cref="MainWindow"/> directly and restore
    /// it afterwards. That is wrong under Mini mode: the full window is hidden,
    /// not minimised, so restoring it would pop it back up alongside the pill.
    /// Routing through the coordinator restores whatever was actually showing.
    /// Reference-counted because a picker can be launched while another is
    /// unwinding.
    /// </remarks>
    public void HideForPicker()
    {
        if (_pickerDepth++ > 0) return;

        if (_stateMachine.CurrentState == AppShellState.Mini)
            _miniWindow?.HideMini();
        else
            MinimizeMainWindow();
    }

    /// <summary>Puts back whatever <see cref="HideForPicker"/> took away.</summary>
    public void RestoreAfterPicker()
    {
        if (_pickerDepth == 0) return;
        if (--_pickerDepth > 0) return;

        // A recording that began while the picker was up owns the screen now.
        if (_stateMachine.CurrentState == AppShellState.Recording) return;

        ApplyState(_stateMachine.CurrentState);
    }

    private int _pickerDepth;

    /// <summary>
    /// Tray "Open Musio": bring up the full window. Goes through the state
    /// machine rather than poking the HWND, otherwise the shell would think Mini
    /// is still current and later hide the full window out from under the user.
    /// </summary>
    public void ShowFullFromTray()
    {
        if (_stateMachine.CurrentState == AppShellState.Recording) return;

        if (!Apply(AppShellTrigger.Expand))
        {
            // Already in Full — nothing to transition, just surface it again.
            ShowFullSurface();
        }
    }

    private bool Apply(AppShellTrigger trigger)
    {
        if (!_stateMachine.TryApply(trigger, out var newState))
            return false;

        ApplyState(newState);
        return true;
    }

    private void ApplyState(AppShellState state)
    {
        switch (state)
        {
            case AppShellState.Mini:
                HideMainWindow();
                ShowMiniSurface();
                break;

            case AppShellState.Full:
                _miniWindow?.HideMini();
                ShowFullSurface();
                break;

            case AppShellState.Recording:
                _miniWindow?.HideMini();
                MinimizeMainWindow();
                break;
        }
    }

    #endregion

    #region Surfaces

    private void ShowMiniSurface()
    {
        _miniWindow ??= CreateMiniWindow();
        _miniWindow.ShowMini();
    }

    private MiniWindow CreateMiniWindow()
    {
        var window = new MiniWindow();
        window.RecordRequested += (_, _) => _ = StartRecordingAsync();
        window.ExpandRequested += (_, _) => ExpandToFull();
        window.HideRequested += (_, _) => HideToTray();
        return window;
    }

    private void ShowFullSurface()
    {
        if (_mainWindow is null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        ShowWindow(hwnd, SW_SHOW);
        ShowWindow(hwnd, SW_RESTORE);
        _mainWindow.Activate();
    }

    private void HideMainWindow()
    {
        if (_mainWindow is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void MinimizeMainWindow()
    {
        if (_mainWindow is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        ShowWindow(hwnd, SW_MINIMIZE);
    }

    #endregion

    #region Recording

    /// <summary>
    /// Clears the shell off screen, waits for the hide/minimize animation to
    /// finish so it can't be captured, then starts the recording.
    /// </summary>
    public async Task StartRecordingAsync()
    {
        if (_viewModel.IsRecording) return;

        try
        {
            // Enter Recording up front: the windows have to be gone *before* the
            // capture starts, and a failed start rewinds via RecordingFailed.
            Apply(AppShellTrigger.RecordingStarted);

            await Task.Delay(WindowHideSettleMs);

            _viewModel.StartRecordingCommand.Execute(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShellCoordinator] Start failed: {ex.Message}");
            Apply(AppShellTrigger.RecordingFailed);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingViewModel.IsRecording)) return;
        if (_mainWindow is null) return;

        _mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (_viewModel.IsRecording)
                OnRecordingBegan();
            else
                OnRecordingEnded();
        });
    }

    private void OnRecordingBegan()
    {
        ShowRegionBorder();

        _overlay = new RecordingOverlayWindow(_viewModel);
        _overlay.StopRequested += OnOverlayStopRequested;
        _overlay.Activate();

        // Everything captured before this point is discarded, so opening the gate
        // only once the overlay is up removes the startup delta.
        _viewModel.OpenCaptureGate();
    }

    private void OnRecordingEnded()
    {
        TearDownRecordingChrome();

        var project = _viewModel.LastProject;
        if (project is null)
        {
            // Stop failed or produced nothing — put the user back where they were.
            Apply(AppShellTrigger.RecordingFailed);
            return;
        }

        if (_viewModel.IsAppendMode)
            ProjectService.Instance.AppendRecording(project);

        // Append mode is a one-shot instruction from the editor's "Record More"
        // button. Clearing it here matters because the Mini window can start the
        // next recording without the Record page ever re-running its navigation
        // handler, which is what used to reset the flag.
        _viewModel.IsAppendMode = false;

        // A finished take always hands off to the full app so the user lands in
        // the editor, even when they recorded from the Mini pill.
        Apply(AppShellTrigger.RecordingStopped);
        _mainWindow?.ShowEditor();
    }

    private void OnOverlayStopRequested(object? sender, EventArgs e)
    {
        if (_viewModel.IsRecording)
            _viewModel.StopRecordingCommand.Execute(null);
    }

    private void TearDownRecordingChrome()
    {
        if (_overlay is not null)
        {
            _overlay.StopRequested -= OnOverlayStopRequested;
            _overlay.CloseOverlay();
            _overlay = null;
        }

        _regionBorder?.Dispose();
        _regionBorder = null;
    }

    private void OnViewModelErrorRaised(object? sender, string message)
    {
        if (_mainWindow is null) return;

        _mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            // Leave an in-flight recording alone; its own stop path rewinds the
            // shell. This branch is for a start that bailed out before capture
            // began, which would otherwise strand the user with no window.
            if (!_viewModel.IsRecording && _stateMachine.CurrentState == AppShellState.Recording)
            {
                TearDownRecordingChrome();
                Apply(AppShellTrigger.RecordingFailed);
            }

            SurfaceError(message);
        });
    }

    private void SurfaceError(string message)
    {
        if (_stateMachine.CurrentState == AppShellState.Mini)
            _miniWindow?.ShowStatus(message, isTransient: false);
        else
            _mainWindow?.ShowRecordingError(message);
    }

    /// <summary>
    /// Draws a border around the captured region so the user can see the area.
    /// Region coordinates are monitor-local DIPs from the selector overlay, so
    /// they are scaled to physical pixels and offset by the monitor's origin.
    /// </summary>
    private void ShowRegionBorder()
    {
        if (_viewModel.CaptureMode != CaptureMode.CustomRegion) return;
        if (_viewModel.SelectedRegion is not { } region) return;
        if (region.Width <= 0 || region.Height <= 0) return;

        _regionBorder = new RegionBorderHighlight();

        float dpiScale = GetRegionMonitorDpiScale(region);
        var (monLeft, monTop) = GetRegionMonitorOrigin(region);

        // Math.Round on the origin and even-flooring on the size mirror the crop
        // rect computed by RecordingSession (H.264 needs even dimensions), so the
        // border matches the recorded frame exactly.
        int px = monLeft + (int)Math.Round(region.X * dpiScale);
        int py = monTop + (int)Math.Round(region.Y * dpiScale);
        int pw = ((int)(region.Width * dpiScale)) & ~1;
        int ph = ((int)(region.Height * dpiScale)) & ~1;
        if (pw < 2) pw = 2;
        if (ph < 2) ph = 2;

        _regionBorder.Show(px, py, pw, ph);
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ErrorRaised -= OnViewModelErrorRaised;

        TearDownRecordingChrome();

        try { _miniWindow?.CloseMini(); } catch { }
        _miniWindow = null;
        _mainWindow = null;

        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    /// <summary>
    /// How long to wait after hiding/minimising the shell before capture starts,
    /// so the window animation is never recorded.
    /// </summary>
    private const int WindowHideSettleMs = 600;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    private static CaptureTarget? FindMonitorForRegion(CaptureRegion region)
    {
        var monitors = MonitorEnumerator.GetAllMonitors();
        // Exact match against the raw device name. DisplayName is either
        // "\\.\DISPLAY1" or "\\.\DISPLAY1 (Primary)", so Contains would wrongly
        // match "\\.\DISPLAY1" against "\\.\DISPLAY10".
        return monitors.FirstOrDefault(m =>
                m.DisplayName == region.MonitorId
                || m.DisplayName.StartsWith(region.MonitorId + " "))
            ?? monitors.FirstOrDefault();
    }

    private static float GetRegionMonitorDpiScale(CaptureRegion region)
    {
        var monitor = FindMonitorForRegion(region);

        if (monitor is not null && monitor.Handle != IntPtr.Zero)
        {
            int hr = GetDpiForMonitor(monitor.Handle, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            if (hr == 0 && dpiX > 0)
                return dpiX / 96.0f;
        }

        return 1.0f;
    }

    private static (int Left, int Top) GetRegionMonitorOrigin(CaptureRegion region)
    {
        var monitor = FindMonitorForRegion(region);

        if (monitor is not null && monitor.Handle != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor.Handle, ref info))
                return (info.rcMonitor.Left, info.rcMonitor.Top);
        }

        return (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
