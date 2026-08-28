using Musio.Core.Export;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Covers the model side of cursor anchors: the undo/redo operations, the per-source
/// ownership rule preview and export share, and the deliberate decision that anchors do not
/// count as timeline content.
/// </summary>
[TestClass]
public sealed class CursorAnchorTests
{
    private const string PrimaryPath = @"C:\rec\primary.mp4";
    private const string AppendedPath = @"C:\rec\appended.mp4";

    private static TimelineModel BuildModel()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(30),
            TrimEnd = TimeSpan.FromSeconds(30),
        };
        model.Segments.Add(new VideoSegment
        {
            VideoFilePath = PrimaryPath,
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.FromSeconds(30),
        });
        return model;
    }

    // ── Operations ──

    [TestMethod]
    public void Add_ThenUndo_RemovesTheAnchor()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        Assert.AreEqual(1, model.CursorAnchors.Count);

        undo.Undo();
        Assert.AreEqual(0, model.CursorAnchors.Count);
    }

    [TestMethod]
    public void Add_Redo_KeepsTheSameAnchorId()
    {
        // An id minted per Execute would change on redo, silently breaking the selection and
        // every later operation that resolves this anchor by id.
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string firstId = model.CursorAnchors[0].Id;

        undo.Undo();
        undo.Redo();

        Assert.AreEqual(1, model.CursorAnchors.Count);
        Assert.AreEqual(firstId, model.CursorAnchors[0].Id);
    }

    [TestMethod]
    public void Add_ClampsPositionToTheFrame()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(1), -0.5, 3.0));

        Assert.AreEqual(0.0, model.CursorAnchors[0].X);
        Assert.AreEqual(1.0, model.CursorAnchors[0].Y);
    }

    [TestMethod]
    public void Move_ThenUndo_RestoresThePreviousPosition()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        undo.Execute(new MoveCursorAnchorOperation(id, 0.6, 0.1));
        Assert.AreEqual(0.6, model.CursorAnchors[0].X);
        Assert.AreEqual(0.1, model.CursorAnchors[0].Y);

        undo.Undo();
        Assert.AreEqual(0.25, model.CursorAnchors[0].X);
        Assert.AreEqual(0.75, model.CursorAnchors[0].Y);
        Assert.AreEqual(id, model.CursorAnchors[0].Id, "moving must not re-identify the anchor");
    }

    [TestMethod]
    public void Move_AnAnchorThatNoLongerExists_ReportsNoChange()
    {
        var model = BuildModel();
        var operation = new MoveCursorAnchorOperation("missing", 0.5, 0.5);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel,
            "an operation that changed nothing must not become an undo entry that undoes nothing");
    }

    [TestMethod]
    public void Move_ToTheSamePosition_ReportsNoChange()
    {
        // A press on the cursor handle that never travels, or a drag that ends where it
        // started, must not spend an undo entry that visibly does nothing.
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        var operation = new MoveCursorAnchorOperation(id, 0.25, 0.75);
        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel,
            "an operation that changed nothing must not become an undo entry that undoes nothing");
    }

    [TestMethod]
    public void Move_ToTheSamePosition_LeavesTheUndoStackOnTheAddAlone()
    {
        // The first Ctrl+Z after a no-op drag has to undo the ADD, not a phantom move.
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        undo.Execute(new MoveCursorAnchorOperation(id, 0.25, 0.75));
        undo.Undo();

        Assert.AreEqual(0, model.CursorAnchors.Count);
    }

    [TestMethod]
    public void Remove_ThenUndo_RestoresTheAnchorWithItsIdAndPosition()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        undo.Execute(new RemoveCursorAnchorOperation(id));
        Assert.AreEqual(0, model.CursorAnchors.Count);

        undo.Undo();
        Assert.AreEqual(1, model.CursorAnchors.Count);
        Assert.AreEqual(id, model.CursorAnchors[0].Id);
        Assert.AreEqual(0.25, model.CursorAnchors[0].X);
        Assert.AreEqual(0.75, model.CursorAnchors[0].Y);
    }

    [TestMethod]
    public void Add_KeepsAnchorsSortedByTime()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(8), 0.1, 0.1));
        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(2), 0.2, 0.2));

        Assert.AreEqual(TimeSpan.FromSeconds(2), model.CursorAnchors[0].Timestamp);
        Assert.AreEqual(TimeSpan.FromSeconds(8), model.CursorAnchors[1].Timestamp);
    }

    [TestMethod]
    public void Retime_MovesTheAnchorInTimeAndKeepsItsPositionAndId()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        undo.Execute(new RetimeCursorAnchorOperation(id, TimeSpan.FromSeconds(9)));

        Assert.AreEqual(TimeSpan.FromSeconds(9), model.CursorAnchors[0].Timestamp);
        Assert.AreEqual(0.25, model.CursorAnchors[0].X);
        Assert.AreEqual(0.75, model.CursorAnchors[0].Y);
        Assert.AreEqual(id, model.CursorAnchors[0].Id, "re-timing must not re-identify the anchor");
    }

    [TestMethod]
    public void Retime_ThenUndo_RestoresThePreviousMoment()
    {
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(4), 0.25, 0.75));
        string id = model.CursorAnchors[0].Id;

        undo.Execute(new RetimeCursorAnchorOperation(id, TimeSpan.FromSeconds(9)));
        undo.Undo();

        Assert.AreEqual(TimeSpan.FromSeconds(4), model.CursorAnchors[0].Timestamp);
        Assert.AreEqual(id, model.CursorAnchors[0].Id);
    }

    [TestMethod]
    public void Retime_KeepsAnchorsSortedByTime()
    {
        // CursorPathWarp blends towards NEIGHBOURING anchors, so an out-of-order list would
        // make a dragged anchor blend towards the wrong one.
        var model = BuildModel();
        var undo = new UndoRedoManager(model);

        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(2), 0.1, 0.1));
        undo.Execute(new AddCursorAnchorOperation(TimeSpan.FromSeconds(8), 0.2, 0.2));
        string earlyId = model.CursorAnchors[0].Id;

        undo.Execute(new RetimeCursorAnchorOperation(earlyId, TimeSpan.FromSeconds(20)));

        Assert.AreEqual(TimeSpan.FromSeconds(8), model.CursorAnchors[0].Timestamp);
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.CursorAnchors[1].Timestamp);
        Assert.AreEqual(earlyId, model.CursorAnchors[1].Id);

        undo.Undo();
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.CursorAnchors[0].Timestamp);
        Assert.AreEqual(TimeSpan.FromSeconds(8), model.CursorAnchors[1].Timestamp);
    }

    [TestMethod]
    public void Retime_ToTheSameMoment_ReportsNoChange()
    {
        var model = BuildModel();
        model.CursorAnchors.Add(new CursorAnchor { Id = "a", Timestamp = TimeSpan.FromSeconds(3) });

        var operation = new RetimeCursorAnchorOperation("a", TimeSpan.FromSeconds(3));
        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel,
            "a drag that lands where it started must not become an undo entry that undoes nothing");
    }

    [TestMethod]
    public void Retime_AnAnchorThatNoLongerExists_ReportsNoChange()
    {
        var model = BuildModel();
        var operation = new RetimeCursorAnchorOperation("missing", TimeSpan.FromSeconds(3));

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel);
    }

    [TestMethod]
    public void Retime_ClampsANegativeMomentToZero()
    {
        var model = BuildModel();
        model.CursorAnchors.Add(new CursorAnchor { Id = "a", Timestamp = TimeSpan.FromSeconds(3) });

        new RetimeCursorAnchorOperation("a", TimeSpan.FromSeconds(-5)).Execute(model);

        Assert.AreEqual(TimeSpan.Zero, model.CursorAnchors[0].Timestamp);
    }

    // ── Per-source ownership ──

    [TestMethod]
    public void SelectCursorAnchors_KeepsAnAppendedRecordingsAnchorsOffThePrimary()
    {
        // Anchors displace a specific recording's cursor path. Leaking one onto another source
        // would warp footage the user never touched.
        var model = BuildModel();
        model.PrimaryVideoFilePath = PrimaryPath;
        model.CursorAnchors.Add(new CursorAnchor { Timestamp = TimeSpan.FromSeconds(1), X = 0.1, Y = 0.1 });
        model.CursorAnchors.Add(new CursorAnchor
        {
            Timestamp = TimeSpan.FromSeconds(2),
            X = 0.9,
            Y = 0.9,
            SourceVideoFilePath = AppendedPath,
        });

        var primary = SegmentFrameComposer.SelectCursorAnchors(model, PrimaryPath);
        var appended = SegmentFrameComposer.SelectCursorAnchors(model, AppendedPath);

        Assert.AreEqual(1, primary.Count);
        Assert.IsNull(primary[0].SourceVideoFilePath, "a null source path means the primary recording");

        Assert.AreEqual(1, appended.Count);
        Assert.AreEqual(AppendedPath, appended[0].SourceVideoFilePath);
    }

    [TestMethod]
    public void SelectCursorAnchors_ReturnsThemInTimeOrder()
    {
        var model = BuildModel();
        model.PrimaryVideoFilePath = PrimaryPath;
        model.CursorAnchors.Add(new CursorAnchor { Timestamp = TimeSpan.FromSeconds(7) });
        model.CursorAnchors.Add(new CursorAnchor { Timestamp = TimeSpan.FromSeconds(3) });

        var selected = SegmentFrameComposer.SelectCursorAnchors(model, PrimaryPath);

        Assert.AreEqual(TimeSpan.FromSeconds(3), selected[0].Timestamp);
        Assert.AreEqual(TimeSpan.FromSeconds(7), selected[1].Timestamp);
    }

    // ── Emptiness ──

    [TestMethod]
    public void CursorAnchors_AreNotTimelineContent()
    {
        // Deliberate, and the same rule zoom keyframes follow: an anchor decorates footage
        // rather than standing on its own, so a timeline holding only anchors has nothing left
        // to decorate and must still count as empty.
        var model = TimelineModel.CreateEmpty();
        model.CursorAnchors.Add(new CursorAnchor { Timestamp = TimeSpan.FromSeconds(1) });

        Assert.IsFalse(model.HasNonSegmentContent);
        Assert.IsTrue(model.IsEmpty);
    }
}
