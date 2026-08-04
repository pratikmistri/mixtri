using Musio.Core.Audio;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Tests for the T7 "audio crossfade" behaviour layered onto <see cref="ExportAudioPlan"/>:
/// active <see cref="TransitionResolver"/> boundaries extend the outgoing placement's
/// <see cref="AudioPlacement.TakeDuration"/> and set the fade metadata
/// (<see cref="AudioPlacement.FadeOutDuration"/>/<see cref="AudioPlacement.FadeInDuration"/>)
/// that describes an equal-power crossfade (<see cref="EqualPowerCrossfade"/>) across the
/// dissolve. See <see cref="ExportAudioPlanTests"/> for the pre-existing, untouched
/// placement-mapping behaviour these tests build on top of.
/// </summary>
[TestClass]
public sealed class ExportAudioPlanTransitionTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";

    private static Project NewProject(params string[] audioPaths) => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(30),
        AudioFilePaths = [.. audioPaths],
    };

    private static VideoSegment Video(
        string path, double sourceStartSec, double sourceDurationSec,
        double speed = 1.0, TransitionConfig? inTransition = null) => new()
        {
            VideoFilePath = path,
            SourceStart = TimeSpan.FromSeconds(sourceStartSec),
            SourceDuration = TimeSpan.FromSeconds(sourceDurationSec),
            Duration = TimeSpan.FromSeconds(sourceDurationSec / speed),
            SpeedFactor = speed,
            InTransition = inTransition,
        };

    private static TextSlideSegment Slide(double durationSec, TransitionConfig? inTransition = null) => new()
    {
        Duration = TimeSpan.FromSeconds(durationSec),
        InTransition = inTransition,
    };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    private static void AssertSeconds(double expected, TimeSpan actual, string message)
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    #region Back-compat: no transition configured anywhere

    [TestMethod]
    public void NoActiveTransition_PlacementsAreUnchangedAndFadesAreZero()
    {
        // Two plain video segments, no InTransition, no text slide touching either side —
        // TransitionResolver reports None at this boundary, so nothing here may differ
        // from ExportAudioPlanTests' pre-existing (pre-T7) expectations.
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(5, embedded[0].TakeDuration!.Value, "No transition: take is not extended");
        AssertSeconds(5, embedded[1].TakeDuration!.Value, "No transition: take is not extended");
        Assert.AreEqual(TimeSpan.Zero, embedded[0].FadeOutDuration, "No transition: no fade-out");
        Assert.AreEqual(TimeSpan.Zero, embedded[0].FadeInDuration, "No transition: no fade-in");
        Assert.AreEqual(TimeSpan.Zero, embedded[1].FadeOutDuration, "No transition: no fade-out");
        Assert.AreEqual(TimeSpan.Zero, embedded[1].FadeInDuration, "No transition: no fade-in");
    }

    #endregion

    #region Active transition between two video segments

    [TestMethod]
    public void ActiveTransition_ExtendsOutgoingTakeAndSetsBothFades()
    {
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 10,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(11, embedded[0].TakeDuration!.Value,
            "Outgoing take is extended by exactly the transition's clamped duration");
        AssertSeconds(1, embedded[0].FadeOutDuration,
            "Outgoing fade-out matches the transition duration");
        AssertSeconds(10, embedded[1].TakeDuration!.Value,
            "Incoming take is untouched by the transition");
        AssertSeconds(1, embedded[1].FadeInDuration,
            "Incoming fade-in matches the transition duration");
        AssertSeconds(0, embedded[0].FadeInDuration, "Outgoing has no fade-in");
        AssertSeconds(0, embedded[1].FadeOutDuration, "Incoming has no fade-out (yet)");
    }

    [TestMethod]
    public void ActiveTransition_AlsoExtendsSeparatelyRecordedAudioFiles()
    {
        const string Mic = @"C:\rec\primary\mic_0.wav";
        var project = NewProject(Mic);
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 10,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));

        var plan = ExportAudioPlan.Build(project, model, null);
        var mic = plan.Where(p => p.Kind == AudioSourceKind.AudioFile).OrderBy(p => p.Delay).ToList();

        Assert.AreEqual(2, mic.Count);
        AssertSeconds(11, mic[0].TakeDuration!.Value, "Mic audio extends exactly like the embedded track");
        AssertSeconds(1, mic[0].FadeOutDuration, "Mic audio gets the same fade-out");
    }

    [TestMethod]
    public void ExtensionNeverExceedsTheTransitionsOwnClampedDuration()
    {
        // Both neighbouring segments are only 1s long, so TransitionResolver clamps the
        // configured 2s duration down to half of the shorter neighbour (0.5s) — the pure,
        // no-I/O bound ExportAudioPlan can actually enforce (it cannot verify the real
        // source file's length; see its class remarks). The extension must follow that
        // clamp, not the configured 2s.
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 1),
            Video(
                Primary, sourceStartSec: 1, sourceDurationSec: 1,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(2) }));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        AssertSeconds(1.5, embedded[0].TakeDuration!.Value,
            "Extension is capped to the clamped 0.5s transition window, not the configured 2s");
        AssertSeconds(0.5, embedded[0].FadeOutDuration, "Fade-out matches the clamped window");
        AssertSeconds(0.5, embedded[1].FadeInDuration, "Fade-in matches the clamped window");
    }

    [TestMethod]
    public void ExplicitNoneTransition_SuppressesTheFadeEvenNextToASlide()
    {
        // An explicit TransitionType.None on an incoming segment must silence even the
        // legacy slide-adjacent fallback (see TransitionResolver's remarks) FOR THE
        // BOUNDARY IT GOVERNS — but a text slide sits on TWO independent boundaries (video
        // into the slide, and the slide into the next video), each resolved and suppressed
        // separately. Both must be configured with an explicit None here to prove neither
        // one is affected by the fix that made the video-into-slide boundary resolve at
        // all (see SlideAdjacentLegacyBoundary_FadesTheClipBeforeAndAfterTheSlide, which
        // covers the case where NEITHER side is explicitly configured).
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Slide(2, inTransition: new TransitionConfig { Type = TransitionType.None }),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5, inTransition: new TransitionConfig { Type = TransitionType.None }));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        AssertSeconds(5, embedded[0].TakeDuration!.Value, "No extension into the slide when explicitly suppressed");
        Assert.AreEqual(TimeSpan.Zero, embedded[0].FadeOutDuration, "No fade-out into the slide when explicitly suppressed");
        AssertSeconds(5, embedded[1].TakeDuration!.Value, "No extension for the clip after the slide either");
        Assert.AreEqual(TimeSpan.Zero, embedded[1].FadeInDuration, "No fade-in when explicitly suppressed");
    }

    #endregion

    #region Legacy slide-adjacent fallback

    [TestMethod]
    public void SlideAdjacentLegacyBoundary_FadesTheClipBeforeAndAfterTheSlide()
    {
        // A text slide touches TWO boundaries, and TransitionResolver's legacy fallback
        // (500ms linear CrossFade whenever incoming OR outgoing is a TextSlideSegment)
        // applies independently at BOTH: video-into-slide (incoming = slide) AND
        // slide-into-video (outgoing = slide). The slide itself carries no audio, but the
        // VIDEO on either side of it does, and BOTH must get their own fade — the video
        // before the slide fades OUT into it (its own audio keeps rolling/dissolving
        // instead of hard-stopping the instant the slide begins), and the video after the
        // slide fades IN from silence. This was a real gap: a boundary whose incoming side
        // is a TextSlideSegment was previously never resolved by BuildFromSegments at all
        // (only video segments' own Starts were checked), so the outgoing clip's fade-out
        // into a slide was silently dropped.
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Slide(3),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(5.5, embedded[0].TakeDuration!.Value,
            "The clip before the slide extends its own audio underneath the slide by the legacy 500ms fallback");
        AssertSeconds(0.5, embedded[0].FadeOutDuration,
            "The clip before the slide fades out using the legacy 500ms crossfade duration");
        AssertSeconds(0.5, embedded[1].FadeInDuration,
            "The clip after the slide fades in from silence using the legacy 500ms crossfade duration");
    }

    #endregion

    #region Speed-adjusted segments keep their existing invariant

    [TestMethod]
    public void SpeedAdjustedOutgoingSegment_TakeDurationStillObeysTheExistingNativeRateCap()
    {
        // The pre-existing rule (see ExportAudioPlanTests.SpedUpSegment_ClampsTakeToTheOutputDuration):
        // audio for a speed-adjusted segment is muxed at native rate and hard-capped to the
        // segment's own output room so it can never overlap the next segment. An active
        // transition must not be allowed to push TakeDuration past that existing cap.
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10, speed: 2.0), // 10s source @2x -> 5s output
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 5,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        AssertSeconds(5, embedded[0].TakeDuration!.Value,
            "Speed-adjusted segment's take is unchanged by the transition — same as with no transition");
        Assert.IsTrue(embedded[0].PlaysAtNativeRateOnSpeedAdjustedSegment);
        Assert.IsTrue(embedded[0].FadeOutDuration <= embedded[0].TakeDuration!.Value,
            "Any fade-out that is still applied must fit inside the existing (unextended) take");
    }

    #endregion

    #region Equal-power crossfade curve

    [TestMethod]
    public void EqualPowerCrossfade_OutGain_Is1AtStartAnd0AtEnd()
    {
        Assert.AreEqual(1.0, EqualPowerCrossfade.OutGain(0), 1e-9);
        Assert.AreEqual(0.0, EqualPowerCrossfade.OutGain(1), 1e-9);
    }

    [TestMethod]
    public void EqualPowerCrossfade_InGain_Is0AtStartAnd1AtEnd()
    {
        Assert.AreEqual(0.0, EqualPowerCrossfade.InGain(0), 1e-9);
        Assert.AreEqual(1.0, EqualPowerCrossfade.InGain(1), 1e-9);
    }

    [TestMethod]
    public void EqualPowerCrossfade_GainsSumToConstantPowerAcrossTheWholeWindow()
    {
        for (double t = 0; t <= 1.0; t += 0.01)
        {
            double outGain = EqualPowerCrossfade.OutGain(t);
            double inGain = EqualPowerCrossfade.InGain(t);
            double power = outGain * outGain + inGain * inGain;
            Assert.AreEqual(1.0, power, 1e-9, $"out²+in² must stay 1 at t={t}");
        }
    }

    [TestMethod]
    public void EqualPowerCrossfade_ClampsProgressOutsideZeroToOne()
    {
        Assert.AreEqual(1.0, EqualPowerCrossfade.OutGain(-0.5), 1e-9);
        Assert.AreEqual(0.0, EqualPowerCrossfade.OutGain(1.5), 1e-9);
        Assert.AreEqual(0.0, EqualPowerCrossfade.InGain(-0.5), 1e-9);
        Assert.AreEqual(1.0, EqualPowerCrossfade.InGain(1.5), 1e-9);
    }

    #endregion

    #region VideoEncoder export round-trip: the hard-cut reconstruction must be exact

    // These tests exercise VideoEncoder.ExportTakeDuration directly (internal, visible here
    // via Musio.Core's InternalsVisibleTo) against REAL AudioPlacements produced by
    // ExportAudioPlan.Build — not hand-built structs — so a regression in either class
    // would be caught. Each test compares the corrected take against an INDEPENDENTLY
    // built baseline plan with NO transition at all: if VideoEncoder.ExportTakeDuration's
    // subtraction were ever removed (e.g. "return take;" unconditionally), every one of
    // these would fail because the placement's raw (extended) TakeDuration would no longer
    // match that baseline. This is the single highest-consequence regression this feature
    // can produce (full-volume overlapping audio in every export at a transition boundary),
    // so it must never be provable only indirectly through a full WinRT mux.

    [TestMethod]
    public void ExportTakeDuration_ReconstructsExactPreFeatureHardCut_ForActiveTransition()
    {
        var project = NewProject();
        var withTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 10,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));
        var withoutTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 10));

        var outgoingWithFade = ExportAudioPlan.Build(project, withTransition, null)
            .First(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack);
        var outgoingBaseline = ExportAudioPlan.Build(project, withoutTransition, null)
            .First(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack);

        Assert.IsTrue(outgoingWithFade.FadeOutDuration > TimeSpan.Zero, "Sanity: this placement really is faded");
        Assert.AreNotEqual(outgoingBaseline.TakeDuration, outgoingWithFade.TakeDuration,
            "Sanity: the planner really did extend the take (otherwise this test would prove nothing)");

        var muxedTake = VideoEncoder.ExportTakeDuration(outgoingWithFade);
        Assert.AreEqual(outgoingBaseline.TakeDuration, muxedTake,
            "The muxed take must exactly reconstruct the pre-feature (no-transition) hard cut");
    }

    [TestMethod]
    public void ExportTakeDuration_ReconstructsExactPreFeatureHardCut_ForSeparatelyRecordedAudio()
    {
        const string Mic = @"C:\rec\primary\mic_0.wav";
        var project = NewProject(Mic);
        var withTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 10,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));
        var withoutTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 10));

        var micWithFade = ExportAudioPlan.Build(project, withTransition, null)
            .First(p => p.Kind == AudioSourceKind.AudioFile);
        var micBaseline = ExportAudioPlan.Build(project, withoutTransition, null)
            .First(p => p.Kind == AudioSourceKind.AudioFile);

        var muxedTake = VideoEncoder.ExportTakeDuration(micWithFade);
        Assert.AreEqual(micBaseline.TakeDuration, muxedTake,
            "A separately recorded audio file's muxed take must also reconstruct the pre-feature hard cut");
    }

    [TestMethod]
    public void ExportTakeDuration_ReconstructsExactPreFeatureHardCut_WithNonZeroAudioOffset()
    {
        const string Mic = @"C:\rec\primary\mic_0.wav";
        var project = new Project
        {
            VideoFilePath = Primary,
            Duration = TimeSpan.FromSeconds(30),
            AudioFilePaths = [Mic],
            AudioToVideoOffsetSeconds = 0.2,
        };
        var withTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 10,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));
        var withoutTransition = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 10));

        var micWithFade = ExportAudioPlan.Build(project, withTransition, null)
            .First(p => p.Kind == AudioSourceKind.AudioFile);
        var micBaseline = ExportAudioPlan.Build(project, withoutTransition, null)
            .First(p => p.Kind == AudioSourceKind.AudioFile);

        var muxedTake = VideoEncoder.ExportTakeDuration(micWithFade);
        Assert.AreEqual(micBaseline.TakeDuration, muxedTake,
            "A non-zero AudioToVideoOffsetSeconds must not change the round-trip guarantee");
    }

    [TestMethod]
    public void ExportTakeDuration_ReconstructsExactPreFeatureHardCut_AtTheSlideAdjacentLegacyBoundary()
    {
        var project = NewProject();
        var withSlide = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Slide(3),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5));
        var withoutSlide = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5));

        var beforeSlideWithFade = ExportAudioPlan.Build(project, withSlide, null)
            .Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).First();
        var beforeSlideBaseline = ExportAudioPlan.Build(project, withoutSlide, null)
            .Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).First();

        Assert.IsTrue(beforeSlideWithFade.FadeOutDuration > TimeSpan.Zero,
            "Sanity: the legacy slide-adjacent fallback really does fade this placement (see the boundary-resolution fix)");

        var muxedTake = VideoEncoder.ExportTakeDuration(beforeSlideWithFade);
        Assert.AreEqual(beforeSlideBaseline.TakeDuration, muxedTake,
            "The clip before a text slide must also mux back to its pre-feature hard cut");
    }

    [TestMethod]
    public void ExportTakeDuration_DoesNotShortenASpeedAdjustedSegmentsAlreadyCorrectTake()
    {
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10, speed: 2.0),
            Video(
                Primary, sourceStartSec: 10, sourceDurationSec: 5,
                inTransition: new TransitionConfig { Type = TransitionType.CrossFade, Duration = TimeSpan.FromSeconds(1) }));

        var speedAdjusted = ExportAudioPlan.Build(project, model, null)
            .First(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack);

        Assert.IsTrue(speedAdjusted.PlaysAtNativeRateOnSpeedAdjustedSegment);

        var muxedTake = VideoEncoder.ExportTakeDuration(speedAdjusted);
        Assert.AreEqual(speedAdjusted.TakeDuration, muxedTake,
            "A speed-adjusted placement's take was never extended, so it must be muxed unchanged " +
            "even when FadeOutDuration is non-zero — subtracting here would wrongly shorten it");
    }

    [TestMethod]
    public void ExportTakeDuration_LeavesLegacyNoTimelinePlacementsUnchanged()
    {
        // BuildLegacy never sets FadeOutDuration (it is not transition-aware at all), so
        // this is trivially a hard cut already — but it is included so a future change to
        // BuildLegacy that accidentally starts setting fades would be caught immediately.
        var project = NewProject();
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(2),
            TrimEnd = TimeSpan.FromSeconds(8),
        };
        var mapper = new TimelineMapper(model, 30);

        var placement = ExportAudioPlan.Build(project, model, mapper)
            .First(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack);

        Assert.AreEqual(TimeSpan.Zero, placement.FadeOutDuration, "Legacy path never sets a fade");
        Assert.AreEqual(placement.TakeDuration, VideoEncoder.ExportTakeDuration(placement),
            "With no fade, ExportTakeDuration must return the take completely unchanged");
    }

    #endregion

    #region VideoEncoder.ClampToSourceDuration: bounding metadata against the real source length

    [TestMethod]
    public void ClampToSourceDuration_LeavesPlacementUnchanged_WhenDurationIsUnknown()
    {
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.Zero, TimeSpan.FromSeconds(11),
            TimeSpan.Zero, false, FadeOutDuration: TimeSpan.FromSeconds(1));

        var clamped = VideoEncoder.ClampToSourceDuration(placement, sourceDuration: null);

        Assert.AreEqual(placement, clamped, "Without a known real duration there is nothing safe to clamp against");
    }

    [TestMethod]
    public void ClampToSourceDuration_LeavesPlacementUnchanged_WhenAlreadyWithinBounds()
    {
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.Zero, TimeSpan.FromSeconds(9),
            TimeSpan.Zero, false, FadeOutDuration: TimeSpan.FromSeconds(1));

        var clamped = VideoEncoder.ClampToSourceDuration(placement, TimeSpan.FromSeconds(10));

        Assert.AreEqual(placement, clamped, "A take that already fits inside the real duration is untouched");
    }

    [TestMethod]
    public void ClampToSourceDuration_CapsAnExtendedTakeToTheFilesRealRemainingLength()
    {
        // Exactly the scenario the review flagged: a segment trimmed to end at a 10s
        // file's own EOF, with a 1s transition planning an 11s take.
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.Zero, TimeSpan.FromSeconds(11),
            TimeSpan.Zero, false, FadeOutDuration: TimeSpan.FromSeconds(1));

        var clamped = VideoEncoder.ClampToSourceDuration(placement, TimeSpan.FromSeconds(10));

        AssertSeconds(10, clamped.TakeDuration!.Value, "Take is capped to the file's real remaining length");

        // The fade must shrink by however much of the EXTENSION the file could not satisfy.
        // The whole 1s extension was clamped away here, so no extension survives.
        AssertSeconds(0, clamped.FadeOutDuration,
            "Fade-out must drop by the clamped-away extension, not survive at its planned length");

        // The point of all of the above: the muxed hard cut must still be the original 10s.
        // Leaving FadeOutDuration at 1s made ExportTakeDuration return 10 - 1 = 9s, silently
        // truncating a second off the end of every export whose audio ended at EOF.
        AssertSeconds(10, VideoEncoder.ExportTakeDuration(clamped)!.Value,
            "Muxed duration must reproduce the original hard cut exactly");
    }

    [TestMethod]
    public void ClampToSourceDuration_PartiallyClampedExtension_KeepsTheSurvivingRemainder()
    {
        // Only half of the 1s extension is available (file has 10.5s, plan wanted 11s), so
        // half the extension survives and the hard cut is still exactly 10s.
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.Zero, TimeSpan.FromSeconds(11),
            TimeSpan.Zero, false, FadeOutDuration: TimeSpan.FromSeconds(1));

        var clamped = VideoEncoder.ClampToSourceDuration(placement, TimeSpan.FromSeconds(10.5));

        AssertSeconds(10.5, clamped.TakeDuration!.Value, "Take is capped to the real remaining length");
        AssertSeconds(0.5, clamped.FadeOutDuration, "Half the planned extension survives");
        AssertSeconds(10, VideoEncoder.ExportTakeDuration(clamped)!.Value,
            "Muxed duration must still reproduce the original hard cut");
    }

    [TestMethod]
    public void ClampToSourceDuration_AlsoCapsFadeDurationsToTheClampedTake()
    {
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.Zero, TimeSpan.FromSeconds(11),
            TimeSpan.Zero, false,
            FadeOutDuration: TimeSpan.FromSeconds(3), FadeInDuration: TimeSpan.FromSeconds(3));

        // Real duration is only 2s past TrimFromStart — smaller than either fade.
        var clamped = VideoEncoder.ClampToSourceDuration(placement, TimeSpan.FromSeconds(2));

        AssertSeconds(2, clamped.TakeDuration!.Value, "Take cannot exceed the real duration");

        // 9s of the planned take was clamped away, which is more than the entire 3s extension,
        // so nothing of the fade-out survives.
        AssertSeconds(0, clamped.FadeOutDuration,
            "Fade-out is consumed entirely by the clamped-away extension");

        // A fade-IN does not extend the take (it lives at the placement's leading edge), so it
        // is only ever capped to what is actually there to ramp over.
        AssertSeconds(2, clamped.FadeInDuration, "Fade-in cannot exceed the (clamped) take");
    }

    [TestMethod]
    public void ClampToSourceDuration_ClampsTrimFromStartPastTheFilesEnd()
    {
        var placement = new AudioPlacement(
            Primary, AudioSourceKind.EmbeddedVideoTrack, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5),
            TimeSpan.Zero, false, FadeOutDuration: TimeSpan.FromSeconds(1));

        var clamped = VideoEncoder.ClampToSourceDuration(placement, TimeSpan.FromSeconds(10));

        AssertSeconds(10, clamped.TrimFromStart, "TrimFromStart cannot exceed the file's real duration");
        AssertSeconds(0, clamped.TakeDuration!.Value, "Nothing real is left to take once trimmed to the file's end");
        AssertSeconds(0, clamped.FadeOutDuration, "A zero take leaves no room for any fade");
    }

    #endregion
}

