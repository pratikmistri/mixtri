using Musio.Core.Models;
using Musio.Core.Processing;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="CursorPathWarp"/> — the displacement field behind repositioning the
/// cursor at a moment.
/// </summary>
/// <remarks>
/// The feature's whole promise is "the rest of the mouse journey stays intact", so most of
/// these tests assert what the warp does NOT do. Pure maths, no GPU.
/// </remarks>
[TestClass]
public sealed class CursorPathWarpTests
{
    private const double Fps = 30.0;
    private const int FrameCount = 300; // 10 seconds

    /// <summary>A diagonal sweep, so every frame has a distinct position and a real velocity.</summary>
    private static List<SmoothedPosition> BuildPath(int frames = FrameCount)
    {
        var path = new List<SmoothedPosition>(frames);
        for (int i = 0; i < frames; i++)
        {
            path.Add(new SmoothedPosition
            {
                X = 100 + (i * 2.0),
                Y = 50 + (i * 1.0),
                TimestampSeconds = i / Fps,
                VelocityX = 2.0 * Fps,
                VelocityY = 1.0 * Fps,
                Shape = CursorShape.Arrow,
            });
        }
        return path;
    }

    private static void AssertSamePosition(
        SmoothedPosition expected, SmoothedPosition actual, int index, string what)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-9, $"{what}: X at frame {index}");
        Assert.AreEqual(expected.Y, actual.Y, 1e-9, $"{what}: Y at frame {index}");
    }

    [TestMethod]
    public void Apply_WithNoAnchors_LeavesThePathExactlyAsRecorded()
    {
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(basePath, [], [60, 120], Fps);

        Assert.AreEqual(basePath.Count, result.Count);
        for (int i = 0; i < basePath.Count; i++)
        {
            AssertSamePosition(basePath[i], result[i], i, "no anchors");
            Assert.AreEqual(basePath[i].VelocityX, result[i].VelocityX, 1e-9, $"VelocityX at {i}");
            Assert.AreEqual(basePath[i].VelocityY, result[i].VelocityY, 1e-9, $"VelocityY at {i}");
        }
    }

    [TestMethod]
    public void Apply_PutsTheCursorExactlyOnTheAnchorTarget()
    {
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], [], Fps);

        Assert.AreEqual(900, result[150].X, 1e-9);
        Assert.AreEqual(700, result[150].Y, 1e-9);
    }

    [TestMethod]
    public void Apply_LeavesEveryFrameOutsideTheNeighbouringClicksUntouched()
    {
        // Clicks at 100 and 200 bound the anchor at 150, so the influence window is exactly
        // (100, 200) — this is the "rest of the journey stays intact" guarantee.
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath,
            [new CursorPathWarp.AnchorPoint(150, 900, 700)],
            [100, 200],
            Fps);

        for (int i = 0; i <= 100; i++)
            AssertSamePosition(basePath[i], result[i], i, "before the preceding click");

        for (int i = 200; i < FrameCount; i++)
            AssertSamePosition(basePath[i], result[i], i, "after the following click");

        Assert.AreNotEqual(basePath[150].X, result[150].X, "the anchored frame must actually move");
    }

    [TestMethod]
    public void Apply_NeverMovesTheCursorOffAClick()
    {
        // Q3: a click is a moment the recording, the touch indicator and auto-zoom all agree
        // on, so the displacement is pinned to zero there no matter how near an anchor is.
        var basePath = BuildPath();
        int[] clicks = [140, 160, 250];

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], clicks, Fps);

        foreach (int click in clicks)
            AssertSamePosition(basePath[click], result[click], click, "click");
    }

    [TestMethod]
    public void Apply_WithNoClicks_BlendsOutToBothEndsOfTheRecording()
    {
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], [], Fps);

        AssertSamePosition(basePath[0], result[0], 0, "first frame");
        AssertSamePosition(basePath[^1], result[^1], FrameCount - 1, "last frame");
    }

    [TestMethod]
    public void Apply_WithTwoAnchors_HitsBothTargetsExactly()
    {
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath,
            [
                new CursorPathWarp.AnchorPoint(100, 400, 400),
                new CursorPathWarp.AnchorPoint(200, 800, 200),
            ],
            [],
            Fps);

        Assert.AreEqual(400, result[100].X, 1e-9);
        Assert.AreEqual(400, result[100].Y, 1e-9);
        Assert.AreEqual(800, result[200].X, 1e-9);
        Assert.AreEqual(200, result[200].Y, 1e-9);
    }

    [TestMethod]
    public void Apply_ProducesNoJumpAnywhereAlongThePath()
    {
        // Smoothstep has zero derivative at both ends of each span, so the field is C1 across
        // every control point. A discontinuity here would read as the cursor teleporting.
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath,
            [new CursorPathWarp.AnchorPoint(150, 900, 700)],
            [100, 200],
            Fps);

        double maxBaseStep = 0;
        double maxWarpedStep = 0;
        for (int i = 1; i < result.Count; i++)
        {
            maxBaseStep = Math.Max(maxBaseStep, Distance(basePath[i - 1], basePath[i]));
            maxWarpedStep = Math.Max(maxWarpedStep, Distance(result[i - 1], result[i]));
        }

        // The warp spreads ~700px over 50 frames, so a smooth field peaks well under the
        // per-frame budget below; a hard switch at a control point would blow straight past it.
        Assert.IsTrue(
            maxWarpedStep < maxBaseStep + 40,
            $"warped path jumps {maxWarpedStep:F1}px in one frame (base {maxBaseStep:F1}px)");

        static double Distance(SmoothedPosition a, SmoothedPosition b)
            => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    }

    [TestMethod]
    public void Apply_AnchorOnAClick_WinsOverTheProtectRule()
    {
        // Dragging the cursor at a click is unambiguous intent. Letting the click's zero-node
        // win instead would make the drag look silently ignored.
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], [150], Fps);

        Assert.AreEqual(900, result[150].X, 1e-9);
        Assert.AreEqual(700, result[150].Y, 1e-9);
    }

    [TestMethod]
    public void Apply_CorrectsVelocityInsideTheWarpAndLeavesItAloneOutside()
    {
        // Velocity drives cursor tilt and shutter motion blur. Displacing positions without it
        // would make the pointer lean along its recorded direction while visibly moving along
        // a different one.
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath,
            [new CursorPathWarp.AnchorPoint(150, 900, 700)],
            [100, 200],
            Fps);

        Assert.AreNotEqual(
            basePath[130].VelocityX, result[130].VelocityX,
            "velocity must follow the displacement inside the window");

        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(basePath[i].VelocityX, result[i].VelocityX, 1e-9, $"VelocityX at {i}");
            Assert.AreEqual(basePath[i].VelocityY, result[i].VelocityY, 1e-9, $"VelocityY at {i}");
        }
    }

    [TestMethod]
    public void Apply_PreservesTimestampsAndShapes()
    {
        var basePath = BuildPath();
        basePath[150] = basePath[150] with { Shape = CursorShape.Hand };

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], [], Fps);

        Assert.AreEqual(CursorShape.Hand, result[150].Shape);
        for (int i = 0; i < result.Count; i++)
            Assert.AreEqual(basePath[i].TimestampSeconds, result[i].TimestampSeconds, 1e-9, $"time at {i}");
    }

    [TestMethod]
    public void Apply_ClampsAnAnchorPastTheEndOfThePathInsteadOfThrowing()
    {
        var basePath = BuildPath();

        var result = CursorPathWarp.Apply(
            basePath, [new CursorPathWarp.AnchorPoint(FrameCount + 500, 900, 700)], [], Fps);

        Assert.AreEqual(FrameCount, result.Count);
        Assert.AreEqual(900, result[^1].X, 1e-9);
    }

    [TestMethod]
    public void Apply_OnAnEmptyPath_ReturnsEmptyWithoutThrowing()
    {
        var result = CursorPathWarp.Apply([], [new CursorPathWarp.AnchorPoint(0, 1, 1)], [], Fps);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Apply_DoesNotMutateTheBasePath()
    {
        // The compositor re-warps from this list on every anchor change; mutating it would
        // compound each drag on top of the last.
        var basePath = BuildPath();
        var snapshot = BuildPath();

        CursorPathWarp.Apply(basePath, [new CursorPathWarp.AnchorPoint(150, 900, 700)], [], Fps);

        for (int i = 0; i < basePath.Count; i++)
            AssertSamePosition(snapshot[i], basePath[i], i, "base path");
    }
}
