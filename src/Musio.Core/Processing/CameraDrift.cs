namespace Musio.Core.Processing;

/// <summary>
/// The extra camera movement contributed by <see cref="CameraDrift"/> at one instant.
/// </summary>
/// <param name="ZoomFactor">
/// Multiplier for the segment's zoom <i>depth</i>. Apply it as
/// <c>driftedZoom = 1 + (zoom - 1) * ZoomFactor</c> — never as <c>zoom * ZoomFactor</c>,
/// which would leave a residual magnification behind once the segment returns to 1×.
/// </param>
/// <param name="OffsetX">Horizontal offset to add to the zoom center, in source pixels.</param>
/// <param name="OffsetY">Vertical offset to add to the zoom center, in source pixels.</param>
public readonly record struct CameraDriftResult(float ZoomFactor, float OffsetX, float OffsetY)
{
    /// <summary>No drift: the camera is left exactly where the zoom engine put it.</summary>
    public static readonly CameraDriftResult None = new(1f, 0f, 0f);

    /// <summary>True when this result would visibly move the camera at all.</summary>
    public bool IsActive =>
        OffsetX != 0f || OffsetY != 0f || Math.Abs(ZoomFactor - 1f) > float.Epsilon;
}

/// <summary>
/// Continuous, subtle camera motion layered on top of a zoom segment so a zoomed
/// scene never sits perfectly still — the Ken Burns idea applied to a live camera
/// rather than a still photo.
/// <para>
/// Everything here is a <b>pure function of the zoom segment's progress</b>. Nothing
/// accumulates between frames, which is what lets preview scrubbing, playback, and
/// export all produce byte-identical framing for the same instant, and what keeps a
/// paused playhead completely static.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Why the motion is windowed by zoom depth.</b> Both drift layers are
/// scaled by <see cref="Window"/>, which is exactly 0 at 1× and saturates once the
/// camera is meaningfully zoomed in. That has three consequences worth keeping:</para>
/// <list type="number">
/// <item>An un-zoomed frame is guaranteed bit-stable — drift can only ever appear
/// inside a zoom segment, which is where it was asked for.</item>
/// <item>A segment necessarily returns to its exact original framing, because the
/// drift amplitude collapses to zero as the zoom releases. Applying drift
/// unconditionally is the classic bug here: the scene ends a few percent zoomed in
/// and never recovers.</item>
/// <item>There is no velocity discontinuity at the segment edges. The zoom easing
/// (<see cref="CubicBezierEasing.EaseInOutCinematic"/>) arrives at 1× with zero
/// velocity, so the windowed drift inherits a zero derivative there and fades in
/// and out without a perceptible kick.</item>
/// </list>
/// <para><b>Why the arc, not a sine.</b> Panning both axes with the same sine traces a
/// straight diagonal, and a full oscillation returns the camera to where it started —
/// a wobble rather than a drift. Tracing a partial circular arc keeps the offset
/// advancing in one direction and puts the two axes inherently 90° out of phase, so
/// the path is a genuine curve.</para>
/// </remarks>
public static class CameraDrift
{
    /// <summary>Golden angle in radians, used to decorrelate per-segment headings.</summary>
    private const float GoldenAngle = 2.39996323f;

    /// <summary>
    /// Zoom depth (above 1×) at which the drift window reaches ~63% of full
    /// amplitude. Small enough that drift is at essentially full strength for any
    /// normal zoom, while still collapsing smoothly to nothing as the camera releases.
    /// </summary>
    private const float WindowKnee = 0.2f;

    /// <summary>
    /// Fades drift in with zoom depth: exactly <c>0</c> at 1×, approaching <c>1</c>
    /// once the camera is zoomed in by more than a few percent.
    /// </summary>
    public static float Window(float zoomLevel)
    {
        float depth = zoomLevel - 1f;
        if (!float.IsFinite(depth) || depth <= 0f) return 0f;
        return 1f - MathF.Exp(-depth / WindowKnee);
    }

    /// <summary>
    /// Maps a stable per-segment seed to a unit heading vector. Returned as a vector
    /// rather than an angle so callers can blend headings across overlapping segments
    /// by averaging components — averaging raw angles breaks across the ±π wrap.
    /// </summary>
    public static (float X, float Y) HeadingFromSeed(int seed)
    {
        float angle = seed * GoldenAngle;
        return (MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>
    /// Evaluates the drift for one instant.
    /// </summary>
    /// <param name="settings">Drift configuration; disabled settings return <see cref="CameraDriftResult.None"/>.</param>
    /// <param name="segmentProgress">
    /// Normalized progress <c>[0,1]</c> through the active zoom segment. Drift is driven
    /// from segment progress rather than absolute time on purpose: a zoom segment lasts
    /// only a few seconds, and an absolute-time oscillator can land on a stationary point
    /// of its own cycle for that entire window, leaving the camera visibly parked.
    /// Segment-relative motion traverses the same arc every time, so every zoom drifts.
    /// </param>
    /// <param name="zoomLevel">Zoom level from the zoom engine, before drift.</param>
    /// <param name="viewportWidth">Visible viewport width in source pixels; sets the pan scale.</param>
    /// <param name="viewportHeight">Visible viewport height in source pixels.</param>
    /// <param name="slackX">Room (source px) between the viewport and the nearer horizontal source edge. The float is bounded by this so it never drives the viewport into the clamp, which would stall the motion.</param>
    /// <param name="slackY">Room (source px) to the nearer vertical source edge.</param>
    /// <param name="headingX">X of the drift heading (see <see cref="HeadingFromSeed"/>). Need not be normalized.</param>
    /// <param name="headingY">Y of the drift heading.</param>
    public static CameraDriftResult Evaluate(
        CameraDriftSettings settings,
        float segmentProgress,
        float zoomLevel,
        float viewportWidth,
        float viewportHeight,
        float slackX,
        float slackY,
        float headingX = 1f,
        float headingY = 0f)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled || !float.IsFinite(segmentProgress))
            return CameraDriftResult.None;

        float window = Window(zoomLevel);
        if (window <= 0f) return CameraDriftResult.None;

        float amount = Math.Clamp(settings.Strength, 0f, 4f) * window;
        if (amount <= 0f) return CameraDriftResult.None;

        float p = Math.Clamp(segmentProgress, 0f, 1f);

        // ── Breathe: a monotonic push-in across the segment ──
        // A monotonic ramp (rather than an oscillation) is what actually reads as a
        // living camera over a ~4s segment: the scale is always still moving, in one
        // direction, for the whole hold. Smoothstep keeps the velocity zero at both
        // ends so it eases in and out of the push rather than starting abruptly.
        float push = Smoothstep(p);
        float zoomFactor = 1f + Math.Max(0f, settings.ZoomAmplitude) * amount * push;

        // ── Float: an arc the camera traverses once across the segment ──
        // Parameterised as a circular arc rather than a sine so the offset keeps
        // advancing instead of retracing itself, and so the two axes are inherently
        // 90 degrees out of phase (a genuine curve, not a diagonal line).
        double sweepMax = 2.0 * Math.PI * Math.Max(0f, settings.PanSweepCycles);

        // (sin θ, 1 − cos θ) is NOT a unit vector — its magnitude is 2·sin(θ/2), which
        // peaks at 2. Normalising by that peak is what lets the slack bound below
        // actually hold: without it the offset could exceed the room available and be
        // clamped by the viewport, stalling the motion — the exact failure this bound
        // exists to prevent.
        float arcPeak = 2f * MathF.Sin((float)(Math.Min(sweepMax, Math.PI) / 2.0));
        if (arcPeak <= 1e-4f) return new CameraDriftResult(zoomFactor, 0f, 0f);

        double sweep = sweepMax * p;
        float arcX = MathF.Sin((float)sweep) / arcPeak;
        float arcY = (1f - MathF.Cos((float)sweep)) / arcPeak;

        // Rotate the arc onto this segment's heading so consecutive zooms do not all
        // drift the same way. Normalising keeps |(unitX, unitY)| <= 1.
        float headingLength = MathF.Sqrt(headingX * headingX + headingY * headingY);
        float cosH = 1f, sinH = 0f;
        if (headingLength > 1e-4f && float.IsFinite(headingLength))
        {
            cosH = headingX / headingLength;
            sinH = headingY / headingLength;
        }

        float unitX = arcX * cosH - arcY * sinH;
        float unitY = arcX * sinH + arcY * cosH;

        float slackLimit = Math.Clamp(settings.MaxSlackFraction, 0f, 1f);
        float panAmplitude = Math.Max(0f, settings.PanAmplitude) * amount;

        float amplitudeX = BoundToSlack(panAmplitude * viewportWidth, slackX, slackLimit);
        float amplitudeY = BoundToSlack(panAmplitude * viewportHeight, slackY, slackLimit);

        return new CameraDriftResult(zoomFactor, amplitudeX * unitX, amplitudeY * unitY);
    }

    /// <summary>Classic smoothstep: eases from 0 to 1 with zero velocity at both ends.</summary>
    private static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Applies a drift result to a zoom level, preserving the invariant that a 1×
    /// camera stays exactly 1×.
    /// </summary>
    public static float ApplyZoom(float zoomLevel, CameraDriftResult drift)
    {
        float depth = zoomLevel - 1f;
        if (depth <= 0f) return zoomLevel;
        return 1f + depth * drift.ZoomFactor;
    }

    /// <summary>
    /// Clamps a desired pan amplitude to the room actually available, so the float
    /// can never push the viewport against the source edge. Hitting the clamp would
    /// freeze the motion mid-swing, which reads as a glitch rather than as a camera
    /// coming to rest.
    /// </summary>
    private static float BoundToSlack(float desired, float slack, float slackFraction)
    {
        if (!float.IsFinite(desired) || desired <= 0f) return 0f;
        if (!float.IsFinite(slack) || slack <= 0f) return 0f;
        return Math.Min(desired, slack * slackFraction);
    }
}
