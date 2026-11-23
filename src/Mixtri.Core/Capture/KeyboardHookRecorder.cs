using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixtri.Core.Interop;

namespace Mixtri.Core.Capture;

public record KeyPressEvent(
    long TimestampTicks,
    int VirtualKeyCode,
    string KeyName,
    bool IsDown,
    bool IsCtrl,
    bool IsAlt,
    bool IsShift,
    bool IsWin,
    TextInputFocus? TextFocus = null);

/// <summary>
/// Best-effort screen-space location of the focused native text control and insertion caret
/// at one keyboard event. Coordinates are physical pixels for this PerMonitorV2 process.
/// </summary>
public readonly record struct TextInputFocus(
    int CaretX,
    int CaretY,
    int BoundsLeft,
    int BoundsTop,
    int BoundsRight,
    int BoundsBottom);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO threadInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out RECT rect);

    private const int GWL_STYLE = -16;
    private const int ES_PASSWORD = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    #endregion

    private const int DefaultCapacity = 10_000;

    private nint _hookId;
    private HookInterop.LowLevelKeyboardProc? _hookProc;

    private readonly List<KeyPressEvent> _events = new(DefaultCapacity);
    private readonly object _lock = new();

    private bool _disposed;
    private volatile bool _paused;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _abortHookThread;
    private readonly ManualResetEventSlim _hookReady = new(false);

    // Modifier state tracked across callbacks
    private volatile bool _ctrlDown;
    private volatile bool _altDown;
    private volatile bool _shiftDown;
    private volatile bool _winDown;

    // Hook-thread-only cache: whether the last focused HWND is an edit-class control, so
    // the class-name lookup runs once per focus change rather than once per key.
    private readonly char[] _classNameBuffer = new char[64];
    private nint _lastFocusHwnd;
    private bool _lastFocusIsEditClass;

    /// <summary>
    /// Key name recorded in place of a key typed into a password field. The event keeps its
    /// timing so typing detection still sees activity, but never which key it was.
    /// </summary>
    internal const string RedactedKeyName = "\u2022";

    /// <summary>
    /// Virtual-key code recorded for a redacted key. 'A' is used because it is a plain
    /// letter: typing-activity detection counts it, and it carries no information.
    /// </summary>
    internal const int RedactedVirtualKeyCode = 0x41;

    public bool IsRecording { get; internal set; }

    /// <summary>
    /// When false, plain character keys are never recorded (not even as redacted placeholders):
    /// only modifier keys, shortcut combos and non-text keys survive. Set before
    /// <see cref="StartRecording"/>. Defaults to true (record everything).
    /// </summary>
    public bool RecordTypedText { get; set; } = true;

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

        // No hook thread is running yet, so this cannot race the callback.
        _typedTextFilter.Reset();
        _typedTextFilterResetPending = false;
        _ctrlDown = false;
        _altDown = false;
        _shiftDown = false;
        _winDown = false;
        _paused = false;

        _hookReady.Reset();
        _abortHookThread = false;

        // A previous session's id must never be the target of an abort for this one.
        _hookThreadId = 0;
        _hookThread = new Thread(HookThreadProc)
        {
            Name = "KeyboardHookRecorder",
            IsBackground = true,
        };
        _hookThread.Start();

        if (!_hookReady.Wait(TimeSpan.FromSeconds(5)))
        {
            AbortHookThread();
            throw new InvalidOperationException("Keyboard hook thread failed to start within 5 seconds.");
        }

        if (_hookId == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            AbortHookThread();
            throw new InvalidOperationException($"Failed to install keyboard hook. Win32 error: {error}");
        }

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

    /// <summary>
    /// Tears down a hook thread that may still be starting up or running. Used when a start
    /// fails (e.g. the install timed out) and from Dispose, so a late-installing thread can
    /// never keep its low-level hook and message loop alive forever.
    /// </summary>
    private void AbortHookThread()
    {
        _abortHookThread = true;

        var thread = _hookThread;
        _hookThread = null;
        if (thread is null) return;

        // The thread's message queue may not exist yet, so keep re-posting WM_QUIT until it
        // exits; the _abortHookThread check in HookThreadProc covers the not-yet-installed case.
        var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
        while (thread.IsAlive && Stopwatch.GetTimestamp() < deadline)
        {
            uint id = Volatile.Read(ref _hookThreadId);
            if (id != 0)
                HookInterop.PostThreadMessage(id, HookInterop.WM_QUIT, 0, 0);
            if (thread.Join(TimeSpan.FromMilliseconds(100)))
                break;
        }
    }

    public void PauseRecording()
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording.");
        _paused = true;
        // Releases arriving while paused are never seen, so press decisions taken before the
        // pause go stale; the hook thread drops them on its next event.
        _typedTextFilterResetPending = true;
    }

    public void ResumeRecording()
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording.");

        // Modifier keys can change while collection is paused. Rehydrate their current
        // physical state before accepting resumed events.
        _ctrlDown = IsKeyDown(VK_CONTROL);
        _altDown = IsKeyDown(VK_MENU);
        _shiftDown = IsKeyDown(VK_SHIFT);
        _winDown = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
        _typedTextFilterResetPending = true;
        _paused = false;
    }

    public List<KeyPressEvent> GetRecordedEvents()
    {
        lock (_lock)
        {
            return new List<KeyPressEvent>(_events);
        }
    }

    internal bool TryRecordEvent(KeyPressEvent evt)
    {
        if (!IsRecording || _paused)
            return false;

        lock (_lock)
        {
            _events.Add(evt);
        }

        return true;
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

        // Start was abandoned (timeout) before the hook came up: do not pump messages.
        if (_abortHookThread)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
            return;
        }

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
            if (nCode >= 0 && IsRecording && !_paused)
            {
                // Pause/resume asks for this; only the hook thread ever touches the filter.
                if (_typedTextFilterResetPending)
                {
                    _typedTextFilterResetPending = false;
                    _typedTextFilter.Reset();
                }

                var hookData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                int vk = (int)hookData.vkCode;

                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                UpdateModifierState(vk, isDown);

                // Filter on the press; a release is recorded exactly when its press was, so
                // presses and releases stay balanced (Ctrl+C released after Ctrl would
                // otherwise leave a recorded "C" down with no up, and a dropped "c" press
                // would otherwise surface as a lone release).
                bool record = RecordTypedText
                    || _typedTextFilter.ShouldRecord(
                        vk, isDown, _ctrlDown, _altDown, _winDown,
                        isExtended: (hookData.flags & LLKHF_EXTENDED) != 0);

                if (!record)
                    return HookInterop.CallNextHookEx(_hookId, nCode, wParam, lParam);

                long ticks = Stopwatch.GetTimestamp();
                string keyName = MapVirtualKeyToName(vk);

                // One cheap GUI-thread query per key serves both the caret lookup and the
                // password check. Low-level hooks have a hard timeout, so nothing here may
                // block on another process (no SendMessage, no UI Automation).
                bool haveGuiInfo = TryGetForegroundGuiThreadInfo(out var guiInfo);

                var evt = new KeyPressEvent(
                    TimestampTicks: ticks,
                    VirtualKeyCode: vk,
                    KeyName: keyName,
                    IsDown: isDown,
                    IsCtrl: _ctrlDown,
                    IsAlt: _altDown,
                    IsShift: _shiftDown,
                    IsWin: _winDown,
                    TextFocus: isDown && haveGuiInfo ? TryGetTextInputFocus(guiInfo) : null);

                KeyPressEvent? recorded = evt;
                if (haveGuiInfo && IsPasswordControl(guiInfo.hwndFocus))
                    recorded = RedactSecureKey(evt);

                if (recorded is not null)
                    TryRecordEvent(recorded);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHookRecorder] HookCallback error: {ex.Message}");
        }

        return HookInterop.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool TryGetForegroundGuiThreadInfo(out GUITHREADINFO info)
    {
        info = default;

        nint foreground = GetForegroundWindow();
        if (foreground == nint.Zero) return false;

        uint threadId = GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0) return false;

        info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(threadId, ref info);
    }

    /// <summary>
    /// Best-effort: true when <paramref name="hwndFocus"/> is a classic Win32 edit control
    /// (Edit, RichEdit, and look-alikes whose class name contains "Edit") with the
    /// <c>ES_PASSWORD</c> style. Controls without their own HWND — browsers, WinUI/WPF,
    /// UWP — cannot be detected this way and are recorded as before.
    /// </summary>
    private bool IsPasswordControl(nint hwndFocus)
    {
        if (hwndFocus == nint.Zero)
            return false;

        try
        {
            if (hwndFocus != _lastFocusHwnd)
            {
                _lastFocusHwnd = hwndFocus;
                int length = GetClassName(hwndFocus, _classNameBuffer, _classNameBuffer.Length);
                _lastFocusIsEditClass = length > 0
                    && IsEditClassName(new string(_classNameBuffer, 0, length));
            }

            // Re-read the style every time: "show password" toggles flip ES_PASSWORD live.
            return _lastFocusIsEditClass
                && (GetWindowLong(hwndFocus, GWL_STYLE) & ES_PASSWORD) != 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHookRecorder] Password check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// ES_PASSWORD (0x20) only means "password" on edit controls; on other classes the same
    /// bit is an unrelated style, so the class must be checked first.
    /// </summary>
    internal static bool IsEditClassName(string? className)
        => !string.IsNullOrEmpty(className)
           && className.Contains("Edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a key that would put a character into a text field: letters, digits, space,
    /// numpad and OEM punctuation, pressed without Ctrl/Alt/Win (which make it a shortcut).
    /// </summary>
    internal static bool IsCharacterKey(int vk, bool isCtrl, bool isAlt, bool isWin)
    {
        if (isCtrl || isAlt || isWin)
            return false;

        return vk == 0x20
            || vk is >= 0x30 and <= 0x39
            || vk is >= 0x41 and <= 0x5A
            || vk is >= 0x60 and <= 0x6F
            || vk is >= 0xBA and <= 0xC0
            || vk is 0xC1 or 0xC2            // ABNT_C1 / ABNT_C2 (Brazilian layout: '/' and '.')
            || vk is >= 0xDB and <= 0xDF
            || vk == 0xE2; // OEM_102 (extra key on ISO layouts)
    }

    // Typed-text exclusion state (balanced press/release, Alt+hex sequences). Touched only on
    // the hook thread; other threads ask for a reset through the volatile flag.
    private readonly TypedTextFilter _typedTextFilter = new();
    private volatile bool _typedTextFilterResetPending;
    private const uint LLKHF_EXTENDED = 0x01;

    /// <summary>
    /// Filtering rule for the "Record typed text" setting. With <paramref name="recordTypedText"/>
    /// true everything is recorded. With it false, any key that can insert text is dropped:
    /// plain character keys (letters, digits, space, numpad, OEM punctuation; Shift does not
    /// make one a shortcut), AltGr chords (Ctrl+Alt + character) and Alt+numpad codes.
    /// Modifier keys, Ctrl/Alt/Win shortcut combos, and non-text keys (function, navigation,
    /// Enter, Tab, Esc, Backspace, Delete) are kept.
    /// </summary>
    internal static bool ShouldRecordKey(
        int vk, bool isCtrl, bool isAlt, bool isWin, bool recordTypedText, bool isExtended = false)
    {
        if (recordTypedText)
            return true;

        if (IsCharacterKey(vk, isCtrl, isAlt, isWin))
            return false;

        bool altGrChord = isCtrl && isAlt && !isWin
            && IsCharacterKey(vk, isCtrl: false, isAlt: false, isWin: false);
        // Only KEYPAD-originated codes enter Alt+numpad characters. The dedicated navigation
        // cluster reports the same virtual keys (Left, Home, ...) but with the extended flag,
        // and Alt+Left / Alt+Home there are ordinary shortcuts.
        bool altNumpadCode = isAlt && !isCtrl && !isWin && !isExtended && IsAltCodeKey(vk);

        return !altGrChord && !altNumpadCode;
    }

    /// <summary>
    /// Strips the identity of a key typed into a secure field. Character keys become a fixed
    /// placeholder with no modifiers, and Shift presses are dropped (either would reveal
    /// case); navigation, editing and shortcut keys reveal nothing about the secret and are
    /// kept so the timeline still makes sense.
    /// </summary>
    /// <returns>The event to record, or null when it must not be recorded at all.</returns>
    internal static KeyPressEvent? RedactSecureKey(KeyPressEvent evt)
    {
        if (evt.VirtualKeyCode is VK_LSHIFT or VK_RSHIFT or VK_SHIFT)
            return null;

        // AltGr arrives as Ctrl+Alt but inserts a character (e.g. AltGr+Q = '@' on German
        // layouts), so a Ctrl+Alt chord on a character key is conservatively treated as text.
        bool altGrChord = evt.IsCtrl && evt.IsAlt && !evt.IsWin
            && IsCharacterKey(evt.VirtualKeyCode, isCtrl: false, isAlt: false, isWin: false);

        // Alt+numpad codes (Alt held, digits typed on the keypad) also enter characters. With
        // NumLock off the keypad reports navigation keys instead, and the low-level hook data
        // recorded here cannot tell those from the dedicated arrow block, so any Alt+keypad-
        // shaped key in a secure field is redacted; nothing secret is lost by over-redacting.
        bool altNumpadCode = evt.IsAlt && !evt.IsCtrl && !evt.IsWin
            && IsAltCodeKey(evt.VirtualKeyCode);

        if (!altGrChord && !altNumpadCode
            && !IsCharacterKey(evt.VirtualKeyCode, evt.IsCtrl, evt.IsAlt, evt.IsWin))
            return evt;

        return evt with
        {
            VirtualKeyCode = RedactedVirtualKeyCode,
            KeyName = RedactedKeyName,
            IsShift = false,
            IsCtrl = false,
            IsAlt = false,
        };
    }

    /// <summary>
    /// Keys that can form an Alt+numpad character code: VK_NUMPAD0–9, and the keypad's
    /// NumLock-off equivalents (Insert, End, Down, PageDown, Left, Clear, Right, Home, Up,
    /// PageUp).
    /// </summary>
    internal static bool IsAltCodeKey(int vk)
        => vk is >= 0x60 and <= 0x69
            or 0x2D or 0x23 or 0x28 or 0x22 or 0x25 or 0x0C or 0x27 or 0x24 or 0x26 or 0x21;

    private static TextInputFocus? TryGetTextInputFocus(GUITHREADINFO info)
    {
        if (info.hwndCaret == nint.Zero)
            return null;

        var caretTopLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var caretBottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref caretTopLeft)
            || !ClientToScreen(info.hwndCaret, ref caretBottomRight))
        {
            return null;
        }

        int caretX = caretTopLeft.X + ((caretBottomRight.X - caretTopLeft.X) / 2);
        int caretY = caretTopLeft.Y + ((caretBottomRight.Y - caretTopLeft.Y) / 2);

        var boundsTopLeft = new POINT { X = caretX, Y = caretY };
        var boundsBottomRight = boundsTopLeft;
        if (GetClientRect(info.hwndCaret, out var client))
        {
            boundsTopLeft = new POINT { X = client.Left, Y = client.Top };
            boundsBottomRight = new POINT { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(info.hwndCaret, ref boundsTopLeft)
                || !ClientToScreen(info.hwndCaret, ref boundsBottomRight))
            {
                boundsTopLeft = new POINT { X = caretX, Y = caretY };
                boundsBottomRight = boundsTopLeft;
            }
        }

        return new TextInputFocus(
            caretX,
            caretY,
            Math.Min(boundsTopLeft.X, boundsBottomRight.X),
            Math.Min(boundsTopLeft.Y, boundsBottomRight.Y),
            Math.Max(boundsTopLeft.X, boundsBottomRight.X),
            Math.Max(boundsTopLeft.Y, boundsBottomRight.Y));
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

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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

        // A failed or timed-out start can leave a hook thread behind with IsRecording false.
        AbortHookThread();

        _hookReady.Dispose();
    }
}

/// <summary>
/// Stateful half of the "Record typed text = off" rule, run on the keyboard hook thread.
/// Presses are judged by <see cref="KeyboardHookRecorder.ShouldRecordKey"/>; a release is
/// recorded exactly when its press was, so the overlay never sees unbalanced pairs. Alt+hex
/// input (Alt held, Num+, then hex digits on any keys, with Windows' EnableHexNumpad) types a
/// character from keys that look like shortcuts, so from Num+ until Alt is released nothing
/// but Alt itself is recorded.
/// </summary>
internal sealed class TypedTextFilter
{
    private const int VK_ADD = 0x6B;
    private readonly HashSet<int> _recordedDown = new();
    private bool _altHexActive;

    public void Reset()
    {
        _recordedDown.Clear();
        _altHexActive = false;
    }

    /// <param name="alt">Alt state AFTER this event's modifier update.</param>
    public bool ShouldRecord(int vk, bool isDown, bool ctrl, bool alt, bool win, bool isExtended)
    {
        bool isAltKey = vk is 0x12 or 0xA4 or 0xA5; // VK_MENU, VK_LMENU, VK_RMENU

        if (!isDown)
        {
            if (isAltKey && !alt) _altHexActive = false;
            return _recordedDown.Remove(vk);
        }

        if (_altHexActive && !isAltKey)
            return false;

        if (alt && !ctrl && !win && vk == VK_ADD)
        {
            _altHexActive = true;
            return false;
        }

        bool record = KeyboardHookRecorder.ShouldRecordKey(vk, ctrl, alt, win, recordTypedText: false, isExtended);
        if (record) _recordedDown.Add(vk);
        return record;
    }
}
