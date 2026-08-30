namespace Mixtri.Core.Timeline;

/// <summary>
/// Shared auto→manual click-suppression logic for zoom-keyframe edits. When a user edits an
/// auto-generated keyframe (drag, resize, or property change) for the first time, the underlying
/// auto-zoom click must be suppressed so the auto-zoom engine doesn't regenerate a competing
/// segment for it; undo must restore the suppression exactly as it was. This was previously
/// duplicated, identically, across 4 zoom operations × Execute/Undo.
/// </summary>
internal static class ZoomOperationHelpers
{
    /// <summary>
    /// Suppresses the source click if the keyframe being edited was auto-generated (not manual)
    /// and has a recorded source click. No-ops for already-manual keyframes or ones with no
    /// source click (manually-added keyframes).
    /// </summary>
    public static void SuppressClickIfAutoToManual(TimelineModel model, bool wasManual, long? sourceClickTicks)
    {
        if (!wasManual && sourceClickTicks is long ticks)
            model.SuppressedClickTicks.Add(ticks);
    }

    /// <summary>Undo mirror of <see cref="SuppressClickIfAutoToManual"/>.</summary>
    public static void RestoreClickSuppression(TimelineModel model, bool wasManual, long? sourceClickTicks)
    {
        if (!wasManual && sourceClickTicks is long ticks)
            model.SuppressedClickTicks.Remove(ticks);
    }
}
