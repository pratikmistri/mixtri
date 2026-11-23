using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Mixtri.Core.Interop;
using Windows.Graphics.Imaging;

namespace Mixtri_App.Controls;

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

    /// <summary>
    /// Why <see cref="Source"/> is null, or null on success. Callers surface this rather than
    /// presenting an unexplained empty overlay.
    /// </summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Captures the full virtual desktop as a <see cref="SoftwareBitmapSource"/> via
/// GDI <c>BitBlt</c>/<c>GetDIBits</c>. Consolidated from identical per-file copies
/// in <c>RegionSelectorOverlay</c> and <c>WindowSelectorOverlay</c> (see the W4-1
/// UI consolidation report).
/// </summary>
/// <remarks>
/// The GDI capture, the pixel read-back and the alpha fix-up run on the thread pool: on a
/// multi-monitor desktop they move hundreds of megabytes and used to freeze the UI thread
/// for a visible beat before the picker appeared. Only
/// <see cref="SoftwareBitmapSource.SetBitmapAsync"/>, which needs the UI thread, runs there —
/// so this must be called from the UI thread.
/// </remarks>
internal static class OverlayScreenshotHelper
{
    public const long MaxScreenshotBytes = 1_073_741_824L; // 1 GB
    private const uint SRCCOPY = 0x00CC0020;

    public static async Task<OverlayScreenshotResult> CaptureDesktopScreenshotAsync(bool includePixels)
    {
        var capture = await Task.Run(CapturePixels);

        if (capture.Pixels is null)
            return Fail(capture.Width, capture.Height, capture.FailureReason ?? "unknown failure");

        try
        {
            using var softwareBitmap = await Task.Run(() =>
            {
                var bitmap = new SoftwareBitmap(
                    BitmapPixelFormat.Bgra8, capture.Width, capture.Height, BitmapAlphaMode.Premultiplied);
                bitmap.CopyFromBuffer(capture.Pixels.AsBuffer());
                return bitmap;
            });

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);

            return new OverlayScreenshotResult
            {
                Source = source,
                Pixels = includePixels ? capture.Pixels : null,
                Width = capture.Width,
                Height = capture.Height,
            };
        }
        catch (Exception ex)
        {
            return Fail(capture.Width, capture.Height, $"bitmap conversion failed: {ex.Message}");
        }
    }

    private static OverlayScreenshotResult Fail(int width, int height, string reason)
    {
        Mixtri.Core.Diagnostics.DiagLog.Write(
            "Overlay", $"Desktop screenshot unavailable ({width}x{height}): {reason}");

        return new OverlayScreenshotResult
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            FailureReason = reason,
        };
    }

    private readonly record struct PixelCapture(byte[]? Pixels, int Width, int Height, string? FailureReason);

    /// <summary>
    /// Thread-pool half: GDI capture of the virtual desktop into an opaque BGRA buffer. All
    /// GDI handles are created and released on the calling thread.
    /// </summary>
    private static PixelCapture CapturePixels()
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
                return new PixelCapture(null, width, height, "virtual desktop has no size");
            if (width > 16384 || height > 16384)
                return new PixelCapture(null, width, height, "virtual desktop exceeds 16384px");

            long byteCount;
            try
            {
                byteCount = checked((long)width * height * 4L);
            }
            catch (OverflowException)
            {
                return new PixelCapture(null, width, height, "pixel buffer size overflows");
            }

            if (byteCount > MaxScreenshotBytes)
                return new PixelCapture(null, width, height, "pixel buffer exceeds 1 GB");

            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return new PixelCapture(null, width, height, "GetDC failed");

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                return new PixelCapture(null, width, height, "CreateCompatibleDC failed");

            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
                return new PixelCapture(null, width, height, "CreateCompatibleBitmap failed");

            oldObj = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                return new PixelCapture(null, width, height, "SelectObject failed");

            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY))
                return new PixelCapture(null, width, height, "BitBlt failed");

            IntPtr restoredObj = NativeMethods.SelectObject(hdcMem, oldObj);
            if (restoredObj == IntPtr.Zero || restoredObj == new IntPtr(-1))
                return new PixelCapture(null, width, height, "SelectObject restore failed");
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
                return new PixelCapture(null, width, height, $"GetDIBits returned {scanLines} of {height} lines");

            // GetDIBits with BI_RGB fills the 4th byte of each pixel with an UNDEFINED value
            // (commonly 0). SoftwareBitmapSource requires Bgra8/Premultiplied, so an alpha of 0
            // renders the whole screenshot invisible and the picker shows an empty overlay.
            // Force opaque alpha; the BGR channels are already straight/opaque values, so with
            // A = 255 the premultiplied interpretation is exact.
            var span = pixelData.AsSpan();
            for (int i = 3; i < span.Length; i += 4)
                span[i] = 0xFF;

            return new PixelCapture(pixelData, width, height, null);
        }
        catch (Exception ex)
        {
            return new PixelCapture(null, width, height, $"capture threw: {ex.Message}");
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
