using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Settings;
using Musio_App.Services;

namespace Musio_App.ViewModels;

public enum CaptureMode
{
    FullScreen,
    Window,
    CustomRegion
}

public partial class RecordingViewModel : ObservableObject
{
    /// <summary>
    /// Process-wide singleton so capture mode, selected region/window, and audio
    /// toggles survive page navigation (e.g. Editor → Record). The
    /// <see cref="RecordingPage"/> wires up to this instance via
    /// <c>OnNavigatedTo</c>/<c>OnNavigatedFrom</c> rather than constructor
    /// subscriptions to avoid handler leaks.
    /// </summary>
    public static RecordingViewModel Shared { get; } = new();

    private RecordingSession? _session;
    private System.Threading.Timer? _elapsedTimer;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    /// <summary>
    /// When true, the finished recording is appended to the existing project
    /// (the page calls <see cref="Services.ProjectService.AppendRecording"/>)
    /// instead of replacing it, so the VM must NOT reset the project via SetProject.
    /// </summary>
    public bool IsAppendMode { get; set; }

    /// <summary>
    /// Opens the capture gate so frames and audio begin recording.
    /// Call after the recording overlay is visible.
    /// </summary>
    public void OpenCaptureGate() => _session?.OpenCaptureGate();

    /// <summary>
    /// Must be called from the UI thread (e.g. in page constructor) to enable
    /// thread-safe property updates from background recording callbacks.
    /// </summary>
    public void SetDispatcher(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        => _dispatcher = dispatcher;

    private void RunOnUI(Action action)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            _dispatcher.TryEnqueue(() => action());
        else
            action();
    }

    [ObservableProperty]
    private CaptureMode _captureMode = CaptureMode.FullScreen;

    [ObservableProperty]
    private bool _isSystemAudioEnabled = AppSettings.Instance.IsSystemAudioEnabled;

    [ObservableProperty]
    private bool _isMicEnabled = AppSettings.Instance.IsMicEnabled;

    [ObservableProperty]
    private int _fps = 30;

    [ObservableProperty]
    private bool _isWebcamEnabled = AppSettings.Instance.IsWebcamEnabled;

    [ObservableProperty]
    private string _recordingStatus = "Ready to record";

    /// <summary>
    /// Raised when an operation fails, so the view can surface it to the user.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingStatus"/> is not a substitute: it also carries routine progress
    /// text (per-frame counters, "Recording saved") and, since the recording page's status
    /// bar was removed, has no UI surface of its own — failures written only to it would be
    /// silent.
    /// </remarks>
    public event EventHandler<string>? ErrorRaised;

    /// <summary>Records a failure on <see cref="RecordingStatus"/> and announces it.</summary>
    private void SetError(string message)
    {
        RecordingStatus = message;
        ErrorRaised?.Invoke(this, message);
    }

    [ObservableProperty]
    private CaptureRegion? _selectedRegion;

    [ObservableProperty]
    private string _regionDisplayText = "";

    [ObservableProperty]
    private bool _hasSelectedRegion;

    [ObservableProperty]
    private WindowInfo? _selectedWindow;

    public ObservableCollection<WindowItem> AvailableWindows { get; } = new();

    private CancellationTokenSource? _windowRefreshCts;

    public bool HasSelectedWindow => SelectedWindow is not null;

    public bool IsWindowMode => CaptureMode == CaptureMode.Window;

    partial void OnSelectedWindowChanged(WindowInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedWindow));
    }

    /// <summary>
    /// Enumerates visible windows (excluding Musio), populates <see cref="AvailableWindows"/>,
    /// and loads app icons in the background. Preserves the current selection if the window
    /// is still alive.
    /// </summary>
    public async Task RefreshAvailableWindowsAsync()
    {
        // Cancel any previous refresh still loading icons
        _windowRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _windowRefreshCts = cts;

        var regionSelector = new RegionSelector();
        var windows = regionSelector.GetVisibleWindows();

        var currentPid = (uint)Process.GetCurrentProcess().Id;
        var filtered = windows
            .Where(w =>
            {
                try
                {
                    GetWindowThreadProcessId(w.Handle, out uint pid);
                    return pid != currentPid;
                }
                catch { return true; }
            })
            .OrderBy(w => w.Title)
            .ToList();

        // Remember the previously selected handle so we can restore it
        var previousHandle = SelectedWindow?.Handle;

        AvailableWindows.Clear();
        WindowItem? restoredItem = null;

        foreach (var w in filtered)
        {
            var item = new WindowItem(w);
            AvailableWindows.Add(item);
            if (previousHandle is not null && w.Handle == previousHandle)
                restoredItem = item;
        }

        // Restore selection if the window is still in the list
        if (restoredItem is not null)
            SelectedWindow = restoredItem.Info;
        else if (previousHandle is not null)
            SelectedWindow = null; // previously selected window is gone

        // Load icons in parallel (best-effort, cancellable)
        try
        {
            var tasks = AvailableWindows.Select(w => w.LoadIconAsync(cts.Token));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    [ObservableProperty]
    private bool _isRecording;

    private bool _isStopping;

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

    partial void OnIsMicEnabledChanged(bool value)
        => AppSettings.Instance.IsMicEnabled = value;

    partial void OnIsSystemAudioEnabledChanged(bool value)
        => AppSettings.Instance.IsSystemAudioEnabled = value;

    partial void OnIsWebcamEnabledChanged(bool value)
        => AppSettings.Instance.IsWebcamEnabled = value;

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        OnPropertyChanged(nameof(IsCustomRegionMode));
        OnPropertyChanged(nameof(IsWindowMode));
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
                SetError("Could not determine capture target");
                return;
            }

            // Bring the selected window to the front so it's fully visible for capture
            if (CaptureMode == CaptureMode.Window && SelectedWindow is not null)
            {
                SetForegroundWindow(SelectedWindow.Handle);
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
                IsWebcamEnabled = IsWebcamEnabled,
                WebcamDeviceId = GetWebcamDeviceId(),
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
            SetError($"Failed to start: {ex.Message}");
            Debug.WriteLine($"[RecordingViewModel] Start error: {ex}");
            CleanupSession();
        }
    }

    /// <summary>
    /// Signals that the user has initiated a stop. Call as early as possible
    /// (e.g. in the Stop button handler) so the stop-trigger click is excluded.
    /// </summary>
    public void NotifyStopRequested() => _session?.NotifyStopRequested();

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording || _session is null || _isStopping)
            return;

        _isStopping = true;

        try
        {
            RecordingStatus = "Stopping…";

            // Run heavy stop work on a background thread so the UI stays responsive
            // (overlay spinner/messages keep animating)
            var session = _session;
            await Task.Run(async () => await session.StopAsync());

            LastProject = _session.GetProject();
            if (LastProject is not null && !IsAppendMode)
            {
                // Normal recording: replace the project/timeline. In append mode the
                // page calls ProjectService.AppendRecording instead, so skip this to
                // avoid duplicating the new recording as two segments.
                ProjectService.Instance.SetProject(LastProject);
            }
            RecordingStatus = "Recording saved";
        }
        catch (Exception ex)
        {
            SetError($"Stop error: {ex.Message}");
            Debug.WriteLine($"[RecordingViewModel] Stop error: {ex}");
        }
        finally
        {
            CleanupSession();
            IsRecording = false;
            _isStopping = false;
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
                if (SelectedWindow is null)
                {
                    SetError("No window selected — please select a window first");
                    return null;
                }

                // Validate the window handle is still valid
                if (!IsWindow(SelectedWindow.Handle))
                {
                    SetError("Selected window is no longer available");
                    SelectedWindow = null;
                    return null;
                }

                return new CaptureTarget(
                    CaptureTargetType.Window,
                    SelectedWindow.Handle,
                    SelectedWindow.Title);
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
                    SetError("No region selected — please select a region first");
                    return null;
                }

                // Resolve the monitor that owns this region by MonitorId.
                // Exact match against device name — `Contains` is unsafe
                // (e.g. "\\.\DISPLAY1" would match "\\.\DISPLAY10").
                var allMonitors = MonitorEnumerator.GetAllMonitors();
                var monitor = allMonitors.FirstOrDefault(m =>
                        m.DisplayName == SelectedRegion.MonitorId
                        || m.DisplayName.StartsWith(SelectedRegion.MonitorId + " "));
                if (monitor is null)
                {
                    // The monitor that owned this region is no longer connected
                    // (e.g. external display unplugged since the region was saved).
                    // Don't silently fall back to the primary monitor — the region's
                    // monitor-local coords would land on the wrong display, producing
                    // a clamped/off-screen capture. Clear the stale region and ask
                    // the user to reselect.
                    SelectedRegion = null;
                    HasSelectedRegion = false;
                    SetError("Saved region's monitor is no longer connected — please select a new region");
                    return null;
                }

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
        RunOnUI(() =>
        {
            FramesCaptured = e.FramesCaptured;
            CurrentFps = e.CurrentFps;
        });
    }

    private void OnSessionError(object? sender, string message)
    {
        Debug.WriteLine($"[RecordingSession] Error: {message}");
        RunOnUI(() => SetError($"Error: {message}"));
    }

    private void OnSessionStateChanged(object? sender, RecordingState state)
    {
        Debug.WriteLine($"[RecordingSession] State → {state}");
    }

    private void UpdateElapsedDisplay()
    {
        if (_session is null) return;

        var elapsed = _session.Elapsed;
        var timeStr = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        RunOnUI(() =>
        {
            ElapsedTime = timeStr;
            RecordingStatus = $"Recording — {ElapsedTime} · {FramesCaptured} frames · {CurrentFps:F0} fps";
        });
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static string? GetWebcamDeviceId()
    {
        var savedId = AppSettings.Instance.WebcamDeviceId;
        return string.IsNullOrWhiteSpace(savedId) ? null : savedId;
    }
}
