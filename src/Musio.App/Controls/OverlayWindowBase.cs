using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Musio.Core.Interop;

namespace Musio_App.Controls;

/// <summary>
/// Shared host-window lifecycle for the full-screen picker overlays
/// (<c>RegionSelectorOverlay</c> / <c>WindowSelectorOverlay</c>): hides the
/// shell (or minimizes the main window), waits for the hide animation, captures
/// the desktop screenshot, creates a borderless always-on-top host
/// <see cref="Window"/> sized to the virtual desktop, installs/uninstalls the
/// Escape low-level keyboard hook, and restores the shell — all bracketed so
/// every exit path (confirm, cancel, Escape, or exception) leaves the app
/// visible again.
/// </summary>
/// <remarks>
/// Each overlay owns one instance via a private nested subclass rather than
/// inheriting directly, so the overlay's own XAML root type (<c>UserControl</c>)
/// is left untouched — see the W4-1 UI consolidation report for why an
/// inheritance-based design (changing the XAML root element) was rejected as
/// unnecessary extra risk in a surface with no automated test coverage.
/// </remarks>
internal abstract class OverlayWindowBase
{
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private static readonly IntPtr WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private IntPtr _keyboardHook;
    private HookInterop.LowLevelKeyboardProc? _hookProc;

    /// <summary>The borderless host window, non-null only while a picker is showing.</summary>
    protected Window? HostWindow { get; private set; }

    /// <summary>The overlay <see cref="UIElement"/> to host as the window's content.</summary>
    protected abstract UIElement Content { get; }

    /// <summary>The dispatcher the Escape hook marshals its callback onto.</summary>
    protected abstract DispatcherQueue DispatcherQueue { get; }

    /// <summary>Invoked (via <see cref="DispatcherQueue"/>) when Escape is pressed.</summary>
    protected abstract void OnEscapePressed();

    /// <summary>
    /// Optional hook that runs after the shell/main-window is hidden and the
    /// hide-animation delay has elapsed, but before the desktop screenshot is
    /// captured. Default is a no-op.
    /// </summary>
    protected virtual Task OnBeforeScreenshotAsync() => Task.CompletedTask;

    /// <summary>
    /// Called once <see cref="HostWindow"/> exists, is presenter-configured, and
    /// sized to the virtual desktop, but before the Escape hook is installed and
    /// the window is activated. Implementations use this to assign the captured
    /// screenshot to their image control, wire up <c>Closed</c>/<c>Activated</c>,
    /// and stash any pixel data they need.
    /// </summary>
    protected abstract void OnHostWindowReady(OverlayScreenshotResult screenshot);

    /// <summary>
    /// Runs the full picker lifecycle described on the type, and returns the
    /// result of <paramref name="awaitResult"/> (typically a picker's
    /// <see cref="TaskCompletionSource{T}"/>.Task). Public (rather than
    /// protected) because the owning overlay drives this from outside the
    /// nested subclass that implements the abstract members below.
    /// </summary>
    public async Task<T?> ShowOverlayAsync<T>(
        string title,
        int shellHideDelayMs,
        bool includeScreenshotPixels,
        Func<Task<T?>> awaitResult)
        where T : class
    {
        bool didMinimize = false;
        IntPtr mainHwnd = IntPtr.Zero;
        var shell = Musio_App.Services.ShellCoordinator.Instance;
        var mainWindow = Musio_App.App.Current.MainAppWindow;

        try
        {
            // Minimize Musio so it doesn't appear in the screenshot.
            if (shell is not null)
            {
                // The shell knows whether the Mini pill or the full window is up.
                shell.HideForPicker();
                didMinimize = true;
                await Task.Delay(shellHideDelayMs); // let the hide/minimize animation complete
            }
            else if (mainWindow is not null)
            {
                mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                NativeMethods.ShowWindow(mainHwnd, SW_MINIMIZE);
                didMinimize = true;
                await Task.Delay(shellHideDelayMs); // let the minimize animation complete
            }

            await OnBeforeScreenshotAsync();

            var screenshot = await OverlayScreenshotHelper.CaptureDesktopScreenshotAsync(includeScreenshotPixels);

            HostWindow = new Window
            {
                Content = Content,
                ExtendsContentIntoTitleBar = true,
                Title = title,
            };

            // Hide title bar chrome; do NOT maximize — Maximize() only covers a
            // single monitor. We size the window manually to the full virtual
            // desktop so the overlay covers every display.
            ConfigureBorderlessAlwaysOnTopPresenter(HostWindow.AppWindow);

            // Position and size the window to span the entire virtual desktop
            // (all monitors) in physical pixels. This matches the dimensions of
            // the screenshot captured above so the image displays 1:1 with each
            // monitor's physical pixels, even across mixed-DPI setups.
            int vdLeft = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_XVIRTUALSCREEN);
            int vdTop = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_YVIRTUALSCREEN);
            int vdWidth = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CXVIRTUALSCREEN);
            int vdHeight = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CYVIRTUALSCREEN);
            if (vdWidth > 0 && vdHeight > 0)
            {
                HostWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    vdLeft, vdTop, vdWidth, vdHeight));
            }

            OnHostWindowReady(screenshot);

            // Install low-level keyboard hook so Escape works even without XAML focus.
            // Pass the real module handle (as KeyboardHookRecorder does): SetWindowsHookEx
            // can reject a NULL hMod, and a silent failure here costs the user Escape-to-cancel
            // on a full-screen always-on-top overlay, so log it rather than fail quietly.
            _hookProc = EscapeHookCallback;
            _keyboardHook = SetWindowsHookEx(HookInterop.WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                Musio.Core.Diagnostics.DiagLog.Write(
                    "Overlay",
                    $"Escape keyboard hook could not be installed (error {Marshal.GetLastWin32Error()}); " +
                    "Escape-to-cancel will only work while the overlay has XAML focus.");
            }

            HostWindow.Activate();

            return await awaitResult();
        }
        finally
        {
            // Must run even if the screenshot or host window fails: the shell hide
            // is a latch that only this call releases, so skipping it would leave
            // the Mini pill (or the minimised main window) hidden for good — and the
            // pill isn't in Alt-Tab, so the user would have only the tray icon left.
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            _hookProc = null;

            try { HostWindow?.Close(); }
            catch { /* already closed */ }
            HostWindow = null;

            // Restore Musio if we minimized it
            if (didMinimize)
            {
                if (shell is not null)
                {
                    shell.RestoreAfterPicker();
                }
                else if (mainHwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(mainHwnd, SW_RESTORE);
                    mainWindow?.Activate();
                }
            }
        }
    }

    private IntPtr EscapeHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_ESCAPE)
            {
                DispatcherQueue.TryEnqueue(OnEscapePressed);
                return (IntPtr)1;
            }
        }
        return HookInterop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Applies the borderless, always-on-top, non-resizable/maximizable/minimizable
    /// presenter configuration shared by all four borderless overlay/pill windows:
    /// the region/window picker host windows, <c>MiniWindow</c>, and
    /// <c>RecordingOverlayWindow</c>.
    /// </summary>
    public static void ConfigureBorderlessAlwaysOnTopPresenter(AppWindow appWindow)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookInterop.LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
