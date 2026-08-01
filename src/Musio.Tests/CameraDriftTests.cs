namespace Musio.Tests;

using Musio.Core.Processing;

/// <summary>
/// Covers <see cref="CameraDrift"/>, the continuous "living camera" motion layered on
/// top of zoom segments.
/// <para>
/// Two families of invariant matter here. The first keeps the effect from corrupting
/// the framing: drift must vanish at 1x, must never permanently magnify the scene,
/// must stay inside the available slack, and must be perfectly deterministic so
/// preview and export render identical frames. The second is the whole point of the
/// feature — the camera must <b>never stall</b> mid-segment. An earlier version drove
/// drift from absolute time with a ~12s period; since a zoom segment only lasts about
/// 4s, a segment could land on a stationary point of that oscillator and the camera
/// visibly parked. Drift is now driven by segment progress, and the tests below pin
/// that down.
/// </para>
/// </summary>
[TestClass]
public sealed class CameraDriftTests
{
    private static CameraDriftSettings Settings() => new();

    private static CameraDriftResult EvaluateAt(
        float progress, float zoom = 2.0f, CameraDriftSettings? settings = null,
        float slackX = 100000f, float slackY = 100000f, int seed = 0,
        float viewportWidth = 960f, float viewportHeight = 540f)
        => CameraDrift.Evaluate(
            settings ?? Settings(), progress, zoom,
            viewportWidth, viewportHeight,
            slackX: slackX, slackY: slackY,
            headingX: CameraDrift.HeadingFromSeed(seed).X,
            headingY: CameraDrift.HeadingFromSeed(seed).Y);

    private static float PanMagnitude(CameraDriftResult r)
        => MathF.Sqrt(r.OffsetX * r.OffsetX + r.OffsetY * r.OffsetY);

    #region The camera must never stall

    [TestMethod]
    public void Evaluate_PanKeepsMovingAcrossTheWholeSegment()
    {
        // The regression that motivated segment-relative drift: the camera parked
        // because an absolute-time sine sat on a stationary point for the hold.
        // With a square viewport both pan axes share an amplitude, so the offset
        // magnitude traces a circular arc and must strictly grow with progress.
        float previous = -1f;
        for (int i = 0; i <= 40; i++)
        {
            float p = i / 40f;
            float magnitude = PanMagnitude(
                EvaluateAt(p, viewportWidth: 800f, viewportHeight: 800f));

            Assert.IsTrue(magnitude > previous,
                $"Pan stalled or reversed at progress {p} ({magnitude} <= {previous}).");
            previous = magnitude;
        }
    }

    [TestMethod]
    public void Evaluate_PushInIsMonotonicAcrossTheSegment()
    {
        // The scale must always be moving in one direction through the segment
        // rather than oscillating back to where it started.
        float previous = -1f;
        for (int i = 0; i <= 40; i++)
        {
            float zoomFactor = EvaluateAt(i / 40f).ZoomFactor;
            Assert.IsTrue(zoomFactor >= previous,
                $"Push-in reversed at progress {i / 40f}.");
            previous = zoomFactor;
        }
    }

    [TestMethod]
    public void Evaluate_MotionIsNeverStationaryThroughTheBodyOfTheSegment()
    {
        // Sample the interior (the ends legitimately ease to rest) and require every
        // step to produce visible movement in either layer.
        const float viewport = 800f;
        var previous = EvaluateAt(0.10f, viewportWidth: viewport, viewportHeight: viewport);

        for (int i = 1; i <= 32; i++)
        {
            float p = 0.10f + (0.80f * i / 32f);
            var current = EvaluateAt(p, viewportWidth: viewport, viewportHeight: viewport);

            float dx = current.OffsetX - previous.OffsetX;
            float dy = current.OffsetY - previous.OffsetY;
            float panDelta = MathF.Sqrt(dx * dx + dy * dy);
            float zoomDelta = Math.Abs(current.ZoomFactor - previous.ZoomFactor);

            Assert.IsTrue(panDelta > 1e-3f || zoomDelta > 1e-5f,
                $"Camera was stationary between steps at progress {p}.");
            previous = current;
        }
    }

    [TestMethod]
    public void Evaluate_PanDoesNotReturnToItsStartingPoint()
    {
        // A full oscillation would end where it began, reading as a wobble rather
        // than a drift. The default sweep is well under a full turn.
        var atEnd = EvaluateAt(1.0f, viewportWidth: 800f, viewportHeight: 800f);

        Assert.IsTrue(PanMagnitude(atEnd) > 1f,
            "Pan returned to its origin by the end of the segment.");
    }

    #endregion

    #region Window

    [TestMethod]
    public void Window_AtOneX_IsExactlyZero()
    {
        // The whole "returns to original framing" guarantee rests on this.
        Assert.AreEqual(0f, CameraDrift.Window(1.0f));
    }

    [TestMethod]
    public void Window_BelowOneX_IsZero()
    {
        Assert.AreEqual(0f, CameraDrift.Window(0.5f));
        Assert.AreEqual(0f, CameraDrift.Window(0f));
    }

    [TestMethod]
    public void Window_IncreasesMonotonicallyWithZoomDepth()
    {
        float previous = CameraDrift.Window(1.0f);
        for (float zoom = 1.01f; zoom <= 4f; zoom += 0.05f)
        {
            float current = CameraDrift.Window(zoom);
            Assert.IsTrue(current >= previous,
                $"Window should not decrease as zoom grows (zoom={zoom}).");
            previous = current;
        }
    }

    [TestMethod]
    public void Window_StaysWithinUnitRange()
    {
        for (float zoom = 1f; zoom <= 10f; zoom += 0.25f)
        {
            float w = CameraDrift.Window(zoom);
            Assert.IsTrue(w >= 0f && w <= 1f, $"Window out of range at zoom={zoom}: {w}");
        }
    }

    [TestMethod]
    public void Window_AtTypicalZoom_IsNearlyFullAmplitude()
    {
        // A 2x zoom is the product default; drift should be essentially at full
        // strength there rather than being scaled down to nothing.
        Assert.IsTrue(CameraDrift.Window(2.0f) > 0.95f);
    }

    #endregion

    #region Disabled / degenerate input

    [TestMethod]
    public void Evaluate_WhenDisabled_ReturnsNone()
    {
        var result = EvaluateAt(0.5f, settings: new CameraDriftSettings { Enabled = false });

        Assert.AreEqual(CameraDriftResult.None, result);
        Assert.IsFalse(result.IsActive);
    }

    [TestMethod]
    public void Evaluate_AtOneX_ReturnsNone()
    {
        // Drift is scoped to zoom segments; an un-zoomed frame must be bit-stable.
        Assert.AreEqual(CameraDriftResult.None, EvaluateAt(0.5f, zoom: 1.0f));
    }

    [TestMethod]
    public void Evaluate_WithZeroStrength_ReturnsNone()
    {
        Assert.AreEqual(CameraDriftResult.None,
            EvaluateAt(0.5f, settings: new CameraDriftSettings { Strength = 0f }));
    }

    [TestMethod]
    public void Evaluate_WithNonFiniteProgress_ReturnsNone()
    {
        Assert.AreEqual(CameraDriftResult.None, EvaluateAt(float.NaN));
        Assert.AreEqual(CameraDriftResult.None, EvaluateAt(float.PositiveInfinity));
    }

    [TestMethod]
    public void Evaluate_ClampsProgressOutsideUnitRange()
    {
        Assert.AreEqual(EvaluateAt(0f), EvaluateAt(-2f));
        Assert.AreEqual(EvaluateAt(1f), EvaluateAt(3f));
    }

    [TestMethod]
    public void Evaluate_WithNullSettings_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => CameraDrift.Evaluate(null!, 0.5f, 2f, 960f, 540f, 100f, 100f));
    }

    #endregion

    #region ApplyZoom

    [TestMethod]
    public void ApplyZoom_AtOneX_IsAlwaysExactlyOneX()
    {
        // Guards the classic bug: multiplying the zoom directly would leave the scene
        // permanently magnified after the segment released back to 1x.
        Assert.AreEqual(1f, CameraDrift.ApplyZoom(1f, new CameraDriftResult(1.5f, 0f, 0f)));
    }

    [TestMethod]
    public void ApplyZoom_ScalesTheZoomDepthNotTheZoomLevel()
    {
        // depth = 1.0, so 1 + 1.0 * 1.05 = 2.05 (NOT 2.0 * 1.05 = 2.1)
        Assert.AreEqual(2.05f, CameraDrift.ApplyZoom(2.0f, new CameraDriftResult(1.05f, 0f, 0f)), 1e-5f);
    }

    [TestMethod]
    public void ApplyZoom_WithNoDrift_IsIdentity()
    {
        Assert.AreEqual(2.5f, CameraDrift.ApplyZoom(2.5f, CameraDriftResult.None), 1e-6f);
    }

    #endregion

    #region Amplitude bounds

    [TestMethod]
    public void Evaluate_ZoomFactor_OnlyEverPushesIn()
    {
        for (int i = 0; i <= 20; i++)
            Assert.IsTrue(EvaluateAt(i / 20f).ZoomFactor >= 1f,
                "Breathing must not pull below the segment's own zoom.");
    }

    [TestMethod]
    public void Evaluate_ZoomFactor_StaysWithinConfiguredAmplitude()
    {
        var settings = Settings();
        float ceiling = 1f + settings.ZoomAmplitude * settings.Strength;

        for (int i = 0; i <= 40; i++)
        {
            float zoomFactor = EvaluateAt(i / 40f, settings: settings).ZoomFactor;
            Assert.IsTrue(zoomFactor <= ceiling + 1e-5f,
                $"ZoomFactor {zoomFactor} exceeded ceiling {ceiling}.");
        }
    }

    [TestMethod]
    public void Evaluate_PanOffsets_RespectTheViewportAmplitude()
    {
        var settings = Settings();
        const float viewportWidth = 960f;
        const float viewportHeight = 540f;

        // The arc is normalized to unit peak magnitude, so each axis is bounded by the
        // configured amplitude directly.
        float maxX = settings.PanAmplitude * settings.Strength * viewportWidth;
        float maxY = settings.PanAmplitude * settings.Strength * viewportHeight;

        for (int i = 0; i <= 40; i++)
        {
            var result = EvaluateAt(i / 40f);
            Assert.IsTrue(Math.Abs(result.OffsetX) <= maxX + 1e-3f,
                $"OffsetX {result.OffsetX} exceeded {maxX}.");
            Assert.IsTrue(Math.Abs(result.OffsetY) <= maxY + 1e-3f,
                $"OffsetY {result.OffsetY} exceeded {maxY}.");
        }
    }

    [TestMethod]
    public void Evaluate_PanOffsets_AreBoundedByAvailableSlack()
    {
        var settings = Settings();
        const float slack = 4f; // far tighter than the viewport-derived amplitude
        float limit = slack * settings.MaxSlackFraction;

        for (int i = 0; i <= 40; i++)
        {
            var result = EvaluateAt(i / 40f, slackX: slack, slackY: slack);
            Assert.IsTrue(Math.Abs(result.OffsetX) <= limit + 1e-4f,
                $"OffsetX {result.OffsetX} exceeded slack limit {limit}.");
            Assert.IsTrue(Math.Abs(result.OffsetY) <= limit + 1e-4f,
                $"OffsetY {result.OffsetY} exceeded slack limit {limit}.");
        }
    }

    [TestMethod]
    public void Evaluate_PanNeverExceedsTheAvailableSlack()
    {
        // The invariant the slack bound exists for. It was previously violated because
        // the arc (sin, 1-cos) has magnitude up to 2, so scaling an axis amplitude by
        // it overshot the bound and the viewport clamp could still engage — stalling
        // the very motion this feature is meant to guarantee.
        var settings = Settings();

        foreach (float slack in new[] { 1f, 4f, 25f, 200f })
        {
            float limit = slack * settings.MaxSlackFraction;
            for (int i = 0; i <= 40; i++)
            {
                var result = EvaluateAt(i / 40f, slackX: slack, slackY: slack);
                Assert.IsTrue(Math.Abs(result.OffsetX) <= limit + 1e-4f,
                    $"OffsetX {result.OffsetX} exceeded slack limit {limit} (slack={slack}).");
                Assert.IsTrue(Math.Abs(result.OffsetY) <= limit + 1e-4f,
                    $"OffsetY {result.OffsetY} exceeded slack limit {limit} (slack={slack}).");
            }
        }
    }

    [TestMethod]
    public void Evaluate_PanNeverExceedsTheAvailableSlack_ForAnyHeading()
    {
        // Rotating the arc must not let a single axis exceed the bound either.
        var settings = Settings();
        const float slack = 10f;
        float limit = slack * settings.MaxSlackFraction;

        for (int seed = 0; seed < 24; seed++)
        {
            for (int i = 0; i <= 20; i++)
            {
                var result = EvaluateAt(i / 20f, slackX: slack, slackY: slack, seed: seed);
                Assert.IsTrue(Math.Abs(result.OffsetX) <= limit + 1e-4f
                    && Math.Abs(result.OffsetY) <= limit + 1e-4f,
                    $"Heading seed {seed} broke the slack bound at progress {i / 20f}.");
            }
        }
    }

    [TestMethod]
    public void Evaluate_WithNoSlack_ProducesNoPan()
    {
        // Against a source edge there is nowhere to pan. Panning anyway would be
        // silently clamped by the viewport, which stalls the motion mid-swing.
        var result = EvaluateAt(0.5f, slackX: 0f, slackY: 0f);

        Assert.AreEqual(0f, result.OffsetX);
        Assert.AreEqual(0f, result.OffsetY);
    }

    [TestMethod]
    public void Evaluate_WithNegativeSlack_ProducesNoPan()
    {
        var result = EvaluateAt(0.5f, slackX: -10f, slackY: -10f);

        Assert.AreEqual(0f, result.OffsetX);
        Assert.AreEqual(0f, result.OffsetY);
    }

    [TestMethod]
    public void Evaluate_PanPath_IsNotAStraightLine()
    {
        // Both axes on one phase would trace a diagonal. The arc puts them 90 degrees
        // apart, so the ratio between them must keep changing.
        var ratios = new List<double>();
        for (int i = 1; i <= 20; i++)
        {
            var result = EvaluateAt(i / 20f);
            if (Math.Abs(result.OffsetX) > 1e-4f)
                ratios.Add(result.OffsetY / result.OffsetX);
        }

        Assert.IsTrue(ratios.Count > 5, "Not enough samples to judge the path shape.");
        double spread = ratios.Max() - ratios.Min();
        Assert.IsTrue(spread > 0.1, $"Pan path looks linear; ratio spread was only {spread}.");
    }

    #endregion

    #region Determinism and fade-out

    [TestMethod]
    public void Evaluate_IsDeterministic()
    {
        // Preview and export must agree frame for frame; any hidden state would show
        // up here as a mismatch.
        Assert.AreEqual(EvaluateAt(0.42f), EvaluateAt(0.42f));
    }

    [TestMethod]
    public void Evaluate_DifferentPhaseSeeds_ProduceDifferentHeadings()
    {
        Assert.AreNotEqual(EvaluateAt(0.5f, seed: 0), EvaluateAt(0.5f, seed: 5));
    }

    [TestMethod]
    public void Evaluate_EqualPhaseSeeds_ProduceIdenticalMotion()
    {
        Assert.AreEqual(EvaluateAt(0.5f, seed: 3), EvaluateAt(0.5f, seed: 3));
    }

    [TestMethod]
    public void Evaluate_DriftFadesToNothingAsZoomReleases()
    {
        // As a segment eases back to 1x the drift amplitude must collapse, so the
        // segment lands on exactly its original framing.
        float previousMagnitude = float.MaxValue;

        foreach (float zoom in new[] { 2.0f, 1.5f, 1.2f, 1.1f, 1.05f, 1.01f })
        {
            var result = EvaluateAt(0.5f, zoom: zoom);
            float magnitude = PanMagnitude(result) + Math.Abs(result.ZoomFactor - 1f) * 1000f;

            Assert.IsTrue(magnitude <= previousMagnitude + 1e-3f,
                $"Drift grew while zooming out (zoom={zoom}).");
            previousMagnitude = magnitude;
        }

        Assert.AreEqual(CameraDriftResult.None, EvaluateAt(0.5f, zoom: 1.0f));
    }

    #endregion

    #region CameraDriftResult

    [TestMethod]
    public void None_IsInactiveAndNeutral()
    {
        Assert.IsFalse(CameraDriftResult.None.IsActive);
        Assert.AreEqual(1f, CameraDriftResult.None.ZoomFactor);
        Assert.AreEqual(0f, CameraDriftResult.None.OffsetX);
        Assert.AreEqual(0f, CameraDriftResult.None.OffsetY);
    }

    [TestMethod]
    public void IsActive_DetectsEitherLayer()
    {
        Assert.IsTrue(new CameraDriftResult(1.01f, 0f, 0f).IsActive);
        Assert.IsTrue(new CameraDriftResult(1f, 2f, 0f).IsActive);
        Assert.IsTrue(new CameraDriftResult(1f, 0f, 2f).IsActive);
    }

    #endregion
}
