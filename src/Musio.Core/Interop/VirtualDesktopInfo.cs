using System.Runtime.InteropServices;

namespace Musio.Core.Interop;

/// <summary>
/// Shared virtual-desktop metrics P/Invoke entry point and constants, consolidated
/// from identical per-file duplicates in <c>RegionSelectorOverlay</c>,
/// <c>WindowSelectorOverlay</c>, and <c>SelectionHighlight</c>.
/// </summary>
public static class VirtualDesktopInfo
{
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
}
