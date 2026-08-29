namespace Musio.Core.Timeline;

/// <summary>
/// Disables or restores a single recorded mouse click, by its raw
/// <see cref="Musio.Core.Models.ClickEvent.TimestampTicks"/>.
/// </summary>
/// <remarks>
/// <para>
/// A disabled click is treated as never having happened everywhere a click has an effect: no
/// auto-zoom shot is generated for it, no click ripple is drawn in the rendered frame, and it
/// contributes no protected span pinning the cursor path during the press. That last one is
/// the reason this is an undoable operation with a visible marker rather than a quiet flag —
/// dropping the protection can visibly pull the smoothed cursor off the thing it clicked, and
/// the user needs to be able to see what they disabled and put it back.
/// </para>
/// <para>
/// This acts on <see cref="TimelineModel.DisabledClickTicks"/>, deliberately NOT on
/// <see cref="TimelineModel.SuppressedClickTicks"/>. The latter records only that a click's
/// AUTO-ZOOM was cancelled, and every project saved before this feature carries ticks
/// accumulated from ordinary auto-zoom editing. Reusing it would have retroactively disabled
/// every one of those clicks on projects the user never touched — which is exactly what
/// happened when the two were briefly shared.
/// </para>
/// </remarks>
public class SetClickSuppressedOperation : IEditOperation
{
    private readonly long _clickTicks;
    private readonly bool _suppress;
    private bool _wasSuppressed;

    public string Description => _suppress ? "Delete Click" : "Restore Click";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public SetClickSuppressedOperation(long clickTicks, bool suppress)
    {
        _clickTicks = clickTicks;
        _suppress = suppress;
    }

    public void Execute(TimelineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _wasSuppressed = model.DisabledClickTicks.Contains(_clickTicks);

        // Asking for the state it is already in changes nothing, and must not reach the undo
        // stack — otherwise Ctrl+Z appears to do nothing.
        _changed = _wasSuppressed != _suppress;
        if (!_changed) return;

        if (_suppress)
            model.DisabledClickTicks.Add(_clickTicks);
        else
            model.DisabledClickTicks.Remove(_clickTicks);
    }

    public void Undo(TimelineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_changed) return;

        if (_wasSuppressed)
            model.DisabledClickTicks.Add(_clickTicks);
        else
            model.DisabledClickTicks.Remove(_clickTicks);
    }
}
