using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Mixtri.Core.Capture;

public record WebcamDeviceInfo(string Id, string Name);

/// <summary>
/// Result of a bounded webcam stop. Anything other than <see cref="Completed"/> or
/// <see cref="NotRecording"/> means the camera recording may be truncated, but session
/// finalization has been allowed to continue.
/// </summary>
public enum WebcamStopOutcome
{
    /// <summary>Nothing was recording, so nothing had to stop.</summary>
    NotRecording,

    /// <summary>The driver finished the stop within the budget.</summary>
    Completed,

    /// <summary>The stop did not return within the timeout; the capture was abandoned.</summary>
    TimedOut,

    /// <summary>The caller cancelled while waiting; the capture was abandoned.</summary>
    Canceled,

    /// <summary>The stop threw.</summary>
    Failed,
}

/// <summary>
/// Captures webcam video to an MP4 file using <see cref="MediaCapture"/>.
/// Call <see cref="GetDevicesAsync"/> to enumerate available cameras,
/// then <see cref="StartAsync"/> / <see cref="StopAsync(TimeSpan, CancellationToken)"/> to record.
/// </summary>
public sealed class WebcamCaptureEngine : IDisposable
{
    /// <summary>
    /// How long a stop is allowed to take before the session gives up on the camera.
    /// A wedged driver must never hold the app in the stopping state.
    /// </summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(3);

    private MediaCapture? _mediaCapture;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public string? OutputFilePath { get; private set; }

    /// <summary>Outcome of the last stop attempt (diagnostic).</summary>
    public WebcamStopOutcome LastStopOutcome { get; private set; } = WebcamStopOutcome.NotRecording;

    /// <summary>The failure from the last stop, if it threw.</summary>
    public Exception? LastStopError { get; private set; }

    // ── Device enumeration ──────────────────────────────────────────

    public static async Task<List<WebcamDeviceInfo>> GetDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        var results = new List<WebcamDeviceInfo>(devices.Count);

        foreach (var device in devices)
        {
            results.Add(new WebcamDeviceInfo(device.Id, device.Name));
        }

        return results;
    }

    // ── Recording lifecycle ─────────────────────────────────────────

    public async Task StartAsync(string deviceId, string outputFolder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        Directory.CreateDirectory(outputFolder);

        _mediaCapture = new MediaCapture();

        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            };

            await _mediaCapture.InitializeAsync(settings);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"webcam_{timestamp}.mp4";
            var folder = await StorageFolder.GetFolderFromPathAsync(outputFolder);
            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

            OutputFilePath = file.Path;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            await _mediaCapture.StartRecordToStorageFileAsync(profile, file);
        }
        catch
        {
            DisposeMediaCapture();
            throw;
        }

        IsRecording = true;
    }

    /// <summary>
    /// Stops the camera recording under a watchdog. A stalled camera driver can leave
    /// <c>StopRecordAsync</c> pending forever, so the wait is bounded and cancellable; on
    /// timeout the capture object is abandoned to a background reclaim and the caller is
    /// free to finish the rest of session finalization.
    /// </summary>
    /// <returns>How the stop ended. Never throws for a stalled or failing driver.</returns>
    public async Task<WebcamStopOutcome> StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var capture = _mediaCapture;
        if (!IsRecording || capture is null)
        {
            LastStopOutcome = WebcamStopOutcome.NotRecording;
            return LastStopOutcome;
        }

        // Clear the recording flag first: whatever the driver does from here, this engine
        // must not be asked to stop a second time.
        IsRecording = false;
        LastStopError = null;

        Task stopTask;
        try
        {
            stopTask = capture.StopRecordAsync().AsTask();
        }
        catch (Exception ex)
        {
            LastStopError = ex;
            LastStopOutcome = WebcamStopOutcome.Failed;
            Debug.WriteLine($"[WebcamCaptureEngine] StopRecordAsync threw: {ex.Message}");
            DisposeMediaCapture();
            return LastStopOutcome;
        }

        var outcome = await WaitForStopAsync(stopTask, timeout, ct).ConfigureAwait(false);
        LastStopOutcome = outcome;

        if (outcome == WebcamStopOutcome.Failed)
            LastStopError = stopTask.Exception?.GetBaseException();

        if (outcome is WebcamStopOutcome.Completed or WebcamStopOutcome.Failed)
        {
            DisposeMediaCapture();
        }
        else
        {
            Debug.WriteLine(
                $"[WebcamCaptureEngine] Stop {outcome} after {timeout}; abandoning the capture object.");
            AbandonMediaCapture(capture, stopTask);
        }

        return outcome;
    }

    public Task<WebcamStopOutcome> StopAsync(CancellationToken ct = default)
        => StopAsync(DefaultStopTimeout, ct);

    /// <summary>
    /// Bounded, cancellation-aware wait for a driver stop call. Split out so the watchdog
    /// can be tested without a camera.
    /// </summary>
    internal static async Task<WebcamStopOutcome> WaitForStopAsync(
        Task stopTask, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stopTask);

        try
        {
            await stopTask.WaitAsync(timeout, ct).ConfigureAwait(false);
            return WebcamStopOutcome.Completed;
        }
        catch (TimeoutException)
        {
            ObserveAbandoned(stopTask);
            return WebcamStopOutcome.TimedOut;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ObserveAbandoned(stopTask);
            return WebcamStopOutcome.Canceled;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebcamCaptureEngine] Stop failed: {ex.Message}");
            return WebcamStopOutcome.Failed;
        }
    }

    /// <summary>Keeps an abandoned stop from surfacing as an unobserved task exception.</summary>
    private static void ObserveAbandoned(Task stopTask)
    {
        _ = stopTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Releases a capture whose stop never returned, without blocking the caller and
    /// without disposing while a driver call is still in flight (which can fault the
    /// process). If the driver never returns the object is leaked for the remaining
    /// lifetime of the process — deliberately preferred over hanging the stop.
    /// </summary>
    private void AbandonMediaCapture(MediaCapture capture, Task stopTask)
    {
        _mediaCapture = null;

        _ = Task.Run(async () =>
        {
            try { await stopTask.ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[WebcamCaptureEngine] Abandoned stop ended: {ex.Message}"); }

            try { capture.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[WebcamCaptureEngine] Abandoned dispose failed: {ex.Message}"); }
        });
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var capture = _mediaCapture;
        if (IsRecording && capture is not null)
        {
            IsRecording = false;

            try
            {
                var stopTask = capture.StopRecordAsync().AsTask();
                if (!stopTask.Wait(DisposeStopTimeout))
                {
                    Debug.WriteLine("[WebcamCaptureEngine] StopRecordAsync timed out in Dispose.");
                    ObserveAbandoned(stopTask);
                    AbandonMediaCapture(capture, stopTask);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebcamCaptureEngine] Dispose stop failed: {ex.Message}");
            }
        }

        DisposeMediaCapture();
    }

    private void DisposeMediaCapture()
    {
        try { _mediaCapture?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[WebcamCaptureEngine] Dispose failed: {ex.Message}"); }

        _mediaCapture = null;
    }
}
