using System.Text.Json;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

namespace Musio.Tests;

/// <summary>
/// Per-segment Animate In / Animate Out: the toggles that turn a zoom segment's leading and
/// trailing eases into cuts.
/// <para>
/// The governing rule these all check is that a cut does NOT shorten the segment. The ease
/// span stays exactly where the user dragged it out to and simply plays at full zoom, so the
/// jump lands on the segment's own outer edge. Every assertion below is therefore phrased in
/// terms of the authored edge times rather than in terms of whatever window the path has left
/// over — see the round-4 handoff entry in the archive for why that distinction keeps mattering
/// in this file.
/// </para>
/// </summary>
[TestClass]
public sealed class ZoomCameraMovementTests
{
    private const int SourceWidth = 1920;
    private const int SourceHeight = 1080;
    private const double TickFrequency = 10_000_000.0;

    private const float TargetZoom = 2f;
    private const float SecondZoom = 3f;

    /// <summary>
    /// A shot with the same shape the editor authors: a 1s ramp, a 1s hold and a 1s release.
    /// </summary>
    private static ZoomShot Shot(
        double rampStart,
        float zoom = TargetZoom,
        float centerX = 400f,
        float centerY = 300f,
        bool animateIn = true,
        bool animateOut = true,
        double ramp = 1.0,
        double hold = 1.0,
        double release = 1.0)
        => new(
            rampStart,
            rampStart + ramp,
            rampStart + ramp + hold,
            rampStart + ramp + hold + release,
            zoom,
            centerX,
            centerY,
            Seed: (int)Math.Round(rampStart * 1000.0),
            HasFixedCenter: true,
            AnimateIn: animateIn,
            AnimateOut: animateOut);

    private static ZoomCameraSample Sample(ZoomCameraPath path, double timeSeconds)
    {
        Assert.IsTrue(path.TryEvaluate(timeSeconds, out var sample),
            $"Expected the path to own the camera at t={timeSeconds:F3}.");
        return sample;
    }

    private static ZoomKeyframe Keyframe(
        double startSeconds,
        double endSeconds,
        bool animateIn = true,
        bool animateOut = true,
        double zoom = 2.0,
        double centerX = 0.5,
        double centerY = 0.5)
        => ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds),
            zoom, centerX, centerY) with
        {
            AnimateIn = animateIn,
            AnimateOut = animateOut,
        };

    #region Defaults and persistence

    /// <summary>
    /// Every projected saved before these toggles existed omits both fields, and must keep
    /// animating exactly as it did. A plain <c>bool</c> with a <c>true</c> initializer is what
    /// buys that — deliberately NOT the nullable-with-fallback shape used by
    /// <see cref="ZoomKeyframe.HasAuthoredCenter"/>, whose fallback reads another flag and so
    /// has to be re-applied by every writer of that flag.
    /// </summary>
    [TestMethod]
    public void AKeyframeFromAProjectWithoutTheseFields_StillAnimatesBothEnds()
    {
        var fresh = new ZoomKeyframe();
        Assert.IsTrue(fresh.AnimateIn, "A new keyframe must animate in by default.");
        Assert.IsTrue(fresh.AnimateOut, "A new keyframe must animate out by default.");

        var legacy = JsonSerializer.Deserialize<ZoomKeyframe>(
            """{"Timestamp":"00:00:03","ZoomLevel":2.0,"IsManual":true}""");

        Assert.IsNotNull(legacy);
        Assert.IsTrue(legacy!.AnimateIn, "A keyframe deserialized without AnimateIn must animate in.");
        Assert.IsTrue(legacy.AnimateOut, "A keyframe deserialized without AnimateOut must animate out.");
    }

    [TestMethod]
    public void TheTogglesRoundTripThroughJson()
    {
        var original = Keyframe(1.0, 5.0, animateIn: false, animateOut: false);

        var restored = JsonSerializer.Deserialize<ZoomKeyframe>(JsonSerializer.Serialize(original));

        Assert.IsNotNull(restored);
        Assert.IsFalse(restored!.AnimateIn);
        Assert.IsFalse(restored.AnimateOut);
    }

    #endregion

    #region A lone segment

    [TestMethod]
    public void AnimateInOn_StillEasesUpFrom1x()
    {
        var path = ZoomCameraPath.Build([Shot(2.0)]);

        Assert.AreEqual(1f, Sample(path, 2.0).Zoom, 0.01f, "An animated ramp starts at 1x.");
        float mid = Sample(path, 2.5).Zoom;
        Assert.IsTrue(mid is > 1.05f and < TargetZoom - 0.05f,
            $"An animated ramp should be mid-travel halfway through it, saw {mid:F3}.");
    }

    /// <summary>
    /// The whole leading span plays at the target zoom, so the jump from 1x falls on the
    /// segment's left edge — the instant the block starts on the timeline.
    /// </summary>
    [TestMethod]
    public void AnimateInOff_HoldsFullZoomAcrossTheWholeLeadingSpan()
    {
        var path = ZoomCameraPath.Build([Shot(2.0, animateIn: false)]);

        foreach (double t in new[] { 2.0, 2.25, 2.5, 2.75, 2.999 })
        {
            Assert.AreEqual(TargetZoom, Sample(path, t).Zoom, 0.001f,
                $"A cut-in segment must already be at full zoom at t={t:F3}.");
        }
    }

    /// <summary>
    /// Turning the ease off must not shorten the segment: the path owns exactly the same span
    /// either way. That is what lets the toggle be undone by flipping it back, and what keeps
    /// the block from moving under the user on the timeline.
    /// </summary>
    [TestMethod]
    public void TurningOffEitherEase_LeavesTheSegmentSpanUnchanged()
    {
        var animated = ZoomCameraPath.Build([Shot(2.0)]);
        var cut = ZoomCameraPath.Build([Shot(2.0, animateIn: false, animateOut: false)]);

        foreach (var path in new[] { animated, cut })
        {
            Assert.IsFalse(path.TryEvaluate(1.99, out _), "Neither variant may own the camera before its left edge.");
            Assert.IsTrue(path.TryEvaluate(2.0, out _), "Both variants must own the camera at their left edge.");
            Assert.IsTrue(path.TryEvaluate(5.0, out _), "Both variants must own the camera at their right edge.");
            Assert.IsFalse(path.TryEvaluate(5.01, out _), "Neither variant may own the camera past its right edge.");
        }
    }

    [TestMethod]
    public void AnimateOutOn_StillEasesBackTo1x()
    {
        var path = ZoomCameraPath.Build([Shot(2.0)]);

        float mid = Sample(path, 4.5).Zoom;
        Assert.IsTrue(mid is > 1.05f and < TargetZoom - 0.05f,
            $"An animated release should be mid-travel halfway through it, saw {mid:F3}.");
        Assert.AreEqual(1f, Sample(path, 5.0).Zoom, 0.01f, "An animated release lands on 1x.");
    }

    /// <summary>
    /// The zoom is held right through the trailing span and the cut back to full frame happens
    /// at the segment's right edge, where the path stops owning the camera at all.
    /// </summary>
    [TestMethod]
    public void AnimateOutOff_HoldsFullZoomToTheRightEdgeThenCuts()
    {
        var path = ZoomCameraPath.Build([Shot(2.0, animateOut: false)]);

        foreach (double t in new[] { 4.0, 4.25, 4.5, 4.75, 5.0 })
        {
            Assert.AreEqual(TargetZoom, Sample(path, t).Zoom, 0.001f,
                $"A cut-out segment must still be at full zoom at t={t:F3}.");
        }

        Assert.IsFalse(path.TryEvaluate(5.01, out _),
            "Past its right edge the path must yield the camera, which is what makes the cut back to 1x.");
    }

    [TestMethod]
    public void TheTwoEndsAreIndependent()
    {
        var cutInOnly = ZoomCameraPath.Build([Shot(2.0, animateIn: false)]);
        Assert.AreEqual(TargetZoom, Sample(cutInOnly, 2.0).Zoom, 0.001f);
        Assert.AreEqual(1f, Sample(cutInOnly, 5.0).Zoom, 0.01f,
            "Turning off Animate In must leave the release animating.");

        var cutOutOnly = ZoomCameraPath.Build([Shot(2.0, animateOut: false)]);
        Assert.AreEqual(1f, Sample(cutOutOnly, 2.0).Zoom, 0.01f,
            "Turning off Animate Out must leave the ramp animating.");
        Assert.AreEqual(TargetZoom, Sample(cutOutOnly, 5.0).Zoom, 0.001f);
    }

    #endregion

    #region Linked segments

    // Two shots 0.5s apart, which is inside LinkGapSeconds, so they chain rather than
    // returning to 1x in between. B's leading edge — and therefore the handoff window — is
    // at t=5.5, and B settles at 6.5.
    private static ZoomShot LinkedA(bool animateOut = true)
        => Shot(2.0, TargetZoom, 400f, 300f, animateOut: animateOut);

    private static ZoomShot LinkedB(bool animateIn = true)
        => Shot(5.5, SecondZoom, 1400f, 800f, animateIn: animateIn);

    [TestMethod]
    public void LinkedSegments_InterpolateByDefault()
    {
        var path = ZoomCameraPath.Build([LinkedA(), LinkedB()]);
        Assert.IsTrue(path.IsLinkedAfter(0), "The fixture must actually produce a linked pair.");

        float mid = Sample(path, 5.25).Zoom;
        Assert.IsTrue(mid is > TargetZoom + 0.05f and < SecondZoom - 0.05f,
            $"Halfway through the handoff the zoom should be travelling between the two levels, saw {mid:F3}.");
    }

    /// <summary>
    /// The case that motivated the feature: with two segments close together the incoming
    /// segment has no ramp of its own to suppress — its ease has been replaced by a handoff
    /// from the previous shot — so unless Animate In also governs that handoff, the toggle
    /// silently does nothing for exactly the segments where the anticipatory move is most
    /// visible.
    /// </summary>
    [TestMethod]
    public void AnimateInOff_CutsIntoALinkedSegmentInsteadOfInterpolating()
    {
        var path = ZoomCameraPath.Build([LinkedA(), LinkedB(animateIn: false)]);
        Assert.IsTrue(path.IsLinkedAfter(0),
            "Linkage must survive: unlinking would give both shots overlapping pieces.");

        var before = Sample(path, 5.49);
        Assert.AreEqual(TargetZoom, before.Zoom, 0.001f,
            "The outgoing segment holds its own level right up to the incoming one's leading edge.");
        Assert.AreEqual(400f, before.CenterX, 0.5f);

        foreach (double t in new[] { 5.5, 5.75, 6.0, 6.25, 6.5 })
        {
            var after = Sample(path, t);
            Assert.AreEqual(SecondZoom, after.Zoom, 0.001f,
                $"After the cut the camera is fully on the incoming segment at t={t:F3}.");
            Assert.AreEqual(1400f, after.CenterX, 0.5f,
                $"The focal point cuts across with the zoom at t={t:F3}, it does not travel.");
        }
    }

    /// <summary>
    /// Animate Out is scoped to the return to full frame. A segment that hands off never makes
    /// that return, so there is nothing for the flag to suppress and the move into the next
    /// segment stays owned by that segment's Animate In. Each join is therefore decided by
    /// exactly one flag and two adjacent segments can never disagree about it.
    /// </summary>
    [TestMethod]
    public void AnimateOutOff_DoesNotDisturbAHandoffIntoTheNextSegment()
    {
        var path = ZoomCameraPath.Build([LinkedA(animateOut: false), LinkedB()]);

        float mid = Sample(path, 5.25).Zoom;
        Assert.IsTrue(mid is > TargetZoom + 0.05f and < SecondZoom - 0.05f,
            $"The handoff must still interpolate, saw {mid:F3}.");
    }

    /// <summary>
    /// The cut fires on the incoming segment's LEADING EDGE, not wherever the repair step
    /// happened to open the handoff window. With a real gap between two linked segments that
    /// window opens as soon as the outgoing one stops holding — 1.5s early here — and a jump
    /// there would have nothing on the timeline to explain it.
    /// </summary>
    [TestMethod]
    public void TheCutLandsOnTheIncomingSegmentsLeadingEdge()
    {
        // A holds 3.0-4.0 and would release by 5.0; B's block starts at 5.5. The handoff
        // window therefore opens at 4.0, well before B's edge.
        var path = ZoomCameraPath.Build([LinkedA(), LinkedB(animateIn: false)]);

        foreach (double t in new[] { 4.01, 4.5, 5.0, 5.49 })
        {
            Assert.AreEqual(TargetZoom, Sample(path, t).Zoom, 0.001f,
                $"Before the incoming segment's edge the camera holds the outgoing one at t={t:F3}, " +
                "rather than cutting early or releasing toward 1x.");
        }

        Assert.AreEqual(SecondZoom, Sample(path, 5.5).Zoom, 0.001f,
            "The jump belongs exactly on the incoming segment's leading edge.");
    }

    /// <summary>
    /// A cut-in still has to be a cut and nothing more — the camera must not pump out toward
    /// 1x on its way, which is the artifact the chained path exists to remove.
    /// </summary>
    [TestMethod]
    public void ACutIntoALinkedSegment_NeverDipsTowardFullFrame()
    {
        var path = ZoomCameraPath.Build([LinkedA(), LinkedB(animateIn: false)]);

        // From where the outgoing shot settles to where the incoming one stops holding, i.e.
        // the whole chain minus each end's own ramp/release.
        for (double t = 3.0; t <= 7.5; t += 0.02)
        {
            float zoom = Sample(path, t).Zoom;
            Assert.IsTrue(zoom >= TargetZoom - 0.001f,
                $"The chain dipped to {zoom:F3} at t={t:F2}; a cut must not release toward 1x.");
        }
    }

    #endregion

    #region Shared predicates

    [TestMethod]
    public void Interpolates_IsLinkageAndTheIncomingSegmentsAnimateIn()
    {
        var a = Keyframe(0.0, 3.0);
        var near = Keyframe(3.4, 6.4);
        var far = Keyframe(5.0, 8.0);

        Assert.IsTrue(ZoomCameraPath.AreLinked(a, near), "Fixture check: these must be linked.");
        Assert.IsTrue(ZoomCameraPath.Interpolates(a, near));

        var cutIn = near with { AnimateIn = false };
        Assert.IsTrue(ZoomCameraPath.AreLinked(a, cutIn),
            "Linkage is purely temporal and must not change — hold repair depends on it.");
        Assert.IsFalse(ZoomCameraPath.Interpolates(a, cutIn),
            "The indicator predicate must not promise a move the renderer replaces with a cut.");

        Assert.IsFalse(ZoomCameraPath.AreLinked(a, far));
        Assert.IsFalse(ZoomCameraPath.Interpolates(a, far));
    }

    [TestMethod]
    public void HasLinkedFollower_OnlyLooksAtTheImmediateFollowerOnTheSameChain()
    {
        var first = Keyframe(0.0, 3.0);
        var near = Keyframe(3.4, 6.4);
        var far = Keyframe(9.0, 12.0);

        Assert.IsTrue(ZoomCameraPath.HasLinkedFollower(first, [first, near, far]));
        Assert.IsFalse(ZoomCameraPath.HasLinkedFollower(near, [first, near, far]),
            "The next segment is well beyond the link gap.");
        Assert.IsFalse(ZoomCameraPath.HasLinkedFollower(far, [first, near, far]),
            "The last segment has no follower at all.");
        Assert.IsFalse(ZoomCameraPath.HasLinkedFollower(first, [first]),
            "A lone segment always returns to full frame.");
    }

    [TestMethod]
    public void HasLinkedFollower_IgnoresSegmentsOnAnotherClip()
    {
        var primary = Keyframe(0.0, 3.0);
        var appended = Keyframe(3.4, 6.4) with { SourceVideoFilePath = @"C:\clips\appended.mp4" };

        Assert.IsFalse(ZoomCameraPath.HasLinkedFollower(primary, [primary, appended]),
            "A keyframe on a different source file is on a different camera chain.");
    }

    #endregion

    #region Through the engine

    /// <summary>
    /// Exercised end to end rather than only against <see cref="ZoomCameraPath"/> directly:
    /// the archive records a round where the path machinery was entirely correct and entirely
    /// bypassed by the engine that feeds it, and every path-level test still passed.
    /// </summary>
    [TestMethod]
    public void TheEngineCarriesTheTogglesOntoItsShots()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(
            TestMouseRecordingBuilder.WithClicks(20.0, [], TickFrequency),
            SourceWidth, SourceHeight, TickFrequency);

        var keyframe = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(4.0),
            ZoomLevel = 2.0,
            CenterX = 0.5,
            CenterY = 0.5,
            PreDuration = TimeSpan.FromSeconds(1.0),
            HoldDuration = TimeSpan.FromSeconds(1.0),
            PostDuration = TimeSpan.FromSeconds(1.0),
            IsManual = true,
            HasAuthoredCenter = true,
        };

        engine.SetManualKeyframes([keyframe]);
        Assert.AreEqual(1f, engine.GetZoomState(3.0).ZoomLevel, 0.01f,
            "Baseline: an animated segment starts its ramp at 1x.");
        Assert.AreEqual(1f, engine.GetZoomState(6.0).ZoomLevel, 0.01f,
            "Baseline: an animated segment ends its release at 1x.");

        engine.SetManualKeyframes([keyframe with { AnimateIn = false, AnimateOut = false }]);
        Assert.AreEqual(2f, engine.GetZoomState(3.0).ZoomLevel, 0.01f,
            "A cut-in segment must be at full zoom from its left edge, through the engine too.");
        Assert.AreEqual(2f, engine.GetZoomState(6.0).ZoomLevel, 0.01f,
            "A cut-out segment must still be at full zoom on its right edge.");
        Assert.AreEqual(1f, engine.GetZoomState(6.01).ZoomLevel, 0.01f,
            "...and back to full frame immediately after it.");
    }

    #endregion

    #region Editing

    [TestMethod]
    public void TheOperationSetsAndUndoesEachToggleIndependently()
    {
        var model = new TimelineModel();
        var keyframe = Keyframe(1.0, 5.0);
        model.ZoomKeyframes.Add(keyframe);

        var turnOffIn = new UpdateZoomSegmentPropertiesOperation(keyframe.Id, animateIn: false);
        turnOffIn.Execute(model);

        Assert.IsFalse(model.ZoomKeyframes[0].AnimateIn);
        Assert.IsTrue(model.ZoomKeyframes[0].AnimateOut, "Editing one end must not touch the other.");

        var turnOffOut = new UpdateZoomSegmentPropertiesOperation(keyframe.Id, animateOut: false);
        turnOffOut.Execute(model);

        Assert.IsFalse(model.ZoomKeyframes[0].AnimateIn);
        Assert.IsFalse(model.ZoomKeyframes[0].AnimateOut);

        turnOffOut.Undo(model);
        Assert.IsFalse(model.ZoomKeyframes[0].AnimateIn, "Undo must not resurrect the earlier edit.");
        Assert.IsTrue(model.ZoomKeyframes[0].AnimateOut);

        turnOffIn.Undo(model);
        Assert.IsTrue(model.ZoomKeyframes[0].AnimateIn);
        Assert.IsTrue(model.ZoomKeyframes[0].AnimateOut);
    }

    /// <summary>
    /// The operation is shared with the zoom-level, framing and drift edits, so an edit to any
    /// of those must carry the movement toggles across untouched.
    /// </summary>
    [TestMethod]
    public void AnUnrelatedPropertyEdit_PreservesTheToggles()
    {
        var model = new TimelineModel();
        model.ZoomKeyframes.Add(Keyframe(1.0, 5.0, animateIn: false, animateOut: false));

        new UpdateZoomSegmentPropertiesOperation(model.ZoomKeyframes[0].Id, zoomLevel: 3.0).Execute(model);

        Assert.AreEqual(3.0, model.ZoomKeyframes[0].ZoomLevel, 0.001);
        Assert.IsFalse(model.ZoomKeyframes[0].AnimateIn, "A zoom-level edit must not re-enable the ramp.");
        Assert.IsFalse(model.ZoomKeyframes[0].AnimateOut, "A zoom-level edit must not re-enable the release.");
    }

    #endregion
}
