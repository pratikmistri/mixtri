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
    // writes the RIFF/data sizes on Flush/Dispose). Stopwatch timestamps; 0 = not started.
    // Guarded by _lock.
    private static readonly long FlushIntervalTicks = Stopwatch.Frequency * 5;
    private long _systemLastFlush;
    private long _micLastFlush;

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

            _isPaused = false;
            IsRecording = true;
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

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

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
                WriteAligned(_systemWriter, _systemAligner, _systemBlockAlign, e, ref _systemLastFlush);
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
                WriteAligned(_micWriter, _micAligner, _micBlockAlign, e, ref _micLastFlush);
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
        Pcm16CaptureWriter writer, AudioClockAligner? aligner, int blockAlign, WaveInEventArgs e,
        ref long lastFlush)
    {
        if (aligner is null || blockAlign <= 0)
        {
            writer.Write(e.Buffer, e.BytesRecorded);
            FlushIfDue(writer, ref lastFlush);
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
        FlushIfDue(writer, ref lastFlush);
    }

    /// <summary>
    /// Flushes the writer (updating the WAV header sizes) at most every ~5 s. Never throws:
    /// a flush failure must not kill the WASAPI callback. Caller holds <see cref="_lock"/>.
    /// </summary>
    private static void FlushIfDue(Pcm16CaptureWriter writer, ref long lastFlush)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastFlush == 0)
        {
            lastFlush = now;
            return;
        }
        if (now - lastFlush < FlushIntervalTicks)
            return;

        lastFlush = now;
        try { writer.Flush(); }
        catch (Exception ex) { Debug.WriteLine($"[AudioCaptureEngine] Periodic WAV flush failed: {ex.Message}"); }
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

            sysCapture = _systemCapture; _systemCapture = null;
            micCapture = _micCapture;    _micCapture = null;
            sysWriter = _systemWriter;   _systemWriter = null;
            micWriter = _micWriter;      _micWriter = null;
        }

        StopAndDisposeCapture(sysCapture, sysWriter);
        StopAndDisposeCapture(micCapture, micWriter);
    }
}
