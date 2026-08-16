namespace Musio.Core.Timeline;

/// <summary>
/// Sets (or clears) a single boundary's <see cref="TimelineSegment.InTransition"/>, identified
/// by the incoming segment's Id — the same Id <see cref="TimelineControl"/>'s
/// <c>TransitionSelected</c>/<c>TransitionRemoveRequested</c> events carry.
/// </summary>
/// <remarks>
/// <paramref name="newConfig"/> is <c>null</c>-able because <c>null</c> and an explicit
/// <see cref="TransitionConfig"/> with <see cref="TransitionType.None"/> are semantically
/// different states (see <see cref="TimelineSegment.InTransition"/>'s remarks): the former means
/// "not configured, use the legacy fallback", the latter is an explicit hard cut that suppresses
/// even that fallback. Snapshotting the whole previous config (rather than diffing individual
/// properties) is what lets <see cref="Undo"/> restore either state exactly, including going
/// from a set value back to <c>null</c>.
/// </remarks>
public class UpdateTransitionOperation : IEditOperation
{
    private readonly string _incomingSegmentId;
    private readonly TransitionConfig? _newConfig;
    private readonly string? _description;

    private TransitionConfig? _previous;
    private bool _found;

    public string Description => _description ?? "Update Transition";

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="_found"/> already answers exactly this question — the boundary either
    /// resolved and was written, or nothing happened at all.
    /// </remarks>
    public bool ChangedModel => _found;

    /// <param name="incomingSegmentId">
    /// Id of the incoming segment that owns the boundary (<see cref="TimelineSegment.InTransition"/>).
    /// </param>
    /// <param name="newConfig">The config to apply, or <c>null</c> to clear it back to "not configured".</param>
    /// <param name="description">Optional undo-stack label; defaults to "Update Transition".</param>
    public UpdateTransitionOperation(string incomingSegmentId, TransitionConfig? newConfig, string? description = null)
    {
        _incomingSegmentId = incomingSegmentId ?? throw new ArgumentNullException(nameof(incomingSegmentId));
        _newConfig = newConfig;
        _description = description;
    }

    public void Execute(TimelineModel model)
    {
        // Index <= 0 has no incoming boundary — matches TransitionResolver's and
        // ApplyTransitionToAllBoundariesOperation's own "index <= 0 -> no transition" rule.
        // Without this guard a config written to segment 0 lies dormant on InTransition and
        // can activate later if that segment is ever reordered away from index 0.
        var index = IndexOf(model, _incomingSegmentId);
        if (index <= 0)
        {
            _found = false;
            return;
        }

        var segment = model.Segments[index];
        _found = true;
        _previous = segment.InTransition;
        segment.InTransition = _newConfig;
    }

    public void Undo(TimelineModel model)
    {
        if (!_found) return;

        // Only a MISSING segment (index < 0) can be skipped here. Unlike Execute — which must
        // refuse index 0 because a config written there would lie dormant and reactivate on a
        // later reorder — Undo is restoring a value this operation itself captured, and it must
        // land wherever the segment now lives. If an intervening reorder moved it to the front,
        // refusing would strand the applied config in place: the undo would silently do nothing,
        // and the config would come back to life the moment the segment moved off index 0 again.
        var index = IndexOf(model, _incomingSegmentId);
        if (index < 0) return;
        model.Segments[index].InTransition = _previous;
    }

    private static int IndexOf(TimelineModel model, string segmentId)
    {
        var segments = model.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Id == segmentId) return i;
        }

        return -1;
    }
}

/// <summary>
/// Applies the same <see cref="TransitionConfig"/> (or <c>null</c>, clearing it) to every
/// boundary on the timeline in one undo step — used by the properties pane's "Apply to all
/// boundaries" action, which must not push one undo entry per boundary.
/// </summary>
/// <remarks>
/// The first segment (index 0) has no leading boundary — <see cref="TimelineSegment.InTransition"/>
/// describes the boundary between a segment and its predecessor, and the first segment has none
/// — so it is always skipped, matching <see cref="Musio.Core.Timeline.TransitionResolver"/>'s own
/// "index &lt;= 0 -&gt; no transition" rule.
/// <para>
/// Each boundary's own prior value is snapshotted individually (not one shared "previous"), so
/// <see cref="Undo"/> restores boundaries that were <c>null</c> back to <c>null</c> and boundaries
/// that carried a different config back to that different config — never to whatever the last
/// boundary in the loop happened to have.
/// </para>
/// </remarks>
public class ApplyTransitionToAllBoundariesOperation : IEditOperation
{
    private readonly TransitionConfig? _newConfig;
    private readonly string? _description;

    private List<(string SegmentId, TransitionConfig? Previous)>? _previous;

    public string Description => _description ?? "Apply Transition to All Boundaries";

    public ApplyTransitionToAllBoundariesOperation(TransitionConfig? newConfig, string? description = null)
    {
        _newConfig = newConfig;
        _description = description;
    }

    public void Execute(TimelineModel model)
    {
        var snapshot = new List<(string, TransitionConfig?)>();
        for (int i = 1; i < model.Segments.Count; i++)
        {
            var segment = model.Segments[i];
            snapshot.Add((segment.Id, segment.InTransition));
            segment.InTransition = _newConfig;
        }
        _previous = snapshot;
    }

    public void Undo(TimelineModel model)
    {
        if (_previous is null) return;
        // Index once: a linear FirstOrDefault per stored boundary is O(n*m) on long timelines.
        var byId = new Dictionary<string, TimelineSegment>(model.Segments.Count);
        foreach (var segment in model.Segments) byId[segment.Id] = segment;

        foreach (var (segmentId, previous) in _previous)
        {
            if (byId.TryGetValue(segmentId, out var segment))
                segment.InTransition = previous;
        }
    }
}