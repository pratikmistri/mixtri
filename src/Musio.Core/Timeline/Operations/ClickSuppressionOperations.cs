namespace Musio.Core.Timeline;

/// <summary>
/// Disables or restores a single recorded mouse click, by its raw
/// <see cref="Musio.Core.Models.ClickEvent.TimestampTicks"/>.
/// </summary>
/// <remarks>
/// <para>
/// A suppressed click is treated as never having happened everywhere a click has an effect:
/// no auto-zoom shot is generated for it, no click ripple is drawn in the rendered frame, and
/// it contributes no protected span pinning the cursor path during the press. That last one is
/// the reason this is an undoable operation with a visible marker rather than a quiet flag —
/// dropping the protection can visibly pull the smoothed cursor off the thing it clicked, and
/// the user needs to be able to see what they disabled and put it back.
/// </para>
/// <para>
/// The suppression set is shared with the auto-zoom machinery, which suppresses a click when
/// its generated segment is first edited or deleted (see <c>ZoomOperationHelpers</c>). Undo
/// therefore restores the PREVIOUS membership rather than unconditionally removing the tick:
/// a click that was already suppressed because its auto-zoom had been edited must stay
/// suppressed when a redundant suppress is undone, or undoing one action would silently revive
/// an auto-zoom the user removed by a completely different route.
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

        _wasSuppressed = model.SuppressedClickTicks.Contains(_clickTicks);

        // Asking for the state it is already in changes nothing, and must not reach the undo
        // stack — otherwise Ctrl+Z appears to do nothing.
        _changed = _wasSuppressed != _suppress;
        if (!_changed) return;

        if (_suppress)
            model.SuppressedClickTicks.Add(_clickTicks);
        else
            model.SuppressedClickTicks.Remove(_clickTicks);
    }

    public void Undo(TimelineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_changed) return;

        if (_wasSuppressed)
            model.SuppressedClickTicks.Add(_clickTicks);
        else
            model.SuppressedClickTicks.Remove(_clickTicks);
    }
}
