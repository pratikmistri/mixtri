using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Musio_App.Services;

/// <summary>
/// Resolves a previously-remembered top-level window (process name + title)
/// back to a live HWND. Used by the Phase C "restore last selected window"
/// flow described in §5.4 of the spec.
/// </summary>
/// <remarks>
/// Phase A defines this service with a working implementation but does not yet
/// call it from anywhere; it exists so the persistence keys and the matching
/// behaviour can be unit-tested in advance.
/// </remarks>
public static class WindowMatcher
{
    /// <summary>
    /// Enumerate every top-level window and return the first HWND whose
    /// owning process (without the <c>.exe</c> suffix, case-insensitive) is
    /// <paramref name="processName"/> AND whose window text equals
    /// <paramref name="windowTitle"/> exactly (case-sensitive, whitespace
    /// preserved). Returns <c>null</c> if no window matches.
    /// </summary>
    public static IntPtr? FindWindow(string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName) || windowTitle is null)
            return null;

        IntPtr match = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            try
            {
                // Skip invisible windows — they're never user-pickable.
                if (!IsWindowVisible(hwnd)) return true;

                // Skip DWM-cloaked windows. Many UWP/Settings/Start/virtual-
                // desktop windows are visible-but-cloaked and have non-empty
                // titles that can collide with a remembered selection,
                // resolving to a ghost HWND that captures black/garbage.
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                    && cloaked != 0)
                    return true;

                // Title must match exactly (spec §5.4: case-sensitive, exact whitespace).
                var title = GetWindowTitle(hwnd);
                if (!string.Equals(title, windowTitle, StringComparison.Ordinal))
                    return true;

                // Process name must match (case-insensitive, no .exe).
                if (!TryGetProcessName(hwnd, out var owningProcessName))
                    return true;
                if (!string.Equals(owningProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    return true;

                match = hwnd;
                return false; // stop enumeration
            }
            catch
            {
                // Defensive: never let a transient error in a single window
                // abort the whole enumeration.
                return true;
            }
        }, IntPtr.Zero);

        return match == IntPtr.Zero ? null : match;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        int copied = GetWindowText(hwnd, sb, sb.Capacity);
        return copied > 0 ? sb.ToString() : string.Empty;
    }

    private static bool TryGetProcessName(IntPtr hwnd, out string processName)
    {
        processName = string.Empty;
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0)
            return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            // ProcessName is already without the .exe suffix on Windows.
            processName = proc.ProcessName ?? string.Empty;
            return !string.IsNullOrEmpty(processName);
        }
        catch
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const int DWMWA_CLOAKED = 14;
}
