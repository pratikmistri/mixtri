using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Disabling a recorded click writes to <see cref="TimelineModel.DisabledClickTicks"/>, which
/// is deliberately SEPARATE from <see cref="TimelineModel.SuppressedClickTicks"/>. The two
/// answer different questions — "was this click's auto-zoom cancelled?" versus "did the user
/// disable this click?" — and briefly sharing one set retroactively disabled every click in
/// every existing project, because those projects accumulate suppressed ticks from ordinary
/// auto-zoom editing. These tests pin the separation.
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

        Assert.IsTrue(model.DisabledClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Suppress_Undo_RemovesTheTick()
    {
        var model = Model();
        var op = new SetClickSuppressedOperation(ClickTicks, suppress: true);

        op.Execute(model);
        op.Undo(model);

        Assert.IsFalse(model.DisabledClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Restore_RemovesTheTick()
    {
        var model = Model();
        model.DisabledClickTicks.Add(ClickTicks);

        new SetClickSuppressedOperation(ClickTicks, suppress: false).Execute(model);

        Assert.IsFalse(model.DisabledClickTicks.Contains(ClickTicks));
    }

    [TestMethod]
    public void Restore_Undo_PutsTheTickBack()
    {
        var model = Model();
        model.DisabledClickTicks.Add(ClickTicks);
        var op = new SetClickSuppressedOperation(ClickTicks, suppress: false);

        op.Execute(model);
        op.Undo(model);

        Assert.IsTrue(model.DisabledClickTicks.Contains(ClickTicks));
    }

    /// <summary>
    /// Asking for the state the model is already in must not reach the undo stack, or Ctrl+Z
    /// appears to do nothing.
    /// </summary>
    [TestMethod]
    public void Suppress_WhenAlreadySuppressed_ReportsNoChange()
    {
        var model = Model();
        model.DisabledClickTicks.Add(ClickTicks);

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
    /// The regression this separation exists for. Deleting an auto-zoom segment suppresses its
    /// source click so the engine stops regenerating it — an ordinary edit that every project
    /// accumulates. That must NOT disable the click: the ripple and the cursor-path protection
    /// belong to the click, not to the zoom it happened to generate.
    /// </summary>
    [TestMethod]
    public void DeletingAnAutoZoom_SuppressesItsZoom_ButDoesNotDisableTheClick()
    {
        var model = Model();
        var keyframe = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            IsManual = false,
            SourceClickTicks = ClickTicks,
        };
        model.ZoomKeyframes.Add(keyframe);

        new RemoveZoomKeyframeOperation(keyframe.Id).Execute(model);

        Assert.IsTrue(model.SuppressedClickTicks.Contains(ClickTicks),
            "the auto-zoom must stay suppressed so the engine does not regenerate it");
        Assert.IsFalse(model.DisabledClickTicks.Contains(ClickTicks),
            "deleting a zoom must never disable the click that generated it");
    }

    /// <summary>
    /// Backward compatibility, stated directly: a project restored with suppressed ticks and no
    /// disabled ticks — which is every project saved before this feature — has no disabled
    /// clicks at all.
    /// </summary>
    [TestMethod]
    public void ProjectWithOnlyLegacySuppressions_HasNoDisabledClicks()
    {
        var model = Model();
        model.SuppressedClickTicks.Add(ClickTicks);
        model.SuppressedClickTicks.Add(90_000_000L);
        model.SuppressedClickTicks.Add(150_000_000L);

        Assert.AreEqual(0, model.DisabledClickTicks.Count);
    }

    /// <summary>
    /// The two sets are independent in the other direction too: disabling a click must not
    /// quietly write into the auto-zoom suppression set, or undoing the disable would leave a
    /// suppression behind that nothing can now remove.
    /// </summary>
    [TestMethod]
    public void DisablingAClick_DoesNotWriteToTheZoomSuppressionSet()
    {
        var model = Model();

        var op = new SetClickSuppressedOperation(ClickTicks, suppress: true);
        op.Execute(model);

        Assert.IsTrue(model.DisabledClickTicks.Contains(ClickTicks));
        Assert.AreEqual(0, model.SuppressedClickTicks.Count);

        op.Undo(model);
        Assert.AreEqual(0, model.DisabledClickTicks.Count);
        Assert.AreEqual(0, model.SuppressedClickTicks.Count);
    }

    /// <summary>
    /// Two different clicks are independent; suppressing one must not disturb the other.
    /// </summary>
    [TestMethod]
    public void SuppressingOneClick_LeavesOthersAlone()
    {
        var model = Model();
        const long other = 90_000_000L;
        model.DisabledClickTicks.Add(other);

        new SetClickSuppressedOperation(ClickTicks, suppress: true).Execute(model);

        Assert.IsTrue(model.DisabledClickTicks.Contains(ClickTicks));
        Assert.IsTrue(model.DisabledClickTicks.Contains(other));
    }
}
