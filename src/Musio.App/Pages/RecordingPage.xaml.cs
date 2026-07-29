using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio_App.Pages;

/// <summary>
/// The full app's Record page. It hosts the same
/// <see cref="Controls.RecordToolbarControl"/> as the Mini window and adds a
/// hero-sized Record button.
/// </summary>
/// <remarks>
/// Window hiding, the recording overlay, the region border, and the hand-off to
/// the editor all belong to <see cref="ShellCoordinator"/> — Mini mode can start a
/// recording without this page ever being constructed, so none of that can live
/// here.
/// </remarks>
public sealed partial class RecordingPage : Page
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private DispatcherTimer? _infoBarTimer;

    public RecordingPage()
    {
        InitializeComponent();

        Toolbar.SelectionMetadataChanged += OnSelectionMetadataChanged;
        Toolbar.InfoMessage += OnToolbarInfoMessage;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Tell the VM not to reset the project on stop when appending.
        ViewModel.IsAppendMode = e.Parameter as string == "append";

        Toolbar.SyncFromViewModel();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Detach the InfoBar timer so it can't fire (and keep this page alive)
        // after navigation.
        if (_infoBarTimer is not null)
        {
            _infoBarTimer.Stop();
            _infoBarTimer.Tick -= OnInfoBarTimerTick;
            _infoBarTimer = null;
        }

        if (RecordingInfoBar is not null)
            RecordingInfoBar.IsOpen = false;

        base.OnNavigatedFrom(e);
    }

    private void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShellCoordinator.Instance is { } coordinator)
        {
            _ = coordinator.StartRecordingAsync();
            return;
        }

        // No shell (shouldn't happen outside tests/tooling) — record without the
        // window choreography rather than silently doing nothing.
        ViewModel.StartRecordingCommand.Execute(null);
    }

    private void OnSelectionMetadataChanged(object? sender, string? metadata)
    {
        if (SelectionMetadataText is null) return;

        if (string.IsNullOrWhiteSpace(metadata))
        {
            SelectionMetadataText.Visibility = Visibility.Collapsed;
            SelectionMetadataText.Text = string.Empty;
        }
        else
        {
            SelectionMetadataText.Text = metadata;
            SelectionMetadataText.Visibility = Visibility.Visible;
        }
    }

    private void OnToolbarInfoMessage(object? sender, string message) => ShowTransientInfo(message);

    private void ShowTransientInfo(string message)
    {
        if (RecordingInfoBar is null) return;

        RecordingInfoBar.Severity = InfoBarSeverity.Informational;
        RecordingInfoBar.Title = string.Empty;
        RecordingInfoBar.Message = message;
        RecordingInfoBar.IsOpen = true;

        // Auto-dismiss after a few seconds so the bar doesn't linger forever.
        _infoBarTimer?.Stop();
        _infoBarTimer ??= new DispatcherTimer();
        _infoBarTimer.Interval = TimeSpan.FromSeconds(4);
        _infoBarTimer.Tick -= OnInfoBarTimerTick;
        _infoBarTimer.Tick += OnInfoBarTimerTick;
        _infoBarTimer.Start();
    }

    private void OnInfoBarTimerTick(object? sender, object args)
    {
        if (RecordingInfoBar is not null)
            RecordingInfoBar.IsOpen = false;
        (sender as DispatcherTimer)?.Stop();
    }

    // x:Bind helpers
    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;
}
