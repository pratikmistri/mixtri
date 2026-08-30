namespace Mixtri.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mixtri.Core.Timeline;

/// <summary>
/// Pins the edit signal that drives the "you have unsaved changes" prompt shown when the
/// window is dismissed. The distinction that matters here is between
/// <see cref="UndoRedoManager.StateChanged"/> — which also fires when a project is loaded and
/// the stacks are cleared — and <see cref="UndoRedoManager.EditPerformed"/>, which must fire
/// only for real user edits. Getting that wrong makes every freshly opened project prompt to
/// be saved before it has been touched.
/// </summary>
[TestClass]
public sealed class UndoRedoEditSignalTests
{
    private sealed class CountingOperation : IEditOperation
    {
        public string Description => "Test Operation";
        public int Executes { get; private set; }
        public int Undos { get; private set; }

        public void Execute(TimelineModel model) => Executes++;
        public void Undo(TimelineModel model) => Undos++;
    }

    /// <summary>
    /// Stands in for the many real operations that resolve their target by id or index and
    /// return without touching anything when it no longer exists.
    /// </summary>
    private sealed class NoOpOperation : IEditOperation
    {
        public string Description => "No-op Operation";
        public int Executes { get; private set; }

        public void Execute(TimelineModel model) => Executes++;
        public void Undo(TimelineModel model) { }
        public bool ChangedModel => false;
    }

    private static (UndoRedoManager Manager, Func<int> EditCount) Build()
    {
        var manager = new UndoRedoManager(new TimelineModel());
        int edits = 0;
        manager.EditPerformed += (_, _) => edits++;
        return (manager, () => edits);
    }

    [TestMethod]
    public void Execute_RaisesEditPerformed()
    {
        var (manager, editCount) = Build();

        manager.Execute(new CountingOperation());

        Assert.AreEqual(1, editCount());
    }

    [TestMethod]
    public void UndoAndRedo_EachCountAsAnEdit()
    {
        var (manager, editCount) = Build();
        manager.Execute(new CountingOperation());

        manager.Undo();
        manager.Redo();

        Assert.AreEqual(3, editCount(), "Execute, undo and redo all leave the project differing from its saved file.");
    }

    /// <summary>
    /// Clear runs when a project is loaded. It refreshes the undo/redo buttons, so it still
    /// raises StateChanged — but treating it as an edit would mark every opened project dirty.
    /// </summary>
    [TestMethod]
    public void Clear_RaisesStateChangedButNotEditPerformed()
    {
        var (manager, editCount) = Build();
        manager.Execute(new CountingOperation());

        int stateChanges = 0;
        manager.StateChanged += (_, _) => stateChanges++;

        manager.Clear();

        Assert.AreEqual(1, stateChanges, "The undo/redo buttons still need refreshing.");
        Assert.AreEqual(1, editCount(), "Clear must not count as a user edit.");
    }

    [TestMethod]
    public void UndoWithEmptyStack_RaisesNothing()
    {
        var (manager, editCount) = Build();

        manager.Undo();
        manager.Redo();

        Assert.AreEqual(0, editCount(), "A no-op cannot make the project dirty.");
    }

    #region Operations that resolved nothing

    /// <summary>
    /// An operation whose target no longer exists still runs, but changes nothing. Reporting it
    /// as an edit would prompt the user to save work that was never done.
    /// </summary>
    [TestMethod]
    public void ExecuteOfANoOpOperation_DoesNotCountAsAnEdit()
    {
        var (manager, editCount) = Build();
        var op = new NoOpOperation();

        manager.Execute(op);

        Assert.AreEqual(1, op.Executes, "The operation still gets its chance to run.");
        Assert.AreEqual(0, editCount(), "Nothing changed, so nothing can be unsaved.");
        Assert.IsFalse(manager.CanUndo, "A no-op must not leave an undo entry that undoes nothing.");
    }

    /// <summary>
    /// The redo stack must survive a no-op. Clearing it would silently discard a redo the user
    /// could still legitimately perform, since the model never moved.
    /// </summary>
    [TestMethod]
    public void NoOpOperation_LeavesTheRedoStackIntact()
    {
        var (manager, _) = Build();
        manager.Execute(new CountingOperation());
        manager.Undo();
        Assert.IsTrue(manager.CanRedo, "Precondition: there is something to redo.");

        manager.Execute(new NoOpOperation());

        Assert.IsTrue(manager.CanRedo, "A no-op does not branch the history, so the redo stays valid.");
    }

    /// <summary>
    /// Guards against the mechanism silently disabling every operation that has not opted in:
    /// <see cref="IEditOperation.ChangedModel"/> defaults to true, so an operation that says
    /// nothing keeps its existing behaviour.
    /// </summary>
    [TestMethod]
    public void OperationThatDoesNotOptIn_StillCountsAsAnEdit()
    {
        var (manager, editCount) = Build();

        manager.Execute(new CountingOperation());

        Assert.AreEqual(1, editCount());
        Assert.IsTrue(manager.CanUndo);
    }

    /// <summary>
    /// The real thing, end to end: a segment operation pointed at an id that is not in the
    /// model. This is the case the guard clauses in <c>SegmentOperations</c> handle.
    /// </summary>
    [TestMethod]
    public void RealOperationWithAMissingTarget_IsTreatedAsANoOp()
    {
        var model = new TimelineModel();
        model.Segments.Add(new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) });
        var manager = new UndoRedoManager(model);
        int edits = 0;
        manager.EditPerformed += (_, _) => edits++;

        manager.Execute(new RemoveSegmentOperation("no-such-segment"));

        Assert.AreEqual(1, model.Segments.Count, "Precondition: nothing was removed.");
        Assert.AreEqual(0, edits, "A removal that removed nothing is not an edit.");
        Assert.IsFalse(manager.CanUndo, "Ctrl+Z must not be armed by an operation that did nothing.");
    }

    /// <summary>
    /// The complement of the test above — the same operation type, pointed at a real segment,
    /// must still register. A no-op guard that swallowed real edits would be far worse than the
    /// bug it fixes.
    /// </summary>
    [TestMethod]
    public void RealOperationWithAResolvableTarget_StillCountsAsAnEdit()
    {
        var model = new TimelineModel();
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) };
        model.Segments.Add(slide);
        var manager = new UndoRedoManager(model);
        int edits = 0;
        manager.EditPerformed += (_, _) => edits++;

        manager.Execute(new RemoveSegmentOperation(slide.Id));

        Assert.AreEqual(0, model.Segments.Count);
        Assert.AreEqual(1, edits);
        Assert.IsTrue(manager.CanUndo);
    }

    #endregion
}
