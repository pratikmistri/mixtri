using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixtri.Core.Capture;
using Mixtri.Core.Models;
using Mixtri.Core.Settings;
using Mixtri_App.Services;

namespace Mixtri_App.ViewModels;

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
    /// Enumerates visible windows (excluding Mixtri), populates <see cref="AvailableWindows"/>,
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

    /// <summary>
    /// True from the moment a stop begins until the session is finalized. Observable so
    /// the recording overlay can switch to its "Stopping…" state whichever route started
    /// the stop (overlay button, tray Exit, a closed capture target, OS shutdown).
    /// </summary>
    [ObservableProperty]
    private bool _isStopping;

    /// <summary>
    /// Finalization progress (0..1) reported by the session while stopping, or -1 while
    /// no progress has been reported yet.
    /// </summary>
    [ObservableProperty]
    private double _finalizationProgress = -1;

    /// <summary>
    /// User-facing description of a fault the running session has recorded, or null while
    /// the capture is healthy. Shown by the recording overlay as a warning, since the full
    /// window — where errors normally surface — is off screen for the whole recording.
    /// </summary>
    [ObservableProperty]
    private string? _recordingWarning;

    /// <summary>
    /// Why the last recording stopped when it was not the user's own Stop (e.g. the
    /// recorded window closed). Null after a normal stop. Read by the shell once the
    /// recording has ended, so it can tell the user.
    /// </summary>
    public string? LastAutoStopReason { get; private set; }

    /// <summary>The in-flight stop, so every caller that needs it finalized awaits the same one.</summary>
    private Task? _stopTask;

    /// <summary>
    /// The background half of the in-flight stop (<see cref="RecordingSession.StopAsync"/>
    /// on the thread pool). Unlike <see cref="_stopTask"/> it never needs the UI thread to
    /// complete, so the session-end path can wait on it synchronously.
    /// </summary>
    private Task? _sessionStopTask;

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

    /// <summary>
    /// Checks that the current selection resolves to something that can be captured,
    /// surfacing the reason through <see cref="ErrorRaised"/> when it does not. Called by
    /// the shell BEFORE it hides its windows, so a bad selection never leaves the user
    /// staring at an empty desktop.
    /// </summary>
    public bool ValidateCaptureTarget()
    {
        try
        {
            return BuildCaptureTarget() is not null;
        }
        catch (Exception ex)
        {
            SetError($"Could not determine capture target: {ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (IsRecording)
            return;

        // A previous take must never be mistaken for this one's result.
        LastProject = null;
        LastAutoStopReason = null;
        RecordingWarning = null;
        FinalizationProgress = -1;

        try
        {
            // A minimized window renders nothing (and reports off-screen bounds), so restore
            // it and bring it to the front BEFORE the capture target is derived from it.
            if (CaptureMode == CaptureMode.Window && SelectedWindow is not null)
            {
                var hwnd = SelectedWindow.Handle;
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    // Give DWM a moment to finish the restore animation and publish real bounds.
                    await Task.Delay(300);
                }

                SetForegroundWindow(hwnd);
            }

            var target = BuildCaptureTarget();
            if (target is null)
            {
                SetError("Could not determine capture target");
                return;
            }

            // Recordings are working state, not deliverables: they go to LocalAppData so
            // the user's save folder only ever receives saved .mixtri projects and exports.
            var fallback = AppSettings.Instance.DefaultSavePath;
            if (string.IsNullOrWhiteSpace(fallback))
                fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var outputFolder = SessionPaths.EnsureSessionsRoot(fallback);

            var config = new RecordingSessionConfig
            {
                Target = target,
                Fps = Fps,
                SystemAudioEnabled = IsSystemAudioEnabled,
                MicEnabled = IsMicEnabled,
                IsWebcamEnabled = IsWebcamEnabled,
                WebcamDeviceId = GetWebcamDeviceId(),
                OutputFolder = outputFolder,
                CaptureQuality = AppSettings.Instance.CaptureQuality,
            };

            _session = new RecordingSession(config);
            _session.StatsUpdated += OnStatsUpdated;
            _session.Error += OnSessionError;
            _session.StateChanged += OnSessionStateChanged;
            _session.CaptureTargetLost += OnCaptureTargetLost;
            _session.FinalizationProgressChanged += OnFinalizationProgressChanged;

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
    private Task StopRecordingAsync() => StopAndFinalizeAsync();

    /// <summary>
    /// Stops the recording and finalizes it. Safe to call from any route and any number of
    /// times: a stop already in flight is returned rather than started again, so the tray's
    /// Exit, the shutdown path and the overlay's Stop button all wait on the same one.
    /// Must be called on the UI thread.
    /// </summary>
    public Task StopAndFinalizeAsync()
    {
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        if (!IsRecording || _session is null || IsStopping) return Task.CompletedTask;

        _stopTask = StopCoreAsync();
        return _stopTask;
    }

    /// <summary>
    /// Blocks for at most <paramref name="timeout"/> while the session finalizes. Only for
    /// the OS session-end path, which must finish inside the message handler: it waits on
    /// the thread-pool half of the stop, which needs nothing from the (blocked) UI thread.
    /// </summary>
    public bool WaitForSessionStop(TimeSpan timeout)
    {
        var task = _sessionStopTask;
        if (task is null) return true;

        try
        {
            return task.Wait(timeout);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingViewModel] Session stop failed during shutdown wait: {ex.Message}");
            return true;
        }
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        if (!IsRecording || session is null || IsStopping)
            return;

        IsStopping = true;

        // Cleared up front: if anything below fails, no stale project from an earlier
        // take may be handed to the editor as this recording's result.
        LastProject = null;
        FinalizationProgress = -1;

        // Stop the elapsed ticker FIRST, so its next tick cannot overwrite "Stopping…" with
        // a stale "Recording — 00:42 …" line. A callback already in flight is harmless: it
        // re-checks IsStopping on the UI thread before writing anything.
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;

        try
        {
            RecordingStatus = "Stopping…";

            // Run heavy stop work on a background thread so the UI stays responsive
            // (overlay spinner/messages keep animating)
            var stopWork = Task.Run(async () => await session.StopAsync());
            _sessionStopTask = stopWork;
            await stopWork;

            LastProject = session.GetProject();
            if (LastProject is null)
            {
                // Nothing usable was captured. The session recorded why; say so rather
                // than opening an empty editor.
                var fault = session.Fault;
                SetError(fault is not null
                    ? $"Recording failed: {fault}"
                    : "Recording failed: no video was captured.");
            }
            else
            {
                if (!IsAppendMode)
                {
                    // Normal recording: replace the project/timeline. In append mode the
                    // page calls ProjectService.AppendRecording instead, so skip this to
                    // avoid duplicating the new recording as two segments.
                    ProjectService.Instance.SetProject(LastProject);
                }
                // A take that finished degraded (lost frames, a dropped audio device) still
                // opens, but the shell reports why — including faults raised during
                // finalization, after the overlay's last look.
                if (session.Fault is { } degraded)
                    RecordingWarning = degraded.ToString();

                RecordingStatus = "Recording saved";
            }
        }
        catch (Exception ex)
        {
            LastProject = null;
            SetError($"Stop error: {ex.Message}");
            Debug.WriteLine($"[RecordingViewModel] Stop error: {ex}");
        }
        finally
        {
            CleanupSession();
            _sessionStopTask = null;
            IsRecording = false;
            IsStopping = false;
            FinalizationProgress = -1;
        }
    }

    private CaptureTarget? BuildCaptureTarget()
    {
        switch (CaptureMode)
        {
            case CaptureMode.FullScreen:
            {
                // "Full Screen" means the primary display, not whichever monitor the OS
                // happens to enumerate first. MonitorEnumerator marks it in DisplayName.
                var monitors = MonitorEnumerator.GetAllMonitors();
                return monitors.FirstOrDefault(m =>
                           m.DisplayName.EndsWith("(Primary)", StringComparison.Ordinal))
                       ?? monitors.FirstOrDefault();
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
        RunOnUI(() =>
        {
            // The full window is off screen while recording, so the overlay's warning
            // state is the only place the user will see this before they press Stop.
            if (IsRecording && !IsStopping)
                RecordingWarning = message;
            SetError($"Error: {message}");
        });
    }

    private void OnSessionStateChanged(object? sender, RecordingState state)
    {
        Debug.WriteLine($"[RecordingSession] State → {state}");
    }

    /// <summary>
    /// The capture target (recorded window or display) went away. Stop and keep what was
    /// recorded rather than carrying on filming nothing.
    /// </summary>
    private void OnCaptureTargetLost(object? sender, EventArgs e)
    {
        RunOnUI(() =>
        {
            if (!IsRecording || IsStopping || _session is null) return;

            Debug.WriteLine("[RecordingViewModel] Capture target lost; stopping.");
            LastAutoStopReason = CaptureMode == CaptureMode.Window
                ? "The recorded window was closed, so the recording was stopped."
                : "The recorded display was disconnected, so the recording was stopped.";
            _ = StopAndFinalizeAsync();
        });
    }

    private void OnFinalizationProgressChanged(object? sender, double progress)
    {
        if (double.IsNaN(progress)) return;
        var clamped = Math.Clamp(progress, 0.0, 1.0);
        RunOnUI(() =>
        {
            if (IsStopping) FinalizationProgress = clamped;
        });
    }

    private void UpdateElapsedDisplay()
    {
        // Runs on a thread-pool timer, racing the stop flow that nulls and disposes the
        // session. Read the field once, and treat a session torn down mid-read as "done".
        var session = _session;
        if (session is null) return;

        TimeSpan elapsed;
        string? warning;
        try
        {
            elapsed = session.Elapsed;
            warning = session.Fault?.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingViewModel] Elapsed tick skipped: {ex.Message}");
            return;
        }

        var timeStr = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        RunOnUI(() =>
        {
            // A tick queued just before the stop began must not overwrite "Stopping…".
            if (!IsRecording || IsStopping || !ReferenceEquals(session, _session)) return;

            ElapsedTime = timeStr;
            RecordingStatus = $"Recording — {ElapsedTime} · {FramesCaptured} frames · {CurrentFps:F0} fps";

            if (!string.Equals(RecordingWarning, warning, StringComparison.Ordinal) && warning is not null)
                RecordingWarning = warning;
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
            _session.CaptureTargetLost -= OnCaptureTargetLost;
            _session.FinalizationProgressChanged -= OnFinalizationProgressChanged;
            _session.Dispose();
            _session = null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static string? GetWebcamDeviceId()
    {
        var savedId = AppSettings.Instance.WebcamDeviceId;
        return string.IsNullOrWhiteSpace(savedId) ? null : savedId;
    }
}
