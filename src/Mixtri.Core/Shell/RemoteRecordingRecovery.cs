namespace Mixtri.Core.Shell;

public enum RemoteRecordingRecoveryAction
{
    KeepHidden,
    RecorderExited,
    RecordingEnded,
}

public static class RemoteRecordingRecovery
{
    public static RemoteRecordingRecoveryAction Evaluate(
        bool recorderExited, bool startConfirmed, ShellProcessResponse? status)
    {
        if (recorderExited) return RemoteRecordingRecoveryAction.RecorderExited;
        if (startConfirmed && status is { Success: true, IsRecording: false, IsDelivering: false })
            return RemoteRecordingRecoveryAction.RecordingEnded;
        return RemoteRecordingRecoveryAction.KeepHidden;
    }
}
