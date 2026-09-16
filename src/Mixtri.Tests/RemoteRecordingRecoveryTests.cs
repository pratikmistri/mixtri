using Mixtri.Core.Shell;

namespace Mixtri.Tests;

[TestClass]
public sealed class RemoteRecordingRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrFailedStatusNeverRestoresALiveRecorder(bool startConfirmed)
    {
        Assert.AreEqual(RemoteRecordingRecoveryAction.KeepHidden,
            RemoteRecordingRecovery.Evaluate(false, startConfirmed, null));
        Assert.AreEqual(RemoteRecordingRecoveryAction.KeepHidden,
            RemoteRecordingRecovery.Evaluate(false, startConfirmed, new(false)));
    }

    [TestMethod]
    public void IdleStatusDoesNotResolveAnUnacknowledgedStart()
    {
        Assert.AreEqual(RemoteRecordingRecoveryAction.KeepHidden,
            RemoteRecordingRecovery.Evaluate(false, false, new(true)));
    }

    [TestMethod]
    public void ConfirmedStartFollowedByIdleMayRestore()
    {
        Assert.AreEqual(RemoteRecordingRecoveryAction.RecordingEnded,
            RemoteRecordingRecovery.Evaluate(false, true, new(true)));
    }

    [TestMethod]
    public void CaptureAndDeliveryRemainParked()
    {
        Assert.AreEqual(RemoteRecordingRecoveryAction.KeepHidden,
            RemoteRecordingRecovery.Evaluate(false, true, new(true) { IsRecording = true }));
        Assert.AreEqual(RemoteRecordingRecoveryAction.KeepHidden,
            RemoteRecordingRecovery.Evaluate(false, true, new(true) { IsDelivering = true }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConfirmedProcessExitAllowsRecoveryWithoutAPipeReply(bool startConfirmed)
    {
        Assert.AreEqual(RemoteRecordingRecoveryAction.RecorderExited,
            RemoteRecordingRecovery.Evaluate(true, startConfirmed, null));
    }

    [TestMethod]
    public async Task StartRetryCannotReachAReplacementProcessWithTheSamePipeName()
    {
        string name = "mixtri-recorder-identity-" + Guid.NewGuid().ToString("N");
        int handled = 0;
        using var server = new ShellProcessPipe(name, _ =>
        {
            Interlocked.Increment(ref handled);
            return Task.FromResult(new ShellProcessResponse(true));
        });
        var request = new ShellProcessRequest
        {
            Command = ShellProcessCommand.StartRecording,
            ExpectedProcessId = Environment.ProcessId ^ 1,
        };
        await Assert.ThrowsExceptionAsync<IOException>(() => ShellProcessPipe.SendAsync(name, request));
        Assert.AreEqual(0, handled, "Identity must be checked before sending a start request.");

        var response = await ShellProcessPipe.SendAsync(name, request with { ExpectedProcessId = Environment.ProcessId });
        Assert.IsTrue(response.Success);
        Assert.AreEqual(1, handled);
    }
}
