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
using Musio.Core.Interop;
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

    private const long MaxScreenshotBytes = 1_073_741_824L; // 1 GB

    // Virtual desktop bounds (physical pixels)
    private int _vdLeft, _vdTop, _vdWidth, _vdHeight;

    // Low-level keyboard hook for Escape (XAML focus isn't reliable before user clicks)
    private IntPtr _keyboardHook;
    private HookInterop.LowLevelKeyboardProc? _hookProc;

    public WindowSelectorOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        KeyDown += OnKeyDown;
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
        // Escape can complete the picker from inside XAML keyboard dispatch.
        // Do not run the awaiting teardown inline and close the host window
        // while that event is still being processed.
        _tcs = new TaskCompletionSource<WindowInfo?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        bool didMinimize = false;
        IntPtr mainHwnd = IntPtr.Zero;
        var shell = Musio_App.Services.ShellCoordinator.Instance;
        var mainWindow = Musio_App.App.Current.MainAppWindow;

        try
        {
            if (shell is not null)
            {
                // The shell knows whether the Mini pill or the full window is up.
                shell.HideForPicker();
                didMinimize = true;
                await Task.Delay(400);
            }
            else if (mainWindow is not null)
            {
                mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                NativeMethods.ShowWindow(mainHwnd, SW_MINIMIZE);
                didMinimize = true;
                await Task.Delay(400);
            }

            // Enumerate visible windows (in Z-order) before the overlay appears
            EnumerateWindows();

            _vdLeft = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_XVIRTUALSCREEN);
            _vdTop = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_YVIRTUALSCREEN);
            _vdWidth = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CXVIRTUALSCREEN);
            _vdHeight = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CYVIRTUALSCREEN);

            var screenshotSource = await CaptureDesktopScreenshotAsync();

            _hostWindow = new Window();
            _hostWindow.Content = this;
            _hostWindow.ExtendsContentIntoTitleBar = true;
            _hostWindow.Title = "Select Window";

            if (_hostWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            if (_vdWidth > 0 && _vdHeight > 0)
            {
                _hostWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    _vdLeft, _vdTop, _vdWidth, _vdHeight));
            }

            if (screenshotSource is not null)
                ScreenshotImage.Source = screenshotSource;

            _hostWindow.Closed += (_, _) => _tcs.TrySetResult(null);
            _hostWindow.Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                    DispatcherQueue.TryEnqueue(() => Focus(FocusState.Programmatic));
            };

            // Install low-level keyboard hook so Escape works even without XAML focus
            _hookProc = EscapeHookCallback;
            _keyboardHook = SetWindowsHookEx(HookInterop.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

            _hostWindow.Activate();

            return await _tcs.Task;
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            _hookProc = null;

            try { _hostWindow?.Close(); }
            catch { /* already closed */ }
            _hostWindow = null;

            if (didMinimize)
            {
                if (shell is not null)
                {
                    shell.RestoreAfterPicker();
                }
                else if (mainHwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(mainHwnd, SW_RESTORE);
                    mainWindow?.Activate();
                }
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

        NativeMethods.EnumWindows((IntPtr hwnd, IntPtr lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid) return true;

            // Skip DWM-cloaked windows (hidden UWP apps, virtual desktops)
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (cloaked != 0) return true;

            if (!TryGetVisibleBounds(hwnd, out var rect)) return true;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return true;

            var titleBuffer = new char[256];
            int titleLen = NativeMethods.GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
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
            if (NativeMethods.IsWindow(_hoveredWindow.Handle) && NativeMethods.IsWindowVisible(_hoveredWindow.Handle))
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

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _tcs?.TrySetResult(null);
            e.Handled = true;
        }
    }

    private IntPtr EscapeHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_ESCAPE)
            {
                DispatcherQueue.TryEnqueue(() => _tcs?.TrySetResult(null));
                return (IntPtr)1;
            }
        }
        return HookInterop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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
            int left = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_XVIRTUALSCREEN);
            int top = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_YVIRTUALSCREEN);
            int width = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CXVIRTUALSCREEN);
            int height = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                return null;

            long byteCount;
            try
            {
                byteCount = checked((long)width * height * 4L);
            }
            catch (OverflowException)
            {
                return null;
            }

            if (byteCount > MaxScreenshotBytes)
                return null;

            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return null;

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                return null;

            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
                return null;

            oldObj = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                return null;

            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY))
                return null;

            IntPtr restoredObj = NativeMethods.SelectObject(hdcMem, oldObj);
            if (restoredObj == IntPtr.Zero || restoredObj == new IntPtr(-1))
                return null;
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

            var pixelData = new byte[(int)byteCount];
            int scanLines = NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
            if (scanLines != height)
                return null;

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
                NativeMethods.SelectObject(hdcMem, oldObj);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    #endregion

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

    #region P/Invoke

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint SRCCOPY = 0x00CC0020;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private static readonly IntPtr WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookInterop.LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    #endregion
}
