using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Pages;
using Windows.Graphics;

namespace Musio_App;

public sealed partial class MainWindow : Window
{
    /// <summary>Exposes the navigation frame so App can reach the current page.</summary>
    public Frame ContentFrame => NavFrame;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Minimum window size: 1024×768
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1024 * scale), (int)(768 * scale)));
        SetMinSize(1024, 768, scale);


    }

    private void SetMinSize(int minWidth, int minHeight, double scale)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        // Use subclassing to enforce min size
        _minWidth = (int)(minWidth * scale);
        _minHeight = (int)(minHeight * scale);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate = new WndProcDelegate(WndProc)));
    }

    private int _minWidth;
    private int _minHeight;
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
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = _minWidth;
            info.ptMinTrackSize.Y = _minHeight;
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        // Any OS-initiated session/quiesce signal: tell the OS we can close
        // and start a bounded shutdown so we never exceed the quiesce timeout.
        // This covers logoff, shutdown, MSIX update (ENDSESSION_CLOSEAPP), and
        // Task Manager's "End task" path.
        if (msg == WM_QUERYENDSESSION)
        {
            App.Current.BeginQuiesce();
            return new IntPtr(1);
        }

        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            App.Current.BeginQuiesce();
            return IntPtr.Zero;
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The built-in settings item is disabled in favour of a footer item that matches
        // the rail's icon-over-label layout, so everything arrives here by Tag.
        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "record":
                    NavFrame.Navigate(typeof(RecordingPage));
                    break;
                case "editor":
                    NavFrame.Navigate(typeof(EditorPage));
                    break;
                case "settings":
                    NavFrame.Navigate(typeof(SettingsPage));
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Unknown navigation item tag: {item.Tag}");
                    break;
            }
        }
    }
}
