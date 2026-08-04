using System.Diagnostics;
using System.Runtime.InteropServices;
using Musio.Core.Interop;
using Musio.Core.Settings;

namespace Musio.Core.Capture;

public record MonitorInfo(IntPtr Handle, string Id, string Name, int X, int Y, int Width, int Height, bool IsPrimary);

public record WindowInfo(IntPtr Handle, string Title, string ProcessName, int X, int Y, int Width, int Height, string? ExecutablePath = null);

/// <summary>
/// Provides screen region selection helpers: monitor enumeration, window lookup, and region persistence.
/// </summary>
public class RegionSelector
{
    private readonly RegionMemory _regionMemory;

    public CaptureRegion? LastRegion { get; private set; }

    public RegionSelector()
    {
        _regionMemory = new RegionMemory();
    }

    public CaptureRegion? LoadLastRegion()
    {
        LastRegion = _regionMemory.LoadRegion();
        return LastRegion;
    }

    public void SaveRegion(CaptureRegion region)
    {
        _regionMemory.SaveRegion(region.X, region.Y, region.Width, region.Height, region.MonitorId);
        LastRegion = region;
    }

    public List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        MonitorInterop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();

                if (MonitorInterop.GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice;
                    bool isPrimary = (info.dwFlags & MonitorInterop.MONITORINFOF_PRIMARY) != 0;

                    monitors.Add(new MonitorInfo(
                        hMonitor,
                        deviceName,
                        isPrimary ? $"{deviceName} (Primary)" : deviceName,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        isPrimary));
                }

                return true;
            },
            IntPtr.Zero);

        return monitors;
    }

    public WindowInfo? GetWindowAtPoint(int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        var hwnd = WindowFromPoint(point);

        if (hwnd == IntPtr.Zero)
            return null;

        return BuildWindowInfo(hwnd);
    }

    public List<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((IntPtr hwnd, IntPtr lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (NativeMethods.GetWindowTextLength(hwnd) == 0)
                return true;

            var info = BuildWindowInfo(hwnd);
            if (info is not null && info.Width > 0 && info.Height > 0)
                windows.Add(info);

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static WindowInfo? BuildWindowInfo(IntPtr hwnd)
    {
        try
        {
            if (!TryGetVisibleBounds(hwnd, out var rect))
                return null;

            var titleBuffer = new char[256];
            int titleLen = NativeMethods.GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
            string title = titleLen > 0 ? new string(titleBuffer, 0, titleLen) : string.Empty;

            string processName = string.Empty;
            string? exePath = null;
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId != 0)
                {
                    using var process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                    try { exePath = process.MainModule?.FileName; } catch { }
                }
            }
            catch
            {
                // Process may not be accessible
            }

            return new WindowInfo(
                hwnd,
                title,
                processName,
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                exePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the window's visible bounds using DWM extended frame bounds when available,
    /// which excludes the invisible resize border that <see cref="GetWindowRect"/> includes.
    /// This matches what Windows Graphics Capture actually records for the window.
    /// </summary>
    private static bool TryGetVisibleBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect, Marshal.SizeOf<RECT>()) == 0 &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return true;
        }
        return NativeMethods.GetWindowRect(hwnd, out rect);
    }

    #region P/Invoke declarations

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    #endregion
}
