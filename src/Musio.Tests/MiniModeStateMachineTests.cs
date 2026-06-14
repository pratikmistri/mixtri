using Musio_App.Shell;
using Musio_App.ViewModels;

namespace Musio.Tests;

[TestClass]
public sealed class MiniModeStateMachineTests
{
    [TestMethod]
    [DataRow(CaptureMode.CustomRegion)]
    [DataRow(CaptureMode.Window)]
    [DataRow(CaptureMode.FullScreen)]
    public void Record_FromMiniSetup_EntersMiniRecording_ForAnyCaptureMode(CaptureMode mode)
    {
        var machine = new AppShellStateMachine(AppShellState.MiniSetup);

        var destination = machine.Record(mode);

        Assert.AreEqual(AppShellState.MiniRecording, destination);
        Assert.AreEqual(AppShellState.MiniSetup, machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void StopSucceeded_FromMiniRecording_OpensFullEditorDestination()
    {
        var machine = new AppShellStateMachine(AppShellState.MiniSetup);
        machine.Record(CaptureMode.CustomRegion);

        var destination = machine.StopSucceeded();

        Assert.AreEqual(AppShellState.Full, destination);
        Assert.IsNull(machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void StopFailed_FromMiniRecording_FallsBackToMiniSetupOrigin()
    {
        var machine = new AppShellStateMachine(AppShellState.MiniSetup);
        machine.Record(CaptureMode.Window);

        var destination = machine.StopFailed();

        Assert.AreEqual(AppShellState.MiniSetup, destination);
        Assert.IsNull(machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void Expand_FromMiniSetup_OpensFull()
    {
        var destination = AppShellStateMachine.NextState(
            AppShellState.MiniSetup,
            AppShellEvent.MiniSetupExpand,
            new());

        Assert.AreEqual(AppShellState.Full, destination);
    }

    [TestMethod]
    public void Record_FromFull_EntersFullRecordingAndCapturesOrigin()
    {
        var machine = new AppShellStateMachine(AppShellState.Full);

        var destination = machine.Record(CaptureMode.FullScreen);

        Assert.AreEqual(AppShellState.FullRecording, destination);
        Assert.AreEqual(AppShellState.Full, machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void StopSucceeded_FromFullRecording_OpensFullEditorDestination()
    {
        var machine = new AppShellStateMachine(AppShellState.Full);
        machine.Record(CaptureMode.CustomRegion);

        var destination = machine.StopSucceeded();

        Assert.AreEqual(AppShellState.Full, destination);
        Assert.IsNull(machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void StopFailed_FromFullRecording_FallsBackToFullOrigin()
    {
        var machine = new AppShellStateMachine(AppShellState.Full);
        machine.Record(CaptureMode.Window);

        var destination = machine.StopFailed();

        Assert.AreEqual(AppShellState.Full, destination);
        Assert.IsNull(machine.OriginStateBeforeRecording);
    }

    [TestMethod]
    public void EscDismiss_FromMiniSetup_IsAllowedOnlyAfterRecentSummonAndNoPicker()
    {
        var machine = new AppShellStateMachine(AppShellState.MiniSetup)
        {
            WasRecentlySummoned = true,
            IsPickerOpen = false,
        };

        Assert.IsTrue(machine.TryDismissMiniSetup());
    }

    [TestMethod]
    public void EscDismiss_FromMiniSetup_IsBlockedWhilePickerOpen()
    {
        var machine = new AppShellStateMachine(AppShellState.MiniSetup)
        {
            WasRecentlySummoned = true,
            IsPickerOpen = true,
        };

        Assert.IsFalse(machine.TryDismissMiniSetup());
    }

    [TestMethod]
    public void StopFailed_WithoutOrigin_UsesStateFallback()
    {
        var machine = new AppShellStateMachine(AppShellState.FullRecording);

        var destination = machine.StopFailed();

        Assert.AreEqual(AppShellState.Full, destination);
    }
}
