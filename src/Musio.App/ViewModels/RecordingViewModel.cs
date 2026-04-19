using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Settings;

namespace Musio_App.ViewModels;

public enum CaptureMode
{
    FullScreen,
    Window,
    CustomRegion
}

public partial class RecordingViewModel : ObservableObject
{
    private RecordingSession? _session;
    private System.Threading.Timer? _elapsedTimer;

    [ObservableProperty]
    private CaptureMode _captureMode = CaptureMode.FullScreen;

    [ObservableProperty]
    private bool _isSystemAudioEnabled = true;

    [ObservableProperty]
    private bool _isMicEnabled;

    [ObservableProperty]
    private int _fps = 30;

    [ObservableProperty]
    private bool _isWebcamEnabled;

    [ObservableProperty]
    private string _recordingStatus = "Ready to record";

    [ObservableProperty]
    private CaptureRegion? _selectedRegion;

    [ObservableProperty]
    private string _regionDisplayText = "";

    [ObservableProperty]
    private bool _hasSelectedRegion;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _elapsedTime = "00:00";

    [ObservableProperty]
    private long _framesCaptured;

    [ObservableProperty]
    private double _currentFps;

    /// <summary>
    /// The project produced after recording stops, for navigation to the editor.
    /// </summary>
    public Project? LastProject { get; private set; }

    public bool IsCustomRegionMode => CaptureMode == CaptureMode.CustomRegion;

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        OnPropertyChanged(nameof(IsCustomRegionMode));
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (IsRecording)
            return;

        try
        {
            var target = BuildCaptureTarget();
            if (target is null)
            {
                RecordingStatus = "Could not determine capture target";
                return;
            }

            var outputFolder = AppSettings.Instance.DefaultSavePath;
            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var config = new RecordingSessionConfig
            {
                Target = target,
                Fps = Fps,
                SystemAudioEnabled = IsSystemAudioEnabled,
                MicEnabled = IsMicEnabled,
                OutputFolder = outputFolder,
            };

            _session = new RecordingSession(config);
            _session.StatsUpdated += OnStatsUpdated;
            _session.Error += OnSessionError;
            _session.StateChanged += OnSessionStateChanged;

            await _session.StartAsync();

            IsRecording = true;
            RecordingStatus = "Recording…";
            ElapsedTime = "00:00";

            // UI-thread timer to update elapsed display every 250ms
            _elapsedTimer = new System.Threading.Timer(
                _ => UpdateElapsedDisplay(), null, 250, 250);
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Failed to start: {ex.Message}";
            Debug.WriteLine($"[RecordingViewModel] Start error: {ex}");
            CleanupSession();
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording || _session is null)
            return;

        try
        {
            RecordingStatus = "Stopping…";
            await _session.StopAsync();

            LastProject = _session.GetProject();
            RecordingStatus = "Recording saved";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Stop error: {ex.Message}";
            Debug.WriteLine($"[RecordingViewModel] Stop error: {ex}");
        }
        finally
        {
            CleanupSession();
            IsRecording = false;
        }
    }

    private CaptureTarget? BuildCaptureTarget()
    {
        switch (CaptureMode)
        {
            case CaptureMode.FullScreen:
            {
                var monitors = MonitorEnumerator.GetAllMonitors();
                return monitors.FirstOrDefault();
            }
            case CaptureMode.Window:
            {
                // TODO: Wire up a window picker dialog. For now capture primary monitor.
                var monitors = MonitorEnumerator.GetAllMonitors();
                return monitors.FirstOrDefault();
            }
            case CaptureMode.CustomRegion:
            {
                if (SelectedRegion is null)
                {
                    var regionSelector = new RegionSelector();
                    SelectedRegion = regionSelector.LoadLastRegion();
                }

                if (SelectedRegion is null)
                {
                    RecordingStatus = "No region selected — please select a region first";
                    return null;
                }

                // Capture the full monitor containing the region, with a crop rect
                var allMonitors = MonitorEnumerator.GetAllMonitors();
                var monitor = allMonitors.FirstOrDefault();
                if (monitor is null)
                    return null;

                var crop = new Windows.Foundation.Rect(
                    SelectedRegion.X, SelectedRegion.Y,
                    SelectedRegion.Width, SelectedRegion.Height);

                return new CaptureTarget(
                    CaptureTargetType.Region,
                    monitor.Handle,
                    $"Region {SelectedRegion.Width}×{SelectedRegion.Height}",
                    crop);
            }
            default:
                return null;
        }
    }

    private void OnStatsUpdated(object? sender, RecordingStatsEventArgs e)
    {
        FramesCaptured = e.FramesCaptured;
        CurrentFps = e.CurrentFps;
    }

    private void OnSessionError(object? sender, string message)
    {
        Debug.WriteLine($"[RecordingSession] Error: {message}");
        RecordingStatus = $"Error: {message}";
    }

    private void OnSessionStateChanged(object? sender, RecordingState state)
    {
        Debug.WriteLine($"[RecordingSession] State → {state}");
    }

    private void UpdateElapsedDisplay()
    {
        if (_session is null) return;

        var elapsed = _session.Elapsed;
        ElapsedTime = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        RecordingStatus = $"Recording — {ElapsedTime} · {FramesCaptured} frames · {CurrentFps:F0} fps";
    }

    private void CleanupSession()
    {
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;

        if (_session is not null)
        {
            _session.StatsUpdated -= OnStatsUpdated;
            _session.Error -= OnSessionError;
            _session.StateChanged -= OnSessionStateChanged;
            _session.Dispose();
            _session = null;
        }
    }
}
