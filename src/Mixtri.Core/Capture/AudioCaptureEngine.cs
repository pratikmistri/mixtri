using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mixtri.Core.Capture;

public sealed class AudioCaptureEngine : IDisposable
{
    private readonly object _lock = new();

    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private Pcm16CaptureWriter? _systemWriter;
    private Pcm16CaptureWriter? _micWriter;

    private string? _systemAudioFilePath;
    private string? _micAudioFilePath;

    // Wall-clock alignment per stream (see AudioClockAligner). Guarded by _lock.
    private AudioClockAligner? _systemAligner;
    private AudioClockAligner? _micAligner;
    private int _systemBlockAlign;
    private int _micBlockAlign;

    // Periodic header flush so a crash/kill/power loss leaves readable WAVs (NAudio only
    // writes the RIFF/data sizes on Flush/Dispose). Timer-driven, not packet-driven:
    // loopback delivers no packets at all while nothing plays, so a flush tied to
    // DataAvailable could go minutes without running. Guarded by _lock.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private System.Threading.Timer? _flushTimer;

    private bool _isPaused;
    private bool _disposed;
    private long _firstDataTicks;
    private volatile bool _gateOpen;

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

    /// <summary>
    /// Opens the capture gate so audio data is written to disk.
    /// Call after the recording overlay is visible.
    /// </summary>
    public void OpenGate() => _gateOpen = true;

    /// <summary>
    /// Raised when an audio device could not be started, or stopped delivering audio with an
    /// error mid-recording. The argument is a user-facing description. Raised outside the
    /// engine's lock, possibly on a WASAPI thread; the recording continues without that
    /// stream.
    /// </summary>
    public event EventHandler<string>? DeviceFailed;

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
        var failures = new List<string>();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording) throw new InvalidOperationException("Already recording.");

            Directory.CreateDirectory(outputFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // A restarted engine must not carry the previous recording's paths into this
            // attempt: a start failure abandons (deletes) whatever path is current.
            _systemAudioFilePath = null;
            _micAudioFilePath = null;

            using var enumerator = new MMDeviceEnumerator();

            // System audio (WASAPI loopback). A device that cannot start is reported and
            // skipped: a recording without system audio beats no recording at all.
            if (IsSystemAudioEnabled)
            {
                try
                {
                    var device = GetDefaultDevice(enumerator, DataFlow.Render);
                    if (device != null)
                    {
                        _systemAudioFilePath = Path.Combine(outputFolder, $"system_{timestamp}.wav");
                        SystemAudioDeviceName = device.FriendlyName;
                        _systemBoundDeviceId = device.ID;

                        _systemCapture = new WasapiLoopbackCapture(device);
                        _systemWriter = new Pcm16CaptureWriter(_systemAudioFilePath, _systemCapture.WaveFormat);
                        _systemBlockAlign = _systemCapture.WaveFormat.BlockAlign;
                        _systemAligner = new AudioClockAligner(
                            _systemCapture.WaveFormat.SampleRate, Stopwatch.Frequency);

                        _systemCapture.DataAvailable += OnSystemDataAvailable;
                        _systemCapture.RecordingStopped += OnSystemRecordingStopped;
                        _systemCapture.StartRecording();
                    }
                    else
                    {
                        failures.Add("No system audio output device was found; recording without system audio.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioCaptureEngine] System audio start failed: {ex}");
                    AbandonSystemCaptureLocked();
                    failures.Add($"System audio could not be recorded: {ex.Message}");
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
                        _micBoundDeviceId = device.ID;

                        _micCapture = new WasapiCapture(device);
                        _micWriter = new Pcm16CaptureWriter(_micAudioFilePath, _micCapture.WaveFormat);
                        _micBlockAlign = _micCapture.WaveFormat.BlockAlign;
                        _micAligner = new AudioClockAligner(
                            _micCapture.WaveFormat.SampleRate, Stopwatch.Frequency);

                        _micCapture.DataAvailable += OnMicDataAvailable;
                        _micCapture.RecordingStopped += OnMicRecordingStopped;
                        _micCapture.StartRecording();
                    }
                    else
                    {
                        failures.Add("No microphone was found; recording without the microphone.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioCaptureEngine] Mic start failed: {ex}");
                    AbandonMicCaptureLocked();
                    failures.Add($"The microphone could not be recorded: {ex.Message}");
                }
            }

            _watchSystemDevice = _systemCapture is not null;
            _watchMicDevice = _micCapture is not null;
            StartDefaultDeviceWatchLocked();

            _isPaused = false;
            IsRecording = true;

            _flushTimer?.Dispose();
            _flushTimer = new System.Threading.Timer(
                _ => FlushWriters(), null, FlushInterval, FlushInterval);
        }

        foreach (var failure in failures)
            RaiseDeviceFailed(failure);
    }

    /// <summary>
    /// Tears down a system capture that failed to start and forgets its file, so the session
    /// never lists a header-only WAV as recorded audio. Caller holds <see cref="_lock"/>.
    /// </summary>
    private void AbandonSystemCaptureLocked()
    {
        var capture = _systemCapture;
        var writer = _systemWriter;
        var path = _systemAudioFilePath;
        _systemCapture = null;
        _systemWriter = null;
        _systemAligner = null;
        _systemAudioFilePath = null;

        if (capture is not null)
        {
            capture.DataAvailable -= OnSystemDataAvailable;
            capture.RecordingStopped -= OnSystemRecordingStopped;
        }

        SafeDisposeWriter(writer);
        try { capture?.Dispose(); } catch { /* best-effort */ }
        TryDeleteFile(path);
    }

    /// <summary>Mic counterpart of <see cref="AbandonSystemCaptureLocked"/>.</summary>
    private void AbandonMicCaptureLocked()
    {
        var capture = _micCapture;
        var writer = _micWriter;
        var path = _micAudioFilePath;
        _micCapture = null;
        _micWriter = null;
        _micAligner = null;
        _micAudioFilePath = null;

        if (capture is not null)
        {
            capture.DataAvailable -= OnMicDataAvailable;
            capture.RecordingStopped -= OnMicRecordingStopped;
        }

        SafeDisposeWriter(writer);
        try { capture?.Dispose(); } catch { /* best-effort */ }
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try { File.Delete(path); }
        catch (Exception ex) { Debug.WriteLine($"[AudioCaptureEngine] Could not remove '{path}': {ex.Message}"); }
    }

    // ── Default-device change watch ─────────────────────────────────

    // The captures bind to the default endpoint at start and never follow it, so a default
    // change mid-recording leaves that track silent. There is no live re-binding: the user is
    // told instead. Callbacks arrive on an arbitrary COM thread and must not take _lock
    // (unregistering can wait on in-flight callbacks while the caller holds it).
    private MMDeviceEnumerator? _notifyEnumerator;
    private DefaultDeviceWatcher? _deviceWatcher;
    private volatile bool _watchSystemDevice;
    private volatile bool _watchMicDevice;
    private volatile string? _systemBoundDeviceId;
    private volatile string? _micBoundDeviceId;
    private int _systemChangeReported;
    private int _micChangeReported;

    private void StartDefaultDeviceWatchLocked()
    {
        _systemChangeReported = 0;
        _micChangeReported = 0;

        if (!_watchSystemDevice && !_watchMicDevice)
            return;

        try
        {
            var watcher = new DefaultDeviceWatcher(this);
            var enumerator = new MMDeviceEnumerator();
            try
            {
                enumerator.RegisterEndpointNotificationCallback(watcher);
            }
            catch
            {
                enumerator.Dispose();
                throw;
            }

            _deviceWatcher = watcher;
            _notifyEnumerator = enumerator;
        }
        catch (Exception ex)
        {
            // Losing the warning must never cost the recording.
            Debug.WriteLine($"[AudioCaptureEngine] Default-device watch unavailable: {ex.Message}");
        }
    }

    /// <summary>Unregisters the watcher. Call outside <see cref="_lock"/>.</summary>
    private void StopDefaultDeviceWatch()
    {
        _watchSystemDevice = false;
        _watchMicDevice = false;

        var watcher = Interlocked.Exchange(ref _deviceWatcher, null);
        var enumerator = Interlocked.Exchange(ref _notifyEnumerator, null);
        if (enumerator is null)
            return;

        try
        {
            if (watcher is not null)
                enumerator.UnregisterEndpointNotificationCallback(watcher);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioCaptureEngine] Unregister device watch failed: {ex.Message}");
        }

        try { enumerator.Dispose(); } catch { /* best-effort */ }
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string? newDeviceId)
    {
        try
        {
            if (!IsRecording || role != Role.Multimedia)
                return;

            if (flow == DataFlow.Render && _watchSystemDevice
                && !string.Equals(newDeviceId, _systemBoundDeviceId, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _systemChangeReported, 1) == 0)
            {
                RaiseDeviceFailed(
                    "The default speaker changed during recording; the system audio track may be silent from here.");
            }
            else if (flow == DataFlow.Capture && _watchMicDevice
                && !string.Equals(newDeviceId, _micBoundDeviceId, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _micChangeReported, 1) == 0)
            {
                RaiseDeviceFailed(
                    "The default microphone changed during recording; the microphone track may be silent from here.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioCaptureEngine] Default-device callback failed: {ex.Message}");
        }
    }

    private sealed class DefaultDeviceWatcher : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        private readonly AudioCaptureEngine _owner;

        public DefaultDeviceWatcher(AudioCaptureEngine owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            => _owner.OnDefaultDeviceChanged(flow, role, defaultDeviceId);

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private void RaiseDeviceFailed(string message)
    {
        Debug.WriteLine($"[AudioCaptureEngine] Device failure: {message}");
        try
        {
            DeviceFailed?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            // A subscriber must never take down a WASAPI thread or the start path.
            Debug.WriteLine($"[AudioCaptureEngine] DeviceFailed handler threw: {ex.Message}");
        }
    }

    public void StopRecording()
    {
        WasapiLoopbackCapture? sysCapture;
        WasapiCapture? micCapture;
        Pcm16CaptureWriter? sysWriter;
        Pcm16CaptureWriter? micWriter;

        // Grab references and null out fields under lock so data handlers
        // become no-ops immediately. Then stop/dispose OUTSIDE the lock
        // to avoid deadlocking with RecordingStopped callbacks.
        lock (_lock)
        {
            if (!IsRecording) return;

            IsRecording = false;

            // Bring each stream up to the stop time, so a recording that ended in silence
            // (loopback delivers nothing then) is as long as the wall clock says it was.
            if (!_isPaused)
            {
                long now = Stopwatch.GetTimestamp();
                PadToNowLocked(_systemWriter, _systemAligner, now, "System");
                PadToNowLocked(_micWriter, _micAligner, now, "Mic");
            }

            _isPaused = false;

            // A tick already waiting on _lock finds the writers null below and does nothing.
            _flushTimer?.Dispose();
            _flushTimer = null;

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

        StopDefaultDeviceWatch();
        StopAndDisposeCapture(sysCapture, sysWriter);
        StopAndDisposeCapture(micCapture, micWriter);
    }

    /// <summary>Writes trailing silence up to <paramref name="now"/>. Caller holds the lock.</summary>
    private void PadToNowLocked(
        Pcm16CaptureWriter? writer, AudioClockAligner? aligner, long now, string label)
    {
        if (writer is null || aligner is null)
            return;

        try
        {
            long pad = aligner.ComputeTrailingSilence(Interlocked.Read(ref _firstDataTicks), now);
            if (pad > 0)
            {
                writer.WriteSilence(pad);
                aligner.Commit(pad, 0, now);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioCaptureEngine] {label} trailing pad failed: {ex.Message}");
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
        if (!_gateOpen) return;
        if (_firstDataTicks == 0)
            Interlocked.CompareExchange(ref _firstDataTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        lock (_lock)
        {
            if (_isPaused || _systemWriter == null) return;
            try
            {
                // Loopback goes quiet (no packets at all) while nothing plays; fill the hole
                // so everything after it stays aligned with the video.
                WriteAligned(_systemWriter, _systemAligner, _systemBlockAlign, e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioCaptureEngine] System audio write failed: {ex.Message}");
            }
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_gateOpen) return;
        if (_firstDataTicks == 0)
            Interlocked.CompareExchange(ref _firstDataTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        lock (_lock)
        {
            if (_isPaused || _micWriter == null) return;
            try
            {
                // A live mic delivers continuously, so this only pads after a pause or a
                // late device start — the same wall-clock holes the loopback stream has.
                WriteAligned(_micWriter, _micAligner, _micBlockAlign, e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioCaptureEngine] Mic audio write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a captured buffer, preceded by any silence needed to keep the stream on the
    /// wall clock. Caller holds <see cref="_lock"/>.
    /// </summary>
    private void WriteAligned(
        Pcm16CaptureWriter writer, AudioClockAligner? aligner, int blockAlign, WaveInEventArgs e)
    {
        if (aligner is null || blockAlign <= 0)
        {
            writer.Write(e.Buffer, e.BytesRecorded);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long incomingFrames = e.BytesRecorded / blockAlign;
        long pad = aligner.ComputeLeadingSilence(
            Interlocked.Read(ref _firstDataTicks), now, incomingFrames);

        if (pad > 0)
        {
            Debug.WriteLine(
                $"[AudioCaptureEngine] Padding {pad} frames of silence to stay on the wall clock.");
            writer.WriteSilence(pad);
        }

        writer.Write(e.Buffer, e.BytesRecorded);
        aligner.Commit(pad, incomingFrames, now);
    }

    /// <summary>
    /// Timer tick: rewrites both WAV headers so the bytes already on disk stay readable if the
    /// process dies. Never throws — a flush failure must not take down a thread-pool thread.
    /// </summary>
    private void FlushWriters()
    {
        lock (_lock)
        {
            if (!IsRecording) return;
            FlushLocked(_systemWriter, "System");
            FlushLocked(_micWriter, "Mic");
        }
    }

    private static void FlushLocked(Pcm16CaptureWriter? writer, string label)
    {
        if (writer is null) return;
        try { writer.Flush(); }
        catch (Exception ex) { Debug.WriteLine($"[AudioCaptureEngine] {label} WAV flush failed: {ex.Message}"); }
    }

    // RecordingStopped fires on the WASAPI thread during Dispose —
    // no lock needed since writers are already detached in StopRecording.
    // A non-null exception means the device failed (unplugged, invalidated, exclusive-mode
    // takeover) while it was still supposed to be recording.
    private void OnSystemRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } ex)
            RaiseDeviceFailed($"System audio stopped recording: {ex.Message}");
    }

    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } ex)
            RaiseDeviceFailed($"The microphone stopped recording: {ex.Message}");
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

    private static void StopAndDisposeCapture(IDisposable? capture, Pcm16CaptureWriter? writer)
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

    private static void SafeDisposeWriter(Pcm16CaptureWriter? writer)
    {
        try { writer?.Dispose(); } catch { /* best-effort dispose */ }
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        WasapiLoopbackCapture? sysCapture;
        WasapiCapture? micCapture;
        Pcm16CaptureWriter? sysWriter;
        Pcm16CaptureWriter? micWriter;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            IsRecording = false;

            // Bring each stream up to the stop time, so a recording that ended in silence
            // (loopback delivers nothing then) is as long as the wall clock says it was.
            if (!_isPaused)
            {
                long now = Stopwatch.GetTimestamp();
                PadToNowLocked(_systemWriter, _systemAligner, now, "System");
                PadToNowLocked(_micWriter, _micAligner, now, "Mic");
            }

            _isPaused = false;

            // A tick already waiting on _lock finds the writers null below and does nothing.
            _flushTimer?.Dispose();
            _flushTimer = null;

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

        StopDefaultDeviceWatch();
        StopAndDisposeCapture(sysCapture, sysWriter);
        StopAndDisposeCapture(micCapture, micWriter);
    }
}
