using System.Runtime.InteropServices;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Settings;
using Musio_App.ViewModels;

namespace Musio_App.Services;

/// <summary>
/// Owns the lifetime of a <see cref="RegionBorderHighlight"/> for the current
/// recording session. Encapsulates monitor lookup, DPI scaling and the
/// physical-pixel math so any shell host (mini or full) can show / hide
/// the red region rectangle with a single call.
/// </summary>
internal sealed class RegionBorderManager : IDisposable
{
    private RegionBorderHighlight? _border;

    /// <summary>
    /// Show the red region border for the VM's currently selected region.
    /// No-op unless <paramref name="vm"/> is in CustomRegion mode with a
    /// non-empty selection.
    /// </summary>
    public void ShowIfNeeded(RecordingViewModel vm)
    {
        if (vm.CaptureMode != CaptureMode.CustomRegion
            || vm.SelectedRegion is not CaptureRegion region
            || region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        _border?.Dispose();
        _border = new RegionBorderHighlight();
        float dpiScale = GetRegionMonitorDpiScale(region);
        var (monLeft, monTop) = GetRegionMonitorOrigin(region);

        int px = monLeft + (int)Math.Round(region.X * dpiScale);
        int py = monTop + (int)Math.Round(region.Y * dpiScale);
        int pw = ((int)(region.Width * dpiScale)) & ~1;
        int ph = ((int)(region.Height * dpiScale)) & ~1;
        if (pw < 2) pw = 2;
        if (ph < 2) ph = 2;
        _border.Show(px, py, pw, ph);
    }

    public void Hide()
    {
        _border?.Dispose();
        _border = null;
    }

    public void Dispose() => Hide();

    // ── Monitor / DPI helpers (duplicate-free home for what used to live
    //    in RecordingPage; the helpers were private there and only the
    //    Full shell could reach them).

    private static CaptureTarget? FindMonitorForRegion(CaptureRegion region)
    {
        var monitors = MonitorEnumerator.GetAllMonitors();
        return monitors.FirstOrDefault(m =>
                m.DisplayName == region.MonitorId
                || m.DisplayName.StartsWith(region.MonitorId + " "))
            ?? monitors.FirstOrDefault();
    }

    private static float GetRegionMonitorDpiScale(CaptureRegion region)
    {
        var monitor = FindMonitorForRegion(region);
        if (monitor is not null && monitor.Handle != IntPtr.Zero)
        {
            int hr = GetDpiForMonitor(monitor.Handle, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            if (hr == 0 && dpiX > 0)
                return dpiX / 96.0f;
        }
        return 1.0f;
    }

    private static (int Left, int Top) GetRegionMonitorOrigin(CaptureRegion region)
    {
        var monitor = FindMonitorForRegion(region);
        if (monitor is not null && monitor.Handle != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor.Handle, ref info))
                return (info.rcMonitor.Left, info.rcMonitor.Top);
        }
        return (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
