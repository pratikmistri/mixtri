using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mixtri.Core.Interop;
using Mixtri_App.Helpers;
using Mixtri_App.Pages;
using Windows.Graphics;

namespace Mixtri_App;

public sealed partial class MainWindow : Window
{
    /// <summary>Exposes the navigation frame so App can reach the current page.</summary>
    public Frame ContentFrame => NavFrame;

    public MainWindow()
    {
        InitializeComponent();

        // Reflects the package manifest, so a locally deployed build reads "Mixtri (dev)".
        Title = AppBranding.DisplayName;
        AppTitleBar.Title = AppBranding.DisplayName;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Preferred (and minimum) size: 1024×768 DIPs — but never larger than the work area
        // of the display the window opens on. On a small or heavily scaled screen (1366×768
        // at 125%, say) the unclamped size ran under the taskbar and off the bottom edge.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int width = (int)(MinWidthDips * scale);
        int height = (int)(MinHeightDips * scale);
        try
        {
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            if (workArea.Width > 0) width = Math.Min(width, workArea.Width);
            if (workArea.Height > 0) height = Math.Min(height, workArea.Height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Work area unavailable: {ex.Message}");
        }
        AppWindow.Resize(new SizeInt32(width, height));

        // Subclassed to enforce the minimum size and to see session-end messages.
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate = new WndProcDelegate(WndProc)));

        NavFrame.Navigated += NavFrame_Navigated;
        NavFrame.NavigationStopped += NavFrame_NavigationStopped;
    }

    private const int MinWidthDips = 1024;
    private const int MinHeightDips = 768;

    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_GETMINMAXINFO = 0x0024;
        const uint WM_QUERYENDSESSION = 0x0011;
        const uint WM_ENDSESSION = 0x0016;

        if (msg == WM_GETMINMAXINFO)
        {
            try
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var (minWidth, minHeight) = ComputeMinTrackSize(hwnd);
                info.ptMinTrackSize.X = minWidth;
                info.ptMinTrackSize.Y = minHeight;
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                // Never let an exception unwind into native code from a window procedure.
                System.Diagnostics.Debug.WriteLine($"[MainWindow] WM_GETMINMAXINFO failed: {ex.Message}");
            }
        }

        // WM_QUERYENDSESSION only ASKS whether the session may end — another app can still
        // veto it, and quiescing here used to exit Mixtri over a shutdown that never
        // happened. Answer yes with no teardown; the only work started is finalizing a live
        // recording, which has to stop either way and benefits from the head start.
        if (msg == WM_QUERYENDSESSION)
        {
            try { App.Current.OnQueryEndSession(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] QueryEndSession: {ex.Message}"); }
            return new IntPtr(1);
        }

        // wParam != 0: the session really is ending (logoff, shutdown, MSIX update via
        // ENDSESSION_CLOSEAPP). Save what can be saved within a bound, then quiesce.
        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            try { App.Current.OnEndSession(); }
            catch { App.Current.BeginQuiesce(); }
            return IntPtr.Zero;
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// The minimum track size for the window's CURRENT monitor and DPI: 1024×768 DIPs, capped
    /// to that monitor's work area so a small display can still fit (and snap) the window.
    /// Computed per message rather than once at startup, so dragging the window to a
    /// differently scaled display gets the right minimum.
    /// </summary>
    private static (int Width, int Height) ComputeMinTrackSize(IntPtr hwnd)
    {
        int dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int minWidth = (int)(MinWidthDips * scale);
        int minHeight = (int)(MinHeightDips * scale);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                int workWidth = info.rcWork.Right - info.rcWork.Left;
                int workHeight = info.rcWork.Bottom - info.rcWork.Top;
                if (workWidth > 0) minWidth = Math.Min(minWidth, workWidth);
                if (workHeight > 0) minHeight = Math.Min(minHeight, workHeight);
            }
        }

        return (minWidth, minHeight);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const int GWLP_WNDPROC = -4;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
            NavFrame.GoBack();
    }

    /// <summary>Set while the rail selection is being moved to match the frame.</summary>
    private bool _syncingNavSelection;

    /// <summary>
    /// Keeps the rail selection in step with the page actually shown. Without this the
    /// title bar's Back button changed the page but left the previous item highlighted —
    /// and clicking that highlighted item then did nothing, since it was already selected.
    /// </summary>
    private void NavFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        => SyncNavSelectionTo(e.SourcePageType);

    /// <summary>
    /// A page refused to be left (the editor cancels navigation while an export runs). The
    /// rail already moved to the target item when it was clicked, so put it back on the page
    /// that is still showing.
    /// </summary>
    private void NavFrame_NavigationStopped(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        => QueueNavSelectionResync();

    /// <summary>
    /// Re-syncs the rail to the frame on the next dispatcher turn. Deferred because the
    /// cancellation is observed from inside NavigationView's own SelectionChanged, and
    /// moving SelectedItem re-entrantly from there is not reliable.
    /// </summary>
    private void QueueNavSelectionResync()
    {
        DispatcherQueue.TryEnqueue(() => SyncNavSelectionTo(NavFrame?.CurrentSourcePageType));
    }

    private void SyncNavSelectionTo(Type? pageType)
    {
        if (NavView is null || pageType is null) return;

        NavigationViewItem? item =
            pageType == typeof(RecordingPage) ? RecordNavItem
            : pageType == typeof(EditorPage) ? EditorNavItem
            : pageType == typeof(OpenProjectsPage) ? ProjectsNavItem
            : pageType == typeof(SettingsPage) ? SettingsNavItem
            : null;

        if (item is null || ReferenceEquals(NavView.SelectedItem, item)) return;

        _syncingNavSelection = true;
        try
        {
            NavView.SelectedItem = item;
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The frame is already showing this page (Back navigation); only the rail moved.
        if (_syncingNavSelection) return;

        // The built-in settings item is disabled in favour of a footer item that matches
        // the rail's icon-over-label layout, so everything arrives here by Tag.
        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "record":
                    NavigateIfNeeded(typeof(RecordingPage));
                    break;
                case "editor":
                    NavigateIfNeeded(typeof(EditorPage));
                    break;
                case "projects":
                    NavigateIfNeeded(typeof(OpenProjectsPage));
                    break;
                case "settings":
                    NavigateIfNeeded(typeof(SettingsPage));
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Unknown navigation item tag: {item.Tag}");
                    break;
            }
        }
    }

    /// <summary>
    /// Navigates unless the frame already shows <paramref name="pageType"/> — which is the
    /// case when the rail was only moved to match a Back navigation, and a second Navigate
    /// would push a duplicate entry onto the back stack.
    /// </summary>
    private void NavigateIfNeeded(Type pageType)
    {
        if (NavFrame.CurrentSourcePageType == pageType) return;

        // False when the current page cancelled the navigation (OnNavigatingFrom); the rail
        // must then follow the page that is still on screen, not the item just clicked.
        if (!NavFrame.Navigate(pageType))
            QueueNavSelectionResync();
    }

    private void CollapseToMiniButton_Click(object sender, RoutedEventArgs e)
        => Services.ShellCoordinator.Instance?.CollapseToMini();

    /// <summary>
    /// Navigates to the editor and moves the rail selection with it, so the shell
    /// doesn't end up showing the editor while "Record" is still highlighted.
    /// </summary>
    public void ShowEditor()
    {
        if (ReferenceEquals(NavView.SelectedItem, EditorNavItem))
        {
            // Already selected, so SelectionChanged won't fire — navigate directly.
            NavFrame.Navigate(typeof(EditorPage));
            return;
        }

        NavView.SelectedItem = EditorNavItem;
    }

    /// <summary>Surfaces a message on the shell-level InfoBar.</summary>
    public void ShowShellMessage(string message, InfoBarSeverity severity)
    {
        if (ShellInfoBar is null) return;
        ShellInfoBar.Severity = severity;
        ShellInfoBar.Title = string.Empty;
        ShellInfoBar.Message = message;
        ShellInfoBar.IsOpen = true;
    }

    /// <summary>Surfaces a recording failure on the shell-level InfoBar.</summary>
    public void ShowRecordingError(string message)
        => ShowShellMessage(message, InfoBarSeverity.Error);
}
