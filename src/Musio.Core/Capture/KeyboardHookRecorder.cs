using System.Diagnostics;
using System.Runtime.InteropServices;
using Musio.Core.Interop;

namespace Musio.Core.Capture;

public record KeyPressEvent(
    long TimestampTicks,
    int VirtualKeyCode,
    string KeyName,
    bool IsDown,
    bool IsCtrl,
    bool IsAlt,
    bool IsShift,
    bool IsWin);

/// <summary>
/// Records keyboard events at high frequency using a low-level keyboard hook
/// (WH_KEYBOARD_LL). A dedicated background thread runs a message pump so
/// the hook works regardless of the calling thread.
/// </summary>
public sealed class KeyboardHookRecorder : IDisposable
{
    #region Win32 interop

    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;

    // Virtual key codes for modifiers
    private const int VK_LSHIFT   = 0xA0;
    private const int VK_RSHIFT   = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU    = 0xA4;
    private const int VK_RMENU    = 0xA5;
    private const int VK_LWIN     = 0x5B;
    private const int VK_RWIN     = 0x5C;
    private const int VK_SHIFT    = 0x10;
    private const int VK_CONTROL  = 0x11;
    private const int VK_MENU     = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookInterop.LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion

    private const int DefaultCapacity = 10_000;

    private nint _hookId;
    private HookInterop.LowLevelKeyboardProc? _hookProc;

    private readonly List<KeyPressEvent> _events = new(DefaultCapacity);
    private readonly object _lock = new();

    private bool _disposed;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);

    // Modifier state tracked across callbacks
    private volatile bool _ctrlDown;
    private volatile bool _altDown;
    private volatile bool _shiftDown;
    private volatile bool _winDown;

    public bool IsRecording { get; private set; }

    // ── Recording lifecycle ─────────────────────────────────────────

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");

        lock (_lock)
        {
            _events.Clear();
        }

        _ctrlDown = false;
        _altDown = false;
        _shiftDown = false;
        _winDown = false;

        _hookReady.Reset();

        _hookThread = new Thread(HookThreadProc)
        {
            Name = "KeyboardHookRecorder",
            IsBackground = true,
        };
        _hookThread.Start();

        if (!_hookReady.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Keyboard hook thread failed to start within 5 seconds.");

        if (_hookId == nint.Zero)
            throw new InvalidOperationException(
                $"Failed to install keyboard hook. Win32 error: {Marshal.GetLastWin32Error()}");

        IsRecording = true;
    }

    public void StopRecording()
    {
        if (!IsRecording) return;

        IsRecording = false;

        HookInterop.PostThreadMessage(_hookThreadId, HookInterop.WM_QUIT, 0, 0);
        _hookThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _hookThread = null;
    }

    public List<KeyPressEvent> GetRecordedEvents()
    {
        lock (_lock)
        {
            return new List<KeyPressEvent>(_events);
        }
    }

    // ── Hook thread ─────────────────────────────────────────────────

    private void HookThreadProc()
    {
        _hookThreadId = HookInterop.GetCurrentThreadId();
        _hookProc = HookCallback;

        nint hMod = GetModuleHandle(null);
        _hookId = SetWindowsHookEx(HookInterop.WH_KEYBOARD_LL, _hookProc, hMod, 0);

        _hookReady.Set();

        if (_hookId == nint.Zero)
            return;

        while (HookInterop.GetMessage(out HookInterop.MSG msg, nint.Zero, 0, 0))
        {
            HookInterop.TranslateMessage(ref msg);
            HookInterop.DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    // ── Hook callback ───────────────────────────────────────────────

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var hookData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                int vk = (int)hookData.vkCode;

                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                UpdateModifierState(vk, isDown);

                long ticks = Stopwatch.GetTimestamp();
                string keyName = MapVirtualKeyToName(vk);

                var evt = new KeyPressEvent(
                    TimestampTicks: ticks,
                    VirtualKeyCode: vk,
                    KeyName: keyName,
                    IsDown: isDown,
                    IsCtrl: _ctrlDown,
                    IsAlt: _altDown,
                    IsShift: _shiftDown,
                    IsWin: _winDown);

                lock (_lock)
                {
                    _events.Add(evt);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHookRecorder] HookCallback error: {ex.Message}");
        }

        return HookInterop.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vk, bool isDown)
    {
        switch (vk)
        {
            case VK_LCONTROL or VK_RCONTROL or VK_CONTROL:
                _ctrlDown = isDown;
                break;
            case VK_LMENU or VK_RMENU or VK_MENU:
                _altDown = isDown;
                break;
            case VK_LSHIFT or VK_RSHIFT or VK_SHIFT:
                _shiftDown = isDown;
                break;
            case VK_LWIN or VK_RWIN:
                _winDown = isDown;
                break;
        }
    }

    /// <summary>
    /// Maps common virtual key codes to human-readable names.
    /// </summary>
    private static string MapVirtualKeyToName(int vk) => vk switch
    {
        // Modifiers
        VK_LCONTROL or VK_RCONTROL or VK_CONTROL => "Ctrl",
        VK_LMENU or VK_RMENU or VK_MENU => "Alt",
        VK_LSHIFT or VK_RSHIFT or VK_SHIFT => "Shift",
        VK_LWIN or VK_RWIN => "Win",

        // Common keys
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        0x14 => "CapsLock",

        // Function keys F1–F12
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",

        // Numbers 0–9
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),

        // Letters A–Z
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),

        // Numpad 0–9
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",

        // Numpad operators
        0x6A => "Num*",
        0x6B => "Num+",
        0x6D => "Num-",
        0x6E => "Num.",
        0x6F => "Num/",

        // OEM keys
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",

        _ => $"VK_{vk:X2}",
    };

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
            StopRecording();

        _hookReady.Dispose();
    }
}
