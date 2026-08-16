namespace Musio.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Musio.Core.Timeline;

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
}
