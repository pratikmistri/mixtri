namespace Mixtri.Core.Processing;

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
///   <item>two per protected click press, carrying <b>zero</b> — a click is a moment the
///   recording, the touch indicator and auto-zoom all agree on, so by default an anchor is not
///   allowed to drag the pointer off one;</item>
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
/// <para>
/// <b>Clicks near an anchor are absorbed rather than protected.</b> Protection is only
/// meaningful when there is room to blend into it. A click press is a down/up PAIR a few frames
/// apart, so an anchor placed on or beside one would otherwise have to deliver its whole
/// displacement and take it away again within a handful of frames — which does not read as a
/// repositioned cursor, it reads as a flash. Any press within <see cref="MinRampSeconds"/> of an
/// anchor therefore contributes no control points at all and simply travels with the cursor,
/// which is also what someone repositioning a cursor at a click actually wants: they are moving
/// it off whatever it was occluding, and the click belongs with it.
/// </para>
/// </remarks>
public static class CursorPathWarp
{
    /// <summary>
    /// Shortest span over which a displacement may be introduced or removed. A click press
    /// closer than this to an anchor is absorbed into the anchor's motion instead of pinning it.
    /// </summary>
    /// <remarks>
    /// This is a FLOOR, not the usual ramp: with no click nearby the blend runs all the way to
    /// the neighbouring anchor or the end of the recording. It exists only to stop the geometry
    /// from demanding a move that is too fast to read as movement.
    /// </remarks>
    public const double MinRampSeconds = 0.25;

    /// <summary>An anchor's target position for one frame of the path, in capture-frame pixels.</summary>
    /// <param name="FrameIndex">Index into the smoothed path.</param>
    /// <param name="X">Target horizontal position, capture-frame pixels.</param>
    /// <param name="Y">Target vertical position, capture-frame pixels.</param>
    public readonly record struct AnchorPoint(int FrameIndex, double X, double Y);

    /// <summary>
    /// One button press, from button-down to the matching button-up.
    /// </summary>
    /// <remarks>
    /// Deliberately a span rather than two independent instants. The recorder stores down and up
    /// as separate <c>ClickEvent</c>s tens of milliseconds apart, and treating them separately
    /// let an anchor claim the down while the up still pinned the path — the exact shape of a
    /// visible snap. A press is protected, or absorbed, as one thing.
    /// </remarks>
    /// <param name="StartFrame">Frame of the button-down.</param>
    /// <param name="EndFrame">Frame of the button-up; equal to <paramref name="StartFrame"/> for an unpaired event.</param>
    public readonly record struct ClickSpan(int StartFrame, int EndFrame);

    private readonly record struct ControlPoint(int FrameIndex, double DeltaX, double DeltaY, bool IsAnchor);

    /// <summary>
    /// Returns <paramref name="basePath"/> displaced by <paramref name="anchors"/>.
    /// </summary>
    /// <param name="basePath">
    /// The pristine smoothed path. Never mutated — callers keep it so that re-warping after an
    /// edit starts from the recording again rather than compounding the previous warp.
    /// </param>
    /// <param name="anchors">Anchor targets, in capture-frame pixels. Order does not matter.</param>
    /// <param name="clicks">
    /// Recorded click presses. Each contributes zero-displacement control points unless an anchor
    /// is close enough to absorb it.
    /// </param>
    /// <param name="outputFps">
    /// Output frame rate, used to convert <see cref="MinRampSeconds"/> into frames and to scale
    /// the velocity correction.
    /// </param>
    public static List<SmoothedPosition> Apply(
        IReadOnlyList<SmoothedPosition> basePath,
        IReadOnlyList<AnchorPoint> anchors,
        IReadOnlyList<ClickSpan> clicks,
        double outputFps)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(clicks);

        var result = new List<SmoothedPosition>(basePath);
        if (anchors.Count == 0 || result.Count == 0) return result;

        var controls = BuildControlPoints(basePath, anchors, clicks, outputFps);
        if (controls.Count == 0) return result;

        var displacement = BuildDisplacementField(controls, result.Count);
        ApplyDisplacement(result, basePath, displacement, outputFps);
        return result;
    }

    /// <summary>
    /// Merges anchors, the zero-nodes of every protected click press, and the two end zero-nodes
    /// into one list sorted by frame index, with at most one control point per index.
    /// </summary>
    private static List<ControlPoint> BuildControlPoints(
        IReadOnlyList<SmoothedPosition> basePath,
        IReadOnlyList<AnchorPoint> anchors,
        IReadOnlyList<ClickSpan> clicks,
        double outputFps)
    {
        int last = basePath.Count - 1;
        var byIndex = new Dictionary<int, ControlPoint>();
        var anchorFrames = new List<int>(anchors.Count);

        foreach (var anchor in anchors)
        {
            int i = Math.Clamp(anchor.FrameIndex, 0, last);
            var at = basePath[i];

            // A later anchor at the same frame simply wins; two anchors cannot share an
            // instant, and silently keeping the older one would make the drag look ignored.
            byIndex[i] = new ControlPoint(i, anchor.X - at.X, anchor.Y - at.Y, IsAnchor: true);
            anchorFrames.Add(i);
        }

        int minRamp = outputFps > 0
            ? Math.Max(1, (int)Math.Round(MinRampSeconds * outputFps))
            : 1;

        foreach (var press in clicks)
        {
            int start = Math.Clamp(Math.Min(press.StartFrame, press.EndFrame), 0, last);
            int end = Math.Clamp(Math.Max(press.StartFrame, press.EndFrame), 0, last);

            // Absorbed: this press travels with the cursor instead of pinning it. Decided for
            // the press as a WHOLE — pinning one end of it and not the other is what produced
            // the snap this rule exists to remove.
            if (IsAbsorbed(anchorFrames, start, end, minRamp)) continue;

            AddZeroNode(byIndex, start);
            AddZeroNode(byIndex, end);
        }

        // Ends last, and only where nothing has claimed them, so an anchor sitting on the very
        // first or last frame is not overwritten by a zero it would then have to fight.
        AddZeroNode(byIndex, 0);
        AddZeroNode(byIndex, last);

        var controls = new List<ControlPoint>(byIndex.Values);
        controls.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return controls;

        static void AddZeroNode(Dictionary<int, ControlPoint> byIndex, int frame)
        {
            if (frame < 0 || byIndex.ContainsKey(frame)) return;
            byIndex[frame] = new ControlPoint(frame, 0, 0, IsAnchor: false);
        }
    }

    private static bool IsAbsorbed(List<int> anchorFrames, int start, int end, int minRamp)
    {
        foreach (int anchor in anchorFrames)
        {
            if (anchor >= start - minRamp && anchor <= end + minRamp) return true;
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
