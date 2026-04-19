using Musio.Core.Models;

namespace Musio.Core.Processing;

public enum SmoothingAlgorithm { None, SpringPhysics, OneEuroFilter, CatmullRomSpline }
public enum SmoothingStrength { None, Subtle, Medium, Smooth, UltraSmooth }

public struct SmoothedPosition
{
    public double X;
    public double Y;
    public double TimestampSeconds;
    public double VelocityX;
    public double VelocityY;
}

public class CursorSmoother
{
    public SmoothingAlgorithm Algorithm { get; set; }
    public SmoothingStrength Strength { get; set; }

    public List<SmoothedPosition> SmoothPath(MouseRecordingData rawData, int targetFps)
    {
        ArgumentNullException.ThrowIfNull(rawData);
        if (targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (rawData.Samples.Count == 0) return [];

        double tickFreq = rawData.TickFrequency;
        long startTick = rawData.StartTimestampTicks;
        long endTick = rawData.EndTimestampTicks;
        double durationSeconds = (endTick - startTick) / tickFreq;
        if (durationSeconds <= 0) durationSeconds = 0;

        int frameCount = Math.Max(1, (int)Math.Round(durationSeconds * targetFps));

        List<SmoothedPosition> result;

        if (Algorithm == SmoothingAlgorithm.None || Strength == SmoothingStrength.None)
            result = GenerateUnsmoothed(rawData, targetFps, frameCount, startTick, tickFreq);
        else
            result = Algorithm switch
            {
                SmoothingAlgorithm.SpringPhysics => SmoothSpringPhysics(rawData, targetFps, frameCount, startTick, tickFreq),
                SmoothingAlgorithm.OneEuroFilter => SmoothOneEuroFilter(rawData, targetFps, frameCount, startTick, tickFreq),
                SmoothingAlgorithm.CatmullRomSpline => SmoothCatmullRom(rawData, targetFps, frameCount, startTick, tickFreq),
                _ => GenerateUnsmoothed(rawData, targetFps, frameCount, startTick, tickFreq),
            };

        // Snap cursor to raw position on click events so the smoothed cursor
        // is at the correct location when click animations play.
        if (Algorithm != SmoothingAlgorithm.None && Strength != SmoothingStrength.None)
            SnapToClickPositions(result, rawData, targetFps, startTick, tickFreq);

        return result;
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
            SmoothingStrength.Subtle => (400, 28, 1.0),
            SmoothingStrength.Medium => (250, 22, 1.0),
            SmoothingStrength.Smooth => (150, 16, 1.0),
            SmoothingStrength.UltraSmooth => (80, 12, 1.0),
            _ => (400, 28, 1.0),
        };

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
}
