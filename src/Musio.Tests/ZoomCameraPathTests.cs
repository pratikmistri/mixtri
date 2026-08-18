using Musio.Core.Export;
using Musio.Core.Processing;
using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public sealed class ZoomCameraPathTests
{
    private const int SourceWidth = 1920;
    private const int SourceHeight = 1080;

    private static ZoomShot Shot(
        double rampStart,
        double holdStart,
        double holdEnd,
        double releaseEnd,
        float zoom,
        float centerX,
        float centerY,
        int seed = 0,
        bool isManual = false)
        => new(rampStart, holdStart, holdEnd, releaseEnd, zoom, centerX, centerY, seed, isManual);

    private static ZoomShot ShotFrom(ZoomKeyframe keyframe)
    {
        double rampStart = keyframe.Start.TotalSeconds;
        return new ZoomShot(
            rampStart,
            keyframe.Timestamp.TotalSeconds,
            (keyframe.Timestamp + keyframe.HoldDuration).TotalSeconds,
            keyframe.End.TotalSeconds,
            (float)keyframe.ZoomLevel,
            (float)(keyframe.CenterX * SourceWidth),
            (float)(keyframe.CenterY * SourceHeight),
            (int)Math.Round(rampStart * 1000.0));
    }

    private static ZoomCameraSample Sample(ZoomCameraPath path, double timeSeconds)
    {
        Assert.IsTrue(path.TryEvaluate(timeSeconds, out var sample), $"Expected active sample at t={timeSeconds:F3}.");
        return sample;
    }

    /// <summary>
    /// Two zoom segments dragged out near each other on the timeline must actually link.
    /// <para>
    /// Regression: <see cref="ZoomCameraPath.LinkGapSeconds"/> was originally 0.35s, which
    /// was tighter than the gap two default-eased segments naturally leave, so the handoff
    /// never engaged on the "close together" case it exists for and the feature looked like
    /// it did nothing. These build the keyframes exactly as the editor does — via
    /// <see cref="ZoomKeyframe.FromRange"/> — rather than with hand-picked ramp times, so the
    /// test tracks what a user actually authors.
    /// </para>
    /// </summary>
    [DataTestMethod]
    [DataRow(0.0, 3.0, 3.4, 6.4, true, DisplayName = "0.4s apart links")]
    [DataRow(0.0, 3.0, 3.6, 6.6, true, DisplayName = "0.6s apart links")]
    [DataRow(0.0, 3.0, 4.4, 7.4, false, DisplayName = "1.4s apart stays independent")]
    public void SegmentsAuthoredCloseTogether_LinkIntoAHandoff(
        double aStart, double aEnd, double bStart, double bEnd, bool expectLinked)
    {
        var a = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(aStart), TimeSpan.FromSeconds(aEnd), 2.0, 0.3, 0.3);
        var b = ZoomKeyframe.FromRange(
            TimeSpan.FromSeconds(bStart), TimeSpan.FromSeconds(bEnd), 2.0, 0.7, 0.7);

        Assert.AreEqual(expectLinked, ZoomCameraPath.AreLinked(a, b),
            $"AreLinked disagreed for a gap of {(b.Start - a.End).TotalSeconds:F2}s " +
            $"(LinkGapSeconds = {ZoomCameraPath.LinkGapSeconds:F2}).");

        var path = ZoomCameraPath.Build([ShotFrom(a), ShotFrom(b)]);
        Assert.AreEqual(expectLinked, path.IsLinkedAfter(0),
            "The path's own linkage must agree with the shared AreLinked predicate the timeline draws from.");

        if (!expectLinked)
            return;

        // A linked pair must carry the camera across without pumping back toward 1x.
        float minZoom = float.MaxValue;
        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart; t += 0.002)
            minZoom = Math.Min(minZoom, Sample(path, t).Zoom);

        Assert.IsTrue(minZoom > 1.5f,
            $"Linked segments should hand off without releasing toward 1x; min zoom was {minZoom:F3}.");
    }

    /// <summary>
    /// A handoff between two DIFFERENT zoom levels must be monotonic — it must never
    /// undershoot its destination and climb back.
    /// <para>
    /// Regression from Sample7.musio, reported as "segment 1 is 2x and segment 2 is 1.5x...
    /// I expect a smooth transition from 2x to 1.5x without a dip". The arc originally divided
    /// the whole interpolated zoom, so with these centres ~1058px apart it pulled the move down
    /// to ~1.39x mid-transition and then back up to 1.5x. The arc now applies above a floor that
    /// rises to the lower endpoint as the two zooms diverge, so the frame widening the move
    /// already provides is not doubled up on.
    /// </para>
    /// </summary>
    [TestMethod]
    public void HandoffBetweenDifferentZoomLevels_IsMonotonic_AndNeverUndershoots()
    {
        // Exact values from Sample7.musio (source 2304x1536).
        const int w = 2304;
        const int h = 1536;
        var auto = Shot(1.426384, 2.426384, 3.759384, 5.315384, 2.0f, (float)(0.669 * w), (float)(0.876 * h), 1426);
        var manual = Shot(3.2024495, 4.2024495, 5.5354495, 7.0914495, 1.5f, (float)(0.931 * w), (float)(0.014 * h), 3202, isManual: true);

        var path = ZoomCameraPath.Build([auto, manual]);
        Assert.IsTrue(path.IsLinkedAfter(0), "These two segments overlap and must be linked.");

        float previous = float.MaxValue;
        float minZoom = float.MaxValue;
        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart; t += 0.002)
        {
            float zoom = Sample(path, t).Zoom;
            minZoom = Math.Min(minZoom, zoom);

            // Going 2.0 -> 1.5, so the curve must never rise again.
            Assert.IsTrue(zoom <= previous + 0.0005f,
                $"Zoom rose from {previous:F4} to {zoom:F4} at t={t:F3}s — that is the dip-and-recover bounce.");
            previous = zoom;
        }

        Assert.IsTrue(minZoom >= 1.5f - 0.001f,
            $"Zoom undershot the 1.5x destination, reaching {minZoom:F4}.");
    }
    /// <summary>
    /// A chain of equal-zoom segments must hold that zoom dead flat the whole way through.
    /// <para>
    /// Regression from Sample9.musio: five overlapping auto segments, all 2×, produced four
    /// visible zoom-out/zoom-in pulses across what should have been one continuous 2× move.
    /// The cause was the long-move "cinematic arc", which dipped the zoom in proportion to how
    /// far the stored centres were apart — and for auto shots those stored centres are not even
    /// what is on screen, since the compositor re-centres them on the live cursor. The arc is
    /// gone; a handoff is now a pure interpolation between the two segments' own levels.
    /// </para>
    /// </summary>
    [TestMethod]
    public void ChainOfEqualZoomSegments_HoldsThatZoomFlatThroughout()
    {
        const int w = 2304;
        const int h = 1536;

        // Sample9.musio: five auto clicks, all 2x, centres flung across opposite corners so any
        // travel-driven zoom effect would show up loudly.
        var path = ZoomCameraPath.Build(
        [
            Shot(-0.481, 0.519, 1.852, 3.408, 2.0f, (float)(0.053 * w), (float)(0.132 * h), 1),
            Shot(0.679, 1.679, 3.012, 4.568, 2.0f, (float)(0.882 * w), (float)(0.149 * h), 2),
            Shot(1.807, 2.807, 4.140, 5.696, 2.0f, (float)(0.034 * w), (float)(0.780 * h), 3),
            Shot(2.991, 3.991, 5.324, 6.880, 2.0f, (float)(0.898 * w), (float)(0.768 * h), 4),
            Shot(4.199, 5.199, 6.532, 8.088, 2.0f, (float)(0.458 * w), (float)(0.421 * h), 5),
        ]);

        for (int i = 0; i < 4; i++)
            Assert.IsTrue(path.IsLinkedAfter(i), $"Shots {i} and {i + 1} overlap and must be linked.");

        // From the first segment settling to the last one releasing, the zoom must not move.
        double start = path.Shots[0].HoldStart;
        double end = path.Shots[4].HoldEnd;
        for (double t = start; t <= end; t += 0.002)
        {
            float zoom = Sample(path, t).Zoom;
            Assert.AreEqual(2.0f, zoom, 0.001f,
                $"Zoom moved to {zoom:F4} at t={t:F3}s in a chain where every segment is 2x. " +
                "An all-2x chain must read as one continuous move with no pulses.");
        }
    }

    /// <summary>
    /// The handoff must run across the INCOMING segment's own ramp — starting at its leading
    /// edge on the timeline and lasting its authored <c>PreDuration</c> — so an overlapped
    /// segment animates in at the same place and speed as an unoverlapped one.
    /// <para>
    /// Regression from Sample7.musio: "the transition to 1.5x happens when first segment is
    /// ending, instead of where 2nd segment is starting, and the timing curve is different than
    /// usual zoom segment's start." The window used to be derived from the outgoing shot's hold
    /// end, which for these two segments started the move at 3.759s and squeezed it into 443ms
    /// instead of starting at 3.202s and running the authored 1.0s.
    /// </para>
    /// </summary>
    [TestMethod]
    public void Handoff_RunsAcrossTheIncomingSegmentsOwnRamp()
    {
        const int w = 2304;
        const int h = 1536;
        var auto = Shot(1.426384, 2.426384, 3.759384, 5.315384, 2.0f, (float)(0.669 * w), (float)(0.876 * h), 1426);
        var manual = Shot(3.2024495, 4.2024495, 5.5354495, 7.0914495, 1.5f, (float)(0.931 * w), (float)(0.014 * h), 3202, isManual: true);

        var path = ZoomCameraPath.Build([auto, manual]);
        Assert.IsTrue(path.IsLinkedAfter(0));

        // The move starts exactly at segment 2's leading edge...
        Assert.AreEqual(manual.RampStart, path.Shots[0].HoldEnd, 0.001,
            "The handoff should begin where the incoming segment begins, not where the outgoing one stops holding.");
        // ...and ends exactly where segment 2 settles, so it lasts its authored PreDuration.
        Assert.AreEqual(manual.HoldStart, path.Shots[1].HoldStart, 0.001,
            "The incoming segment should still settle at its own timestamp.");
        Assert.AreEqual(
            (manual.HoldStart - manual.RampStart),
            path.Shots[1].HoldStart - path.Shots[0].HoldEnd,
            0.001,
            "The handoff should last the incoming segment's authored PreDuration.");

        // The outgoing segment holds its target right up to that edge.
        Assert.AreEqual(2.0f, Sample(path, manual.RampStart - 0.01).Zoom, 0.01f,
            "Segment 1 should still be holding 2x immediately before segment 2's edge.");
    }

    /// <summary>
    /// The handoff curve must match a normal zoom-in's curve. Both run
    /// <see cref="CubicBezierEasing.EaseInOutCinematic"/> over the incoming segment's ramp, so
    /// the normalized progress of a handoff and of an ordinary ramp-in should agree closely.
    /// </summary>
    [TestMethod]
    public void Handoff_UsesTheSameTimingCurveAsAnOrdinaryRampIn()
    {
        const int w = 1920;
        const int h = 1080;
        const double rampStart = 3.0;
        const double holdStart = 4.0;

        // Overlapped: 2x hands off to 1.5x across the incoming ramp.
        var overlapped = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 3.6, 5.0, 2.0f, 960, 540, 1),
            Shot(rampStart, holdStart, 5.0, 6.5, 1.5f, 960, 540, 2, isManual: true),
        ]);

        // Unoverlapped: the same incoming segment ramping from 1x on its own.
        var alone = ZoomCameraPath.Build(
        [
            Shot(rampStart, holdStart, 5.0, 6.5, 1.5f, 960, 540, 2, isManual: true),
        ]);

        // Same centre on both shots above, so the arc cannot contribute and the curves are
        // comparable purely as timing.
        for (double u = 0.05; u <= 0.95; u += 0.05)
        {
            double t = rampStart + ((holdStart - rampStart) * u);
            float overlappedProgress = (Sample(overlapped, t).Zoom - 2.0f) / (1.5f - 2.0f);
            float aloneProgress = (Sample(alone, t).Zoom - 1.0f) / (1.5f - 1.0f);

            Assert.AreEqual(aloneProgress, overlappedProgress, 0.02f,
                $"At u={u:F2} the handoff was {overlappedProgress:F3} through its move but an " +
                $"ordinary ramp-in was {aloneProgress:F3} through its own — the curves must match.");
        }
    }

    /// <summary>
    /// In a MIXED handoff — one shot centring on the live cursor, the other on its own stored
    /// centre — the focal point must travel in step with the zoom.
    /// <para>
    /// Regression: "the pre animation does the zoom but not the travel, so zoom starts first and
    /// then the travel making overall animation feel odd." The path interpolated its centre AND
    /// the compositor blended the cursor in afterwards, so the two compose to
    /// <c>A + (B - A) * e²</c> — quadratic travel against linear zoom. At e=0.40 the camera was
    /// only ~16% of the way across. The path now reports the manual endpoint as a fixed anchor
    /// and lets the compositor's single blend supply the whole move, making travel linear in e.
    /// </para>
    /// <para>
    /// This emulates <c>FrameCompositor.ResolveZoomState</c>'s blend, which is the only place the
    /// two contributions meet — testing the path's centre alone cannot catch this.
    /// </para>
    /// </summary>
    [TestMethod]
    public void MixedHandoff_FocalPointTravelsInStepWithTheEasedMove()
    {
        const int w = 2304;
        const int h = 1536;
        var auto = Shot(1.426384, 2.426384, 3.759384, 5.315384, 2.0f, (float)(0.669 * w), (float)(0.876 * h), 1426);
        var manual = Shot(3.2024495, 4.2024495, 5.5354495, 7.0914495, 1.5f, (float)(0.931 * w), (float)(0.014 * h), 3202, isManual: true);

        var path = ZoomCameraPath.Build([auto, manual]);
        Assert.IsTrue(path.IsLinkedAfter(0));

        // The compositor parks the auto shot on the live cursor; place it where that shot looks.
        float cursorX = auto.CenterX;
        float cursorY = auto.CenterY;

        double tStart = path.Shots[0].HoldEnd;
        double tEnd = path.Shots[1].HoldStart;

        (float X, float Y) Blend(double t)
        {
            var s = Sample(path, t);
            float weight = Math.Clamp(s.CursorFollowWeight, 0f, 1f);
            return (s.CenterX + ((cursorX - s.CenterX) * weight),
                    s.CenterY + ((cursorY - s.CenterY) * weight));
        }

        var start = Blend(tStart);
        var end = Blend(tEnd);
        double total = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        Assert.IsTrue(total > 1, "This fixture is meant to cover a genuine camera move.");

        for (double u = 0.1; u <= 0.9; u += 0.1)
        {
            double t = tStart + ((tEnd - tStart) * u);
            var p = Blend(t);
            double travelled = Math.Sqrt(Math.Pow(p.X - start.X, 2) + Math.Pow(p.Y - start.Y, 2)) / total;
            float expected = CubicBezierEasing.EaseInOutCinematic((float)u);

            Assert.AreEqual(expected, travelled, 0.05,
                $"At u={u:F2} the camera was {travelled:P0} across but the eased move was {expected:P0} " +
                "through. Travel must follow the same parameter as the zoom, not lag it quadratically.");
        }
    }

    /// <summary>
    /// Fuzzes the path over many randomly generated shot arrangements and asserts the structural
    /// invariant that must hold for every input: the piecewise camera function is CONTINUOUS —
    /// every piece hands off to its neighbour at the same value.
    /// <para>
    /// This is the assertion that catches a whole class of bug at once: a gap in the tiling
    /// (which makes <c>TryEvaluate</c> return false and renders 1x, i.e. a visible flash), two
    /// pieces overlapping so the binary search can return either, or an off-by-one at a piece
    /// edge. Treating "no active piece" as 1x is exactly what the renderer does, so this probes
    /// the tiling as the compositor actually experiences it.
    /// </para>
    /// <para>
    /// It samples a hair either side of every piece boundary rather than on a fixed grid,
    /// deliberately. A coarse grid conflates continuity with SPEED, and speed is authored: two
    /// segments can legitimately settle a few milliseconds apart, and the camera then has to
    /// cover the whole zoom change in those few milliseconds. That is fast, but it is not a
    /// discontinuity, and <see cref="ZoomCameraPath.MinTransitionSeconds"/> cannot widen it when
    /// neighbouring holds have already been consumed. Asserting at the boundaries measures the
    /// property that is actually invariant.
    /// </para>
    /// <para>
    /// Ramps and releases are given a realistic floor, because a zero-length ramp makes a shot
    /// pop from 1x to its target in one instant — a discontinuity by construction rather than a
    /// tiling defect — and the app cannot author one: <see cref="ZoomKeyframe.FromRange"/>
    /// derives a pre-duration of at least ~40ms, resizing clamps it to 50ms, and auto segments
    /// use a fixed 1s. Zero-length holds ARE generated, since heavy overlap collapses them.
    /// </para>
    /// <para>The generator is seeded, so any failure reproduces exactly.</para>
    /// </summary>
    [TestMethod]
    public void Fuzz_PieceBoundariesAreContinuous()
    {
        // Far smaller than the shortest piece the generator can produce, so the sampled pair
        // straddles the boundary and nothing else.
        const double epsilon = 1e-6;
        const float tolerance = 0.01f;

        for (int trial = 0; trial < 400; trial++)
        {
            var rng = new Random(trial);
            var shots = new List<ZoomShot>();

            for (int i = 0, n = 1 + rng.Next(5); i < n; i++)
            {
                double rampStart = Math.Round(rng.NextDouble() * 8.0, 3);
                double ramp = 0.05 + (rng.NextDouble() * 1.0);
                double hold = rng.Next(4) == 0 ? 0 : rng.NextDouble() * 1.5;
                double release = 0.05 + (rng.NextDouble() * 1.5);

                shots.Add(Shot(
                    rampStart,
                    rampStart + ramp,
                    rampStart + ramp + hold,
                    rampStart + ramp + hold + release,
                    1.05f + ((float)rng.NextDouble() * 3f),
                    (float)rng.NextDouble() * SourceWidth,
                    (float)rng.NextDouble() * SourceHeight,
                    seed: i,
                    isManual: rng.Next(2) == 0));
            }

            var path = ZoomCameraPath.Build(shots);
            if (path.IsEmpty) continue;

            // "No active piece" is 1x, which is what the renderer shows there.
            float ZoomAt(double t) => path.TryEvaluate(t, out var s) ? s.Zoom : 1f;

            foreach (var shot in path.Shots)
            {
                foreach (double boundary in new[] { shot.RampStart, shot.HoldStart, shot.HoldEnd, shot.ReleaseEnd })
                {
                    float before = ZoomAt(boundary - epsilon);
                    float after = ZoomAt(boundary + epsilon);

                    Assert.IsTrue(float.IsFinite(before) && float.IsFinite(after),
                        $"trial {trial}: non-finite zoom around boundary {boundary:F6}.");
                    Assert.IsTrue(before >= 1f && after >= 1f,
                        $"trial {trial}: zoom below 1x around boundary {boundary:F6} " +
                        $"({before:F4} -> {after:F4}).");
                    Assert.IsTrue(Math.Abs(after - before) <= tolerance,
                        $"trial {trial}: the camera jumped {Math.Abs(after - before):F4} across the " +
                        $"piece boundary at {boundary:F6}s ({before:F4} -> {after:F4}). Pieces must " +
                        "hand off at equal values — this is a gap or an overlap in the tiling.");
                }
            }
        }
    }

    /// <summary>
    /// Fuzzes deliberately degenerate input — zero-length ramps, holds and releases, shots out of
    /// order, heavy overlap, full containment, shots sharing a moment — and asserts what still
    /// has to hold there: the path never throws, never produces a non-finite or sub-1x value, and
    /// always comes back with ordered, non-overlapping holds, which is the premise the binary
    /// search in <c>TryEvaluate</c> relies on.
    /// </summary>
    [TestMethod]
    public void Fuzz_DegenerateShots_StayWellFormed()
    {
        for (int trial = 0; trial < 300; trial++)
        {
            var rng = new Random(10_000 + trial);
            var shots = new List<ZoomShot>();

            for (int i = 0, n = 1 + rng.Next(5); i < n; i++)
            {
                // Cluster the starts so containment and shared moments actually occur.
                double rampStart = Math.Round(rng.NextDouble() * 2.0, 3);
                double ramp = rng.Next(3) == 0 ? 0 : rng.NextDouble();
                double hold = rng.Next(3) == 0 ? 0 : rng.NextDouble() * 1.5;
                double release = rng.Next(3) == 0 ? 0 : rng.NextDouble() * 1.5;

                shots.Add(Shot(
                    rampStart,
                    rampStart + ramp,
                    rampStart + ramp + hold,
                    rampStart + ramp + hold + release,
                    1.05f + ((float)rng.NextDouble() * 3f),
                    (float)rng.NextDouble() * SourceWidth,
                    (float)rng.NextDouble() * SourceHeight,
                    seed: i,
                    isManual: rng.Next(2) == 0));
            }

            var path = ZoomCameraPath.Build(shots);
            if (path.IsEmpty) continue;

            for (int i = 0; i < path.Shots.Count; i++)
            {
                var s = path.Shots[i];
                Assert.IsTrue(s.RampStart <= s.HoldStart && s.HoldStart <= s.HoldEnd && s.HoldEnd <= s.ReleaseEnd,
                    $"trial {trial}: shot {i} has out-of-order edges: " +
                    $"{s.RampStart}/{s.HoldStart}/{s.HoldEnd}/{s.ReleaseEnd}.");
                if (i > 0)
                {
                    Assert.IsTrue(path.Shots[i - 1].HoldEnd <= s.HoldStart + 1e-9,
                        $"trial {trial}: shot {i - 1}'s hold overlaps shot {i}'s — the binary " +
                        "search premise of one active piece would break.");
                }
            }

            double start = path.Shots.Min(s => s.RampStart) - 0.2;
            double end = path.Shots.Max(s => s.ReleaseEnd) + 0.2;
            for (double t = start; t <= end; t += 0.001)
            {
                if (!path.TryEvaluate(t, out var sample)) continue;

                Assert.IsTrue(float.IsFinite(sample.Zoom) && sample.Zoom >= 1f,
                    $"trial {trial}: zoom {sample.Zoom} at t={t:F3} is not a sane zoom level.");
                Assert.IsTrue(float.IsFinite(sample.CenterX) && float.IsFinite(sample.CenterY),
                    $"trial {trial}: focal point was not finite at t={t:F3}.");
                Assert.IsTrue(float.IsFinite(sample.DriftScale)
                    && float.IsFinite(sample.CursorFollowWeight)
                    && float.IsFinite(sample.SegmentProgress),
                    $"trial {trial}: a drift/blend value was not finite at t={t:F3}.");
            }
        }
    }

    [TestMethod]
    public void LinkedPair_ZoomVelocity_IsContinuousAcrossHandoff()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(1.00, 2.00, 2.25, 3.25, 2.2f, 700, 500, 1000),
            Shot(2.00, 2.45, 3.20, 4.20, 2.2f, 780, 540, 2000),
        ]);

        Assert.IsTrue(path.IsLinkedAfter(0));

        double start = path.Shots[0].HoldEnd - 0.08;
        double end = path.Shots[1].HoldStart + 0.08;
        const double dt = 0.002;
        var zooms = new List<float>();
        for (double t = start; t <= end + 1e-9; t += dt)
            zooms.Add(Sample(path, t).Zoom);

        var velocities = new List<double>();
        for (int i = 1; i < zooms.Count; i++)
            velocities.Add((zooms[i] - zooms[i - 1]) / dt);

        double maxVelocityDelta = 0;
        for (int i = 1; i < velocities.Count; i++)
            maxVelocityDelta = Math.Max(maxVelocityDelta, Math.Abs(velocities[i] - velocities[i - 1]));

        Assert.IsTrue(maxVelocityDelta < 0.05,
            $"Zoom velocity changed by {maxVelocityDelta:F3} zoom/s between adjacent 2ms samples. " +
            "The old max(falling,rising) overlap logic was value-continuous but had a derivative corner, " +
            "so this velocity-continuity assertion would have caught it.");
    }

    [TestMethod]
    public void LinkedPair_NeverPumpsBackTowardOneBetweenTargets()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(1.00, 2.00, 2.25, 3.25, 2.2f, 700, 500, 1000),
            Shot(2.00, 2.45, 3.20, 4.20, 2.2f, 780, 540, 2000),
        ]);

        float minZoom = float.MaxValue;
        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart; t += 0.002)
            minZoom = Math.Min(minZoom, Sample(path, t).Zoom);

        Assert.IsTrue(minZoom > 2.05f,
            $"Linked equal-zoom shots should hand off near the target zoom, not pump toward 1x; min was {minZoom:F3}.");
    }

    [TestMethod]
    public void AnyPath_ZoomIsNeverBelowOne()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.00, 0.50, 1.00, 2.00, 1.2f, 100, 100, 1),
            Shot(0.90, 1.50, 2.00, 3.00, 1.2f, 1850, 1000, 2),
            Shot(2.70, 3.20, 3.60, 4.20, 2.8f, 960, 540, 3),
        ]);

        int activeSamples = 0;
        for (double t = -0.2; t <= 4.4; t += 0.005)
        {
            if (!path.TryEvaluate(t, out var sample))
                continue;

            activeSamples++;
            Assert.IsTrue(sample.Zoom >= 1.0f, $"Zoom fell below 1x at t={t:F3}: {sample.Zoom:F4}.");
        }

        Assert.IsTrue(activeSamples > 0);
    }

    [TestMethod]
    public void ContainedLowerZoomShot_IsVisitedAsAWaypoint()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 6.0, 7.0, 3.0f, 200, 200, 1),
            Shot(2.0, 2.5, 3.0, 3.5, 2.0f, 1600, 800, 2),
        ]);

        Assert.IsTrue(path.IsLinkedAfter(0));

        var waypoint = Sample(path, 3.0);
        Assert.AreEqual(2.0f, waypoint.Zoom, 0.02f,
            "A lower-zoom shot contained in a higher-zoom shot should still become the active waypoint.");
        Assert.AreEqual(1600f, waypoint.CenterX, 1f);
        Assert.AreEqual(800f, waypoint.CenterY, 1f);
    }

    [TestMethod]
    public void DegenerateInputs_DoNotThrow()
    {
        var empty = ZoomCameraPath.Build([]);
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsFalse(empty.TryEvaluate(0, out _));
        Assert.IsFalse(ZoomCameraPath.Empty.TryEvaluate(double.PositiveInfinity, out _));

        var invalid = ZoomCameraPath.Build(
        [
            Shot(double.NaN, 0, 0, 0, 2.0f, 960, 540),
            Shot(0, double.PositiveInfinity, 0, 0, 2.0f, 960, 540),
            Shot(0, 0, 0, 0, float.NaN, 960, 540),
        ]);
        Assert.IsTrue(invalid.IsEmpty);

        var zeroLength = ZoomCameraPath.Build(
        [
            Shot(1, 1, 1, 1, 2.0f, 960, 540, 1),
            Shot(1, 1, 1, 1, 2.5f, 1000, 560, 2),
        ]);
        Assert.IsFalse(zeroLength.IsEmpty);
        _ = zeroLength.TryEvaluate(1, out _);

        var single = ZoomCameraPath.Build([Shot(2, 2, 2, 2, 2.0f, 960, 540)]);
        Assert.IsFalse(single.IsEmpty);
        _ = single.TryEvaluate(2, out _);
    }

    /// <summary>
    /// A long lateral handoff must NOT invent a zoom dip. This used to assert the opposite —
    /// that the "cinematic arc" pulled back for orientation — but that arc was removed after it
    /// turned an all-2× chain into a series of visible pulses. Zoom is now driven only by the
    /// two segments' own levels, which is the predictable behaviour.
    /// </summary>
    [TestMethod]
    public void FarHandoff_DoesNotInventAZoomDip()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 2.0, 3.0, 3.0f, 200, 200, 1),
            Shot(1.9, 2.5, 3.5, 4.5, 3.0f, 1800, 900, 2),
        ]);

        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart; t += 0.002)
        {
            float zoom = Sample(path, t).Zoom;
            Assert.AreEqual(3.0f, zoom, 0.001f,
                $"Two 3x shots must stay at 3x however far apart their centres are; got {zoom:F4} at t={t:F3}.");
        }
    }

    [TestMethod]
    public void NearbyHandoff_DoesNotAddArcDip()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 2.0, 3.0, 2.0f, 960, 540, 1),
            Shot(1.9, 2.5, 3.5, 4.5, 2.6f, 985, 550, 2),
        ]);

        float previous = Sample(path, path.Shots[0].HoldEnd).Zoom;
        for (double t = path.Shots[0].HoldEnd + 0.005; t <= path.Shots[1].HoldStart + 1e-9; t += 0.005)
        {
            float zoom = Sample(path, t).Zoom;
            Assert.IsTrue(zoom >= previous - 1e-4f,
                $"Nearby handoff should be monotonic without an orientation dip; {zoom:F4} after {previous:F4} at t={t:F3}.");
            Assert.IsTrue(zoom >= 2.0f - 1e-4f);
            previous = zoom;
        }
    }

    [TestMethod]
    public void BuildAndEvaluate_IsDeterministic()
    {
        ZoomShot[] shots =
        [
            Shot(0.0, 1.0, 2.0, 3.0, 2.2f, 200, 200, 10),
            Shot(1.8, 2.4, 3.2, 4.2, 2.8f, 1700, 760, 20),
            Shot(4.8, 5.3, 5.7, 6.5, 1.6f, 960, 540, 30),
        ];

        var first = ZoomCameraPath.Build(shots);
        var second = ZoomCameraPath.Build(shots);

        for (double t = 0; t <= 6.6; t += 0.037)
        {
            bool firstHit = first.TryEvaluate(t, out var a);
            bool secondHit = second.TryEvaluate(t, out var b);
            Assert.AreEqual(firstHit, secondHit);
            Assert.AreEqual(a.Zoom, b.Zoom);
            Assert.AreEqual(a.CenterX, b.CenterX);
            Assert.AreEqual(a.CenterY, b.CenterY);
            Assert.AreEqual(a.SegmentProgress, b.SegmentProgress);
            Assert.AreEqual(a.HeadingX, b.HeadingX);
            Assert.AreEqual(a.HeadingY, b.HeadingY);
            Assert.AreEqual(a.DriftScale, b.DriftScale);
        }
    }

    [TestMethod]
    public void DifferentSourceFiles_AreFilteredBeforePathBuildSoTheyCannotChain()
    {
        const string primary = @"C:\rec\primary\video.mp4";
        const string appended = @"C:\rec\appended\video.mp4";
        var primaryZoom = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5.0),
            CenterX = 0.25,
            CenterY = 0.25,
            IsManual = true,
        };
        var appendedZoom = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5.1),
            CenterX = 0.75,
            CenterY = 0.75,
            IsManual = true,
            SourceVideoFilePath = appended,
        };
        var model = new TimelineModel { PrimaryVideoFilePath = primary };
        model.ZoomKeyframes.Add(primaryZoom);
        model.ZoomKeyframes.Add(appendedZoom);

        Assert.IsTrue(ZoomCameraPath.AreLinked(primaryZoom, appendedZoom),
            "The raw source times are close enough to link; filtering must keep different recordings apart.");

        var primaryPath = ZoomCameraPath.Build(
            SegmentFrameComposer.SelectManualZoomKeyframes(model, primary).Select(ShotFrom));
        var appendedPath = ZoomCameraPath.Build(
            SegmentFrameComposer.SelectManualZoomKeyframes(model, appended).Select(ShotFrom));

        Assert.AreEqual(1, primaryPath.Shots.Count);
        Assert.AreEqual(1, appendedPath.Shots.Count);
        Assert.IsFalse(primaryPath.IsLinkedAfter(0));
        Assert.IsFalse(appendedPath.IsLinkedAfter(0));
    }

    [TestMethod]
    public void DriftScale_IsOneDuringHoldAndDipsDuringHandoff()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 2.0, 3.0, 2.0f, 200, 200, 1),
            Shot(1.9, 2.5, 3.5, 4.5, 2.0f, 1800, 900, 2),
        ]);

        var hold = Sample(path, 1.5);
        float minHandoffDriftScale = 1f;
        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart + 1e-9; t += 0.002)
            minHandoffDriftScale = Math.Min(minHandoffDriftScale, Sample(path, t).DriftScale);

        Assert.AreEqual(1.0f, hold.DriftScale, 0.0001f);
        Assert.IsTrue(minHandoffDriftScale < 0.30f,
            $"Drift should yield to a deliberate handoff move during the transition; minimum was {minHandoffDriftScale:F3}.");
    }

    [TestMethod]
    public void SingleShot_DriftSettingsAreForwardedOntoEverySample()
    {
        // A lone shot (no handoff) must forward its own per-segment Drift onto the
        // resolved sample through the ramp-in, hold, and release pieces alike.
        var drift = new CameraDriftSettings { Enabled = false, Strength = 2f };
        var shot = new ZoomShot(0.0, 1.0, 2.0, 3.0, 2.0f, 200, 200, 1, Drift: drift);
        var path = ZoomCameraPath.Build([shot]);

        Assert.AreEqual(drift, Sample(path, 0.5).Drift, "ramp-in piece should carry the shot's Drift");
        Assert.AreEqual(drift, Sample(path, 1.5).Drift, "hold piece should carry the shot's Drift");
        Assert.AreEqual(drift, Sample(path, 2.5).Drift, "release piece should carry the shot's Drift");
    }

    [TestMethod]
    public void Transition_DominantShotsDriftSettingsResolveOnEachSideOfTheHandoff()
    {
        // The headline per-segment behaviour for a handoff: CameraDriftSettings is a
        // reference-typed record and cannot be lerped, so ZoomCameraPath resolves the
        // DOMINANT shot's settings instead of blending them. This pins that the outgoing
        // shot's settings apply just after the handoff starts and the incoming shot's
        // settings apply just before it ends.
        var fromDrift = new CameraDriftSettings { Enabled = true, Strength = 1f };
        var toDrift = new CameraDriftSettings { Enabled = false };
        var path = ZoomCameraPath.Build(
        [
            new ZoomShot(0.0, 1.0, 2.0, 3.0, 2.0f, 200, 200, 1, Drift: fromDrift),
            new ZoomShot(1.9, 2.5, 3.5, 4.5, 2.0f, 1800, 900, 2, Drift: toDrift),
        ]);

        double handoffStart = path.Shots[0].HoldEnd;
        double handoffEnd = path.Shots[1].HoldStart;

        var justAfterStart = Sample(path, handoffStart + 0.001);
        var justBeforeEnd = Sample(path, handoffEnd - 0.001);

        Assert.AreEqual(fromDrift, justAfterStart.Drift,
            "the outgoing shot's Drift should still dominate right after the handoff begins");
        Assert.AreEqual(toDrift, justBeforeEnd.Drift,
            "the incoming shot's Drift should dominate right before the handoff ends");
    }
}
