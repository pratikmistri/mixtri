using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Musio_App.Services;

/// <summary>
/// Manages a Win32 system tray icon with context menu for the Musio app.
/// Uses P/Invoke with Shell_NotifyIcon since WinUI 3 has no built-in tray support.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    public event EventHandler? StartRecordingRequested;
    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ShowMiniRequested;
    public event EventHandler? ExitRequested;

    private Window? _mainWindow;
    private IntPtr _messageWindowHwnd;
    private NOTIFYICONDATA _notifyIconData;
    private bool _isVisible;
    private bool _disposed;
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _iconHandle;

    // Menu item IDs
    private const uint IDM_START_RECORDING = 1001;
    private const uint IDM_OPEN = 1002;
    private const uint IDM_EXIT = 1003;
    private const uint IDM_OPEN_MINI = 1004;

    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;
        CreateMessageWindow();
        LoadIcon();
        SetupNotifyIconData();
    }

    public void Show()
    {
        if (_disposed) return;

        if (_isVisible)
        {
            Shell_NotifyIcon(NIM_MODIFY, ref _notifyIconData);
        }
        else
        {
            Shell_NotifyIcon(NIM_ADD, ref _notifyIconData);
            _isVisible = true;
        }
    }

    public void Hide()
    {
        if (_disposed || !_isVisible) return;
        Shell_NotifyIcon(NIM_DELETE, ref _notifyIconData);
        _isVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hide();

        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_messageWindowHwnd != IntPtr.Zero)
        {
            DestroyWindow(_messageWindowHwnd);
            _messageWindowHwnd = IntPtr.Zero;
        }
    }

    private void CreateMessageWindow()
    {
        _wndProcDelegate = TrayWndProc;
        var hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = "MusioTrayMsgWindow"
        };

        RegisterClassW(ref wndClass);

        _messageWindowHwnd = CreateWindowExW(
            0, "MusioTrayMsgWindow", "Musio Tray", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private void LoadIcon()
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            _iconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        }
    }

    private void SetupNotifyIconData()
    {
        _notifyIconData = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _messageWindowHwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _iconHandle,
            szTip = "Musio - Screen Recorder",
            // Initialize remaining string fields to avoid marshalling issues
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

            if (mouseMsg == WM_LBUTTONUP)
            {
                // Left-click is the shortcut back to the compact Mini pill.
                ShowMiniRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        if (msg == WM_COMMAND)
        {
            var menuId = (uint)(wParam.ToInt64() & 0xFFFF);
            switch (menuId)
            {
                case IDM_START_RECORDING:
                    StartRecordingRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case IDM_OPEN_MINI:
                    ShowMiniRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case IDM_OPEN:
                    ShowWindowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case IDM_EXIT:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenu(hMenu, MF_STRING, IDM_START_RECORDING, "Start Recording");
            AppendMenu(hMenu, MF_STRING, IDM_OPEN_MINI, "Open Musio Mini");
            AppendMenu(hMenu, MF_STRING, IDM_OPEN, "Open Musio");
            AppendMenu(hMenu, MF_SEPARATOR, 0, null);
            AppendMenu(hMenu, MF_STRING, IDM_EXIT, "Exit");

            GetCursorPos(out var pt);
            // Required so the menu dismisses when clicking outside it
            SetForegroundWindow(_messageWindowHwnd);
            TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON, pt.X, pt.Y, _messageWindowHwnd, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    #region Win32 Constants

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_COMMAND = 0x0111;

    private const uint MF_STRING = 0x00;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    #endregion

    #region Win32 Structs

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
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
    }

    #endregion

    #region P/Invoke

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    #endregion
}
