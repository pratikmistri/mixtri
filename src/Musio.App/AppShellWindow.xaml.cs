using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Musio_App.Controls;
using Musio_App.Services;
using Musio_App.Shell;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace Musio_App;

/// <summary>
/// Phase B unified single-window shell. Hosts <see cref="MiniSetupControl"/>,
/// <see cref="RecordingPillControl"/>, and <see cref="FullShellControl"/> in
/// a single visual tree and morphs <see cref="Microsoft.UI.Windowing.AppWindow"/>
/// between the four <see cref="AppShellState"/>s with animated
/// <c>MoveAndResize</c> + content cross-fade. See Mini Mode spec §4.6 / §5.2.
/// </summary>
public sealed partial class AppShellWindow : Window
{
    private const double MiniSetupFallbackWidth = 1040;
    private const double MiniSetupHeight = 64;
    private const double WindowChromeBorder = 1;
    private const double MiniRecordingWidth = 220;
    private const double MiniRecordingHeight = 52;
    private const double FullWidth = 1024;
    private const double FullHeight = 768;
    private const int TopMarginDip = 16;

    private readonly RecordingViewModel _viewModel = RecordingViewModel.Shared;
    private AppShellState _currentState;
    private AppShellState? _originStateBeforeRecording;
    private AppShellState? _minTrackStateOverride;

    // Re-entrancy: if a transition is requested while one is in flight, the
    // newest target replaces any previously-queued target. The running
    // transition finishes (so we never leave the window mid-morph), then
    // the queued target runs.
    private Task? _activeTransition;
    private AppShellState? _queuedTarget;
    private readonly object _transitionLock = new();

    /// <summary>The current shell state. Initialised when the window is constructed.</summary>
    public AppShellState CurrentState => _currentState;

    /// <summary>Shared recording view model exposed for App / tray / hotkey wiring.</summary>
    public RecordingViewModel ViewModel => _viewModel;

    /// <summary>
    /// Returns the active <see cref="Frame"/> when in a Full state, else
    /// <c>null</c>. Used by <see cref="App"/> for page navigation from
    /// outside the shell.
    /// </summary>
    public Frame? ContentFrame =>
        (_currentState == AppShellState.Full || _currentState == AppShellState.FullRecording)
            ? FullShell.ContentFrame
            : null;

    /// <summary>
    /// Build the shell window in the given initial state. State-specific
    /// chrome, sizing, presenter flags, and visible inner control are
    /// applied synchronously here (no morph animation on launch).
    /// </summary>
    public AppShellWindow(AppShellState initialState)
    {
        InitializeComponent();

        // Wire inner-control events. The shell is the single state-machine
        // driver — these handlers translate user-action events into
        // TransitionToAsync calls.
        MiniSetup.RecordRequested += OnMiniSetupRecordRequested;
        MiniSetup.ExpandRequested += OnMiniSetupExpandRequested;
        MiniSetup.RequestRemeasure += OnMiniSetupRequestRemeasure;
        MiniSetup.DismissRequested += OnMiniSetupDismissRequested;
        MiniSetup.Loaded += OnMiniSetupLoadedForRemeasure;
        // The shell's MiniSetupControl owns the dim-while-picking flow.
        MiniSetup.DimWhilePicking = true;
        RecordingPill.Initialize(_viewModel);
        RecordingPill.StopRequested += OnPillStopRequested;
        RecordingPill.ExpandRequested += OnPillExpandRequested;
        FullShell.CollapseRequested += OnFullCollapseRequested;
        FullShell.DockedPillStopRequested += OnFullDockedStopRequested;
        FullShell.DockedPillCollapseRequested += OnFullDockedCollapseRequested;

        // Dim driven by the picker service (single source of truth — every
        // picker code path goes through CapturePickerService).
        CapturePickerService.Shared.PickerOpening += OnPickerOpening;
        CapturePickerService.Shared.PickerClosed += OnPickerClosed;

        // Watch for successful Stop so we can auto-open the Editor (spec §3.4).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ConfigureForState(initialState, animate: false);
    }

    // ---------------------------------------------------------------
    // Public transition entry point
    // ---------------------------------------------------------------

    /// <summary>
    /// Morph the shell to <paramref name="target"/>. Animates the window
    /// rect and cross-fades the inner controls. Safe to call re-entrantly:
    /// a second call queues its target and runs once the in-flight
    /// transition has completed (the window is never left mid-morph).
    /// </summary>
    public Task TransitionToAsync(AppShellState target)
    {
        Task toAwait;
        lock (_transitionLock)
        {
            if (_activeTransition is null || _activeTransition.IsCompleted)
            {
                _activeTransition = RunTransitionLoopAsync(target);
                return _activeTransition;
            }

            // A transition is in flight — replace any previously-queued
            // target so we always converge to the most recent request.
            _queuedTarget = target;
            toAwait = _activeTransition;
        }
        return toAwait;
    }

    private async Task RunTransitionLoopAsync(AppShellState firstTarget)
    {
        AppShellState? next = firstTarget;
        while (next is not null)
        {
            var target = next.Value;
            next = null;

            try
            {
                await RunSingleTransitionAsync(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppShellWindow] Transition to {target} failed: {ex}");
            }

            lock (_transitionLock)
            {
                if (_queuedTarget is AppShellState queued && queued != _currentState)
                {
                    next = queued;
                    _queuedTarget = null;
                }
                else
                {
                    _queuedTarget = null;
                    _activeTransition = null;
                }
            }
        }
    }

    private async Task RunSingleTransitionAsync(AppShellState target)
    {
        if (target == _currentState)
        {
            // Even a no-op transition should re-apply state-dependent flags
            // (e.g. when MiniRecording is re-entered, we need the Expand
            // button to be visible again). Cheap, idempotent.
            ApplyStateFlags(target);
            return;
        }

        var previous = _currentState;
        // AppWindow.Position is a struct, so the always-non-null pattern was
        // dead code — read it directly. Use the current size/position as the
        // animation start so the morph picks up wherever we are (rather than
        // snapping back to the canonical rect for the previous state).
        var pos = AppWindow.Position;
        var fromRect = new RectInt32(pos.X, pos.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var toRect = ComputeWindowRect(target);

        // Spec §4.6: Full <-> FullRecording (and any other transition that
        // keeps the same inner control) does NOT animate. Just toggle
        // chrome/presenter/state flags, swap the docked-pill visibility,
        // update _currentState, and return. The cross-fade would otherwise
        // flash the same shell content out and back in for ~240 ms.
        bool sameControl = GetControlForState(previous) == GetControlForState(target);
        if (sameControl)
        {
            ApplyChromeFor(target);
            ApplyPresenterFor(target);
            UpdateBackdropFor(target);
            ApplyStateFlags(target);
            _currentState = target;

            // Bookkeeping mirrors the animated path.
            if (IsRecordingState(target) && !IsRecordingState(previous))
                _originStateBeforeRecording = previous;
            else if (!IsRecordingState(target))
                _originStateBeforeRecording = null;
            return;
        }

        // Fade the OUTGOING control out before resizing the window, then
        // resize+move, then fade the INCOMING control in. This keeps each
        // control's layout from being measured at the wrong size mid-morph.
        var outgoing = GetControlForState(previous);
        // If the pill is about to go invisible, kill its stopping ticker so
        // a hidden control doesn't keep running a DispatcherTimer.
        if (outgoing is RecordingPillControl pillOut)
            pillOut.PauseTickerWhileHidden();
        await CrossFadeOutAsync(outgoing);

        // Apply chrome for the target state BEFORE the morph animation so
        // the window border colours/rounding don't flicker mid-resize.
        ApplyChromeFor(target);
        ApplyPresenterFor(target);
        UpdateBackdropFor(target);
        _minTrackStateOverride = target;

        // Show the target control hidden, then animate the rect; this lets
        // its inner layout settle before fading in. For the pill, always
        // reset to the recording UI on re-entry — the pill is a long-lived
        // instance, so without this a second Record cycle would surface in
        // the disabled "Stopping…" state.
        var targetControl = GetControlForState(target);
        if (targetControl is RecordingPillControl pillIn)
            pillIn.ResetToRecordingState();
        targetControl.Opacity = 0;
        targetControl.Visibility = Visibility.Visible;

        // No window morph animation needed if the rect didn't change.
        if (!IsSameRect(fromRect, toRect))
        {
            var duration = GetTransitionDuration(previous, target);
            var easing = GetEasingFor(previous, target);
            await AnimateWindowAsync(fromRect, toRect, duration, easing);
        }

        ApplyStateFlags(target);
        _currentState = target;
        _minTrackStateOverride = null;

        await CrossFadeInAsync(targetControl);

        // Bookkeeping: track origin only on entry to a *recording* state.
        if (IsRecordingState(target) && !IsRecordingState(previous))
            _originStateBeforeRecording = previous;
        else if (!IsRecordingState(target))
            _originStateBeforeRecording = null;
    }

    // ---------------------------------------------------------------
    // State configuration: control visibility, chrome, presenter, flags
    // ---------------------------------------------------------------

    /// <summary>
    /// Synchronous setup used only on construction. Applies chrome,
    /// presenter flags, window rect, backdrop, and visible inner control
    /// for the initial state without any cross-fade or morph animation.
    /// </summary>
    private void ConfigureForState(AppShellState target, bool animate)
    {
        if (animate)
            throw new InvalidOperationException("Use TransitionToAsync for animated state changes.");

        ApplyChromeFor(target);
        ApplyPresenterFor(target);
        UpdateBackdropFor(target);

        var rect = ComputeWindowRect(target);
        MoveAndResizeWindow(rect);

        // Hide all, show target.
        MiniSetup.Visibility = Visibility.Collapsed; MiniSetup.Opacity = 0;
        RecordingPill.Visibility = Visibility.Collapsed; RecordingPill.Opacity = 0;
        FullShell.Visibility = Visibility.Collapsed; FullShell.Opacity = 0;

        var control = GetControlForState(target);
        control.Visibility = Visibility.Visible;
        control.Opacity = 1;

        ApplyStateFlags(target);
        _currentState = target;
    }

    private UserControl GetControlForState(AppShellState state) => state switch
    {
        AppShellState.MiniSetup => MiniSetup,
        AppShellState.MiniRecording => RecordingPill,
        AppShellState.Full or AppShellState.FullRecording => FullShell,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private void ApplyChromeFor(AppShellState state)
    {
        switch (state)
        {
            case AppShellState.MiniSetup:
                ExtendsContentIntoTitleBar = true;
                WindowChromeService.ApplyTo(this, ChromeProfile.Mini);
                WindowChromeService.SetCaptureExclusion(this, exclude: false);
                break;
            case AppShellState.MiniRecording:
                ExtendsContentIntoTitleBar = true;
                WindowChromeService.ApplyTo(this, ChromeProfile.Mini);
                WindowChromeService.SetCaptureExclusion(this, exclude: true);
                break;
            case AppShellState.Full:
                ExtendsContentIntoTitleBar = true;
                WindowChromeService.ApplyTo(this, ChromeProfile.Full);
                try { SetTitleBar(FullShell.TitleBarElement); } catch { /* not yet in tree on launch */ }
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
                try { AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { /* icon optional */ }
                WindowChromeService.SetCaptureExclusion(this, exclude: false);
                break;
            case AppShellState.FullRecording:
                ExtendsContentIntoTitleBar = true;
                WindowChromeService.ApplyTo(this, ChromeProfile.Full);
                try { SetTitleBar(FullShell.TitleBarElement); } catch { /* not yet in tree on launch */ }
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
                WindowChromeService.SetCaptureExclusion(this, exclude: true);
                break;
        }
    }

    private void ApplyPresenterFor(AppShellState state)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) return;
        switch (state)
        {
            case AppShellState.MiniSetup:
            case AppShellState.MiniRecording:
                p.SetBorderAndTitleBar(false, false);
                p.IsAlwaysOnTop = true;
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                break;
            case AppShellState.Full:
            case AppShellState.FullRecording:
                p.SetBorderAndTitleBar(true, true);
                p.IsAlwaysOnTop = false;
                p.IsResizable = true;
                p.IsMaximizable = true;
                p.IsMinimizable = true;
                break;
        }
    }

    private void ApplyStateFlags(AppShellState state)
    {
        // Inner-control visibility flags that depend on state (Expand button
        // in the recording pill, docked-pill swap in the full shell title bar).
        MiniSetup.IsExpandButtonVisible = state == AppShellState.MiniSetup;
        RecordingPill.IsExpandButtonVisible = state == AppShellState.MiniRecording;

        if (state == AppShellState.FullRecording)
        {
            FullShell.IsCollapseButtonVisible = false;
            FullShell.ShowDockedPill(_viewModel);
        }
        else
        {
            FullShell.HideDockedPill();
            FullShell.IsCollapseButtonVisible = state == AppShellState.Full;
        }
    }

    private RectInt32 ComputeWindowRect(AppShellState state)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = (dpi <= 0 ? 96 : dpi) / 96.0;
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = displayArea.WorkArea;

        switch (state)
        {
            case AppShellState.MiniSetup:
            {
                var sizeDip = GetMiniSetupWindowSizeDip();
                int workX = (int)Math.Round(work.X / scale);
                int workY = (int)Math.Round(work.Y / scale);
                int workWidth = (int)Math.Round(work.Width / scale);
                int w = (int)Math.Ceiling(sizeDip.Width);
                int h = (int)Math.Ceiling(sizeDip.Height);
                w = Math.Min(w, Math.Max(1, workWidth - 32));
                int x = workX + (workWidth - w) / 2;
                int y = workY + TopMarginDip;
                return new RectInt32(x, y, w, h);
            }
            case AppShellState.MiniRecording:
            {
                int w = (int)MiniRecordingWidth;
                int h = (int)MiniRecordingHeight;
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + TopMarginDip;
                return new RectInt32(x, y, w, h);
            }
            case AppShellState.Full:
            case AppShellState.FullRecording:
            {
                int w = (int)FullWidth;
                int h = (int)FullHeight;
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + (work.Height - h) / 2;
                return new RectInt32(x, y, w, h);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    // ---------------------------------------------------------------
    // Animations
    // ---------------------------------------------------------------

    private static bool IsSameRect(RectInt32 a, RectInt32 b)
        => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    private static bool IsRecordingState(AppShellState s)
        => s == AppShellState.MiniRecording || s == AppShellState.FullRecording;

    private static TimeSpan GetTransitionDuration(AppShellState from, AppShellState to)
    {
        // Mini <-> Mini = ~340 ms, anything involving Full = ~400 ms (per spec §4.6).
        if (!IsFullLike(from) && !IsFullLike(to))
            return TimeSpan.FromMilliseconds(340);
        return TimeSpan.FromMilliseconds(400);
    }

    private static bool IsFullLike(AppShellState s)
        => s == AppShellState.Full || s == AppShellState.FullRecording;

    private static Func<double, double> GetEasingFor(AppShellState from, AppShellState to)
    {
        // CubicEase out for grows (mini -> full), QuadraticEase in/out for
        // shrinks and same-area transitions (per spec §4.6).
        bool isGrow = !IsFullLike(from) && IsFullLike(to);
        return isGrow ? CubicEaseOut : QuadraticEaseInOut;
    }

    private static double CubicEaseOut(double t)
    {
        double u = 1 - t;
        return 1 - u * u * u;
    }

    private static double QuadraticEaseInOut(double t)
    {
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    private Task AnimateWindowAsync(RectInt32 from, RectInt32 to, TimeSpan duration, Func<double, double> easing)
    {
        var tcs = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            try
            {
                double total = Math.Max(1.0, duration.TotalMilliseconds);
                double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / total);
                double eased = easing(t);
                var current = Interpolate(from, to, eased);
                MoveAndResizeWindow(current);
                if (t >= 1.0)
                {
                    timer.Stop();
                    MoveAndResizeWindow(to);
                    tcs.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppShellWindow] Animate tick failed: {ex.Message}");
                timer.Stop();
                tcs.TrySetResult();
            }
        };
        timer.Start();
        return tcs.Task;
    }

    private static RectInt32 Interpolate(RectInt32 from, RectInt32 to, double t)
    {
        int x = (int)Math.Round(from.X + (to.X - from.X) * t);
        int y = (int)Math.Round(from.Y + (to.Y - from.Y) * t);
        int w = (int)Math.Round(from.Width + (to.Width - from.Width) * t);
        int h = (int)Math.Round(from.Height + (to.Height - from.Height) * t);
        return new RectInt32(x, y, w, h);
    }

    private void MoveAndResizeWindow(RectInt32 rect)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p)
        {
            AppWindow.MoveAndResize(rect);
            return;
        }

        bool restoreNonResizable = false;
        try
        {
            restoreNonResizable = !p.IsResizable;
            if (restoreNonResizable)
                p.IsResizable = true;
            AppWindow.MoveAndResize(rect);
        }
        finally
        {
            if (restoreNonResizable)
            {
                try { p.IsResizable = false; } catch { }
            }
        }
    }

    private static Task CrossFadeOutAsync(UIElement element)
    {
        return RunOpacityStoryboardAsync(element, from: element.Opacity, to: 0.0,
            durationMs: 120, hideAfter: true);
    }

    private static Task CrossFadeInAsync(UIElement element)
    {
        return RunOpacityStoryboardAsync(element, from: 0.0, to: 1.0,
            durationMs: 120, hideAfter: false);
    }

    private static Task RunOpacityStoryboardAsync(UIElement element, double from, double to, int durationMs, bool hideAfter)
    {
        var tcs = new TaskCompletionSource();
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            element.Opacity = to;
            if (hideAfter) element.Visibility = Visibility.Collapsed;
            tcs.TrySetResult();
        };
        sb.Begin();
        return tcs.Task;
    }

    // ---------------------------------------------------------------
    // Backdrop management (Mica for Full, Desktop Acrylic for Mini)
    // ---------------------------------------------------------------

    private void UpdateBackdropFor(AppShellState state)
    {
        // Use Window.SystemBackdrop (the high-level API) — WinUI manages the
        // SystemBackdropConfiguration + controller lifecycle for us. Setting
        // the same type repeatedly is cheap; we only allocate a new instance
        // when the kind actually changes.
        bool wantMini = state == AppShellState.MiniSetup || state == AppShellState.MiniRecording;
        bool wantFull = state == AppShellState.Full || state == AppShellState.FullRecording;

        if (wantMini && SystemBackdrop is not DesktopAcrylicBackdrop)
        {
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Acrylic backdrop failed: {ex.Message}"); }
        }
        else if (wantFull && SystemBackdrop is not MicaBackdrop)
        {
            try { SystemBackdrop = new MicaBackdrop(); }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Mica backdrop failed: {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------
    // Inner-control event handlers (the state-machine wiring)
    // ---------------------------------------------------------------

    private async void OnMiniSetupRecordRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = AppShellStateMachine.NextState(_currentState, AppShellEvent.RecordPressed, new())
                ?? AppShellState.MiniRecording;
            await TransitionToAsync(target);
            // Re-check state after the await: if the user (or any other
            // event) drove us back out of MiniRecording while the morph was
            // in flight (e.g. Stop fired before recording even started), do
            // NOT spawn a recording — we'd otherwise leave the shell sitting
            // in MiniSetup with an invisible recording running underneath.
            if ((_currentState == AppShellState.MiniRecording || _currentState == AppShellState.FullRecording)
                && !_viewModel.IsRecording)
                _viewModel.StartRecordingCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Record from Mini failed: {ex}");
        }
    }

    private async void OnMiniSetupExpandRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = AppShellStateMachine.NextState(_currentState, AppShellEvent.MiniSetupExpand, new());
            if (target is AppShellState destination)
                await TransitionToAsync(destination);
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Expand failed: {ex}"); }
    }

    private async void OnPillStopRequested(object? sender, EventArgs e)
    {
        // Spec §3.4 / Phase C rubber-duck R2: all stop sources funnel
        // through HandleRecordingStoppedAsync — fired when the VM's
        // IsRecording transitions to false. Trigger the command here and
        // let the centralised post-stop handler own the visual transition
        // (success → Full+Editor, failure → origin + red InfoBar).
        try
        {
            if (_viewModel.IsRecording)
                await _viewModel.StopRecordingCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Pill stop failed: {ex}");
        }
    }

    private async void OnPillExpandRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = AppShellStateMachine.NextState(_currentState, AppShellEvent.RecordingExpand, new());
            if (target is AppShellState destination)
                await TransitionToAsync(destination);
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Pill expand failed: {ex}"); }
    }

    private async void OnFullCollapseRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = AppShellStateMachine.NextState(_currentState, AppShellEvent.FullCollapse, new());
            if (target is AppShellState destination)
                await TransitionToAsync(destination);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Full collapse failed: {ex}");
        }
    }

    private async void OnFullDockedStopRequested(object? sender, EventArgs e)
    {
        // Funnel through the same centralised completion path as the pill
        // (rubber-duck R2). HandleRecordingStoppedAsync drives the transition
        // once the VM's IsRecording lands at false.
        try
        {
            if (_viewModel.IsRecording)
                await _viewModel.StopRecordingCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Docked stop failed: {ex}");
        }
    }

    private async void OnFullDockedCollapseRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = AppShellStateMachine.NextState(_currentState, AppShellEvent.DockedPillCollapse, new());
            if (target is AppShellState destination)
                await TransitionToAsync(destination);
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Docked collapse failed: {ex}"); }
    }

    // ---------------------------------------------------------------
    // Min-size enforcement (Full state only) — ported from MainWindow.
    // ---------------------------------------------------------------

    private int _minWidth;
    private int _minHeight;
    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private bool _wndProcInstalled;

    private void InstallWndProcOnce()
    {
        if (_wndProcInstalled) return;
        _wndProcInstalled = true;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _minWidth = (int)FullWidth;
        _minHeight = (int)FullHeight;
        _wndProcDelegate = new WndProcDelegate(WndProc);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_GETMINMAXINFO = 0x0024;
        const uint WM_QUERYENDSESSION = 0x0011;
        const uint WM_ENDSESSION = 0x0016;

        if (msg == WM_GETMINMAXINFO)
        {
            var minState = _minTrackStateOverride ?? _currentState;
            if (minState != AppShellState.Full && minState != AppShellState.FullRecording)
                return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);

            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = _minWidth;
            info.ptMinTrackSize.Y = _minHeight;
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        if (msg == WM_QUERYENDSESSION)
        {
            App.Current.BeginQuiesce();
            return new IntPtr(1);
        }

        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            App.Current.BeginQuiesce();
            return IntPtr.Zero;
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const int GWLP_WNDPROC = -4;

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------
    // Activate override — install WndProc once we have an HWND.
    // ---------------------------------------------------------------

    internal void InitializeAfterActivation()
    {
        InstallWndProcOnce();
        // The XAML title bar element is needed for SetTitleBar in Full states;
        // re-apply now that the visual tree is realised.
        if (_currentState == AppShellState.Full || _currentState == AppShellState.FullRecording)
        {
            try { SetTitleBar(FullShell.TitleBarElement); } catch { }
        }
        else if (_currentState == AppShellState.MiniSetup)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    await RemeasureMiniSetupWidthAsync();
                }
                catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Post-activation MiniSetup remeasure failed: {ex}"); }
            });
        }
    }

    // ---------------------------------------------------------------
    // Phase C: width morph on capture-mode switch (spec §4.6)
    // ---------------------------------------------------------------

    private async void OnMiniSetupRequestRemeasure(object? sender, EventArgs e)
    {
        if (_currentState != AppShellState.MiniSetup) return;
        try { await RemeasureMiniSetupWidthAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Remeasure failed: {ex}"); }
    }

    private async void OnMiniSetupLoadedForRemeasure(object sender, RoutedEventArgs e)
    {
        if (_currentState != AppShellState.MiniSetup) return;
        try
        {
            await Task.Yield();
            await RemeasureMiniSetupWidthAsync();
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Initial MiniSetup remeasure failed: {ex}"); }
    }

    private async Task RemeasureMiniSetupWidthAsync()
    {
        // Give XAML a chance to relayout after the inline panel toggled
        // visibility, then read the toolbar's intrinsic width and morph
        // the AppWindow to match (preserving the top-center anchor).
        MiniSetup.InvalidateMeasure();
        MiniSetup.Measure(new Size(double.PositiveInfinity, MiniSetupHeight));
        MiniSetup.UpdateLayout();
        await Task.Yield();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = (dpi <= 0 ? 96 : dpi) / 96.0;
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = displayArea.WorkArea;
        var sizeDip = GetMiniSetupWindowSizeDip();
        int workX = (int)Math.Round(work.X / scale);
        int workY = (int)Math.Round(work.Y / scale);
        int workWidth = (int)Math.Round(work.Width / scale);
        int targetWidth = (int)Math.Ceiling(sizeDip.Width);
        int targetHeight = (int)Math.Ceiling(sizeDip.Height);
        targetWidth = Math.Min(targetWidth, Math.Max(1, workWidth - 32));
        int targetX = workX + (workWidth - targetWidth) / 2;
        int targetY = workY + TopMarginDip;

        var pos = AppWindow.Position;
        var currentSize = AppWindow.Size;
        var fromRect = new RectInt32(pos.X, pos.Y, currentSize.Width, currentSize.Height);
        var toRect = new RectInt32(targetX, targetY, targetWidth, targetHeight);
        if (IsSameRect(fromRect, toRect)) return;

        await AnimateWindowAsync(fromRect, toRect, TimeSpan.FromMilliseconds(150), QuadraticEaseInOut);
    }

    private Size GetMiniSetupWindowSizeDip()
    {
        try
        {
            var desired = MiniSetup.MeasureToolbarDesiredSize(MiniSetupHeight);
            if (desired.Width > 0 && desired.Height > 0)
            {
                return new Size(
                    Math.Ceiling(Math.Max(desired.Width + (2 * WindowChromeBorder), MiniSetupFallbackWidth)),
                    Math.Ceiling(Math.Max(desired.Height + (2 * WindowChromeBorder), MiniSetupHeight)));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] MiniSetup measure failed: {ex.Message}");
        }

        return new Size(MiniSetupFallbackWidth, MiniSetupHeight);
    }

    // ---------------------------------------------------------------
    // Phase C: dim-while-picking glue + focus watchdog (spec §4.5)
    // ---------------------------------------------------------------

    private DispatcherTimer? _focusWatchdog;

    private void OnPickerOpening(object? sender, EventArgs e)
    {
        // The actual Dim/Undim animation runs inside CapturePickerService via
        // the IDimmable handed in by MiniSetupControl. This event-side hook
        // just arms the defensive focus watchdog (spec §4.5: 2 s without the
        // picker as the foreground window → undim so the user can recover).
        try
        {
            _focusWatchdog?.Stop();
            _focusWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _focusWatchdog.Tick += OnFocusWatchdogTick;
            _focusWatchdog.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Picker opening hook failed: {ex.Message}"); }
    }

    private void OnPickerClosed(object? sender, EventArgs e)
    {
        try { _focusWatchdog?.Stop(); _focusWatchdog = null; } catch { }
    }

    private async void OnFocusWatchdogTick(object? sender, object e)
    {
        // Best-effort: if neither the shell nor any other Musio window owns the
        // foreground 2 s after the picker opened, the picker is probably hung
        // or has lost focus to some other app — restore the toolbar so the
        // user isn't stuck with a dimmed-and-untouchable UI.
        try
        {
            var foreground = GetForegroundWindow();
            var shellHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (foreground != shellHwnd && !IsOurProcessWindow(foreground))
            {
                if (sender is DispatcherTimer t) t.Stop();
                await MiniSetup.UndimAsync();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Focus watchdog failed: {ex.Message}"); }
    }

    private static bool IsOurProcessWindow(IntPtr hwnd)
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------
    // Phase C: auto-open Editor on a successful Stop (spec §3.4)
    // ---------------------------------------------------------------

    private bool _wasRecordingLastTick;
    private bool _handlingStop;
    private DateTime _lastSummonAt = DateTime.MinValue;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingViewModel.IsRecording)) return;
        bool isRecording = _viewModel.IsRecording;
        bool justStopped = _wasRecordingLastTick && !isRecording;
        _wasRecordingLastTick = isRecording;

        if (!justStopped) return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await HandleRecordingStoppedAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Post-stop handler failed: {ex}"); }
        });
    }

    /// <summary>
    /// Single completion path for ALL stop sources (pill / docked pill /
    /// hotkey / tray / close-to-tray). Fires once per recording session via
    /// <see cref="OnViewModelPropertyChanged"/> on the IsRecording false-edge.
    /// </summary>
    /// <remarks>
    /// Rubber-duck R1c/R2: failure detection uses <c>LastProject == null</c>
    /// as the only reliable signal — sniffing RecordingStatus strings is
    /// fragile. On success we morph to Full + navigate Editor; on failure we
    /// fall back to <see cref="_originStateBeforeRecording"/> and surface
    /// a red InfoBar via <see cref="ShowTransientInfo(string,InfoBarSeverity)"/>.
    /// </remarks>
    private async Task HandleRecordingStoppedAsync()
    {
        if (_handlingStop) return;
        _handlingStop = true;

        try
        {
            var origin = _originStateBeforeRecording;
            _originStateBeforeRecording = null;
            var transitionContext = new AppShellTransitionContext(origin);

            if (_viewModel.LastProject is null)
            {
                // Failed stop: return to origin (or sensible fallback) and
                // surface the error in a red InfoBar.
                var fallback = AppShellStateMachine.NextState(
                    _currentState,
                    AppShellEvent.StopFailed,
                    transitionContext) ?? AppShellState.MiniSetup;

                if (_currentState != fallback)
                {
                    try { await TransitionToAsync(fallback); }
                    catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Failure transition failed: {ex}"); }
                }

                var message = string.IsNullOrWhiteSpace(_viewModel.RecordingStatus)
                    ? "Recording could not be saved."
                    : _viewModel.RecordingStatus;
                ShowTransientInfo(message, InfoBarSeverity.Error);
                return;
            }

            // Successful Stop: morph to Full and navigate to the Editor page
            // (the project is already on ProjectService.Instance via
            // RecordingViewModel.StopRecordingAsync, so the Editor will pick
            // it up via its existing ProjectService.Instance.CurrentProject
            // read).
            var successDestination = AppShellStateMachine.NextState(
                _currentState,
                AppShellEvent.StopSucceeded,
                transitionContext) ?? AppShellState.Full;

            if (_currentState != successDestination)
                await TransitionToAsync(successDestination);

            try
            {
                // R3: always navigate, even when the editor is already the
                // current page, so a second recording's clip replaces the
                // first instead of leaving the stale clip on screen.
                FullShell.ContentFrame.Navigate(typeof(Pages.EditorPage));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppShellWindow] Navigate to Editor failed: {ex}");
            }
        }
        finally
        {
            _handlingStop = false;
        }
    }

    /// <summary>
    /// Surface a transient message in the shell's overlay <see cref="InfoBar"/>.
    /// Visible regardless of current state (Mini Setup / Mini Recording /
    /// Full / Full Recording). Auto-dismisses after 6 s.
    /// </summary>
    public void ShowTransientInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (ShellInfoBar is null) return;
        try
        {
            ShellInfoBar.Severity = severity;
            ShellInfoBar.Title = string.Empty;
            ShellInfoBar.Message = message;
            ShellInfoBar.IsOpen = true;

            _infoBarTimer?.Stop();
            _infoBarTimer ??= DispatcherQueue.CreateTimer();
            _infoBarTimer.Interval = TimeSpan.FromSeconds(6);
            _infoBarTimer.IsRepeating = false;
            _infoBarTimer.Tick -= OnShellInfoBarTimerTick;
            _infoBarTimer.Tick += OnShellInfoBarTimerTick;
            _infoBarTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] ShowTransientInfo failed: {ex.Message}");
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _infoBarTimer;

    private void OnShellInfoBarTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try { if (ShellInfoBar is not null) ShellInfoBar.IsOpen = false; } catch { }
        sender.Stop();
    }

    // ---------------------------------------------------------------
    // Phase C: summon (spec §4.7) — bring to front, switch to MiniSetup,
    // un-minimize if needed, focus the Record button.
    // ---------------------------------------------------------------

    /// <summary>
    /// Bring the shell to the foreground, un-minimize, switch to
    /// <see cref="AppShellState.MiniSetup"/>, and place focus on the Record
    /// button so the user can confirm with Space/Enter. Used by the global
    /// summon hotkey, the system tray, and CLI activation.
    /// </summary>
    /// <param name="suppressMiniMorph">
    /// When the caller wants the window to come to the foreground in its
    /// current state (e.g. the tray "Show recording pill" entry during an
    /// active recording), pass true to skip the implicit MiniSetup morph.
    /// </param>
    public async Task SummonAsync(bool suppressMiniMorph = false)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // Restore toolbar opacity in case a stale picker left it dimmed.
            try { await MiniSetup.UndimAsync(); } catch { }

            // Un-hide / un-minimize.
            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);

            // If recording, prefer the user's current shell state (don't morph
            // a FullRecording into MiniSetup; spec §4.7 keeps recording state).
            if (!suppressMiniMorph && !_viewModel.IsRecording && _currentState != AppShellState.MiniSetup)
            {
                await TransitionToAsync(AppShellState.MiniSetup);
            }

            AllowSetForegroundWindow(unchecked((uint)Environment.ProcessId));
            SetForegroundWindow(hwnd);
            Activate();

            _lastSummonAt = DateTime.UtcNow;

            // Focus the Record button (when applicable).
            MiniSetup.FocusRecordButton();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Summon failed: {ex}");
        }
    }

    /// <summary>
    /// True when the most recent <see cref="SummonAsync"/> happened within
    /// the last 5 seconds. Used by the Esc-dismiss-summon rule (spec §4.7).
    /// </summary>
    public bool WasRecentlySummoned => (DateTime.UtcNow - _lastSummonAt) < TimeSpan.FromSeconds(5);

    /// <summary>Returns the captured origin state at record-start, or <c>null</c>.</summary>
    public AppShellState? OriginStateBeforeRecording => _originStateBeforeRecording;

    private void OnMiniSetupDismissRequested(object? sender, EventArgs e)
    {
        // Spec §4.7: Esc within ~5 s of a summon dismisses the toolbar back
        // to the tray; outside that window, it's a no-op. We also require
        // we're in MiniSetup and not actively recording.
        var target = AppShellStateMachine.NextState(
            _currentState,
            AppShellEvent.EscDismiss,
            new AppShellTransitionContext(
                OriginStateBeforeRecording: _originStateBeforeRecording,
                IsPickerOpen: CapturePickerService.Shared.IsPickerOpen,
                IsRecording: _viewModel.IsRecording,
                WasRecentlySummoned: WasRecentlySummoned));
        if (target is null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_HIDE);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Esc-dismiss failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
}
