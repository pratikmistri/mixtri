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

    private static void ApplyMini(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Mini Setup must be visible to screenshots/accessibility checks.
        // Recording states opt into WDA_EXCLUDEFROMCAPTURE via
        // SetCaptureExclusion when they need to stay out of recordings.
        SetWindowDisplayAffinity(hwnd, WDA_NONE);

        // Remove DWM-drawn border and caption
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        uint colorCaption = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorCaption, sizeof(uint));

        // Strip WS_BORDER and WS_DLGFRAME from the window style
        var style = (long)GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(long)(WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, (IntPtr)style);

        // Round the window corners at the OS level for a pill shape
        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

        // Force the non-client area to recompute now that the style flipped.
        // Without SWP_FRAMECHANGED the frame can stay stale until the next
        // move/resize, which matters for the Full <-> FullRecording no-morph
        // path where MoveAndResize never fires.
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

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    // Use the *Ptr variants so the LONG_PTR return/parameter is the natural
    // 8-byte width on x64 (the int versions silently truncated the top half).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
