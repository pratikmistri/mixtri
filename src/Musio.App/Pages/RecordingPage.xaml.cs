using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Controls;
using Musio_App.ViewModels;
using Musio.Core.Capture;

namespace Musio_App.Pages;

public sealed partial class RecordingPage : Page
{
    public RecordingViewModel ViewModel { get; } = new();

    private readonly RegionSelector _regionSelector = new();
    private RecordingOverlayWindow? _overlayWindow;
    private bool _recordingMinimizedWindow;

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

    private void ShowRecordingOverlay()
    {
        // Minimize the main window so it doesn't occlude the screen
        var mainWindow = App.Current.MainAppWindow;
        if (mainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            ShowWindow(hwnd, SW_MINIMIZE);
            _recordingMinimizedWindow = true;
        }

        // Create and show the compact overlay
        _overlayWindow = new RecordingOverlayWindow(ViewModel);
        _overlayWindow.StopRequested += OnOverlayStopRequested;
        _overlayWindow.Activate();
    }

    private void CloseRecordingOverlay()
    {
        if (_overlayWindow is not null)
        {
            _overlayWindow.StopRequested -= OnOverlayStopRequested;
            _overlayWindow.CloseOverlay();
            _overlayWindow = null;
        }

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

    // x:Bind helper: dim options grid while recording
    public double RecordingOpacity(bool isRecording) =>
        isRecording ? 0.4 : 1.0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureModeSelector.SelectedIndex = (int)ViewModel.CaptureMode;
        UpdateRegionPanelVisibility();
    }

    private void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is FrameworkElement item && item.Tag is string tag)
        {
            ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
            UpdateRegionPanelVisibility();
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
                _ = RefreshWindowListAsync();
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
        var overlay = new RegionSelectorOverlay();
        var region = await overlay.ShowAsync();

        if (region is not null)
        {
            ViewModel.SelectedRegion = region;
            ViewModel.HasSelectedRegion = true;
            UpdateRegionInfoDisplay();
        }
    }

    private void WindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowComboBox?.SelectedItem is WindowItem item)
            ViewModel.SelectedWindow = item.Info;
    }

    private async void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWindowListAsync();
    }

    private async Task RefreshWindowListAsync()
    {
        await ViewModel.RefreshAvailableWindowsAsync();

        // Restore ComboBox selection if the ViewModel still has a selected window
        if (ViewModel.SelectedWindow is not null && WindowComboBox is not null)
        {
            var match = ViewModel.AvailableWindows
                .FirstOrDefault(w => w.Info.Handle == ViewModel.SelectedWindow.Handle);
            WindowComboBox.SelectedItem = match;
        }
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
}
