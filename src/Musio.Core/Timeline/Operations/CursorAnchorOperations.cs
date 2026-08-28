namespace Musio.Core.Timeline;

// ── Cursor anchor operations ──
// Anchors are pinned to SOURCE-video time and belong to one recording, so — like text
// overlays and zoom keyframes — none of these touch the segment list or trigger a re-flow.

/// <summary>Adds a cursor anchor, repositioning the pointer at one moment of a recording.</summary>
public class AddCursorAnchorOperation : IEditOperation
{
    private readonly CursorAnchor _anchor;

    public string CreatedId => _anchor.Id;
    public string Description => "Move Cursor";

    public AddCursorAnchorOperation(CursorAnchor anchor)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
    }

    /// <remarks>
    /// The anchor — and therefore its generated <see cref="CursorAnchor.Id"/> — is built ONCE
    /// here rather than inside <see cref="Execute"/>. An id minted per run would change on
    /// redo, silently breaking the selection and every later operation that resolves this
    /// anchor by id.
    /// </remarks>
    public AddCursorAnchorOperation(
        TimeSpan timestamp, double x, double y, string? sourceVideoFilePath = null)
    {
        _anchor = new CursorAnchor
        {
            Timestamp = timestamp,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
            SourceVideoFilePath = sourceVideoFilePath,
        };
    }

    public void Execute(TimelineModel model)
    {
        model.CursorAnchors.Add(_anchor);
        model.CursorAnchors.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public void Undo(TimelineModel model)
    {
        model.CursorAnchors.RemoveAll(a => a.Id == _anchor.Id);
    }
}

/// <summary>Moves an existing cursor anchor to a new position, keeping its moment.</summary>
public class MoveCursorAnchorOperation : IEditOperation
{
    private readonly string _anchorId;
    private readonly double _newX;
    private readonly double _newY;
    private double _previousX;
    private double _previousY;

    public string Description => "Move Cursor";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public MoveCursorAnchorOperation(string anchorId, double x, double y)
    {
        _anchorId = anchorId ?? throw new ArgumentNullException(nameof(anchorId));
        _newX = Math.Clamp(x, 0, 1);
        _newY = Math.Clamp(y, 0, 1);
    }

    public void Execute(TimelineModel model)
    {
        int index = model.CursorAnchors.FindIndex(a => a.Id == _anchorId);
        if (index < 0)
        {
            _changed = false;
            return;
        }

        _changed = true;
        var anchor = model.CursorAnchors[index];
        _previousX = anchor.X;
        _previousY = anchor.Y;
        model.CursorAnchors[index] = anchor with { X = _newX, Y = _newY };
    }

    public void Undo(TimelineModel model)
    {
        if (!_changed) return;

        int index = model.CursorAnchors.FindIndex(a => a.Id == _anchorId);
        if (index < 0) return;

        model.CursorAnchors[index] = model.CursorAnchors[index] with
        {
            X = _previousX,
            Y = _previousY,
        };
    }
}

/// <summary>
/// Moves an existing cursor anchor to a different moment, keeping its position.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="MoveCursorAnchorOperation"/>, which moves the anchor in the
/// FRAME. This one moves it in TIME, and is what a drag of the anchor's marker along the
/// timeline's cursor lane commits. The list is re-sorted afterwards because
/// <see cref="AddCursorAnchorOperation"/> establishes timestamp order and
/// <c>CursorPathWarp</c> builds its control points from neighbouring anchors — an
/// out-of-order list would blend towards the wrong neighbour.
/// </remarks>
public class RetimeCursorAnchorOperation : IEditOperation
{
    private readonly string _anchorId;
    private readonly TimeSpan _newTimestamp;
    private TimeSpan _previousTimestamp;

    public string Description => "Move Cursor Keyframe";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public RetimeCursorAnchorOperation(string anchorId, TimeSpan newTimestamp)
    {
        _anchorId = anchorId ?? throw new ArgumentNullException(nameof(anchorId));
        _newTimestamp = newTimestamp < TimeSpan.Zero ? TimeSpan.Zero : newTimestamp;
    }

    public void Execute(TimelineModel model)
    {
        int index = model.CursorAnchors.FindIndex(a => a.Id == _anchorId);
        if (index < 0)
        {
            _changed = false;
            return;
        }

        var anchor = model.CursorAnchors[index];
        if (anchor.Timestamp == _newTimestamp)
        {
            _changed = false;
            return;
        }

        _changed = true;
        _previousTimestamp = anchor.Timestamp;
        model.CursorAnchors[index] = anchor with { Timestamp = _newTimestamp };
        model.CursorAnchors.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public void Undo(TimelineModel model)
    {
        if (!_changed) return;

        int index = model.CursorAnchors.FindIndex(a => a.Id == _anchorId);
        if (index < 0) return;

        model.CursorAnchors[index] = model.CursorAnchors[index] with { Timestamp = _previousTimestamp };
        model.CursorAnchors.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }
}

/// <summary>Removes a cursor anchor, restoring the recorded path around that moment.</summary>
public class RemoveCursorAnchorOperation : IEditOperation
{
    private readonly string _anchorId;
    private CursorAnchor? _removed;
    private int _removedIndex = -1;

    public string Description => "Remove Cursor Move";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public RemoveCursorAnchorOperation(string anchorId)
    {
        _anchorId = anchorId ?? throw new ArgumentNullException(nameof(anchorId));
    }

    public void Execute(TimelineModel model)
    {
        _removedIndex = model.CursorAnchors.FindIndex(a => a.Id == _anchorId);
        if (_removedIndex < 0)
        {
            _changed = false;
            return;
        }

        _changed = true;
        _removed = model.CursorAnchors[_removedIndex];
        model.CursorAnchors.RemoveAt(_removedIndex);
    }

    public void Undo(TimelineModel model)
    {
        if (!_changed || _removed is null) return;

        // Restoring the whole record — not just its position — is what keeps the anchor's Id
        // stable across undo, so a redo and any later edit still resolve the same anchor.
        int index = Math.Clamp(_removedIndex, 0, model.CursorAnchors.Count);
        model.CursorAnchors.Insert(index, _removed);
    }
}
