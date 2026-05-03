using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Musio.Core.Capture;

public sealed class AudioCaptureEngine : IDisposable
{
    private readonly object _lock = new();

    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _systemWriter;
    private WaveFileWriter? _micWriter;

    private string? _systemAudioFilePath;
    private string? _micAudioFilePath;

    private bool _isPaused;
    private bool _disposed;
    private long _firstDataTicks;

    public bool IsRecording { get; private set; }
    public bool IsSystemAudioEnabled { get; set; } = true;
    public bool IsMicEnabled { get; set; }
    public string? SystemAudioDeviceName { get; private set; }
    public string? MicDeviceName { get; private set; }

    /// <summary>
    /// Stopwatch timestamp of the first audio data callback.
    /// This is when WAV file position 0 begins.
    /// </summary>
    public long FirstDataTicks => _firstDataTicks;

    // ── Device enumeration ──────────────────────────────────────────

    public static List<AudioDeviceInfo> GetSystemAudioDevices()
    {
        return EnumerateDevices(DataFlow.Render);
    }

    public static List<AudioDeviceInfo> GetMicDevices()
    {
        return EnumerateDevices(DataFlow.Capture);
    }

    private static List<AudioDeviceInfo> EnumerateDevices(DataFlow dataFlow)
    {
        var results = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // No default device available
            }

            var devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
            foreach (var device in devices)
            {
                bool isDefault = defaultDevice != null && device.ID == defaultDevice.ID;
                results.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, isDefault));
            }

            defaultDevice?.Dispose();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No audio subsystem available
        }

        return results;
    }

    // ── Recording control ───────────────────────────────────────────

    public void StartRecording(string outputFolder)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording) throw new InvalidOperationException("Already recording.");

            Directory.CreateDirectory(outputFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            using var enumerator = new MMDeviceEnumerator();

            // System audio (WASAPI loopback)
            if (IsSystemAudioEnabled)
            {
                try
                {
                    var device = GetDefaultDevice(enumerator, DataFlow.Render);
                    if (device != null)
                    {
                        _systemAudioFilePath = Path.Combine(outputFolder, $"system_{timestamp}.wav");
                        SystemAudioDeviceName = device.FriendlyName;

                        _systemCapture = new WasapiLoopbackCapture(device);
                        _systemWriter = new WaveFileWriter(_systemAudioFilePath, _systemCapture.WaveFormat);

                        _systemCapture.DataAvailable += OnSystemDataAvailable;
                        _systemCapture.RecordingStopped += OnSystemRecordingStopped;
                        _systemCapture.StartRecording();
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // System audio device unavailable — continue without it
                }
            }

            // Microphone (WASAPI capture)
            if (IsMicEnabled)
            {
                try
                {
                    var device = GetDefaultDevice(enumerator, DataFlow.Capture);
                    if (device != null)
                    {
                        _micAudioFilePath = Path.Combine(outputFolder, $"mic_{timestamp}.wav");
                        MicDeviceName = device.FriendlyName;

                        _micCapture = new WasapiCapture(device);
                        _micWriter = new WaveFileWriter(_micAudioFilePath, _micCapture.WaveFormat);

                        _micCapture.DataAvailable += OnMicDataAvailable;
                        _micCapture.RecordingStopped += OnMicRecordingStopped;
                        _micCapture.StartRecording();
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Mic device unavailable — continue without it
                }
            }

            _isPaused = false;
            IsRecording = true;
        }
    }

    public void StopRecording()
    {
        WasapiLoopbackCapture? sysCapture;
        WasapiCapture? micCapture;
        WaveFileWriter? sysWriter;
        WaveFileWriter? micWriter;

        // Grab references and null out fields under lock so data handlers
        // become no-ops immediately. Then stop/dispose OUTSIDE the lock
        // to avoid deadlocking with RecordingStopped callbacks.
        lock (_lock)
        {
            if (!IsRecording) return;

            IsRecording = false;
            _isPaused = false;

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

        StopAndDisposeCapture(sysCapture, sysWriter);
        StopAndDisposeCapture(micCapture, micWriter);
    }

    public void PauseRecording()
    {
        lock (_lock)
        {
            if (!IsRecording || _isPaused) return;
            _isPaused = true;
        }
    }

    public void ResumeRecording()
    {
        lock (_lock)
        {
            if (!IsRecording || !_isPaused) return;
            _isPaused = false;
        }
    }

    // ── File path accessors ─────────────────────────────────────────

    public string? GetSystemAudioFilePath() => _systemAudioFilePath;
    public string? GetMicAudioFilePath() => _micAudioFilePath;

    // ── Data handlers ───────────────────────────────────────────────

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_firstDataTicks == 0)
            Interlocked.CompareExchange(ref _firstDataTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        lock (_lock)
        {
            if (_isPaused || _systemWriter == null) return;
            _systemWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_firstDataTicks == 0)
            Interlocked.CompareExchange(ref _firstDataTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        lock (_lock)
        {
            if (_isPaused || _micWriter == null) return;
            _micWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    // RecordingStopped fires on the WASAPI thread during Dispose —
    // no lock needed since writers are already detached in StopRecording.
    private void OnSystemRecordingStopped(object? sender, StoppedEventArgs e) { }
    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e) { }

    // ── Helpers ──────────────────────────────────────────────────────

    private static MMDevice? GetDefaultDevice(MMDeviceEnumerator enumerator, DataFlow dataFlow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static void StopAndDisposeCapture(IDisposable? capture, WaveFileWriter? writer)
    {
        try
        {
            if (capture is WasapiLoopbackCapture loopback)
                loopback.StopRecording();
            else if (capture is WasapiCapture wasapi)
                wasapi.StopRecording();
        }
        catch { /* best-effort stop */ }

        // Brief wait for final data callbacks to drain
        Thread.Sleep(200);

        SafeDisposeWriter(writer);
        try { capture?.Dispose(); } catch { /* best-effort */ }
    }

    private static void SafeDisposeWriter(WaveFileWriter? writer)
    {
        try { writer?.Dispose(); } catch { /* best-effort dispose */ }
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        WasapiLoopbackCapture? sysCapture;
        WasapiCapture? micCapture;
        WaveFileWriter? sysWriter;
        WaveFileWriter? micWriter;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            IsRecording = false;
            _isPaused = false;

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

        StopAndDisposeCapture(sysCapture, sysWriter);
        StopAndDisposeCapture(micCapture, micWriter);
    }
}
