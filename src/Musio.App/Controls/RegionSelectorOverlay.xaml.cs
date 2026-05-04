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

    private const double HandleSize = 8;
    private const double HandleHitArea = 16;
    private const double MinSelectionSize = 10;

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
        var screenshotSource = await CaptureDesktopScreenshotAsync();

        _hostWindow = new Window();
        _hostWindow.Content = this;
        _hostWindow.ExtendsContentIntoTitleBar = true;
        _hostWindow.Title = "Select Region";

        // Hide title bar chrome and maximize
        if (_hostWindow.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.Maximize();
        }

        // Set the screenshot as background
        if (screenshotSource is not null)
            ScreenshotImage.Source = screenshotSource;

        _hostWindow.Closed += (_, _) => _tcs.TrySetResult(null);
        _hostWindow.Activate();

        var result = await _tcs.Task;

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

            return source;
        }
        catch
        {
            return null;
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
    }

    private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);

            if (_selW > MinSelectionSize && _selH > MinSelectionSize)
            {
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
            e.Handled = true;
        }
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

        // Determine which monitor contains the selection centre
        var monitors = _regionSelector.GetMonitors();
        string monitorId = monitors.FirstOrDefault()?.Id ?? "unknown";
        int centerX = (int)(_selX + _selW / 2);
        int centerY = (int)(_selY + _selH / 2);

        foreach (var mon in monitors)
        {
            if (centerX >= mon.X && centerX < mon.X + mon.Width &&
                centerY >= mon.Y && centerY < mon.Y + mon.Height)
            {
                monitorId = mon.Id;
                break;
            }
        }

        var region = new CaptureRegion((int)_selX, (int)_selY, (int)_selW, (int)_selH, monitorId);
        _regionSelector.SaveRegion(region);
        CompleteWithRegion(region);
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

    #endregion
}
