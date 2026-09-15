using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Models;
using Mixtri.Core.Shell;
using Mixtri_App.Pages;
using Mixtri_App.ViewModels;

namespace Mixtri_App.Services;

/// <summary>Owns the process boundary; the recorder never constructs an editor or loads its media.</summary>
public sealed class EditorProcessCoordinator : IDisposable
{
    /// <summary>How long a delivery attempt waits for the editor to apply the recording.</summary>
    private static readonly TimeSpan DeliverTimeout = TimeSpan.FromSeconds(15);
    private const int DeliverRetries = 2;
    private static readonly TimeSpan DeliverRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long capture waits for the editor to confirm its window is hidden.</summary>
    private static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a StartRecording request waits for the recorder to accept it.</summary>
    private static readonly TimeSpan StartRecordingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cadence and tolerance for the editor's watch on the recorder during remote capture.</summary>
    private static readonly TimeSpan RecorderHeartbeatInterval = TimeSpan.FromSeconds(2);
    private const int RecorderHeartbeatFailuresBeforeRecovery = 3;

    /// <summary>
    /// Upper bound on remembered request results. Entries exist only so a retried request id
    /// replays its outcome instead of re-applying it, so a small recent window is enough.
    /// </summary>
    private const int MaxRememberedRequests = 64;

    private readonly DispatcherQueue _dispatcher;
    private readonly string _scope;
    private readonly RecordingHandoffStore _handoffs;
    private readonly RecordingApplication _recordingApplications = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Dictionary<Guid, Task<ShellProcessResponse>> _requests = new();
    private readonly Queue<Guid> _requestOrder = new();
    private ShellProcessPipe? _pipe;
    private Guid? _primaryEditor;
    private Process? _primaryProcess;
    private Guid? _recordingEditor;
    private Guid? _appendToProject;
    private bool _delivering;
    private ShellProcessRequest? _pendingRecording;
    private CancellationTokenSource? _recorderWatch;
    private int _remoteRecordingStarting;
    private bool _disposed;

    public bool IsRecorder { get; }
    public Guid EditorId { get; }
    public bool IsRemoteRecording { get; private set; }
    public bool IsBusy => _delivering || _pendingRecording is not null;

    public EditorProcessCoordinator(DispatcherQueue dispatcher, bool isEditor, Guid? editorId)
    {
        _dispatcher = dispatcher;
        IsRecorder = !isEditor;
        EditorId = editorId ?? Guid.NewGuid();
        string identity;
        try { identity = Windows.ApplicationModel.Package.Current.Id.FamilyName; }
        catch (InvalidOperationException) { identity = AppContext.BaseDirectory; }
        using var process = Process.GetCurrentProcess();
        _scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity}|{process.SessionId}")))[..24];
        _handoffs = new RecordingHandoffStore(Mixtri.Core.AppDataPaths.Resolve("EditorHandoffs", _scope));
    }

    private string RecorderPipe => $"Mixtri-{_scope}-recorder";
    private string EditorPipe(Guid id) => $"Mixtri-{_scope}-editor-{id:N}";

    public void Start()
    {
        _pipe = new ShellProcessPipe(IsRecorder ? RecorderPipe : EditorPipe(EditorId), DispatchAsync);
        DiagLog.Write("ShellProcess", $"PID {Environment.ProcessId}: {(IsRecorder ? "recorder" : "editor")} ready ({EditorId:N})");
    }

    private Task<ShellProcessResponse> DispatchAsync(ShellProcessRequest request)
    {
        if (request.Command == ShellProcessCommand.Ping)
            return Task.FromResult(new ShellProcessResponse(
                true, HotkeyRegistered: App.Current.IsMiniHotkeyRegistered, ProcessId: Environment.ProcessId)
            {
                // Let a caller whose StartRecording response was lost reconcile the real state.
                IsRecording = IsRecorder && (RecordingViewModel.Shared.IsRecording
                    || ShellCoordinator.Instance?.CurrentState == AppShellState.Recording),
                IsDelivering = IsRecorder && IsBusy,
            });
        var completion = new TaskCompletionSource<ShellProcessResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (_disposed) throw new InvalidOperationException("This window is closing.");
                if (!_requests.TryGetValue(request.Id, out var operation))
                {
                    var owner = new TaskCompletionSource<ShellProcessResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation = owner.Task;
                    Remember(request.Id, operation);
                    try { owner.SetResult(await HandleAsync(request)); }
                    catch (Exception ex)
                    {
                        DiagLog.Write("ShellProcess", $"{request.Command} failed: {ex}");
                        owner.SetResult(new(false, ex.Message));
                    }
                    if (!(await operation).Success) Forget(request.Id);
                }
                completion.SetResult(await operation);
            }
            catch (Exception ex) { completion.SetResult(new(false, ex.Message)); }
        }))
            completion.SetResult(new(false, "The application dispatcher is shutting down."));
        return completion.Task;
    }

    /// <summary>
    /// Records a request outcome so a retry of the same id replays it rather than re-applying
    /// the side effect. Bounded: a resident process would otherwise accumulate an entry for
    /// every activation, park, option change and recording for its whole lifetime.
    /// Runs on the dispatcher, so no additional locking is needed.
    /// </summary>
    /// <remarks>
    /// Only SETTLED results are evictable. An entry whose handler is still running is the
    /// very thing that makes a retried id wait for the original outcome instead of starting
    /// <see cref="HandleAsync"/> a second time; dropping it mid-flight would let a delivery
    /// retry apply the recording twice. The cache can therefore exceed its bound while many
    /// requests are genuinely in flight, which is inherently self-limiting.
    /// </remarks>
    private void Remember(Guid id, Task<ShellProcessResponse> operation)
    {
        _requests[id] = operation;
        _requestOrder.Enqueue(id);
        if (_requestOrder.Count <= MaxRememberedRequests) return;

        for (int scanned = 0, scan = _requestOrder.Count;
             scanned < scan && _requestOrder.Count > MaxRememberedRequests;
             scanned++)
        {
            var oldest = _requestOrder.Dequeue();
            if (!_requests.TryGetValue(oldest, out var settled)) continue;
            if (settled.IsCompleted) _requests.Remove(oldest);
            else _requestOrder.Enqueue(oldest);
        }
    }

    private void Forget(Guid id) => _requests.Remove(id);

    private async Task<ShellProcessResponse> HandleAsync(ShellProcessRequest request)
    {
        var shell = ShellCoordinator.Instance ?? throw new InvalidOperationException("The shell is not ready.");
        if (IsRecorder)
        {
            switch (request.Command)
            {
                case ShellProcessCommand.ExitRecorder:
                    if (request.EditorId != _primaryEditor || shell.CurrentState == AppShellState.Recording || IsBusy)
                        return new(false, "The recorder still has active work.");
                    App.Current.BeginQuiesce();
                    return new(true);
                case ShellProcessCommand.ShowMini:
                    if (request.Recording is not null && shell.CurrentState != AppShellState.Recording)
                        RecordingViewModel.Shared.ApplyRemoteOptions(request.Recording);
                    if (request.EditorId != Guid.Empty && _primaryEditor != request.EditorId)
                    {
                        _primaryProcess?.Dispose();
                        _primaryProcess = null;
                        _primaryEditor = request.EditorId;
                    }
                    shell.ActivateFromTray(parkWorkspace: false);
                    return new(true, HotkeyRegistered: App.Current.IsMiniHotkeyRegistered);
                case ShellProcessCommand.UpdateRecordingOptions:
                    if (request.Recording is null || shell.CurrentState == AppShellState.Recording)
                        return new(false, "Recording settings cannot be changed during capture.");
                    RecordingViewModel.Shared.ApplyRemoteOptions(request.Recording);
                    return new(true);
                case ShellProcessCommand.StartRecording:
                    if (request.EditorId == Guid.Empty || request.Recording is null)
                        return new(false, "The recording request is incomplete.");
                    if (shell.CurrentState == AppShellState.Recording || IsBusy)
                        return new(false, "Another recording or recording handoff is already in progress.");
                    RecordingViewModel.Shared.ApplyRemoteOptions(request.Recording);
                    _recordingEditor = request.EditorId;
                    _appendToProject = request.AppendToProjectId;
                    await shell.StartRecordingAsync();
                    if (!RecordingViewModel.Shared.IsRecording)
                    {
                        _recordingEditor = null;
                        _appendToProject = null;
                        return new(false, RecordingViewModel.Shared.RecordingStatus);
                    }
                    return new(true);
                default:
                    return new(false, "This command requires an editor process.");
            }
        }

        switch (request.Command)
        {
            case ShellProcessCommand.CloseWorkspace:
                return App.Current.RequestWorkspaceExit()
                    ? new(true)
                    : new(false, "Finish the current project operation or dialog before exiting.");
            case ShellProcessCommand.ActivateEditor:
                if (IsRemoteRecording) return new(false, "A recording is in progress.");
                if (request.Recording is not null)
                    RecordingViewModel.Shared.ApplyRemoteOptions(request.Recording);
                if (request.OpenEditor) (App.Current.MainAppWindow as MainWindow)?.ShowEditor();
                shell.ShowFullWindow();
                await shell.FullWindowPresentation;
                return new(true);
            case ShellProcessCommand.SuspendEditor:
                if (ProjectSaveCoordinator.IsPromptActive)
                    return new(false, "Finish the editor's save prompt before recording.");
                Process? recorder = await OpenRecorderProcessAsync();
                try
                {
                    IsRemoteRecording = true;
                    await shell.SuspendForExternalRecordingAsync();
                    StartRecorderWatch(recorder);
                    recorder = null;
                    return new(true);
                }
                catch
                {
                    IsRemoteRecording = false;
                    shell.ShowFullWindow();
                    throw;
                }
                finally { recorder?.Dispose(); }
            case ShellProcessCommand.ParkEditor:
                if (ProjectSaveCoordinator.IsPromptActive) return new(false, "A save prompt is open.");
                await FlushRecordingOptionsAsync();
                await shell.ParkFullWindowAsync();
                return new(true);
            case ShellProcessCommand.RecordingFailed:
                IsRemoteRecording = false;
                StopRecorderWatch();
                shell.ShowFullWindow();
                ShowError(request.Message ?? "The recording could not be completed.");
                return new(true);
            case ShellProcessCommand.RecordingRedirected:
                IsRemoteRecording = false;
                StopRecorderWatch();
                (App.Current.MainAppWindow as MainWindow)?.ShowShellMessage(
                    request.Message ?? "The new recording was opened separately.", InfoBarSeverity.Informational);
                return new(true);
            case ShellProcessCommand.RecordingCompleted:
                if (request.Project is not { } recording)
                    return new(false, "No recording was supplied.");
                var projects = ProjectService.Instance;
                string receiptDirectory = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(recording.VideoFilePath))!, ".handoff-receipts");
                var applied = _recordingApplications.Apply(request, receiptDirectory,
                    () => RecordingDeliveryPolicy.GetRejection(
                        projects.CurrentProject?.Id, projects.HasUnrecoverableWork,
                        App.Current.IsProjectOperationInFlight, request.AppendToProjectId)
                        ?? (File.Exists(recording.VideoFilePath) ? null : "The completed recording's video file is missing."),
                    () =>
                    {
                        if (request.AppendToProjectId.HasValue) projects.AppendRecording(recording);
                        else projects.SetProject(recording);
                    });
                IsRemoteRecording = false;
                StopRecorderWatch();
                if (!applied.Success)
                {
                    if (applied.OutcomeUnknown)
                    {
                        shell.ShowFullWindow();
                        ShowError(applied.Error ?? "The recording delivery needs recovery.");
                    }
                    return applied;
                }
                RecordingViewModel.Shared.IsAppendMode = false;
                shell.ShowFullWindow();
                (App.Current.MainAppWindow as MainWindow)?.ShowEditor();
                if (request.Message is not null)
                    (App.Current.MainAppWindow as MainWindow)?.ShowShellMessage(request.Message, InfoBarSeverity.Informational);
                return new(true);
            default:
                return new(false, "This command requires the resident recorder.");
        }
    }

    public Task OpenEditorAsync() => OpenWorkspaceAsync(openEditor: true);

    public async Task OpenWorkspaceAsync(bool openEditor = false)
    {
        await _openGate.WaitAsync();
        try
        {
            bool recovered = false;
            if (_pendingRecording is { } pendingRecording)
            {
                await PersistPendingRecordingAsync(pendingRecording);
                await DeliverAsync(pendingRecording);
                _pendingRecording = null;
                RecordingViewModel.Shared.ReleaseLastProject();
                recovered = true;
            }
            foreach (var pending in await _handoffs.ReadPendingAsync())
            {
                await DeliverAsync(pending);
                recovered = true;
            }
            if (!recovered)
            {
                var editor = await EnsureEditorAsync(startInEditor: openEditor);
                RequireSuccess(await ShellProcessPipe.SendAsync(EditorPipe(editor),
                    new()
                    {
                        Command = ShellProcessCommand.ActivateEditor, OpenEditor = openEditor,
                        Recording = RecordingViewModel.Shared.GetRemoteOptions(),
                    }));
            }
            ShellCoordinator.Instance?.HideToTray();
        }
        catch (Exception ex) { ReportError("Could not open the editor", ex); }
        finally { _openGate.Release(); }
    }

    public Task OpenPackageAsync(string path)
    {
        // File-key redirection is handled by App before this child creates any windows.
        using var process = Launch(EditorProcessLaunch.Arguments(Guid.NewGuid(), path));
        ShellCoordinator.Instance?.HideToTray();
        return Task.CompletedTask;
    }

    private async Task<Guid> EnsureEditorAsync(bool newSession = false, bool startInEditor = false)
    {
        if (!newSession && _primaryEditor is { } existing
            && _primaryProcess is not { HasExited: true })
        {
            var response = await TrySendAsync(EditorPipe(existing), new() { Command = ShellProcessCommand.Ping });
            if (response is { Success: true })
            {
                AllowSetForegroundWindow(response.ProcessId);
                return existing;
            }
            if (_primaryProcess is not { HasExited: true })
                throw new InvalidOperationException(
                    "The existing editor did not respond. It has been left running to preserve its work; try opening it again.");
        }

        // A separate session leaves the previous editor alive; an ambiguous ping never
        // authorizes closing or killing it, even when this process launched it.
        ReleasePrimaryProcessHandle();

        var id = Guid.NewGuid();
        _primaryProcess = Launch(EditorProcessLaunch.Arguments(id, startInEditor: startInEditor));
        _primaryEditor = id;
        RequireSuccess(await ShellProcessPipe.SendAsync(EditorPipe(id), new() { Command = ShellProcessCommand.Ping }));
        return id;
    }

    /// <summary>Drops our handle to the current editor without affecting the process itself.</summary>
    private void ReleasePrimaryProcessHandle()
    {
        _primaryProcess?.Dispose();
        _primaryProcess = null;
    }

    private static Process Launch(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate the Mixtri executable."))
        {
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Windows did not start Mixtri.");
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private async Task EnsureRecorderAsync()
    {
        if (await TrySendAsync(RecorderPipe, new() { Command = ShellProcessCommand.Ping }) is { Success: true }) return;
        using var process = Launch(["--background-recorder"]);
        RequireSuccess(await ShellProcessPipe.SendAsync(RecorderPipe, new() { Command = ShellProcessCommand.Ping }));
    }

    private async Task<Process> OpenRecorderProcessAsync()
    {
        var response = await ShellProcessPipe.SendAsync(RecorderPipe, new() { Command = ShellProcessCommand.Ping });
        RequireSuccess(response);
        if (response.ProcessId <= 0)
            throw new InvalidOperationException("The recorder did not identify its process.");
        var process = Process.GetProcessById(response.ProcessId);
        try
        {
            _ = process.SafeHandle; // Retain this process identity rather than reopening a reused PID.
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task ShowMiniAsync()
    {
        try
        {
            await EnsureRecorderAsync();
            (App.Current.MainAppWindow as MainWindow)?.SavePlacement();
            RequireSuccess(await ShellProcessPipe.SendAsync(RecorderPipe,
                new()
                {
                    Command = ShellProcessCommand.ShowMini, EditorId = EditorId,
                    Recording = RecordingViewModel.Shared.GetRemoteOptions(),
                }));
            await ShellCoordinator.Instance!.ParkFullWindowAsync();
        }
        catch (Exception ex) { ReportError("Could not show Mini mode", ex); }
    }

    /// <summary>
    /// Asks the resident recorder to start capturing on this editor's behalf.
    /// </summary>
    /// <remarks>
    /// The transition is claimed with an interlocked flag BEFORE the first await. Checking
    /// <see cref="IsRemoteRecording"/> alone is not a gate: it is only set after the recorder
    /// ping completes, and the editor's own <c>RecordingViewModel.IsRecording</c> never turns
    /// true for remote capture, so the Record button stays live. Two quick clicks could
    /// otherwise both enter, and the loser's rollback would restore the full window into a
    /// recording that the winner had already started.
    /// </remarks>
    public async Task StartRemoteRecordingAsync()
    {
        if (IsRemoteRecording) return;
        if (Interlocked.Exchange(ref _remoteRecordingStarting, 1) == 1) return;
        Process? recorder = null;
        ShellProcessRequest? request = null;
        bool attemptedStart = false;
        try
        {
            await EnsureRecorderAsync();
            recorder = await OpenRecorderProcessAsync();
            var model = RecordingViewModel.Shared;
            request = new ShellProcessRequest
            {
                Command = ShellProcessCommand.StartRecording,
                EditorId = EditorId,
                ExpectedProcessId = recorder.Id,
                Recording = model.GetRemoteOptions(),
                AppendToProjectId = model.IsAppendMode ? ProjectService.Instance.CurrentProject?.Id : null,
            };
            IsRemoteRecording = true;
            await ShellCoordinator.Instance!.SuspendForExternalRecordingAsync();

            attemptedStart = true;
            var response = await TrySendAsync(RecorderPipe, request, StartRecordingTimeout);
            for (int attempt = 0; response is null && attempt < DeliverRetries; attempt++)
            {
                await Task.Delay(DeliverRetryDelay);
                response = await TrySendAsync(RecorderPipe, request, StartRecordingTimeout);
            }

            if (response is { Success: false })
            {
                attemptedStart = false;
                RequireSuccess(response);
            }
            if (!IsRemoteRecording) return; // A completion/failure message may have arrived during the wait.
            if (response is null)
                DiagLog.Write("ShellProcess", "Start outcome is unknown; keeping the editor hidden and retrying the same request.");
            StartRecorderWatch(recorder, response is null ? request : null, startConfirmed: response is { Success: true });
            recorder = null;
        }
        catch (Exception ex)
        {
            if (attemptedStart && IsRemoteRecording && recorder is not null && request is not null)
            {
                DiagLog.Write("ShellProcess", $"Could not confirm recording start; keeping editor hidden: {ex}");
                StartRecorderWatch(recorder, request);
                recorder = null;
                ShowError("The recorder has not confirmed the start. The editor remains hidden while it reconnects.");
            }
            else
            {
                IsRemoteRecording = false;
                StopRecorderWatch();
                ReportError("Could not start recording", ex);
            }
        }
        finally
        {
            recorder?.Dispose();
            Interlocked.Exchange(ref _remoteRecordingStarting, 0);
        }
    }

    /// <summary>Owns the recorder handle and never restores the editor solely because IPC is unavailable.</summary>
    private void StartRecorderWatch(Process recorder, ShellProcessRequest? pendingStart = null, bool startConfirmed = false)
    {
        StopRecorderWatch();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _recorderWatch = cancellation;
        _ = Task.Run(async () =>
        {
            int idleResponses = 0;
            bool unavailableReported = false;
            void Restore(string message)
            {
                if (!_dispatcher.TryEnqueue(() =>
                {
                    if (_disposed || token.IsCancellationRequested || !IsRemoteRecording
                        || !ReferenceEquals(_recorderWatch, cancellation)) return;
                    IsRemoteRecording = false;
                    StopRecorderWatch();
                    ShellCoordinator.Instance?.ShowFullWindow();
                    ShowError(message);
                }))
                    DiagLog.Write("ShellProcess", $"Could not dispatch recording recovery: {message}");
            }
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(RecorderHeartbeatInterval, token);
                    if (recorder.HasExited)
                    {
                        Restore("The recording process exited. Any pending recording will be recovered when the recorder is reopened.");
                        return;
                    }
                    if (pendingStart is not null)
                    {
                        var response = await TrySendAsync(RecorderPipe, pendingStart, StartRecordingTimeout, token);
                        if (response is { Success: false })
                        {
                            Restore(response.Error ?? "The recorder rejected the recording request.");
                            return;
                        }
                        if (response is { Success: true })
                        {
                            startConfirmed = true;
                            pendingStart = null;
                        }
                    }
                    var status = await TrySendAsync(RecorderPipe, new()
                    {
                        Command = ShellProcessCommand.Ping,
                        ExpectedProcessId = recorder.Id,
                    }, ct: token);
                    if (pendingStart is null && status is { Success: true, IsRecording: true })
                        startConfirmed = true;
                    var action = RemoteRecordingRecovery.Evaluate(recorder.HasExited, startConfirmed, status);
                    if (action == RemoteRecordingRecoveryAction.RecorderExited)
                    {
                        Restore("The recording process exited. Reopen the recorder to recover any pending recording.");
                        return;
                    }
                    if (action == RemoteRecordingRecoveryAction.RecordingEnded)
                    {
                        if (++idleResponses >= RecorderHeartbeatFailuresBeforeRecovery)
                        {
                            Restore("The recorder is idle but did not deliver a completion message. Open the recorder to recover any pending recording.");
                            return;
                        }
                    }
                    else idleResponses = 0;
                    if (status is null && !unavailableReported)
                    {
                        DiagLog.Write("ShellProcess", "The recorder is unreachable; keeping the editor hidden because recording may still be active.");
                        unavailableReported = true;
                    }
                    else if (status is { Success: true }) unavailableReported = false;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { DiagLog.Write("ShellProcess", $"Recorder watch failed; editor remains hidden: {ex}"); }
            finally
            {
                recorder.Dispose();
                cancellation.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StopRecorderWatch()
    {
        var watch = _recorderWatch;
        _recorderWatch = null;
        if (watch is null) return;
        try { watch.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Hides the resident editor before local capture begins. A null response means the
    /// editor is alive but did not confirm (timeout or broken pipe) — its window may still
    /// be on screen, so capture must not start. Only an explicit success proceeds.
    /// </summary>
    public async Task PrepareLocalRecordingAsync()
    {
        if (_recordingEditor.HasValue || !_primaryEditor.HasValue) return;
        var response = await TrySendAsync(EditorPipe(_primaryEditor.Value),
            new() { Command = ShellProcessCommand.SuspendEditor }, SuspendTimeout);
        if (response is null)
            throw new InvalidOperationException(
                "The full window did not confirm that it was hidden, so recording was cancelled to avoid capturing it.");
        RequireSuccess(response);
    }

    public async Task ParkWorkspaceAsync()
    {
        if (_primaryEditor is { } editor && (_primaryProcess is null || !_primaryProcess.HasExited))
            await TrySendAsync(EditorPipe(editor), new() { Command = ShellProcessCommand.ParkEditor });
    }

    public async Task CompleteRecordingAsync(Project? project)
    {
        _delivering = true;
        await _openGate.WaitAsync();
        try
        {
            if (project is null)
            {
                await NotifyRecordingFailedAsync();
                return;
            }
            var request = new ShellProcessRequest
            {
                Command = ShellProcessCommand.RecordingCompleted,
                EditorId = _recordingEditor ?? _primaryEditor ?? Guid.Empty,
                AppendToProjectId = _appendToProject,
                Project = project,
            };
            await PersistPendingRecordingAsync(request);
            await DeliverAsync(request);
            _pendingRecording = null;
            RecordingViewModel.Shared.ReleaseLastProject();
            ShellCoordinator.Instance?.HideToTray();
            try { await ReclaimCaptureResourcesAsync(); }
            catch (Exception ex) { DiagLog.Write("Memory", $"Recording opened, but idle capture cleanup failed: {ex}"); }
        }
        catch (Exception ex) { ReportError("Recording kept for retry; open Editor to try again", ex); }
        finally
        {
            _recordingEditor = null;
            _appendToProject = null;
            _delivering = false;
            _openGate.Release();
        }
    }

    private async Task PersistPendingRecordingAsync(ShellProcessRequest request)
    {
        _pendingRecording = request;
        await _handoffs.SaveAsync(request);
    }

    private async Task DeliverAsync(ShellProcessRequest request)
    {
        var delivered = await RecordingDelivery.DeliverAsync(
            request,
            pending => TrySendAsync(EditorPipe(pending.EditorId), pending, DeliverTimeout),
            () => EnsureEditorAsync(newSession: true, startInEditor: true),
            PersistPendingRecordingAsync,
            DeliverRetries,
            DeliverRetryDelay);
        await _handoffs.AcknowledgeAsync(delivered.Id);
        if (_pendingRecording?.Id == delivered.Id)
            _pendingRecording = null;
        if (delivered.RedirectedFromEditorId is { } original)
            await TrySendAsync(EditorPipe(original), new()
            {
                Command = ShellProcessCommand.RecordingRedirected,
                Message = "The new recording was opened in a separate editor to preserve this project.",
            });
    }

    public async Task NotifyRecordingFailedAsync(string? message = null)
    {
        if ((_recordingEditor ?? _primaryEditor) is { } editor)
            await TrySendAsync(EditorPipe(editor), new()
            {
                Command = ShellProcessCommand.RecordingFailed,
                Message = message ?? RecordingViewModel.Shared.RecordingStatus,
            });
        _recordingEditor = null;
        _appendToProject = null;
    }

    private static async Task ReclaimCaptureResourcesAsync()
    {
        await Task.Delay(250);
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
        if (ShellCoordinator.Instance is { CanReleaseCaptureDevice: true })
        {
            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            device.Trim();
            device.Dispose();
            await Task.Run(() => NativeHeapReclaimer.OptimizeUnusedHeaps());
        }
    }

    public async Task FlushRecordingOptionsAsync()
    {
        if (IsRecorder) return;
        var response = await TrySendAsync(RecorderPipe, new()
        {
            Command = ShellProcessCommand.UpdateRecordingOptions,
            Recording = RecordingViewModel.Shared.GetRemoteOptions(),
        });
        if (response is { Success: false })
            DiagLog.Write("ShellProcess", $"Recording settings were not changed: {response.Error}");
    }

    public async Task<bool> RequestWorkspaceCloseAsync()
    {
        if (_primaryEditor is not { } editor || _primaryProcess is { HasExited: true }) return false;
        var response = await TrySendAsync(EditorPipe(editor), new() { Command = ShellProcessCommand.CloseWorkspace });
        if (response is null)
        {
            if (_primaryProcess is not { HasExited: false }) return false;
            ShowError("The full window has not responded to Exit. It and the recorder have been left running.");
            return true;
        }
        if (!response.Success) ShowError(response.Error ?? "The full window could not close.");
        return true;
    }

    public async Task RequestRecorderExitAsync()
    {
        var response = await TrySendAsync(RecorderPipe,
            new() { Command = ShellProcessCommand.ExitRecorder, EditorId = EditorId });
        if (response is { Success: false })
            DiagLog.Write("ShellProcess", $"Recorder remains running: {response.Error}");
    }

    public async Task<bool> IsRecorderHotkeyAvailableAsync()
    {
        var response = await TrySendAsync(RecorderPipe, new() { Command = ShellProcessCommand.Ping });
        return response is { Success: true, HotkeyRegistered: true };
    }

    private static async Task<ShellProcessResponse?> TrySendAsync(
        string pipe, ShellProcessRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(1));
        try { return await ShellProcessPipe.SendAsync(pipe, request, cancellation.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            DiagLog.Write("ShellIPC", $"{request.Command} endpoint unavailable: {ex.Message}");
            return null;
        }
    }

    private static void RequireSuccess(ShellProcessResponse response)
    {
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "The other process rejected this operation.");
    }

    private static void ShowError(string message)
    {
        ShellCoordinator.Instance?.ShowErrorMessage(message);
    }

    private static void ReportError(string message, Exception ex)
    {
        DiagLog.Write("ShellProcess", $"{message}: {ex}");
        if (App.Current.EditorProcesses is { IsRecorder: true }) ShellCoordinator.Instance?.ActivateFromTray();
        else ShellCoordinator.Instance?.ShowFullWindow();
        ShowError($"{message}: {ex.Message}");
    }

    public void Dispose()
    {
        _disposed = true;
        StopRecorderWatch();
        _pipe?.Dispose();
        _primaryProcess?.Dispose();
        _requests.Clear();
        _requestOrder.Clear();
        _openGate.Dispose();
    }
}
