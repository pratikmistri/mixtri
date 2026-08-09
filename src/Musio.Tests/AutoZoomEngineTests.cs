namespace Musio.Tests;

using Musio.Core.Processing;
using Musio.Core.Models;
using Musio.Core.Timeline;
using Musio.Tests.TestSupport;

[TestClass]
public sealed class AutoZoomEngineTests
{
    private const double TickFrequency = 10_000_000.0;

    private static MouseRecordingData BuildRecordingWithClicks(
        double durationSeconds,
        List<(double timeSeconds, int x, int y)> clicks)
        => TestMouseRecordingBuilder.WithClicks(durationSeconds, clicks, TickFrequency);

    private static void AssertSmoothCenterVelocity(
        IReadOnlyList<float> centers,
        double sampleStepSeconds,
        double maxVelocityDelta,
        string axis)
    {
        var velocities = new List<double>();
        for (int i = 1; i < centers.Count; i++)
            velocities.Add((centers[i] - centers[i - 1]) / sampleStepSeconds);

        double observed = 0;
        for (int i = 1; i < velocities.Count; i++)
            observed = Math.Max(observed, Math.Abs(velocities[i] - velocities[i - 1]));

        Assert.IsTrue(observed < maxVelocityDelta,
            $"Center {axis} velocity changed by {observed:F1}px/s between adjacent samples; " +
            "that indicates a snap rather than a smooth eased handoff.");
    }

    [TestMethod]
    public void GetZoomState_NoClicks_ReturnsNoZoom()
    {
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var state = engine.GetZoomState(2.5);

        Assert.AreEqual(1.0f, state.ZoomLevel, 0.001f, "Zoom should be 1.0 with no clicks");
        Assert.AreEqual(1920f, state.ViewportWidth, 0.01f, "Viewport width should equal source width at 1x zoom");
        Assert.AreEqual(1080f, state.ViewportHeight, 0.01f, "Viewport height should equal source height at 1x zoom");
    }

    [TestMethod]
    public void GetZoomState_AtClick_ReturnsZoomedIn()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // At click time (end of zoom-in phase), should be fully or nearly fully zoomed
        var state = engine.GetZoomState(2.0);

        Assert.IsTrue(state.ZoomLevel > 1.5f,
            $"Expected zoom > 1.5 at click time, got {state.ZoomLevel}");

        // During hold phase (just after click), should be at target zoom
        var holdState = engine.GetZoomState(2.1);
        Assert.AreEqual(2.0f, holdState.ZoomLevel, 0.01f,
            $"Expected zoom = 2.0 during hold, got {holdState.ZoomLevel}");
    }

    [TestMethod]
    public void GetZoomState_AfterEaseOut_ReturnsNoZoom()
    {
        var config = new AutoZoomConfig
        {
            DefaultZoomLevel = 2.0f,
            PreClickDuration = 0.3f,
            HoldDuration = 0.5f,
            EaseOutDuration = 0.5f,
        };
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, [(1.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // click=1.0 + hold=0.5 + easeOut=0.5 → segment ends at 2.0
        // Well after the segment, zoom should be 1.0
        var state = engine.GetZoomState(3.0);

        Assert.AreEqual(1.0f, state.ZoomLevel, 0.01f,
            $"Expected zoom ≈ 1.0 after ease-out, got {state.ZoomLevel}");
    }

    [TestMethod]
    public void GetZoomState_ViewportClamped_StaysInBounds()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 3.0f };
        var engine = new AutoZoomEngine(config);
        // Click at top-left corner
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 0, 0)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var state = engine.GetZoomState(2.1); // during hold

        Assert.IsTrue(state.ViewportX >= 0,
            $"ViewportX should be >= 0, got {state.ViewportX}");
        Assert.IsTrue(state.ViewportY >= 0,
            $"ViewportY should be >= 0, got {state.ViewportY}");
        Assert.IsTrue(state.ViewportX + state.ViewportWidth <= 1920 + 0.01f,
            $"Viewport right edge ({state.ViewportX + state.ViewportWidth}) should be <= 1920");
        Assert.IsTrue(state.ViewportY + state.ViewportHeight <= 1080 + 0.01f,
            $"Viewport bottom edge ({state.ViewportY + state.ViewportHeight}) should be <= 1080");

        // Also test bottom-right corner click
        var recording2 = BuildRecordingWithClicks(5.0, [(2.0, 1920, 1080)]);
        engine.BuildZoomTimeline(recording2, 1920, 1080, TickFrequency);

        var state2 = engine.GetZoomState(2.1);

        Assert.IsTrue(state2.ViewportX >= 0,
            $"ViewportX should be >= 0 for corner click, got {state2.ViewportX}");
        Assert.IsTrue(state2.ViewportY >= 0,
            $"ViewportY should be >= 0 for corner click, got {state2.ViewportY}");
        Assert.IsTrue(state2.ViewportX + state2.ViewportWidth <= 1920 + 0.01f,
            $"Viewport right edge should be <= 1920 for corner click");
    }

    [TestMethod]
    public void GetZoomState_ManualKeyframe_OverridesAuto()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        // Auto click at t=2.0 with zoom=2.0
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Manual keyframe at same time with higher zoom=3.5
        engine.AddManualKeyframe(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2.0),
            ZoomLevel = 3.5,
            CenterX = 0.5,
            CenterY = 0.5,
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
            // The fixture's whole premise is that this is a manual, user-framed keyframe;
            // it just never said so. IsManual now only governs ownership, and the framing
            // question it also used to answer lives on HasAuthoredCenter, so a keyframe that
            // means to hold its own centre has to declare it.
            IsManual = true,
            HasAuthoredCenter = true,
        });

        // During hold phase of the manual keyframe
        var state = engine.GetZoomState(2.0);

        Assert.AreEqual(3.5f, state.ZoomLevel, 0.01f,
            $"Manual keyframe zoom (3.5) should override auto (2.0), got {state.ZoomLevel}");
        Assert.IsTrue(state.IsManualOverride, "Manual override state must be reported for compositor/export parity.");
    }

    /// <summary>
    /// A manual segment overlapping an auto (click-driven) zoom must hand off to it, not cut.
    /// <para>
    /// Regression, reported from the editor as "a flash to no zoom then zoom in again".
    /// Manual and auto used to be two independent camera paths resolved by hard precedence, so
    /// when a manual segment overlapped an auto one the manual path took over at its own ramp
    /// start — which begins at 1× — and the camera snapped from the auto zoom out to full frame
    /// and immediately back in. Two independent paths can never hand off to each other; they
    /// are now one chained path.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetZoomState_ManualOverlappingAuto_HandsOffWithoutFlashingToFullFrame()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        // Auto click at t=2.0 produces a segment spanning roughly [1.0 .. 4.9].
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(12.0, [(2.0, 400, 300)]), 1920, 1080, TickFrequency);

        // A manual segment dragged out so it overlaps that auto segment's tail.
        var manual = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), 3.0, 0.8, 0.7);
        engine.SetManualKeyframes([manual]);

        // Sweep the whole overlap. Once the auto zoom has settled, the camera must never
        // fall back toward full frame on its way into the manual segment.
        float minZoom = float.MaxValue;
        double minAt = 0;
        for (double t = 2.0; t <= manual.Timestamp.TotalSeconds; t += 0.002)
        {
            float zoom = engine.GetZoomState(t).ZoomLevel;
            if (zoom < minZoom)
            {
                minZoom = zoom;
                minAt = t;
            }
        }

        Assert.IsTrue(minZoom > 1.5f,
            $"Camera dropped to {minZoom:F3}x at t={minAt:F3}s while moving from the auto zoom " +
            "into the overlapping manual segment. Anything near 1x is the flash-to-full-frame cut.");
    }

    /// <summary>
    /// The focal point must stay continuous across a manual/auto handoff even though the two
    /// kinds of shot take their centre from different sources (live cursor versus the
    /// keyframe's stored centre). <see cref="ZoomState.CursorFollowWeight"/> eases between
    /// them; a hard switch would snap the camera at the join.
    /// </summary>
    [TestMethod]
    public void GetZoomState_ManualOverlappingAuto_EasesCursorFollowWeightAcrossTheHandoff()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(12.0, [(2.0, 400, 300)]), 1920, 1080, TickFrequency);
        var manual = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), 3.0, 0.8, 0.7) with
        {
            // This test is about a MIXED handoff, which needs one shot that holds its own
            // centre. Creating a segment no longer does that on its own — only editing its
            // region does — so the fixture states that explicitly.
            HasAuthoredCenter = true,
        };
        engine.SetManualKeyframes([manual]);

        Assert.AreEqual(1f, engine.GetZoomState(2.5).CursorFollowWeight, 0.001f,
            "A settled auto zoom should follow the cursor exactly as before.");
        Assert.AreEqual(0f, engine.GetZoomState(manual.Timestamp.TotalSeconds + 0.2).CursorFollowWeight, 0.001f,
            "A settled manual segment should use its own centre.");

        var weights = new List<float>();
        for (double t = 2.0; t <= manual.Timestamp.TotalSeconds + 0.2; t += 0.002)
            weights.Add(engine.GetZoomState(t).CursorFollowWeight);

        double maxStep = 0;
        for (int i = 1; i < weights.Count; i++)
            maxStep = Math.Max(maxStep, Math.Abs(weights[i] - weights[i - 1]));

        Assert.IsTrue(maxStep < 0.05,
            $"CursorFollowWeight jumped by {maxStep:F3} between adjacent 2ms samples; " +
            "it must ease across the handoff rather than switching, or the focal point snaps.");
    }

    /// <summary>
    /// Promoting one auto keyframe to manual (which is what editing a segment's length does)
    /// must not delete its neighbours.
    /// <para>
    /// Regression from Sample9.musio: "it gets weird when I start editing the segment lengths."
    /// Auto segments span ~3.9s while clicks are often ~1s apart, so consecutive auto shots
    /// overlap heavily as a matter of course. The engine suppressed any auto shot whose hold
    /// OVERLAPPED a manual shot's hold, so promoting the middle segment silently removed the
    /// zooms on both sides of it. Suppression is now a same-moment test — it only replaces the
    /// click the manual keyframe actually came from.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetZoomState_EditingOneOverlappingAutoSegment_KeepsItsNeighbours()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        // Five clicks about 1.1s apart, as in Sample9 — every auto segment overlaps its neighbours.
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(12.0,
            [
                (0.5, 120, 200), (1.7, 2030, 230), (2.8, 80, 1200), (4.0, 2070, 1180), (5.2, 1050, 650),
            ]),
            2304, 1536, TickFrequency);

        // Editing the middle segment's length promotes it to manual at the same moment.
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.8),
                ZoomLevel = 2.0,
                CenterX = 80.0 / 2304,
                CenterY = 1200.0 / 1536,
                IsManual = true,
            },
        ]);

        // The neighbouring clicks must still zoom. Sample each one's settled moment.
        foreach (double clickTime in new[] { 0.5, 1.7, 4.0, 5.2 })
        {
            float zoom = engine.GetZoomState(clickTime).ZoomLevel;
            Assert.IsTrue(zoom > 1.5f,
                $"The zoom at the click at {clickTime:F1}s collapsed to {zoom:F3}x after a " +
                "neighbouring segment was edited — editing one segment must not delete the others.");
        }
    }

    /// <summary>
    /// Resizing or moving a click-driven zoom must not silently repoint the camera.
    /// <para>
    /// Regression: <c>IsManual</c> used to mean both "the user owns this" and "hold this
    /// centre", so dragging a segment's edge promoted it and thereby switched it from
    /// following the cursor to being pinned on the click point — a framing change nobody asked
    /// for, and part of "it gets weird when I start editing the segment lengths".
    /// <see cref="ZoomKeyframe.HasAuthoredCenter"/> now carries the framing question on its own.
    /// </para>
    /// </summary>
    [TestMethod]
    public void ResizingAnAutoKeyframe_KeepsItFollowingTheCursor()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(12) };
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Id = "kf1",
            Timestamp = TimeSpan.FromSeconds(3),
            ZoomLevel = 2.0,
            CenterX = 0.25,
            CenterY = 0.75,
            IsManual = false,
        });

        new ResizeZoomSegmentOperation("kf1", resizeStart: false, TimeSpan.FromSeconds(7))
            .Execute(model);

        var resized = model.ZoomKeyframes[0];
        Assert.IsTrue(resized.IsManual, "Resizing should still hand ownership to the user.");
        Assert.IsFalse(resized.UsesAuthoredCenter,
            "Resizing must not pin the framing — the segment should still follow the cursor.");

        new MoveZoomKeyframeOperation("kf1", TimeSpan.FromSeconds(5)).Execute(model);
        Assert.IsFalse(model.ZoomKeyframes[0].UsesAuthoredCenter,
            "Moving must not pin the framing either.");
    }

    /// <summary>
    /// Explicitly editing a segment's region is the gesture that DOES pin its framing —
    /// the other half of the split guarded by the test above. Changing only the zoom level
    /// must leave the framing question alone.
    /// </summary>
    [TestMethod]
    public void EditingTheRegion_PinsTheFraming_ButChangingOnlyZoomDoesNot()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(12) };
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Id = "kf1",
            Timestamp = TimeSpan.FromSeconds(3),
            ZoomLevel = 2.0,
            IsManual = false,
        });

        new UpdateZoomSegmentPropertiesOperation("kf1", zoomLevel: 3.0).Execute(model);
        Assert.IsFalse(model.ZoomKeyframes[0].UsesAuthoredCenter,
            "Changing the zoom level alone should not repoint the camera.");

        new UpdateZoomSegmentPropertiesOperation("kf1", centerX: 0.8, centerY: 0.2).Execute(model);
        Assert.IsTrue(model.ZoomKeyframes[0].UsesAuthoredCenter,
            "Authoring a region is exactly the gesture that should hold that framing.");
    }

    /// <summary>
    /// Projects saved before <see cref="ZoomKeyframe.HasAuthoredCenter"/> existed carry no
    /// value for it, and must keep rendering exactly as they did: a manual keyframe held its
    /// centre, an auto one followed the cursor.
    /// </summary>
    [TestMethod]
    public void LegacyKeyframesWithoutTheFlag_KeepTheirOriginalFramingBehaviour()
    {
        var legacyManual = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1), IsManual = true };
        var legacyAuto = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1), IsManual = false };

        Assert.IsNull(legacyManual.HasAuthoredCenter);
        Assert.IsTrue(legacyManual.UsesAuthoredCenter,
            "A manual keyframe from an older project must keep holding its authored centre.");
        Assert.IsFalse(legacyAuto.UsesAuthoredCenter,
            "An auto keyframe must keep following the cursor.");
    }

    /// <summary>
    /// Creating a segment says WHEN to zoom, not WHERE to look, so it must not pin the framing —
    /// a freshly created segment keeps following the cursor until its region is edited.
    /// </summary>
    [TestMethod]
    public void CreatingASegment_DoesNotPinItsFraming()
    {
        var created = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), 2.0, 0.7, 0.3);

        Assert.IsTrue(created.IsManual, "A created segment is user-owned and editable.");
        Assert.IsFalse(created.UsesAuthoredCenter,
            "Creating a segment should leave it following the cursor; only editing its region pins it.");

        // The seeded centre is still carried, so it is available as the fallback for a
        // cursorless clip and as the starting point if the region is later authored.
        Assert.AreEqual(0.7, created.CenterX, 1e-9);
        Assert.AreEqual(0.3, created.CenterY, 1e-9);

        // ...and editing the region is what pins it.
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(12) };
        model.ZoomKeyframes.Add(created);
        new UpdateZoomSegmentPropertiesOperation(created.Id, centerX: 0.2, centerY: 0.9).Execute(model);
        Assert.IsTrue(model.ZoomKeyframes[0].UsesAuthoredCenter);
    }

    /// <summary>
    /// The region picker's "Follow mouse" button hands the framing back to the cursor, keeps
    /// the zoom level, and is undoable.
    /// </summary>
    [TestMethod]
    public void FollowMouse_UnpinsTheFraming_KeepsZoom_AndUndoesCleanly()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(12) };
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Id = "kf1",
            Timestamp = TimeSpan.FromSeconds(3),
            ZoomLevel = 2.0,
            CenterX = 0.8,
            CenterY = 0.2,
            IsManual = true,
            HasAuthoredCenter = true,
        });

        var op = new UpdateZoomSegmentPropertiesOperation(
            "kf1", zoomLevel: 2.5, hasAuthoredCenter: false);
        Assert.AreEqual("Follow Mouse", op.Description,
            "The undo entry should say what the user actually did.");

        op.Execute(model);

        var updated = model.ZoomKeyframes[0];
        Assert.IsFalse(updated.UsesAuthoredCenter, "The segment should be following the cursor again.");
        Assert.AreEqual(2.5, updated.ZoomLevel, 1e-9, "The zoom level set in the picker should be kept.");
        Assert.AreEqual(0.8, updated.CenterX, 1e-9,
            "The stored centre is kept as the fallback and as a starting point if re-pinned.");

        op.Undo(model);
        Assert.IsTrue(model.ZoomKeyframes[0].UsesAuthoredCenter, "Undo must restore the pinned framing.");
        Assert.AreEqual(2.0, model.ZoomKeyframes[0].ZoomLevel, 1e-9);
    }

    [TestMethod]
    public void GetZoomState_AutoZoom_DoesNotSetManualOverride()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        engine.BuildZoomTimeline(BuildRecordingWithClicks(5.0, [(2.0, 960, 540)]), 1920, 1080, TickFrequency);

        var state = engine.GetZoomState(2.1);

        Assert.IsTrue(state.HasSegment);
        Assert.IsFalse(state.IsManualOverride, "Auto zooms must not be reported as manual overrides.");
    }

    [TestMethod]
    public void GetZoomState_LinkedManualKeyframes_ZoomVelocityIsContinuous()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(BuildRecordingWithClicks(8.0, []), 1920, 1080, TickFrequency);
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 2.4,
                CenterX = 0.45,
                CenterY = 0.50,
                PreDuration = TimeSpan.FromMilliseconds(500),
                HoldDuration = TimeSpan.FromMilliseconds(700),
                PostDuration = TimeSpan.FromSeconds(1.0),
                IsManual = true,
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.3),
                ZoomLevel = 1.8,
                CenterX = 0.55,
                CenterY = 0.50,
                PreDuration = TimeSpan.FromSeconds(1.0),
                HoldDuration = TimeSpan.FromMilliseconds(600),
                PostDuration = TimeSpan.FromMilliseconds(600),
                IsManual = true,
            },
        ]);

        const double dt = 0.002;
        var zooms = new List<float>();
        for (double t = 2.60; t <= 3.40; t += dt)
            zooms.Add(engine.GetZoomState(t).ZoomLevel);

        var velocities = new List<double>();
        for (int i = 1; i < zooms.Count; i++)
            velocities.Add((zooms[i] - zooms[i - 1]) / dt);

        double maxVelocityDelta = 0;
        for (int i = 1; i < velocities.Count; i++)
            maxVelocityDelta = Math.Max(maxVelocityDelta, Math.Abs(velocities[i] - velocities[i - 1]));

        Assert.IsTrue(maxVelocityDelta < 0.40,
            $"Linked manual zoom velocity changed by {maxVelocityDelta:F3} zoom/s between adjacent 2ms samples. " +
            "This asserts derivative continuity, not just value continuity, so the old max(falling,rising) corner would fail.");
    }

    [TestMethod]
    public void GetZoomState_ContainedManualKeyframe_IsVisitedAsWaypoint()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(BuildRecordingWithClicks(10.0, []), 1920, 1080, TickFrequency);
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 3.0,
                CenterX = 0.35,
                CenterY = 0.35,
                PreDuration = TimeSpan.FromSeconds(1.0),
                HoldDuration = TimeSpan.FromSeconds(5.0),
                PostDuration = TimeSpan.FromSeconds(1.0),
                IsManual = true,
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.0,
                CenterX = 0.65,
                CenterY = 0.65,
                PreDuration = TimeSpan.FromMilliseconds(500),
                HoldDuration = TimeSpan.FromMilliseconds(500),
                PostDuration = TimeSpan.FromMilliseconds(500),
                IsManual = true,
            },
        ]);

        var waypoint = engine.GetZoomState(3.5);

        Assert.AreEqual(2.0f, waypoint.ZoomLevel, 0.02f,
            "A contained lower-zoom keyframe must become a waypoint rather than being swallowed by the higher zoom.");
        Assert.AreEqual(0.65f * 1920, waypoint.CenterX, 2f);
        Assert.AreEqual(0.65f * 1080, waypoint.CenterY, 2f);
    }

    [TestMethod]
    public void GetZoomState_LinkedManualKeyframes_ZoomNeverFallsBelowOne()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(BuildRecordingWithClicks(8.0, []), 1920, 1080, TickFrequency);
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 1.2,
                CenterX = 0.35,
                CenterY = 0.35,
                PreDuration = TimeSpan.FromMilliseconds(500),
                HoldDuration = TimeSpan.FromMilliseconds(500),
                PostDuration = TimeSpan.FromMilliseconds(600),
                IsManual = true,
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.7),
                ZoomLevel = 1.2,
                CenterX = 0.80,
                CenterY = 0.80,
                PreDuration = TimeSpan.FromMilliseconds(500),
                HoldDuration = TimeSpan.FromMilliseconds(500),
                PostDuration = TimeSpan.FromMilliseconds(600),
                IsManual = true,
            },
        ]);

        for (double t = 0.0; t <= 4.0; t += 0.005)
        {
            var state = engine.GetZoomState(t);
            Assert.IsTrue(state.ZoomLevel >= 1.0f,
                $"Zoom fell below 1x at t={t:F3}: {state.ZoomLevel:F4}.");
        }
    }

    [TestMethod]
    public void GetZoomState_LinkedAutoClicks_VisitsEachClickCenter()
    {
        // This is the auto-click equivalent of the manual/path handoff tests: a close
        // pair must become two ordered waypoints. If AutoZoomEngine collapses raw clicks
        // before building ZoomCameraPath, the first click's center is lost.
        var engine = new AutoZoomEngine(new AutoZoomConfig
        {
            DefaultZoomLevel = 2.0f,
            PreClickDuration = 0.5f,
            HoldDuration = 0.6f,
            EaseOutDuration = 0.6f,
        });
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(6.0, [(2.0, 700, 500), (2.6, 1200, 500)]),
            1920,
            1080,
            TickFrequency);

        var firstClick = engine.GetZoomState(2.0);
        // The chained camera may spend a short minimum-duration handoff around the
        // second click, so assert the second waypoint once that handoff has settled.
        var secondClick = engine.GetZoomState(2.85);

        Assert.AreEqual(700f, firstClick.CenterX, 2f,
            "The auto path should visit the first click before handing off to the second.");
        Assert.AreEqual(1200f, secondClick.CenterX, 2f,
            "The auto path should visit the second click after the handoff.");
    }

    [TestMethod]
    public void RemoveManualKeyframe_RemovesCorrectKeyframe()
    {
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(5.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        var ts = TimeSpan.FromSeconds(2.0);
        engine.AddManualKeyframe(new ZoomKeyframe
        {
            Timestamp = ts,
            ZoomLevel = 3.0,
            CenterX = 0.5,
            CenterY = 0.5,
        });

        // Verify keyframe is active
        var before = engine.GetZoomState(2.0);
        Assert.AreEqual(3.0f, before.ZoomLevel, 0.01f);

        // Remove and verify it's gone
        engine.RemoveManualKeyframe(ts);
        var after = engine.GetZoomState(2.0);
        Assert.AreEqual(1.0f, after.ZoomLevel, 0.01f, "Zoom should be 1.0 after removing manual keyframe");
    }

    [TestMethod]
    public void GetZoomState_SuppressedClick_SkipsAutoZoom()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);
        // Click at t=2.0s with source tick = 2.0 * TickFrequency
        long clickTicks = (long)(2.0 * TickFrequency);
        var recording = BuildRecordingWithClicks(5.0, [(2.0, 500, 400)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Before suppression: zoom is active at click time
        var before = engine.GetZoomState(2.1);
        Assert.AreEqual(2.0f, before.ZoomLevel, 0.01f,
            "Expected zoom = 2.0 at click time before suppression");

        // Suppress the click
        engine.SetSuppressedClickTicks([clickTicks]);

        // After suppression: zoom should be 1.0
        var after = engine.GetZoomState(2.1);
        Assert.AreEqual(1.0f, after.ZoomLevel, 0.01f,
            "Zoom should be 1.0 after suppressing the auto-zoom click");
    }

    [TestMethod]
    public void GetZoomState_SuppressOneOfTwoClicks_OnlyOneZooms()
    {
        var config = new AutoZoomConfig
        {
            DefaultZoomLevel = 2.0f,
            MinTimeBetweenZooms = 0.5f,
        };
        var engine = new AutoZoomEngine(config);
        // Two clicks far apart: t=1.0 and t=4.0
        long click1Ticks = (long)(1.0 * TickFrequency);
        var recording = BuildRecordingWithClicks(6.0, [(1.0, 200, 200), (4.0, 800, 600)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Both clicks produce zoom before suppression
        Assert.AreEqual(2.0f, engine.GetZoomState(1.1).ZoomLevel, 0.01f);
        Assert.AreEqual(2.0f, engine.GetZoomState(4.1).ZoomLevel, 0.01f);

        // Suppress only the first click
        engine.SetSuppressedClickTicks([click1Ticks]);

        // First click: no zoom; second click: still zoomed
        Assert.AreEqual(1.0f, engine.GetZoomState(1.1).ZoomLevel, 0.01f,
            "Suppressed click should not produce zoom");
        Assert.AreEqual(2.0f, engine.GetZoomState(4.1).ZoomLevel, 0.01f,
            "Non-suppressed click should still zoom");
    }

    [TestMethod]
    public void RemoveZoomKeyframeOperation_SuppressesAutoClick()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        long clickTicks = 20_000_000; // 2.0s at 10MHz

        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2.0),
            ZoomLevel = 2.0,
            CenterX = 0.5,
            CenterY = 0.5,
            SourceClickTicks = clickTicks,
        });

        var op = new Core.Timeline.RemoveZoomKeyframeOperation(model.ZoomKeyframes[0].Id);
        op.Execute(model);

        Assert.AreEqual(0, model.ZoomKeyframes.Count, "Keyframe should be removed");
        Assert.IsTrue(model.SuppressedClickTicks.Contains(clickTicks),
            "Source click should be suppressed after removing auto keyframe");

        // Undo should restore both the keyframe and remove the suppression
        op.Undo(model);
        Assert.AreEqual(1, model.ZoomKeyframes.Count, "Keyframe should be restored on undo");
        Assert.IsFalse(model.SuppressedClickTicks.Contains(clickTicks),
            "Suppression should be removed on undo");
    }

    [TestMethod]
    public void SpringInterpolate_ConvergesToTarget()
    {
        float current = 1.0f;
        float target = 2.0f;

        // Simulate 100 steps
        for (int i = 0; i < 100; i++)
            current = AutoZoomEngine.SpringInterpolate(current, target, 200f, 20f, 0.016f);

        Assert.AreEqual(target, current, 0.01f,
            $"SpringInterpolate should converge to target, got {current}");
    }

    [TestMethod]
    public void GetZoomState_OverlappingManualKeyframes_NoJump()
    {
        // Regression test: two overlapping manual keyframes should transition
        // seamlessly — the higher zoom wins, preventing a snap/jump.
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(10.0, []);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // Keyframe A at t=2.0, keyframe B at t=3.0
        // With default durations (pre=345ms, hold=575ms, post=575ms):
        //   A: active from 1.655 to 3.15 (zoom-out from 2.575 to 3.15)
        //   B: active from 2.655 to 4.15 (zoom-in from 2.655 to 3.0)
        // Overlap zone: 2.655 to 3.15
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 2.0,
                CenterX = 0.3,
                CenterY = 0.3,
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.0,
                CenterX = 0.7,
                CenterY = 0.7,
            },
        ]);

        // Sample through the overlap zone: zoom should never dip below 1.0
        // and the transition should be monotonically smooth (no sudden jumps)
        float prevZoom = 0;
        bool hadJump = false;
        for (double t = 2.5; t <= 3.5; t += 0.01)
        {
            var state = engine.GetZoomState(t);
            Assert.IsTrue(state.ZoomLevel >= 1.0f,
                $"Zoom at t={t:F2} should be >= 1.0, got {state.ZoomLevel}");

            // A large sudden drop (> 0.3 in 10ms) indicates a visual jump
            if (prevZoom > 0 && prevZoom - state.ZoomLevel > 0.3f)
                hadJump = true;

            prevZoom = state.ZoomLevel;
        }

        Assert.IsFalse(hadJump,
            "Zoom should not have sudden drops during overlapping keyframe transition");
    }

    [TestMethod]
    public void GetZoomState_OverlappingManualKeyframes_CenterDoesNotSnap()
    {
        // Regression: when two overlapping keyframes have different centers,
        // the focal point should glide smoothly from A to B rather than
        // snapping at the moment B's zoom first exceeds A's.
        const int W = 1920;
        const int H = 1080;
        var config = new AutoZoomConfig();
        var engine = new AutoZoomEngine(config);
        var recording = BuildRecordingWithClicks(10.0, []);
        engine.BuildZoomTimeline(recording, W, H, TickFrequency);

        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(2.0),
                ZoomLevel = 2.0,
                CenterX = 0.3,
                CenterY = 0.3,
                PreDuration = TimeSpan.FromMilliseconds(450),
                HoldDuration = TimeSpan.FromMilliseconds(600),
                PostDuration = TimeSpan.FromMilliseconds(700),
            },
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.0,
                CenterX = 0.7,
                CenterY = 0.7,
                PreDuration = TimeSpan.FromMilliseconds(450),
                HoldDuration = TimeSpan.FromMilliseconds(600),
                PostDuration = TimeSpan.FromMilliseconds(700),
            },
        ]);

        // Step through the linked handoff and assert the real invariant: center motion
        // is smooth in velocity (bounded acceleration), not that each 5ms step stays
        // under a number derived from the old weighted-blend behavior. An eased
        // handoff legitimately has a higher peak velocity than its average; the snap
        // bug was the velocity spike from a hard center switch.
        const double step = 0.005;
        var centerXs = new List<float>();
        var centerYs = new List<float>();
        for (double t = 2.0; t <= 3.6; t += step)
        {
            var state = engine.GetZoomState(t);
            centerXs.Add(state.CenterX);
            centerYs.Add(state.CenterY);
        }

        // The limits are calibrated to the eased curve's acceleration, while staying
        // far below the old hard-switch snap (~150,000 px/s velocity delta on X).
        AssertSmoothCenterVelocity(centerXs, step, 5_000.0, "X");
        AssertSmoothCenterVelocity(centerYs, step, 3_000.0, "Y");

        // Endpoint assertions: outside the overlap, each keyframe should own
        // the focal point — verifies the blend doesn't bias the result.
        // At t = 2.0 (kf A's timestamp, hold phase, kf B not yet active),
        // center must equal A's center.
        var stateA = engine.GetZoomState(2.0);
        Assert.AreEqual(0.3f * W, stateA.CenterX, 1f, "Center at A's timestamp should be A's center");
        Assert.AreEqual(0.3f * H, stateA.CenterY, 1f, "Center at A's timestamp should be A's center");

        // At t = 3.0 (kf B's timestamp, hold phase, kf A's post-ease finished
        // at 2.0 + hold(0.575) + post(0.575) = 3.15 — A is still tailing off
        // here, but B's weight at hold-peak dominates by ~3x). Use a looser
        // tolerance reflecting the still-active tail.
        var stateB = engine.GetZoomState(3.0);
        Assert.IsTrue(stateB.CenterX > 0.55f * W,
            $"Center at B's timestamp should be near B's center (0.7*W=1344), got {stateB.CenterX}");

        // After both segments end (B's post-ease finishes at 3.0 + 0.575 + 0.575
        // = 4.15), no manual override → falls through to auto (empty), so
        // zoom returns to 1.0.
        var stateAfter = engine.GetZoomState(4.5);
        Assert.AreEqual(1.0f, stateAfter.ZoomLevel, 0.01f,
            "Zoom should return to 1.0 after all segments end");
    }

    [TestMethod]
    public void GetZoomState_ClickBeforeVideoStart_DoesNotZoom()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        // Click lands 0.2s into the mouse recording, but capture only started 0.5s in, so
        // it maps to -0.3s — before the first video frame.
        var recording = BuildRecordingWithClicks(5.0, [(0.2, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency,
            timeOffsetSeconds: 0.5, durationSeconds: 5.0);

        // The pre-roll click's hold and ease-out still overlap the start of the video, so
        // without the range check the preview zooms while the editor shows no segment on
        // the zoom track — leaving the user no way to select or delete it.
        Assert.AreEqual(1.0f, engine.GetZoomState(0.0).ZoomLevel, 0.001f,
            "A click before the video started must not zoom the first frame");
        Assert.AreEqual(1.0f, engine.GetZoomState(0.5).ZoomLevel, 0.001f,
            "A click before the video started must not zoom during its hold window");
    }

    [TestMethod]
    public void GetZoomState_ClickAfterVideoEnd_DoesNotZoom()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        // Click 0.4s past the end of a 5s video; its anticipatory zoom-in would otherwise
        // begin at 4.4s and zoom the tail of the video.
        var recording = BuildRecordingWithClicks(5.0, [(5.4, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency, durationSeconds: 5.0);

        Assert.AreEqual(1.0f, engine.GetZoomState(4.9).ZoomLevel, 0.001f,
            "A click past the end of the video must not zoom its final frames");
    }

    [TestMethod]
    public void GetZoomState_ClickInsideVideo_StillZoomsWhenDurationKnown()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var recording = BuildRecordingWithClicks(5.0, [(2.5, 960, 540)]);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency, durationSeconds: 5.0);

        Assert.IsTrue(engine.GetZoomState(2.5).ZoomLevel > 1.5f,
            "Passing a duration must not stop in-range clicks from zooming");
    }

    [TestMethod]
    public void GetZoomState_NoDurationSupplied_KeepsEveryInRangeClick()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var recording = BuildRecordingWithClicks(5.0, [(4.9, 960, 540)]);
        // durationSeconds defaults to 0, meaning "unknown" — only the negative check applies.
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        Assert.IsTrue(engine.GetZoomState(4.9).ZoomLevel > 1.5f,
            "With no duration supplied, clicks must not be filtered by an upper bound");
    }
}