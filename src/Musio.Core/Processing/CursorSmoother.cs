using Musio.Core.Models;

namespace Musio.Core.Processing;

public enum SmoothingAlgorithm { None, SpringPhysics, OneEuroFilter, CatmullRomSpline, ZeroPhaseSpring }
public enum SmoothingStrength { None, Subtle, Medium, Smooth, UltraSmooth }

public struct SmoothedPosition
{
    public double X;
    public double Y;
    public double TimestampSeconds;
    public double VelocityX;
    public double VelocityY;
    public CursorShape Shape;
}

/// <summary>
/// Tunable parameters for the de-stutter pre-pass that removes trackpad
/// "stop and go" micro-stalls while preserving intentional pauses.
/// </summary>
public sealed class DestutterOptions
{
    /// <summary>Speed below which motion is considered a stall (pixels/second).</summary>
    public double StallSpeedPxPerSec { get; set; } = 45.0;

    /// <summary>Half-width of the window used to measure local speed (seconds).</summary>
    public double SpeedWindowSeconds { get; set; } = 0.03;

    /// <summary>A low-speed run shorter than this is ignored (treated as motion noise).</summary>
    public double MinStallSeconds { get; set; } = 0.04;

    /// <summary>A low-speed run at least this long is a genuine rest and is preserved.</summary>
    public double GenuineDwellSeconds { get; set; } = 0.40;

    /// <summary>Time before a click during which a stall is protected (preserved).</summary>
    public double ClickPreGuardSeconds { get; set; } = 0.18;

    /// <summary>Time after a click during which a stall is protected (preserved).</summary>
    public double ClickPostGuardSeconds { get; set; } = 0.12;

    /// <summary>
    /// Blend between constant-speed-along-arc (0) and full ease-in-out (1) when
    /// re-timing a motion gesture. Higher values decelerate more into rests/clicks.
    /// </summary>
    public double EaseStrength { get; set; } = 0.6;
}

public class CursorSmoother
{
    public SmoothingAlgorithm Algorithm { get; set; }
    public SmoothingStrength Strength { get; set; }

    /// <summary>
    /// When enabled, a de-stutter pre-pass runs before the smoothing filter to
    /// detect trackpad "stop and go" micro-stalls and glide the cursor through
    /// them, while preserving intentional pauses (clicks and long dwells).
    /// </summary>
    public bool DestutterEnabled { get; set; } = true;

    /// <summary>Tunable parameters for the de-stutter pre-pass.</summary>
    public DestutterOptions Destutter { get; set; } = new();

    public List<SmoothedPosition> SmoothPath(MouseRecordingData rawData, int targetFps)
    {
        ArgumentNullException.ThrowIfNull(rawData);
        if (targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (rawData.Samples.Count == 0) return [];

        bool smoothingActive = Algorithm != SmoothingAlgorithm.None && Strength != SmoothingStrength.None;

        // De-stutter is a time-preserving position remap: it only runs when a
        // smoothing filter is active so the None/passthrough path stays exact.
        MouseRecordingData data = smoothingActive && DestutterEnabled
            ? ApplyDestutter(rawData)
            : rawData;

        double tickFreq = data.TickFrequency;
        long startTick = data.StartTimestampTicks;
        long endTick = data.EndTimestampTicks;
        double durationSeconds = (endTick - startTick) / tickFreq;
        if (durationSeconds <= 0) durationSeconds = 0;

        int frameCount = Math.Max(1, (int)Math.Round(durationSeconds * targetFps));

        List<SmoothedPosition> result;

        if (!smoothingActive)
            result = GenerateUnsmoothed(data, targetFps, frameCount, startTick, tickFreq);
        else
            result = Algorithm switch
            {
                SmoothingAlgorithm.SpringPhysics => SmoothSpringPhysics(data, targetFps, frameCount, startTick, tickFreq),
                SmoothingAlgorithm.OneEuroFilter => SmoothOneEuroFilter(data, targetFps, frameCount, startTick, tickFreq),
                SmoothingAlgorithm.CatmullRomSpline => SmoothCatmullRom(data, targetFps, frameCount, startTick, tickFreq),
                SmoothingAlgorithm.ZeroPhaseSpring => SmoothZeroPhaseSpring(data, targetFps, frameCount, startTick, tickFreq),
                _ => GenerateUnsmoothed(data, targetFps, frameCount, startTick, tickFreq),
            };

        // Snap cursor to raw position on click events so the smoothed cursor
        // is at the correct location when click animations play.
        if (smoothingActive)
            SnapToClickPositions(result, data, targetFps, startTick, tickFreq);

        AssignShapes(result, data, startTick, tickFreq, targetFps);

        return result;
    }

    /// <summary>
    /// Minimum time a cursor shape must persist before it is shown. Shorter excursions
    /// are treated as flicker (the OS cursor briefly changing at element boundaries,
    /// captured by the shape poller) and are suppressed so the rendered cursor does not
    /// pop between glyphs frame-to-frame.
    /// </summary>
    private const double MinShapeHoldSeconds = 0.12;

    /// <summary>
    /// Assigns each output position the cursor shape of the temporally nearest source
    /// sample, then debounces the per-frame shape track so brief flickers are removed.
    /// Shape is discrete metadata, so it is matched by nearest-neighbour rather than
    /// interpolated. Samples are ordered by timestamp, enabling a single forward scan.
    /// </summary>
    private static void AssignShapes(
        List<SmoothedPosition> result, MouseRecordingData data, long startTick, double tickFreq, int targetFps)
    {
        var samples = data.Samples;
        if (result.Count == 0 || samples.Count == 0 || tickFreq <= 0)
            return;

        int j = 0;
        for (int i = 0; i < result.Count; i++)
        {
            long tick = startTick + (long)(result[i].TimestampSeconds * tickFreq);

            // Advance j to the last sample whose timestamp is <= tick.
            while (j + 1 < samples.Count && samples[j + 1].TimestampTicks <= tick)
                j++;

            int nearest = j;
            // The next sample may be closer in time than the current one.
            if (j + 1 < samples.Count &&
                Math.Abs(samples[j + 1].TimestampTicks - tick) < Math.Abs(samples[j].TimestampTicks - tick))
            {
                nearest = j + 1;
            }

            var p = result[i];
            p.Shape = samples[nearest].Shape;
            result[i] = p;
        }

        StabilizeShapes(result, Math.Max(2, (int)Math.Round(MinShapeHoldSeconds * targetFps)));
    }

    /// <summary>
    /// Debounces the per-frame shape track: any run of frames whose shape persists for
    /// fewer than <paramref name="minHoldFrames"/> is replaced with the most recent
    /// shape that did meet the hold threshold (forward fill). This removes single-frame
    /// glyph "pops" caused by transient OS-cursor flicker while preserving genuine,
    /// sustained shape changes (link hover, text I-beam, resize, ...).
    /// </summary>
    private static void StabilizeShapes(List<SmoothedPosition> result, int minHoldFrames)
    {
        if (result.Count == 0 || minHoldFrames <= 1)
            return;

        // Seed the stable shape with the first run that meets the hold threshold; if no
        // run qualifies, fall back to the first frame's shape.
        CursorShape stable = result[0].Shape;
        {
            int s = 0;
            while (s < result.Count)
            {
                int e = s;
                while (e + 1 < result.Count && result[e + 1].Shape == result[s].Shape) e++;
                if (e - s + 1 >= minHoldFrames) { stable = result[s].Shape; break; }
                s = e + 1;
            }
        }

        int i = 0;
        while (i < result.Count)
        {
            int runEnd = i;
            while (runEnd + 1 < result.Count && result[runEnd + 1].Shape == result[i].Shape)
                runEnd++;

            int runLen = runEnd - i + 1;
            if (runLen >= minHoldFrames)
            {
                // Long enough to be a real shape: it becomes the new stable shape.
                stable = result[i].Shape;
            }
            else
            {
                // Flicker: overwrite this short run with the last stable shape.
                for (int k = i; k <= runEnd; k++)
                {
                    var p = result[k];
                    p.Shape = stable;
                    result[k] = p;
                }
            }

            i = runEnd + 1;
        }
    }

    // Returns raw positions sampled at the target frame rate without any smoothing.
    private static List<SmoothedPosition> GenerateUnsmoothed(
        MouseRecordingData rawData, int targetFps, int frameCount,
        long startTick, double tickFreq)
    {
        var samples = rawData.Samples;
        var result = new List<SmoothedPosition>(frameCount);
        double dt = 1.0 / targetFps;

        for (int i = 0; i < frameCount; i++)
        {
            double t = i * dt;
            long tick = startTick + (long)(t * tickFreq);
            var (x, y) = InterpolateRawPosition(samples, tick);

            double vx = 0, vy = 0;
            if (i > 0)
            {
                var prev = result[i - 1];
                vx = (x - prev.X) / dt;
                vy = (y - prev.Y) / dt;
            }

            result.Add(new SmoothedPosition
            {
                X = x,
                Y = y,
                TimestampSeconds = t,
                VelocityX = vx,
                VelocityY = vy,
            });
        }

        return result;
    }

    #region Spring Physics

    private List<SmoothedPosition> SmoothSpringPhysics(
        MouseRecordingData rawData, int targetFps, int frameCount,
        long startTick, double tickFreq)
    {
        var (k, b, mass) = GetSpringParams(Strength);
        var samples = rawData.Samples;
        var result = new List<SmoothedPosition>(frameCount);
        double dt = 1.0 / targetFps;

        // Initialize to first sample position with estimated initial velocity
        // so the spring doesn't start from rest when the mouse is already moving.
        double posX = samples[0].X;
        double posY = samples[0].Y;
        double velX = 0, velY = 0;

        if (samples.Count >= 2)
        {
            double timeDelta = (samples[1].TimestampTicks - samples[0].TimestampTicks) / tickFreq;
            if (timeDelta > 0)
            {
                velX = (samples[1].X - samples[0].X) / timeDelta;
                velY = (samples[1].Y - samples[0].Y) / timeDelta;
            }
        }

        for (int i = 0; i < frameCount; i++)
        {
            double t = i * dt;
            long tick = startTick + (long)(t * tickFreq);
            var (targetX, targetY) = InterpolateRawPosition(samples, tick);

            // Damped spring: F = -k*displacement - b*velocity
            double forceX = -k * (posX - targetX) - b * velX;
            double forceY = -k * (posY - targetY) - b * velY;

            velX += forceX / mass * dt;
            velY += forceY / mass * dt;

            posX += velX * dt;
            posY += velY * dt;

            result.Add(new SmoothedPosition
            {
                X = posX,
                Y = posY,
                TimestampSeconds = t,
                VelocityX = velX,
                VelocityY = velY,
            });
        }

        return result;
    }

    private static (double k, double b, double mass) GetSpringParams(SmoothingStrength strength) =>
        strength switch
        {
            // NOTE: damping is intentionally kept at the original (slightly underdamped)
            // values. Increasing b to critically damp the spring measurably increases
            // the cursor's tracking lag (steady-state lag ≈ (b/k)·velocity), which shows
            // up as the rendered cursor trailing the real on-screen interaction. The
            // small overshoot from underdamping is not what users perceive as jitter —
            // that was shape-track flicker, handled by StabilizeShapes.
            SmoothingStrength.Subtle => (400, 28, 1.0),
            SmoothingStrength.Medium => (250, 22, 1.0),
            SmoothingStrength.Smooth => (150, 16, 1.0),
            SmoothingStrength.UltraSmooth => (80, 12, 1.0),
            _ => (400, 28, 1.0),
        };

    #endregion

    #region Zero-Phase Spring (offline, no lag)

    // Stiffness per strength for the zero-phase spring. Because the filter is applied
    // forward AND backward (so it has zero net lag) it can use a much stiffer spring than
    // the causal SmoothSpringPhysics without trailing the real cursor. Higher k preserves
    // more peak velocity (snappier) but removes less trackpad stop-and-go; lower k is
    // smoother. ~350 is the sweet spot: stop-and-go essentially gone, good velocity,
    // cursor still lands on target on time.
    private static double GetZeroPhaseStiffness(SmoothingStrength strength) =>
        strength switch
        {
            SmoothingStrength.Subtle => 420,
            SmoothingStrength.Medium => 300,
            SmoothingStrength.Smooth => 200,
            SmoothingStrength.UltraSmooth => 130,
            _ => 200,
        };

    /// <summary>
    /// Zero-phase (forward-backward) critically-damped spring smoothing. Because the
    /// whole recording is available offline, the spring is run forward over the resampled
    /// path and then backward over the result; the two passes cancel each other's phase
    /// lag, so the cursor is smoothed (no trackpad stop-and-go) without trailing in time —
    /// it reaches each destination on schedule. Integrated with fixed substeps for
    /// stability at high stiffness and across frame rates.
    /// </summary>
    private List<SmoothedPosition> SmoothZeroPhaseSpring(
        MouseRecordingData rawData, int targetFps, int frameCount,
        long startTick, double tickFreq)
    {
        var samples = rawData.Samples;
        double dt = 1.0 / targetFps;

        // Resample the raw path at the target frame rate.
        var px = new double[frameCount];
        var py = new double[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            long tick = startTick + (long)(i * dt * tickFreq);
            var (x, y) = InterpolateRawPosition(samples, tick);
            px[i] = x; py[i] = y;
        }

        double k = GetZeroPhaseStiffness(Strength);
        double b = 2.0 * Math.Sqrt(k); // critical damping (mass = 1), no overshoot

        // Forward pass, then backward pass over the forward result → zero net phase lag.
        var fwd = SpringPass(px, py, frameCount, dt, k, b, forward: true);
        var both = SpringPass(fwd.x, fwd.y, frameCount, dt, k, b, forward: false);

        var result = new List<SmoothedPosition>(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            double vx = 0, vy = 0;
            if (i > 0)
            {
                vx = (both.x[i] - both.x[i - 1]) / dt;
                vy = (both.y[i] - both.y[i - 1]) / dt;
            }
            result.Add(new SmoothedPosition
            {
                X = both.x[i],
                Y = both.y[i],
                TimestampSeconds = i * dt,
                VelocityX = vx,
                VelocityY = vy,
            });
        }

        return result;
    }

    // One causal damped-spring pass over a position array (optionally reversed), using
    // fixed substeps so the explicit integrator stays stable at high stiffness.
    private static (double[] x, double[] y) SpringPass(
        double[] inX, double[] inY, int count, double dt, double k, double b, bool forward)
    {
        const int substeps = 8;
        double h = dt / substeps;

        var outX = new double[count];
        var outY = new double[count];
        if (count == 0) return (outX, outY);

        int first = forward ? 0 : count - 1;
        int step = forward ? 1 : -1;

        double posX = inX[first], posY = inY[first], velX = 0, velY = 0;
        outX[first] = posX; outY[first] = posY;

        for (int n = 1; n < count; n++)
        {
            int i = forward ? n : count - 1 - n;
            double targetX = inX[i], targetY = inY[i];
            for (int s = 0; s < substeps; s++)
            {
                double ax = -k * (posX - targetX) - b * velX;
                double ay = -k * (posY - targetY) - b * velY;
                velX += ax * h; velY += ay * h;
                posX += velX * h; posY += velY * h;
            }
            outX[i] = posX; outY[i] = posY;
        }

        return (outX, outY);
    }

    #endregion

    #region One Euro Filter

    private List<SmoothedPosition> SmoothOneEuroFilter(
        MouseRecordingData rawData, int targetFps, int frameCount,
        long startTick, double tickFreq)
    {
        var (minCutoff, beta, dCutoff) = GetOneEuroParams(Strength);
        var samples = rawData.Samples;
        var result = new List<SmoothedPosition>(frameCount);
        double dt = 1.0 / targetFps;

        var filterX = new OneEuroState();
        var filterY = new OneEuroState();

        for (int i = 0; i < frameCount; i++)
        {
            double t = i * dt;
            long tick = startTick + (long)(t * tickFreq);
            var (rawX, rawY) = InterpolateRawPosition(samples, tick);

            double smoothX, smoothY;

            if (i == 0)
            {
                filterX.PrevFiltered = rawX;
                filterX.PrevDerivFiltered = 0;
                filterY.PrevFiltered = rawY;
                filterY.PrevDerivFiltered = 0;
                smoothX = rawX;
                smoothY = rawY;
            }
            else
            {
                smoothX = ApplyOneEuro(ref filterX, rawX, dt, minCutoff, beta, dCutoff);
                smoothY = ApplyOneEuro(ref filterY, rawY, dt, minCutoff, beta, dCutoff);
            }

            double vx = 0, vy = 0;
            if (i > 0)
            {
                var prev = result[i - 1];
                vx = (smoothX - prev.X) / dt;
                vy = (smoothY - prev.Y) / dt;
            }

            result.Add(new SmoothedPosition
            {
                X = smoothX,
                Y = smoothY,
                TimestampSeconds = t,
                VelocityX = vx,
                VelocityY = vy,
            });
        }

        return result;
    }

    private struct OneEuroState
    {
        public double PrevFiltered;
        public double PrevDerivFiltered;
    }

    private static double ApplyOneEuro(ref OneEuroState state, double rawValue,
        double dt, double minCutoff, double beta, double dCutoff)
    {
        // Derivative of the raw signal
        double derivative = (rawValue - state.PrevFiltered) / dt;

        // Filter the derivative
        double alphaD = ComputeAlpha(dt, dCutoff);
        double filteredDeriv = alphaD * derivative + (1.0 - alphaD) * state.PrevDerivFiltered;
        state.PrevDerivFiltered = filteredDeriv;

        // Adaptive cutoff based on filtered derivative magnitude
        double cutoff = minCutoff + beta * Math.Abs(filteredDeriv);

        // Filter the signal
        double alpha = ComputeAlpha(dt, cutoff);
        double filtered = alpha * rawValue + (1.0 - alpha) * state.PrevFiltered;
        state.PrevFiltered = filtered;

        return filtered;
    }

    private static double ComputeAlpha(double dt, double cutoff)
    {
        double tau = 1.0 / (2.0 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }

    private static (double minCutoff, double beta, double dCutoff) GetOneEuroParams(SmoothingStrength strength) =>
        strength switch
        {
            SmoothingStrength.Subtle => (3.0, 0.001, 1.0),
            SmoothingStrength.Medium => (1.5, 0.004, 1.0),
            SmoothingStrength.Smooth => (0.8, 0.007, 1.0),
            SmoothingStrength.UltraSmooth => (0.3, 0.01, 1.0),
            _ => (3.0, 0.001, 1.0),
        };

    #endregion

    #region Catmull-Rom Spline

    private List<SmoothedPosition> SmoothCatmullRom(
        MouseRecordingData rawData, int targetFps, int frameCount,
        long startTick, double tickFreq)
    {
        var samples = rawData.Samples;
        var result = new List<SmoothedPosition>(frameCount);
        double dt = 1.0 / targetFps;

        // Downsample raw data to key points based on strength
        int skipFactor = GetCatmullRomSkipFactor(Strength);
        var keyPoints = new List<(double time, double x, double y)>();
        for (int i = 0; i < samples.Count; i += skipFactor)
        {
            var s = samples[i];
            double t = (s.TimestampTicks - startTick) / tickFreq;
            keyPoints.Add((t, s.X, s.Y));
        }
        // Always include the last sample
        if (samples.Count > 1)
        {
            var last = samples[^1];
            double lastT = (last.TimestampTicks - startTick) / tickFreq;
            if (keyPoints[^1].time < lastT)
                keyPoints.Add((lastT, last.X, last.Y));
        }

        if (keyPoints.Count < 2)
        {
            // Not enough points for spline, return unsmoothed
            return GenerateUnsmoothed(rawData, targetFps, frameCount, startTick, tickFreq);
        }

        for (int i = 0; i < frameCount; i++)
        {
            double t = i * dt;

            var (px, py) = EvaluateCatmullRom(keyPoints, t);

            double vx = 0, vy = 0;
            if (i > 0)
            {
                var prev = result[i - 1];
                vx = (px - prev.X) / dt;
                vy = (py - prev.Y) / dt;
            }

            result.Add(new SmoothedPosition
            {
                X = px,
                Y = py,
                TimestampSeconds = t,
                VelocityX = vx,
                VelocityY = vy,
            });
        }

        return result;
    }

    private static (double x, double y) EvaluateCatmullRom(
        List<(double time, double x, double y)> points, double t)
    {
        // Find the segment containing t
        int segIdx = 0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            if (t >= points[i].time && t <= points[i + 1].time)
            {
                segIdx = i;
                break;
            }
            if (i == points.Count - 2)
                segIdx = i; // clamp to last segment
        }

        // Clamp t to data range
        if (t <= points[0].time) return (points[0].x, points[0].y);
        if (t >= points[^1].time) return (points[^1].x, points[^1].y);

        // Get four control points: p0, p1, p2, p3
        int i0 = Math.Max(0, segIdx - 1);
        int i1 = segIdx;
        int i2 = Math.Min(points.Count - 1, segIdx + 1);
        int i3 = Math.Min(points.Count - 1, segIdx + 2);

        var p0 = points[i0];
        var p1 = points[i1];
        var p2 = points[i2];
        var p3 = points[i3];

        // Normalize t within segment [p1.time, p2.time]
        double segDuration = p2.time - p1.time;
        double u = segDuration > 0 ? (t - p1.time) / segDuration : 0;

        // Standard Catmull-Rom with tension=0.5
        const double tension = 0.5;
        double x = CatmullRomInterp(p0.x, p1.x, p2.x, p3.x, u, tension);
        double y = CatmullRomInterp(p0.y, p1.y, p2.y, p3.y, u, tension);

        return (x, y);
    }

    private static double CatmullRomInterp(double v0, double v1, double v2, double v3, double u, double tension)
    {
        double u2 = u * u;
        double u3 = u2 * u;

        // Catmull-Rom matrix with tension parameter
        double a = -tension * v0 + (2 - tension) * v1 + (tension - 2) * v2 + tension * v3;
        double b = 2 * tension * v0 + (tension - 3) * v1 + (3 - 2 * tension) * v2 - tension * v3;
        double c = -tension * v0 + tension * v2;
        double d = v1;

        return a * u3 + b * u2 + c * u + d;
    }

    private static int GetCatmullRomSkipFactor(SmoothingStrength strength) =>
        strength switch
        {
            SmoothingStrength.Subtle => 3,
            SmoothingStrength.Medium => 6,
            SmoothingStrength.Smooth => 12,
            SmoothingStrength.UltraSmooth => 24,
            _ => 3,
        };

    #endregion

    #region Shared Helpers

    /// <summary>
    /// Snaps the smoothed cursor to the raw mouse position around click-down events.
    /// Smoothing algorithms introduce positional lag, which makes the cursor appear
    /// behind the actual click location. This blends the smoothed path toward the
    /// raw position over a short window before each click, arriving exactly at the
    /// click position on the click frame.
    /// </summary>
    private static void SnapToClickPositions(
        List<SmoothedPosition> positions,
        MouseRecordingData rawData,
        int targetFps,
        long startTick,
        double tickFreq)
    {
        if (positions.Count == 0 || rawData.Clicks.Count == 0)
            return;

        double dt = 1.0 / targetFps;
        var samples = rawData.Samples;

        // Number of frames to ease into the raw position before the click.
        // This avoids a jarring "teleport" by smoothly blending over ~100ms.
        int easeInFrames = Math.Max(1, (int)(0.1 * targetFps)); // ~3 frames at 30fps

        foreach (var click in rawData.Clicks)
        {
            if (!click.IsDown) continue;

            double clickTimeSeconds = (click.TimestampTicks - startTick) / tickFreq;
            int clickFrame = (int)Math.Round(clickTimeSeconds * targetFps);

            if (clickFrame < 0 || clickFrame >= positions.Count)
                continue;

            // Get the raw cursor position at click time
            var (rawX, rawY) = InterpolateRawPosition(samples, click.TimestampTicks);

            // Blend frames leading up to the click toward the raw position
            int blendStart = Math.Max(0, clickFrame - easeInFrames);
            for (int i = blendStart; i <= clickFrame && i < positions.Count; i++)
            {
                // t goes from 0 (start of blend) to 1 (click frame)
                float t = (float)(i - blendStart) / Math.Max(1, clickFrame - blendStart);
                // Ease-in curve for smooth acceleration toward target
                float blend = t * t;

                var pos = positions[i];
                positions[i] = new SmoothedPosition
                {
                    X = pos.X + (rawX - pos.X) * blend,
                    Y = pos.Y + (rawY - pos.Y) * blend,
                    TimestampSeconds = pos.TimestampSeconds,
                    VelocityX = pos.VelocityX,
                    VelocityY = pos.VelocityY,
                };
            }
        }
    }

    /// <summary>
    /// Linearly interpolates the raw sample position at a given tick timestamp.
    /// </summary>
    private static (double x, double y) InterpolateRawPosition(List<MouseSample> samples, long tick)
    {
        if (samples.Count == 1)
            return (samples[0].X, samples[0].Y);

        // Binary search for the surrounding samples
        int lo = 0, hi = samples.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].TimestampTicks <= tick)
                lo = mid;
            else
                hi = mid;
        }

        if (tick <= samples[lo].TimestampTicks)
            return (samples[lo].X, samples[lo].Y);
        if (tick >= samples[hi].TimestampTicks)
            return (samples[hi].X, samples[hi].Y);

        // Lerp between lo and hi
        double range = samples[hi].TimestampTicks - samples[lo].TimestampTicks;
        double frac = (tick - samples[lo].TimestampTicks) / range;
        double x = samples[lo].X + (samples[hi].X - samples[lo].X) * frac;
        double y = samples[lo].Y + (samples[hi].Y - samples[lo].Y) * frac;
        return (x, y);
    }

    #endregion

    #region De-stutter (stop-and-go removal)

    /// <summary>
    /// Detects trackpad "stop and go" micro-stalls and re-times each motion
    /// gesture so the cursor glides through them, while preserving intentional
    /// pauses. A stall is preserved (protected) when it sits near a click or is
    /// long enough to be a genuine rest. The pass is time-preserving: every
    /// sample keeps its original timestamp (so the cursor stays synced to the
    /// underlying video); only positions inside motion gestures are redistributed
    /// along the path with a smooth, near-constant speed profile.
    /// </summary>
    private MouseRecordingData ApplyDestutter(MouseRecordingData data)
    {
        var samples = data.Samples;
        int n = samples.Count;
        if (n < 4) return data;

        double tickFreq = data.TickFrequency;
        if (tickFreq <= 0) return data;

        var opt = Destutter;
        long start = data.StartTimestampTicks;

        // Time in seconds for each sample, relative to the recording start.
        var time = new double[n];
        for (int i = 0; i < n; i++)
            time[i] = (samples[i].TimestampTicks - start) / tickFreq;

        // Classify each sample as "slow" using a windowed speed estimate, which
        // is robust to single-sample jitter that briefly cancels real motion.
        var slow = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int lo = i, hi = i;
            while (lo > 0 && time[i] - time[lo - 1] < opt.SpeedWindowSeconds) lo--;
            while (hi < n - 1 && time[hi + 1] - time[i] < opt.SpeedWindowSeconds) hi++;

            double dt = time[hi] - time[lo];
            double dx = samples[hi].X - samples[lo].X;
            double dy = samples[hi].Y - samples[lo].Y;
            double speed = dt > 0 ? Math.Sqrt(dx * dx + dy * dy) / dt : 0;
            slow[i] = speed < opt.StallSpeedPxPerSec;
        }

        // Build protected (rest) intervals: low-speed runs that are either long
        // enough to be a genuine dwell, or overlap a click guard window.
        var isRest = new bool[n];
        int runStart = 0;
        bool inRun = false;
        for (int i = 0; i <= n; i++)
        {
            bool s = i < n && slow[i];
            if (s && !inRun) { runStart = i; inRun = true; }
            else if (!s && inRun)
            {
                int runEnd = i - 1;
                double runDuration = time[runEnd] - time[runStart];
                bool longEnough = runDuration >= opt.MinStallSeconds;
                if (longEnough &&
                    (runDuration >= opt.GenuineDwellSeconds ||
                     OverlapsClickGuard(data, time[runStart], time[runEnd], start, tickFreq, opt)))
                {
                    for (int k = runStart; k <= runEnd; k++) isRest[k] = true;
                }
                inRun = false;
            }
        }

        // Anchors: first/last sample, the boundaries of every protected rest run,
        // and every click sample. Anchors keep their exact position and time.
        var anchor = new bool[n];
        anchor[0] = true;
        anchor[n - 1] = true;
        for (int i = 0; i < n; i++)
        {
            if (samples[i].EventKind is MouseEventKind.ButtonDown or MouseEventKind.ButtonUp)
                anchor[i] = true;
            bool restHere = isRest[i];
            bool restPrev = i > 0 && isRest[i - 1];
            bool restNext = i < n - 1 && isRest[i + 1];
            if (restHere && (!restPrev || !restNext))
                anchor[i] = true; // edge of a rest run
        }

        var result = new List<MouseSample>(samples);

        // Walk consecutive anchor pairs; re-time the interior of motion gestures.
        int prevAnchor = 0;
        for (int b = 1; b < n; b++)
        {
            if (!anchor[b]) continue;
            int a = prevAnchor;
            prevAnchor = b;

            // Skip spans that are entirely a protected rest (leave the cursor still).
            bool allRest = true;
            for (int k = a; k <= b; k++)
            {
                if (!isRest[k]) { allRest = false; break; }
            }
            if (allRest || b - a < 2) continue;

            RetimeGesture(samples, result, time, a, b, opt.EaseStrength);
        }

        return new MouseRecordingData
        {
            Samples = result,
            Clicks = data.Clicks,
            StartTimestampTicks = data.StartTimestampTicks,
            EndTimestampTicks = data.EndTimestampTicks,
            TickFrequency = data.TickFrequency,
        };
    }

    private static bool OverlapsClickGuard(
        MouseRecordingData data, double runStartSec, double runEndSec,
        long start, double tickFreq, DestutterOptions opt)
    {
        foreach (var click in data.Clicks)
        {
            double clickSec = (click.TimestampTicks - start) / tickFreq;
            double guardLo = clickSec - opt.ClickPreGuardSeconds;
            double guardHi = clickSec + opt.ClickPostGuardSeconds;
            if (runEndSec >= guardLo && runStartSec <= guardHi)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Re-distributes the interior sample positions of a single motion gesture
    /// (between anchors <paramref name="a"/> and <paramref name="b"/>) along the
    /// gesture's path using a smooth speed profile. Endpoints are left untouched.
    /// </summary>
    private static void RetimeGesture(
        List<MouseSample> src, List<MouseSample> dst, double[] time,
        int a, int b, double easeStrength)
    {
        // Cumulative arc length of the raw polyline a..b.
        int m = b - a + 1;
        var cum = new double[m];
        cum[0] = 0;
        for (int i = 1; i < m; i++)
        {
            double dx = src[a + i].X - src[a + i - 1].X;
            double dy = src[a + i].Y - src[a + i - 1].Y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[m - 1];
        if (total <= 0) return; // degenerate, nothing to redistribute

        double t0 = time[a], t1 = time[b];
        double span = t1 - t0;
        if (span <= 0) return;

        for (int i = 1; i < m - 1; i++)
        {
            int idx = a + i;
            double u = (time[idx] - t0) / span;
            double s = total * RemapEase(u, easeStrength);
            var (x, y) = PositionAtArcLength(src, cum, a, m, s);

            var sample = dst[idx];
            sample.X = (int)Math.Round(x);
            sample.Y = (int)Math.Round(y);
            dst[idx] = sample;
        }
    }

    // Blend between linear arc traversal (constant speed) and smootherstep ease.
    private static double RemapEase(double u, double easeStrength)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        double smoother = u * u * u * (u * (u * 6 - 15) + 10); // 6u^5 - 15u^4 + 10u^3
        return (1 - easeStrength) * u + easeStrength * smoother;
    }

    private static (double x, double y) PositionAtArcLength(
        List<MouseSample> src, double[] cum, int a, int m, double s)
    {
        if (s <= 0) return (src[a].X, src[a].Y);
        if (s >= cum[m - 1]) return (src[a + m - 1].X, src[a + m - 1].Y);

        int lo = 0, hi = m - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (cum[mid] <= s) lo = mid; else hi = mid;
        }

        double segLen = cum[hi] - cum[lo];
        double f = segLen > 0 ? (s - cum[lo]) / segLen : 0;
        double x = src[a + lo].X + (src[a + hi].X - src[a + lo].X) * f;
        double y = src[a + lo].Y + (src[a + hi].Y - src[a + lo].Y) * f;
        return (x, y);
    }

    #endregion
}
