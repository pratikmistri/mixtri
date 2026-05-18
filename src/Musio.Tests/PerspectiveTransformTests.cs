namespace Musio.Tests;

using System.Numerics;
using Musio.Core.Processing;
using Musio.Core.Models;
using Musio.Core.Timeline;

[TestClass]
public sealed class PerspectiveTransformTests
{
    private const double TickFrequency = 10_000_000.0;

    #region Matrix Tests

    [TestMethod]
    public void BuildMatrix_ZeroRotation_ReturnsIdentity()
    {
        var matrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, 0f, 0f, 2000f);

        // With zero rotation, the matrix should be identity except for the
        // perspective term M34 = -1/cameraDistance
        Assert.AreEqual(1f, matrix.M11, 0.001f);
        Assert.AreEqual(1f, matrix.M22, 0.001f);
        Assert.AreEqual(1f, matrix.M33, 0.001f);
        Assert.AreEqual(0f, matrix.M12, 0.001f);
        Assert.AreEqual(0f, matrix.M21, 0.001f);
        Assert.AreEqual(1f / 2000f, matrix.M34, 0.0001f, "Perspective term should be 1/d");
    }

    [TestMethod]
    public void BuildMatrix_YRotation_ProducesNonIdentity()
    {
        float angle = 10f * MathF.PI / 180f;
        var matrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, angle, 0f, 2000f);

        // M11 should be cos(angle), not 1.0
        float expectedCos = MathF.Cos(angle);
        Assert.AreEqual(expectedCos, matrix.M11, 0.001f, "M11 should be cos(angle)");

        // M31 should be non-zero (sin component from rotation affects perspective row)
        Assert.AreNotEqual(0f, matrix.M31, "M31 should be non-zero for Y rotation");
    }

    [TestMethod]
    public void BuildMatrix_XRotation_ProducesNonIdentity()
    {
        float angle = 10f * MathF.PI / 180f;
        var matrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, 0f, angle, 2000f);

        float expectedCos = MathF.Cos(angle);
        Assert.AreEqual(expectedCos, matrix.M22, 0.001f, "M22 should be cos(angle)");
    }

    [TestMethod]
    public void BuildMatrix_CombinedRotation_ProducesValidMatrix()
    {
        float angleY = 8f * MathF.PI / 180f;
        float angleX = 5f * MathF.PI / 180f;
        var matrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, angleY, angleX, 2000f);

        // The matrix should be invertible (non-zero determinant)
        Assert.IsTrue(Matrix4x4.Invert(matrix, out _), "Transform matrix should be invertible");
    }

    [TestMethod]
    public void BuildMatrix_LargeDistance_SubtlePerspective()
    {
        // Large camera distance = subtle perspective
        var subtleMatrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, 0f, 0f, 10000f);
        var dramaticMatrix = PerspectiveTransform.BuildPerspectiveMatrix(1920, 1080, 0f, 0f, 500f);

        // M34 should be smaller (less perspective) for larger distance
        Assert.IsTrue(MathF.Abs(subtleMatrix.M34) < MathF.Abs(dramaticMatrix.M34),
            "Larger camera distance should produce subtler perspective");
    }

    #endregion

    #region Rotation Interpolation Tests

    private static MouseRecordingData BuildSilentRecording(double durationSeconds)
    {
        long startTick = 0;
        long endTick = (long)(durationSeconds * TickFrequency);
        int sampleCount = Math.Max(2, (int)(durationSeconds * 100));

        var samples = new List<MouseSample>();
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i * durationSeconds / (sampleCount - 1);
            samples.Add(new MouseSample
            {
                TimestampTicks = (long)(t * TickFrequency),
                X = 960, Y = 540,
                EventKind = MouseEventKind.Move,
                Button = MouseButton.None,
                ScrollDelta = 0,
            });
        }

        return new MouseRecordingData
        {
            Samples = samples,
            Clicks = [],
            StartTimestampTicks = startTick,
            EndTimestampTicks = endTick,
            TickFrequency = TickFrequency,
        };
    }

    [TestMethod]
    public void ManualKeyframe_RotationInterpolates_DuringPreDuration()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var recording = BuildSilentRecording(5.0);
        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        engine.AddManualKeyframe(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2.0),
            ZoomLevel = 2.0,
            CenterX = 0.5,
            CenterY = 0.5,
            PreDuration = TimeSpan.FromSeconds(0.5),
            HoldDuration = TimeSpan.FromSeconds(1.0),
            PostDuration = TimeSpan.FromSeconds(0.5),
            RotationY = 10f,
            RotationX = 5f,
            IsManual = true,
        });

        // Before segment: no rotation
        var before = engine.GetZoomState(1.0);
        Assert.AreEqual(0f, before.RotationY, 0.01f, "No rotation before segment");
        Assert.AreEqual(0f, before.RotationX, 0.01f);

        // During pre-duration (easing in): partial rotation
        var easing = engine.GetZoomState(1.75);
        Assert.IsTrue(easing.RotationY > 0f && easing.RotationY < 10f,
            $"RotationY should be partially eased, got {easing.RotationY}");

        // During hold: full rotation
        var hold = engine.GetZoomState(2.5);
        Assert.AreEqual(10f, hold.RotationY, 0.1f, "RotationY should be at target during hold");
        Assert.AreEqual(5f, hold.RotationX, 0.1f, "RotationX should be at target during hold");

        // During post-duration (easing out): partial rotation
        var easingOut = engine.GetZoomState(3.25);
        Assert.IsTrue(easingOut.RotationY > 0f && easingOut.RotationY < 10f,
            $"RotationY should be partially eased out, got {easingOut.RotationY}");

        // After segment: no rotation
        var after = engine.GetZoomState(4.0);
        Assert.AreEqual(0f, after.RotationY, 0.01f, "No rotation after segment");
        Assert.AreEqual(0f, after.RotationX, 0.01f);
    }

    [TestMethod]
    public void ZoomState_DefaultRotation_IsZero()
    {
        var state = new ZoomState();
        Assert.AreEqual(0f, state.RotationY);
        Assert.AreEqual(0f, state.RotationX);
    }

    [TestMethod]
    public void AutoSegments_HaveZeroRotation()
    {
        var config = new AutoZoomConfig { DefaultZoomLevel = 2.0f };
        var engine = new AutoZoomEngine(config);

        var clicks = new List<(double, int, int)> { (2.0, 500, 400) };
        long startTick = 0;
        long endTick = (long)(5.0 * TickFrequency);
        var samples = new List<MouseSample>();
        for (int i = 0; i < 500; i++)
        {
            double t = i * 5.0 / 499;
            samples.Add(new MouseSample
            {
                TimestampTicks = (long)(t * TickFrequency),
                X = 960, Y = 540,
                EventKind = MouseEventKind.Move,
                Button = MouseButton.None,
                ScrollDelta = 0,
            });
        }

        var clickEvents = clicks.Select(c => new ClickEvent(
            TimestampTicks: (long)(c.Item1 * TickFrequency),
            X: c.Item2, Y: c.Item3,
            Button: MouseButton.Left, IsDown: true
        )).ToList();

        var recording = new MouseRecordingData
        {
            Samples = samples,
            Clicks = clickEvents,
            StartTimestampTicks = startTick,
            EndTimestampTicks = endTick,
            TickFrequency = TickFrequency,
        };

        engine.BuildZoomTimeline(recording, 1920, 1080, TickFrequency);

        // During auto-zoom hold, rotation should be 0
        var state = engine.GetZoomState(2.1);
        Assert.IsTrue(state.ZoomLevel > 1.5f, "Auto-zoom should be active");
        Assert.AreEqual(0f, state.RotationY, 0.001f, "Auto segments have no Y rotation");
        Assert.AreEqual(0f, state.RotationX, 0.001f, "Auto segments have no X rotation");
    }

    #endregion
}
