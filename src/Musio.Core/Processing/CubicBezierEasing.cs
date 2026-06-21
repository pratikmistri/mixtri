namespace Musio.Core.Processing;

/// <summary>
/// Evaluates cubic Bézier easing curves for animation timing.
/// Control points are (0,0), (x1,y1), (x2,y2), (1,1).
/// </summary>
public static class CubicBezierEasing
{
    private const int NewtonIterations = 8;
    private const float NewtonMinSlope = 1e-6f;
    private const int BisectionIterations = 20;
    private const float BisectionTolerance = 1e-4f;

    /// <summary>
    /// Evaluate a cubic Bézier easing curve at parameter <paramref name="t"/> (0–1).
    /// </summary>
    public static float Evaluate(float t, float x1, float y1, float x2, float y2)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        // Linear shortcut
        if (x1 == y1 && x2 == y2) return t;

        // Newton-Raphson: find parameter u where BezierX(u) = t
        float u = t;
        for (int i = 0; i < NewtonIterations; i++)
        {
            float slope = SampleDerivative(u, x1, x2);
            if (MathF.Abs(slope) < NewtonMinSlope) break;
            u -= (SampleCurve(u, x1, x2) - t) / slope;
        }

        u = Math.Clamp(u, 0f, 1f);

        // Bisection fallback when Newton didn't converge
        if (MathF.Abs(SampleCurve(u, x1, x2) - t) > BisectionTolerance)
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < BisectionIterations; i++)
            {
                u = (lo + hi) * 0.5f;
                if (SampleCurve(u, x1, x2) < t) lo = u;
                else hi = u;
            }
        }

        return SampleCurve(u, y1, y2);
    }

    /// <summary>Bézier polynomial: B(u) = 3·c1·(1-u)²·u + 3·c2·(1-u)·u² + u³</summary>
    private static float SampleCurve(float u, float c1, float c2)
    {
        float oneMinusU = 1f - u;
        return 3f * c1 * oneMinusU * oneMinusU * u
             + 3f * c2 * oneMinusU * u * u
             + u * u * u;
    }

    /// <summary>First derivative of the Bézier polynomial with respect to u.</summary>
    private static float SampleDerivative(float u, float c1, float c2)
    {
        float oneMinusU = 1f - u;
        return 3f * c1 * oneMinusU * oneMinusU
             + 6f * (c2 - c1) * oneMinusU * u
             + 3f * (1f - c2) * u * u;
    }

    /// <summary>Ease-out: cubic-bezier(0, 0, 0.2, 1)</summary>
    public static float EaseOut(float t) => Evaluate(t, 0f, 0f, 0.2f, 1f);

    /// <summary>Ease-in: cubic-bezier(0.4, 0, 1, 1)</summary>
    public static float EaseIn(float t) => Evaluate(t, 0.4f, 0f, 1f, 1f);

    /// <summary>Ease-in-out: cubic-bezier(0.4, 0, 0.2, 1)</summary>
    public static float EaseInOut(float t) => Evaluate(t, 0.4f, 0f, 0.2f, 1f);

    /// <summary>Spring-out: cubic-bezier(0, 0, 0.2, 1)</summary>
    public static float SpringOut(float t) => Evaluate(t, 0f, 0f, 0.2f, 1f);

    /// <summary>
    /// Cinematic ease-in-out: cubic-bezier(0.76, 0, 0.24, 1).
    /// A symmetric "quart" curve that starts and settles from rest with near-zero
    /// velocity and a swift, gliding middle. Both ends have zero slope, so camera
    /// moves enter and resolve without any visible jolt — the deliberate, elegant
    /// feel of a hand-animated zoom.
    /// </summary>
    public static float EaseInOutCinematic(float t) => Evaluate(t, 0.76f, 0f, 0.24f, 1f);
}
