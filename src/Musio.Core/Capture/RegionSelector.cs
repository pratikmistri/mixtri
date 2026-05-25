using System.Diagnostics;
using System.Runtime.InteropServices;
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

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice;
                    bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

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

        EnumWindows((IntPtr hwnd, IntPtr lParam) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            if (GetWindowTextLength(hwnd) == 0)
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
            int titleLen = GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
            string title = titleLen > 0 ? new string(titleBuffer, 0, titleLen) : string.Empty;

            string processName = string.Empty;
            string? exePath = null;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint processId);
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
        return GetWindowRect(hwnd, out rect);
    }

    #region P/Invoke declarations

    private const uint MONITORINFOF_PRIMARY = 1;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    #endregion
}
