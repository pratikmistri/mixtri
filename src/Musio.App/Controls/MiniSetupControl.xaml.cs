using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Musio_App.Services;
using Musio_App.ViewModels;
using Musio.Core.Capture;
using Windows.Foundation;

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
public sealed partial class MiniSetupControl : UserControl, IDimmable
{
    public RecordingViewModel ViewModel { get; } = RecordingViewModel.Shared;

    private readonly RegionSelector _regionSelector = new();
    private bool _isLoading = true;
    private Storyboard? _dimStoryboard;

    /// <summary>
    /// Whether picker launches from this toolbar should dim the toolbar
    /// itself (per spec §4.5). True when hosted in <c>AppShellWindow</c>'s
    /// MiniSetup slot; false when hosted in <c>RecordingPage</c> (the Full
    /// state never dims its picker host).
    /// </summary>
    public bool DimWhilePicking
    {
        get => (bool)GetValue(DimWhilePickingProperty);
        set => SetValue(DimWhilePickingProperty, value);
    }

    public static readonly DependencyProperty DimWhilePickingProperty =
        DependencyProperty.Register(
            nameof(DimWhilePicking),
            typeof(bool),
            typeof(MiniSetupControl),
            new PropertyMetadata(false));

    /// <summary>
    /// Whether the toolbar should show its own inline Record / Stop button.
    /// <c>true</c> by default (used by <c>AppShellWindow</c> where the
    /// toolbar IS the entire Mini surface). <c>RecordingPage</c> sets this
    /// to <c>false</c> because it renders its own hero Record button below
    /// the toolbar; surfacing two Record buttons in Full state would be
    /// confusing.
    /// </summary>
    public bool ShowInlineRecordButton
    {
        get => (bool)GetValue(ShowInlineRecordButtonProperty);
        set => SetValue(ShowInlineRecordButtonProperty, value);
    }

    public static readonly DependencyProperty ShowInlineRecordButtonProperty =
        DependencyProperty.Register(
            nameof(ShowInlineRecordButton),
            typeof(bool),
            typeof(MiniSetupControl),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether the compact toolbar's Expand-to-Full button is visible.
    /// Hidden by default so secondary hosts can opt in only when Mini Setup
    /// is the active shell state.
    /// </summary>
    public bool IsExpandButtonVisible
    {
        get => (bool)GetValue(IsExpandButtonVisibleProperty);
        set => SetValue(IsExpandButtonVisibleProperty, value);
    }

    public static readonly DependencyProperty IsExpandButtonVisibleProperty =
        DependencyProperty.Register(
            nameof(IsExpandButtonVisible),
            typeof(bool),
            typeof(MiniSetupControl),
            new PropertyMetadata(false, OnIsExpandButtonVisibleChanged));

    private static void OnIsExpandButtonVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MiniSetupControl control)
            control.Bindings.Update();
    }

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

    /// <summary>
    /// Raised when the toolbar wants the host to update a "selected
    /// window/region" caption. Phase B moved the caption out of the
    /// toolbar (it would have pushed the compact 64 px target back to
    /// ~80 px); hosts that want to surface it (e.g. <c>RecordingPage</c>)
    /// subscribe and render it in their own layout. A <c>null</c> value
    /// means "hide the caption".
    /// </summary>
    public event EventHandler<string?>? SelectionMetadataChanged;

    /// <summary>
    /// Raised when the toolbar's intrinsic width may have changed (e.g. the
    /// capture-mode switched and the inline Window/Region selector toggled
    /// visibility). The host shell uses this to animate the AppWindow width
    /// while preserving the top-center anchor (spec §4.6).
    /// </summary>
    public event EventHandler? RequestRemeasure;

    /// <summary>
    /// Raised when the user presses Esc while the toolbar has focus shortly
    /// after a summon. The host shell handles this by hiding the toolbar
    /// back to the system tray (spec §4.7).
    /// </summary>
    public event EventHandler? DismissRequested;

    public MiniSetupControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (CapturePickerService.Shared.IsPickerOpen || RecordingViewModel.Shared.IsRecording)
                return;

            DismissRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SelectCaptureModeSegment(ViewModel.CaptureMode);
        UpdateRegionPanelVisibility();
        _isLoading = false;
        DispatcherQueue.TryEnqueue(() => Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Move the segmented control's selection to the segment whose Tag matches
    /// the supplied <paramref name="mode"/>. Tag-based so the visual ordering
    /// of the segments (Region → Window → FullScreen in Phase C) is decoupled
    /// from the <see cref="CaptureMode"/> enum's underlying integer values.
    /// </summary>
    private void SelectCaptureModeSegment(CaptureMode mode)
    {
        if (CaptureModeSelector is null) return;
        var tag = mode.ToString();
        for (int i = 0; i < CaptureModeSelector.Items.Count; i++)
        {
            if (CaptureModeSelector.Items[i] is FrameworkElement item
                && item.Tag is string segmentTag
                && string.Equals(segmentTag, tag, StringComparison.Ordinal))
            {
                CaptureModeSelector.SelectedIndex = i;
                return;
            }
        }
    }

    // x:Bind helper: invert boolean
    public bool InvertBool(bool value) => !value;

    // x:Bind helper: bool → Visibility
    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    // x:Bind helper: inverted bool → Visibility
    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    // x:Bind helper: separator + inline Record/Stop visible only when the
    // host opted in via ShowInlineRecordButton.
    public Visibility ShowInlineRecordVisibility(bool show) =>
        show ? Visibility.Visible : Visibility.Collapsed;

    // x:Bind helper: inline Record visible iff host opted in AND not recording.
    public Visibility InlineRecordVisibility(bool show, bool isRecording) =>
        show && !isRecording ? Visibility.Visible : Visibility.Collapsed;

    // x:Bind helper: inline Stop+timer visible iff host opted in AND recording.
    public Visibility InlineStopVisibility(bool show, bool isRecording) =>
        show && isRecording ? Visibility.Visible : Visibility.Collapsed;

    // x:Bind helper: dim options grid while recording
    public double RecordingOpacity(bool isRecording) =>
        isRecording ? 0.4 : 1.0;

    private async void CaptureModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModeSelector?.SelectedItem is FrameworkElement item && item.Tag is string tag)
        {
            if (!Enum.TryParse<CaptureMode>(tag, out var parsedMode))
                return;
            ViewModel.CaptureMode = parsedMode;
            UpdateRegionPanelVisibility();

            // Notify host so it can animate the AppWindow width to absorb
            // the inline Window/Region selector showing/hiding (spec §4.6).
            RequestRemeasure?.Invoke(this, EventArgs.Empty);

            if (_isLoading)
                return;

            try
            {
                // If a different picker is still open from the previous mode,
                // cancel it and wait for it to fully close so the next launch
                // isn't rejected by the re-entrancy guard. Also covers the
                // "switch to Full Screen while picker open" case — cancel
                // closes the overlay and we simply don't launch anything new.
                if (CapturePickerService.Shared.IsPickerOpen)
                    await CapturePickerService.Shared.CancelActivePickerAsync();

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

    private void UpdateRegionPanelVisibility()
    {
        // The Region/Window inline buttons were removed (selecting the tab now
        // auto-launches the picker). This method survives only to refresh the
        // selection-metadata caption that hosts surface via
        // SelectionMetadataChanged.
        if (ViewModel.CaptureMode == CaptureMode.CustomRegion)
            UpdateRegionInfoDisplay();
        else if (ViewModel.CaptureMode == CaptureMode.Window)
            UpdateWindowInfoDisplay();
        else
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

    private async Task LaunchWindowPickerAsync()
    {
        // Snapshot prior state so we can replicate the "kept previous window"
        // hint analogous to the region path — only fire the InfoBar when the
        // picker was actually shown and the user explicitly cancelled.
        bool hadPriorWindow = ViewModel.SelectedWindow is not null;
        var window = GetHostWindow();
        IDimmable? dim = DimWhilePicking ? this : null;
        var result = await CapturePickerService.Shared.PickWindowAsync(window, dimTarget: dim);

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
        IDimmable? dim = DimWhilePicking ? this : null;
        var result = await CapturePickerService.Shared.PickRegionAsync(window, dimTarget: dim);

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
        SelectionMetadataChanged?.Invoke(this, text);
    }

    private void HideSelectionMetadata()
    {
        SelectionMetadataChanged?.Invoke(this, null);
    }

    private void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Public re-entry point used by the host (e.g. <c>AppShellWindow</c>)
    /// to re-sync the segmented control with the shared VM's
    /// <see cref="RecordingViewModel.CaptureMode"/> after a restore.
    /// </summary>
    public void SyncCaptureModeFromViewModel()
    {
        SelectCaptureModeSegment(ViewModel.CaptureMode);
        UpdateRegionPanelVisibility();
    }

    /// <summary>
    /// Move keyboard focus to the inline Record button so Space/Enter triggers
    /// it. Used by the global summon hotkey + tray "new recording" entries
    /// (spec §4.7). No-op when the inline Record button is hidden (e.g. when
    /// recording, or when the host has set <see cref="ShowInlineRecordButton"/>
    /// to false).
    /// </summary>
    public void FocusRecordButton()
    {
        try
        {
            if (StartRecordButton is null || StartRecordButton.Visibility != Visibility.Visible)
                return;
            StartRecordButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniSetupControl] FocusRecordButton failed: {ex.Message}");
        }
    }

    public Size MeasureToolbarDesiredSize(double availableHeight)
    {
        double content = MeasureVisibleWidth(ToolbarContentPanel, availableHeight);
        ToolbarBorder.Measure(new Size(double.PositiveInfinity, availableHeight));
        double width = ToolbarBorder.DesiredSize.Width;
        if (content > 0)
        {
            width = Math.Max(width, content + ToolbarBorder.Padding.Left + ToolbarBorder.Padding.Right
                + ToolbarBorder.BorderThickness.Left + ToolbarBorder.BorderThickness.Right);
        }

        double height = Math.Max(ToolbarBorder.DesiredSize.Height, ToolbarBorder.ActualHeight);
        if (height <= 0)
            height = availableHeight;
        return new Size(width, height);
    }

    private static double MeasureVisibleWidth(FrameworkElement element, double availableHeight)
    {
        if (element.Visibility != Visibility.Visible)
            return 0;

        if (element is StackPanel stack && stack.Orientation == Orientation.Horizontal)
        {
            double width = 0;
            int visibleChildren = 0;
            foreach (var child in stack.Children)
            {
                if (child is FrameworkElement childElement
                    && childElement.Visibility == Visibility.Visible)
                {
                    width += MeasureVisibleWidth(childElement, availableHeight);
                    visibleChildren++;
                }
            }
            if (visibleChildren > 1)
                width += stack.Spacing * (visibleChildren - 1);
            return width;
        }

        element.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, availableHeight));
        double measuredWidth = Math.Max(element.DesiredSize.Width, element.ActualWidth);
        if (!double.IsNaN(element.Width) && element.Width > 0)
            measuredWidth = Math.Max(measuredWidth, element.Width);
        if (element.MinWidth > 0)
            measuredWidth = Math.Max(measuredWidth, element.MinWidth);
        return measuredWidth;
    }

    // ===========================================================
    // IDimmable — spec §4.5 dim-while-picking flow
    // ===========================================================

    /// <summary>
    /// Animate the toolbar opacity down to the theme's
    /// <c>MiniToolbarDimmedOpacity</c> resource (0.15 default, 0.4 in high
    /// contrast) and disable hit-testing so clicks fall through to the
    /// picker overlay beneath. Idempotent + cancellable: restarting a dim
    /// while one is in flight just hops to the most recent target opacity.
    /// </summary>
    public Task DimAsync()
    {
        double target = ResolveDimmedOpacity();
        ToolbarBorder.IsHitTestVisible = false;
        return AnimateOpacityAsync(ToolbarBorder, target);
    }

    /// <summary>
    /// Restore the toolbar to 100% opacity and re-enable hit-testing.
    /// </summary>
    public Task UndimAsync()
    {
        ToolbarBorder.IsHitTestVisible = true;
        return AnimateOpacityAsync(ToolbarBorder, 1.0);
    }

    private double ResolveDimmedOpacity()
    {
        try
        {
            // First preference: a XAML resource the host can override
            // (Phase C ships MiniToolbarDimmedOpacity in AppColors.xaml).
            if (Application.Current.Resources.TryGetValue("MiniToolbarDimmedOpacity", out var raw)
                && raw is double d && d > 0)
                return d;
        }
        catch { /* resource lookup is best-effort */ }

        // Fallback: spec §4.8 — high-contrast override 0.4, default 0.15.
        try
        {
            var hc = new Windows.UI.ViewManagement.AccessibilitySettings();
            if (hc.HighContrast) return 0.4;
        }
        catch { /* AccessibilitySettings may not be available in some shells */ }
        return 0.15;
    }

    private Task AnimateOpacityAsync(UIElement element, double to)
    {
        var tcs = new TaskCompletionSource();
        try
        {
            _dimStoryboard?.Stop();
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                From = element.Opacity,
                To = to,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, element);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                element.Opacity = to;
                tcs.TrySetResult();
            };
            _dimStoryboard = sb;
            sb.Begin();
        }
        catch
        {
            element.Opacity = to;
            tcs.TrySetResult();
        }
        return tcs.Task;
    }
}
