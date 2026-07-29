using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio.Core.Capture;
using Musio.Core.Settings;
using Musio_App.ViewModels;

namespace Musio_App.Controls;

/// <summary>
/// The capture-mode / audio / camera toolbar shared by the full Record page and
/// the Mini window, so both surfaces drive the same <see cref="RecordingViewModel"/>
/// and can never drift out of sync.
/// </summary>
/// <remarks>
/// The Mini window opts into <see cref="ShowInlineRecordButton"/> and
/// <see cref="ShowExpandButton"/>; the Record page leaves both off and renders its
/// own hero-sized Record button beneath the toolbar.
/// </remarks>
public sealed partial class RecordToolbarControl : UserControl
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private readonly RegionSelector _regionSelector = new();
    private bool _isLoading = true;
    private bool _isPickerOpen;

    /// <summary>Raised when the inline Record button is pressed.</summary>
    public event EventHandler? RecordRequested;

    /// <summary>Raised when the expand-to-full-app button is pressed.</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>
    /// Raised with a human-readable description of the current selection, or null
    /// when there is nothing to show. Hosts decide where to render it.
    /// </summary>
    public event EventHandler<string?>? SelectionMetadataChanged;

    /// <summary>Raised with a transient message the host should surface to the user.</summary>
    public event EventHandler<string>? InfoMessage;

    public RecordToolbarControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty ShowInlineRecordButtonProperty =
        DependencyProperty.Register(
            nameof(ShowInlineRecordButton), typeof(bool), typeof(RecordToolbarControl),
            new PropertyMetadata(false, OnLayoutFlagChanged));

    /// <summary>Shows a compact Record/Stop control inside the toolbar itself.</summary>
    public bool ShowInlineRecordButton
    {
        get => (bool)GetValue(ShowInlineRecordButtonProperty);
        set => SetValue(ShowInlineRecordButtonProperty, value);
    }

    public static readonly DependencyProperty ShowExpandButtonProperty =
        DependencyProperty.Register(
            nameof(ShowExpandButton), typeof(bool), typeof(RecordToolbarControl),
            new PropertyMetadata(false, OnLayoutFlagChanged));

    /// <summary>Shows the button that swaps the Mini window for the full app window.</summary>
    public bool ShowExpandButton
    {
        get => (bool)GetValue(ShowExpandButtonProperty);
        set => SetValue(ShowExpandButtonProperty, value);
    }

    private static void OnLayoutFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // x:Bind function bindings don't re-evaluate off a DP change on their own,
        // so nudge them explicitly.
        if (d is RecordToolbarControl control)
            control.Bindings.Update();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        CaptureModeSelector.SelectedIndex = (int)ViewModel.CaptureMode;
        UpdateSelectionPanels();
        _isLoading = false;
    }

    /// <summary>
    /// Drops the toolbar's own card background and border. The Mini window is
    /// itself a rounded acrylic pill, so a second card inside it would double up.
    /// </summary>
    public void UseTransparentChrome()
    {
        ToolbarBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolbarBorder.BorderThickness = new Thickness(0);
        ToolbarBorder.Padding = new Thickness(10, 6, 10, 6);
    }

    /// <summary>
    /// Re-reads the shared view model. Call when a host is shown again after being
    /// hidden, since the other surface may have changed the selection meanwhile.
    /// </summary>
    public void SyncFromViewModel()
    {
        _isLoading = true;
        if (CaptureModeSelector.SelectedIndex != (int)ViewModel.CaptureMode)
            CaptureModeSelector.SelectedIndex = (int)ViewModel.CaptureMode;
        UpdateSelectionPanels();
        _isLoading = false;
    }

    private async void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is not FrameworkElement item || item.Tag is not string tag)
            return;

        ViewModel.CaptureMode = Enum.Parse<CaptureMode>(tag);
        ShellSettings.Instance.LastCaptureMode = tag;
        UpdateSelectionPanels();

        // Auto-launch the matching picker when the user chooses a mode, but not
        // while we're seeding the selector from the view model.
        if (_isLoading || _isPickerOpen) return;

        try
        {
            if (ViewModel.CaptureMode == CaptureMode.Window)
                await LaunchWindowPickerAsync();
            else if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
                await LaunchRegionPickerAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordToolbar] Picker launch failed: {ex.Message}");
        }
    }

    private void UpdateSelectionPanels()
    {
        if (RegionPanel is not null)
        {
            RegionPanel.Visibility = ViewModel.CaptureMode == CaptureMode.CustomRegion
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (WindowPanel is not null)
        {
            WindowPanel.Visibility = ViewModel.CaptureMode == CaptureMode.Window
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        switch (ViewModel.CaptureMode)
        {
            case CaptureMode.CustomRegion:
                UpdateRegionMetadata();
                break;
            case CaptureMode.Window:
                UpdateWindowMetadata();
                break;
            default:
                SelectionMetadataChanged?.Invoke(this, null);
                break;
        }
    }

    private void UpdateRegionMetadata()
    {
        if (ViewModel.HasSelectedRegion && ViewModel.SelectedRegion is { } current)
        {
            SelectionMetadataChanged?.Invoke(
                this, $"Region: {current.Width}\u00d7{current.Height} at ({current.X}, {current.Y})");
            return;
        }

        var saved = _regionSelector.LoadLastRegion();
        if (saved is not null)
        {
            ViewModel.SelectedRegion = saved;
            ViewModel.HasSelectedRegion = true;
            SelectionMetadataChanged?.Invoke(
                this, $"Region: {saved.Width}\u00d7{saved.Height} at ({saved.X}, {saved.Y})");
        }
        else
        {
            SelectionMetadataChanged?.Invoke(this, null);
        }
    }

    private void UpdateWindowMetadata()
    {
        SelectionMetadataChanged?.Invoke(
            this,
            ViewModel.SelectedWindow is { } w
                ? $"{w.Title} \u2014 {w.ProcessName}"
                : null);
    }

    private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
        => await LaunchRegionPickerAsync();

    private async void SelectWindowButton_Click(object sender, RoutedEventArgs e)
        => await LaunchWindowPickerAsync();

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
                UpdateWindowMetadata();
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
                UpdateRegionMetadata();
            }
            else if (overlay.WasCancelled && ViewModel.HasSelectedRegion)
            {
                // Pixel-identical dimensions before/after cancel make the no-op
                // invisible — surface an explicit "kept previous region" hint.
                InfoMessage?.Invoke(this, "Region selection cancelled — kept previous region.");
            }
        }
        finally
        {
            _isPickerOpen = false;
        }
    }

    private void InlineRecordButton_Click(object sender, RoutedEventArgs e)
        => RecordRequested?.Invoke(this, EventArgs.Empty);

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
        => ExpandRequested?.Invoke(this, EventArgs.Empty);

    // x:Bind helpers
    public bool InvertBool(bool value) => !value;

    public double RecordingOpacity(bool isRecording) => isRecording ? 0.4 : 1.0;

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InlineRecordVisibility(bool showInline, bool isRecording) =>
        showInline && !isRecording ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InlineStopVisibility(bool showInline, bool isRecording) =>
        showInline && isRecording ? Visibility.Visible : Visibility.Collapsed;
}
