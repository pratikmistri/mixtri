using System;
using System.Runtime.InteropServices;

namespace Musio_App.Services;

/// <summary>
/// Which state a <see cref="SelectionHighlight"/> is conveying. The two look the
/// same on screen; the distinction only drives whether the highlight keeps
/// following a moving window.
/// </summary>
public enum HighlightStyle
{
    /// <summary>What *would* be captured — shown while the user is still setting up.</summary>
    Preview,

    /// <summary>What *is* being captured right now.</summary>
    Recording,
}

/// <summary>
/// Marks a screen region or window as the capture target: a rounded, dashed border
/// around it, and a dimmed "smoke" layer over everything else.
/// </summary>
/// <remarks>
/// Both layers are click-through, always-on-top, and excluded from screen capture,
/// so they never land in the recording and never get in the user's way.
/// <para>
/// The look is deliberately identical whether the user is still choosing a target or
/// already recording — the recording overlay says which of those is happening, so
/// restyling the highlight as well would only add noise.
/// </para>
/// <para>
/// Each layer is a single window shaped with <c>SetWindowRgn</c> rather than a set
/// of plain bars — that is what allows the rounded corners and the dashes, and it
/// lets the smoke be one window with a hole in it instead of four abutting strips.
/// </para>
/// <para>All members must be called on the UI thread; these are HWNDs it owns.</para>
/// </remarks>
public sealed class SelectionHighlight : IDisposable
{
    /// <summary>Width of the border ring, in physical pixels.</summary>
    private const int BorderThickness = 3;

    /// <summary>Corner radius of the selection edge, in physical pixels.</summary>
    private const int CornerRadius = 4;

    /// <summary>Length of each dash along the border, in physical pixels.</summary>
    private const int DashLength = 10;

    /// <summary>Gap between dashes, in physical pixels.</summary>
    private const int DashGap = 7;

    /// <summary>Dim applied outside the selection — 0x4D matches the pickers' 30% mask.</summary>
    private const byte SmokeAlpha = 0x4D;

    // COLORREF is 0x00BBGGRR, i.e. byte-reversed from the #RRGGBB used in XAML.
    private const uint BorderColor = 0x00D47800; // #0078D4 accent blue

    private IntPtr _borderHwnd;
    private IntPtr _smokeHwnd;
    private IntPtr _borderBrush;
    private bool _disposed;

    private int _x, _y, _width, _height;

    private static bool _classRegistered;
    private const string ClassName = "MusioSelectionHighlight";

    // The window class stores a marshalled function pointer, so the delegate must
    // stay rooted for as long as the class is registered — otherwise it can be
    // collected and the next message dispatch jumps into freed memory.
    private static WndProcDelegate? _wndProc;

    /// <summary>The window currently being tracked, or <see cref="IntPtr.Zero"/> for a fixed region.</summary>
    public IntPtr TrackedWindow { get; private set; }

    /// <summary>Whether a highlight is currently on screen.</summary>
    public bool IsShown { get; private set; }

    /// <summary>
    /// Shows (or moves) the highlight around a fixed rectangle in physical screen pixels.
    /// </summary>
    public void ShowRect(int x, int y, int width, int height, HighlightStyle style)
    {
        if (_disposed) return;

        TrackedWindow = IntPtr.Zero;
        Render(x, y, width, height);
    }

    /// <summary>
    /// Shows the highlight around <paramref name="hwnd"/> and remembers it, so
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

        Render(x, y, w, h);
    }

    /// <summary>
    /// Re-reads the tracked window's bounds and repositions the highlight. Returns
    /// false when the window has gone away or is no longer visible, so the caller can
    /// stop polling and drop the highlight.
    /// </summary>
    public bool RefreshTrackedWindow(HighlightStyle style)
    {
        if (_disposed || TrackedWindow == IntPtr.Zero) return false;

        if (!TryGetWindowBounds(TrackedWindow, out var x, out var y, out var w, out var h))
        {
            Hide();
            return false;
        }

        // Nothing moved — skip the reshaping churn.
        if (IsShown && x == _x && y == _y && w == _width && h == _height)
            return true;

        Render(x, y, w, h);
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

    private void Render(int x, int y, int width, int height)
    {
        EnsureClassRegistered();
        EnsureBorderBrush();

        _x = x; _y = y; _width = width; _height = height;

        UpdateSmoke(x, y, width, height);
        UpdateBorder(x, y, width, height);

        IsShown = true;
    }

    /// <summary>
    /// Positions the border window over the selection, shaped as a rounded dashed
    /// ring so the middle stays fully interactive and undimmed.
    /// </summary>
    private void UpdateBorder(int x, int y, int width, int height)
    {
        // The ring sits *outside* the selection so it never covers captured content.
        int outerX = x - BorderThickness;
        int outerY = y - BorderThickness;
        int outerW = width + (2 * BorderThickness);
        int outerH = height + (2 * BorderThickness);

        if (_borderHwnd == IntPtr.Zero)
        {
            _borderHwnd = CreateLayerWindow(outerX, outerY, outerW, outerH, _borderBrush, 255);
            if (_borderHwnd == IntPtr.Zero) return;
        }
        else
        {
            SetWindowPos(_borderHwnd, HWND_TOPMOST, outerX, outerY, outerW, outerH,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Region coordinates are window-relative. The outer radius is bumped by the
        // thickness so the ring's inner and outer curves stay concentric.
        var ring = CreateRoundRectRgn(0, 0, outerW + 1, outerH + 1,
            (CornerRadius + BorderThickness) * 2, (CornerRadius + BorderThickness) * 2);
        var inner = CreateRoundRectRgn(
            BorderThickness, BorderThickness,
            BorderThickness + width + 1, BorderThickness + height + 1,
            CornerRadius * 2, CornerRadius * 2);

        CombineRgn(ring, ring, inner, RGN_DIFF);
        DeleteObject(inner);

        PunchDashGaps(ring, outerW, outerH);

        // The window takes ownership of the region — it must not be deleted here.
        SetWindowRgn(_borderHwnd, ring, true);
        InvalidateRect(_borderHwnd, IntPtr.Zero, true);
    }

    /// <summary>
    /// Cuts evenly spaced gaps out of the ring to make it read as a dashed outline,
    /// matching the pickers' dashed selection rectangle.
    /// </summary>
    /// <remarks>
    /// Gaps are kept clear of the corners: cutting into the rounded arcs would
    /// square them off again and lose the rounding.
    /// </remarks>
    private static void PunchDashGaps(IntPtr ring, int outerW, int outerH)
    {
        int margin = CornerRadius + (2 * BorderThickness);
        int stride = DashLength + DashGap;

        for (int x = margin; x < outerW - margin; x += stride)
        {
            int start = Math.Min(x + DashLength, outerW - margin);
            int end = Math.Min(start + DashGap, outerW - margin);
            if (end <= start) break;

            Subtract(ring, start, 0, end, BorderThickness);
            Subtract(ring, start, outerH - BorderThickness, end, outerH);
        }

        for (int y = margin; y < outerH - margin; y += stride)
        {
            int start = Math.Min(y + DashLength, outerH - margin);
            int end = Math.Min(start + DashGap, outerH - margin);
            if (end <= start) break;

            Subtract(ring, 0, start, BorderThickness, end);
            Subtract(ring, outerW - BorderThickness, start, outerW, end);
        }
    }

    private static void Subtract(IntPtr region, int left, int top, int right, int bottom)
    {
        var gap = CreateRectRgn(left, top, right, bottom);
        CombineRgn(region, region, gap, RGN_DIFF);
        DeleteObject(gap);
    }

    /// <summary>
    /// Covers the whole virtual desktop with a dim layer, with the selection (plus
    /// its border ring) punched out.
    /// </summary>
    private void UpdateSmoke(int x, int y, int width, int height)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;

        if (_smokeHwnd == IntPtr.Zero)
        {
            _smokeHwnd = CreateLayerWindow(vx, vy, vw, vh, GetStockObject(BLACK_BRUSH), SmokeAlpha);
            if (_smokeHwnd == IntPtr.Zero) return;
        }
        else
        {
            SetWindowPos(_smokeHwnd, HWND_TOPMOST, vx, vy, vw, vh,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Punch out the selection *and* its ring, so the border isn't dimmed too.
        int holeX = x - vx - BorderThickness;
        int holeY = y - vy - BorderThickness;
        int holeW = width + (2 * BorderThickness);
        int holeH = height + (2 * BorderThickness);

        var full = CreateRectRgn(0, 0, vw, vh);
        var hole = CreateRoundRectRgn(holeX, holeY, holeX + holeW + 1, holeY + holeH + 1,
            (CornerRadius + BorderThickness) * 2, (CornerRadius + BorderThickness) * 2);

        CombineRgn(full, full, hole, RGN_DIFF);
        DeleteObject(hole);

        SetWindowRgn(_smokeHwnd, full, true);
        InvalidateRect(_smokeHwnd, IntPtr.Zero, true);
    }

    /// <summary>
    /// Re-raises <paramref name="hwnd"/> above the highlight layers.
    /// </summary>
    /// <remarks>
    /// The smoke covers the whole desktop and is topmost, so anything of ours that
    /// sits outside the selection — the Mini pill, the recording overlay — would
    /// otherwise be dimmed along with everything else. Both are topmost too, so the
    /// most recently raised wins.
    /// </remarks>
    public void KeepAbove(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero || !IsShown) return;

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Removes the highlight from screen. Safe to call when nothing is shown.</summary>
    public void Hide()
    {
        DestroyLayer(ref _borderHwnd);
        DestroyLayer(ref _smokeHwnd);

        if (_borderBrush != IntPtr.Zero)
        {
            DeleteObject(_borderBrush);
            _borderBrush = IntPtr.Zero;
        }

        TrackedWindow = IntPtr.Zero;
        IsShown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
    }

    private void EnsureBorderBrush()
    {
        if (_borderBrush != IntPtr.Zero) return;

        _borderBrush = CreateSolidBrush(BorderColor);

        if (_borderHwnd != IntPtr.Zero)
        {
            SetClassLongPtr(_borderHwnd, GCLP_HBRBACKGROUND, _borderBrush);
            InvalidateRect(_borderHwnd, IntPtr.Zero, true);
        }
    }

    private static IntPtr CreateLayerWindow(int x, int y, int w, int h, IntPtr brush, byte alpha)
    {
        var hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, "",
            WS_POPUP | WS_VISIBLE,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        // The class has no background brush (it would be shared across colours and
        // across the border/smoke layers), so give each window its own.
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush);

        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

        return hwnd;
    }

    private static void DestroyLayer(ref IntPtr hwnd)
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
            // Deliberately null: the brush is per-window (see CreateLayerWindow),
            // because one class serves the border and the smoke.
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
    private const int RGN_DIFF = 4;
    private const int BLACK_BRUSH = 4;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
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
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);

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
    private static extern int GetSystemMetrics(int nIndex);

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
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

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
