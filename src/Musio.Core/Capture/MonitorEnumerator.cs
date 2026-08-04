using System.Runtime.InteropServices;
using Musio.Core.Interop;

namespace Musio.Core.Capture;

/// <summary>
/// Enumerates available display monitors via Win32 APIs.
/// </summary>
public static class MonitorEnumerator
{
    public static List<CaptureTarget> GetAllMonitors()
    {
        var monitors = new List<CaptureTarget>();

        MonitorInterop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();

                if (MonitorInterop.GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = info.szDevice;
                    bool isPrimary = (info.dwFlags & MonitorInterop.MONITORINFOF_PRIMARY) != 0;
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
}
