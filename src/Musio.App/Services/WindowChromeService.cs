using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Musio_App.Services;

/// <summary>
/// Pick the visual chrome treatment a <see cref="Window"/> should adopt.
/// Phase A only uses <see cref="Mini"/>; <see cref="Full"/> is a placeholder
/// for the unified shell window that lands in a later phase.
/// </summary>
public enum ChromeProfile
{
    /// <summary>
    /// Borderless rounded pill chrome shared by the recording overlay and the
    /// future Mini Setup toolbar: no border, no caption colour, OS-rounded
    /// corners. Recording states opt into capture exclusion separately.
    /// </summary>
    Mini,

    /// <summary>
    /// Default WinUI 3 chrome with standard caption. Reserved for the unified
    /// <c>AppShellWindow</c>; currently unused.
    /// </summary>
    Full,
}

/// <summary>
/// Centralizes the Win32 / DWM calls used to make a WinUI 3 <see cref="Window"/>
/// look like the recording pill (or, in the future, the full app shell).
/// Behaviour previously lived inline in <c>RecordingOverlayWindow.ConfigureWindow</c>.
/// </summary>
public static class WindowChromeService
{
    /// <summary>
    /// Apply the chrome treatment described by <paramref name="profile"/> to
    /// <paramref name="window"/>. Safe to call from the window's constructor
    /// after <c>InitializeComponent</c> — the window must already have an
    /// <see cref="Microsoft.UI.Windowing.AppWindow"/> backing it.
    /// </summary>
    public static void ApplyTo(Window window, ChromeProfile profile)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        switch (profile)
        {
            case ChromeProfile.Mini:
                ApplyMini(window);
                break;
            case ChromeProfile.Full:
                ApplyFull(window);
                break;
        }
    }

    /// <summary>
    /// Strip frame styles for full-screen overlay/picker windows. Same
    /// rationale as <see cref="ApplyMini"/> — kills the 1px lighter
    /// WS_DLGFRAME edge Win11 draws around any window — but does NOT
    /// touch the backdrop (overlays paint their own dim/smoke layer).
    /// </summary>
    public static void ApplyOverlayChrome(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = (long)GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(long)(WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_DLGFRAME | WS_BORDER);
        SetWindowLong(hwnd, GWL_STYLE, (IntPtr)style);
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private static void ApplyMini(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        SetWindowDisplayAffinity(hwnd, WDA_NONE);

        // Strip ALL frame styles. The thin 1px lighter edge users were
        // seeing was Win11's WS_DLGFRAME border rendered by DWM in the
        // active-window accent colour. WinUI's SystemBackdrop=MicaBackdrop
        // (set in AppShellWindow.UpdateBackdropFor) initialises the Mica
        // compositor BEFORE we reach this strip, so Mica keeps rendering
        // even without WS_DLGFRAME.
        var style = (long)GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(long)(WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_DLGFRAME | WS_BORDER);
        SetWindowLong(hwnd, GWL_STYLE, (IntPtr)style);

        // Belt-and-suspenders: also tell DWM not to paint any border
        // (COLOR_NONE) in case the compositor still draws an active-window
        // accent line.
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(uint));

        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Switch a previously-Mini window back to standard chrome (used when the
    /// unified shell morphs Mini → Full). Restores capture inclusion, removes
    /// the DWM border-color override, and re-adds WS_BORDER/WS_DLGFRAME so the
    /// caption renders. Capture exclusion is re-applied by callers that need
    /// it (FullRecording).
    /// </summary>
    private static void ApplyFull(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Default: don't exclude from capture (FullRecording flips this back
        // on via SetCaptureExclusion).
        SetWindowDisplayAffinity(hwnd, WDA_NONE);

        // Restore default DWM border + caption colours (DWMWA_COLOR_DEFAULT)
        uint colorDefault = DWMWA_COLOR_DEFAULT;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorDefault, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorDefault, sizeof(uint));

        // Re-add WS_BORDER and WS_DLGFRAME so the standard caption renders
        // (Mini stripped them; Full needs them back). Idempotent if already set.
        var style = (long)GetWindowLong(hwnd, GWL_STYLE);
        style |= (long)(WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, (IntPtr)style);

        // Standard OS rounding for top-level windows.
        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

        // Force the non-client area to recompute now that the style flipped.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Toggle capture-exclusion (<c>WDA_EXCLUDEFROMCAPTURE</c>) independently
    /// of the chrome profile. Used to flip the Full window into / out of
    /// capture-excluded mode when entering / leaving the FullRecording state
    /// without re-applying the rest of the chrome treatment.
    /// </summary>
    public static void SetCaptureExclusion(Window window, bool exclude)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMSBT_NONE = 1;
    private const uint DWMSBT_MAINWINDOW = 2;
    private const uint DWMSBT_TRANSIENTWINDOW = 3;
    private const uint DWMSBT_TABBEDWINDOW = 4;
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    // Use the *Ptr variants so the LONG_PTR return/parameter is the natural
    // 8-byte width on x64 (the int versions silently truncated the top half).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
