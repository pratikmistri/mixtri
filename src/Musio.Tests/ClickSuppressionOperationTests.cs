using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Disabling a recorded click is a single flag (<see cref="TimelineModel.SuppressedClickTicks"/>)
/// shared by three consumers — the auto-zoom engine, the click ripple, and the cursor-path
/// protection — and by two ROUTES: this operation, and the implicit suppression that happens
/// when an auto-zoom's generated segment is first edited or deleted. These tests pin the
/// interaction between those two routes, which is where the sharing can lose information.
/// </summary>
[TestClass]
public sealed class ClickSuppressionOperationTests
{
    private const long ClickTicks = 20_000_000L;

    private static TimelineModel Model() => new() { Duration = TimeSpan.FromSeconds(10) };

    [TestMethod]
    public void Suppress_AddsTheTick()
    {
        var model = Model();

        new SetClickSuppressedOperation(ClickTicks, suppress: true).Execute(model);

        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Suppress_Undo_RemovesTheTick()
    {
        var model = Model();
        var op = new SetClickSuppressedOperation(ClickTicks, suppress: true);

        op.Execute(model);
        op.Undo(model);

        Assert.IsFalse(model.SuppressedClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Restore_RemovesTheTick()
    {
        var model = Model();
        model.SuppressedClickTicks.Add(ClickTicks);

        new SetClickSuppressedOperation(ClickTicks, suppress: false).Execute(model);

        Assert.IsFalse(model.SuppressedClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Restore_Undo_PutsTheTickBack()
    {
        var model = Model();
        model.SuppressedClickTicks.Add(ClickTicks);
        var op = new SetClickSuppressedOperation(ClickTicks, suppress: false);

        op.Execute(model);
        op.Undo(model);

        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks));
    }

    /// <summary>
    /// Asking for the state the model is already in must not reach the undo stack, or Ctrl+Z
    /// appears to do nothing.
    /// </summary>
    [TestMethod]
    public void Suppress_WhenAlreadySuppressed_ReportsNoChange()
    {
        var model = Model();
        model.SuppressedClickTicks.Add(ClickTicks);

        var op = new SetClickSuppressedOperation(ClickTicks, suppress: true);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel);
    }

    [TestMethod]
    public void Restore_WhenNotSuppressed_ReportsNoChange()
    {
        var model = Model();

        var op = new SetClickSuppressedOperation(ClickTicks, suppress: false);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel);
    }

    /// <summary>
    /// The load-bearing case. The click was ALREADY suppressed because the user had deleted its
    /// auto-zoom segment; a redundant explicit suppress is then undone. Undo must restore the
    /// PREVIOUS membership, not unconditionally remove the tick — otherwise undoing this action
    /// silently revives an auto-zoom the user removed by a completely different route.
    /// </summary>
    [TestMethod]
    public void Undo_RestoresPreviousMembership_NotUnconditionalRemoval()
    {
        var model = Model();

        // Route 1: the auto-zoom's own deletion suppressed the click.
        var keyframe = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            IsManual = false,
            SourceClickTicks = ClickTicks,
        };
        model.ZoomKeyframes.Add(keyframe);
        new RemoveZoomKeyframeOperation(keyframe.Id).Execute(model);
        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks), "precondition");

        // Route 2: an explicit suppress that changes nothing, then undone.
        var op = new SetClickSuppressedOperation(ClickTicks, suppress: true);
        op.Execute(model);
        op.Undo(model);

        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks),
            "Undoing a redundant suppress must not revive an auto-zoom removed by deleting its segment");
    }

    /// <summary>
    /// Two different clicks are independent; suppressing one must not disturb the other.
    /// </summary>
    [TestMethod]
    public void SuppressingOneClick_LeavesOthersAlone()
    {
        var model = Model();
        const long other = 90_000_000L;
        model.SuppressedClickTicks.Add(other);

        new SetClickSuppressedOperation(ClickTicks, suppress: true).Execute(model);

        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks));
        Assert.IsTrue(model.SuppressedClickTicks.Contains(other));
    }
}
