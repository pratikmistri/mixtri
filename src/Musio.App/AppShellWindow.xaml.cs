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

        // Recording timer callbacks fire on a background ThreadPool thread.
        // Without a dispatcher set, ElapsedTime PropertyChanged would update
        // bound TextBlocks off-thread → COMException (RPC_E_WRONG_THREAD)
        // and process termination. RecordingPage also sets this on its own
        // construction; doing it here too means the shell works even when
        // recording is launched directly from the mini toolbar without ever
        // visiting RecordingPage.
        _viewModel.SetDispatcher(this.DispatcherQueue);

        // Wire inner-control events. The shell is the single state-machine
        // driver — these handlers translate user-action events into
        // TransitionToAsync calls.
        MiniSetup.RecordRequested += OnMiniSetupRecordRequested;
        MiniSetup.ExpandRequested += OnMiniSetupExpandRequested;
        MiniSetup.RequestRemeasure += OnMiniSetupRequestRemeasure;
        MiniSetup.DismissRequested += OnMiniSetupDismissRequested;
        MiniSetup.Loaded += OnMiniSetupLoadedForRemeasure;
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
        // Bare-Escape inside an open picker means "exit completely" — the
        // overlay raises this BEFORE cancelling itself so we can await the
        // close and then hide the shell.
        CapturePickerService.Shared.EscapeToDismissRequested += OnPickerEscapeToDismissRequested;
        // Async hooks: the picker service AWAITS these so the slide-out
        // animation completes BEFORE the region picker grabs the screen
        // (otherwise the toolbar gets captured in the frozen backdrop).
        CapturePickerService.Shared.OnPickerOpeningAsync = OnPickerOpeningAsyncHook;
        CapturePickerService.Shared.OnPickerClosedAsync = OnPickerClosedAsyncHook;

        // Watch for successful Stop so we can auto-open the Editor (spec §3.4).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Seed the summon timestamp so Esc-dismiss works during the initial
        // launch window (same ~5s grace as a real summon). Without this,
        // Esc on first launch was a no-op because _lastSummonAt was
        // DateTime.MinValue and the EscDismiss reducer requires
        // WasRecentlySummoned.
        _lastSummonAt = DateTime.UtcNow;

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
        // Set state-dependent inner-control visibility flags (ExpandButton,
        // docked-pill swap) BEFORE measuring, otherwise GetMiniSetupWindowSizeDip
        // will miss the ExpandButton width and the toolbar will be clipped.
        ApplyStateFlags(target);
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
            ApplyPresenterFor(target);
            UpdateBackdropFor(target);
            ApplyChromeFor(target);
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

        // Apply chrome for the target state BEFORE the morph animation so
        // the window border colours/rounding don't flicker mid-resize.
        // Order: presenter & backdrop first (both touch DWM attributes),
        // then chrome last so our border-colour override sticks.
        ApplyPresenterFor(target);
        UpdateBackdropFor(target);
        ApplyChromeFor(target);
        _minTrackStateOverride = target;

        var targetControl = GetControlForState(target);
        if (targetControl is RecordingPillControl pillIn)
            pillIn.ResetToRecordingState();
        // Both controls stay in the visual tree during the morph so the
        // window rect animation and the opacity cross-fade run in parallel
        // (~125 Hz timer). This is what gives the expand/collapse its 120fps
        // "snappy" feel — previously fade-out → resize → fade-in ran
        // sequentially for ~580 ms total.
        targetControl.Opacity = 0;
        targetControl.Visibility = Visibility.Visible;

        if (!IsSameRect(fromRect, toRect))
        {
            var duration = GetTransitionDuration(previous, target);
            var easing = GetEasingFor(previous, target);
            await AnimateMorphAsync(fromRect, toRect, duration, easing, outgoing, targetControl);
        }
        else
        {
            // Same rect, just swap visuals quickly.
            outgoing.Opacity = 0;
            targetControl.Opacity = 1;
        }
        outgoing.Visibility = Visibility.Collapsed;

        ApplyStateFlags(target);
        _currentState = target;
        _minTrackStateOverride = null;

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

        ApplyPresenterFor(target);
        UpdateBackdropFor(target);
        // Chrome last so our DWM border-colour / style strip survives any
        // resets that the presenter or SystemBackdrop assignment do.
        ApplyChromeFor(target);

        // Apply state-dependent inner-control flags (ExpandButton visibility,
        // etc.) BEFORE measuring/sizing the window so the toolbar measurement
        // sees its final visible children.
        ApplyStateFlags(target);

        var rect = ComputeWindowRect(target);
        MoveAndResizeWindow(rect);

        // Hide all, show target.
        MiniSetup.Visibility = Visibility.Collapsed; MiniSetup.Opacity = 0;
        RecordingPill.Visibility = Visibility.Collapsed; RecordingPill.Opacity = 0;
        FullShell.Visibility = Visibility.Collapsed; FullShell.Opacity = 0;

        var control = GetControlForState(target);
        control.Visibility = Visibility.Visible;
        control.Opacity = 1;

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
                int w = (int)Math.Ceiling(sizeDip.Width * scale);
                int h = (int)Math.Ceiling(sizeDip.Height * scale);
                int marginPx = (int)Math.Ceiling(16 * scale);
                w = Math.Min(w, Math.Max(1, work.Width - 2 * marginPx));
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + (int)Math.Round(TopMarginDip * scale);
                return new RectInt32(x, y, w, h);
            }
            case AppShellState.MiniRecording:
            {
                int w = (int)Math.Round(MiniRecordingWidth * scale);
                int h = (int)Math.Round(MiniRecordingHeight * scale);
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + (int)Math.Round(TopMarginDip * scale);
                return new RectInt32(x, y, w, h);
            }
            case AppShellState.Full:
            case AppShellState.FullRecording:
            {
                int w = (int)Math.Round(FullWidth * scale);
                int h = (int)Math.Round(FullHeight * scale);
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
        // Snappy: mini<->mini ~200ms, anything involving Full ~280ms.
        // Spec §4.6 originally specified 340/400, but the parallel
        // cross-fade + faster timer make those feel sluggish.
        if (!IsFullLike(from) && !IsFullLike(to))
            return TimeSpan.FromMilliseconds(200);
        return TimeSpan.FromMilliseconds(280);
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
        => AnimateMorphAsync(from, to, duration, easing, fadingOut: null, fadingIn: null);

    /// <summary>
    /// Drives the window-rect morph and, optionally, the opacity cross-fade
    /// of two inner controls in a single ~125 Hz (8 ms) timer loop. Doing
    /// both in one tick means the fade tracks the resize frame-for-frame,
    /// which is what makes expand/collapse feel snappy instead of segmented.
    /// </summary>
    private Task AnimateMorphAsync(
        RectInt32 from,
        RectInt32 to,
        TimeSpan duration,
        Func<double, double> easing,
        UIElement? fadingOut,
        UIElement? fadingIn)
    {
        var tcs = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        // 8 ms tick = ~125 Hz; on a 120 Hz display this gives one frame per
        // tick. WinUI's Window.MoveAndResize doesn't sub-frame interpolate so
        // we cap at one update per refresh anyway.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            try
            {
                double total = Math.Max(1.0, duration.TotalMilliseconds);
                double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / total);
                double eased = easing(t);
                var current = Interpolate(from, to, eased);
                MoveAndResizeWindow(current);
                if (fadingOut is not null)
                {
                    // Crossfade compressed into the first ~55% of the morph
                    // so the outgoing control is gone before the rect grows
                    // past its layout bounds.
                    double fo = Math.Min(1.0, eased / 0.55);
                    fadingOut.Opacity = 1 - fo;
                }
                if (fadingIn is not null)
                {
                    // Incoming fades in over the last ~55%, overlapping the
                    // outgoing fade by ~10% for a smooth handoff.
                    double fi = Math.Max(0.0, (eased - 0.45) / 0.55);
                    fadingIn.Opacity = Math.Min(1.0, fi);
                }
                if (t >= 1.0)
                {
                    timer.Stop();
                    MoveAndResizeWindow(to);
                    if (fadingOut is not null) fadingOut.Opacity = 0;
                    if (fadingIn is not null) fadingIn.Opacity = 1;
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
        // Keep SystemBackdrop=MicaBackdrop for ALL states. WinUI sets the
        // window root background to Transparent when a SystemBackdrop is
        // assigned, which is what lets Mica show through. ApplyChromeFor
        // (which runs AFTER this) then strips WS_DLGFRAME to kill the
        // 1px Win11 accent edge — Mica keeps rendering because the
        // SystemBackdrop machinery is already hooked up by that point.
        if (SystemBackdrop is not MicaBackdrop mica || mica.Kind != MicaKind.BaseAlt)
        {
            try { SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Mica Alt backdrop failed: {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------
    // Inner-control event handlers (the state-machine wiring)
    // ---------------------------------------------------------------

    private async void OnMiniSetupRecordRequested(object? sender, EventArgs e)
    {
        try
        {
            // If the region picker is open (we auto-launch it when the user
            // selects the Region tab), the Record button acts as the implicit
            // confirm — commit whatever is currently drawn. The await on the
            // picker in MiniSetupControl.LaunchRegionPickerAsync then resolves
            // and writes ViewModel.SelectedRegion before we transition.
            if (CapturePickerService.Shared.IsPickerOpen
                && _viewModel.CaptureMode == CaptureMode.CustomRegion)
            {
                CapturePickerService.Shared.TryConfirmActiveRegionPicker();
                // Yield so the picker's TCS completion + the awaiting
                // LaunchRegionPickerAsync continuation get a chance to write
                // ViewModel.SelectedRegion before we read it downstream.
                await Task.Yield();
            }
            else if (CapturePickerService.Shared.IsPickerOpen
                && _viewModel.CaptureMode == CaptureMode.Window)
            {
                CapturePickerService.Shared.TryConfirmActiveWindowPicker();
                await Task.Yield();
            }

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
        // Re-apply chrome now that the HWND is fully realised. Some Win11
        // DWM attributes (DWMWA_BORDER_COLOR in particular) silently no-op
        // when set before the window has been activated for the first time.
        try { ApplyChromeFor(_currentState); }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Post-activation ApplyChromeFor failed: {ex}"); }
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
                    // Place focus on the toolbar so first-launch Escape can
                    // reach MiniSetupControl.OnKeyDown — without this, the
                    // initial activation leaves focus unset and Esc was a
                    // silent no-op until the user round-tripped through a
                    // hotkey summon (which calls FocusRecordButton).
                    try { MiniSetup.FocusRecordButton(); }
                    catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Initial FocusRecordButton failed: {ex.Message}"); }
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
        int targetWidth = (int)Math.Ceiling(sizeDip.Width * scale);
        int targetHeight = (int)Math.Ceiling(sizeDip.Height * scale);
        int marginPx = (int)Math.Ceiling(16 * scale);
        targetWidth = Math.Min(targetWidth, Math.Max(1, work.Width - 2 * marginPx));
        int targetX = work.X + (work.Width - targetWidth) / 2;
        int targetY = work.Y + (int)Math.Round(TopMarginDip * scale);

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
                // Pure content-driven width — the toolbar hugs whatever its
                // visible children require. Height is pinned to MiniSetupHeight
                // (64 DIP per spec §4.1); the ToolbarBorder is stretched
                // vertically so the visible pill IS the window — no acrylic
                // gap above/below it.
                return new Size(
                    Math.Ceiling(desired.Width + (2 * WindowChromeBorder)),
                    Math.Ceiling(MiniSetupHeight));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] MiniSetup measure failed: {ex.Message}");
        }

        return new Size(MiniSetupFallbackWidth, MiniSetupHeight);
    }

    // ---------------------------------------------------------------
    // Phase C: slide-out-of-view-while-picking glue + focus watchdog (spec §4.5)
    // ---------------------------------------------------------------

    private DispatcherTimer? _focusWatchdog;
    private PointInt32? _positionBeforeSlide;

    private void OnPickerOpening(object? sender, PickerOpeningEventArgs e)
    {
        // Sync hook: just arm the defensive focus watchdog (spec §4.5). The
        // slide-out animation runs in OnPickerOpeningAsyncHook, which the
        // picker service awaits before showing the picker overlay.
        try
        {
            _focusWatchdog?.Stop();
            _focusWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _focusWatchdog.Tick += OnFocusWatchdogTick;
            _focusWatchdog.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] Picker opening hook failed: {ex.Message}"); }
    }

    private async Task OnPickerOpeningAsyncHook(PickerOpeningEventArgs e)
    {
        // Intentionally a no-op for now. The slide-out-of-view experiment for
        // region picking is disabled — the toolbar stays put so the user can
        // see it while dragging. Method retained so the picker service still
        // has something to await (cheap) and so the hook is easy to re-enable
        // if we revisit the behaviour.
        await Task.CompletedTask;
    }

    private void OnPickerClosed(object? sender, EventArgs e)
    {
        try { _focusWatchdog?.Stop(); _focusWatchdog = null; } catch { }
    }

    private async Task OnPickerClosedAsyncHook()
    {
        // No slide-back required while the slide-out is disabled. Kept as a
        // no-op for symmetry with the opening hook.
        await Task.CompletedTask;
    }

    private Task SlideOutOfViewAsync()
    {
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _positionBeforeSlide = pos;
        int targetY = -(size.Height + 40);
        var fromRect = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
        var toRect = new RectInt32(pos.X, targetY, size.Width, size.Height);
        return AnimateWindowAsync(fromRect, toRect, TimeSpan.FromMilliseconds(180), QuadraticEaseInOut);
    }

    private Task SlideBackIntoViewAsync(PointInt32 originalPos)
    {
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var fromRect = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
        var toRect = new RectInt32(originalPos.X, originalPos.Y, size.Width, size.Height);
        return AnimateWindowAsync(fromRect, toRect, TimeSpan.FromMilliseconds(220), QuadraticEaseInOut);
    }

    private void OnFocusWatchdogTick(object? sender, object e)
    {
        // Best-effort: if neither the shell nor any other Musio window owns the
        // foreground 2 s after the picker opened, the picker is probably hung
        // or has lost focus to some other app — stop the watchdog so we don't
        // keep ticking.
        try
        {
            var foreground = GetForegroundWindow();
            var shellHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (foreground != shellHwnd && !IsOurProcessWindow(foreground))
            {
                if (sender is DispatcherTimer t) t.Stop();
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
    private readonly RegionBorderManager _regionBorder = new();
    private bool _handlingStop;
    private DateTime _lastSummonAt = DateTime.MinValue;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingViewModel.IsRecording)) return;
        bool isRecording = _viewModel.IsRecording;
        bool justStarted = !_wasRecordingLastTick && isRecording;
        bool justStopped = _wasRecordingLastTick && !isRecording;
        _wasRecordingLastTick = isRecording;

        if (justStarted)
        {
            // Show the red region border (only in CustomRegion mode; no-op
            // otherwise) — RecordingPage's copy of this only fires in the
            // Full shell, so without this Mini-mode region recordings give
            // the user no visual indication of what's being captured.
            try { _regionBorder.ShowIfNeeded(_viewModel); }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] ShowRegionBorder failed: {ex}"); }

            // Open the capture gate now that the shell has already morphed
            // to MiniRecording / FullRecording (the morph awaits in
            // OnMiniSetupRecordRequested before StartRecordingCommand runs).
            // Without this, the gate stays closed in Mini mode (RecordingPage
            // — which used to own this call — is never navigated) and every
            // captured frame is discarded → empty video.mp4 / no .frames dir
            // → blue-track + empty preview in the Editor.
            try { _viewModel.OpenCaptureGate(); }
            catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] OpenCaptureGate failed: {ex}"); }
            return;
        }

        if (!justStopped) return;

        // Tear down the region border on stop (covers Mini-mode stops; the
        // Full shell's RecordingPage also hides its own border but its
        // handler may not run if the page isn't loaded).
        try { _regionBorder.Hide(); } catch { }

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
                // Keep the left-nav highlight in sync with the visible page;
                // programmatic Frame.Navigate doesn't update NavigationView's
                // SelectedItem, which would otherwise leave "Record" selected
                // while the Editor is on screen.
                FullShell.SelectNavItem("editor");
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
            bool wasHidden = !IsWindowVisible(hwnd);

            bool keepCurrent = suppressMiniMorph || _viewModel.IsRecording;
            var desiredState = keepCurrent ? _currentState : AppShellState.MiniSetup;

            if (wasHidden && desiredState == AppShellState.MiniSetup)
            {
                // PrepareForSummonInBackground (called from OnWindowClosing)
                // has already put the shell into MiniSetup with the correct
                // chrome / size / layout — so we can safely position the
                // window above the work area and slide it down without any
                // first-paint stale-content flash.
                if (_currentState != AppShellState.MiniSetup)
                    ConfigureForState(AppShellState.MiniSetup, animate: false);

                var target = ComputeWindowRect(AppShellState.MiniSetup);
                var startRect = new RectInt32(
                    target.X,
                    target.Y - target.Height - 24,
                    target.Width,
                    target.Height);
                MoveAndResizeWindow(startRect);
                StateHost.UpdateLayout();

                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                AllowSetForegroundWindow(unchecked((uint)Environment.ProcessId));
                SetForegroundWindow(hwnd);

                await AnimateWindowAsync(startRect, target,
                    TimeSpan.FromMilliseconds(240), CubicEaseOut);

                Activate();
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
                ShowWindow(hwnd, SW_RESTORE);

                if (!keepCurrent && _currentState != AppShellState.MiniSetup)
                    await TransitionToAsync(AppShellState.MiniSetup);

                AllowSetForegroundWindow(unchecked((uint)Environment.ProcessId));
                SetForegroundWindow(hwnd);
                Activate();
            }

            _lastSummonAt = DateTime.UtcNow;
            MiniSetup.FocusRecordButton();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Summon failed: {ex}");
        }
    }

    /// <summary>
    /// Called by <see cref="App"/> right before <c>SW_HIDE</c> on close-to-tray.
    /// Reconfigures the shell into <see cref="AppShellState.MiniSetup"/>
    /// (chrome, presenter, controls, size, position) WHILE the window is
    /// still visible — i.e. all the layout work happens up-front, so the
    /// next summon can just slide the already-configured window in from
    /// above without a first-paint flash of the old Full content.
    /// </summary>
    public void PrepareForSummonInBackground()
    {
        if (_viewModel.IsRecording) return; // don't disturb an active recording
        if (_currentState == AppShellState.MiniSetup) return;

        try
        {
            ConfigureForState(AppShellState.MiniSetup, animate: false);
            var target = ComputeWindowRect(AppShellState.MiniSetup);
            MoveAndResizeWindow(target);
            StateHost.UpdateLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] PrepareForSummonInBackground failed: {ex}");
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
        _ = DismissToTrayWithSlideAsync();
    }

    /// <summary>
    /// Esc inside an open picker with nothing selected: cancel the picker
    /// and hide the shell. Bypasses the WasRecentlySummoned gate because
    /// the user explicitly pressed Esc inside the picker — the intent is
    /// unambiguous.
    /// </summary>
    private async void OnPickerEscapeToDismissRequested(object? sender, EventArgs e)
    {
        try { await CapturePickerService.Shared.CancelActivePickerAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[AppShellWindow] CancelActivePickerAsync failed: {ex.Message}"); }

        if (_currentState != AppShellState.MiniSetup || _viewModel.IsRecording)
            return;

        await DismissToTrayWithSlideAsync();
    }

    /// <summary>
    /// Mirror of the summon slide-down (see <see cref="SummonAsync"/>): animate
    /// the window upward off the work area, then <c>SW_HIDE</c>. Replaces the
    /// instant SW_HIDE flash so dismiss reads as a deliberate retraction
    /// matching the slide-in on summon.
    /// </summary>
    private async Task DismissToTrayWithSlideAsync()
    {
        try
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            var fromRect = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
            var toRect = new RectInt32(pos.X, pos.Y - size.Height - 24, size.Width, size.Height);

            await AnimateWindowAsync(fromRect, toRect,
                TimeSpan.FromMilliseconds(200), QuadraticEaseInOut);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_HIDE);

            // Restore on-screen position so the next non-summon path (e.g.
            // tray click that just SW_SHOWs without recomputing rect) doesn't
            // open with the window parked off-screen.
            try { MoveAndResizeWindow(fromRect); } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShellWindow] Slide-out dismiss failed: {ex.Message}");
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_HIDE);
            }
            catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
}
