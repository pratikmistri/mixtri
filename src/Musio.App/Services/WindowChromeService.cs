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
    /// corners, excluded from screen capture.
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
                // Reserved for AppShellWindow (later phase). No-op for now.
                break;
        }
    }

    private static void ApplyMini(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Exclude overlay from screen capture
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

        // Remove DWM-drawn border and caption
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        uint colorCaption = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorCaption, sizeof(uint));

        // Strip WS_BORDER and WS_DLGFRAME from the window style
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Round the window corners at the OS level for a pill shape
        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));
    }

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
}
