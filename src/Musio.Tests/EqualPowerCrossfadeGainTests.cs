namespace Musio.Tests;

using Musio.Core.Audio;

/// <summary>
/// Covers <see cref="EqualPowerCrossfade.GainAt"/>, which combines a file's fade windows into a
/// single gain for an instant.
/// </summary>
/// <remarks>
/// This logic used to live as a private method inside <c>AudioPlaybackEngine</c>'s private nested
/// sample provider, where nothing could reach it — which is how the fade-out latching bug below
/// survived review. It was extracted precisely so these rules can be pinned directly.
/// </remarks>
[TestClass]
public sealed class EqualPowerCrossfadeGainTests
{
    private static AudioFadeWindow FadeOut(double startSeconds, double durationSeconds) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(durationSeconds), IsFadeIn: false);

    private static AudioFadeWindow FadeIn(double startSeconds, double durationSeconds) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(durationSeconds), IsFadeIn: true);

    [TestMethod]
    public void GainAt_NoWindows_IsFullVolume()
    {
        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt([], 5.0), 1e-9);
    }

    [TestMethod]
    public void GainAt_BeforeAnyWindow_IsFullVolume()
    {
        var windows = new[] { FadeOut(10, 1) };

        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt(windows, 2.0), 1e-9);
    }

    [TestMethod]
    public void GainAt_InsideAFadeOut_FollowsTheEqualPowerCurve()
    {
        var windows = new[] { FadeOut(10, 1) };

        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt(windows, 10.0), 1e-9);
        Assert.AreEqual(EqualPowerCrossfade.OutGain(0.5), EqualPowerCrossfade.GainAt(windows, 10.5), 1e-9);
    }

    [TestMethod]
    public void GainAt_InsideAFadeIn_FollowsTheEqualPowerCurve()
    {
        var windows = new[] { FadeIn(10, 1) };

        Assert.AreEqual(0.0, EqualPowerCrossfade.GainAt(windows, 10.0), 1e-9);
        Assert.AreEqual(EqualPowerCrossfade.InGain(0.5), EqualPowerCrossfade.GainAt(windows, 10.5), 1e-9);
    }

    [TestMethod]
    public void GainAt_AfterACompletedFadeOut_RestsAtSilence()
    {
        // Deliberate: the placement has conceptually stopped playing, so snapping back to full
        // volume would pop immediately after every crossfade.
        var windows = new[] { FadeOut(10, 1) };

        Assert.AreEqual(0.0, EqualPowerCrossfade.GainAt(windows, 11.0), 1e-9);
        Assert.AreEqual(0.0, EqualPowerCrossfade.GainAt(windows, 30.0), 1e-9);
    }

    [TestMethod]
    public void GainAt_AfterACompletedFadeIn_RestsAtFullVolume()
    {
        var windows = new[] { FadeIn(10, 1) };

        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt(windows, 11.0), 1e-9);
    }

    [TestMethod]
    public void GainAt_FadeInAfterAFadeOut_Recovers()
    {
        // THE regression test. A completed fade-out used to latch the gain to zero for the rest
        // of the file, so this later fade-in multiplied into that zero and stayed silent
        // forever — making "fade out -> silent gap -> fade in" impossible, even though multiple
        // windows per file are explicitly supported (a reordered timeline can use one recording
        // either side of another).
        var windows = new[] { FadeOut(10, 1), FadeIn(20, 1) };

        Assert.AreEqual(0.0, EqualPowerCrossfade.GainAt(windows, 15.0), 1e-9,
            "The silent gap between the two windows must stay silent.");

        Assert.AreEqual(EqualPowerCrossfade.InGain(0.5), EqualPowerCrossfade.GainAt(windows, 20.5), 1e-9,
            "The later fade-in must ramp back up, not stay latched at zero.");

        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt(windows, 25.0), 1e-9,
            "Once the fade-in completes, the file plays at full volume again.");
    }

    [TestMethod]
    public void GainAt_PrecedenceIsByStartTime_NotListOrder()
    {
        // Callers are not required to pre-sort, so the resting level must be decided by which
        // completed window started LAST, not by which happens to appear last in the list.
        var unsorted = new[] { FadeIn(20, 1), FadeOut(10, 1) };

        Assert.AreEqual(1.0, EqualPowerCrossfade.GainAt(unsorted, 25.0), 1e-9,
            "The fade-in starts later, so it — not the earlier fade-out — sets the resting level.");
    }

    [TestMethod]
    public void GainAt_InProgressWindow_SupersedesAnEarlierCompletedOne()
    {
        var windows = new[] { FadeOut(10, 1), FadeIn(20, 4) };

        // Mid fade-in: the in-progress window governs outright rather than being multiplied
        // into the completed fade-out's silence.
        Assert.AreEqual(EqualPowerCrossfade.InGain(0.25), EqualPowerCrossfade.GainAt(windows, 21.0), 1e-9);
    }

    [TestMethod]
    public void GainAt_ZeroDurationWindow_DoesNotDivideByZero()
    {
        var windows = new[] { FadeOut(10, 0) };

        double gain = EqualPowerCrossfade.GainAt(windows, 10.0);

        Assert.IsFalse(double.IsNaN(gain), "A zero-length window must not produce NaN.");
        Assert.AreEqual(0.0, gain, 1e-9, "A zero-length fade-out is already complete: silent.");
    }
}
