using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Services;
using Musio_App.ViewModels;
using Musio.Core.Capture;

namespace Musio_App.Controls;

/// <summary>
/// The compact Mini Setup toolbar: capture-mode segmented control + inline
/// window/region selectors + audio/camera toggles, plus the page-local
/// Record/Stop visuals. Owns its own picker handlers (which route through
/// <see cref="CapturePickerService"/>) and exposes events the host page can
/// use for InfoBar messages / record orchestration / future Expand wiring.
/// </summary>
/// <remarks>
/// Phase A: the control is hosted by <c>RecordingPage</c>; in Phase B the
/// same control will also be hosted inside <c>AppShellWindow</c>. All shared
/// state continues to live on <see cref="RecordingViewModel.Shared"/>.
/// </remarks>
public sealed partial class MiniSetupControl : UserControl
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private readonly RegionSelector _regionSelector = new();
    private bool _isLoading = true;

    /// <summary>
    /// Raised when the user presses the hero Record button. The host page
    /// owns recording orchestration (window minimize, overlay lifecycle).
    /// </summary>
    public event EventHandler? RecordRequested;

    /// <summary>
    /// Raised when the placeholder Expand button is clicked. Phase A: wired
    /// to the (hidden) button so the slot exists; no host consumes it yet.
    /// </summary>
    public event EventHandler? ExpandRequested;

    /// <summary>
    /// Raised when the toolbar wants the host to surface a short InfoBar
    /// message (e.g. "Region selection cancelled — kept previous region.").
    /// </summary>
    public event EventHandler<string>? TransientInfoRequested;

    public MiniSetupControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureModeSelector.SelectedIndex = (int)ViewModel.CaptureMode;
        UpdateRegionPanelVisibility();
        _isLoading = false;
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

    private async void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is FrameworkElement item && item.Tag is string tag)
        {
            ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
            UpdateRegionPanelVisibility();

            // Auto-launch the appropriate picker when the user selects a mode
            if (!_isLoading && !CapturePickerService.Shared.IsPickerOpen)
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
                    System.Diagnostics.Debug.WriteLine($"[MiniSetupControl] Picker launch failed: {ex.Message}");
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
        // Snapshot prior state so we can replicate the "kept previous window"
        // hint analogous to the region path — only fire the InfoBar when the
        // picker was actually shown and the user explicitly cancelled.
        bool hadPriorWindow = ViewModel.SelectedWindow is not null;
        var window = GetHostWindow();
        var result = await CapturePickerService.Shared.PickWindowAsync(window, dimTarget: null);

        switch (result)
        {
            case PickerResult.Selected:
                UpdateWindowInfoDisplay();
                break;
            case PickerResult.Cancelled when hadPriorWindow && ViewModel.SelectedWindow is not null:
                TransientInfoRequested?.Invoke(this,
                    "Window selection cancelled \u2014 kept previous window.");
                break;
            // PickerResult.AlreadyOpen: silent no-op — no picker was shown,
            // nothing changed, so do NOT surface a "kept previous" message.
        }
    }

    private async Task LaunchRegionPickerAsync()
    {
        // Snapshot prior state so we can replicate the "kept previous region"
        // hint that used to live in RecordingPage.LaunchRegionPickerAsync —
        // only fire the InfoBar on an actual user cancel (PickerResult.Cancelled),
        // never on an AlreadyOpen re-entrancy rejection where no picker was shown.
        bool hadPriorRegion = ViewModel.HasSelectedRegion;
        var window = GetHostWindow();
        var result = await CapturePickerService.Shared.PickRegionAsync(window, dimTarget: null);

        switch (result)
        {
            case PickerResult.Selected:
                UpdateRegionInfoDisplay();
                break;
            case PickerResult.Cancelled when hadPriorRegion && ViewModel.HasSelectedRegion:
                TransientInfoRequested?.Invoke(this,
                    "Region selection cancelled \u2014 kept previous region.");
                break;
            // PickerResult.AlreadyOpen: silent no-op.
        }
    }

    private Window? GetHostWindow()
    {
        // The MiniSetupControl is hosted by either RecordingPage (today) or
        // AppShellWindow (Phase B). Today we don't need a real owner — the
        // pickers self-host — but the future shell will pass one through.
        return null;
    }

    private void UpdateWindowInfoDisplay()
    {
        if (ViewModel.SelectedWindow is not null)
        {
            ShowSelectionMetadata($"{ViewModel.SelectedWindow.Title} \u2014 {ViewModel.SelectedWindow.ProcessName}");
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

    private void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }
}
