using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Musio.Core.Interop;
using Windows.Graphics.Imaging;

namespace Musio_App.Controls;

/// <summary>
/// Result of <see cref="OverlayScreenshotHelper.CaptureDesktopScreenshotAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="Pixels"/> is only populated when the caller passes
/// <c>includePixels: true</c>. The region picker needs the raw BGRA buffer for
/// its edge-snap contrast analysis; the window picker does not read pixels at
/// all, so it passes <c>includePixels: false</c> and is never handed (nor made
/// to retain) the full-desktop byte[] — the local pixel buffer used internally
/// to build the <see cref="SoftwareBitmap"/> is eligible for GC as soon as this
/// method returns.
/// </remarks>
internal readonly struct OverlayScreenshotResult
{
    public SoftwareBitmapSource? Source { get; init; }
    public byte[]? Pixels { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// Captures the full virtual desktop as a <see cref="SoftwareBitmapSource"/> via
/// GDI <c>BitBlt</c>/<c>GetDIBits</c>. Consolidated from identical per-file copies
/// in <c>RegionSelectorOverlay</c> and <c>WindowSelectorOverlay</c> (see the W4-1
/// UI consolidation report).
/// </summary>
internal static class OverlayScreenshotHelper
{
    public const long MaxScreenshotBytes = 1_073_741_824L; // 1 GB
    private const uint SRCCOPY = 0x00CC0020;

    public static async Task<OverlayScreenshotResult> CaptureDesktopScreenshotAsync(bool includePixels)
    {
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        int width = 0;
        int height = 0;

        try
        {
            int left = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_XVIRTUALSCREEN);
            int top = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_YVIRTUALSCREEN);
            width = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CXVIRTUALSCREEN);
            height = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                return new OverlayScreenshotResult { Width = width, Height = height };
            if (width > 16384 || height > 16384)
                return new OverlayScreenshotResult { Width = width, Height = height };

            long byteCount;
            try
            {
                byteCount = checked((long)width * height * 4L);
            }
            catch (OverflowException)
            {
                return new OverlayScreenshotResult { Width = width, Height = height };
            }

            if (byteCount > MaxScreenshotBytes)
                return new OverlayScreenshotResult { Width = width, Height = height };

            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return new OverlayScreenshotResult { Width = width, Height = height };

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                return new OverlayScreenshotResult { Width = width, Height = height };

            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
                return new OverlayScreenshotResult { Width = width, Height = height };

            oldObj = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                return new OverlayScreenshotResult { Width = width, Height = height };

            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY))
                return new OverlayScreenshotResult { Width = width, Height = height };

            IntPtr restoredObj = NativeMethods.SelectObject(hdcMem, oldObj);
            if (restoredObj == IntPtr.Zero || restoredObj == new IntPtr(-1))
                return new OverlayScreenshotResult { Width = width, Height = height };
            oldObj = IntPtr.Zero;

            // Read pixel data from the HBITMAP
            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };

            var pixelData = new byte[(int)byteCount];
            int scanLines = NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
            if (scanLines != height)
                return new OverlayScreenshotResult { Width = width, Height = height };

            // Convert BGRA pixel data to SoftwareBitmap
            using var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixelData.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);

            return new OverlayScreenshotResult
            {
                Source = source,
                Pixels = includePixels ? pixelData : null,
                Width = width,
                Height = height,
            };
        }
        catch
        {
            return width > 0 && height > 0
                ? new OverlayScreenshotResult { Width = width, Height = height }
                : new OverlayScreenshotResult();
        }
        finally
        {
            // Ensure GDI resources are always released
            if (oldObj != IntPtr.Zero)
                NativeMethods.SelectObject(hdcMem, oldObj);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
