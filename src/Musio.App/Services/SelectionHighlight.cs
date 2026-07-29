using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Musio_App.Services;

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
    /// <summary>Width of the border ring, in DIPs.</summary>
    private const double BorderThicknessDip = 2;

    /// <summary>Corner radius of the selection edge, in DIPs.</summary>
    private const double CornerRadiusDip = 4;

    /// <summary>Length of each dash along the border, in DIPs.</summary>
    private const double DashLengthDip = 7;

    /// <summary>Gap between dashes, in DIPs.</summary>
    private const double DashGapDip = 5;

    /// <summary>Dim applied outside the selection — 0x4D matches the pickers' 30% mask.</summary>
    private const byte SmokeAlpha = 0x4D;

    // COLORREF is 0x00BBGGRR, i.e. byte-reversed from the #RRGGBB used in XAML.
    private const uint AccentColor = 0x00D47800; // #0078D4 accent blue

    // Physical-pixel geometry for the monitor the selection currently sits on.
    // Recomputed per render so the ring matches the pickers' DIP-based stroke on
    // any display, instead of being hardcoded pixels that shrink as DPI rises.
    private int _thickness = 2;
    private int _radius = 4;
    private int _dashLength = 7;
    private int _dashGap = 5;

    private IntPtr _borderHwnd;
    private IntPtr _smokeHwnd;
    private IntPtr _borderBrush;
    private uint _borderColor;
    private bool _disposed;

    private int _x, _y, _width, _height;

    private static bool _classRegistered;
    private const string ClassName = "MusioSelectionHighlight";

    // The window class stores a marshalled function pointer, so the delegate must
    // stay rooted for as long as the class is registered — otherwise it can be
    // collected and the next message dispatch jumps into freed memory.
    private static WndProcDelegate? _wndProc;

    /// <summary>
    /// Background brush per layer window, consulted by <see cref="HighlightWndProc"/>.
    /// </summary>
    /// <remarks>
    /// The obvious route — <c>SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush)</c> —
    /// does not work here: that value lives on the *class*, not the window, so the
    /// border and the smoke (same class) would fight over one brush and the last
    /// writer would win for both. Painting the background ourselves from a per-HWND
    /// brush is what actually gives the two layers different colours.
    /// </remarks>
    private static readonly Dictionary<IntPtr, IntPtr> _layerBrushes = new();

    /// <summary>Window we must stay below, re-asserted on every render.</summary>
    private IntPtr _keepAboveHwnd;

    /// <summary>The window currently being tracked, or <see cref="IntPtr.Zero"/> for a fixed region.</summary>
    public IntPtr TrackedWindow { get; private set; }

    /// <summary>Whether a highlight is currently on screen.</summary>
    public bool IsShown { get; private set; }

    /// <summary>
    /// Shows (or moves) the highlight around a fixed rectangle in physical screen pixels.
    /// </summary>
    public void ShowRect(int x, int y, int width, int height)
    {
        if (_disposed) return;

        TrackedWindow = IntPtr.Zero;
        Render(x, y, width, height);
    }

    /// <summary>
    /// Shows the highlight around <paramref name="hwnd"/> and remembers it, so
    /// <see cref="RefreshTrackedWindow"/> can follow the window as it moves.
    /// </summary>
    public void ShowWindow(IntPtr hwnd)
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
    public bool RefreshTrackedWindow()
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

        UpdateScale(x, y, width, height);

        _x = x; _y = y; _width = width; _height = height;

        UpdateSmoke(x, y, width, height);
        UpdateBorder(x, y, width, height);

        IsShown = true;

        // Both layers were just re-inserted at the top of the topmost band.
        ApplyKeepAbove();
    }

    /// <summary>
    /// Converts the DIP-based geometry to physical pixels for the monitor the
    /// selection sits on, so the ring is the same visual weight as the pickers'
    /// XAML stroke regardless of that monitor's scaling.
    /// </summary>
    private void UpdateScale(int x, int y, int width, int height)
    {
        double scale = 1.0;

        var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        _thickness = ToPixels(BorderThicknessDip, scale);
        _radius = ToPixels(CornerRadiusDip, scale);
        _dashLength = ToPixels(DashLengthDip, scale);
        _dashGap = ToPixels(DashGapDip, scale);
    }

    private static int ToPixels(double dip, double scale) =>
        Math.Max(1, (int)Math.Round(dip * scale));

    /// <summary>
    /// Positions the border window over the selection, shaped as a rounded dashed
    /// ring so the middle stays fully interactive and undimmed.
    /// </summary>
    private void UpdateBorder(int x, int y, int width, int height)
    {
        // The ring sits *outside* the selection so it never covers captured content.
        int outerX = x - _thickness;
        int outerY = y - _thickness;
        int outerW = width + (2 * _thickness);
        int outerH = height + (2 * _thickness);

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

        // The +1s are load-bearing: CreateRoundRectRgn produces a region one pixel
        // smaller than CreateRectRgn given the same bounds (verified — for
        // (0,0,10,10) the rect region covers 0..9 but the round-rect covers 0..8).
        // Without them the ring falls a pixel short on its right and bottom edges,
        // leaving those two sides visibly misaligned with the selection.
        var ring = CreateRoundRectRgn(0, 0, outerW + 1, outerH + 1,
            (_radius + _thickness) * 2, (_radius + _thickness) * 2);
        var inner = CreateRoundRectRgn(
            _thickness, _thickness,
            _thickness + width + 1, _thickness + height + 1,
            _radius * 2, _radius * 2);

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
    private void PunchDashGaps(IntPtr ring, int outerW, int outerH)
    {
        int margin = _radius + (2 * _thickness);
        int stride = _dashLength + _dashGap;

        for (int x = margin; x < outerW - margin; x += stride)
        {
            int start = Math.Min(x + _dashLength, outerW - margin);
            int end = Math.Min(start + _dashGap, outerW - margin);
            if (end <= start) break;

            Subtract(ring, start, 0, end, _thickness);
            Subtract(ring, start, outerH - _thickness, end, outerH);
        }

        for (int y = margin; y < outerH - margin; y += stride)
        {
            int start = Math.Min(y + _dashLength, outerH - margin);
            int end = Math.Min(start + _dashGap, outerH - margin);
            if (end <= start) break;

            Subtract(ring, 0, start, _thickness, end);
            Subtract(ring, outerW - _thickness, start, outerW, end);
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
        int holeX = x - vx - _thickness;
        int holeY = y - vy - _thickness;
        int holeW = width + (2 * _thickness);
        int holeH = height + (2 * _thickness);

        var full = CreateRectRgn(0, 0, vw, vh);
        // +1 for the same round-rect quirk as the ring, so the hole lines up with
        // the ring's outer edge exactly and leaves no undimmed seam.
        var hole = CreateRoundRectRgn(holeX, holeY, holeX + holeW + 1, holeY + holeH + 1,
            (_radius + _thickness) * 2, (_radius + _thickness) * 2);

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
        if (_disposed) return;

        // Remembered, not just applied once: every render re-inserts both layers at
        // the top of the topmost band, so a tracked window moving would otherwise
        // lift the smoke over the pill and dim it.
        _keepAboveHwnd = hwnd;
        ApplyKeepAbove();
    }

    private void ApplyKeepAbove()
    {
        if (_keepAboveHwnd == IntPtr.Zero || !IsShown) return;
        if (!IsWindow(_keepAboveHwnd)) { _keepAboveHwnd = IntPtr.Zero; return; }

        SetWindowPos(_keepAboveHwnd, HWND_TOPMOST, 0, 0, 0, 0,
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
            _borderColor = 0;
        }

        TrackedWindow = IntPtr.Zero;
        _keepAboveHwnd = IntPtr.Zero;
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
        uint color = ResolveBorderColor();
        if (_borderBrush != IntPtr.Zero && _borderColor == color) return;

        var old = _borderBrush;
        _borderBrush = CreateSolidBrush(color);
        _borderColor = color;

        // Repoint the window at the new brush *before* freeing the old one, or a
        // repaint in between would use a freed GDI handle.
        if (_borderHwnd != IntPtr.Zero)
        {
            _layerBrushes[_borderHwnd] = _borderBrush;
            InvalidateRect(_borderHwnd, IntPtr.Zero, true);
        }

        if (old != IntPtr.Zero) DeleteObject(old);
    }

    /// <summary>
    /// The accent blue normally, or the system highlight colour under High Contrast.
    /// </summary>
    /// <remarks>
    /// The XAML pickers stroke with <c>SystemColorHighlightColor</c> in High
    /// Contrast, so a fixed blue here would both mismatch them and risk being
    /// near-invisible on some schemes.
    /// </remarks>
    private static uint ResolveBorderColor()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        if (SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0)
            && (hc.dwFlags & HCF_HIGHCONTRASTON) != 0)
        {
            // GetSysColor already returns a COLORREF, the same 0x00BBGGRR layout
            // CreateSolidBrush expects.
            return GetSysColor(COLOR_HIGHLIGHT);
        }

        return AccentColor;
    }

    private static IntPtr CreateLayerWindow(int x, int y, int w, int h, IntPtr brush, byte alpha)
    {
        // Created hidden so the brush is registered before the first paint; the
        // class has no background brush of its own.
        var hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, "",
            WS_POPUP,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        _layerBrushes[hwnd] = brush;

        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        return hwnd;
    }

    private static void DestroyLayer(ref IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _layerBrushes.Remove(hwnd);
            DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Fills each layer with its own brush. Everything else falls through to
    /// <c>DefWindowProc</c>.
    /// </summary>
    private static IntPtr HighlightWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_ERASEBKGND
            && _layerBrushes.TryGetValue(hwnd, out var brush)
            && brush != IntPtr.Zero
            && GetClientRect(hwnd, out var rect))
        {
            // wParam is the device context for the erase.
            FillRect(wParam, ref rect, brush);
            return new IntPtr(1);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;

        _wndProc = HighlightWndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            // Deliberately null — see _layerBrushes. A class brush is shared by every
            // window of the class, which is exactly what we must avoid.
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };

        RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    // ── Win32 constants ─────────────────────────────────────────────
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const byte LWA_ALPHA = 0x02;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint WM_ERASEBKGND = 0x0014;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int RGN_DIFF = 4;
    private const int BLACK_BRUSH = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;
    private const int COLOR_HIGHLIGHT = 13;
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

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

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
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

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
