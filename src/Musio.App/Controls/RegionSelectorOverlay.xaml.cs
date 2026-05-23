using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Musio.Core.Capture;
using Musio.Core.Settings;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Musio_App.Controls;

/// <summary>
/// Full-screen overlay that lets the user draw a capture region rectangle.
/// </summary>
public sealed partial class RegionSelectorOverlay : UserControl
{
    private Window? _hostWindow;
    private TaskCompletionSource<CaptureRegion?>? _tcs;
    private readonly RegionSelector _regionSelector;

    private Point _dragStart;
    private bool _isDragging;
    private bool _isResizing;
    private string _resizeHandle = "";
    private Point _resizeStart;

    private double _selX, _selY, _selW, _selH;
    private bool _hasSelection;

    // Screenshot pixel data for edge-snap contrast analysis
    private byte[]? _screenshotPixels;
    private int _screenshotWidth;
    private int _screenshotHeight;

    // Low-level keyboard hook for Escape (XAML focus isn't reliable before user clicks)
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _hookProc;

    // Cached cursors to avoid allocating on every pointer-move
    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Cross;
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> _cursorCache = new();

    private const double HandleSize = 8;
    private const double HandleHitArea = 16;
    private const double MinSelectionSize = 10;
    private const int SnapRadius = 15;
    private const float SnapMinContrast = 12.0f;

    public event EventHandler<CaptureRegion>? RegionSelected;
    public event EventHandler? SelectionCancelled;

    public RegionSelectorOverlay()
    {
        InitializeComponent();
        _regionSelector = new RegionSelector();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var lastRegion = _regionSelector.LoadLastRegion();
        if (lastRegion != null)
        {
            UseLastButton.Visibility = Visibility.Visible;
        }

        UpdateOverlay();
        Focus(FocusState.Programmatic);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverlay();
    }

    /// <summary>
    /// Opens a borderless maximized window with this overlay and waits for the user to
    /// confirm a selection or cancel.
    /// </summary>
    public async Task<CaptureRegion?> ShowAsync()
    {
        _tcs = new TaskCompletionSource<CaptureRegion?>();

        // Minimize Musio so it doesn't appear in the screenshot
        bool didMinimize = false;
        IntPtr mainHwnd = IntPtr.Zero;
        var mainWindow = Musio_App.App.Current.MainAppWindow;
        if (mainWindow is not null)
        {
            mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            ShowWindow(mainHwnd, SW_MINIMIZE);
            didMinimize = true;
            await Task.Delay(300); // let the minimize animation complete
        }

        // Capture the virtual desktop screenshot
        var (screenshotSource, pixels, ssW, ssH) = await CaptureDesktopScreenshotAsync();
        _screenshotPixels = pixels;
        _screenshotWidth = ssW;
        _screenshotHeight = ssH;

        _hostWindow = new Window();
        _hostWindow.Content = this;
        _hostWindow.ExtendsContentIntoTitleBar = true;
        _hostWindow.Title = "Select Region";

        // Hide title bar chrome; do NOT maximize — Maximize() only covers a
        // single monitor. We size the window manually to the full virtual
        // desktop so the overlay covers every display.
        if (_hostWindow.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Position and size the window to span the entire virtual desktop
        // (all monitors) in physical pixels. This matches the dimensions of
        // the screenshot we captured above so the image displays 1:1 with
        // each monitor's physical pixels, even across mixed-DPI setups.
        int vdLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vdTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vdWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vdHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vdWidth > 0 && vdHeight > 0)
        {
            _hostWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                vdLeft, vdTop, vdWidth, vdHeight));
        }

        // Set the screenshot as background
        if (screenshotSource is not null)
            ScreenshotImage.Source = screenshotSource;

        _hostWindow.Closed += (_, _) => _tcs.TrySetResult(null);

        // Install low-level keyboard hook so Escape works even without XAML focus
        _hookProc = EscapeHookCallback;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

        _hostWindow.Activate();

        var result = await _tcs.Task;

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _hookProc = null;

        try { _hostWindow.Close(); }
        catch { /* already closed */ }
        _hostWindow = null;

        // Restore Musio if we minimized it
        if (didMinimize && mainHwnd != IntPtr.Zero)
        {
            ShowWindow(mainHwnd, SW_RESTORE);
            mainWindow?.Activate();
        }

        return result;
    }

    /// <summary>
    /// Captures the full virtual desktop as a SoftwareBitmapSource via GDI BitBlt.
    /// Also returns the raw BGRA pixel data for contrast analysis.
    /// </summary>
    private static async Task<(SoftwareBitmapSource? Source, byte[]? Pixels, int Width, int Height)> CaptureDesktopScreenshotAsync()
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
                return (null, null, 0, 0);

            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            oldObj = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY);

            SelectObject(hdcMem, oldObj);
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

            var pixelData = new byte[width * height * 4];
            GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);

            // Convert BGRA pixel data to SoftwareBitmap
            using var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixelData.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);

            return (source, pixelData, width, height);
        }
        catch
        {
            return (null, null, 0, 0);
        }
        finally
        {
            // Ensure GDI resources are always released
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

    #region Overlay drawing

    private void UpdateOverlay()
    {
        double canvasW = ActualWidth;
        double canvasH = ActualHeight;
        if (canvasW <= 0 || canvasH <= 0)
            return;

        if (!_hasSelection)
        {
            // Full dark overlay — no selection yet
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

            SelectionRect.Visibility = Visibility.Collapsed;
            DimensionLabel.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }

        // Clamp selection to canvas
        double sx = Math.Max(0, _selX);
        double sy = Math.Max(0, _selY);
        double sw = Math.Min(Math.Max(0, _selW), canvasW - sx);
        double sh = Math.Min(Math.Max(0, _selH), canvasH - sy);

        // Top mask
        Canvas.SetLeft(TopMask, 0);
        Canvas.SetTop(TopMask, 0);
        TopMask.Width = canvasW;
        TopMask.Height = sy;

        // Bottom mask
        Canvas.SetLeft(BottomMask, 0);
        Canvas.SetTop(BottomMask, sy + sh);
        BottomMask.Width = canvasW;
        BottomMask.Height = Math.Max(0, canvasH - sy - sh);

        // Left mask
        Canvas.SetLeft(LeftMask, 0);
        Canvas.SetTop(LeftMask, sy);
        LeftMask.Width = sx;
        LeftMask.Height = sh;

        // Right mask
        Canvas.SetLeft(RightMask, sx + sw);
        Canvas.SetTop(RightMask, sy);
        RightMask.Width = Math.Max(0, canvasW - sx - sw);
        RightMask.Height = sh;

        // Selection rectangle
        Canvas.SetLeft(SelectionRect, sx);
        Canvas.SetTop(SelectionRect, sy);
        SelectionRect.Width = sw;
        SelectionRect.Height = sh;
        SelectionRect.Visibility = Visibility.Visible;

        // Dimension label positioned just below the selection
        DimensionText.Text = $"{(int)sw} \u00d7 {(int)sh}";
        Canvas.SetLeft(DimensionLabel, sx);
        Canvas.SetTop(DimensionLabel, sy + sh + 8);
        DimensionLabel.Visibility = Visibility.Visible;

        // Resize handles
        UpdateHandles(sx, sy, sw, sh);
    }

    private void UpdateHandles(double x, double y, double w, double h)
    {
        double hh = HandleSize / 2;
        PositionHandle(HandleTL, x - hh, y - hh);
        PositionHandle(HandleT, x + w / 2 - hh, y - hh);
        PositionHandle(HandleTR, x + w - hh, y - hh);
        PositionHandle(HandleL, x - hh, y + h / 2 - hh);
        PositionHandle(HandleR, x + w - hh, y + h / 2 - hh);
        PositionHandle(HandleBL, x - hh, y + h - hh);
        PositionHandle(HandleB, x + w / 2 - hh, y + h - hh);
        PositionHandle(HandleBR, x + w - hh, y + h - hh);
    }

    private static void PositionHandle(Microsoft.UI.Xaml.Shapes.Rectangle handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
        handle.Visibility = Visibility.Visible;
    }

    private void HideHandles()
    {
        HandleTL.Visibility = Visibility.Collapsed;
        HandleT.Visibility = Visibility.Collapsed;
        HandleTR.Visibility = Visibility.Collapsed;
        HandleL.Visibility = Visibility.Collapsed;
        HandleR.Visibility = Visibility.Collapsed;
        HandleBL.Visibility = Visibility.Collapsed;
        HandleB.Visibility = Visibility.Collapsed;
        HandleBR.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Pointer handling

    private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;

        // Check resize handles first
        if (_hasSelection)
        {
            string handle = HitTestHandle(pos.X, pos.Y);
            if (!string.IsNullOrEmpty(handle))
            {
                _isResizing = true;
                _resizeHandle = handle;
                _resizeStart = pos;
                RootGrid.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }

        // Start new selection
        _isDragging = true;
        _dragStart = pos;
        _selX = pos.X;
        _selY = pos.Y;
        _selW = 0;
        _selH = 0;
        _hasSelection = true;

        RootGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;

        if (_isDragging)
        {
            _selX = Math.Min(_dragStart.X, pos.X);
            _selY = Math.Min(_dragStart.Y, pos.Y);
            _selW = Math.Abs(pos.X - _dragStart.X);
            _selH = Math.Abs(pos.Y - _dragStart.Y);

            UpdateOverlay();
            e.Handled = true;
        }
        else if (_isResizing)
        {
            double dx = pos.X - _resizeStart.X;
            double dy = pos.Y - _resizeStart.Y;
            _resizeStart = pos;

            ApplyResize(_resizeHandle, dx, dy);
            UpdateOverlay();
            e.Handled = true;
        }
        else if (_hasSelection)
        {
            string handle = HitTestHandle(pos.X, pos.Y);
            SetCursorShape(GetCursorForHandle(handle));
        }
        else
        {
            SetCursorShape(InputSystemCursorShape.Cross);
        }
    }

    private void SetCursorShape(InputSystemCursorShape shape)
    {
        if (shape == _currentCursorShape) return;
        _currentCursorShape = shape;
        if (!_cursorCache.TryGetValue(shape, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _cursorCache[shape] = cursor;
        }
        ProtectedCursor = cursor;
    }

    private static InputSystemCursorShape GetCursorForHandle(string handle) => handle switch
    {
        "TL" or "BR" => InputSystemCursorShape.SizeNorthwestSoutheast,
        "TR" or "BL" => InputSystemCursorShape.SizeNortheastSouthwest,
        "T" or "B"   => InputSystemCursorShape.SizeNorthSouth,
        "L" or "R"   => InputSystemCursorShape.SizeWestEast,
        _            => InputSystemCursorShape.Cross,
    };

    private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool shiftHeld = IsShiftHeld();

        if (_isDragging)
        {
            _isDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);

            if (_selW > MinSelectionSize && _selH > MinSelectionSize)
            {
                if (!shiftHeld)
                    SnapSelectionEdges(snapLeft: true, snapTop: true, snapRight: true, snapBottom: true);
                ButtonPanel.Visibility = Visibility.Visible;
            }
            else
            {
                _hasSelection = false;
                ButtonPanel.Visibility = Visibility.Collapsed;
            }

            UpdateOverlay();
            e.Handled = true;
        }
        else if (_isResizing)
        {
            _isResizing = false;
            RootGrid.ReleasePointerCapture(e.Pointer);

            if (!shiftHeld)
            {
                // Only snap the edges that were being resized
                var (sl, st, sr, sb) = _resizeHandle switch
                {
                    "TL" => (true, true, false, false),
                    "T"  => (false, true, false, false),
                    "TR" => (false, true, true, false),
                    "L"  => (true, false, false, false),
                    "R"  => (false, false, true, false),
                    "BL" => (true, false, false, true),
                    "B"  => (false, false, false, true),
                    "BR" => (false, false, true, true),
                    _    => (false, false, false, false),
                };
                SnapSelectionEdges(sl, st, sr, sb);
                UpdateOverlay();
            }

            e.Handled = true;
        }
    }

    private static bool IsShiftHeld()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private string HitTestHandle(double px, double py)
    {
        double cx = _selX, cy = _selY, cw = _selW, ch = _selH;

        if (IsNear(px, py, cx, cy)) return "TL";
        if (IsNear(px, py, cx + cw, cy)) return "TR";
        if (IsNear(px, py, cx, cy + ch)) return "BL";
        if (IsNear(px, py, cx + cw, cy + ch)) return "BR";
        if (IsNear(px, py, cx + cw / 2, cy)) return "T";
        if (IsNear(px, py, cx + cw / 2, cy + ch)) return "B";
        if (IsNear(px, py, cx, cy + ch / 2)) return "L";
        if (IsNear(px, py, cx + cw, cy + ch / 2)) return "R";

        return "";
    }

    private static bool IsNear(double px, double py, double tx, double ty)
    {
        return Math.Abs(px - tx) <= HandleHitArea && Math.Abs(py - ty) <= HandleHitArea;
    }

    private void ApplyResize(string handle, double dx, double dy)
    {
        switch (handle)
        {
            case "TL": _selX += dx; _selY += dy; _selW -= dx; _selH -= dy; break;
            case "T": _selY += dy; _selH -= dy; break;
            case "TR": _selY += dy; _selW += dx; _selH -= dy; break;
            case "L": _selX += dx; _selW -= dx; break;
            case "R": _selW += dx; break;
            case "BL": _selX += dx; _selW -= dx; _selH += dy; break;
            case "B": _selH += dy; break;
            case "BR": _selW += dx; _selH += dy; break;
        }

        if (_selW < MinSelectionSize) _selW = MinSelectionSize;
        if (_selH < MinSelectionSize) _selH = MinSelectionSize;
    }

    #endregion

    #region Edge-snap contrast analysis

    private int OverlayToPixelX(double x) =>
        ActualWidth > 0 ? (int)Math.Round(x * _screenshotWidth / ActualWidth) : 0;
    private int OverlayToPixelY(double y) =>
        ActualHeight > 0 ? (int)Math.Round(y * _screenshotHeight / ActualHeight) : 0;
    private double PixelToOverlayX(int px) =>
        _screenshotWidth > 0 ? px * ActualWidth / _screenshotWidth : 0;
    private double PixelToOverlayY(int py) =>
        _screenshotHeight > 0 ? py * ActualHeight / _screenshotHeight : 0;

    private int GetLuminance(int px, int py)
    {
        if (px < 0 || px >= _screenshotWidth || py < 0 || py >= _screenshotHeight)
            return 0;
        int idx = (py * _screenshotWidth + px) * 4;
        // (B + 2G + R) / 4 — fast approximate brightness
        return (_screenshotPixels![idx] + 2 * _screenshotPixels[idx + 1] + _screenshotPixels[idx + 2]) >> 2;
    }

    /// <summary>
    /// Contrast at row boundary y (between row y-1 and y), scored over columns [x0..x1].
    /// </summary>
    private float ComputeHorizontalContrast(int y, int x0, int x1)
    {
        if (y <= 0 || y >= _screenshotHeight) return 0;
        x0 = Math.Clamp(x0, 0, _screenshotWidth - 1);
        x1 = Math.Clamp(x1, 0, _screenshotWidth - 1);
        if (x0 >= x1) return 0;

        long sum = 0;
        for (int x = x0; x <= x1; x++)
            sum += Math.Abs(GetLuminance(x, y) - GetLuminance(x, y - 1));
        return (float)sum / (x1 - x0 + 1);
    }

    /// <summary>
    /// Contrast at column boundary x (between col x-1 and x), scored over rows [y0..y1].
    /// </summary>
    private float ComputeVerticalContrast(int x, int y0, int y1)
    {
        if (x <= 0 || x >= _screenshotWidth) return 0;
        y0 = Math.Clamp(y0, 0, _screenshotHeight - 1);
        y1 = Math.Clamp(y1, 0, _screenshotHeight - 1);
        if (y0 >= y1) return 0;

        long sum = 0;
        for (int y = y0; y <= y1; y++)
            sum += Math.Abs(GetLuminance(x, y) - GetLuminance(x - 1, y));
        return (float)sum / (y1 - y0 + 1);
    }

    /// <summary>
    /// Searches ±SnapRadius around <paramref name="pos"/> for the strongest contrast edge.
    /// Only snaps if the best candidate has local prominence (1.5× average) and meets min threshold.
    /// </summary>
    private int FindBestSnap(int pos, int spanStart, int spanEnd, bool isHorizontal)
    {
        int limit = isHorizontal ? _screenshotHeight - 1 : _screenshotWidth - 1;
        pos = Math.Clamp(pos, 1, limit);
        int searchMin = Math.Max(1, pos - SnapRadius);
        int searchMax = Math.Min(limit, pos + SnapRadius);

        int best = pos;
        float bestScore = 0;
        float totalScore = 0;
        int count = 0;

        for (int candidate = searchMin; candidate <= searchMax; candidate++)
        {
            float score = isHorizontal
                ? ComputeHorizontalContrast(candidate, spanStart, spanEnd)
                : ComputeVerticalContrast(candidate, spanStart, spanEnd);
            totalScore += score;
            count++;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        float avgScore = count > 0 ? totalScore / count : 0;
        if (bestScore < SnapMinContrast || bestScore < avgScore * 1.5f)
            return pos;

        return best;
    }

    private void SnapSelectionEdges(bool snapLeft, bool snapTop, bool snapRight, bool snapBottom)
    {
        if (_screenshotPixels == null || _screenshotWidth <= 0 || _screenshotHeight <= 0
            || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        int pxLeft = OverlayToPixelX(_selX);
        int pxTop = OverlayToPixelY(_selY);
        int pxRight = OverlayToPixelX(_selX + _selW);
        int pxBottom = OverlayToPixelY(_selY + _selH);

        int origLeft = pxLeft, origTop = pxTop, origRight = pxRight, origBottom = pxBottom;

        // Snap horizontal edges (top/bottom) using the current left..right span
        if (snapTop)
            pxTop = FindBestSnap(pxTop, pxLeft, pxRight, isHorizontal: true);
        if (snapBottom)
            pxBottom = FindBestSnap(pxBottom, pxLeft, pxRight, isHorizontal: true);

        // Snap vertical edges (left/right) using the (possibly snapped) top..bottom span
        if (snapLeft)
            pxLeft = FindBestSnap(pxLeft, pxTop, pxBottom, isHorizontal: false);
        if (snapRight)
            pxRight = FindBestSnap(pxRight, pxTop, pxBottom, isHorizontal: false);

        // Enforce minimum size with separate X/Y scales
        int minPxX = Math.Max(1, (int)(MinSelectionSize * _screenshotWidth / ActualWidth));
        int minPxY = Math.Max(1, (int)(MinSelectionSize * _screenshotHeight / ActualHeight));
        if (pxRight - pxLeft < minPxX) { pxLeft = origLeft; pxRight = origRight; }
        if (pxBottom - pxTop < minPxY) { pxTop = origTop; pxBottom = origBottom; }

        _selX = PixelToOverlayX(pxLeft);
        _selY = PixelToOverlayY(pxTop);
        _selW = PixelToOverlayX(pxRight) - _selX;
        _selH = PixelToOverlayY(pxBottom) - _selY;
    }

    #endregion

    #region Confirmation / cancellation

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void UseLastButton_Click(object sender, RoutedEventArgs e)
    {
        var lastRegion = _regionSelector.LoadLastRegion();
        if (lastRegion is not null)
            CompleteWithRegion(lastRegion);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelSelection();

    private void OnEscapePressed(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CancelSelection();
        args.Handled = true;
    }

    private IntPtr EscapeHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_ESCAPE)
            {
                DispatcherQueue.TryEnqueue(CancelSelection);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void OnEnterPressed(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_hasSelection)
        {
            ConfirmSelection();
            args.Handled = true;
        }
    }

    private void ConfirmSelection()
    {
        if (!_hasSelection)
            return;

        // The captured screenshot spans the entire virtual desktop in
        // physical pixels, and the host window covers the same physical
        // bounds. So converting overlay DIPs → screenshot pixels gives us
        // virtual-desktop physical pixel coordinates (with the virtual
        // desktop origin as zero). Add the virtual desktop origin to obtain
        // absolute screen coordinates.
        double scaleX = ActualWidth > 0 ? _screenshotWidth / ActualWidth : 1.0;
        double scaleY = ActualHeight > 0 ? _screenshotHeight / ActualHeight : 1.0;
        int vdLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vdTop = GetSystemMetrics(SM_YVIRTUALSCREEN);

        int physX = vdLeft + (int)Math.Round(_selX * scaleX);
        int physY = vdTop + (int)Math.Round(_selY * scaleY);
        int physW = (int)Math.Round(_selW * scaleX);
        int physH = (int)Math.Round(_selH * scaleY);

        // Determine which monitor contains the selection centre (using
        // screen-absolute physical pixel coordinates).
        var monitors = _regionSelector.GetMonitors();
        int centerX = physX + physW / 2;
        int centerY = physY + physH / 2;

        var hostMonitor = monitors.FirstOrDefault(m =>
                centerX >= m.X && centerX < m.X + m.Width &&
                centerY >= m.Y && centerY < m.Y + m.Height)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault();

        if (hostMonitor is null)
        {
            CancelSelection();
            return;
        }

        // Clamp the physical rect to the host monitor so the crop never
        // spills onto an adjacent display (which would be invalid for the
        // monitor-relative crop rect used by RecordingSession).
        int monLeft = hostMonitor.X;
        int monTop = hostMonitor.Y;
        int monRight = hostMonitor.X + hostMonitor.Width;
        int monBottom = hostMonitor.Y + hostMonitor.Height;
        int clampedLeft = Math.Max(physX, monLeft);
        int clampedTop = Math.Max(physY, monTop);
        int clampedRight = Math.Min(physX + physW, monRight);
        int clampedBottom = Math.Min(physY + physH, monBottom);
        int clampedW = Math.Max(1, clampedRight - clampedLeft);
        int clampedH = Math.Max(1, clampedBottom - clampedTop);

        // Convert physical pixels → monitor-local DIPs using THIS monitor's
        // DPI scale (mixed-DPI multi-monitor support). RecordingSession
        // multiplies the stored crop rect by the same per-monitor DPI scale
        // to recover the physical crop, so we must store DIPs here.
        float dpiScale = GetDpiScaleForPoint(centerX, centerY);
        if (dpiScale <= 0) dpiScale = 1.0f;

        int dipX = (int)Math.Round((clampedLeft - monLeft) / dpiScale);
        int dipY = (int)Math.Round((clampedTop - monTop) / dpiScale);
        int dipW = Math.Max(1, (int)Math.Round(clampedW / dpiScale));
        int dipH = Math.Max(1, (int)Math.Round(clampedH / dpiScale));

        var region = new CaptureRegion(dipX, dipY, dipW, dipH, hostMonitor.Id);
        _regionSelector.SaveRegion(region);
        CompleteWithRegion(region);
    }

    private static float GetDpiScaleForPoint(int x, int y)
    {
        try
        {
            IntPtr hMonitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return 1.0f;
            int hr = GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            if (hr == 0 && dpiX > 0)
                return dpiX / 96.0f;
        }
        catch { }
        return 1.0f;
    }

    private void CompleteWithRegion(CaptureRegion region)
    {
        RegionSelected?.Invoke(this, region);
        _tcs?.TrySetResult(region);
    }

    private void CancelSelection()
    {
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
        _tcs?.TrySetResult(null);
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

    private const int WH_KEYBOARD_LL = 13;
    private static readonly IntPtr WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    #endregion
}
