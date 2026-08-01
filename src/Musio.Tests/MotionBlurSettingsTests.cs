namespace Musio.Tests;

using Musio.Core.Processing;

/// <summary>
/// Covers <see cref="MotionBlurSettings"/>, which encodes the photographic shutter model
/// used by both the screen and cursor blur paths. The adaptive sample count is the part
/// that keeps the feature affordable: a still camera must resolve to a single sample so
/// the common case costs nothing beyond today's single draw call.
/// </summary>
[TestClass]
public sealed class MotionBlurSettingsTests
{
    #region ShutterFraction

    [TestMethod]
    public void ShutterFraction_WithCinemaDefaults_IsHalfAFrameScaledByStrength()
    {
        // 180 degrees = shutter open half the frame; the 0.5 default strength halves it again.
        var settings = new MotionBlurSettings();

        Assert.AreEqual(0.25f, settings.ShutterFraction, 1e-6f);
    }

    [TestMethod]
    public void ShutterFraction_AtFullStrengthAnd180Degrees_IsHalfAFrame()
    {
        var settings = new MotionBlurSettings { Strength = 1f };

        Assert.AreEqual(0.5f, settings.ShutterFraction, 1e-6f);
    }

    [TestMethod]
    public void ShutterFraction_At360Degrees_IsAWholeFrame()
    {
        var settings = new MotionBlurSettings { Strength = 1f, ShutterAngleDegrees = 360f };

        Assert.AreEqual(1f, settings.ShutterFraction, 1e-6f);
    }

    [TestMethod]
    public void ShutterFraction_WithZeroStrength_IsZero()
    {
        Assert.AreEqual(0f, new MotionBlurSettings { Strength = 0f }.ShutterFraction);
    }

    [TestMethod]
    public void ShutterFraction_ClampsOutOfRangeInput()
    {
        Assert.AreEqual(1f,
            new MotionBlurSettings { Strength = 5f, ShutterAngleDegrees = 900f }.ShutterFraction, 1e-6f);
        Assert.AreEqual(0f,
            new MotionBlurSettings { Strength = -3f }.ShutterFraction);
    }

    #endregion

    #region ResolveSampleCount

    [TestMethod]
    public void ResolveSampleCount_WhenDisabled_IsOne()
    {
        var settings = new MotionBlurSettings { Enabled = false };

        Assert.AreEqual(1, settings.ResolveSampleCount(500f));
    }

    [TestMethod]
    public void ResolveSampleCount_WithZeroStrength_IsOne()
    {
        var settings = new MotionBlurSettings { Strength = 0f };

        Assert.AreEqual(1, settings.ResolveSampleCount(500f));
    }

    [TestMethod]
    public void ResolveSampleCount_BelowMinimumTravel_IsOne()
    {
        // The still-camera case: hold phase, idle playhead, or a gentle drift. This must
        // cost nothing extra, so it has to collapse to a single unblurred draw.
        var settings = new MotionBlurSettings();

        Assert.AreEqual(1, settings.ResolveSampleCount(0f));
        Assert.AreEqual(1, settings.ResolveSampleCount(settings.MinBlurPixels - 0.01f));
    }

    [TestMethod]
    public void ResolveSampleCount_WithNonFiniteTravel_IsOne()
    {
        var settings = new MotionBlurSettings();

        Assert.AreEqual(1, settings.ResolveSampleCount(float.NaN));
        Assert.AreEqual(1, settings.ResolveSampleCount(float.PositiveInfinity));
    }

    [TestMethod]
    public void ResolveSampleCount_AtMinimumTravel_BlursWithAtLeastTwoSamples()
    {
        var settings = new MotionBlurSettings();

        Assert.IsTrue(settings.ResolveSampleCount(settings.MinBlurPixels) >= 2);
    }

    [TestMethod]
    public void ResolveSampleCount_NeverExceedsMaxSamples()
    {
        var settings = new MotionBlurSettings { MaxSamples = 8 };

        Assert.AreEqual(8, settings.ResolveSampleCount(10_000f));
        Assert.AreEqual(8, settings.ResolveSampleCount(400f));
    }

    [TestMethod]
    public void ResolveSampleCount_GrowsWithTravel()
    {
        var settings = new MotionBlurSettings { MaxSamples = 64 };
        int previous = 0;

        for (float travel = 2f; travel <= 60f; travel += 2f)
        {
            int samples = settings.ResolveSampleCount(travel);
            Assert.IsTrue(samples >= previous,
                $"Sample count went backwards at travel={travel}.");
            previous = samples;
        }

        Assert.IsTrue(previous > 2, "Sample count never grew for long travel.");
    }

    [TestMethod]
    public void ResolveSampleCount_KeepsSamplesCloserThanTheTargetSpacing()
    {
        // Samples spaced further apart than this band into discrete ghosts instead of
        // reading as a continuous smear.
        var settings = new MotionBlurSettings { MaxSamples = 64, SampleSpacingPixels = 1.5f };

        foreach (float travel in new[] { 3f, 7f, 15f, 40f, 90f })
        {
            int samples = settings.ResolveSampleCount(travel);
            float spacing = travel / (samples - 1);
            Assert.IsTrue(spacing <= settings.SampleSpacingPixels + 1e-3f,
                $"Spacing {spacing}px exceeded target at travel={travel} with {samples} samples.");
        }
    }

    [TestMethod]
    public void ResolveSampleCount_WithDegenerateSpacing_StaysBounded()
    {
        var settings = new MotionBlurSettings { SampleSpacingPixels = 0f, MaxSamples = 8 };

        int samples = settings.ResolveSampleCount(100f);

        Assert.IsTrue(samples is >= 2 and <= 8);
    }

    #endregion

    #region Defaults

    [TestMethod]
    public void Defaults_AreCinemaSaneAndEnabled()
    {
        var settings = new MotionBlurSettings();

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(180f, settings.ShutterAngleDegrees);
        Assert.AreEqual(1f, settings.CursorStrength);
        Assert.AreEqual(1f, settings.ZoomStrength);
        Assert.AreEqual(1f, settings.PanStrength);
        Assert.IsTrue(settings.MaxSamples is >= 2 and <= 32);
    }

    [TestMethod]
    public void Defaults_ForCameraDrift_AreSubtle()
    {
        // Ken Burns convention: a few percent across a whole shot. If the viewer
        // consciously notices the drift, it is too strong. The sweep stays under half
        // a turn so the pan keeps advancing instead of retracing itself.
        var settings = new CameraDriftSettings();

        Assert.IsTrue(settings.Enabled);
        Assert.IsTrue(settings.ZoomAmplitude is > 0f and <= 0.10f);
        Assert.IsTrue(settings.PanAmplitude is > 0f and <= 0.10f);
        Assert.IsTrue(settings.MaxSlackFraction is > 0f and < 1f);
        Assert.IsTrue(settings.PanSweepCycles is > 0f and <= 0.5f);
    }

    #endregion
}
