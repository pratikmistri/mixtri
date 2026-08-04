using System.Runtime.InteropServices;

namespace Musio.Core.Interop;

/// <summary>
/// Shared low-level input hook plumbing (message-loop primitives, the <see cref="MSG"/>
/// struct, and the hook-callback delegate types), consolidated from identical per-file
/// duplicates in <c>KeyboardHookRecorder</c> and <c>MouseHookRecorder</c>.
/// </summary>
/// <remarks>
/// <see cref="SetWindowsHookEx"/>, <see cref="UnhookWindowsHookEx"/>, and
/// <see cref="GetModuleHandle"/> are deliberately NOT consolidated here: the
/// Core-layer copies (in <c>KeyboardHookRecorder</c>/<c>MouseHookRecorder</c>) declare
/// <c>SetLastError = true</c> (and, for <c>UnhookWindowsHookEx</c>, an explicit
/// <c>[return: MarshalAs(UnmanagedType.Bool)]</c>), while the App-layer copies (in
/// <c>RegionSelectorOverlay</c>/<c>WindowSelectorOverlay</c>) omit these. This is a
/// genuine marshalling disagreement between layers, not incidental drift - see the
/// W2-1 interop consolidation report. Each call site keeps its own private declaration.
/// </remarks>
public static class HookInterop
{
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);
    public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
