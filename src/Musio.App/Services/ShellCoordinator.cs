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
    private SelectionHighlight? _highlight;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _windowTracker;

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
        {
            // Refreshes the preview itself.
            ShowMiniSurface();
        }
        else
        {
            ShowFullSurface();
        }
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

    /// <summary>
    /// Whether a system tray icon exists to summon the app back. Tray setup is
    /// best-effort (see <see cref="App"/>), and without it a hidden shell would be
    /// unreachable — the Mini pill is not in Alt-Tab and the full window is
    /// <c>SW_HIDE</c>-den — so the hide-to-tray paths fall back to the full window.
    /// </summary>
    public bool IsTrayAvailable { get; set; }

    /// <summary>Parks the shell in the tray without changing which surface is "current".</summary>
    public void HideToTray()
    {
        if (!IsTrayAvailable)
        {
            // Nothing could bring the app back, so surface the full window instead
            // of stranding the user with no reachable window.
            ShowFullWindow();
            return;
        }

        _miniWindow?.HideMini();
        HideMainWindow();
        UpdateSelectionPreview();
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
    /// </remarks>
    public void HideForPicker()
    {
        if (_isPickerHiding) return;
        _isPickerHiding = true;

        // The preview border would otherwise sit on top of the picker's
        // full-screen screenshot and get baked into the next one it takes.
        StopWindowTracking();
        _highlight?.Hide();

        if (_stateMachine.CurrentState == AppShellState.Mini)
            _miniWindow?.HideMini();
        else
            MinimizeMainWindow();
    }

    /// <summary>Puts back whatever <see cref="HideForPicker"/> took away.</summary>
    public void RestoreAfterPicker()
    {
        if (!_isPickerHiding) return;
        _isPickerHiding = false;

        // A recording that began while the picker was up owns the screen now.
        if (_stateMachine.CurrentState == AppShellState.Recording) return;

        ApplyState(_stateMachine.CurrentState);
    }

    /// <summary>
    /// Whether a picker has hidden the shell. A plain latch rather than a reference
    /// count on purpose: a count that ever got out of step (two surfaces opening a
    /// picker, an unbalanced restore) would leave every window hidden with no way
    /// back, whereas a latch always restores on the first release.
    /// </summary>
    private bool _isPickerHiding;

    /// <summary>
    /// Brings up the full window. Goes through the state machine rather than poking
    /// the HWND, otherwise the shell would think Mini is still current and later
    /// hide the full window out from under the user.
    /// </summary>
    public void ShowFullWindow()
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
                // ShowMiniSurface refreshes the preview itself.
                ShowMiniSurface();
                break;

            case AppShellState.Full:
                _miniWindow?.HideMini();
                ShowFullSurface();
                // The preview belongs to the Mini surface, so take it away here.
                UpdateSelectionPreview();
                break;

            case AppShellState.Recording:
                _miniWindow?.HideMini();
                MinimizeMainWindow();
                // OnRecordingBegan puts up the recording highlight instead.
                break;
        }
    }

    #endregion

    #region Surfaces

    private void ShowMiniSurface()
    {
        _miniWindow ??= CreateMiniWindow();
        _miniWindow.ShowMini();

        // Refreshed here rather than at each call site so the preview comes back no
        // matter how the pill was summoned — collapse from the full window, the tray
        // icon, launch, or a picker unwinding.
        UpdateSelectionPreview();
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
        if (_mainWindow is null) return;

        switch (e.PropertyName)
        {
            case nameof(RecordingViewModel.IsRecording):
                _mainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_viewModel.IsRecording)
                        OnRecordingBegan();
                    else
                        OnRecordingEnded();
                });
                break;

            // The selection preview reflects these, and they can change from the
            // Mini pill, the Record page, or a picker.
            case nameof(RecordingViewModel.CaptureMode):
            case nameof(RecordingViewModel.SelectedRegion):
            case nameof(RecordingViewModel.SelectedWindow):
            case nameof(RecordingViewModel.HasSelectedRegion):
                _mainWindow.DispatcherQueue.TryEnqueue(UpdateSelectionPreview);
                break;
        }
    }

    private void OnRecordingBegan()
    {
        // Swap the accent preview highlight for the red recording one.
        StopWindowTracking();
        ShowHighlight(HighlightStyle.Recording);

        _overlay = new RecordingOverlayWindow(_viewModel);
        _overlay.StopRequested += OnOverlayStopRequested;
        _overlay.Activate();

        // The overlay sits outside the selection, so lift it clear of the smoke.
        _highlight?.KeepAbove(WinRT.Interop.WindowNative.GetWindowHandle(_overlay));

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

        StopWindowTracking();
        _highlight?.Hide();
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
    /// Draws a border around whatever is about to be captured, so the user can see
    /// their selection without opening a picker. Region coordinates are monitor-local
    /// DIPs from the selector overlay, so they are scaled to physical pixels and
    /// offset by the monitor's origin; window bounds are already physical pixels.
    /// </summary>
    private bool ShowHighlight(HighlightStyle style)
    {
        switch (_viewModel.CaptureMode)
        {
            case CaptureMode.CustomRegion:
            {
                if (_viewModel.SelectedRegion is not { } region) return false;
                if (region.Width <= 0 || region.Height <= 0) return false;

                // No falling back to the primary monitor: the region's coordinates
                // are monitor-local, so if its display is gone they would place the
                // highlight on the wrong screen, over content that will never be
                // captured. RecordingViewModel.BuildCaptureTarget refuses the same
                // case at record time; showing nothing here matches that.
                if (!TryResolveRegionMonitor(region, out int monLeft, out int monTop, out float dpiScale))
                    return false;

                // Math.Round on the origin and even-flooring on the size mirror the
                // crop rect computed by RecordingSession (H.264 needs even
                // dimensions), so the border matches the recorded frame exactly.
                int px = monLeft + (int)Math.Round(region.X * dpiScale);
                int py = monTop + (int)Math.Round(region.Y * dpiScale);
                int pw = ((int)(region.Width * dpiScale)) & ~1;
                int ph = ((int)(region.Height * dpiScale)) & ~1;
                if (pw < 2) pw = 2;
                if (ph < 2) ph = 2;

                _highlight ??= new SelectionHighlight();
                _highlight.ShowRect(px, py, pw, ph, style);
                return true;
            }

            case CaptureMode.Window:
            {
                if (_viewModel.SelectedWindow is not { } window) return false;

                _highlight ??= new SelectionHighlight();
                _highlight.ShowWindow(window.Handle, style);
                return _highlight.IsShown;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Shows the accent-coloured preview border when the Mini pill is the visible
    /// surface and something is selected, and takes it away otherwise.
    /// </summary>
    /// <remarks>
    /// Mini deliberately shows no selection text, so this border is the only thing
    /// telling the user what Record is about to capture. Scoped to Mini because the
    /// full Record page prints the selection underneath its toolbar instead.
    /// </remarks>
    private void UpdateSelectionPreview()
    {
        if (_isDisposed) return;

        bool wanted = _stateMachine.CurrentState == AppShellState.Mini
                      && !_viewModel.IsRecording
                      && !_isPickerHiding
                      && _miniWindow is { IsVisible: true };

        if (!wanted)
        {
            StopWindowTracking();
            _highlight?.Hide();
            return;
        }

        if (!ShowHighlight(HighlightStyle.Preview))
        {
            StopWindowTracking();
            _highlight?.Hide();
            return;
        }

        // The smoke would otherwise dim the pill along with the rest of the desktop.
        if (_miniWindow is not null)
            _highlight?.KeepAbove(WinRT.Interop.WindowNative.GetWindowHandle(_miniWindow));

        // A window can be moved or resized while the pill is up, so follow it.
        if (_highlight?.TrackedWindow != IntPtr.Zero)
            StartWindowTracking();
        else
            StopWindowTracking();
    }

    private void StartWindowTracking()
    {
        if (_mainWindow is null) return;

        _windowTracker ??= _mainWindow.DispatcherQueue.CreateTimer();
        if (_windowTracker.IsRunning) return;

        _windowTracker.Interval = TimeSpan.FromMilliseconds(WindowTrackIntervalMs);
        _windowTracker.IsRepeating = true;
        _windowTracker.Tick -= OnWindowTrackerTick;
        _windowTracker.Tick += OnWindowTrackerTick;
        _windowTracker.Start();
    }

    private void StopWindowTracking()
    {
        if (_windowTracker is null) return;
        _windowTracker.Stop();
        _windowTracker.Tick -= OnWindowTrackerTick;
    }

    private void OnWindowTrackerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // Returns false once the target window is closed, hidden or minimised —
        // the highlight hides itself, so just stop polling.
        if (_highlight is null || !_highlight.RefreshTrackedWindow(HighlightStyle.Preview))
            StopWindowTracking();
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ErrorRaised -= OnViewModelErrorRaised;

        TearDownRecordingChrome();
        StopWindowTracking();
        _windowTracker = null;
        _highlight?.Dispose();
        _highlight = null;

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

    /// <summary>How often the preview border re-reads a tracked window's bounds.</summary>
    private const int WindowTrackIntervalMs = 150;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    /// <summary>
    /// Resolves the origin and DPI scale of the monitor that owns
    /// <paramref name="region"/>, returning false when that display is no longer
    /// connected.
    /// </summary>
    /// <remarks>
    /// Deliberately has no primary-monitor fallback — see the call site.
    /// </remarks>
    private static bool TryResolveRegionMonitor(
        CaptureRegion region, out int left, out int top, out float dpiScale)
    {
        left = 0;
        top = 0;
        dpiScale = 1.0f;

        // Exact match against the raw device name. DisplayName is either
        // "\\.\DISPLAY1" or "\\.\DISPLAY1 (Primary)", so Contains would wrongly
        // match "\\.\DISPLAY1" against "\\.\DISPLAY10".
        var monitor = MonitorEnumerator.GetAllMonitors().FirstOrDefault(m =>
            m.DisplayName == region.MonitorId
            || m.DisplayName.StartsWith(region.MonitorId + " "));

        if (monitor is null || monitor.Handle == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor.Handle, ref info)) return false;

        left = info.rcMonitor.Left;
        top = info.rcMonitor.Top;

        if (GetDpiForMonitor(monitor.Handle, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            dpiScale = dpiX / 96.0f;

        return true;
    }

    private const int MDT_EFFECTIVE_DPI = 0;

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
