using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public class TimelineEditTests
{
    private static TimelineModel CreateModel()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(30),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(30)
        };
        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(30), "Main"));
        return model;
    }

    [TestMethod]
    public void TrimOperation_SetsTrimPoints()
    {
        var model = CreateModel();
        var op = new TrimOperation(
            trimStart: TimeSpan.FromSeconds(5),
            trimEnd: TimeSpan.FromSeconds(25));

        op.Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(5), model.TrimStart);
        Assert.AreEqual(TimeSpan.FromSeconds(25), model.TrimEnd);
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.EffectiveDuration);
    }

    [TestMethod]
    public void TrimOperation_Undo_RestoresOriginal()
    {
        var model = CreateModel();
        var op = new TrimOperation(
            trimStart: TimeSpan.FromSeconds(5),
            trimEnd: TimeSpan.FromSeconds(25));

        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(TimeSpan.Zero, model.TrimStart);
        Assert.AreEqual(TimeSpan.FromSeconds(30), model.TrimEnd);
    }

    [TestMethod]
    public void CutOperation_RemovesSegment()
    {
        var model = CreateModel();
        // Cut out seconds 10-20 from a 0-30 clip
        var op = new CutOperation(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        op.Execute(model);

        // Duration should shrink by 10s
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Duration);
        // Original clip should be split: [0-10] and [10-20]
        Assert.AreEqual(2, model.Clips.Count);
        Assert.AreEqual(TimeSpan.Zero, model.Clips[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Clips[0].End);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Clips[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Clips[1].End);
    }

    [TestMethod]
    public void CutOperation_Undo_RestoresOriginal()
    {
        var model = CreateModel();
        var op = new CutOperation(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(TimeSpan.FromSeconds(30), model.Duration);
        Assert.AreEqual(1, model.Clips.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(30), model.Clips[0].End);
    }

    [TestMethod]
    public void SplitOperation_CreatesTwoClips()
    {
        var model = CreateModel();
        var op = new SplitOperation(TimeSpan.FromSeconds(15));

        op.Execute(model);

        Assert.AreEqual(2, model.Clips.Count);
        Assert.AreEqual(TimeSpan.Zero, model.Clips[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(15), model.Clips[0].End);
        Assert.AreEqual(TimeSpan.FromSeconds(15), model.Clips[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(30), model.Clips[1].End);
    }

    [TestMethod]
    public void SplitOperation_Undo_MergesBack()
    {
        var model = CreateModel();
        var op = new SplitOperation(TimeSpan.FromSeconds(15));

        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(1, model.Clips.Count);
        Assert.AreEqual(TimeSpan.Zero, model.Clips[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(30), model.Clips[0].End);
    }

    [TestMethod]
    public void SpeedChangeOperation_AddsSegment()
    {
        var model = CreateModel();
        var op = new SpeedChangeOperation(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            speed: 2.0);

        op.Execute(model);

        Assert.AreEqual(1, model.SpeedSegments.Count);
        Assert.AreEqual(2.0, model.SpeedSegments[0].Speed);
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.SpeedSegments[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(15), model.SpeedSegments[0].End);
    }

    [TestMethod]
    public void SpeedChangeOperation_Undo_RemovesSegment()
    {
        var model = CreateModel();
        var op = new SpeedChangeOperation(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            speed: 2.0);

        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(0, model.SpeedSegments.Count);
    }

    [TestMethod]
    public void DeleteSegmentOperation_RemovesClip()
    {
        var model = CreateModel();
        // Split first, then delete one
        new SplitOperation(TimeSpan.FromSeconds(15)).Execute(model);
        Assert.AreEqual(2, model.Clips.Count);

        var deleteOp = new DeleteSegmentOperation(1);
        deleteOp.Execute(model);

        Assert.AreEqual(1, model.Clips.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(15), model.Clips[0].End);
    }

    [TestMethod]
    public void UndoRedo_ExecuteUndoRedo_WorksCorrectly()
    {
        var model = CreateModel();
        var manager = new UndoRedoManager(model);

        manager.Execute(new TrimOperation(
            trimStart: TimeSpan.FromSeconds(5),
            trimEnd: TimeSpan.FromSeconds(25)));

        Assert.AreEqual(TimeSpan.FromSeconds(5), model.TrimStart);
        Assert.IsTrue(manager.CanUndo);
        Assert.IsFalse(manager.CanRedo);
        Assert.AreEqual("Trim", manager.UndoDescription);

        manager.Undo();
        Assert.AreEqual(TimeSpan.Zero, model.TrimStart);
        Assert.IsFalse(manager.CanUndo);
        Assert.IsTrue(manager.CanRedo);
        Assert.AreEqual("Trim", manager.RedoDescription);

        manager.Redo();
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.TrimStart);
        Assert.IsTrue(manager.CanUndo);
        Assert.IsFalse(manager.CanRedo);
    }

    [TestMethod]
    public void UndoRedo_ExecuteClearsRedoStack()
    {
        var model = CreateModel();
        var manager = new UndoRedoManager(model);

        manager.Execute(new TrimOperation(trimStart: TimeSpan.FromSeconds(5)));
        manager.Undo();
        Assert.IsTrue(manager.CanRedo);

        // Executing a new operation should clear redo
        manager.Execute(new TrimOperation(trimEnd: TimeSpan.FromSeconds(20)));
        Assert.IsFalse(manager.CanRedo);
        Assert.IsTrue(manager.CanUndo);
    }

    [TestMethod]
    public void UndoRedo_StateChangedEvent_Fires()
    {
        var model = CreateModel();
        var manager = new UndoRedoManager(model);
        int fireCount = 0;
        manager.StateChanged += (_, _) => fireCount++;

        manager.Execute(new TrimOperation(trimStart: TimeSpan.FromSeconds(1)));
        manager.Undo();
        manager.Redo();
        manager.Clear();

        Assert.AreEqual(4, fireCount);
    }
}
