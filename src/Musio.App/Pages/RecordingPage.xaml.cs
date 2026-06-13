using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Controls;
using Musio_App.Services;
using Musio_App.Shell;
using Musio_App.ViewModels;
using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio_App.Pages;

public sealed partial class RecordingPage : Page
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private RegionBorderHighlight? _regionBorder;

    private System.ComponentModel.PropertyChangedEventHandler? _viewModelHandler;

    public RecordingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bridge the Mini Setup toolbar's events into the page-level
        // orchestration (state-machine transitions + transient InfoBar +
        // selection-metadata caption, which Phase B moved out of the
        // compact toolbar so it could hit the 64 px height target).
        Toolbar.RecordRequested += OnToolbarRecordRequested;
        Toolbar.TransientInfoRequested += OnToolbarTransientInfoRequested;
        Toolbar.SelectionMetadataChanged += OnToolbarSelectionMetadataChanged;
    }

    private void OnToolbarSelectionMetadataChanged(object? sender, string? text)
    {
        if (SelectionMetadataText is null) return;
        if (string.IsNullOrEmpty(text))
        {
            SelectionMetadataText.Visibility = Visibility.Collapsed;
            return;
        }
        SelectionMetadataText.Text = text;
        SelectionMetadataText.Visibility = Visibility.Visible;
    }

    private void HeroRecordButton_Click(object sender, RoutedEventArgs e)
    {
        // The hero Record button in the page mirrors the toolbar's inline
        // Record button (which RecordingPage suppresses via
        // ShowInlineRecordButton="False"). Both share the same orchestration
        // path so the state machine + StartRecordingCommand wiring stays in
        // one place.
        OnToolbarRecordRequested(sender, EventArgs.Empty);
    }

    // x:Bind helper: inverted bool → Visibility (used by the hero Record button).
    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Re-point the shared VM's dispatcher at this page's UI thread (cheap;
        // safe to call repeatedly).
        ViewModel.SetDispatcher(DispatcherQueue);

        // Wire up IsRecording observation per-navigation; tear down in
        // OnNavigatedFrom so the VM doesn't accumulate handlers from prior page
        // instances. The shell window owns the morph; this page is only
        // responsible for the region-border highlight + post-stop nav.
        _viewModelHandler = (_, args) =>
        {
            if (args.PropertyName != nameof(RecordingViewModel.IsRecording)) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.IsRecording)
                {
                    ShowRegionBorderIfNeeded();
                    // Open the capture gate now that the shell has finished
                    // morphing to MiniRecording / FullRecording — all frames
                    // and audio before this point are discarded, eliminating
                    // the startup delta.
                    ViewModel.OpenCaptureGate();
                }
                else
                {
                    HideRegionBorder();

                    if (ViewModel.LastProject is not null)
                        Frame.Navigate(typeof(EditorPage));
                }
            });
        };
        ViewModel.PropertyChanged += _viewModelHandler;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (_viewModelHandler is not null)
        {
            ViewModel.PropertyChanged -= _viewModelHandler;
            _viewModelHandler = null;
        }

        // Detach the InfoBar timer so it can't fire (and keep this page alive)
        // after navigation. The timer is created on this page's DispatcherQueue
        // and retains OnInfoBarTimerTick.
        if (_infoBarTimer is not null)
        {
            _infoBarTimer.Stop();
            _infoBarTimer.Tick -= OnInfoBarTimerTick;
        }
        if (RecordingInfoBar is not null)
            RecordingInfoBar.IsOpen = false;

        base.OnNavigatedFrom(e);
    }

    private async void OnToolbarRecordRequested(object? sender, EventArgs e)
    {
        try
        {
            // Phase B: drive the shell state machine instead of minimising
            // the window + spawning a separate RecordingOverlayWindow. The
            // shell decides whether to morph to MiniRecording (from Mini /
            // Full) and starts recording itself when invoked from its own
            // toolbar — but this page hosts its own MiniSetupControl
            // instance, so we must drive the transition + start the
            // recording from here too.
            var shell = App.Current.Shell;
            if (shell is null) return;

            // Always end up in MiniRecording on Record-press (matches spec
            // §3.6 — recording from Full collapses to top-center pill).
            await shell.TransitionToAsync(AppShellState.MiniRecording);

            // Re-check state after the await: if Stop fired or any other
            // transition took us out of MiniRecording during the morph, do
            // NOT start a recording — we'd otherwise spawn one with no pill
            // visible to stop it.
            if (shell.CurrentState == AppShellState.MiniRecording && !ViewModel.IsRecording)
                ViewModel.StartRecordingCommand.Execute(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordingPage] OnToolbarRecordRequested failed: {ex.Message}");
        }
    }

    private void OnToolbarTransientInfoRequested(object? sender, string message)
    {
        ShowTransientInfo(message);
    }

    private void ShowRegionBorderIfNeeded()
    {
        // Show a border around the selected region so the user can see
        // what area is being captured. The region coordinates are in DIP
        // (logical pixels) from the selector overlay, so scale them to
        // physical screen pixels for the native Win32 border windows.
        if (ViewModel.CaptureMode != CaptureMode.CustomRegion
            || ViewModel.SelectedRegion is not CaptureRegion region
            || region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        _regionBorder?.Dispose();
        _regionBorder = new RegionBorderHighlight();
        float dpiScale = GetRegionMonitorDpiScale(region);
        var (monLeft, monTop) = GetRegionMonitorOrigin(region);
        // region.X/Y are monitor-local DIPs. Convert to monitor-local
        // physical pixels and offset by the monitor's screen-absolute
        // physical origin so the Win32 border windows land on the
        // correct monitor. Use Math.Round + even-dimension flooring to
        // match the crop rect computed by RecordingSession (which rounds
        // origin to int and floors W/H to multiples of 2 for H.264).
        int px = monLeft + (int)Math.Round(region.X * dpiScale);
        int py = monTop + (int)Math.Round(region.Y * dpiScale);
        int pw = ((int)(region.Width * dpiScale)) & ~1;
        int ph = ((int)(region.Height * dpiScale)) & ~1;
        if (pw < 2) pw = 2;
        if (ph < 2) ph = 2;
        _regionBorder.Show(px, py, pw, ph);
    }

    private void HideRegionBorder()
    {
        _regionBorder?.Dispose();
        _regionBorder = null;
    }

    // x:Bind helper: bool → Visibility (used by the status bar)
    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _infoBarTimer;

    private void ShowTransientInfo(string message)
    {
        if (RecordingInfoBar is null) return;
        RecordingInfoBar.Severity = InfoBarSeverity.Informational;
        RecordingInfoBar.Title = string.Empty;
        RecordingInfoBar.Message = message;
        RecordingInfoBar.IsOpen = true;

        // Auto-dismiss after a few seconds so the bar doesn't linger forever.
        _infoBarTimer?.Stop();
        _infoBarTimer ??= DispatcherQueue.CreateTimer();
        _infoBarTimer.Interval = TimeSpan.FromSeconds(4);
        _infoBarTimer.IsRepeating = false;
        _infoBarTimer.Tick -= OnInfoBarTimerTick;
        _infoBarTimer.Tick += OnInfoBarTimerTick;
        _infoBarTimer.Start();
    }

    private void OnInfoBarTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (RecordingInfoBar is not null)
            RecordingInfoBar.IsOpen = false;
        sender.Stop();
    }

    private static CaptureTarget? FindMonitorForRegion(CaptureRegion region)
    {
        var monitors = MonitorEnumerator.GetAllMonitors();
        // Exact match against the raw device name. DisplayName is either
        // "\\.\DISPLAY1" or "\\.\DISPLAY1 (Primary)". Using Contains would
        // make "\\.\DISPLAY1" wrongly match "\\.\DISPLAY10".
        return monitors.FirstOrDefault(m =>
                m.DisplayName == region.MonitorId
                || m.DisplayName.StartsWith(region.MonitorId + " "))
            ?? monitors.FirstOrDefault();
    }

    /// <summary>
    /// Resolves the DPI scale for the monitor that owns the given region.
    /// Uses the same monitor-matching logic as <see cref="RecordingViewModel.BuildCaptureTarget"/>.
    /// </summary>
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
