using System.Runtime.InteropServices;

namespace Mixtri.Core.Services;

[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
    NoRepeat = 0x4000
}

public record HotkeyPressedEventArgs(int HotkeyId);

/// <summary>
/// Registers system-wide hotkeys via a hidden message-only window
/// that processes WM_HOTKEY messages.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public const int ShowMini = 1;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    private IntPtr _hwnd;
    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private readonly HashSet<int> _registeredIds = [];
    private bool _disposed;

    private const int WM_HOTKEY = 0x0312;
    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Initialize the service by subclassing the given window to intercept WM_HOTKEY.
    /// </summary>
    public void Initialize(IntPtr hwnd)
    {
        if (_hwnd != IntPtr.Zero) return;

        _hwnd = hwnd;
        _wndProcDelegate = HotkeyWndProc;

        // SetWindowLongPtr returns 0 on failure (and sets last error), not an exception.
        _originalWndProc = SetWindowLongPtr(
            _hwnd,
            GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        if (_originalWndProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine(
                $"[GlobalHotkeyService] SetWindowLongPtr failed, Win32 error {err}");
            _hwnd = IntPtr.Zero;
            _wndProcDelegate = null;
        }
    }

    public bool RegisterHotkey(int id, ModifierKeys modifiers, int virtualKeyCode)
    {
        if (_hwnd == IntPtr.Zero) return false;

        if (RegisterHotKey(_hwnd, id, (uint)modifiers, (uint)virtualKeyCode))
        {
            _registeredIds.Add(id);
            return true;
        }
        return false;
    }

    public bool UnregisterHotkey(int id)
    {
        if (_hwnd == IntPtr.Zero) return false;

        if (UnregisterHotKey(_hwnd, id))
        {
            _registeredIds.Remove(id);
            return true;
        }
        return false;
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            if (_registeredIds.Contains(hotkeyId))
            {
                try
                {
                    HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(hotkeyId));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyService] HotkeyPressed handler failed: {ex.Message}");
                }
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _registeredIds.ToList())
            UnregisterHotKey(_hwnd, id);
        _registeredIds.Clear();

        if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = IntPtr.Zero;
        }
    }

    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    #endregion
}
