using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Musio.Core.Capture;
using Windows.Graphics.Imaging;

namespace Musio_App.Controls;

/// <summary>
/// Full-screen overlay that lets the user visually pick a window by clicking on it.
/// Windows are highlighted with a dim-everything-else effect as the cursor hovers.
/// </summary>
public sealed partial class WindowSelectorOverlay : UserControl
{
    private Window? _hostWindow;
    private TaskCompletionSource<WindowInfo?>? _tcs;
    private List<WindowInfo> _windows = new();
    private WindowInfo? _hoveredWindow;

    // Virtual desktop bounds (physical pixels)
    private int _vdLeft, _vdTop, _vdWidth, _vdHeight;

    public WindowSelectorOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateOverlay(null);
        Focus(FocusState.Programmatic);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverlay(_hoveredWindow);
    }

    /// <summary>
    /// Minimizes Musio, opens a borderless maximized overlay with a desktop screenshot,
    /// and waits for the user to click a window or press Escape.
    /// </summary>
    public async Task<WindowInfo?> ShowAsync()
    {
        _tcs = new TaskCompletionSource<WindowInfo?>();

        bool didMinimize = false;
        IntPtr mainHwnd = IntPtr.Zero;
        var mainWindow = Musio_App.App.Current.MainAppWindow;

        try
        {
            if (mainWindow is not null)
            {
                mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                ShowWindow(mainHwnd, SW_MINIMIZE);
                didMinimize = true;
                await Task.Delay(400);
            }

            // Enumerate visible windows (in Z-order) before the overlay appears
            EnumerateWindows();

            _vdLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _vdTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _vdWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            _vdHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            var screenshotSource = await CaptureDesktopScreenshotAsync();

            _hostWindow = new Window();
            _hostWindow.Content = this;
            _hostWindow.ExtendsContentIntoTitleBar = true;
            _hostWindow.Title = "Select Window";

            if (_hostWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.Maximize();
            }

            if (screenshotSource is not null)
                ScreenshotImage.Source = screenshotSource;

            _hostWindow.Closed += (_, _) => _tcs.TrySetResult(null);
            _hostWindow.Activate();

            return await _tcs.Task;
        }
        finally
        {
            try { _hostWindow?.Close(); }
            catch { /* already closed */ }
            _hostWindow = null;

            if (didMinimize && mainHwnd != IntPtr.Zero)
            {
                ShowWindow(mainHwnd, SW_RESTORE);
                mainWindow?.Activate();
            }
        }
    }

    /// <summary>
    /// Enumerates visible, non-cloaked, non-minimized top-level windows,
    /// excluding tool windows and Musio's own process. Results are in Z-order
    /// (topmost first) as returned by EnumWindows.
    /// </summary>
    private void EnumerateWindows()
    {
        var windows = new List<WindowInfo>();
        var currentPid = (uint)Process.GetCurrentProcess().Id;

        EnumWindows((IntPtr hwnd, IntPtr lParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsIconic(hwnd)) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid) return true;

            // Skip DWM-cloaked windows (hidden UWP apps, virtual desktops)
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (cloaked != 0) return true;

            if (!GetWindowRect(hwnd, out var rect)) return true;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return true;

            var titleBuffer = new char[256];
            int titleLen = GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
            string title = titleLen > 0 ? new string(titleBuffer, 0, titleLen) : string.Empty;

            string processName = string.Empty;
            try
            {
                if (pid != 0)
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                }
            }
            catch { /* process may not be accessible */ }

            windows.Add(new WindowInfo(hwnd, title, processName, rect.Left, rect.Top, w, h));
            return true;
        }, IntPtr.Zero);

        _windows = windows;
    }

    /// <summary>
    /// Finds the topmost (first in Z-order) window whose rect contains the given
    /// screen-space point.
    /// </summary>
    private WindowInfo? FindWindowAtScreenPoint(double screenX, double screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.X && screenX < w.X + w.Width &&
                screenY >= w.Y && screenY < w.Y + w.Height)
            {
                return w;
            }
        }
        return null;
    }

    /// <summary>
    /// Converts a window's screen rect to canvas (DIP) coordinates.
    /// The desktop screenshot (physical pixels) is stretched to fill the canvas via Stretch="Fill".
    /// </summary>
    private (double x, double y, double w, double h) WindowToCanvas(WindowInfo window)
    {
        double canvasW = ActualWidth;
        double canvasH = ActualHeight;
        if (canvasW <= 0 || canvasH <= 0 || _vdWidth <= 0 || _vdHeight <= 0)
            return (0, 0, 0, 0);

        double scaleX = canvasW / _vdWidth;
        double scaleY = canvasH / _vdHeight;

        return (
            (window.X - _vdLeft) * scaleX,
            (window.Y - _vdTop) * scaleY,
            window.Width * scaleX,
            window.Height * scaleY
        );
    }

    /// <summary>
    /// Converts canvas DIP coordinates to physical screen coordinates.
    /// </summary>
    private (double screenX, double screenY) CanvasToScreen(double canvasX, double canvasY)
    {
        double canvasW = ActualWidth;
        double canvasH = ActualHeight;
        if (canvasW <= 0 || canvasH <= 0)
            return (0, 0);

        return (
            canvasX * ((double)_vdWidth / canvasW) + _vdLeft,
            canvasY * ((double)_vdHeight / canvasH) + _vdTop
        );
    }

    #region Overlay drawing

    private void UpdateOverlay(WindowInfo? highlight)
    {
        double canvasW = ActualWidth;
        double canvasH = ActualHeight;
        if (canvasW <= 0 || canvasH <= 0)
            return;

        if (highlight is null)
        {
            // Full dim overlay — no window highlighted
            Canvas.SetLeft(TopMask, 0);
            Canvas.SetTop(TopMask, 0);
            TopMask.Width = canvasW;
            TopMask.Height = canvasH;

            BottomMask.Width = 0;
            BottomMask.Height = 0;
            LeftMask.Width = 0;
            LeftMask.Height = 0;
            RightMask.Width = 0;
            RightMask.Height = 0;

            HighlightRect.Visibility = Visibility.Collapsed;
            WindowInfoLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var (cx, cy, cw, ch) = WindowToCanvas(highlight);

        // Clamp to canvas bounds
        cx = Math.Max(0, cx);
        cy = Math.Max(0, cy);
        cw = Math.Min(Math.Max(0, cw), canvasW - cx);
        ch = Math.Min(Math.Max(0, ch), canvasH - cy);

        // Top mask
        Canvas.SetLeft(TopMask, 0);
        Canvas.SetTop(TopMask, 0);
        TopMask.Width = canvasW;
        TopMask.Height = cy;

        // Bottom mask
        Canvas.SetLeft(BottomMask, 0);
        Canvas.SetTop(BottomMask, cy + ch);
        BottomMask.Width = canvasW;
        BottomMask.Height = Math.Max(0, canvasH - cy - ch);

        // Left mask
        Canvas.SetLeft(LeftMask, 0);
        Canvas.SetTop(LeftMask, cy);
        LeftMask.Width = cx;
        LeftMask.Height = ch;

        // Right mask
        Canvas.SetLeft(RightMask, cx + cw);
        Canvas.SetTop(RightMask, cy);
        RightMask.Width = Math.Max(0, canvasW - cx - cw);
        RightMask.Height = ch;

        // Highlight border
        Canvas.SetLeft(HighlightRect, cx);
        Canvas.SetTop(HighlightRect, cy);
        HighlightRect.Width = cw;
        HighlightRect.Height = ch;
        HighlightRect.Visibility = Visibility.Visible;

        // Window info label — position below the window (or above if near bottom)
        WindowTitleText.Text = highlight.Title;
        WindowProcessText.Text = highlight.ProcessName;
        double labelY = cy + ch + 8;
        if (labelY > canvasH - 60)
            labelY = Math.Max(0, cy - 50);
        Canvas.SetLeft(WindowInfoLabel, cx);
        Canvas.SetTop(WindowInfoLabel, labelY);
        WindowInfoLabel.Visibility = Visibility.Visible;
    }

    #endregion

    #region Pointer handling

    private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        var (screenX, screenY) = CanvasToScreen(pos.X, pos.Y);
        var window = FindWindowAtScreenPoint(screenX, screenY);

        if (window?.Handle != _hoveredWindow?.Handle)
        {
            _hoveredWindow = window;
            UpdateOverlay(window);
        }

        e.Handled = true;
    }

    private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredWindow is not null)
        {
            // Validate the window is still alive before returning it
            if (IsWindow(_hoveredWindow.Handle) && IsWindowVisible(_hoveredWindow.Handle))
            {
                _tcs?.TrySetResult(_hoveredWindow);
            }
            e.Handled = true;
        }
    }

    #endregion

    private void OnEscapePressed(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _tcs?.TrySetResult(null);
        args.Handled = true;
    }

    #region Desktop Screenshot

    /// <summary>
    /// Captures the full virtual desktop as a SoftwareBitmapSource via GDI BitBlt.
    /// </summary>
    private static async Task<SoftwareBitmapSource?> CaptureDesktopScreenshotAsync()
    {
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                return null;

            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            oldObj = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY);

            SelectObject(hdcMem, oldObj);
            oldObj = IntPtr.Zero;

            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            var pixelData = new byte[width * height * 4];
            GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);

            using var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixelData.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero)
                SelectObject(hdcMem, oldObj);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    #endregion

    #region P/Invoke

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

    #endregion
}
