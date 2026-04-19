using System.Diagnostics;
using Musio.Core.Models;

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
    public int Fps { get; init; } = 60;
    public bool SystemAudioEnabled { get; init; } = true;
    public bool MicEnabled { get; init; } = false;
    public string? SystemAudioDeviceId { get; init; }
    public string? MicDeviceId { get; init; }
    public bool IsWebcamEnabled { get; init; } = false;
    public string? WebcamDeviceId { get; init; }
    public string OutputFolder { get; init; } = "";
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
public class RecordingSession : IDisposable
{
    private readonly RecordingSessionConfig _config;
    private readonly object _lock = new();

    // Engines
    private ScreenCaptureEngine? _screenEngine;
    private MouseHookRecorder? _mouseRecorder;
    private KeyboardHookRecorder? _keyboardRecorder;
    private AudioCaptureEngine? _audioEngine;
    private WebcamCaptureEngine? _webcamEngine;
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

    // Result
    private Project? _project;
    private RecordingState _state = RecordingState.Idle;
    private bool _disposed;

    // ── Public properties ───────────────────────────────────────────

    public CaptureTarget Target => _config.Target;
    public int Fps => _config.Fps;
    public bool SystemAudioEnabled => _config.SystemAudioEnabled;
    public bool MicEnabled => _config.MicEnabled;

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
            if (_config.IsWebcamEnabled && !string.IsNullOrWhiteSpace(_config.WebcamDeviceId))
            {
                _webcamEngine = new WebcamCaptureEngine();
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

            // Start all engines
            _screenEngine.StartCapture();
            _mouseRecorder.StartRecording();
            _keyboardRecorder.StartRecording();
            _audioEngine?.StartRecording(_sessionFolder);

            if (_webcamEngine is not null)
                await _webcamEngine.StartAsync(_config.WebcamDeviceId!, _sessionFolder);

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

    public async Task StopAsync()
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

            // Stop all engines
            _screenEngine?.StopCapture();
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

            // Finalize video writer
            if (_videoWriter is not null)
                await _videoWriter.FinalizeAsync();

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

            // Build project
            _project = new Project
            {
                Name = $"Recording {DateTime.Now:yyyy-MM-dd HH:mm}",
                VideoFilePath = _videoFilePath,
                CursorDataFilePath = _cursorDataFilePath,
                KeyboardDataFilePath = _keyboardDataFilePath,
                WebcamFilePath = _webcamEngine?.OutputFilePath,
                AudioFilePaths = audioFilePaths,
                Duration = _elapsedWatch.Elapsed,
                Width = _captureWidth,
                Height = _captureHeight,
                Fps = _config.Fps,
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
        try
        {
            // Lazily create the video writer once we know capture dimensions
            if (_videoWriter is null)
            {
                _captureWidth = e.Width;
                _captureHeight = e.Height;
                _videoWriter = new VideoWriter(_videoFilePath, e.Width, e.Height, _config.Fps);
            }

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

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _statsTimer?.Dispose();
        _statsTimer = null;

        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { /* best-effort stop */ }
        }

        CleanupEngines();
        GC.SuppressFinalize(this);
    }
}
