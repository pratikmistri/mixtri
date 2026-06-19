using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Controls;
using Musio_App.Services;
using Musio_App.ViewModels;
using Musio.Core.Capture;
using Musio.Core.Settings;
using Windows.Foundation;

namespace Musio_App.Pages;

public sealed partial class RecordingPage : Page
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private readonly RegionSelector _regionSelector = new();
    private RecordingOverlayWindow? _overlayWindow;
    private RegionBorderHighlight? _regionBorder;
    private bool _recordingMinimizedWindow;
    private bool _isPageLoading = true;
    private bool _isPickerOpen;

    private System.ComponentModel.PropertyChangedEventHandler? _viewModelHandler;

    public RecordingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private bool _isAppendMode;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isAppendMode = e.Parameter as string == "append";

        // Re-point the shared VM's dispatcher at this page's UI thread (cheap;
        // safe to call repeatedly).
        ViewModel.SetDispatcher(DispatcherQueue);

        // Wire up IsRecording observation per-navigation; tear down in
        // OnNavigatedFrom so the VM doesn't accumulate handlers from prior page
        // instances.
        _viewModelHandler = (_, args) =>
        {
            if (args.PropertyName != nameof(RecordingViewModel.IsRecording)) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.IsRecording)
                {
                    ShowRecordingOverlay();
                }
                else
                {
                    CloseRecordingOverlay();

                    if (ViewModel.LastProject is not null)
                    {
                        if (_isAppendMode)
                        {
                            // Append the new recording to the existing project
                            var newRecording = ViewModel.LastProject;
                            ProjectService.Instance.AppendRecording(newRecording);
                        }
                        Frame.Navigate(typeof(EditorPage));
                    }
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

    private async void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Minimize the main window before recording starts so the
            // minimize animation is never captured in the recording.
            var mainWindow = App.Current.MainAppWindow;
            if (mainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                ShowWindow(hwnd, SW_MINIMIZE);
                _recordingMinimizedWindow = true;
                // Wait for the minimize animation to finish
                await Task.Delay(600);
            }

            ViewModel.StartRecordingCommand.Execute(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordingPage] StartRecordButton_Click failed: {ex.Message}");
        }
    }

    private void ShowRecordingOverlay()
    {
        // Show a border around the selected region so the user can see
        // what area is being captured. The region coordinates are in DIP
        // (logical pixels) from the selector overlay, so scale them to
        // physical screen pixels for the native Win32 border windows.
        if (ViewModel.CaptureMode == CaptureMode.CustomRegion
            && ViewModel.SelectedRegion is CaptureRegion region
            && region.Width > 0 && region.Height > 0)
        {
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

        // Create and show the compact overlay
        _overlayWindow = new RecordingOverlayWindow(ViewModel);
        _overlayWindow.StopRequested += OnOverlayStopRequested;
        _overlayWindow.Activate();

        // Open the capture gate now that the overlay is visible.
        // All frames and audio before this point are discarded,
        // eliminating the startup delta.
        ViewModel.OpenCaptureGate();
    }

    private void CloseRecordingOverlay()
    {
        if (_overlayWindow is not null)
        {
            _overlayWindow.StopRequested -= OnOverlayStopRequested;
            _overlayWindow.CloseOverlay();
            _overlayWindow = null;
        }

        _regionBorder?.Dispose();
        _regionBorder = null;

        // Restore main window only if recording was what minimized it
        if (_recordingMinimizedWindow)
        {
            _recordingMinimizedWindow = false;
            var mainWindow = App.Current.MainAppWindow;
            if (mainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                ShowWindow(hwnd, SW_RESTORE);
                mainWindow.Activate();
            }
        }
    }

    private void OnOverlayStopRequested(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsRecording)
                ViewModel.StopRecordingCommand.Execute(null);
        });
    }

    // x:Bind helper: invert boolean
    public bool InvertBool(bool value) => !value;

    // x:Bind helper: bool → Visibility
    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    // x:Bind helper: inverted bool → Visibility
    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    // x:Bind helper: dim options grid while recording
    public double RecordingOpacity(bool isRecording) =>
        isRecording ? 0.4 : 1.0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureModeSelector.SelectedIndex = (int)ViewModel.CaptureMode;
        UpdateRegionPanelVisibility();
        _isPageLoading = false;
    }

    private async void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is FrameworkElement item && item.Tag is string tag)
        {
            ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
            UpdateRegionPanelVisibility();

            // Auto-launch the appropriate picker when the user selects a mode
            if (!_isPageLoading && !_isPickerOpen)
            {
                try
                {
                    if (ViewModel.CaptureMode == CaptureMode.Window)
                        await LaunchWindowPickerAsync();
                    else if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
                        await LaunchRegionPickerAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RecordingPage] Picker launch failed: {ex.Message}");
                }
            }
        }
    }

    private void UpdateRegionPanelVisibility()
    {
        if (RegionPanel is not null)
        {
            RegionPanel.Visibility = ViewModel.CaptureMode == CaptureMode.CustomRegion
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
                UpdateRegionInfoDisplay();
        }

        if (WindowPanel is not null)
        {
            WindowPanel.Visibility = ViewModel.CaptureMode == CaptureMode.Window
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ViewModel.CaptureMode == CaptureMode.Window)
                UpdateWindowInfoDisplay();
        }

        // Hide metadata when in FullScreen mode (no selection to show)
        if (ViewModel.CaptureMode == CaptureMode.FullScreen)
            HideSelectionMetadata();
    }

    private void UpdateRegionInfoDisplay()
    {
        if (ViewModel.HasSelectedRegion && ViewModel.SelectedRegion is not null)
        {
            var r = ViewModel.SelectedRegion;
            ShowSelectionMetadata($"Region: {r.Width}\u00d7{r.Height} at ({r.X}, {r.Y})");
            return;
        }

        var saved = _regionSelector.LoadLastRegion();
        if (saved is not null)
        {
            ViewModel.SelectedRegion = saved;
            ViewModel.HasSelectedRegion = true;
            ShowSelectionMetadata($"Region: {saved.Width}\u00d7{saved.Height} at ({saved.X}, {saved.Y})");
        }
        else
        {
            HideSelectionMetadata();
        }
    }

    private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchRegionPickerAsync();
    }

    private async void SelectWindowButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchWindowPickerAsync();
    }

    private async Task LaunchWindowPickerAsync()
    {
        if (_isPickerOpen) return;
        _isPickerOpen = true;

        try
        {
            var overlay = new WindowSelectorOverlay();
            var window = await overlay.ShowAsync();

            if (window is not null)
            {
                ViewModel.SelectedWindow = window;
                UpdateWindowInfoDisplay();
            }
        }
        finally
        {
            _isPickerOpen = false;
        }
    }

    private async Task LaunchRegionPickerAsync()
    {
        if (_isPickerOpen) return;
        _isPickerOpen = true;

        try
        {
            var overlay = new RegionSelectorOverlay();
            var region = await overlay.ShowAsync(ViewModel.SelectedRegion);

            if (region is not null)
            {
                ViewModel.SelectedRegion = region;
                ViewModel.HasSelectedRegion = true;
                UpdateRegionInfoDisplay();
            }
            else if (overlay.WasCancelled && ViewModel.HasSelectedRegion)
            {
                // Pixel-identical dimensions before/after cancel make the no-op
                // invisible — surface an explicit "kept previous region" hint.
                ShowTransientInfo("Region selection cancelled — kept previous region.");
            }
        }
        finally
        {
            _isPickerOpen = false;
        }
    }

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

    private void UpdateWindowInfoDisplay()
    {
        if (ViewModel.SelectedWindow is not null)
        {
            ShowSelectionMetadata($"{ViewModel.SelectedWindow.Title} — {ViewModel.SelectedWindow.ProcessName}");
        }
        else
        {
            HideSelectionMetadata();
        }
    }

    private void ShowSelectionMetadata(string text)
    {
        if (SelectionMetadataText is null) return;
        SelectionMetadataText.Text = text;
        SelectionMetadataText.Visibility = Visibility.Visible;
    }

    private void HideSelectionMetadata()
    {
        if (SelectionMetadataText is null) return;
        SelectionMetadataText.Visibility = Visibility.Collapsed;
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

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
