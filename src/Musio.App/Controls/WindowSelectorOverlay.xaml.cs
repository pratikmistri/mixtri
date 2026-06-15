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
using Musio_App.ViewModels;
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
    // Window the user has clicked. Once set, the mask/highlight stays locked
    // around it (mirrors RegionSelectorOverlay where a drawn region persists)
    // until the user clicks a different window or hits Record/Esc.
    private WindowInfo? _lockedWindow;
    // True only after the user clicks a window in THIS picker open. A
    // restored pre-lock (from a prior session) does NOT set this — so hover
    // preview stays enabled until the user actively commits in-session.
    private bool _userClickedThisSession;

    private const long MaxScreenshotBytes = 1_073_741_824L; // 1 GB

    // Virtual desktop bounds (physical pixels)
    private int _vdLeft, _vdTop, _vdWidth, _vdHeight;

    // Low-level keyboard hook for Escape (XAML focus isn't reliable before user clicks)
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _hookProc;

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
        UpdateOverlay(_lockedWindow ?? _hoveredWindow);
        Focus(FocusState.Programmatic);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverlay(_lockedWindow ?? _hoveredWindow);
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
            bool keepShellVisible = mainWindow is AppShellWindow shell
                && shell.CurrentState == Musio_App.Shell.AppShellState.MiniSetup;
            if (mainWindow is not null && !keepShellVisible)
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
                // When the mini shell stays visible, keep picker NON-topmost
                // so the topmost mini shell is naturally above it (avoids
                // unreliable two-topmost z-order races). The fullscreen
                // screenshot already hides everything beneath it.
                presenter.IsAlwaysOnTop = !keepShellVisible;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            Musio_App.Services.WindowChromeService.ApplyOverlayChrome(_hostWindow);

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
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);

            _hostWindow.Activate();
            if (keepShellVisible && mainWindow is not null)
            {
                try
                {
                    var shellHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                    SetWindowPos(shellHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                catch { mainWindow.Activate(); }
            }

            // Pre-lock the previously selected window if it still exists in
            // the freshly enumerated list. Gives the user immediate visual
            // confirmation of "this is what you had selected"; matches the
            // region picker pre-seeding via TryApplyPresetRegion.
            TryRestorePreviousLockedWindow();

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

            if (!TryGetVisibleBounds(hwnd, out var rect)) return true;
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
        // Yellow + thicker stroke when the user has clicked to lock this
        // window (= confirmed). Default theme stroke when only hovering.
        bool isLocked = _lockedWindow is not null
            && ReferenceEquals(highlight, _lockedWindow);
        try
        {
            if (isLocked && Resources.TryGetValue("WindowPickerLockedStrokeBrush", out var lockedBrush)
                && lockedBrush is Microsoft.UI.Xaml.Media.Brush brush)
            {
                HighlightRect.Stroke = brush;
                HighlightRect.StrokeThickness = 3;
            }
            else if (Application.Current.Resources.TryGetValue("OverlayForegroundBrush", out var defaultBrush)
                && defaultBrush is Microsoft.UI.Xaml.Media.Brush db)
            {
                HighlightRect.Stroke = db;
                HighlightRect.StrokeThickness = 1;
            }
            else
            {
                HighlightRect.StrokeThickness = isLocked ? 3 : 1;
            }
        }
        catch { /* best effort styling */ }

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
            // Visual hover preview only when nothing has been committed
            // in THIS picker open. A restored pre-lock (from prior session)
            // shouldn't disable hover — the user hasn't actively picked yet
            // and should be able to preview alternates. Once they click,
            // _userClickedThisSession freezes the highlight on their pick.
            if (!_userClickedThisSession)
                UpdateOverlay(window ?? _lockedWindow);
        }

        e.Handled = true;
    }

    private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredWindow is not null)
        {
            // Validate the window is still alive before locking it in.
            if (IsWindow(_hoveredWindow.Handle) && IsWindowVisible(_hoveredWindow.Handle))
            {
                _lockedWindow = _hoveredWindow;
                _userClickedThisSession = true;
                UpdateOverlay(_lockedWindow);
                // Phase C parity with region picker: click IS the implicit
                // confirm. Push the selection into the shared VM so the
                // toolbar's Expand button disables, the Record button can
                // fire, and a dismiss-and-reopen restores the choice.
                try { RecordingViewModel.Shared.SelectedWindow = _lockedWindow; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WindowSelectorOverlay] commit selection failed: {ex.Message}");
                }
                // Persist for cross-session restore. Previously this only
                // happened on the Confirm path inside CapturePickerService,
                // which Phase C removed — Esc now dismisses the toolbar
                // without going through that path, so we'd lose the choice.
                Musio_App.Services.CapturePickerService.PersistSelectedWindow(_lockedWindow);
            }
            e.Handled = true;
        }
    }

    #endregion

    private void OnEscapePressed(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HandleEscape();
        args.Handled = true;
    }

    /// <summary>
    /// Programmatically cancel the picker — used by hosts when the user
    /// switches the capture-mode tab away from Window while the picker is
    /// still on screen. Mirrors <see cref="RegionSelectorOverlay.Cancel"/>.
    /// </summary>
    public void Cancel() => _tcs?.TrySetResult(null);

    /// <summary>
    /// Commit the currently-locked (or hovered) window selection. Used by
    /// the toolbar's Record button to act as an implicit confirm so the
    /// in-overlay click only needs to lock the smoke around the window.
    /// Returns true when a selection was committed.
    /// </summary>
    public bool TryConfirmCurrent()
    {
        var candidate = _lockedWindow ?? _hoveredWindow;
        if (candidate is null) return false;
        if (!IsWindow(candidate.Handle) || !IsWindowVisible(candidate.Handle)) return false;
        return _tcs?.TrySetResult(candidate) ?? false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HandleEscape();
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
                DispatcherQueue.TryEnqueue(HandleEscape);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Escape: dismiss the picker AND the mini toolbar in one gesture. Since
    /// the toolbar's only useful action from this state is to re-launch the
    /// same picker we just closed, collapsing both back to the tray is the
    /// least surprising behavior. The committed window selection stays in
    /// the VM, so the next summon restores it.
    /// </summary>
    private void HandleEscape()
    {
        Musio_App.Services.CapturePickerService.Shared.RaiseEscapeToDismissRequested();
        _tcs?.TrySetResult(null);
    }

    private void TryRestorePreviousLockedWindow()
    {
        try
        {
            var prev = RecordingViewModel.Shared.SelectedWindow;

            // Fallback to persisted ShellSettings if VM has no selection
            // (e.g. after the app was restarted between picker invocations).
            if (prev is null || prev.Handle == IntPtr.Zero
                || !IsWindow(prev.Handle) || !IsWindowVisible(prev.Handle))
            {
                prev = TryResolveFromPersistedSettings();
                if (prev is null)
                {
                    Debug.WriteLine("[WindowSelectorOverlay] no previous window to restore");
                    return;
                }
            }

            // Prefer the freshly enumerated instance (rect/title may have
            // changed). Match by handle first, then by ProcessName+Title as a
            // robust fallback if the handle is stale.
            WindowInfo? match = null;
            foreach (var w in _windows)
            {
                if (w.Handle == prev.Handle) { match = w; break; }
            }
            if (match is null)
            {
                foreach (var w in _windows)
                {
                    if (string.Equals(w.ProcessName, prev.ProcessName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(w.Title, prev.Title, StringComparison.Ordinal))
                    {
                        match = w;
                        break;
                    }
                }
            }

            // If freshly enumerated match exists, use it (has correct rect).
            // Otherwise fall back to `prev` but REFRESH its rect from the live
            // handle — `prev` from SelectionRestoreService.RestoreWindow has
            // X/Y/W/H = 0 since it was synthesized at app launch without
            // querying the live window. Without this refresh the highlight
            // paints as a 0x0 rect (invisible), which looks identical to "no
            // lock at all" — the exact symptom users hit when their window
            // wasn't in the fresh enum (cloaked / different desktop / etc.).
            if (match is not null)
            {
                _lockedWindow = match;
            }
            else
            {
                var refreshed = prev;
                if (prev.Width <= 0 || prev.Height <= 0)
                {
                    if (IsWindow(prev.Handle) && TryGetVisibleBounds(prev.Handle, out var liveRect))
                    {
                        int lw = liveRect.Right - liveRect.Left;
                        int lh = liveRect.Bottom - liveRect.Top;
                        if (lw > 0 && lh > 0)
                        {
                            refreshed = new WindowInfo(prev.Handle, prev.Title, prev.ProcessName,
                                liveRect.Left, liveRect.Top, lw, lh, prev.ExecutablePath);
                        }
                    }
                }
                _lockedWindow = refreshed;
            }
            // Re-sync the VM in case we resolved from persisted settings or
            // matched a fresh handle — keeps VM and overlay state in lockstep.
            try { RecordingViewModel.Shared.SelectedWindow = _lockedWindow; } catch { }
            UpdateOverlay(_lockedWindow);
            Debug.WriteLine($"[WindowSelectorOverlay] restored locked window: {_lockedWindow?.Title} rect={_lockedWindow?.X},{_lockedWindow?.Y} {_lockedWindow?.Width}x{_lockedWindow?.Height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WindowSelectorOverlay] TryRestorePreviousLockedWindow failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the persisted ``ShellSettings.LastWindowSelection`` tuple into
    /// a live ``WindowInfo`` by enumerating current windows. Returns null if
    /// no persisted selection exists or it can't be re-located.
    /// </summary>
    private WindowInfo? TryResolveFromPersistedSettings()
    {
        try
        {
            var saved = Musio_App.Services.ShellSettings.Instance.LastWindowSelection;
            if (saved is null) return null;
            var procName = saved.Value.ProcessName;
            var title = saved.Value.WindowTitle;

            foreach (var w in _windows)
            {
                if (string.Equals(w.ProcessName, procName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(w.Title, title, StringComparison.Ordinal))
                {
                    return w;
                }
            }
            // Title may have drifted (e.g., document name changed). Fall back
            // to process-name-only as a softer match.
            foreach (var w in _windows)
            {
                if (string.Equals(w.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowSelectorOverlay] TryResolveFromPersistedSettings failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Clears the locked/hovered window selection without dismissing the
    /// picker. Bound to the first Escape press so the user can pick a
    /// different window while the dim/smoke stays on screen, instead of
    /// being kicked back to the toolbar after a mistaken click.
    /// </summary>
    private void ResetSelection()
    {
        _lockedWindow = null;
        _hoveredWindow = null;
        UpdateOverlay(null);
        // Mirror RegionSelectorOverlay.ResetSelection: clear the VM too so
        // the mini toolbar's Expand button re-enables when there is no
        // selection in flight.
        try { RecordingViewModel.Shared.SelectedWindow = null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WindowSelectorOverlay] clear selection failed: {ex.Message}");
        }
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

            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return null;

            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                return null;

            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
                return null;

            oldObj = SelectObject(hdcMem, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                return null;

            if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY))
                return null;

            IntPtr restoredObj = SelectObject(hdcMem, oldObj);
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
            int scanLines = GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
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
        return GetWindowRect(hwnd, out rect);
    }

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
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private const int WH_KEYBOARD_LL = 13;
    private static readonly IntPtr WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

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

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

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

    #endregion
}
