using System.Runtime.InteropServices;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Interop;

namespace Mixtri_App.Helpers;

/// <summary>
/// Win32/DWM window-chrome tweaks that WinUI does not expose.
/// </summary>
/// <remarks>
/// <para>
/// <c>OverlappedPresenter.SetBorderAndTitleBar(false, false)</c> removes the caption but leaves
/// the DWM frame behind, which renders as a 1px light outline around the window — very visible
/// on a dark backdrop. Clearing it needs both a DWM colour attribute AND the removal of the
/// <c>WS_BORDER</c>/<c>WS_DLGFRAME</c> styles; doing only one leaves the line in place.
/// </para>
/// <para>
/// <c>MiniWindow</c> and <c>RecordingOverlayWindow</c> each still carry their own private copy
/// of this sequence. They are shipped and working, so they were deliberately not migrated as
/// part of adding this; new windows should call here, and those two can move over whenever
/// they are next touched.
/// </para>
/// </remarks>
internal static class WindowChrome
{
    private const int GWL_STYLE = -16;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_DLGFRAME = 0x00400000L;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const uint DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    /// <summary>
    /// Strips the DWM border and caption colours and rounds the corners, so only the window's
    /// own backdrop and <c>CornerRadius</c> are visible.
    /// </summary>
    /// <remarks>
    /// Best-effort: every failure here is cosmetic, so it is logged rather than thrown. A
    /// window that shows with a faint outline is still entirely usable.
    /// </remarks>
    public static void ApplyBorderlessRounded(nint hwnd)
    {
        try
        {
            uint colorNone = DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));

            uint captionNone = DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionNone, sizeof(uint));

            uint roundPreference = DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

            // The DWM colour alone does not remove the frame the styles reserve, which is what
            // actually draws the outline.
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style &= ~(WS_BORDER | WS_DLGFRAME);
            SetWindowLongPtr(hwnd, GWL_STYLE, new nint(style));
        }
        catch (Exception ex)
        {
            DiagLog.Write("WindowChrome", $"Borderless styling failed (cosmetic): {ex.Message}");
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int nIndex, nint dwNewLong);
}
