using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Musio.Core.Capture;

public record WebcamDeviceInfo(string Id, string Name);

/// <summary>
/// Captures webcam video to an MP4 file using <see cref="MediaCapture"/>.
/// Call <see cref="GetDevicesAsync"/> to enumerate available cameras,
/// then <see cref="StartAsync"/> / <see cref="StopAsync"/> to record.
/// </summary>
public sealed class WebcamCaptureEngine : IDisposable
{
    private MediaCapture? _mediaCapture;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public string? OutputFilePath { get; private set; }

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

        IsRecording = true;
    }

    public async Task StopAsync()
    {
        if (!IsRecording || _mediaCapture is null)
            return;

        await _mediaCapture.StopRecordAsync();
        IsRecording = false;

        DisposeMediaCapture();
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording && _mediaCapture is not null)
        {
            // Best-effort synchronous stop; callers should await StopAsync first.
            try { _mediaCapture.StopRecordAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
            IsRecording = false;
        }

        DisposeMediaCapture();
    }

    private void DisposeMediaCapture()
    {
        _mediaCapture?.Dispose();
        _mediaCapture = null;
    }
}
