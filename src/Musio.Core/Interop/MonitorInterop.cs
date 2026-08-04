using System.Runtime.InteropServices;

namespace Musio.Core.Interop;

/// <summary>
/// Shared monitor-enumeration and DPI P/Invoke entry points, consolidated from
/// per-file duplicates in <c>MonitorEnumerator</c>, <c>RecordingSession</c>,
/// <c>RegionSelector</c>, <c>RegionSelectorOverlay</c>, <c>SelectionHighlight</c>,
/// <c>ShellCoordinator</c>, <c>MiniWindow</c>, and <c>RecordingOverlayWindow</c>.
/// </summary>
/// <remarks>
/// <see cref="GetMonitorInfo"/> here is the <see cref="MONITORINFOEX"/> overload only.
/// <c>ShellCoordinator</c> has its own private <c>GetMonitorInfo(ref MONITORINFO)</c>
/// overload (no device-name string, no <c>CharSet.Unicode</c>) which is a deliberate,
/// differently-marshalled variant of the same native function — left in place, not
/// consolidated here. See the W2-1 interop consolidation report for details.
/// </remarks>
public static class MonitorInterop
{
    public const uint MONITORINFOF_PRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    public delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hdcMonitor,
        ref RECT lprcMonitor,
        IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern int GetDpiForWindow(IntPtr hwnd);
}
