using System.Diagnostics;
using System.Runtime.InteropServices;
using Musio.Core.Models;
using Musio.Core.Settings;
using Windows.Foundation;

namespace Musio.Core.Capture;

// ── Supporting types ────────────────────────────────────────────────

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
    Stopped,
    Error,
}

public record RecordingSessionConfig
{
    public CaptureTarget Target { get; init; } = default!;
    public int Fps { get; init; } = 30;
    public bool SystemAudioEnabled { get; init; } = true;
    public bool MicEnabled { get; init; } = false;
    public string? SystemAudioDeviceId { get; init; }
    public string? MicDeviceId { get; init; }
    public bool IsWebcamEnabled { get; init; } = false;
    public string? WebcamDeviceId { get; init; }
    public string OutputFolder { get; init; } = "";

    /// <summary>
    /// Bitrate of the intermediate MP4, which doubles as the project's durable
    /// re-editable master once the captured JPEGs are released.
    /// </summary>
    public CaptureQuality CaptureQuality { get; init; } = CaptureQuality.HighFidelity;
}

public class RecordingStatsEventArgs : EventArgs
{
    public TimeSpan Elapsed { get; init; }
    public long FramesCaptured { get; init; }
    public long DroppedFrames { get; init; }
    public double CurrentFps { get; init; }
}

// ── RecordingSession ────────────────────────────────────────────────

/// <summary>
/// Master orchestrator that coordinates all capture engines
/// (screen, mouse, audio) for a single recording session.
/// </summary>
public class RecordingSession : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan StopFinalizeTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(30);

    private readonly RecordingSessionConfig _config;
    private readonly object _lock = new();

    // Engines
    private ScreenCaptureEngine? _screenEngine;
    private MouseHookRecorder? _mouseRecorder;
    private KeyboardHookRecorder? _keyboardRecorder;
    private AudioCaptureEngine? _audioEngine;
    private WebcamCaptureEngine? _webcamEngine;
    private string? _resolvedWebcamDeviceId;
    private VideoWriter? _videoWriter;

    // Timing
    private readonly Stopwatch _elapsedWatch = new();
    private Timer? _statsTimer;

    // FPS tracking
    private long _lastStatsFrameCount;
    private long _lastStatsTimestamp;

    // Output paths
    private string _sessionFolder = "";
    private string _videoFilePath = "";
    private string _cursorDataFilePath = "";
    private string _keyboardDataFilePath = "";

    // Capture dimensions (set once first frame arrives)
    private int _captureWidth;
    private int _captureHeight;
    private long _videoStartTicks;
    private long _audioStartTicks;
    private Rect? _physicalCropRect;
    private float _recordingDpiScale;
    private volatile bool _captureGateOpen;

    // Screen-absolute cursor offset: the physical pixel position of the
    // captured frame's top-left corner on the virtual desktop. Used to
    // rebase mouse hook coordinates (screen-absolute) into the captured
    // frame's coordinate space.
    private int _cursorOffsetX;
    private int _cursorOffsetY;

    // Absolute Stopwatch timestamp when the first video frame was actually
    // emitted by the capture API. This is the true video time 0 — any
    // startup latency between StartCapture() and the first frame must not
    // be included in the mouse→video offset.
    private long _firstVideoFrameTicks;

    // Result
    private Project? _project;
    private RecordingState _state = RecordingState.Idle;
    private bool _disposed;

    // ── Public properties ───────────────────────────────────────────

    public CaptureTarget Target => _config.Target;
    public int Fps => _config.Fps;
    public bool SystemAudioEnabled => _config.SystemAudioEnabled;
    public bool MicEnabled => _config.MicEnabled;

    /// <summary>
    /// Opens the capture gate so frames and audio begin recording.
    /// Call this after the recording overlay is visible to avoid
    /// capturing startup frames (minimize animation, overlay creation).
    /// </summary>
    public void OpenCaptureGate()
    {
        _audioEngine?.OpenGate();
        _captureGateOpen = true;

        // Reset timing origins so offsets are measured from when
        // actual recording content begins, not from engine start.
        _videoStartTicks = Stopwatch.GetTimestamp();
        _audioStartTicks = Stopwatch.GetTimestamp();
        _elapsedWatch.Restart();
    }

    public RecordingState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public TimeSpan Elapsed => _elapsedWatch.Elapsed;
    public long FramesCaptured => _screenEngine?.FramesCaptured ?? 0;
    public long DroppedFrames => _screenEngine?.DroppedFrames ?? 0;

    /// <summary>
    /// Signals that the user has initiated a stop. Call as early as possible
    /// in the stop flow so the mouse recorder can exclude the stop-trigger click.
    /// </summary>
    public void NotifyStopRequested()
    {
        _mouseRecorder?.NotifyStopRequested();
    }

    // ── Events ──────────────────────────────────────────────────────

    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecordingStatsEventArgs>? StatsUpdated;
    public event EventHandler<string>? Error;

    // ── Constructor ─────────────────────────────────────────────────

    public RecordingSession(RecordingSessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Target);

        if (string.IsNullOrWhiteSpace(config.OutputFolder))
            throw new ArgumentException("OutputFolder must be specified.", nameof(config));

        _config = config;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != RecordingState.Idle)
            throw new InvalidOperationException($"Cannot start from state {State}.");

        State = RecordingState.Starting;

        try
        {
            // Create session output folder
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionFolder = Path.Combine(_config.OutputFolder, $"session_{timestamp}");
            Directory.CreateDirectory(_sessionFolder);

            // Prepare file paths
            _videoFilePath = Path.Combine(_sessionFolder, "video.mp4");
            _cursorDataFilePath = Path.Combine(_sessionFolder, "cursor.mcur");
            _keyboardDataFilePath = Path.Combine(_sessionFolder, "keyboard.mkbd");

            // Initialize screen capture engine
            _screenEngine = _config.Target.Type switch
            {
                CaptureTargetType.Monitor => ScreenCaptureEngine.CreateForMonitor(_config.Target.Handle, _config.Fps),
                CaptureTargetType.Window => ScreenCaptureEngine.CreateForWindow(_config.Target.Handle, _config.Fps),
                CaptureTargetType.Region => ScreenCaptureEngine.CreateForMonitor(_config.Target.Handle, _config.Fps),
                _ => throw new ArgumentOutOfRangeException(nameof(_config.Target.Type))
            };

            _screenEngine.FrameCaptured += OnFrameCaptured;
            _screenEngine.Error += OnEngineError;

            // Initialize mouse hook recorder
            _mouseRecorder = new MouseHookRecorder();

            // Initialize keyboard hook recorder
            _keyboardRecorder = new KeyboardHookRecorder();

            // Initialize webcam capture engine (if enabled)
            if (_config.IsWebcamEnabled)
            {
                var webcamDeviceId = _config.WebcamDeviceId;
                var devices = await WebcamCaptureEngine.GetDevicesAsync();

                if (string.IsNullOrWhiteSpace(webcamDeviceId))
                {
                    // Auto-select first available webcam
                    webcamDeviceId = devices.FirstOrDefault()?.Id;
                }
                else if (!devices.Any(d => d.Id == webcamDeviceId))
                {
                    // Saved device is stale/unplugged — fall back to first available
                    webcamDeviceId = devices.FirstOrDefault()?.Id;
                }

                if (!string.IsNullOrWhiteSpace(webcamDeviceId))
                {
                    _webcamEngine = new WebcamCaptureEngine();
                    _resolvedWebcamDeviceId = webcamDeviceId;
                }
                else
                {
                    Error?.Invoke(this, "Webcam enabled but no camera device found");
                }
            }

            // Initialize audio capture engine (if any audio enabled)
            if (_config.SystemAudioEnabled || _config.MicEnabled)
            {
                _audioEngine = new AudioCaptureEngine
                {
                    IsSystemAudioEnabled = _config.SystemAudioEnabled,
                    IsMicEnabled = _config.MicEnabled,
                };
            }

            // Record a shared reference timestamp BEFORE starting any engine.
            // This becomes the common t=0 for video frames and mouse data.
            long sharedStartTicks = Stopwatch.GetTimestamp();

            // Start mouse + keyboard FIRST (they record absolute Stopwatch ticks)
            _mouseRecorder.StartRecording();
            _keyboardRecorder.StartRecording();

            // Start screen capture (its internal stopwatch starts here)
            _screenEngine.StartCapture();

            // Store the offset between mouse start and screen capture start
            // so we can align them during composition.
            // Mouse started at _mouseRecorder's _startTicks (≈ sharedStartTicks).
            // Video frame 0 corresponds to ScreenCaptureEngine._stopwatch.Elapsed = 0
            // which is when StartCapture() was called (a few ms after mouse start).
            _videoStartTicks = Stopwatch.GetTimestamp();

            _audioStartTicks = Stopwatch.GetTimestamp();
            _audioEngine?.StartRecording(_sessionFolder);

            if (_webcamEngine is not null)
                await _webcamEngine.StartAsync(_resolvedWebcamDeviceId!, _sessionFolder);

            // Start elapsed timer
            _elapsedWatch.Restart();

            // Start stats reporting timer (every 500ms)
            _lastStatsFrameCount = 0;
            _lastStatsTimestamp = Stopwatch.GetTimestamp();
            _statsTimer = new Timer(OnStatsTimer, null, 500, 500);

            State = RecordingState.Recording;
        }
        catch (Exception ex)
        {
            State = RecordingState.Error;
            Error?.Invoke(this, $"Failed to start recording: {ex.Message}");
            CleanupEngines();
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State is not (RecordingState.Recording or RecordingState.Paused))
            throw new InvalidOperationException($"Cannot stop from state {State}.");

        State = RecordingState.Stopping;

        try
        {
            // Stop stats timer
            _statsTimer?.Dispose();
            _statsTimer = null;

            // Stop elapsed timer
            _elapsedWatch.Stop();

            // Stop screen capture first, then drain in-flight frame writes before finalizing.
            _screenEngine?.StopCapture();
            _videoWriter?.StopAcceptingFrames();
            if (_videoWriter is not null)
                await _videoWriter.WaitForQuiescenceAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            _mouseRecorder?.StopRecording();
            _keyboardRecorder?.StopRecording();
            _audioEngine?.StopRecording();

            if (_webcamEngine is not null)
                await _webcamEngine.StopAsync();

            // Save mouse cursor data
            if (_mouseRecorder is not null)
                _mouseRecorder.SaveToFile(_cursorDataFilePath);

            // Save keyboard data
            if (_keyboardRecorder is not null)
                SaveKeyboardData(_keyboardDataFilePath, _keyboardRecorder.GetRecordedEvents());

            // Finalize video writer (encode MP4 from frames).
            // The captured JPEGs are a write-ahead buffer, not an archive: once the MP4
            // exists the editor can decode from it indefinitely, so they are released
            // straight away. If encoding fails they are the only surviving copy of the
            // recording and are deliberately kept.
            if (_videoWriter is not null)
            {
                try
                {
                    using var finalizationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    finalizationCts.CancelAfter(StopFinalizeTimeout);
                    await _videoWriter.FinalizeAsync(finalizationCts.Token).ConfigureAwait(false);
                }
                catch (Exception finEx)
                {
                    Debug.WriteLine(
                        $"[RecordingSession] MP4 finalization failed (frames preserved): {finEx.Message}");
                }

                if (_videoWriter.FinalizeSucceeded)
                    _videoWriter.DeleteCapturedFrames();
            }

            // Collect audio file paths
            var audioFilePaths = new List<string>();
            if (_audioEngine is not null)
            {
                var systemPath = _audioEngine.GetSystemAudioFilePath();
                if (!string.IsNullOrEmpty(systemPath) && File.Exists(systemPath))
                    audioFilePaths.Add(systemPath);

                var micPath = _audioEngine.GetMicAudioFilePath();
                if (!string.IsNullOrEmpty(micPath) && File.Exists(micPath))
                    audioFilePaths.Add(micPath);
            }

            // Compute audio→video offset from the first actual audio data callback
            // to the first actual video frame. Positive = audio started before video
            // (pre-roll to skip). Negative = audio started after video (leading
            // silence on the timeline, e.g. mic permission dialog delay).
            long videoOriginTicks = _firstVideoFrameTicks != 0
                ? _firstVideoFrameTicks
                : _videoStartTicks;
            long audioOriginTicks = _audioEngine?.FirstDataTicks ?? _audioStartTicks;
            if (audioOriginTicks == 0) audioOriginTicks = _audioStartTicks;
            double audioToVideoOffset =
                (double)(videoOriginTicks - audioOriginTicks) / Stopwatch.Frequency;

            Debug.WriteLine(
                $"[RecordingSession] Audio offset: {audioToVideoOffset:F4}s " +
                $"(positive=pre-roll, negative=audio started late)");

            // Compute mouse→video time offset from the first ACTUAL frame,
            // not from when StartCapture() was called. Capture APIs often have
            // startup latency (frame pool creation, first vsync, etc.) that
            // would otherwise shift all cursor/click/zoom overlays late.
            double mouseToVideoOffset = 0;
            if (_mouseRecorder is not null)
            {
                var mouseData = _mouseRecorder.GetRecordedData();
                mouseToVideoOffset = (double)(videoOriginTicks - mouseData.StartTimestampTicks)
                    / Stopwatch.Frequency;
                Debug.WriteLine(
                    $"[RecordingSession] Mouse→video offset: {mouseToVideoOffset:F4}s");
            }

            // Build project — use CFR duration and configured FPS for consistent timing.
            // CfrDuration = frameCount / fps gives exact CFR playback duration.
            // ActualDuration/ActualFps are kept on VideoWriter for diagnostics only.
            var cfrDuration = _videoWriter?.CfrDuration ?? _elapsedWatch.Elapsed;

            _project = new Project
            {
                Name = $"Recording {DateTime.Now:yyyy-MM-dd HH:mm}",
                VideoFilePath = _videoFilePath,
                CursorDataFilePath = _cursorDataFilePath,
                KeyboardDataFilePath = _keyboardDataFilePath,
                WebcamFilePath = _webcamEngine?.OutputFilePath,
                AudioFilePaths = audioFilePaths,
                Duration = cfrDuration,
                Width = _captureWidth,
                Height = _captureHeight,
                Fps = _config.Fps,
                MouseToVideoOffsetSeconds = mouseToVideoOffset,
                AudioToVideoOffsetSeconds = audioToVideoOffset,
                CropOffsetX = _cursorOffsetX,
                CropOffsetY = _cursorOffsetY,
                DpiScale = _recordingDpiScale,
                CaptureType = _config.Target.Type,
            };

            State = RecordingState.Stopped;
        }
        catch (Exception ex)
        {
            State = RecordingState.Error;
            Error?.Invoke(this, $"Failed to stop recording: {ex.Message}");
            throw;
        }
        finally
        {
            CleanupEngines();
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != RecordingState.Recording)
            throw new InvalidOperationException($"Cannot pause from state {State}.");

        _screenEngine?.PauseCapture();
        _mouseRecorder?.PauseRecording();
        _keyboardRecorder?.StopRecording(); // no pause support; stop recording
        _audioEngine?.PauseRecording();
        _elapsedWatch.Stop();

        State = RecordingState.Paused;
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != RecordingState.Paused)
            throw new InvalidOperationException($"Cannot resume from state {State}.");

        _elapsedWatch.Start();
        _screenEngine?.ResumeCapture();
        _mouseRecorder?.ResumeRecording();
        _keyboardRecorder?.StartRecording(); // restart after pause-stop
        _audioEngine?.ResumeRecording();

        State = RecordingState.Recording;
    }

    // ── Result ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the project created after <see cref="StopAsync"/> completes.
    /// Returns null if recording has not been stopped yet.
    /// </summary>
    public Project? GetProject() => _project;

    // ── Frame handling ──────────────────────────────────────────────

    private void OnFrameCaptured(object? sender, CapturedFrameEventArgs e)
    {
        // Discard frames until the recording overlay is visible.
        // This eliminates the startup delta (minimize animation,
        // overlay window creation) between capture start and
        // the moment the user sees the recording widget.
        if (!_captureGateOpen)
            return;

        try
        {
            // Lazily create the video writer once we know capture dimensions
            if (_videoWriter is null)
            {
                // Record the absolute time of the first video frame BEFORE
                // the VideoWriter constructor (which does disk I/O + device
                // creation) so the mouse→video offset is as accurate as possible.
                _firstVideoFrameTicks = Stopwatch.GetTimestamp();

                // For region mode, compute the DPI-adjusted crop rect in physical pixels
                if (_config.Target.Type == CaptureTargetType.Region
                    && _config.Target.CropRect is Rect logicalCrop)
                {
                    float dpiScale = GetMonitorDpiScale(_config.Target.Handle);
                    _recordingDpiScale = dpiScale;
                    var physCrop = new Rect(
                        logicalCrop.X * dpiScale,
                        logicalCrop.Y * dpiScale,
                        logicalCrop.Width * dpiScale,
                        logicalCrop.Height * dpiScale);

                    // Clamp to frame bounds
                    double x = Math.Max(0, Math.Min(physCrop.X, e.Width));
                    double y = Math.Max(0, Math.Min(physCrop.Y, e.Height));
                    double w = Math.Min(physCrop.Width, e.Width - x);
                    double h = Math.Min(physCrop.Height, e.Height - y);

                    if (w > 0 && h > 0)
                    {
                        // H.264 requires even dimensions — round down to nearest multiple of 2
                        int evenW = (int)w & ~1;
                        int evenH = (int)h & ~1;
                        if (evenW < 2) evenW = 2;
                        if (evenH < 2) evenH = 2;

                        // Round origin to integer pixels so the video crop and
                        // cursor offset use the same pixel boundary (avoids
                        // sub-pixel drift from fractional DPI-scaled coords).
                        int roundedX = (int)Math.Round(x);
                        int roundedY = (int)Math.Round(y);

                        _physicalCropRect = new Rect(roundedX, roundedY, evenW, evenH);
                        _captureWidth = evenW;
                        _captureHeight = evenH;
                    }
                    else
                    {
                        _captureWidth = e.Width & ~1;
                        _captureHeight = e.Height & ~1;
                    }
                }
                else
                {
                    _captureWidth = e.Width & ~1;
                    _captureHeight = e.Height & ~1;
                }

                if (_captureWidth < 2) _captureWidth = 2;
                if (_captureHeight < 2) _captureHeight = 2;

                // Compute the screen-absolute cursor offset so the compositor
                // can rebase mouse hook coordinates into the captured frame.
                ComputeCursorOffset();

                Debug.WriteLine(
                    $"[RecordingSession] Creating VideoWriter: " +
                    $"captureW={_captureWidth}, captureH={_captureHeight}, " +
                    $"frameW={e.Width}, frameH={e.Height}, " +
                    $"cropRect={_physicalCropRect}, " +
                    $"cursorOffset=({_cursorOffsetX},{_cursorOffsetY}), " +
                    $"targetType={_config.Target.Type}, " +
                    $"logicalCrop={_config.Target.CropRect}");

                _videoWriter = new VideoWriter(
                    _videoFilePath, _captureWidth, _captureHeight, _config.Fps,
                    _screenEngine?.Device, _physicalCropRect, _config.CaptureQuality);
            }

            // Fill any missed frame slots with duplicates of the previous frame
            // so the CFR output stays synchronized with wall-clock time.
            if (e.SkippedSlots > 0)
                _videoWriter.FillGapFrames(e.SkippedSlots);

            _videoWriter.WriteFrame(e.Surface, e.Timestamp);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Frame write error: {ex.Message}");
        }
    }

    private void OnEngineError(object? sender, string message)
    {
        Error?.Invoke(this, $"Capture engine error: {message}");
    }

    // ── Stats timer ─────────────────────────────────────────────────

    private void OnStatsTimer(object? state)
    {
        if (State is not (RecordingState.Recording or RecordingState.Paused))
            return;

        var now = Stopwatch.GetTimestamp();
        var currentFrames = FramesCaptured;
        var deltaFrames = currentFrames - _lastStatsFrameCount;
        var deltaSeconds = (double)(now - _lastStatsTimestamp) / Stopwatch.Frequency;

        double currentFps = deltaSeconds > 0 ? deltaFrames / deltaSeconds : 0;

        _lastStatsFrameCount = currentFrames;
        _lastStatsTimestamp = now;

        StatsUpdated?.Invoke(this, new RecordingStatsEventArgs
        {
            Elapsed = _elapsedWatch.Elapsed,
            FramesCaptured = currentFrames,
            DroppedFrames = DroppedFrames,
            CurrentFps = Math.Round(currentFps, 1),
        });
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    private void CleanupEngines()
    {
        if (_screenEngine is not null)
        {
            _screenEngine.FrameCaptured -= OnFrameCaptured;
            _screenEngine.Error -= OnEngineError;
            _screenEngine.Dispose();
            _screenEngine = null;
        }

        _mouseRecorder?.Dispose();
        _mouseRecorder = null;

        _keyboardRecorder?.Dispose();
        _keyboardRecorder = null;

        _webcamEngine?.Dispose();
        _webcamEngine = null;

        _audioEngine?.Dispose();
        _audioEngine = null;

        _videoWriter?.Dispose();
        _videoWriter = null;
    }

    private static void SaveKeyboardData(string filePath, List<KeyPressEvent> events)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write(events.Count);
        foreach (var evt in events)
        {
            bw.Write(evt.TimestampTicks);
            bw.Write(evt.VirtualKeyCode);
            bw.Write(evt.KeyName);
            bw.Write(evt.IsDown);
            bw.Write(evt.IsCtrl);
            bw.Write(evt.IsAlt);
            bw.Write(evt.IsShift);
            bw.Write(evt.IsWin);
        }
    }

    public static List<KeyPressEvent> LoadKeyboardData(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        using var br = new BinaryReader(fs);

        int count = br.ReadInt32();
        var events = new List<KeyPressEvent>(count);
        for (int i = 0; i < count; i++)
        {
            events.Add(new KeyPressEvent(
                TimestampTicks: br.ReadInt64(),
                VirtualKeyCode: br.ReadInt32(),
                KeyName: br.ReadString(),
                IsDown: br.ReadBoolean(),
                IsCtrl: br.ReadBoolean(),
                IsAlt: br.ReadBoolean(),
                IsShift: br.ReadBoolean(),
                IsWin: br.ReadBoolean()));
        }
        return events;
    }

    // ── DPI helpers ───────────────────────────────────────────────────

    private static float GetMonitorDpiScale(IntPtr hMonitor)
    {
        try
        {
            int hr = GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
            if (hr == 0 && dpiX > 0)
                return dpiX / 96.0f;
        }
        catch { }

        return 1.0f;
    }

    // ── Cursor offset computation ─────────────────────────────────────

    /// <summary>
    /// Computes the screen-absolute physical pixel position of the captured
    /// frame's top-left corner. This is used to rebase mouse hook coordinates
    /// (which are screen-absolute) into the captured frame's coordinate space.
    /// </summary>
    private void ComputeCursorOffset()
    {
        switch (_config.Target.Type)
        {
            case CaptureTargetType.Monitor:
            {
                // The captured frame covers the full monitor. The cursor
                // offset is the monitor's screen-absolute origin.
                var (mx, my) = GetMonitorOrigin(_config.Target.Handle);
                _cursorOffsetX = mx;
                _cursorOffsetY = my;
                break;
            }
            case CaptureTargetType.Window:
            {
                // The captured frame covers the window's visible bounds.
                // Use DwmGetWindowAttribute for the actual rendered frame
                // bounds (excludes invisible shadow borders that GetWindowRect
                // includes). Falls back to GetWindowRect if DWM call fails.
                var (wx, wy) = GetWindowOrigin(_config.Target.Handle);
                _cursorOffsetX = wx;
                _cursorOffsetY = wy;
                break;
            }
            case CaptureTargetType.Region:
            {
                // The captured frame is a cropped sub-region of the full
                // monitor. The cursor offset is the monitor's screen origin
                // plus the crop rect's position within the monitor frame.
                var (mx, my) = GetMonitorOrigin(_config.Target.Handle);
                int cropX = _physicalCropRect.HasValue ? (int)_physicalCropRect.Value.X : 0;
                int cropY = _physicalCropRect.HasValue ? (int)_physicalCropRect.Value.Y : 0;
                _cursorOffsetX = mx + cropX;
                _cursorOffsetY = my + cropY;
                break;
            }
        }

        Debug.WriteLine(
            $"[RecordingSession] Cursor offset: ({_cursorOffsetX},{_cursorOffsetY}) " +
            $"for {_config.Target.Type}");
    }

    /// <summary>
    /// Returns the monitor's screen-absolute origin in physical pixels.
    /// </summary>
    private static (int X, int Y) GetMonitorOrigin(IntPtr hMonitor)
    {
        try
        {
            var info = new MONITORINFOEX();
            info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMonitor, ref info))
                return (info.rcMonitor.Left, info.rcMonitor.Top);
        }
        catch { }

        return (0, 0);
    }

    /// <summary>
    /// Returns the window's screen-absolute origin in physical pixels.
    /// Prefers DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS) which matches
    /// the Graphics Capture API bounds; falls back to GetWindowRect.
    /// </summary>
    private static (int X, int Y) GetWindowOrigin(IntPtr hwnd)
    {
        try
        {
            // DWM extended frame bounds = actual rendered frame (no shadow)
            int hr = DwmGetWindowAttribute(
                hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT dwmRect, Marshal.SizeOf<RECT>());
            if (hr == 0)
                return (dwmRect.Left, dwmRect.Top);
        }
        catch { }

        // Fallback to GetWindowRect (may include shadow padding)
        try
        {
            if (GetWindowRect(hwnd, out RECT rect))
                return (rect.Left, rect.Top);
        }
        catch { }

        return (0, 0);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    // ── IDisposable ─────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _statsTimer?.Dispose();
            _statsTimer = null;

            if (State is RecordingState.Recording or RecordingState.Paused)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(DisposeStopTimeout);
                    await StopAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RecordingSession] Async dispose stop failed: {ex.Message}");
                    CleanupEngines();
                }
            }
            else
            {
                CleanupEngines();
            }
        }
        finally
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _statsTimer?.Dispose();
        _statsTimer = null;

        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            Debug.WriteLine("[RecordingSession] Dispose called while recording; stopping capture without MP4 finalization.");
            try { _videoWriter?.StopAcceptingFrames(); } catch { }
            try { _screenEngine?.StopCapture(); } catch { }
            try { _mouseRecorder?.StopRecording(); } catch { }
            try { _keyboardRecorder?.StopRecording(); } catch { }
            try { _audioEngine?.StopRecording(); } catch { }
        }

        CleanupEngines();
        GC.SuppressFinalize(this);
    }
}
