namespace Mixtri.Tests;

using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

[TestClass]
public sealed class TransitionResolverTests
{
    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWith(segments);

    private static VideoSegment Video(double durSec, TransitionConfig? inTransition = null)
        => TestTimelineBuilder.TransitionVideo("C:\\primary.mp4", durSec, inTransition);

    private static TextSlideSegment Slide(double durSec, TransitionConfig? inTransition = null) =>
        new() { Duration = TimeSpan.FromSeconds(durSec), InTransition = inTransition };

    [TestMethod]
    public void Resolve_VideoToVideoBoundary_NoInTransition_IsInactive()
    {
        // Preserves today's hard cut: two videos, no InTransition configured anywhere.
        var model = ModelWith(Video(4), Video(4));

        var r = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(4.1));

        Assert.IsFalse(r.Active);
        Assert.AreEqual(TransitionResolution.None, r);
    }

    [TestMethod]
    public void Resolve_VideoToVideoBoundary_WithExplicitInTransition_IsActiveWithConfiguredTypeAndDuration()
    {
        var config = new TransitionConfig { Type = TransitionType.SlideLeft, Duration = TimeSpan.FromMilliseconds(400) };
        var model = ModelWith(Video(4), Video(4, config));

        // Inside the 400ms window.
        var inside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(4.2));
        Assert.IsTrue(inside.Active);
        Assert.AreEqual(TransitionType.SlideLeft, inside.Type);
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), inside.Duration);

        // Outside the window (well within the 4s incoming segment).
        var outside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(4.5));
        Assert.IsFalse(outside.Active);
    }

    [TestMethod]
    public void Resolve_ExplicitNoneOnSlideBoundary_BeatsLegacyFallback()
    {
        // Without an explicit override this boundary would fall back to the legacy crossfade
        // (it touches a text slide) — an explicit "None" must suppress that.
        var noneConfig = new TransitionConfig { Type = TransitionType.None };
        var model = ModelWith(Video(4), Slide(5, noneConfig));

        var r = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(4.1));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_LegacyFallback_SlideToVideo_MatchesSlideTransitions()
    {
        // slide [0,5) | video [5,9), no InTransition configured -> legacy 500ms crossfade fallback.
        var model = ModelWith(Slide(5), Video(4));
        var outputTime = TimeSpan.FromSeconds(5.1);

        var r = TransitionResolver.Resolve(model, outputTime);
        var legacy = SlideTransitions.Resolve(model, outputTime);

        Assert.IsTrue(r.Active);
        Assert.AreEqual(TransitionType.CrossFade, r.Type);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), r.Duration);
        Assert.IsTrue(legacy.Active);
        Assert.AreEqual(legacy.Progress, r.RawProgress, 1e-9);
        // Legacy fallback is Linear, so eased == raw.
        Assert.AreEqual(r.RawProgress, r.EasedProgress, 1e-9);
    }

    [TestMethod]
    public void Resolve_DurationClampedByShortIncomingNeighbour()
    {
        // Incoming segment (video after slide) is only 300ms, so the window clamps to 150ms
        // (half of it), well under the requested 500ms.
        var model = ModelWith(Slide(5), Video(0.3));

        var inside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.075));
        Assert.IsTrue(inside.Active);
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), inside.Duration);
        Assert.AreEqual(0.5, inside.RawProgress, 1e-6);

        var outside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.2));
        Assert.IsFalse(outside.Active);
    }

    [TestMethod]
    public void Resolve_DurationClampedByShortOutgoingNeighbour()
    {
        // Outgoing segment (slide before video) is only 300ms, so the window clamps to 150ms.
        var model = ModelWith(Slide(0.3), Video(4));

        var inside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(0.375));
        Assert.IsTrue(inside.Active);
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), inside.Duration);
        Assert.AreEqual(0.5, inside.RawProgress, 1e-6);

        var outside = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(0.5));
        Assert.IsFalse(outside.Active);
    }

    [TestMethod]
    public void Resolve_FirstSegment_IsInactive()
    {
        var model = ModelWith(Slide(5), Video(4));

        var r = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(0.1));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_PastEndOfAllSegments_IsInactive()
    {
        var model = ModelWith(Video(4), Video(4));

        var r = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(100));

        Assert.IsFalse(r.Active);
    }

    [TestMethod]
    public void Resolve_LegacyFallback_FreezesOutgoingInsteadOfRolling()
    {
        // slide [0,5) | video [5,9), legacy fallback active in [5, 5.5).
        //
        // Back-compat regression guard. Before this feature, an unconfigured slide-adjacent
        // boundary held the outgoing side on its FINAL frame for the whole dissolve. Rolling
        // it instead would sample at and past the cut, so a trimmed video would start showing
        // footage the user had deliberately trimmed away — changing how already-saved
        // projects render. Handles are opt-in: you get them by choosing a transition.
        var model = ModelWith(Slide(5), Video(4));
        var frozen = TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1);

        var atStart = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5));
        Assert.IsTrue(atStart.Active);
        Assert.AreEqual(frozen, atStart.OutgoingLocalOffset);

        var partway = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.2));
        Assert.IsTrue(partway.Active);
        Assert.AreEqual(frozen, partway.OutgoingLocalOffset,
            "The legacy fallback must hold a FIXED instant for the whole dissolve.");
    }

    [TestMethod]
    public void Resolve_ExplicitConfig_OutgoingLocalOffset_RollsPastTheCutPoint()
    {
        // An explicitly configured transition DOES roll: this is the feature's whole premise,
        // and choosing a transition is the opt-in that authorises dipping into handles.
        var config = new TransitionConfig
        {
            Type = TransitionType.CrossFade,
            Duration = TimeSpan.FromMilliseconds(500),
        };
        var model = ModelWith(Video(5), Video(4, config));
        var outgoingDuration = TimeSpan.FromSeconds(5);

        var atStart = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5));
        Assert.IsTrue(atStart.Active);
        Assert.AreEqual(outgoingDuration, atStart.OutgoingLocalOffset);

        var partway = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.2));
        Assert.IsTrue(partway.Active);
        var expectedLocal = TimeSpan.FromSeconds(5.2) - partway.IncomingSegment!.Start;
        Assert.AreEqual(outgoingDuration + expectedLocal, partway.OutgoingLocalOffset);
        Assert.IsTrue(partway.OutgoingLocalOffset > outgoingDuration);
    }

    [TestMethod]
    public void Ease_Linear_ReturnsInputUnchanged()
    {
        foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            Assert.AreEqual(t, TransitionResolver.Ease(TransitionEasing.Linear, t), 1e-9);
    }

    [TestMethod]
    public void Ease_EaseInOut_IsMonotonicAndBounded()
    {
        double previous = TransitionResolver.Ease(TransitionEasing.EaseInOut, 0);
        Assert.AreEqual(0, previous, 1e-9);

        for (int i = 1; i <= 20; i++)
        {
            double t = i / 20.0;
            double eased = TransitionResolver.Ease(TransitionEasing.EaseInOut, t);
            Assert.IsTrue(eased >= previous - 1e-9, $"Eased progress must be monotonically non-decreasing (t={t}).");
            previous = eased;
        }

        Assert.AreEqual(1, previous, 1e-9);
    }

    [TestMethod]
    public void Ease_AllEasings_MapZeroToZeroAndOneToOne()
    {
        foreach (TransitionEasing easing in Enum.GetValues<TransitionEasing>())
        {
            Assert.AreEqual(0, TransitionResolver.Ease(easing, 0), 1e-9, $"{easing} should map 0 -> 0");
            Assert.AreEqual(1, TransitionResolver.Ease(easing, 1), 1e-9, $"{easing} should map 1 -> 1");
        }
    }

    [TestMethod]
    public void Resolve_ProgressEndpoints_ZeroAtStart_ApproachesButNeverReachesOne()
    {
        var model = ModelWith(Slide(5), Video(4));

        var atStart = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5));
        Assert.IsTrue(atStart.Active);
        Assert.AreEqual(0, atStart.RawProgress, 1e-9);

        // One tick before the window closes (500ms window) — as close to 1 as the window allows,
        // but must still be < 1 and Active must still be true (the upper bound is exclusive).
        var almostEnd = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.5) - TimeSpan.FromTicks(1));
        Assert.IsTrue(almostEnd.Active);
        Assert.IsTrue(almostEnd.RawProgress < 1.0);
        Assert.IsTrue(almostEnd.RawProgress > 0.99);

        // At exactly the end of the window, the transition has ended.
        var atEnd = TransitionResolver.Resolve(model, TimeSpan.FromSeconds(5.5));
        Assert.IsFalse(atEnd.Active);
    }

    /// <summary>
    /// Pins <c>Half</c>'s tick TRUNCATION with a literal expected value rather than by comparing
    /// against another implementation. An odd tick count is used deliberately: rounding up instead
    /// of truncating would be invisible to every other test here, and would silently diverge the
    /// clamp from the one the legacy <c>SlideTransitions</c> path has always applied.
    /// </summary>
    [TestMethod]
    public void Resolve_ClampsToHalfOfNeighbour_TruncatingOddTicks()
    {
        // 7-tick outgoing segment: half is 3 ticks, NOT 3.5 rounded to 4.
        var outgoing = new VideoSegment
        {
            VideoFilePath = "C:\\primary.mp4",
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromTicks(7),
            Duration = TimeSpan.FromTicks(7),
        };
        var incoming = Video(4, new TransitionConfig
        {
            Type = TransitionType.CrossFade,
            Duration = TimeSpan.FromSeconds(1),
        });
        var model = ModelWith(outgoing, incoming);

        var r = TransitionResolver.Resolve(model, incoming.Start);

        Assert.IsTrue(r.Active);
        Assert.AreEqual(TimeSpan.FromTicks(3), r.Duration);

        // The window is exclusive at its upper bound, so tick 3 is already past it.
        Assert.IsFalse(TransitionResolver.Resolve(model, incoming.Start + TimeSpan.FromTicks(3)).Active);
        Assert.IsTrue(TransitionResolver.Resolve(model, incoming.Start + TimeSpan.FromTicks(2)).Active);
    }

    /// <summary>
    /// Pins the <c>maxDurationOverride</c> overload with literal expected values. Ignoring the
    /// override outright would otherwise pass every other test in this class, since the default
    /// overload never supplies one.
    /// </summary>
    [TestMethod]
    public void Resolve_MaxDurationOverride_CapsEffectiveDurationAndRescalesProgress()
    {
        var config = new TransitionConfig
        {
            Type = TransitionType.CrossFade,
            Duration = TimeSpan.FromSeconds(1),
            Easing = TransitionEasing.Linear,
        };
        var model = ModelWith(Video(4), Video(4, config));
        var incomingStart = TimeSpan.FromSeconds(4);

        // Without an override the configured 1s wins (both neighbours are 4s, so half = 2s).
        var unclamped = TransitionResolver.Resolve(model, incomingStart + TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(TimeSpan.FromSeconds(1), unclamped.Duration);
        Assert.AreEqual(0.5, unclamped.RawProgress, 1e-9);

        // A 200ms override caps it, which also rescales progress against the SHORTER window.
        var clamped = TransitionResolver.Resolve(
            model, incomingStart + TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
        Assert.IsTrue(clamped.Active);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), clamped.Duration);
        Assert.AreEqual(0.5, clamped.RawProgress, 1e-9);

        // ...and closes the window early: 500ms in is outside a 200ms transition.
        Assert.IsFalse(TransitionResolver.Resolve(
            model, incomingStart + TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(200)).Active);

        // An override LONGER than the configured duration must not extend it.
        var notExtended = TransitionResolver.Resolve(
            model, incomingStart + TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.FromSeconds(1), notExtended.Duration);
    }

    /// <summary>
    /// The legacy <c>SlideTransitions</c> shim must stay completely inert to
    /// <see cref="TimelineSegment.InTransition"/> while the preview and export paths are
    /// mid-migration: an explicit config must neither suppress nor reshape what it returns.
    /// </summary>
    [TestMethod]
    public void SlideTransitionsShim_IgnoresExplicitInTransitionEntirely()
    {
        var start = TimeSpan.FromSeconds(4.1);

        // Explicit None on a slide boundary silences the RESOLVER...
        var suppressed = ModelWith(Video(4), Slide(4, new TransitionConfig { Type = TransitionType.None }));
        Assert.IsFalse(TransitionResolver.Resolve(suppressed, start).Active);
        // ...but must NOT silence the shim, which still owns the un-migrated render paths.
#pragma warning disable CS0618
        var shimSuppressed = SlideTransitions.Resolve(suppressed, start);
#pragma warning restore CS0618
        Assert.IsTrue(shimSuppressed.Active);

        // A configured short duration reshapes the RESOLVER's window...
        var shortened = ModelWith(Video(4), Slide(4, new TransitionConfig
        {
            Type = TransitionType.Wipe,
            Duration = TimeSpan.FromMilliseconds(100),
        }));
        Assert.IsFalse(TransitionResolver.Resolve(shortened, start).Active);
        // ...but the shim keeps its fixed 500ms legacy window.
#pragma warning disable CS0618
        var shimShortened = SlideTransitions.Resolve(shortened, start);
#pragma warning restore CS0618
        Assert.IsTrue(shimShortened.Active);
        Assert.AreEqual(shimSuppressed.Progress, shimShortened.Progress, 1e-9);
    }
}
