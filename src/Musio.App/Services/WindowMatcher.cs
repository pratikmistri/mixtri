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
                // Production path is deliberately lazy/early-exit: visibility
                // and title are cheap, process lookup is comparatively
                // expensive on launch.
                if (!IsWindowVisible(hwnd))
                    return true;

                bool cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloakedValue, sizeof(int)) == 0
                    && cloakedValue != 0;
                if (cloaked)
                    return true;

                var title = GetWindowTitle(hwnd);
                if (!string.Equals(title, windowTitle, StringComparison.Ordinal))
                    return true;

                TryGetProcessName(hwnd, out var owningProcessName);
                var candidate = new WindowSnapshot(hwnd, owningProcessName, title, IsVisible: true, IsCloaked: false);
                var candidateMatch = FindWindow([candidate], processName, windowTitle);
                if (candidateMatch is null)
                    return true;

                match = candidateMatch.Value;
                return false;
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

    internal static IntPtr? FindWindow(IEnumerable<WindowSnapshot> windows, string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName) || windowTitle is null)
            return null;

        foreach (var window in windows)
        {
            if (!window.IsVisible || window.IsCloaked)
                continue;
            if (!string.Equals(window.Title, windowTitle, StringComparison.Ordinal))
                continue;
            if (!string.Equals(NormalizeProcessName(window.ProcessName), NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase))
                continue;
            return window.Handle;
        }

        return null;
    }

    private static string NormalizeProcessName(string value)
        => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

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

internal readonly record struct WindowSnapshot(
    IntPtr Handle,
    string ProcessName,
    string Title,
    bool IsVisible,
    bool IsCloaked);
