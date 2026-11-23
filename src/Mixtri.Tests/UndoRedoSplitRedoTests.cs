using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for undo/redo round trips through a split.
/// </summary>
/// <remarks>
/// Split operations used to mint fresh segment ids on every Execute, so a redo produced halves
/// with DIFFERENT ids from the original run. A later redo that targeted a half by id (remove,
/// trim, speed) then found nothing, and <see cref="UndoRedoManager.Redo"/> pushed the no-op
/// anyway with stale undo state — the next Ctrl+Z inserted at index -1, threw, and left every
/// earlier history entry unreachable.
/// </remarks>
[TestClass]
public sealed class UndoRedoSplitRedoTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";

    private static TimelineModel TenSecondTimeline()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.Add(new VideoSegment
        {
            VideoFilePath = Primary,
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(10),
        });
        model.RecalculateSegmentPositions();
        return model;
    }

    [TestMethod]
    public void SplitRedo_KeepsTheSameHalfIds()
    {
        var model = TenSecondTimeline();
        var manager = new UndoRedoManager(model);

        manager.Execute(new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)));
        var idsAfterExecute = model.Segments.Select(s => s.Id).ToList();

        manager.Undo();
        manager.Redo();

        CollectionAssert.AreEqual(idsAfterExecute, model.Segments.Select(s => s.Id).ToList());
    }

    [TestMethod]
    public void SplitThenRemove_UndoTwiceRedoTwice_RestoresTheRemovalAndUndoStillWorks()
    {
        var model = TenSecondTimeline();
        var manager = new UndoRedoManager(model);

        manager.Execute(new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(4)));
        string firstHalfId = model.Segments[0].Id;
        manager.Execute(new RemoveSegmentOperation(firstHalfId));
        Assert.AreEqual(1, model.Segments.Count);

        manager.Undo();
        manager.Undo();
        manager.Redo();
        manager.Redo();

        // The redo of the removal found its target again, so the edit really re-applied.
        Assert.AreEqual(1, model.Segments.Count);
        Assert.IsFalse(model.Segments.Any(s => s.Id == firstHalfId));

        // And the full history is still walkable without throwing.
        manager.Undo();
        Assert.AreEqual(2, model.Segments.Count);
        manager.Undo();
        Assert.AreEqual(1, model.Segments.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Segments[0].Duration);
        Assert.IsFalse(manager.CanUndo);
    }

    [TestMethod]
    public void InsertTextSlideRedo_KeepsTheSameHalfIds()
    {
        var model = TenSecondTimeline();
        var manager = new UndoRedoManager(model);

        manager.Execute(new SplitAndInsertTextSlideOperation(
            TimeSpan.FromSeconds(4), new TextSlideSegment { Duration = TimeSpan.FromSeconds(2) }));
        var idsAfterExecute = model.Segments.Select(s => s.Id).ToList();

        manager.Undo();
        manager.Redo();

        CollectionAssert.AreEqual(idsAfterExecute, model.Segments.Select(s => s.Id).ToList());
    }

    [TestMethod]
    public void NoOpRedo_IsDroppedInsteadOfPushedWithStaleUndoState()
    {
        var model = TenSecondTimeline();
        var manager = new UndoRedoManager(model);
        string id = model.Segments[0].Id;

        manager.Execute(new RemoveSegmentOperation(id));
        manager.Undo();

        // The target disappears behind the manager's back (e.g. a project reload path).
        model.Segments.Clear();
        int undoDepthBefore = manager.UndoCount;

        manager.Redo();

        Assert.AreEqual(undoDepthBefore, manager.UndoCount, "a no-op redo must not be pushed");
        Assert.IsFalse(manager.CanRedo, "a redo that cannot apply is dropped");
    }
}
