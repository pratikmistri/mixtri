using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mixtri.Core.Capture;
using Mixtri.Core.Interop;

namespace Mixtri_App.Controls;

/// <summary>
/// Full-screen overlay that lets the user visually pick a window by clicking on it.
/// Windows are highlighted with a dim-everything-else effect as the cursor hovers.
/// </summary>
public sealed partial class WindowSelectorOverlay : UserControl
{
    private readonly OverlayHost _overlayHost;
    private TaskCompletionSource<WindowInfo?>? _tcs;
    private List<WindowInfo> _windows = new();
    private WindowInfo? _hoveredWindow;

    // Virtual desktop bounds (physical pixels)
    private int _vdLeft, _vdTop, _vdWidth, _vdHeight;

    public WindowSelectorOverlay()
    {
        InitializeComponent();
        _overlayHost = new OverlayHost(this);
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
    /// Minimizes Mixtri, opens a borderless maximized overlay with a desktop screenshot,
    /// and waits for the user to click a window or press Escape.
    /// </summary>
    public async Task<WindowInfo?> ShowAsync()
    {
        // Escape can complete the picker from inside XAML keyboard dispatch.
        // Do not run the awaiting teardown inline and close the host window
        // while that event is still being processed.
        _tcs = new TaskCompletionSource<WindowInfo?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        return await _overlayHost.ShowOverlayAsync<WindowInfo>(
            title: "Select Window",
            shellHideDelayMs: 400,
            includeScreenshotPixels: false,
            awaitResult: () => _tcs.Task);
    }

    /// <summary>
    /// Shared host-window/Escape-hook/shell-hide lifecycle (see
    /// <see cref="OverlayWindowBase"/>). Nested rather than a base class so this
    /// control's own XAML root type (<see cref="UserControl"/>) is untouched.
    /// </summary>
    private sealed class OverlayHost : OverlayWindowBase
    {
        private readonly WindowSelectorOverlay _owner;

        public OverlayHost(WindowSelectorOverlay owner) => _owner = owner;

        protected override UIElement Content => _owner;
        protected override DispatcherQueue DispatcherQueue => _owner.DispatcherQueue;
        protected override void OnEscapePressed() => _owner._tcs?.TrySetResult(null);

        protected override Task OnBeforeScreenshotAsync()
        {
            // Enumerate visible windows (in Z-order) before the overlay appears
            _owner.EnumerateWindows();

            _owner._vdLeft = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_XVIRTUALSCREEN);
            _owner._vdTop = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_YVIRTUALSCREEN);
            _owner._vdWidth = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CXVIRTUALSCREEN);
            _owner._vdHeight = VirtualDesktopInfo.GetSystemMetrics(VirtualDesktopInfo.SM_CYVIRTUALSCREEN);
            return Task.CompletedTask;
        }

        protected override void OnHostWindowReady(OverlayScreenshotResult screenshot)
        {
            if (screenshot.Source is not null)
                _owner.ScreenshotImage.Source = screenshot.Source;

            HostWindow!.Closed += (_, _) => _owner._tcs?.TrySetResult(null);
            HostWindow!.Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                    _owner.DispatcherQueue.TryEnqueue(() => _owner.Focus(FocusState.Programmatic));
            };
        }
    }

    /// <summary>
    /// Enumerates visible, non-cloaked, non-minimized top-level windows,
    /// excluding tool windows and Mixtri's own process. Results are in Z-order
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    #endregion
}
