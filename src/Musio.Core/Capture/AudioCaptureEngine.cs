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

    public bool IsRecording { get; private set; }
    public bool IsSystemAudioEnabled { get; set; } = true;
    public bool IsMicEnabled { get; set; }
    public string? SystemAudioDeviceName { get; private set; }
    public string? MicDeviceName { get; private set; }

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
        lock (_lock)
        {
            if (!IsRecording) return;

            IsRecording = false;
            _isPaused = false;

            StopAndDisposeCapture(ref _systemCapture, ref _systemWriter);
            StopAndDisposeCapture(ref _micCapture, ref _micWriter);
        }
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
        lock (_lock)
        {
            if (_isPaused || _systemWriter == null) return;
            _systemWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_isPaused || _micWriter == null) return;
            _micWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnSystemRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            SafeDisposeWriter(ref _systemWriter);
        }
    }

    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            SafeDisposeWriter(ref _micWriter);
        }
    }

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

    private static void StopAndDisposeCapture<T>(ref T? capture, ref WaveFileWriter? writer)
        where T : class, IDisposable
    {
        try
        {
            if (capture is WasapiLoopbackCapture loopback)
                loopback.StopRecording();
            else if (capture is WasapiCapture wasapi)
                wasapi.StopRecording();
        }
        catch { /* best-effort stop */ }

        // Give the RecordingStopped event a moment to flush the writer
        Thread.Sleep(200);

        SafeDisposeWriter(ref writer);

        capture?.Dispose();
        capture = null;
    }

    private static void SafeDisposeWriter(ref WaveFileWriter? writer)
    {
        try
        {
            writer?.Dispose();
        }
        catch { /* best-effort dispose */ }
        writer = null;
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (IsRecording) StopRecording();

            _systemCapture?.Dispose();
            _systemCapture = null;
            _micCapture?.Dispose();
            _micCapture = null;

            SafeDisposeWriter(ref _systemWriter);
            SafeDisposeWriter(ref _micWriter);
        }
    }
}
