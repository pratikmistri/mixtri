using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Tests for the independent text overlay track: add / move / trim / remove /
/// update-properties operations, their undo/redo round trips, and no-op safety
/// against a non-existent overlay id. Text overlays are source-time ranged, like
/// <see cref="CameraSegment"/>, so they stay aligned with the recording.
/// </summary>
[TestClass]
public sealed class TextOverlayEditOperationsTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static TextOverlaySegment Overlay(double startSec, double durSec) => new()
    {
        Start = S(startSec),
        Duration = S(durSec),
    };

    // ── AddTextOverlayOperation ─────────────────────────────────────────

    [TestMethod]
    public void Add_SegmentConstructor_InsertsSegment()
    {
        var model = new TimelineModel();
        var segment = Overlay(2, 3);
        var op = new AddTextOverlayOperation(segment);

        op.Execute(model);
        Assert.AreEqual(1, model.TextOverlays.Count);
        Assert.AreSame(segment, model.TextOverlays[0]);
        Assert.AreEqual(segment.Id, op.CreatedId);

        op.Undo(model);
        Assert.AreEqual(0, model.TextOverlays.Count);

        op.Execute(model);
        Assert.AreEqual(1, model.TextOverlays.Count);
        Assert.AreSame(segment, model.TextOverlays[0]);
    }

    [TestMethod]
    public void Add_ValueConstructor_SetsStartDurationTextAndSource()
    {
        var model = new TimelineModel();
        var op = new AddTextOverlayOperation(S(1), S(4), text: "Hello", sourceVideoFilePath: "secondary.mp4");

        op.Execute(model);
        var added = model.TextOverlays.Single(o => o.Id == op.CreatedId);
        Assert.AreEqual(S(1), added.Start);
        Assert.AreEqual(S(4), added.Duration);
        Assert.AreEqual("Hello", added.Text);
        Assert.AreEqual("secondary.mp4", added.SourceVideoFilePath);

        op.Undo(model);
        Assert.AreEqual(0, model.TextOverlays.Count);

        op.Execute(model);
        Assert.AreEqual(1, model.TextOverlays.Count);
    }

    [TestMethod]
    public void Add_ValueConstructor_DefaultsTextWhenNull()
    {
        var model = new TimelineModel();
        new AddTextOverlayOperation(S(0), S(2)).Execute(model);

        Assert.AreEqual("Text", model.TextOverlays[0].Text);
        Assert.IsNull(model.TextOverlays[0].SourceVideoFilePath);
    }

    [TestMethod]
    public void Add_KeepsListSortedByStart()
    {
        var model = new TimelineModel();
        new AddTextOverlayOperation(S(5), S(3)).Execute(model);
        new AddTextOverlayOperation(S(1), S(2)).Execute(model);
        new AddTextOverlayOperation(S(3), S(1)).Execute(model);

        CollectionAssert.AreEqual(
            new[] { S(1), S(3), S(5) },
            model.TextOverlays.Select(o => o.Start).ToArray());
    }

    // ── MoveTextOverlayOperation ────────────────────────────────────────

    [TestMethod]
    public void Move_ChangesStart_PreservesDuration_UndoRedo()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(3));
        add.Execute(model);

        var move = new MoveTextOverlayOperation(add.CreatedId, S(10));
        move.Execute(model);
        var moved = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(10), moved.Start);
        Assert.AreEqual(S(3), moved.Duration);

        move.Undo(model);
        var restored = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(2), restored.Start);
        Assert.AreEqual(S(3), restored.Duration);

        move.Execute(model);
        Assert.AreEqual(S(10), model.TextOverlays.Single(o => o.Id == add.CreatedId).Start);
    }

    [TestMethod]
    public void Move_NegativeStart_ClampsToZero()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(5), S(2));
        add.Execute(model);

        new MoveTextOverlayOperation(add.CreatedId, S(-3)).Execute(model);

        Assert.AreEqual(TimeSpan.Zero, model.TextOverlays.Single(o => o.Id == add.CreatedId).Start);
    }

    [TestMethod]
    public void Move_ReSortsList_AndUndoRestoresOriginalOrder()
    {
        var model = new TimelineModel();
        var first = new AddTextOverlayOperation(S(0), S(2));
        var second = new AddTextOverlayOperation(S(5), S(2));
        first.Execute(model);
        second.Execute(model);

        // Move the first overlay past the second: order should flip.
        var move = new MoveTextOverlayOperation(first.CreatedId, S(8));
        move.Execute(model);
        CollectionAssert.AreEqual(
            new[] { second.CreatedId, first.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());

        move.Undo(model);
        CollectionAssert.AreEqual(
            new[] { first.CreatedId, second.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());
        Assert.AreEqual(S(0), model.TextOverlays.Single(o => o.Id == first.CreatedId).Start);
    }

    // ── TrimTextOverlayOperation ────────────────────────────────────────

    [TestMethod]
    public void Trim_RightEdge_AdjustsDuration_UndoRedo()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        var trim = new TrimTextOverlayOperation(add.CreatedId, fromStart: false, S(5));
        trim.Execute(model);
        var trimmed = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(2), trimmed.Start);
        Assert.AreEqual(S(3), trimmed.Duration);

        trim.Undo(model);
        var restored = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(2), restored.Start);
        Assert.AreEqual(S(5), restored.Duration);

        trim.Execute(model);
        var reapplied = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(2), reapplied.Start);
        Assert.AreEqual(S(3), reapplied.Duration);
    }

    [TestMethod]
    public void Trim_LeftEdge_AdjustsStartAndDuration_UndoRedo()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        var trim = new TrimTextOverlayOperation(add.CreatedId, fromStart: true, S(4));
        trim.Execute(model);
        var trimmed = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(4), trimmed.Start);
        Assert.AreEqual(S(3), trimmed.Duration); // end stays at 7

        trim.Undo(model);
        var restored = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(2), restored.Start);
        Assert.AreEqual(S(5), restored.Duration);

        trim.Execute(model);
        var reapplied = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(S(4), reapplied.Start);
        Assert.AreEqual(S(3), reapplied.Duration);
    }

    [TestMethod]
    public void Trim_RightEdge_BelowMinDuration_ClampsToMinDuration()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        // Drag the right edge back to the start (or before) — duration must clamp, never
        // go to zero or negative.
        new TrimTextOverlayOperation(add.CreatedId, fromStart: false, S(2)).Execute(model);

        var trimmed = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(TrimTextOverlayOperation.MinDuration, trimmed.Duration);
        Assert.IsTrue(trimmed.Duration > TimeSpan.Zero);
    }

    [TestMethod]
    public void Trim_LeftEdge_BeyondEnd_ClampsToMinDuration()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        // Drag the left edge past the end (or to it) — duration must clamp, never go to
        // zero or negative.
        new TrimTextOverlayOperation(add.CreatedId, fromStart: true, S(9)).Execute(model);

        var trimmed = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(TrimTextOverlayOperation.MinDuration, trimmed.Duration);
        Assert.IsTrue(trimmed.Duration > TimeSpan.Zero);
        Assert.AreEqual(S(7) - TrimTextOverlayOperation.MinDuration, trimmed.Start);
    }

    [TestMethod]
    public void Trim_LeftEdge_BeforeZero_ClampsStartToZero()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        new TrimTextOverlayOperation(add.CreatedId, fromStart: true, S(-3)).Execute(model);

        var trimmed = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(TimeSpan.Zero, trimmed.Start);
        Assert.AreEqual(S(7), trimmed.Duration);
    }

    // ── RemoveTextOverlayOperation ──────────────────────────────────────

    [TestMethod]
    public void Remove_AndUndo_ReInsertsAtSortedPosition()
    {
        var model = new TimelineModel();
        var a = new AddTextOverlayOperation(S(0), S(2));
        var b = new AddTextOverlayOperation(S(5), S(2));
        var c = new AddTextOverlayOperation(S(10), S(2));
        a.Execute(model);
        b.Execute(model);
        c.Execute(model);

        var remove = new RemoveTextOverlayOperation(b.CreatedId);
        remove.Execute(model);
        CollectionAssert.AreEqual(
            new[] { a.CreatedId, c.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());

        remove.Undo(model);
        CollectionAssert.AreEqual(
            new[] { a.CreatedId, b.CreatedId, c.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());

        remove.Execute(model);
        CollectionAssert.AreEqual(
            new[] { a.CreatedId, c.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());
    }

    // ── UpdateTextOverlayPropertiesOperation ────────────────────────────

    [TestMethod]
    public void UpdateProperties_ChangesMultipleProperties_UndoRestoresAll_RedoReapplies()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var op = new UpdateTextOverlayPropertiesOperation(add.CreatedId, o =>
        {
            o.Text = "Updated";
            o.FontSize = 64;
            o.IsBold = true;
            o.Background = TextOverlayBackground.Blur;
            o.Anchor = TextOverlayAnchor.TopLeft;
        });

        op.Execute(model);
        var updated = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual("Updated", updated.Text);
        Assert.AreEqual(64, updated.FontSize);
        Assert.IsTrue(updated.IsBold);
        Assert.AreEqual(TextOverlayBackground.Blur, updated.Background);
        Assert.AreEqual(TextOverlayAnchor.TopLeft, updated.Anchor);

        op.Undo(model);
        var restored = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual("Text", restored.Text);
        Assert.AreEqual(42, restored.FontSize);
        Assert.IsFalse(restored.IsBold);
        Assert.AreEqual(TextOverlayBackground.Solid, restored.Background);
        Assert.AreEqual(TextOverlayAnchor.BottomCenter, restored.Anchor);

        op.Execute(model);
        var reapplied = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual("Updated", reapplied.Text);
        Assert.AreEqual(64, reapplied.FontSize);
    }

    [TestMethod]
    public void UpdateProperties_ChangingStartOrDuration_ReSortsList()
    {
        var model = new TimelineModel();
        var a = new AddTextOverlayOperation(S(0), S(2));
        var b = new AddTextOverlayOperation(S(5), S(2));
        a.Execute(model);
        b.Execute(model);

        // Move 'a' past 'b' via the property-update path.
        var op = new UpdateTextOverlayPropertiesOperation(a.CreatedId, o => o.Start = S(10));
        op.Execute(model);

        CollectionAssert.AreEqual(
            new[] { b.CreatedId, a.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());

        op.Undo(model);
        CollectionAssert.AreEqual(
            new[] { a.CreatedId, b.CreatedId },
            model.TextOverlays.Select(o => o.Id).ToArray());
    }

    [TestMethod]
    public void UpdateProperties_SuccessiveIndependentUndos_DoNotCorruptEachOthersSnapshot()
    {
        // Guards the snapshot-aliasing bug class: mutating one operation's undo snapshot
        // must never leak into another operation's independently-taken snapshot.
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var setText = new UpdateTextOverlayPropertiesOperation(add.CreatedId, o => o.Text = "First");
        setText.Execute(model);
        Assert.AreEqual("First", model.TextOverlays.Single(o => o.Id == add.CreatedId).Text);

        setText.Undo(model);
        Assert.AreEqual("Text", model.TextOverlays.Single(o => o.Id == add.CreatedId).Text);

        var setFontSize = new UpdateTextOverlayPropertiesOperation(add.CreatedId, o => o.FontSize = 99);
        setFontSize.Execute(model);
        Assert.AreEqual(99, model.TextOverlays.Single(o => o.Id == add.CreatedId).FontSize);
        // The first operation's undo must still be intact — text unaffected by the second op.
        Assert.AreEqual("Text", model.TextOverlays.Single(o => o.Id == add.CreatedId).Text);

        setFontSize.Undo(model);
        var final = model.TextOverlays.Single(o => o.Id == add.CreatedId);
        Assert.AreEqual(42, final.FontSize);
        Assert.AreEqual("Text", final.Text);

        // Re-running the first op's undo again must still restore the same original text,
        // proving its snapshot was never corrupted by the second operation.
        setText.Execute(model);
        Assert.AreEqual("First", model.TextOverlays.Single(o => o.Id == add.CreatedId).Text);
        setText.Undo(model);
        Assert.AreEqual("Text", model.TextOverlays.Single(o => o.Id == add.CreatedId).Text);
    }

    // ── No-op safety against a non-existent overlay id ──────────────────

    [TestMethod]
    public void Move_NonExistentId_DoesNotThrow_ModelUnchanged()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var op = new MoveTextOverlayOperation("does-not-exist", S(10));
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(1, model.TextOverlays.Count);
        Assert.AreEqual(S(0), model.TextOverlays[0].Start);
    }

    [TestMethod]
    public void Trim_NonExistentId_DoesNotThrow_ModelUnchanged()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var op = new TrimTextOverlayOperation("does-not-exist", fromStart: false, S(2));
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(1, model.TextOverlays.Count);
        Assert.AreEqual(S(4), model.TextOverlays[0].Duration);
    }

    [TestMethod]
    public void Remove_NonExistentId_DoesNotThrow_ModelUnchanged()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var op = new RemoveTextOverlayOperation("does-not-exist");
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(1, model.TextOverlays.Count);
    }

    [TestMethod]
    public void UpdateProperties_NonExistentId_DoesNotThrow_ModelUnchanged()
    {
        var model = new TimelineModel();
        var add = new AddTextOverlayOperation(S(0), S(4));
        add.Execute(model);

        var op = new UpdateTextOverlayPropertiesOperation("does-not-exist", o => o.Text = "Should Not Apply");
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(1, model.TextOverlays.Count);
        Assert.AreEqual("Text", model.TextOverlays[0].Text);
    }

    [TestMethod]
    public void Add_SegmentConstructor_NullSegment_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new AddTextOverlayOperation((TextOverlaySegment)null!));
    }

    // ── Undo restores every editable property ───────────────────────────

    /// <summary>
    /// Guards the whole restore list in UpdateTextOverlayPropertiesOperation.Undo. It copies
    /// each field back one by one, so a property added to TextOverlaySegment without a
    /// matching line there is silently not undoable - which is exactly how HeightFraction
    /// was missed when the box became an explicit rectangle. Reflection means any future
    /// field is covered automatically instead of relying on someone remembering.
    /// </summary>
    [TestMethod]
    public void Update_Undo_RestoresEveryEditableProperty()
    {
        var model = new TimelineModel();
        var overlay = Overlay(0, 5);
        model.TextOverlays.Add(overlay);

        // Identity and timing are the operation's own concern, not style state. "End" is
        // computed. Init-only properties are excluded too: they are set at construction and
        // no editing path can change them afterwards (only reflection can), so there is
        // nothing for Undo to put back.
        var skip = new HashSet<string> { nameof(TextOverlaySegment.Id), nameof(TextOverlaySegment.Start), nameof(TextOverlaySegment.Duration), "End" };

        var props = typeof(TextOverlaySegment).GetProperties()
            .Where(p => p.CanRead && p.CanWrite && !skip.Contains(p.Name) && !IsInitOnly(p))
            .ToList();

        Assert.IsTrue(props.Count > 10, "Expected the overlay to expose a broad set of editable properties.");

        var original = props.ToDictionary(p => p.Name, p => p.GetValue(overlay));

        // Move every property to a value that differs from its current one.
        var op = new UpdateTextOverlayPropertiesOperation(overlay.Id, o =>
        {
            foreach (var p in props)
                p.SetValue(o, MutatedValue(p.PropertyType, p.GetValue(o)));
        });

        op.Execute(model);

        foreach (var p in props)
            Assert.AreNotEqual(original[p.Name], p.GetValue(overlay), $"{p.Name} was not actually changed by the test.");

        op.Undo(model);

        foreach (var p in props)
            Assert.AreEqual(original[p.Name], p.GetValue(overlay), $"{p.Name} was not restored by Undo.");
    }

    /// <summary>
    /// True for an <c>init</c>-only property. The compiler marks such setters with the
    /// <c>IsExternalInit</c> modreq, and reflection still reports <c>CanWrite</c> for them,
    /// so this is the only reliable way to tell them apart from genuinely settable state.
    /// </summary>
    private static bool IsInitOnly(System.Reflection.PropertyInfo p) =>
        p.SetMethod is { } setter &&
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    /// <summary>Returns a value of <paramref name="type"/> guaranteed to differ from <paramref name="current"/>.</summary>
    private static object? MutatedValue(Type type, object? current)
    {
        if (type == typeof(string)) return (current as string) + "_changed";
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(double)) return (double)(current ?? 0.0) + 0.123;
        if (type == typeof(int)) return (int)(current ?? 0) + 1;
        if (type == typeof(TimeSpan)) return (TimeSpan)(current ?? TimeSpan.Zero) + TimeSpan.FromSeconds(1);
        if (type == typeof(TransitionConfig))
        {
            var existing = current as TransitionConfig;
            return new TransitionConfig
            {
                Type = existing?.Type == TransitionType.Fade ? TransitionType.Wipe : TransitionType.Fade,
                Duration = (existing?.Duration ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(250),
            };
        }
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            return values.First(v => !Equals(v, current));
        }
        throw new NotSupportedException($"Add a mutation rule for {type.Name} to keep this guard exhaustive.");
    }
}

