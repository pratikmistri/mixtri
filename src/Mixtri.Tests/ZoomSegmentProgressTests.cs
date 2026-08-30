namespace Mixtri.Tests;

using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

/// <summary>
/// Covers the segment-identity fields <see cref="AutoZoomEngine"/> reports on
/// <see cref="ZoomState"/> (<c>HasSegment</c>, <c>SegmentProgress</c>,
/// <c>SegmentHeadingX</c>/<c>Y</c>).
/// <para>
/// These exist purely to drive <see cref="CameraDrift"/>, which is why they are worth
/// testing directly: if the engine silently stops reporting a segment, drift quietly
/// switches itself off and the only symptom is a camera that looks slightly less alive
/// — no exception, no failing assertion anywhere else. Camera drift was in fact briefly
/// dead for every auto zoom because the compositor's cursor-centre override rebuilt the
/// <see cref="ZoomState"/> and dropped these fields on the floor.
/// </para>
/// </summary>
[TestClass]
public sealed class ZoomSegmentProgressTests
{
    private const double TickFrequency = 10_000_000.0;

    private static MouseRecordingData BuildRecordingWithClicks(
        double durationSeconds, List<(double timeSeconds, int x, int y)> clicks)
        => TestMouseRecordingBuilder.WithClicks(durationSeconds, clicks, TickFrequency);

    private static AutoZoomEngine EngineWithClickAt(double clickTime, double duration = 10.0)
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(duration, [(clickTime, 500, 400)]), 1920, 1080, TickFrequency);
        return engine;
    }

    #region Auto segments

    [TestMethod]
    public void GetZoomState_OutsideAnySegment_ReportsNoSegment()
    {
        var engine = EngineWithClickAt(2.0);

        var state = engine.GetZoomState(8.0);

        Assert.IsFalse(state.HasSegment, "No zoom is active at 8s, so no segment should be reported.");
        Assert.AreEqual(1.0f, state.ZoomLevel, 0.001f);
    }

    [TestMethod]
    public void GetZoomState_InsideAnAutoSegment_ReportsTheSegment()
    {
        var engine = EngineWithClickAt(2.0);

        var state = engine.GetZoomState(2.1); // during the hold

        Assert.IsTrue(state.HasSegment, "An active auto zoom must report a segment so drift can run.");
    }

    [TestMethod]
    public void GetZoomState_SegmentProgress_AdvancesMonotonicallyThroughTheSegment()
    {
        // Drift is driven entirely by this value, so it has to sweep 0 -> 1 across the
        // segment. If it were flat, the camera would sit perfectly still.
        var engine = EngineWithClickAt(3.0);

        float previous = -1f;
        int activeSamples = 0;

        for (double t = 1.0; t <= 6.0; t += 0.05)
        {
            var state = engine.GetZoomState(t);
            if (!state.HasSegment) continue;

            activeSamples++;
            Assert.IsTrue(state.SegmentProgress >= previous - 1e-4f,
                $"Segment progress went backwards at t={t}.");
            Assert.IsTrue(state.SegmentProgress is >= 0f and <= 1f,
                $"Segment progress out of range at t={t}: {state.SegmentProgress}");
            previous = state.SegmentProgress;
        }

        Assert.IsTrue(activeSamples > 10, "Expected the segment to be active across many samples.");
        Assert.IsTrue(previous > 0.5f, "Segment progress never reached the back half of the segment.");
    }

    [TestMethod]
    public void GetZoomState_SegmentProgress_StartsNearZeroAndEndsNearOne()
    {
        var engine = EngineWithClickAt(3.0);

        float first = 1f;
        float last = 0f;
        for (double t = 0.5; t <= 7.0; t += 0.02)
        {
            var state = engine.GetZoomState(t);
            if (!state.HasSegment) continue;
            first = Math.Min(first, state.SegmentProgress);
            last = Math.Max(last, state.SegmentProgress);
        }

        Assert.IsTrue(first < 0.1f, $"Segment progress should start near 0, saw {first}.");
        Assert.IsTrue(last > 0.9f, $"Segment progress should reach near 1, saw {last}.");
    }

    [TestMethod]
    public void GetZoomState_SegmentHeading_IsStableWithinAnIsolatedSegment()
    {
        // The heading picks the drift direction; if it changed mid-segment while only
        // one segment is active, the camera would visibly swerve.
        var engine = EngineWithClickAt(3.0);

        float? headingX = null;
        for (double t = 1.0; t <= 6.0; t += 0.05)
        {
            var state = engine.GetZoomState(t);
            if (!state.HasSegment) continue;

            headingX ??= state.SegmentHeadingX;
            Assert.AreEqual(headingX.Value, state.SegmentHeadingX, 1e-4f,
                $"Segment heading changed mid-segment at t={t}.");
        }

        Assert.IsNotNull(headingX, "Expected at least one active sample.");
    }

    [TestMethod]
    public void GetZoomState_SegmentHeading_IsAUsableDirection()
    {
        var state = EngineWithClickAt(3.0).GetZoomState(3.1);

        float length = MathF.Sqrt(
            state.SegmentHeadingX * state.SegmentHeadingX
            + state.SegmentHeadingY * state.SegmentHeadingY);

        Assert.IsTrue(length > 0.1f, $"Heading vector was degenerate (length {length}).");
    }

    [TestMethod]
    public void GetZoomState_SegmentHeading_DiffersBetweenSegments()
    {
        // Consecutive zooms should not all drift in the same direction.
        var engine = new AutoZoomEngine(new AutoZoomConfig { DefaultZoomLevel = 2.0f });
        engine.BuildZoomTimeline(
            BuildRecordingWithClicks(20.0, [(2.0, 300, 300), (14.0, 900, 700)]),
            1920, 1080, TickFrequency);

        var first = engine.GetZoomState(2.1);
        var second = engine.GetZoomState(14.1);

        Assert.IsTrue(first.HasSegment && second.HasSegment);
        Assert.IsTrue(
            Math.Abs(first.SegmentHeadingX - second.SegmentHeadingX) > 1e-3f
            || Math.Abs(first.SegmentHeadingY - second.SegmentHeadingY) > 1e-3f,
            "Two separate zoom segments drifted along an identical heading.");
    }

    [TestMethod]
    public void GetZoomState_DriftParameters_AreDeterministicAcrossEngineInstances()
    {
        // Preview and export build separate engines, often in separate processes.
        // A per-process hash (e.g. string.GetHashCode) would silently desynchronise them.
        var a = EngineWithClickAt(3.0).GetZoomState(3.1);
        var b = EngineWithClickAt(3.0).GetZoomState(3.1);

        Assert.AreEqual(a.SegmentHeadingX, b.SegmentHeadingX, 1e-6f);
        Assert.AreEqual(a.SegmentHeadingY, b.SegmentHeadingY, 1e-6f);
        Assert.AreEqual(a.SegmentProgress, b.SegmentProgress, 1e-6f);
    }

    #endregion

    #region Manual keyframes

    [TestMethod]
    public void GetZoomState_InsideAManualKeyframe_ReportsTheSegment()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(BuildRecordingWithClicks(10.0, []), 1920, 1080, TickFrequency);
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(3.0),
                ZoomLevel = 2.5,
                CenterX = 0.5,
                CenterY = 0.5,
                PreDuration = TimeSpan.FromSeconds(1.0),
                HoldDuration = TimeSpan.FromSeconds(1.0),
                PostDuration = TimeSpan.FromSeconds(1.0),
                IsManual = true,
            },
        ]);

        var state = engine.GetZoomState(3.5);

        Assert.IsTrue(state.HasSegment, "An active manual keyframe must report a segment.");
        Assert.IsTrue(state.IsManualOverride);
        Assert.IsTrue(state.SegmentProgress is > 0f and < 1f,
            $"Expected mid-segment progress, saw {state.SegmentProgress}.");
    }

    [TestMethod]
    public void GetZoomState_ManualSegmentProgress_SweepsTheWholeRange()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        engine.BuildZoomTimeline(BuildRecordingWithClicks(10.0, []), 1920, 1080, TickFrequency);
        engine.SetManualKeyframes([
            new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(4.0),
                ZoomLevel = 2.0,
                CenterX = 0.5,
                CenterY = 0.5,
                PreDuration = TimeSpan.FromSeconds(1.0),
                HoldDuration = TimeSpan.FromSeconds(1.0),
                PostDuration = TimeSpan.FromSeconds(1.0),
                IsManual = true,
            },
        ]);

        // Segment spans 3.0 -> 6.0.
        Assert.IsTrue(engine.GetZoomState(3.05).SegmentProgress < 0.1f);
        Assert.AreEqual(0.5f, engine.GetZoomState(4.5).SegmentProgress, 0.05f);
        Assert.IsTrue(engine.GetZoomState(5.95).SegmentProgress > 0.9f);
    }

    #endregion
}
