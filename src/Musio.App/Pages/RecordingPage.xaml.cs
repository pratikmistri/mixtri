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
    public RecordingViewModel ViewModel { get; } = new();

    private readonly RegionSelector _regionSelector = new();
    private RecordingOverlayWindow? _overlayWindow;
    private RegionBorderHighlight? _regionBorder;
    private bool _recordingMinimizedWindow;
    private bool _isPageLoading = true;
    private bool _isPickerOpen;

    public RecordingPage()
    {
        InitializeComponent();
        ViewModel.SetDispatcher(DispatcherQueue);
        Loaded += OnLoaded;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RecordingViewModel.IsRecording)) return;

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
                        Frame.Navigate(typeof(EditorPage));
                }
            });
        };
    }

    private async void StartRecordButton_Click(object sender, RoutedEventArgs e)
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
            int px = (int)(region.X * dpiScale);
            int py = (int)(region.Y * dpiScale);
            int pw = (int)(region.Width * dpiScale);
            int ph = (int)(region.Height * dpiScale);
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

    private void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is FrameworkElement item && item.Tag is string tag)
        {
            ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
            UpdateRegionPanelVisibility();

            // Auto-launch the appropriate picker when the user selects a mode
            if (!_isPageLoading && !_isPickerOpen)
            {
                if (ViewModel.CaptureMode == CaptureMode.Window)
                    _ = LaunchWindowPickerAsync();
                else if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
                    _ = LaunchRegionPickerAsync();
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
    }

    private void UpdateRegionInfoDisplay()
    {
        if (ViewModel.HasSelectedRegion && ViewModel.SelectedRegion is not null)
        {
            var r = ViewModel.SelectedRegion;
            RegionInfoText.Text = $"Last: {r.Width}\u00d7{r.Height} at {r.X},{r.Y}";
            RegionInfoText.Visibility = Visibility.Visible;
            return;
        }

        var saved = _regionSelector.LoadLastRegion();
        if (saved is not null)
        {
            ViewModel.SelectedRegion = saved;
            ViewModel.HasSelectedRegion = true;
            RegionInfoText.Text = $"Last: {saved.Width}\u00d7{saved.Height} at {saved.X},{saved.Y}";
            RegionInfoText.Visibility = Visibility.Visible;
        }
        else
        {
            RegionInfoText.Visibility = Visibility.Collapsed;
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
            var region = await overlay.ShowAsync();

            if (region is not null)
            {
                ViewModel.SelectedRegion = region;
                ViewModel.HasSelectedRegion = true;
                UpdateRegionInfoDisplay();
            }
        }
        finally
        {
            _isPickerOpen = false;
        }
    }

    private void UpdateWindowInfoDisplay()
    {
        if (WindowInfoText is null) return;

        if (ViewModel.SelectedWindow is not null)
        {
            WindowInfoText.Text = ViewModel.SelectedWindow.Title;
            ToolTipService.SetToolTip(SelectWindowButton,
                $"{ViewModel.SelectedWindow.Title} ({ViewModel.SelectedWindow.ProcessName}) — click to change");
        }
        else
        {
            WindowInfoText.Text = "Select Window";
        }
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    /// <summary>
    /// Resolves the DPI scale for the monitor that owns the given region.
    /// Uses the same monitor-matching logic as <see cref="RecordingViewModel.BuildCaptureTarget"/>.
    /// </summary>
    private static float GetRegionMonitorDpiScale(CaptureRegion region)
    {
        var monitors = MonitorEnumerator.GetAllMonitors();
        var monitor = monitors.FirstOrDefault(m => m.DisplayName.Contains(region.MonitorId))
            ?? monitors.FirstOrDefault();

        if (monitor is not null && monitor.Handle != IntPtr.Zero)
        {
            int hr = GetDpiForMonitor(monitor.Handle, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            if (hr == 0 && dpiX > 0)
                return dpiX / 96.0f;
        }

        return 1.0f;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
