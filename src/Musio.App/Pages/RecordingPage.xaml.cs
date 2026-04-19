using System;
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

    public RecordingPage()
    {
        InitializeComponent();
        ViewModel.SetDispatcher(DispatcherQueue);
        Loaded += OnLoaded;

        // After a recording stops successfully, navigate to the Editor page
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingViewModel.IsRecording)
                && !ViewModel.IsRecording
                && ViewModel.LastProject is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    Frame.Navigate(typeof(EditorPage));
                });
            }
        };
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
        if (RegionPanel is null) return;

        if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
        {
            RegionPanel.Visibility = Visibility.Visible;
            UpdateRegionInfoDisplay();
        }
        else
        {
            RegionPanel.Visibility = Visibility.Collapsed;
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
}
