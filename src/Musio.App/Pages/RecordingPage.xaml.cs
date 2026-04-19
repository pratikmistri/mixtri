using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        UpdateRegionPanelVisibility();
    }

    private void CaptureMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
            UpdateRegionPanelVisibility();
        }
    }

    private void Fps_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.Fps = int.Parse(tag);
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
        var overlay = new RegionSelectorOverlay();
        var region = await overlay.ShowAsync();

        if (region is not null)
        {
            ViewModel.SelectedRegion = region;
            ViewModel.HasSelectedRegion = true;
            UpdateRegionInfoDisplay();
        }
    }

    private async void SelectWindowButton_Click(object sender, RoutedEventArgs e)
    {
        var windows = _regionSelector.GetVisibleWindows();

        // Filter out Musio's own windows
        var currentPid = (uint)Process.GetCurrentProcess().Id;
        var filteredWindows = windows
            .Where(w =>
            {
                try
                {
                    GetWindowThreadProcessId(w.Handle, out uint pid);
                    return pid != currentPid;
                }
                catch { return true; }
            })
            .OrderBy(w => w.Title)
            .ToList();

        if (filteredWindows.Count == 0)
        {
            var noWindowsDialog = new ContentDialog
            {
                Title = "No Windows Found",
                Content = "No capturable windows were found.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await noWindowsDialog.ShowAsync();
            return;
        }

        var listView = new ListView
        {
            ItemsSource = filteredWindows,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 400,
        };
        listView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                            xmlns:capture=""using:Musio.Core.Capture"">
                <StackPanel Orientation=""Horizontal"" Spacing=""12"" Padding=""4"">
                    <FontIcon Glyph=""&#xE737;"" FontSize=""16"" VerticalAlignment=""Center"" />
                    <StackPanel Spacing=""2"">
                        <TextBlock Text=""{Binding Title}"" TextTrimming=""CharacterEllipsis"" MaxWidth=""350"" />
                        <TextBlock Text=""{Binding ProcessName}"" FontSize=""12""
                                   Foreground=""{ThemeResource TextFillColorSecondaryBrush}"" />
                    </StackPanel>
                </StackPanel>
            </DataTemplate>");

        var dialog = new ContentDialog
        {
            Title = "Select a Window",
            Content = listView,
            PrimaryButtonText = "Select",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        // Enable the primary button only when a window is selected
        dialog.IsPrimaryButtonEnabled = false;
        listView.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = listView.SelectedItem is not null;
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && listView.SelectedItem is WindowInfo selected)
        {
            ViewModel.SelectedWindow = selected;
            UpdateWindowInfoDisplay();
        }
    }

    private void UpdateWindowInfoDisplay()
    {
        if (WindowInfoText is null) return;

        if (ViewModel.SelectedWindow is not null)
        {
            var w = ViewModel.SelectedWindow;
            WindowInfoText.Text = $"{w.Title} ({w.ProcessName}) — {w.Width}×{w.Height}";
            WindowInfoText.Visibility = Visibility.Visible;
        }
        else
        {
            WindowInfoText.Visibility = Visibility.Collapsed;
        }
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
