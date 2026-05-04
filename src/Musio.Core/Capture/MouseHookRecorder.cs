using System.Diagnostics;
using System.Runtime.InteropServices;
using Musio.Core.Models;

namespace Musio.Core.Capture;

/// <summary>
/// Records mouse cursor positions and click events at high frequency using a
/// low-level mouse hook (WH_MOUSE_LL). A dedicated background thread runs a
/// message pump so the hook works regardless of the calling thread.
/// </summary>
public sealed class MouseHookRecorder : IDisposable
{
    #region Win32 interop

    private const int WH_MOUSE_LL = 14;

    private const int WM_MOUSEMOVE      = 0x0200;
    private const int WM_LBUTTONDOWN    = 0x0201;
    private const int WM_LBUTTONUP      = 0x0202;
    private const int WM_RBUTTONDOWN    = 0x0204;
    private const int WM_RBUTTONUP      = 0x0205;
    private const int WM_MBUTTONDOWN    = 0x0207;
    private const int WM_MBUTTONUP      = 0x0208;
    private const int WM_MOUSEWHEEL     = 0x020A;
    private const int WM_MOUSEHWHEEL    = 0x020E;

    private const int WM_QUIT           = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    #endregion

    private const int DefaultSampleCapacity = 100_000;
    private const int DefaultClickCapacity  = 1_000;

    // Binary file format constants
    private static readonly byte[] MagicBytes = "MCUR"u8.ToArray();
    private const int FileFormatVersion = 1;

    private nint _hookId;
    private LowLevelMouseProc? _hookProc; // prevent GC collection of the delegate

    private readonly List<MouseSample> _samples = new(DefaultSampleCapacity);
    private readonly List<ClickEvent> _clicks   = new(DefaultClickCapacity);
    private readonly object _lock = new();

    private long _startTicks;
    private long _endTicks;
    private bool _paused;
    private bool _disposed;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);

    public bool IsRecording { get; private set; }
    public long SampleCount { get { lock (_lock) { return _samples.Count; } } }
    public int ClickCount   { get { lock (_lock) { return _clicks.Count; } } }

    // ────────────────────────────────────────────────────────────────
    // Recording lifecycle
    // ────────────────────────────────────────────────────────────────

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");

        lock (_lock)
        {
            _samples.Clear();
            _clicks.Clear();
        }

        _paused = false;
        _hookReady.Reset();
        _startTicks = Stopwatch.GetTimestamp();

        _hookThread = new Thread(HookThreadProc)
        {
            Name = "MouseHookRecorder",
            IsBackground = true,
        };
        _hookThread.Start();

        // Wait for the hook to be installed before returning.
        _hookReady.Wait();

        if (_hookId == nint.Zero)
            throw new InvalidOperationException(
                $"Failed to install mouse hook. Win32 error: {Marshal.GetLastWin32Error()}");

        IsRecording = true;
    }

    public void StopRecording()
    {
        if (!IsRecording) return;

        _endTicks = Stopwatch.GetTimestamp();
        IsRecording = false;

        // Tell the hook thread's message loop to exit.
        PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _hookThread = null;
    }

    public void PauseRecording()
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording.");
        _paused = true;
    }

    public void ResumeRecording()
    {
        if (!IsRecording)
            throw new InvalidOperationException("Not recording.");
        _paused = false;
    }

    public MouseRecordingData GetRecordedData()
    {
        lock (_lock)
        {
            return new MouseRecordingData
            {
                Samples = new List<MouseSample>(_samples),
                Clicks  = new List<ClickEvent>(_clicks),
                StartTimestampTicks = _startTicks,
                EndTimestampTicks   = _endTicks != 0 ? _endTicks : Stopwatch.GetTimestamp(),
                TickFrequency       = Stopwatch.Frequency,
            };
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Hook thread — runs its own message pump
    // ────────────────────────────────────────────────────────────────

    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookProc = HookCallback;

        nint hMod = GetModuleHandle(null);
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hMod, 0);

        // Signal the calling thread that the hook is (or isn't) installed.
        _hookReady.Set();

        if (_hookId == nint.Zero)
            return;

        // Standard Win32 message pump — required for WH_MOUSE_LL delivery.
        while (GetMessage(out MSG msg, nint.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Clean up the hook on this thread before exiting.
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    // ────────────────────────────────────────────────────────────────
    // Hook callback — must be as fast as possible
    // ────────────────────────────────────────────────────────────────

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_paused)
        {
            var hookData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            long ticks   = Stopwatch.GetTimestamp();
            int msg      = (int)wParam;

            ClassifyEvent(msg, hookData, ticks, out MouseEventKind kind, out MouseButton button,
                          out short scrollDelta, out bool isClick, out bool isDown);

            var sample = new MouseSample
            {
                TimestampTicks = ticks,
                X              = hookData.pt.X,
                Y              = hookData.pt.Y,
                EventKind      = kind,
                Button         = button,
                ScrollDelta    = scrollDelta,
            };

            lock (_lock)
            {
                _samples.Add(sample);

                if (isClick)
                {
                    _clicks.Add(new ClickEvent(ticks, hookData.pt.X, hookData.pt.Y, button, isDown));
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void ClassifyEvent(int msg, MSLLHOOKSTRUCT hookData, long ticks,
        out MouseEventKind kind, out MouseButton button, out short scrollDelta,
        out bool isClick, out bool isDown)
    {
        kind        = MouseEventKind.Move;
        button      = MouseButton.None;
        scrollDelta = 0;
        isClick     = false;
        isDown      = false;

        switch (msg)
        {
            case WM_MOUSEMOVE:
                break;

            case WM_LBUTTONDOWN:
                kind    = MouseEventKind.ButtonDown;
                button  = MouseButton.Left;
                isClick = true;
                isDown  = true;
                break;

            case WM_LBUTTONUP:
                kind    = MouseEventKind.ButtonUp;
                button  = MouseButton.Left;
                isClick = true;
                break;

            case WM_RBUTTONDOWN:
                kind    = MouseEventKind.ButtonDown;
                button  = MouseButton.Right;
                isClick = true;
                isDown  = true;
                break;

            case WM_RBUTTONUP:
                kind    = MouseEventKind.ButtonUp;
                button  = MouseButton.Right;
                isClick = true;
                break;

            case WM_MBUTTONDOWN:
                kind    = MouseEventKind.ButtonDown;
                button  = MouseButton.Middle;
                isClick = true;
                isDown  = true;
                break;

            case WM_MBUTTONUP:
                kind    = MouseEventKind.ButtonUp;
                button  = MouseButton.Middle;
                isClick = true;
                break;

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                kind        = MouseEventKind.Scroll;
                scrollDelta = (short)((hookData.mouseData >> 16) & 0xFFFF);
                break;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Binary serialization
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes all recorded data to a binary file.
    /// Format: [MCUR][version:i32][sampleCount:i32][clickCount:i32]
    ///         [startTicks:i64][endTicks:i64][tickFreq:f64]
    ///         [MouseSample * sampleCount][ClickEvent * clickCount]
    /// </summary>
    /// <remarks>
    /// After saving, this method frees in-memory samples.
    /// Use <see cref="LoadFromFile"/> to read the data back after this call.
    /// </remarks>
    public void SaveToFile(string filePath)
    {
        var data = GetRecordedData();
        SaveDataToFile(filePath, data);

        // Free in-memory samples now that data is persisted to disk.
        // Subsequent reads should use LoadFromFile.
        lock (_lock)
        {
            _samples.Clear();
            _samples.TrimExcess();
            _clicks.Clear();
            _clicks.TrimExcess();
        }
    }

    private static void SaveDataToFile(string filePath, MouseRecordingData data)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        using var bw = new BinaryWriter(fs);

        // Header
        bw.Write(MagicBytes);
        bw.Write(FileFormatVersion);
        bw.Write(data.Samples.Count);
        bw.Write(data.Clicks.Count);
        bw.Write(data.StartTimestampTicks);
        bw.Write(data.EndTimestampTicks);
        bw.Write(data.TickFrequency);

        // Samples
        foreach (var s in data.Samples)
        {
            bw.Write(s.TimestampTicks);
            bw.Write(s.X);
            bw.Write(s.Y);
            bw.Write((byte)s.EventKind);
            bw.Write((byte)s.Button);
            bw.Write(s.ScrollDelta);
        }

        // Clicks
        foreach (var c in data.Clicks)
        {
            bw.Write(c.TimestampTicks);
            bw.Write(c.X);
            bw.Write(c.Y);
            bw.Write((byte)c.Button);
            bw.Write(c.IsDown);
        }
    }

    public static MouseRecordingData LoadFromFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        using var br = new BinaryReader(fs);

        // Header
        byte[] magic = br.ReadBytes(MagicBytes.Length);
        if (!magic.AsSpan().SequenceEqual(MagicBytes))
            throw new InvalidDataException("Not a valid MCUR file.");

        int version = br.ReadInt32();
        if (version != FileFormatVersion)
            throw new InvalidDataException($"Unsupported MCUR version {version}.");

        int sampleCount = br.ReadInt32();
        int clickCount  = br.ReadInt32();
        long startTicks = br.ReadInt64();
        long endTicks   = br.ReadInt64();
        double tickFreq = br.ReadDouble();

        // Samples
        var samples = new List<MouseSample>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            samples.Add(new MouseSample
            {
                TimestampTicks = br.ReadInt64(),
                X              = br.ReadInt32(),
                Y              = br.ReadInt32(),
                EventKind      = (MouseEventKind)br.ReadByte(),
                Button         = (MouseButton)br.ReadByte(),
                ScrollDelta    = br.ReadInt16(),
            });
        }

        // Clicks
        var clicks = new List<ClickEvent>(clickCount);
        for (int i = 0; i < clickCount; i++)
        {
            clicks.Add(new ClickEvent(
                TimestampTicks: br.ReadInt64(),
                X:      br.ReadInt32(),
                Y:      br.ReadInt32(),
                Button: (MouseButton)br.ReadByte(),
                IsDown: br.ReadBoolean()));
        }

        return new MouseRecordingData
        {
            Samples             = samples,
            Clicks              = clicks,
            StartTimestampTicks = startTicks,
            EndTimestampTicks   = endTicks,
            TickFrequency       = tickFreq,
        };
    }

    // ────────────────────────────────────────────────────────────────
    // IDisposable
    // ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
            StopRecording();

        _hookReady.Dispose();
    }
}
