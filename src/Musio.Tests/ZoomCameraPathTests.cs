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
        int seed = 0)
        => new(rampStart, holdStart, holdEnd, releaseEnd, zoom, centerX, centerY, seed);

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

    [TestMethod]
    public void LinkedPair_ZoomVelocity_IsContinuousAcrossHandoff()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(1.00, 2.00, 2.25, 3.25, 2.2f, 700, 500, 1000),
            Shot(2.00, 2.45, 3.20, 4.20, 2.2f, 780, 540, 2000),
        ], SourceWidth, SourceHeight);

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
        ], SourceWidth, SourceHeight);

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
        ], SourceWidth, SourceHeight);

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
        ], SourceWidth, SourceHeight);

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
        var empty = ZoomCameraPath.Build([], SourceWidth, SourceHeight);
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsFalse(empty.TryEvaluate(0, out _));
        Assert.IsFalse(ZoomCameraPath.Empty.TryEvaluate(double.PositiveInfinity, out _));

        var invalid = ZoomCameraPath.Build(
        [
            Shot(double.NaN, 0, 0, 0, 2.0f, 960, 540),
            Shot(0, double.PositiveInfinity, 0, 0, 2.0f, 960, 540),
            Shot(0, 0, 0, 0, float.NaN, 960, 540),
        ], SourceWidth, SourceHeight);
        Assert.IsTrue(invalid.IsEmpty);

        var zeroLength = ZoomCameraPath.Build(
        [
            Shot(1, 1, 1, 1, 2.0f, 960, 540, 1),
            Shot(1, 1, 1, 1, 2.5f, 1000, 560, 2),
        ], SourceWidth, SourceHeight);
        Assert.IsFalse(zeroLength.IsEmpty);
        _ = zeroLength.TryEvaluate(1, out _);

        var single = ZoomCameraPath.Build([Shot(2, 2, 2, 2, 2.0f, 960, 540)], SourceWidth, SourceHeight);
        Assert.IsFalse(single.IsEmpty);
        _ = single.TryEvaluate(2, out _);

        var zeroDimensions = ZoomCameraPath.Build(
            [Shot(0, 1, 2, 3, 2.0f, 960, 540), Shot(0.9, 1.5, 2.5, 3.5, 2.0f, 1000, 600)],
            0,
            0);
        Assert.IsFalse(zeroDimensions.IsEmpty);
        _ = zeroDimensions.TryEvaluate(1.25, out _);
    }

    [TestMethod]
    public void FarHandoff_AddsArcZoomDip()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 2.0, 3.0, 3.0f, 200, 200, 1),
            Shot(1.9, 2.5, 3.5, 4.5, 3.0f, 1800, 900, 2),
        ], SourceWidth, SourceHeight);

        double mid = (path.Shots[0].HoldEnd + path.Shots[1].HoldStart) / 2.0;
        var middle = Sample(path, mid);

        Assert.IsTrue(middle.Zoom < 2.90f,
            $"A long lateral handoff should dip below both endpoint zooms for orientation; mid zoom was {middle.Zoom:F3}.");
        Assert.IsTrue(middle.Zoom > 1.0f);
    }

    [TestMethod]
    public void NearbyHandoff_DoesNotAddArcDip()
    {
        var path = ZoomCameraPath.Build(
        [
            Shot(0.0, 1.0, 2.0, 3.0, 2.0f, 960, 540, 1),
            Shot(1.9, 2.5, 3.5, 4.5, 2.6f, 985, 550, 2),
        ], SourceWidth, SourceHeight);

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

        var first = ZoomCameraPath.Build(shots, SourceWidth, SourceHeight);
        var second = ZoomCameraPath.Build(shots, SourceWidth, SourceHeight);

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
            SegmentFrameComposer.SelectManualZoomKeyframes(model, primary).Select(ShotFrom),
            SourceWidth,
            SourceHeight);
        var appendedPath = ZoomCameraPath.Build(
            SegmentFrameComposer.SelectManualZoomKeyframes(model, appended).Select(ShotFrom),
            SourceWidth,
            SourceHeight);

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
        ], SourceWidth, SourceHeight);

        var hold = Sample(path, 1.5);
        float minHandoffDriftScale = 1f;
        for (double t = path.Shots[0].HoldEnd; t <= path.Shots[1].HoldStart + 1e-9; t += 0.002)
            minHandoffDriftScale = Math.Min(minHandoffDriftScale, Sample(path, t).DriftScale);

        Assert.AreEqual(1.0f, hold.DriftScale, 0.0001f);
        Assert.IsTrue(minHandoffDriftScale < 0.30f,
            $"Drift should yield to a deliberate handoff move during the transition; minimum was {minHandoffDriftScale:F3}.");
    }
}
