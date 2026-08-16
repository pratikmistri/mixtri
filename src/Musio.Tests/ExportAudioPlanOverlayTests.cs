using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="ExportAudioPlan"/> on OVERLAY timelines: audio is sliced by
/// <see cref="TimelineModel.VisibleRanges"/>, so a base segment hidden behind a higher track
/// contributes no audio for the hidden span, and a base segment covered in the middle
/// contributes two separate placements.
/// </summary>
/// <remarks>
/// This is the arithmetic the multi-track work added to the export audio path, and it was the
/// one part of it with no coverage: <see cref="ExportAudioPlanTests"/> and
/// <see cref="ExportAudioPlanTransitionTests"/> both predate overlay tracks and build only
/// single-track (contiguous base chain) timelines, so nothing exercised the visible-range
/// splitting or the rule deciding WHICH slice inherits a boundary's fade metadata.
/// </remarks>
[TestClass]
public sealed class ExportAudioPlanOverlayTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";
    private const string Overlay = @"C:\rec\overlay\video.mp4";

    private static Project NewProject() => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(30),
        AudioFilePaths = [],
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

    /// <summary>A full-frame segment parked on an overlay track at an absolute output time.</summary>
    private static VideoSegment OverlayVideo(
        string path, double startSec, double durationSec, int trackIndex = 1)
    {
        var segment = Video(path, sourceStartSec: 0, sourceDurationSec: durationSec);
        segment.TrackIndex = trackIndex;
        segment.Start = TimeSpan.FromSeconds(startSec);
        return segment;
    }

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    /// <summary>Embedded-track placements for one source file, in output order.</summary>
    private static List<AudioPlacement> Embedded(IReadOnlyList<AudioPlacement> plan, string path)
        => [.. plan
            .Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack && p.SourcePath == path)
            .OrderBy(p => p.Delay)];

    private static void AssertSeconds(double expected, TimeSpan actual, string message)
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    private static void AssertSlice(
        AudioPlacement placement, double trimFromStart, double take, double delay, string message)
    {
        AssertSeconds(trimFromStart, placement.TrimFromStart, $"{message}: source in point");
        AssertSeconds(take, placement.TakeDuration!.Value, $"{message}: take");
        AssertSeconds(delay, placement.Delay, $"{message}: output position");
    }

    #region Hidden base audio

    [TestMethod]
    public void OverlayCoveringTheWholeBaseSegment_SilencesIt()
    {
        var project = NewProject();
        // Base 0..10s is entirely hidden behind a full-length overlay.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            OverlayVideo(Overlay, startSec: 0, durationSec: 10));

        var plan = ExportAudioPlan.Build(project, model, null);

        Assert.AreEqual(0, Embedded(plan, Primary).Count,
            "A base segment nobody can see must not be heard either.");
        Assert.AreEqual(1, Embedded(plan, Overlay).Count,
            "The covering overlay supplies the audio in its place.");
    }

    [TestMethod]
    public void OverlayCoveringTheTail_TruncatesBaseAudioToTheVisibleHead()
    {
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            OverlayVideo(Overlay, startSec: 6, durationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(1, baseSlices.Count, "One visible span means one placement.");
        AssertSlice(baseSlices[0], trimFromStart: 0, take: 6, delay: 0, "Visible head");
    }

    [TestMethod]
    public void OverlayCoveringTheHead_StartsBaseAudioAtTheReveal()
    {
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            OverlayVideo(Overlay, startSec: 0, durationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(1, baseSlices.Count);
        AssertSlice(baseSlices[0], trimFromStart: 4, take: 6, delay: 4,
            "Audio must resume from the source position the reveal exposes, not from zero");
    }

    #endregion

    #region Split visible ranges

    [TestMethod]
    public void OverlayCoveringTheMiddle_SplitsBaseAudioIntoTwoPlacements()
    {
        var project = NewProject();
        // Base 0..10s, overlay hides 3..7s.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            OverlayVideo(Overlay, startSec: 3, durationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(2, baseSlices.Count, "The covered middle splits the segment in two.");
        AssertSlice(baseSlices[0], trimFromStart: 0, take: 3, delay: 0, "Head slice");
        AssertSlice(baseSlices[1], trimFromStart: 7, take: 3, delay: 7, "Tail slice");
    }

    [TestMethod]
    public void TwoOverlaysOnOneBaseSegment_LeaveThreeAudibleSpans()
    {
        var project = NewProject();
        // Base 0..12s; overlays hide 2..4s and 8..10s, leaving 0..2, 4..8 and 10..12.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 12),
            OverlayVideo(Overlay, startSec: 2, durationSec: 2),
            OverlayVideo(Overlay, startSec: 8, durationSec: 2));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(3, baseSlices.Count);
        AssertSlice(baseSlices[0], trimFromStart: 0, take: 2, delay: 0, "First gap");
        AssertSlice(baseSlices[1], trimFromStart: 4, take: 4, delay: 4, "Middle gap");
        AssertSlice(baseSlices[2], trimFromStart: 10, take: 2, delay: 10, "Last gap");
    }

    [TestMethod]
    public void OverlappingOverlays_AreMergedIntoOneHiddenSpan()
    {
        var project = NewProject();
        // The two overlays overlap (2..6 and 4..8), so exactly one span is hidden: 2..8.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            OverlayVideo(Overlay, startSec: 2, durationSec: 4),
            OverlayVideo(Overlay, startSec: 4, durationSec: 4, trackIndex: 2));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(2, baseSlices.Count, "Overlapping covers must not produce a zero-length slice.");
        AssertSlice(baseSlices[0], trimFromStart: 0, take: 2, delay: 0, "Before the covered span");
        AssertSlice(baseSlices[1], trimFromStart: 8, take: 2, delay: 8, "After the covered span");
    }

    [TestMethod]
    public void SpeedAdjustedBaseSegment_ScalesTheVisibleSourceRange()
    {
        var project = NewProject();
        // 2x speed: 10s of source plays in 5s of output. An overlay hides output 0..2s,
        // which corresponds to source 0..4s.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10, speed: 2.0),
            OverlayVideo(Overlay, startSec: 0, durationSec: 2));

        var plan = ExportAudioPlan.Build(project, model, null);
        var baseSlices = Embedded(plan, Primary);

        Assert.AreEqual(1, baseSlices.Count);
        AssertSeconds(4, baseSlices[0].TrimFromStart,
            "The visible range must be projected through the speed factor, not copied verbatim");
        AssertSeconds(2, baseSlices[0].Delay, "The slice starts where the overlay stops covering it");
        Assert.IsTrue(baseSlices[0].PlaysAtNativeRateOnSpeedAdjustedSegment,
            "The slice keeps the segment's speed-limitation flag");
    }

    #endregion

    #region Fade metadata attribution

    /// <summary>
    /// The comment in <c>ExportAudioPlan</c> is explicit that a middle slice revealed between
    /// two overlays is ordinary segment audio and must not borrow another edge's fade data.
    /// This pins that: only the slice that actually touches the base-chain boundary fades.
    /// </summary>
    [TestMethod]
    public void OnlyTheSliceTouchingTheBoundaryCarriesTheCrossfade()
    {
        var project = NewProject();
        var transition = new TransitionConfig
        {
            Type = TransitionType.CrossFade,
            Duration = TimeSpan.FromSeconds(1),
        };

        // Base chain A(0..10) -> B(10..20) with a crossfade into B. An overlay hides 3..7s,
        // splitting A's audio into [0,3) and [7,10) — only the latter meets the boundary.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 10, inTransition: transition),
            OverlayVideo(Overlay, startSec: 3, durationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var slices = Embedded(plan, Primary);

        Assert.AreEqual(3, slices.Count, "Two slices for A, one for B.");

        Assert.AreEqual(TimeSpan.Zero, slices[0].FadeOutDuration,
            "A's head slice ends at an overlay edge, not at the cut — it must not fade out.");
        Assert.AreEqual(TimeSpan.Zero, slices[0].FadeInDuration,
            "Nothing dissolves into the start of the timeline.");

        AssertSeconds(1, slices[1].FadeOutDuration,
            "A's tail slice meets the cut, so it carries the outgoing half of the dissolve");

        AssertSeconds(1, slices[2].FadeInDuration, "B carries the incoming half of the dissolve");
    }

    /// <summary>
    /// The complement: when the boundary-touching span is hidden, there is no placement to
    /// attach the outgoing fade to, and the remaining audio must stay unfaded rather than
    /// having the metadata land on the wrong slice.
    /// </summary>
    [TestMethod]
    public void WhenTheBoundaryItselfIsCovered_NoSliceInheritsTheFadeOut()
    {
        var project = NewProject();
        var transition = new TransitionConfig
        {
            Type = TransitionType.CrossFade,
            Duration = TimeSpan.FromSeconds(1),
        };

        // The overlay hides 6..10s — the whole tail of A, boundary included.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 10, inTransition: transition),
            OverlayVideo(Overlay, startSec: 6, durationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var slices = Embedded(plan, Primary);

        Assert.AreEqual(2, slices.Count, "One surviving slice for A, one for B.");
        Assert.AreEqual(TimeSpan.Zero, slices[0].FadeOutDuration,
            "A's surviving head does not touch the cut, so it must not describe a dissolve.");
        AssertSeconds(6, slices[0].TakeDuration!.Value,
            "The hidden tail is not muxed, and the take is not extended past it either.");
    }

    #endregion
}
