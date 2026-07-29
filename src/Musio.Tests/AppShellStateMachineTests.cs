using Musio.Core.Shell;

namespace Musio.Tests;

[TestClass]
public class AppShellStateMachineTests
{
    [TestMethod]
    public void Expand_MovesMiniToFull()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.Expand, out var state));
        Assert.AreEqual(AppShellState.Full, state);
        Assert.AreEqual(AppShellState.Full, sm.CurrentState);
    }

    [TestMethod]
    public void Collapse_MovesFullToMini()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.Collapse, out var state));
        Assert.AreEqual(AppShellState.Mini, state);
    }

    [TestMethod]
    public void ExpandFromFull_IsIgnored()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);

        Assert.IsFalse(sm.TryApply(AppShellTrigger.Expand, out var state));
        Assert.AreEqual(AppShellState.Full, state);
    }

    [TestMethod]
    public void RecordingStarted_FromMini_HidesBothWindows()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.RecordingStarted, out var state));
        Assert.AreEqual(AppShellState.Recording, state);
        Assert.AreEqual(AppShellState.Mini, sm.RecordingOrigin);
    }

    [TestMethod]
    public void RecordingStopped_FromMini_HandsOffToFull()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.RecordingStopped, out var state));
        Assert.AreEqual(AppShellState.Full, state);
        Assert.IsNull(sm.RecordingOrigin);
    }

    [TestMethod]
    public void RecordingStopped_FromFull_StaysFull()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        sm.TryApply(AppShellTrigger.RecordingStopped, out var state);
        Assert.AreEqual(AppShellState.Full, state);
    }

    [TestMethod]
    public void RecordingFailed_ReturnsToOrigin()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.RecordingFailed, out var state));
        Assert.AreEqual(AppShellState.Mini, state);
        Assert.IsNull(sm.RecordingOrigin);
    }

    [TestMethod]
    public void RecordingFailed_FromFullOrigin_ReturnsToFull()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        sm.TryApply(AppShellTrigger.RecordingFailed, out var state);
        Assert.AreEqual(AppShellState.Full, state);
    }

    [TestMethod]
    public void SecondRecording_DoesNotInheritStaleOrigin()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);
        sm.TryApply(AppShellTrigger.RecordingStopped, out _); // now Full

        sm.TryApply(AppShellTrigger.RecordingStarted, out _);
        Assert.AreEqual(AppShellState.Full, sm.RecordingOrigin);

        sm.TryApply(AppShellTrigger.RecordingFailed, out var state);
        Assert.AreEqual(AppShellState.Full, state);
    }

    [TestMethod]
    public void TrayActivated_OpensMiniFromFull()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);

        Assert.IsTrue(sm.TryApply(AppShellTrigger.TrayActivated, out var state));
        Assert.AreEqual(AppShellState.Mini, state);
    }

    [TestMethod]
    public void TrayActivated_WhileRecording_IsIgnored()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        Assert.IsFalse(sm.TryApply(AppShellTrigger.TrayActivated, out var state));
        Assert.AreEqual(AppShellState.Recording, state);
        Assert.AreEqual(AppShellState.Full, sm.RecordingOrigin);
    }

    [TestMethod]
    public void TrayActivated_WhileAlreadyMini_ReportsNoChange()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);

        Assert.IsFalse(sm.TryApply(AppShellTrigger.TrayActivated, out var state));
        Assert.AreEqual(AppShellState.Mini, state);
    }

    [TestMethod]
    public void CollapseAndExpand_WhileRecording_AreIgnored()
    {
        var sm = new AppShellStateMachine(AppShellState.Full);
        sm.TryApply(AppShellTrigger.RecordingStarted, out _);

        Assert.IsFalse(sm.TryApply(AppShellTrigger.Collapse, out _));
        Assert.IsFalse(sm.TryApply(AppShellTrigger.Expand, out _));
        Assert.AreEqual(AppShellState.Recording, sm.CurrentState);
    }

    [TestMethod]
    public void RecordingStopped_WithoutRecording_IsIgnored()
    {
        var sm = new AppShellStateMachine(AppShellState.Mini);

        Assert.IsFalse(sm.TryApply(AppShellTrigger.RecordingStopped, out var state));
        Assert.AreEqual(AppShellState.Mini, state);
    }
}
