using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Musio_App.ViewModels;
using Windows.Graphics;

namespace Musio_App;

/// <summary>
/// The compact "Mini mode" window: a frameless, always-on-top pill docked at the
/// bottom of the screen that carries the Record page's toolbar and a Record button.
/// </summary>
/// <remarks>
/// This is a genuinely separate window from <see cref="MainWindow"/> rather than a
/// re-skin of it — <see cref="Services.ShellCoordinator"/> owns both and shows
/// exactly one at a time. Sizing is driven by the content, so the pill grows and
/// shrinks as the toolbar reveals its Window/Region pickers.
/// </remarks>
public sealed partial class MiniWindow : Window
{
    private readonly RecordingViewModel _viewModel = RecordingViewModel.Shared;
    private bool _isClosingProgrammatically;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;

    /// <summary>
    /// Widest the pill has been while visible, in <em>DIPs</em>. The window grows to
    /// fit but never shrinks, so the toolbar stretches into freed space instead of
    /// the pill snapping narrower. Reset in <see cref="HideMini"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately stored in DIPs, not physical pixels: dragging the pill from a
    /// 200% monitor to a 100% one would otherwise clamp the new (correctly halved)
    /// width back up to the stale high-DPI value and leave it roughly twice too wide.
    /// </remarks>
    private double _widestSeenDips;

    /// <summary>
    /// Set once the user drags the pill somewhere. After that the pill is clamped
    /// on screen rather than re-centred, so a capture-mode switch cannot teleport it
    /// away from where they parked it.
    /// </summary>
    private bool _userPositioned;

    /// <summary>
    /// Non-zero while we are moving/resizing the window ourselves, so those changes
    /// aren't mistaken for a user drag. A depth counter rather than a bool because
    /// <see cref="AppWindow"/>.Resize raises Changed synchronously, nesting a
    /// programmatic move inside a programmatic resize.
    /// </summary>
    private int _programmaticChangeDepth;

    /// <summary>Raised when the user presses Record.</summary>
    public event EventHandler? RecordRequested;

    /// <summary>Raised when the user wants the full app window instead.</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>Raised when the user dismisses the pill back to the system tray.</summary>
    public event EventHandler? HideRequested;

    public MiniWindow()
    {
        InitializeComponent();

        Toolbar.UseTransparentChrome();
        Toolbar.RecordRequested += (_, _) => RecordRequested?.Invoke(this, EventArgs.Empty);
        Toolbar.ExpandRequested += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        Toolbar.InfoMessage += OnInfoMessage;
        Toolbar.LayoutChanged += OnToolbarLayoutChanged;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        RootGrid.Loaded += (_, _) => ResizeToContent();
        RootGrid.KeyDown += OnRootKeyDown;

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragGrip);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;

        // Keep the pill out of any recording that a picker overlay might be
        // staged over, and out of other apps' captures.
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

        if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();
        else
            SystemBackdrop = new MicaBackdrop();

        // Strip the DWM border/caption so only the rounded acrylic pill shows.
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        uint captionNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionNone, sizeof(uint));

        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

        // Alt+F4 / programmatic close should park the pill in the tray, not tear
        // down the process — the tray icon is how the user gets it back.
        AppWindow.Closing += OnClosing;

        // Any resize (ours or the framework's) re-docks, so the pill can never be
        // left floating away from the bottom edge on a stale height.
        AppWindow.Changed += OnAppWindowChanged;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // A position change we didn't initiate means the user dragged the pill by
        // its grip, so stop re-centring it from then on.
        if (args.DidPositionChange && _programmaticChangeDepth == 0)
            _userPositioned = true;

        // Move() doesn't set DidSizeChange, so this cannot recurse.
        if (args.DidSizeChange) DockBottomCenter();
    }

    /// <summary>
    /// Measures the content and resizes the window around it, then re-docks to the
    /// bottom-centre of the work area so the pill stays centred as it grows.
    /// </summary>
    public void ResizeToContent()
    {
        try
        {
            RootGrid.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize;
            if (desired.Width <= 0 || desired.Height <= 0) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;

            // Grow to fit, but never shrink: switching from Window/Region back to
            // Full Screen removes the picker button, and rather than snapping the
            // pill narrower we hold the width and let the Segmented control's
            // starred column stretch into the freed space. Tracked in DIPs so the
            // mark stays valid across monitors with different scaling.
            _widestSeenDips = Math.Max(_widestSeenDips, desired.Width);

            int width = (int)Math.Ceiling(_widestSeenDips * scale);
            int height = (int)Math.Ceiling(desired.Height * scale);

            // A resize can itself nudge the window (e.g. when growing past a screen
            // edge), so it counts as a programmatic change too.
            _programmaticChangeDepth++;
            try
            {
                AppWindow.Resize(new SizeInt32(width, height));
                DockBottomCenter();
            }
            finally
            {
                _programmaticChangeDepth--;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] ResizeToContent failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Docks the pill to the bottom centre of the work area using the window's
    /// <em>actual</em> current size — unless the user has dragged it, in which case
    /// its position is kept and merely clamped back on screen.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="AppWindow"/>.Size rather than trusting a caller-supplied
    /// height: several paths resize the pill (initial load, status row appearing,
    /// Record/Stop swap), and docking against a stale height left it floating well
    /// above the taskbar. Also wired to <c>AppWindow.Changed</c> so any resize we
    /// didn't initiate still re-docks.
    /// </remarks>
    private void DockBottomCenter()
    {
        try
        {
            var size = AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            int x, y;
            if (_userPositioned)
            {
                // Respect where they put it; only pull it back if growing pushed an
                // edge off screen.
                var current = AppWindow.Position;
                x = Math.Clamp(current.X, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - size.Width));
                y = Math.Clamp(current.Y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - size.Height));
                if (x == current.X && y == current.Y) return;
            }
            else
            {
                x = workArea.X + ((workArea.Width - size.Width) / 2);
                // WorkArea already excludes the taskbar, so this sits just above it.
                y = workArea.Y + workArea.Height - size.Height - DockMarginPx;
            }

            MoveProgrammatically(new PointInt32(x, y));
        }
        catch
        {
            // Fall back to wherever the OS put it.
        }
    }

    /// <summary>
    /// Moves the window without the move being counted as a user drag.
    /// </summary>
    private void MoveProgrammatically(PointInt32 position)
    {
        _programmaticChangeDepth++;
        try { AppWindow.Move(position); }
        finally { _programmaticChangeDepth--; }
    }

    /// <summary>Shows the pill, re-syncing the toolbar with the shared view model first.</summary>
    public void ShowMini()
    {
        Toolbar.SyncFromViewModel();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_SHOW);
        ResizeToContent();
        Activate();
        SetForegroundWindow(hwnd);
    }

    /// <summary>Hides the pill without destroying it, so state and position survive.</summary>
    public void HideMini()
    {
        // Drop the width high-water mark so a re-summoned pill fits itself tightly
        // again instead of inheriting the widest layout from a previous session.
        // A position the user chose is deliberately kept.
        _widestSeenDips = 0;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>Closes the window for real, used only during app shutdown.</summary>
    public void CloseMini()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _statusTimer?.Stop();
        _isClosingProgrammatically = true;
        try { Close(); }
        catch { /* already gone */ }
    }

    /// <summary>Surfaces a message under the toolbar; errors stay until replaced.</summary>
    /// <remarks>
    /// Reserved for failures and transient hints. Selection summaries are not shown
    /// in Mini mode, so the pill stays a single compact row in normal use.
    /// </remarks>
    public void ShowStatus(string message, bool isTransient = true)
    {
        if (StatusText is null) return;

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        ResizeToContent();

        _statusTimer?.Stop();
        if (!isTransient) return;

        _statusTimer ??= DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(4);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick -= OnStatusTimerTick;
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
    }

    private void OnStatusTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        HideStatus();
    }

    /// <summary>Clears the status row and shrinks the pill back around the toolbar.</summary>
    private void HideStatus()
    {
        if (StatusText is null) return;

        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
        ResizeToContent();
    }

    private void OnInfoMessage(object? sender, string message) => ShowStatus(message);

    private void OnToolbarLayoutChanged(object? sender, EventArgs e)
    {
        // Deferred so the picker button's visibility change is through layout
        // before we measure the toolbar's new natural width.
        DispatcherQueue.TryEnqueue(ResizeToContent);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingViewModel.IsRecording)) return;

        // Swapping between the Record and Stop controls changes the pill's width.
        DispatcherQueue.TryEnqueue(ResizeToContent);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (_viewModel.IsRecording) return;

        e.Handled = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
        => HideRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isClosingProgrammatically) return;

        args.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>Gap between the pill and the edge of the work area, in physical pixels.</summary>
    private const int DockMarginPx = 12;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
}
