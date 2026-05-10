using System.Runtime.InteropServices;

namespace Musio_App.Services;

/// <summary>
/// Shows a colored border around a screen region during recording using
/// four thin native windows (top, right, bottom, left). The windows are
/// always-on-top, click-through, and excluded from screen capture.
/// </summary>
public sealed class RegionBorderHighlight : IDisposable
{
    private const int BorderThickness = 3;
    private static readonly uint BorderColor = 0x003030FF; // BGR — red (#FF3030)

    private IntPtr _hTop, _hRight, _hBottom, _hLeft;
    private IntPtr _hBrush;
    private bool _disposed;

    private static bool _classRegistered;
    private const string ClassName = "MusioRegionBorder";

    public void Show(int x, int y, int width, int height)
    {
        if (_disposed) return;

        // Clean up any existing border windows and brush
        Hide();

        EnsureClassRegistered();

        _hBrush = CreateSolidBrush(BorderColor);

        // Top bar
        _hTop = CreateBorderWindow(x - BorderThickness, y - BorderThickness,
            width + 2 * BorderThickness, BorderThickness);
        // Bottom bar
        _hBottom = CreateBorderWindow(x - BorderThickness, y + height,
            width + 2 * BorderThickness, BorderThickness);
        // Left bar
        _hLeft = CreateBorderWindow(x - BorderThickness, y,
            BorderThickness, height);
        // Right bar
        _hRight = CreateBorderWindow(x + width, y,
            BorderThickness, height);
    }

    public void Hide()
    {
        DestroyBorderWindow(ref _hTop);
        DestroyBorderWindow(ref _hRight);
        DestroyBorderWindow(ref _hBottom);
        DestroyBorderWindow(ref _hLeft);

        if (_hBrush != IntPtr.Zero)
        {
            DeleteObject(_hBrush);
            _hBrush = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
    }

    private IntPtr CreateBorderWindow(int x, int y, int w, int h)
    {
        var hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED,
            ClassName, "",
            WS_POPUP | WS_VISIBLE,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            // Make fully opaque (layered + alpha = 255)
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            // Exclude from screen capture
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }

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

    private void EnsureClassRegistered()
    {
        if (_classRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = DefWindowProc,
            hInstance = GetModuleHandle(null),
            hbrBackground = _hBrush != IntPtr.Zero ? _hBrush : CreateSolidBrush(BorderColor),
            lpszClassName = ClassName,
        };

        // Use the brush we'll create — register with a temp brush
        // that gets replaced per-window
        if (_hBrush == IntPtr.Zero)
            _hBrush = CreateSolidBrush(BorderColor);

        wc.hbrBackground = _hBrush;

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
    private const byte LWA_ALPHA = 0x02;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // ── P/Invoke ────────────────────────────────────────────────────
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, byte dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
