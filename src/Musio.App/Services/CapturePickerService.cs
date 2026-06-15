using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Musio_App.Controls;
using Musio_App.ViewModels;
using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio_App.Services;

/// <summary>
/// Owns the "launch picker, await user selection, push result into the shared
/// recording VM" flow that used to live inline in
/// <c>RecordingPage.LaunchRegionPickerAsync</c> / <c>LaunchWindowPickerAsync</c>.
/// </summary>
/// <remarks>
/// Centralising this lets both the page (Phase A) and the future
/// <c>AppShellWindow</c> (Phase B+) launch pickers via the same code path
/// without each call site duplicating the orchestration.
/// </remarks>
public sealed class CapturePickerService
{
    /// <summary>Shared singleton mirroring <see cref="RecordingViewModel.Shared"/>.</summary>
    public static CapturePickerService Shared { get; } = new();

    private readonly Func<CaptureRegion?, Task<CaptureRegion?>> _showRegionPickerAsync;
    private readonly Func<Task<WindowInfo?>> _showWindowPickerAsync;
    private readonly RecordingViewModel _viewModel;

    private CapturePickerService()
        : this(
            previous =>
            {
                var overlay = new RegionSelectorOverlay();
                // Track the live overlay so the toolbar's Record button can
                // confirm-and-record without an in-overlay Confirm button.
                ActiveRegionOverlay = overlay;
                var task = overlay.ShowAsync(previous);
                _ = task.ContinueWith(_ => { ActiveRegionOverlay = null; }, TaskScheduler.Default);
                return task;
            },
            () =>
            {
                var overlay = new WindowSelectorOverlay();
                // Track so a tab-switch away from Window can programmatically
                // cancel the open picker.
                ActiveWindowOverlay = overlay;
                var task = overlay.ShowAsync();
                _ = task.ContinueWith(_ => { ActiveWindowOverlay = null; }, TaskScheduler.Default);
                return task;
            },
            RecordingViewModel.Shared)
    {
    }

    /// <summary>
    /// The active region overlay, set while a region picker is on screen.
    /// Exposed (internally) so <see cref="TryConfirmActiveRegionPicker"/>
    /// can drive Record-button-as-implicit-confirm UX.
    /// </summary>
    internal static RegionSelectorOverlay? ActiveRegionOverlay { get; set; }

    /// <summary>The active window overlay, set while a window picker is on screen.</summary>
    internal static WindowSelectorOverlay? ActiveWindowOverlay { get; set; }

    /// <summary>
    /// If a region picker is currently open AND has a selection drawn,
    /// programmatically commit it (the picker's await in
    /// <see cref="PickRegionAsync"/> resolves with the region). Returns true
    /// when something was confirmed; false otherwise (no picker open or no
    /// selection drawn yet).
    /// </summary>
    public bool TryConfirmActiveRegionPicker()
        => ActiveRegionOverlay?.TryConfirmCurrent() ?? false;

    /// <summary>
    /// If a window picker is currently open AND a window has been clicked
    /// (or is hovered), programmatically commit it. Mirrors
    /// <see cref="TryConfirmActiveRegionPicker"/> so the toolbar's Record
    /// button works for both inline pickers.
    /// </summary>
    public bool TryConfirmActiveWindowPicker()
        => ActiveWindowOverlay?.TryConfirmCurrent() ?? false;

    /// <summary>
    /// Cancel whichever picker (region or window) is currently open and
    /// wait until it has fully closed (so the next <see cref="PickRegionAsync"/>
    /// / <see cref="PickWindowAsync"/> call won't be rejected by the
    /// re-entrancy guard). No-op when no picker is open.
    /// </summary>
    public async Task CancelActivePickerAsync()
    {
        if (!_isPickerOpen) return;

        try { ActiveRegionOverlay?.Cancel(); } catch { /* best-effort */ }
        try { ActiveWindowOverlay?.Cancel(); } catch { /* best-effort */ }

        // Wait for the currently-running PickXxxAsync to complete its finally
        // block (which clears _isPickerOpen + the active overlay refs).
        var signal = _pickerClosedSignal;
        if (signal is not null)
        {
            try { await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* timeout or already completed — fall through */ }
        }
    }

    // Per-call signal completed in the picker's finally so cancellers can
    // await actual close, not just the cancel signal.
    private TaskCompletionSource<object?>? _pickerClosedSignal;

    internal CapturePickerService(
        Func<CaptureRegion?, Task<CaptureRegion?>> showRegionPickerAsync,
        Func<Task<WindowInfo?>> showWindowPickerAsync,
        RecordingViewModel viewModel)
    {
        _showRegionPickerAsync = showRegionPickerAsync ?? throw new ArgumentNullException(nameof(showRegionPickerAsync));
        _showWindowPickerAsync = showWindowPickerAsync ?? throw new ArgumentNullException(nameof(showWindowPickerAsync));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private bool _isPickerOpen;

    /// <summary>
    /// True while a picker overlay is on screen. Used to suppress re-entrant
    /// launches (e.g. capture-mode toggle while a picker is already up).
    /// </summary>
    public bool IsPickerOpen => _isPickerOpen;

    /// <summary>Raised right before a picker overlay is shown.</summary>
    public event EventHandler<PickerOpeningEventArgs>? PickerOpening;

    /// <summary>
    /// Optional async hook invoked AFTER <see cref="PickerOpening"/> and BEFORE
    /// the picker overlay is shown. The picker service awaits it so callers
    /// (e.g. <c>AppShellWindow</c>) can finish a slide-out animation before
    /// the picker grabs the screen contents.
    /// </summary>
    public Func<PickerOpeningEventArgs, Task>? OnPickerOpeningAsync { get; set; }

    /// <summary>Raised after a picker overlay has been dismissed (confirmed or cancelled).</summary>
    public event EventHandler? PickerClosed;

    /// <summary>
    /// Raised by an open picker overlay when the user presses Escape with
    /// nothing selected. Signals the host shell that the user wants to
    /// dismiss the entire toolbar (not just close the picker). The overlay
    /// cancels itself immediately after raising this; the shell handler
    /// should await <see cref="CancelActivePickerAsync"/> and then hide.
    /// </summary>
    public event EventHandler? EscapeToDismissRequested;

    internal void RaiseEscapeToDismissRequested()
        => EscapeToDismissRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Optional async hook invoked AFTER the picker overlay has been
    /// dismissed and AFTER <see cref="PickerClosed"/> fires. The picker
    /// service awaits it so callers can finish a slide-back-in animation
    /// before control returns to them.
    /// </summary>
    public Func<Task>? OnPickerClosedAsync { get; set; }

    private RecordingViewModel ViewModel => _viewModel;

    /// <summary>
    /// Show the region picker. On confirm, updates
    /// <see cref="RecordingViewModel.SelectedRegion"/> / <see cref="RecordingViewModel.HasSelectedRegion"/>
    /// on <see cref="RecordingViewModel.Shared"/> and returns
    /// <see cref="PickerResult.Selected"/>. Returns
    /// <see cref="PickerResult.Cancelled"/> if the user explicitly dismissed
    /// the picker, or <see cref="PickerResult.AlreadyOpen"/> if the re-entrancy
    /// guard rejected the call (no picker was shown — callers should treat
    /// this as a silent no-op).
    /// </summary>
    /// <param name="owner">
    /// Window that should own the picker overlay (used today only by
    /// future-phase positioning logic; the picker itself self-hosts).
    /// </param>
    public async Task<PickerResult> PickRegionAsync(Window? owner)
    {
        if (_isPickerOpen) return PickerResult.AlreadyOpen;
        _isPickerOpen = true;
        _pickerClosedSignal = new TaskCompletionSource<object?>();

        try
        {
            var args = new PickerOpeningEventArgs(PickerKind.Region);
            PickerOpening?.Invoke(this, args);
            if (OnPickerOpeningAsync is { } prepareAsync)
            {
                try { await prepareAsync(args); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] PickerOpeningAsync hook failed: {ex.Message}"); }
            }

            var region = await _showRegionPickerAsync(ViewModel.SelectedRegion);

            if (region is not null)
            {
                ViewModel.SelectedRegion = region;
                ViewModel.HasSelectedRegion = true;
                return PickerResult.Selected;
            }

            return PickerResult.Cancelled;
        }
        finally
        {
            _isPickerOpen = false;
            PickerClosed?.Invoke(this, EventArgs.Empty);
            if (OnPickerClosedAsync is { } closedAsync)
            {
                try { await closedAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] PickerClosedAsync hook failed: {ex.Message}"); }
            }
            _pickerClosedSignal?.TrySetResult(null);
            _pickerClosedSignal = null;
        }
    }

    /// <summary>
    /// Show the window picker. On confirm, updates
    /// <see cref="RecordingViewModel.SelectedWindow"/> on
    /// <see cref="RecordingViewModel.Shared"/> and returns
    /// <see cref="PickerResult.Selected"/>. Returns
    /// <see cref="PickerResult.Cancelled"/> if the user explicitly dismissed
    /// the picker, or <see cref="PickerResult.AlreadyOpen"/> if the
    /// re-entrancy guard rejected the call.
    /// </summary>
    public async Task<PickerResult> PickWindowAsync(Window? owner)
    {
        if (_isPickerOpen) return PickerResult.AlreadyOpen;
        _isPickerOpen = true;
        _pickerClosedSignal = new TaskCompletionSource<object?>();

        try
        {
            var args = new PickerOpeningEventArgs(PickerKind.Window);
            PickerOpening?.Invoke(this, args);
            if (OnPickerOpeningAsync is { } prepareAsync)
            {
                try { await prepareAsync(args); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] PickerOpeningAsync hook failed: {ex.Message}"); }
            }

            var window = await _showWindowPickerAsync();

            if (window is not null)
            {
                ViewModel.SelectedWindow = window;
                try { ShellSettings.Instance.LastWindowSelection = BuildWindowSelectionTuple(window); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] Persist window failed: {ex.Message}"); }
                return PickerResult.Selected;
            }

            return PickerResult.Cancelled;
        }
        finally
        {
            _isPickerOpen = false;
            PickerClosed?.Invoke(this, EventArgs.Empty);
            if (OnPickerClosedAsync is { } closedAsync)
            {
                try { await closedAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] PickerClosedAsync hook failed: {ex.Message}"); }
            }
            _pickerClosedSignal?.TrySetResult(null);
            _pickerClosedSignal = null;
        }
    }

    private static (string ProcessName, string WindowTitle, string ClassName) BuildWindowSelectionTuple(WindowInfo window)
    {
        string className = TryGetClassName(window.Handle) ?? string.Empty;
        // WindowInfo.ProcessName is already without the .exe suffix.
        return (window.ProcessName ?? string.Empty,
                window.Title ?? string.Empty,
                className);
    }

    private static string? TryGetClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            int copied = GetClassName(hwnd, sb, sb.Capacity);
            return copied > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}

/// <summary>
/// Which kind of picker is being opened. Lets the shell decide whether to
/// slide the toolbar out of view (Region picker uses the whole screen as a
/// drag surface) or leave it in place (Window picker only needs clicks on
/// existing windows).
/// </summary>
public enum PickerKind
{
    Region,
    Window,
}

/// <summary>Event args for <see cref="CapturePickerService.PickerOpening"/>.</summary>
public sealed class PickerOpeningEventArgs : EventArgs
{
    public PickerOpeningEventArgs(PickerKind kind) { Kind = kind; }
    public PickerKind Kind { get; }
}

/// <summary>
/// Outcome of a <see cref="CapturePickerService"/> picker call.
/// </summary>
public enum PickerResult
{
    /// <summary>User confirmed a selection; the shared VM was updated.</summary>
    Selected,

    /// <summary>The picker was shown and the user explicitly cancelled (Esc / close / cancel button).</summary>
    Cancelled,

    /// <summary>
    /// The re-entrancy guard rejected the call because another picker was
    /// already on screen. No picker was shown; callers should treat this as
    /// a silent no-op (in particular, do NOT surface a "kept previous
    /// selection" InfoBar message — nothing changed).
    /// </summary>
    AlreadyOpen,
}
