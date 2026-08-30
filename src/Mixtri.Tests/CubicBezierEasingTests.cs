using Mixtri.Core.Processing;

namespace Mixtri.Tests;

[TestClass]
public sealed class CubicBezierEasingTests
{
    private const float Epsilon = 1e-4f;

    #region Evaluate Boundaries

    [TestMethod]
    public void Evaluate_AtZero_ReturnsZero()
    {
        Assert.AreEqual(0f, CubicBezierEasing.Evaluate(0f, 0.25f, 0.1f, 0.25f, 1f));
    }

    [TestMethod]
    public void Evaluate_AtOne_ReturnsOne()
    {
        Assert.AreEqual(1f, CubicBezierEasing.Evaluate(1f, 0.25f, 0.1f, 0.25f, 1f));
    }

    [TestMethod]
    public void Evaluate_BelowZero_ClampedToZero()
    {
        Assert.AreEqual(0f, CubicBezierEasing.Evaluate(-0.5f, 0.4f, 0f, 0.2f, 1f));
    }

    [TestMethod]
    public void Evaluate_AboveOne_ClampedToOne()
    {
        Assert.AreEqual(1f, CubicBezierEasing.Evaluate(1.5f, 0.4f, 0f, 0.2f, 1f));
    }

    #endregion

    #region Linear

    [TestMethod]
    public void Evaluate_Linear_ReturnsInput()
    {
        // When control points are equal (x1==y1, x2==y2), should return t
        for (float t = 0f; t <= 1f; t += 0.1f)
        {
            float result = CubicBezierEasing.Evaluate(t, 0.5f, 0.5f, 0.5f, 0.5f);
            Assert.AreEqual(t, result, Epsilon, $"Linear should return input at t={t}");
        }
    }

    #endregion

    #region EaseOut

    [TestMethod]
    public void EaseOut_StartsAtZero_EndsAtOne()
    {
        Assert.AreEqual(0f, CubicBezierEasing.EaseOut(0f));
        Assert.AreEqual(1f, CubicBezierEasing.EaseOut(1f));
    }

    [TestMethod]
    public void EaseOut_MonotonicallyIncreasing()
    {
        float prev = 0f;
        for (float t = 0.01f; t <= 1f; t += 0.01f)
        {
            float val = CubicBezierEasing.EaseOut(t);
            Assert.IsTrue(val >= prev - Epsilon,
                $"EaseOut should be monotonic: f({t})={val} < f({t - 0.01f})={prev}");
            prev = val;
        }
    }

    [TestMethod]
    public void EaseOut_Midpoint_BetweenZeroAndOne()
    {
        float mid = CubicBezierEasing.EaseOut(0.5f);
        Assert.IsTrue(mid > 0f && mid < 1f, $"Midpoint should be between 0 and 1, got {mid}");
    }

    #endregion

    #region EaseInOut

    [TestMethod]
    public void EaseInOut_StartsAtZero_EndsAtOne()
    {
        Assert.AreEqual(0f, CubicBezierEasing.EaseInOut(0f));
        Assert.AreEqual(1f, CubicBezierEasing.EaseInOut(1f));
    }

    [TestMethod]
    public void EaseInOut_MonotonicallyIncreasing()
    {
        float prev = 0f;
        for (float t = 0.01f; t <= 1f; t += 0.01f)
        {
            float val = CubicBezierEasing.EaseInOut(t);
            Assert.IsTrue(val >= prev - Epsilon,
                $"EaseInOut should be monotonic at t={t}");
            prev = val;
        }
    }

    #endregion

    #region SpringOut

    [TestMethod]
    public void SpringOut_StartsAtZero_EndsAtOne()
    {
        Assert.AreEqual(0f, CubicBezierEasing.SpringOut(0f));
        Assert.AreEqual(1f, CubicBezierEasing.SpringOut(1f));
    }

    [TestMethod]
    public void SpringOut_MonotonicallyIncreasing()
    {
        float prev = 0f;
        for (float t = 0.01f; t <= 1f; t += 0.01f)
        {
            float val = CubicBezierEasing.SpringOut(t);
            Assert.IsTrue(val >= prev - Epsilon,
                $"SpringOut should be monotonic at t={t}");
            prev = val;
        }
    }

    #endregion

    #region Various Control Points

    [TestMethod]
    public void Evaluate_EaseInCurve_SlowStart()
    {
        // ease-in: cubic-bezier(0.42, 0, 1, 1)
        float earlyValue = CubicBezierEasing.Evaluate(0.25f, 0.42f, 0f, 1f, 1f);
        Assert.IsTrue(earlyValue < 0.25f, $"Ease-in at t=0.25 should be < 0.25 (slow start), got {earlyValue}");
    }

    [TestMethod]
    public void Evaluate_EaseOutCurve_FastStart()
    {
        // ease-out: cubic-bezier(0, 0, 0.58, 1)
        float earlyValue = CubicBezierEasing.Evaluate(0.25f, 0f, 0f, 0.58f, 1f);
        Assert.IsTrue(earlyValue > 0.25f, $"Ease-out at t=0.25 should be > 0.25 (fast start), got {earlyValue}");
    }

    #endregion
}
