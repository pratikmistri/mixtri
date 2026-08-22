namespace Musio.Core.Processing;

/// <summary>
/// Applies user-authored cursor anchors to a smoothed cursor path as a <b>displacement
/// field</b>: the recorded journey is offset, never replaced.
/// </summary>
/// <remarks>
/// <para>
/// The field is built from a sorted list of control points, each pairing a frame index with a
/// displacement:
/// </para>
/// <list type="bullet">
///   <item>one per anchor, carrying <c>target - recordedPosition</c> at that frame;</item>
///   <item>one per click, carrying <b>zero</b> — a click is a moment the recording and the
///   render agree on (the touch indicator draws at the raw click point, and auto-zoom aims at
///   it), so an anchor is never allowed to drag the pointer off a click;</item>
///   <item>one at each end of the path, carrying zero, so an anchor with no click beyond it
///   still eases out instead of dragging the tail of the recording with it.</item>
/// </list>
/// <para>
/// Between two control points the displacement is smoothstep-interpolated. Two consequences
/// are worth stating because they are the whole design:
/// </para>
/// <list type="number">
///   <item>The influence window is <b>structural</b> — it is exactly the gap to the
///   neighbouring anchor or click. There is no window parameter to store, tune, or get wrong,
///   and multiple anchors compose without any special case.</item>
///   <item>Smoothstep has zero derivative at both ends, so the field is C1 across every
///   control point: the warp introduces no velocity discontinuity, and outside the outermost
///   control points the path is left bit-for-bit alone.</item>
/// </list>
/// </remarks>
public static class CursorPathWarp
{
    /// <summary>
    /// How close an anchor has to be to a click before the two are treated as the same moment.
    /// </summary>
    public const double AnchorClickMergeSeconds = 0.04;

    /// <summary>An anchor's target position for one frame of the path, in capture-frame pixels.</summary>
    /// <param name="FrameIndex">Index into the smoothed path.</param>
    /// <param name="X">Target horizontal position, capture-frame pixels.</param>
    /// <param name="Y">Target vertical position, capture-frame pixels.</param>
    public readonly record struct AnchorPoint(int FrameIndex, double X, double Y);

    private readonly record struct ControlPoint(int FrameIndex, double DeltaX, double DeltaY, bool IsAnchor);

    /// <summary>
    /// Returns <paramref name="basePath"/> displaced by <paramref name="anchors"/>.
    /// </summary>
    /// <param name="basePath">
    /// The pristine smoothed path. Never mutated — callers keep it so that re-warping after an
    /// edit starts from the recording again rather than compounding the previous warp.
    /// </param>
    /// <param name="anchors">Anchor targets, in capture-frame pixels. Order does not matter.</param>
    /// <param name="clickFrames">
    /// Frame indices of click events. Each contributes a zero-displacement control point unless
    /// an anchor claims the same moment.
    /// </param>
    /// <param name="outputFps">
    /// Output frame rate, used to convert the anchor/click merge window into frames and to
    /// scale the velocity correction.
    /// </param>
    public static List<SmoothedPosition> Apply(
        IReadOnlyList<SmoothedPosition> basePath,
        IReadOnlyList<AnchorPoint> anchors,
        IReadOnlyList<int> clickFrames,
        double outputFps)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(clickFrames);

        var result = new List<SmoothedPosition>(basePath);
        if (anchors.Count == 0 || result.Count == 0) return result;

        var controls = BuildControlPoints(basePath, anchors, clickFrames, outputFps);
        if (controls.Count == 0) return result;

        var displacement = BuildDisplacementField(controls, result.Count);
        ApplyDisplacement(result, basePath, displacement, outputFps);
        return result;
    }

    /// <summary>
    /// Merges anchors, click zero-nodes and the two end zero-nodes into one list sorted by
    /// frame index, with at most one control point per index.
    /// </summary>
    private static List<ControlPoint> BuildControlPoints(
        IReadOnlyList<SmoothedPosition> basePath,
        IReadOnlyList<AnchorPoint> anchors,
        IReadOnlyList<int> clickFrames,
        double outputFps)
    {
        int last = basePath.Count - 1;
        var byIndex = new Dictionary<int, ControlPoint>();

        foreach (var anchor in anchors)
        {
            int i = Math.Clamp(anchor.FrameIndex, 0, last);
            var at = basePath[i];

            // A later anchor at the same frame simply wins; two anchors cannot share an
            // instant, and silently keeping the older one would make the drag look ignored.
            byIndex[i] = new ControlPoint(i, anchor.X - at.X, anchor.Y - at.Y, IsAnchor: true);
        }

        // Anchors are resolved first so this window can be measured against the final set.
        int mergeFrames = outputFps > 0
            ? Math.Max(1, (int)Math.Round(AnchorClickMergeSeconds * outputFps))
            : 1;

        foreach (int clickFrame in clickFrames)
        {
            int i = Math.Clamp(clickFrame, 0, last);
            if (byIndex.ContainsKey(i)) continue;

            // An anchor placed ON a click is an unambiguous instruction to move the pointer
            // there, so it outranks the protect-the-click rule and that click's zero-node is
            // dropped. This costs nothing visually for a mouse cursor: the click animation is
            // a scale applied at the pointer's own position, not a separately-placed ripple.
            if (HasAnchorWithin(byIndex, i, mergeFrames)) continue;

            byIndex[i] = new ControlPoint(i, 0, 0, IsAnchor: false);
        }

        // Ends last, and only where nothing has claimed them, so an anchor sitting on the very
        // first or last frame is not overwritten by a zero it would then have to fight.
        if (!byIndex.ContainsKey(0)) byIndex[0] = new ControlPoint(0, 0, 0, IsAnchor: false);
        if (!byIndex.ContainsKey(last)) byIndex[last] = new ControlPoint(last, 0, 0, IsAnchor: false);

        var controls = new List<ControlPoint>(byIndex.Values);
        controls.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return controls;
    }

    private static bool HasAnchorWithin(Dictionary<int, ControlPoint> byIndex, int frame, int window)
    {
        for (int offset = 1; offset <= window; offset++)
        {
            if (byIndex.TryGetValue(frame - offset, out var before) && before.IsAnchor) return true;
            if (byIndex.TryGetValue(frame + offset, out var after) && after.IsAnchor) return true;
        }
        return false;
    }

    /// <summary>
    /// Expands the control points into a per-frame displacement, smoothstep-interpolated
    /// between neighbours.
    /// </summary>
    private static (double X, double Y)[] BuildDisplacementField(List<ControlPoint> controls, int frameCount)
    {
        var field = new (double X, double Y)[frameCount];

        // Before the first and after the last control point the field holds that point's own
        // value. Both ends are normally zero nodes, so this is only reachable when an anchor
        // sits on frame 0 or the final frame — where "hold it" is the only sensible answer,
        // there being no recording on the far side to blend back into.
        var first = controls[0];
        for (int i = 0; i < first.FrameIndex && i < frameCount; i++)
            field[i] = (first.DeltaX, first.DeltaY);

        for (int c = 0; c < controls.Count - 1; c++)
        {
            var a = controls[c];
            var b = controls[c + 1];
            int span = b.FrameIndex - a.FrameIndex;
            if (span <= 0) continue;

            for (int i = a.FrameIndex; i <= b.FrameIndex; i++)
            {
                double t = (double)(i - a.FrameIndex) / span;
                double w = Smoothstep(t);
                field[i] = (
                    a.DeltaX + ((b.DeltaX - a.DeltaX) * w),
                    a.DeltaY + ((b.DeltaY - a.DeltaY) * w));
            }
        }

        var lastControl = controls[^1];
        for (int i = lastControl.FrameIndex; i < frameCount; i++)
            field[i] = (lastControl.DeltaX, lastControl.DeltaY);

        return field;
    }

    /// <summary>
    /// Offsets each position and corrects its velocity by the displacement's own rate of change.
    /// </summary>
    /// <remarks>
    /// Velocity drives cursor tilt and shutter motion blur, so displacing positions without it
    /// would make the pointer lean and smear along its <i>recorded</i> direction of travel while
    /// visibly moving along a different one. Adding the derivative of the displacement — rather
    /// than recomputing velocity from the warped path — is what keeps unwarped frames exactly as
    /// the smoother produced them, since a constant-zero displacement has a zero derivative.
    /// </remarks>
    private static void ApplyDisplacement(
        List<SmoothedPosition> path,
        IReadOnlyList<SmoothedPosition> basePath,
        (double X, double Y)[] field,
        double outputFps)
    {
        int n = path.Count;
        double fps = outputFps > 0 ? outputFps : 30.0;

        for (int i = 0; i < n; i++)
        {
            var p = basePath[i];
            var d = field[i];

            // Central difference where both neighbours exist, one-sided at the ends.
            int lo = Math.Max(0, i - 1);
            int hi = Math.Min(n - 1, i + 1);
            int steps = hi - lo;
            double dvx = 0, dvy = 0;
            if (steps > 0)
            {
                dvx = (field[hi].X - field[lo].X) * fps / steps;
                dvy = (field[hi].Y - field[lo].Y) * fps / steps;
            }

            path[i] = new SmoothedPosition
            {
                X = p.X + d.X,
                Y = p.Y + d.Y,
                TimestampSeconds = p.TimestampSeconds,
                VelocityX = p.VelocityX + dvx,
                VelocityY = p.VelocityY + dvy,
                Shape = p.Shape,
            };
        }
    }

    private static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }
}
