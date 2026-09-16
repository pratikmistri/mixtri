using Mixtri.Core.Models;
using Mixtri.Core.Settings;

namespace Mixtri.Core.Shell;

public enum ShellProcessCommand
{
    Ping,
    ActivateEditor,
    ShowMini,
    StartRecording,
    SuspendEditor,
    RecordingCompleted,
    RecordingFailed,
    RecordingRedirected,
    ParkEditor,
    UpdateRecordingOptions,
    CloseWorkspace,
    ExitRecorder,
}

public sealed record RemoteRecordingOptions(
    string CaptureMode, long WindowHandle, string WindowTitle, CaptureRegion? Region,
    int Fps, bool SystemAudio, bool Microphone, bool Webcam,
    string WebcamDeviceId, CaptureQuality Quality)
{
    public string WindowProcessName { get; init; } = "";
    public string? WindowExecutablePath { get; init; }
    public int WindowX { get; init; }
    public int WindowY { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
}

public sealed record ShellProcessRequest
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public ShellProcessCommand Command { get; init; }
    public Guid EditorId { get; init; }
    public Guid? RedirectedFromEditorId { get; init; }
    public int? ExpectedProcessId { get; init; }
    public RemoteRecordingOptions? Recording { get; init; }
    public Project? Project { get; init; }
    public Guid? AppendToProjectId { get; init; }
    public string? Message { get; init; }
    public bool OpenEditor { get; init; }
}

public sealed record ShellProcessResponse(
    bool Success, string? Error = null, bool HotkeyRegistered = false, int ProcessId = 0)
{
    public bool OutcomeUnknown { get; init; }

    /// <summary>
    /// Recorder-only, reported on Ping: whether capture is currently running. Lets an editor
    /// reconcile after a start request whose response was lost, instead of guessing.
    /// </summary>
    public bool IsRecording { get; init; }

    /// <summary>Recorder-only, reported on Ping: a recording handoff is in flight.</summary>
    public bool IsDelivering { get; init; }
}

public static class RecordingDeliveryPolicy
{
    public static string? GetRejection(
        Guid? currentProjectId, bool hasUnrecoverableWork, bool isBusy, Guid? appendToProjectId)
    {
        if (isBusy) return "The editor is busy saving, opening, or exporting a project.";
        if (appendToProjectId.HasValue && currentProjectId != appendToProjectId)
            return "The project selected for Record More is no longer open in this editor.";
        if (!appendToProjectId.HasValue && hasUnrecoverableWork)
            return "This editor has unsaved work. The new recording must open separately.";
        return null;
    }
}
