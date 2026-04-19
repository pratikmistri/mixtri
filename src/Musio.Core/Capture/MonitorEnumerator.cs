using System.Runtime.InteropServices;

namespace Musio.Core.Capture;

/// <summary>
/// Enumerates available display monitors via Win32 APIs.
/// </summary>
public static class MonitorEnumerator
{
    public static List<CaptureTarget> GetAllMonitors()
    {
        var monitors = new List<CaptureTarget>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice;
                    bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    string displayName = isPrimary
                        ? $"{deviceName} (Primary)"
                        : deviceName;

                    monitors.Add(new CaptureTarget(
                        CaptureTargetType.Monitor,
                        hMonitor,
                        displayName));
                }

                return true;
            },
            IntPtr.Zero);

        return monitors;
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hdcMonitor,
        ref RECT lprcMonitor,
        IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MONITORINFOEX lpmi);
}
