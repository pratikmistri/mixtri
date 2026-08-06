namespace Musio.Core.Timeline;

// ── Text overlay track operations ──
// Text overlays live on their own track; their Start/Duration are source-video time
// ranges (independent of the primary segment list), so these operations never call
// RecalculateSegmentPositions.

/// <summary>Adds a new animated text overlay segment over a source-time range.</summary>
public class AddTextOverlayOperation : IEditOperation
{
    private readonly TextOverlaySegment _segment;

    public string CreatedId => _segment.Id;
    public string Description => "Add Text Overlay";

    public AddTextOverlayOperation(TextOverlaySegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    public AddTextOverlayOperation(TimeSpan sourceStart, TimeSpan sourceDuration,
        string? text = null, string? sourceVideoFilePath = null)
    {
        _segment = new TextOverlaySegment
        {
            Start = sourceStart,
            Duration = sourceDuration,
            Text = text ?? "Text",
            SourceVideoFilePath = sourceVideoFilePath,
        };
    }

    public void Execute(TimelineModel model)
    {
        model.TextOverlays.Add(_segment);
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        model.TextOverlays.RemoveAll(o => o.Id == _segment.Id);
    }
}

/// <summary>Moves a text overlay to a new source-time start, preserving its duration.</summary>
public class MoveTextOverlayOperation : IEditOperation
{
    private readonly string _overlayId;
    private readonly TimeSpan _newStart;
    private TimeSpan _previousStart;

    public string Description => "Move Text Overlay";

    public MoveTextOverlayOperation(string overlayId, TimeSpan newStart)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        _previousStart = overlay.Start;
        overlay.Start = _newStart < TimeSpan.Zero ? TimeSpan.Zero : _newStart;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        overlay.Start = _previousStart;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Trims one edge of a text overlay (source-time range).</summary>
public class TrimTextOverlayOperation : IEditOperation
{
    /// <summary>Minimum text overlay duration — longer than the camera track's, since text needs time to be read.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(200);

    private readonly string _overlayId;
    private readonly bool _fromStart;
    private readonly TimeSpan _newEdge;
    private TimeSpan _previousStart;
    private TimeSpan _previousDuration;

    public string Description => "Trim Text Overlay";

    /// <param name="overlayId">Overlay to trim.</param>
    /// <param name="fromStart">True for the left edge, false for the right edge.</param>
    /// <param name="newEdgeSourceTime">New source-time position of the dragged edge.</param>
    public TrimTextOverlayOperation(string overlayId, bool fromStart, TimeSpan newEdgeSourceTime)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _fromStart = fromStart;
        _newEdge = newEdgeSourceTime;
    }

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        _previousStart = overlay.Start;
        _previousDuration = overlay.Duration;

        if (_fromStart)
        {
            var end = overlay.End;
            var newStart = _newEdge < TimeSpan.Zero ? TimeSpan.Zero : _newEdge;
            if (newStart > end - MinDuration) newStart = end - MinDuration;
            overlay.Start = newStart;
            overlay.Duration = end - newStart;
        }
        else
        {
            var newEnd = _newEdge;
            if (newEnd < overlay.Start + MinDuration) newEnd = overlay.Start + MinDuration;
            overlay.Duration = newEnd - overlay.Start;
        }

        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;
        overlay.Start = _previousStart;
        overlay.Duration = _previousDuration;
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Removes a text overlay.</summary>
public class RemoveTextOverlayOperation : IEditOperation
{
    private readonly string _overlayId;
    private TextOverlaySegment? _removed;

    public string Description => "Remove Text Overlay";

    public RemoveTextOverlayOperation(string overlayId)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (_removed is not null)
            model.TextOverlays.RemoveAll(o => o.Id == _overlayId);
    }

    public void Undo(TimelineModel model)
    {
        if (_removed is null) return;
        model.TextOverlays.Add(_removed);
        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>
/// Updates one or more properties of a text overlay via an <paramref name="apply"/> callback
/// that mutates the live segment. Rather than enumerating every editable property as a
/// nullable constructor parameter (there are nearly thirty of them, covering placement,
/// typography, and every background style's parameters), the caller supplies a delegate that
/// sets whatever it needs; a full snapshot of the segment is taken before <paramref name="apply"/>
/// runs so Undo can restore every field regardless of which ones changed.
/// </summary>
public class UpdateTextOverlayPropertiesOperation : IEditOperation
{
    private readonly string _overlayId;
    private readonly Action<TextOverlaySegment> _apply;
    private readonly string? _description;

    private TextOverlaySegment? _previous;

    public string Description => _description ?? "Update Text Overlay";

    /// <param name="overlayId">Overlay to update.</param>
    /// <param name="apply">Callback that mutates the live segment's properties.</param>
    /// <param name="description">Optional undo-stack label; defaults to "Update Text Overlay".</param>
    public UpdateTextOverlayPropertiesOperation(string overlayId, Action<TextOverlaySegment> apply,
        string? description = null)
    {
        _overlayId = overlayId ?? throw new ArgumentNullException(nameof(overlayId));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _description = description;
    }

    /// <summary>Convenience for the common case of updating just the overlay's text.</summary>
    public static UpdateTextOverlayPropertiesOperation ForText(string overlayId, string text) =>
        new(overlayId, o => o.Text = text, "Update Text Overlay Text");

    public void Execute(TimelineModel model)
    {
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        // Shallow record copy: captures every property's current value as the undo snapshot
        // before apply() mutates the live segment in place.
        _previous = overlay with { };
        _apply(overlay);

        if (overlay.Start != _previous.Start || overlay.Duration != _previous.Duration)
            model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        if (_previous is null) return;
        var overlay = model.TextOverlays.FirstOrDefault(o => o.Id == _overlayId);
        if (overlay is null) return;

        // Restore every field individually onto the live instance rather than replacing the
        // list element with _previous (or writing through it) — either would leave the
        // snapshot object aliased with the live segment, so a later redo would mutate _previous
        // itself and turn undo into a no-op (see the TrimSegmentEdgeOperation fix above).
        overlay.Text = _previous.Text;
        overlay.Enabled = _previous.Enabled;
        // Inherited from TimelineSegment rather than overlay styling, but the apply action is
        // free-form so it could have touched it — restoring it keeps "undo puts back exactly
        // what Execute found" true with no exceptions to reason about.
        overlay.InTransition = _previous.InTransition;
        overlay.Animation = _previous.Animation;
        overlay.Anchor = _previous.Anchor;
        overlay.X = _previous.X;
        overlay.Y = _previous.Y;
        overlay.WidthFraction = _previous.WidthFraction;
        overlay.HeightFraction = _previous.HeightFraction;
        overlay.MarginFraction = _previous.MarginFraction;
        overlay.FontFamily = _previous.FontFamily;
        overlay.FontSize = _previous.FontSize;
        overlay.IsBold = _previous.IsBold;
        overlay.IsItalic = _previous.IsItalic;
        overlay.TextColor = _previous.TextColor;
        overlay.TextAlignment = _previous.TextAlignment;
        overlay.Background = _previous.Background;
        overlay.BackgroundColor = _previous.BackgroundColor;
        overlay.BackgroundOpacity = _previous.BackgroundOpacity;
        overlay.CornerRadius = _previous.CornerRadius;
        overlay.PaddingScale = _previous.PaddingScale;
        overlay.BlurAmount = _previous.BlurAmount;
        overlay.BlurTintOpacity = _previous.BlurTintOpacity;
        overlay.ScrimDirection = _previous.ScrimDirection;
        overlay.ScrimStrength = _previous.ScrimStrength;
        overlay.OutlineWidth = _previous.OutlineWidth;
        overlay.OutlineColor = _previous.OutlineColor;
        overlay.ShadowStrength = _previous.ShadowStrength;
        overlay.AccentColor = _previous.AccentColor;
        overlay.AccentThickness = _previous.AccentThickness;
        overlay.AccentSide = _previous.AccentSide;
        overlay.Start = _previous.Start;
        overlay.Duration = _previous.Duration;

        model.TextOverlays.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

