using System.Diagnostics;
using System.Runtime.InteropServices;
using Musio.Core.Interop;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookInterop.LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    #endregion

    private const int DefaultSampleCapacity = 100_000;
    private const int DefaultClickCapacity  = 1_000;
    private const double MinMoveIntervalMs = 4.0; // 250Hz cap

    /// <summary>Default cursor-shape polling interval (~50Hz).</summary>
    public const int DefaultShapeSampleIntervalMs = 20;

    /// <summary>Bounds for <see cref="ShapeSampleIntervalMs"/>.</summary>
    public const int MinShapeSampleIntervalMs = 8;
    public const int MaxShapeSampleIntervalMs = 1000;

    // Binary file format constants
    private static readonly byte[] MagicBytes = "MCUR"u8.ToArray();
    private const int FileFormatVersion = 2;

    private nint _hookId;
    private HookInterop.LowLevelMouseProc? _hookProc; // prevent GC collection of the delegate
    private readonly CursorShapeResolver _shapeResolver = new();

    // The active cursor shape is tracked by a dedicated polling thread (see
    // ShapeThreadProc) and re-confirmed on click transitions. GetCursorInfo is NEVER
    // called from the low-level hook callback: doing so puts a syscall on the system
    // input path and adds latency to live cursor motion (see learnings.md). The hook
    // only reads this cached value. Written from the poll/hook threads.
    private volatile int _currentShape = (int)CursorShape.Arrow;

    private readonly List<MouseSample> _samples = new(DefaultSampleCapacity);
    private readonly List<ClickEvent> _clicks   = new(DefaultClickCapacity);
    private readonly object _lock = new();
    private long _lastSampleTicks;

    private long _startTicks;
    private long _endTicks;
    private long _stopRequestedTicks;
    private long _lastMoveTicks;
    private volatile bool _paused;
    private bool _disposed;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);

    private Thread? _shapeThread;
    private readonly ManualResetEventSlim _shapeStop = new(false);
    private int _shapeSampleIntervalMs = DefaultShapeSampleIntervalMs;

    /// <summary>
    /// How often the active cursor shape is sampled while recording, in milliseconds.
    /// Lower values track hover-driven shape changes (link, text, resize) more closely.
    /// Must be set before <see cref="StartRecording"/>; clamped to
    /// [<see cref="MinShapeSampleIntervalMs"/>, <see cref="MaxShapeSampleIntervalMs"/>].
    /// </summary>
    public int ShapeSampleIntervalMs
    {
        get => _shapeSampleIntervalMs;
        set => _shapeSampleIntervalMs = Math.Clamp(value, MinShapeSampleIntervalMs, MaxShapeSampleIntervalMs);
    }

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
            _lastSampleTicks = 0;
        }

        _lastMoveTicks = 0;
        _paused = false;
        _hookReady.Reset();
        _shapeStop.Reset();
        _startTicks = Stopwatch.GetTimestamp();

        // Seed the shape; the poller thread keeps it current from here on.
        _currentShape = (int)_shapeResolver.Resolve();

        _hookThread = new Thread(HookThreadProc)
        {
            Name = "MouseHookRecorder",
            IsBackground = true,
        };
        _hookThread.Start();

        // Wait for the hook to be installed before returning.
        if (!_hookReady.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Mouse hook thread failed to start within 5 seconds.");

        if (_hookId == nint.Zero)
            throw new InvalidOperationException(
                $"Failed to install mouse hook. Win32 error: {Marshal.GetLastWin32Error()}");

        // Start the shape poller only once the hook is live, so it never outlives a
        // failed start.
        _shapeThread = new Thread(ShapeThreadProc)
        {
            Name = "MouseShapePoller",
            IsBackground = true,
        };
        _shapeThread.Start();

        IsRecording = true;
    }

    public void StopRecording()
    {
        if (!IsRecording) return;

        _endTicks = _stopRequestedTicks > 0 ? _stopRequestedTicks : Stopwatch.GetTimestamp();
        IsRecording = false;

        StopShapeThread();

        // Tell the hook thread's message loop to exit.
        HookInterop.PostThreadMessage(_hookThreadId, HookInterop.WM_QUIT, 0, 0);
        _hookThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _hookThread = null;
    }

    private void StopShapeThread()
    {
        _shapeStop.Set();
        _shapeThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _shapeThread = null;
    }

    /// <summary>
    /// Signals that the user has initiated a stop (e.g. clicked the Stop button).
    /// Records the current timestamp and stops collecting new events. The click
    /// that triggered the stop will be excluded from recorded data.
    /// </summary>
    public void NotifyStopRequested()
    {
        _stopRequestedTicks = Stopwatch.GetTimestamp();
        _paused = true;
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
            var data = new MouseRecordingData
            {
                Samples = new List<MouseSample>(_samples),
                Clicks  = new List<ClickEvent>(_clicks),
                StartTimestampTicks = _startTicks,
                EndTimestampTicks   = _endTicks != 0 ? _endTicks : Stopwatch.GetTimestamp(),
                TickFrequency       = Stopwatch.Frequency,
            };

            if (_stopRequestedTicks > 0)
                data = MouseRecordingData.TrimStopClick(data, _stopRequestedTicks);

            return data;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Hook thread — runs its own message pump
    // ────────────────────────────────────────────────────────────────

    private void HookThreadProc()
    {
        _hookThreadId = HookInterop.GetCurrentThreadId();
        _hookProc = HookCallback;

        nint hMod = GetModuleHandle(null);
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hMod, 0);

        // Signal the calling thread that the hook is (or isn't) installed.
        _hookReady.Set();

        if (_hookId == nint.Zero)
            return;

        // Standard Win32 message pump — required for WH_MOUSE_LL delivery.
        while (HookInterop.GetMessage(out HookInterop.MSG msg, nint.Zero, 0, 0))
        {
            HookInterop.TranslateMessage(ref msg);
            HookInterop.DispatchMessage(ref msg);
        }

        // Clean up the hook on this thread before exiting.
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    // ────────────────────────────────────────────────────────────────
    // Shape poller thread — samples the live cursor shape off the hook path
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the active cursor shape on its own thread at <see cref="ShapeSampleIntervalMs"/>.
    /// The hook callback only reads the cached result, so cursor motion is never delayed
    /// by a <c>GetCursorInfo</c> syscall. When the shape changes, a synthetic move sample
    /// is appended so hover-driven changes are recorded even while the mouse is stationary.
    /// </summary>
    private void ShapeThreadProc()
    {
        int interval = _shapeSampleIntervalMs;

        try
        {
            while (!_shapeStop.Wait(interval))
            {
                if (_paused || !IsRecording)
                    continue;

                try
                {
                    var shape = _shapeResolver.Resolve(out int x, out int y, out bool positionValid);
                    if ((int)shape == _currentShape)
                        continue;

                    _currentShape = (int)shape;

                    if (!positionValid)
                        continue;

                    long ticks = Stopwatch.GetTimestamp();
                    lock (_lock)
                    {
                        if (!IsRecording || _paused)
                            continue;

                        AddSampleLocked(new MouseSample
                        {
                            TimestampTicks = ticks,
                            X              = x,
                            Y              = y,
                            EventKind      = MouseEventKind.Move,
                            Button         = MouseButton.None,
                            ScrollDelta    = 0,
                            Shape          = shape,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MouseHookRecorder] Shape poll error: {ex.Message}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The recorder was disposed while this thread was waiting — just exit.
        }
    }

    /// <summary>
    /// Appends a sample, keeping the sample list non-decreasing in time. Two threads
    /// (the hook and the shape poller) append concurrently, so a sample can arrive with
    /// a slightly older timestamp than the previous one; downstream consumers
    /// (<c>CursorSmoother.AssignShapes</c>) assume ordered timestamps.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void AddSampleLocked(MouseSample sample)
    {
        if (sample.TimestampTicks < _lastSampleTicks)
            sample.TimestampTicks = _lastSampleTicks;

        _samples.Add(sample);
        _lastSampleTicks = sample.TimestampTicks;
    }

    // ────────────────────────────────────────────────────────────────
    // Hook callback — must be as fast as possible
    // ────────────────────────────────────────────────────────────────

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && !_paused)
            {
                var hookData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                long ticks   = Stopwatch.GetTimestamp();
                int msg      = (int)wParam;

                ClassifyEvent(msg, hookData, ticks, out MouseEventKind kind, out MouseButton button,
                              out short scrollDelta, out bool isClick, out bool isDown);

                // Re-confirm the cursor shape on click transitions so a press/release
                // shape change (grab, resize, ...) is stamped exactly at the click rather
                // than up to one poll interval later. Clicks are rare, so this syscall
                // never runs on the high-rate move path.
                if (isClick)
                {
                    try { _currentShape = (int)_shapeResolver.Resolve(); }
                    catch { /* keep last known shape on transient failure */ }
                }

                var sample = new MouseSample
                {
                    TimestampTicks = ticks,
                    X              = hookData.pt.X,
                    Y              = hookData.pt.Y,
                    EventKind      = kind,
                    Button         = button,
                    ScrollDelta    = scrollDelta,
                    Shape          = (CursorShape)_currentShape,
                };

                lock (_lock)
                {
                    bool isPureMove = msg == WM_MOUSEMOVE;
                    bool shouldRecordSample = !isPureMove ||
                        _lastMoveTicks == 0 ||
                        (ticks - _lastMoveTicks) * 1000.0 / Stopwatch.Frequency >= MinMoveIntervalMs;

                    if (shouldRecordSample)
                    {
                        AddSampleLocked(sample);

                        if (isPureMove)
                            _lastMoveTicks = ticks;
                    }

                    if (isClick)
                    {
                        _clicks.Add(new ClickEvent(ticks, hookData.pt.X, hookData.pt.Y, button, isDown));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MouseHookRecorder] HookCallback error: {ex.Message}");
        }

        return HookInterop.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
    /// As of version 2 each MouseSample additionally stores a cursor-shape byte.
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
            _lastSampleTicks = 0;
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
            bw.Write((byte)s.Shape);
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
        if (version is not (1 or 2))
            throw new InvalidDataException($"Unsupported MCUR version {version}.");

        bool hasShape = version >= 2;

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
                Shape          = hasShape ? (CursorShape)br.ReadByte() : CursorShape.Arrow,
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
        else
            StopShapeThread();

        _hookReady.Dispose();
        _shapeStop.Dispose();
    }
}
