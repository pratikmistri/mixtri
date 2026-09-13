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
    private readonly DispatcherQueue _dispatcher;
    private readonly string _scope;
    private readonly RecordingHandoffStore _handoffs;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Dictionary<Guid, Task<ShellProcessResponse>> _requests = new();
    private ShellProcessPipe? _pipe;
    private Guid? _primaryEditor;
    private Process? _primaryProcess;
    private Guid? _recordingEditor;
    private Guid? _appendToProject;
    private bool _delivering;
    private ShellProcessRequest? _pendingRecording;
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
                true, HotkeyRegistered: App.Current.IsMiniHotkeyRegistered, ProcessId: Environment.ProcessId));
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
                    _requests.Add(request.Id, operation);
                    try { owner.SetResult(await HandleAsync(request)); }
                    catch (Exception ex)
                    {
                        DiagLog.Write("ShellProcess", $"{request.Command} failed: {ex}");
                        owner.SetResult(new(false, ex.Message));
                    }
                    if (!(await operation).Success) _requests.Remove(request.Id);
                }
                completion.SetResult(await operation);
            }
            catch (Exception ex) { completion.SetResult(new(false, ex.Message)); }
        }))
            completion.SetResult(new(false, "The application dispatcher is shutting down."));
        return completion.Task;
    }

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
                IsRemoteRecording = true;
                await shell.SuspendForExternalRecordingAsync();
                return new(true);
            case ShellProcessCommand.ParkEditor:
                if (ProjectSaveCoordinator.IsPromptActive) return new(false, "A save prompt is open.");
                await FlushRecordingOptionsAsync();
                await shell.ParkFullWindowAsync();
                return new(true);
            case ShellProcessCommand.RecordingFailed:
                IsRemoteRecording = false;
                shell.ShowFullWindow();
                ShowError(request.Message ?? "The recording could not be completed.");
                return new(true);
            case ShellProcessCommand.RecordingRedirected:
                IsRemoteRecording = false;
                (App.Current.MainAppWindow as MainWindow)?.ShowShellMessage(
                    request.Message ?? "The new recording was opened separately.", InfoBarSeverity.Informational);
                return new(true);
            case ShellProcessCommand.RecordingCompleted:
                if (request.Project is not { } recording)
                    return new(false, "No recording was supplied.");
                var projects = ProjectService.Instance;
                string? rejection = RecordingDeliveryPolicy.GetRejection(
                    projects.CurrentProject?.Id, projects.HasUnrecoverableWork,
                    App.Current.IsProjectOperationInFlight, request.AppendToProjectId);
                if (rejection is not null) return new(false, rejection);
                if (!File.Exists(recording.VideoFilePath))
                    return new(false, "The completed recording's video file is missing.");
                IsRemoteRecording = false;
                if (request.AppendToProjectId.HasValue) projects.AppendRecording(recording);
                else projects.SetProject(recording);
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
                await _handoffs.SaveAsync(pendingRecording);
                await DeliverAsync(pendingRecording);
                _pendingRecording = null;
                RecordingViewModel.Shared.ReleaseLastProject();
                recovered = true;
            }
            foreach (var pending in _handoffs.ReadPending())
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
            && (_primaryProcess is null || !_primaryProcess.HasExited)
            && await TrySendAsync(EditorPipe(existing), new() { Command = ShellProcessCommand.Ping }) is { Success: true } response)
        {
            AllowSetForegroundWindow(response.ProcessId);
            return existing;
        }

        var id = Guid.NewGuid();
        _primaryProcess?.Dispose();
        _primaryProcess = Launch(EditorProcessLaunch.Arguments(id, startInEditor: startInEditor));
        _primaryEditor = id;
        RequireSuccess(await ShellProcessPipe.SendAsync(EditorPipe(id), new() { Command = ShellProcessCommand.Ping }));
        return id;
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

    public async Task StartRemoteRecordingAsync()
    {
        if (IsRemoteRecording) return;
        try
        {
            await EnsureRecorderAsync();
            var model = RecordingViewModel.Shared;
            var request = new ShellProcessRequest
            {
                Command = ShellProcessCommand.StartRecording,
                EditorId = EditorId,
                Recording = model.GetRemoteOptions(),
                AppendToProjectId = model.IsAppendMode ? ProjectService.Instance.CurrentProject?.Id : null,
            };
            IsRemoteRecording = true;
            await ShellCoordinator.Instance!.SuspendForExternalRecordingAsync();
            RequireSuccess(await ShellProcessPipe.SendAsync(RecorderPipe, request));
        }
        catch (Exception ex)
        {
            IsRemoteRecording = false;
            ShellCoordinator.Instance?.ShowFullWindow();
            ReportError("Could not start recording", ex);
        }
    }

    public async Task PrepareLocalRecordingAsync()
    {
        if (_recordingEditor.HasValue || !_primaryEditor.HasValue) return;
        var response = await TrySendAsync(EditorPipe(_primaryEditor.Value),
            new() { Command = ShellProcessCommand.SuspendEditor });
        if (response is { Success: false }) RequireSuccess(response);
    }

    public async Task ParkWorkspaceAsync()
    {
        if (_primaryEditor is { } editor && (_primaryProcess is null || !_primaryProcess.HasExited))
            await TrySendAsync(EditorPipe(editor), new() { Command = ShellProcessCommand.ParkEditor });
    }

    public async Task CompleteRecordingAsync(Project? project)
    {
        _delivering = true;
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
            _pendingRecording = request;
            await _handoffs.SaveAsync(request);
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
        }
    }

    private async Task DeliverAsync(ShellProcessRequest request)
    {
        ShellProcessResponse? response = null;
        if (request.EditorId != Guid.Empty)
            response = await TrySendAsync(EditorPipe(request.EditorId), request, TimeSpan.FromSeconds(15));
        if (response is not { Success: true })
        {
            var editor = await EnsureEditorAsync(newSession: true, startInEditor: true);
            var separate = request with
            {
                AppendToProjectId = null,
                Message = request.AppendToProjectId.HasValue
                    ? "The original editor could not accept Record More. Your new recording was opened separately; the original project was not changed."
                    : "The previous editor could not accept this recording, so it was opened separately without replacing existing edits.",
            };
            RequireSuccess(await ShellProcessPipe.SendAsync(EditorPipe(editor), separate));
            if (request.EditorId != Guid.Empty)
                await TrySendAsync(EditorPipe(request.EditorId), new()
                {
                    Command = ShellProcessCommand.RecordingRedirected,
                    Message = "The new recording was opened in a separate editor to preserve this project.",
                });
        }
        _handoffs.Acknowledge(request.Id);
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
        string pipe, ShellProcessRequest request, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(1));
        try { return await ShellProcessPipe.SendAsync(pipe, request, cancellation.Token); }
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
        _pipe?.Dispose();
        _primaryProcess?.Dispose();
    }
}
