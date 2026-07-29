using System;
using System.Runtime.InteropServices;

namespace Musio_App.Services;

/// <summary>
/// Which state a <see cref="SelectionHighlight"/> is conveying.
/// </summary>
public enum HighlightStyle
{
    /// <summary>What *would* be captured — shown while the user is still setting up.</summary>
    Preview,

    /// <summary>What *is* being captured right now.</summary>
    Recording,
}

/// <summary>
/// Draws a coloured border around a screen region or a window using four thin
/// native windows (top, right, bottom, left). The bars are always-on-top,
/// click-through, and excluded from screen capture so they never appear in the
/// recording.
/// </summary>
/// <remarks>
/// Serves two jobs: the persistent <see cref="HighlightStyle.Preview"/> border that
/// shows the user what they have selected before recording, and the
/// <see cref="HighlightStyle.Recording"/> border shown while capture is live.
/// All members must be called on the UI thread — the bars are HWNDs owned by it.
/// </remarks>
public sealed class SelectionHighlight : IDisposable
{
    private const int BorderThickness = 3;

    // COLORREF is 0x00BBGGRR, i.e. byte-reversed from the #RRGGBB used in XAML.
    private const uint PreviewColor = 0x00D47800;   // #0078D4 accent blue
    private const uint RecordingColor = 0x003030FF; // #FF3030 red

    private IntPtr _hTop, _hRight, _hBottom, _hLeft;
    private IntPtr _hBrush;
    private uint _brushColor;
    private bool _disposed;

    private int _x, _y, _width, _height;
    private bool _isShown;

    private static bool _classRegistered;
    private const string ClassName = "MusioSelectionHighlight";

    // The window class stores a marshalled function pointer, so the delegate must
    // stay rooted for as long as the class is registered — otherwise it can be
    // collected and the next message dispatch jumps into freed memory.
    private static WndProcDelegate? _wndProc;

    /// <summary>The window currently being tracked, or <see cref="IntPtr.Zero"/> for a fixed region.</summary>
    public IntPtr TrackedWindow { get; private set; }

    /// <summary>Whether a border is currently on screen.</summary>
    public bool IsShown => _isShown;

    /// <summary>
    /// Shows (or moves) the border around a fixed rectangle in physical screen pixels.
    /// </summary>
    public void ShowRect(int x, int y, int width, int height, HighlightStyle style)
    {
        if (_disposed) return;

        TrackedWindow = IntPtr.Zero;
        Render(x, y, width, height, style);
    }

    /// <summary>
    /// Shows the border around <paramref name="hwnd"/> and remembers it, so
    /// <see cref="RefreshTrackedWindow"/> can follow the window as it moves.
    /// </summary>
    public void ShowWindow(IntPtr hwnd, HighlightStyle style)
    {
        if (_disposed) return;

        TrackedWindow = hwnd;
        if (!TryGetWindowBounds(hwnd, out var x, out var y, out var w, out var h))
        {
            Hide();
            return;
        }

        Render(x, y, w, h, style);
    }

    /// <summary>
    /// Re-reads the tracked window's bounds and repositions the border. Returns false
    /// when the window has gone away or is no longer visible, so the caller can stop
    /// polling and drop the highlight.
    /// </summary>
    public bool RefreshTrackedWindow(HighlightStyle style)
    {
        if (_disposed || TrackedWindow == IntPtr.Zero) return false;

        if (!TryGetWindowBounds(TrackedWindow, out var x, out var y, out var w, out var h))
        {
            Hide();
            return false;
        }

        // Nothing moved — skip the SetWindowPos churn.
        if (_isShown && x == _x && y == _y && w == _width && h == _height)
            return true;

        Render(x, y, w, h, style);
        return true;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        // Prefer the DWM extended frame: GetWindowRect includes the invisible
        // resize border on modern Windows, which would leave a visible gap
        // between the highlight and the window's painted edge.
        RECT rect;
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
            out rect, Marshal.SizeOf<RECT>());
        if (hr != 0 && !GetWindowRect(hwnd, out rect))
            return false;

        x = rect.Left;
        y = rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;

        return width > 0 && height > 0;
    }

    private void Render(int x, int y, int width, int height, HighlightStyle style)
    {
        uint color = style == HighlightStyle.Recording ? RecordingColor : PreviewColor;

        // Recreate only when the colour actually changes; otherwise reuse the bars
        // so moving the highlight doesn't flicker.
        if (_isShown && color != _brushColor)
            DestroyBars();

        EnsureBrush(color);
        EnsureClassRegistered();

        _x = x; _y = y; _width = width; _height = height;

        // The bars sit *outside* the rect so they never cover the content being
        // captured, and the corners are filled by the top and bottom bars.
        PlaceBar(ref _hTop, x - BorderThickness, y - BorderThickness, width + (2 * BorderThickness), BorderThickness);
        PlaceBar(ref _hBottom, x - BorderThickness, y + height, width + (2 * BorderThickness), BorderThickness);
        PlaceBar(ref _hLeft, x - BorderThickness, y, BorderThickness, height);
        PlaceBar(ref _hRight, x + width, y, BorderThickness, height);

        _isShown = true;
    }

    private void PlaceBar(ref IntPtr hwnd, int x, int y, int w, int h)
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = CreateBorderWindow(x, y, w, h);
            return;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Removes the border from screen. Safe to call when nothing is shown.</summary>
    public void Hide()
    {
        DestroyBars();
        TrackedWindow = IntPtr.Zero;
        _isShown = false;
    }

    private void DestroyBars()
    {
        DestroyBorderWindow(ref _hTop);
        DestroyBorderWindow(ref _hRight);
        DestroyBorderWindow(ref _hBottom);
        DestroyBorderWindow(ref _hLeft);

        if (_hBrush != IntPtr.Zero)
        {
            DeleteObject(_hBrush);
            _hBrush = IntPtr.Zero;
            _brushColor = 0;
        }

        _isShown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
    }

    private void EnsureBrush(uint color)
    {
        if (_hBrush != IntPtr.Zero && _brushColor == color) return;

        if (_hBrush != IntPtr.Zero) DeleteObject(_hBrush);
        _hBrush = CreateSolidBrush(color);
        _brushColor = color;
    }

    private IntPtr CreateBorderWindow(int x, int y, int w, int h)
    {
        var hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, "",
            WS_POPUP | WS_VISIBLE,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        // The class has no background brush (it would be shared across colours),
        // so give each window its own and repaint from it.
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, _hBrush);

        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        InvalidateRect(hwnd, IntPtr.Zero, true);

        return hwnd;
    }

    private static void DestroyBorderWindow(ref IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;

        _wndProc = DefWindowProcW;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            // Deliberately null: the brush is per-window (see CreateBorderWindow),
            // because one class serves both the preview and recording colours.
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };

        RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    // ── Win32 constants ─────────────────────────────────────────────
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const byte LWA_ALPHA = 0x02;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int GCLP_HBRBACKGROUND = -10;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // ── P/Invoke ────────────────────────────────────────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, byte dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }
}
