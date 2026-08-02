using Musio.Core.Processing;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Tests for the pure, Win2D-free pieces of the animated text overlay feature:
/// <see cref="TextOverlaySegment"/> defaults and <see cref="TextOverlaySegment.ResolveCenter"/>,
/// <see cref="TimelineModel.GetActiveTextOverlays"/> / <see cref="TimelineModel.GetTextOverlayProgress"/>,
/// and the static timing helpers on <see cref="AnimatedTextEngine"/>. Everything here is exercised
/// without a <c>CanvasDevice</c> — only the engine's static, device-free methods are covered.
/// </summary>
[TestClass]
public sealed class TextOverlayModelTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static TextOverlaySegment Overlay(double startSec, double durSec) => new()
    {
        Start = S(startSec),
        Duration = S(durSec),
    };

    // ── Defaults ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Defaults_AreSensible()
    {
        var overlay = new TextOverlaySegment();

        Assert.IsTrue(overlay.Enabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(overlay.Text));
        Assert.AreEqual(TextOverlayBackground.Solid, overlay.Background);
        Assert.AreEqual(TextOverlayAnchor.BottomCenter, overlay.Anchor);
        Assert.IsNull(overlay.SourceVideoFilePath);
    }

    // ── ResolveCenter: Custom ────────────────────────────────────────────

    [TestMethod]
    public void ResolveCenter_Custom_ReturnsStoredXYUnchanged()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(
            TextOverlayAnchor.Custom, x: 0.3, y: 0.7, margin: 0.1, boxWidthFraction: 0.5, boxHeightFraction: 0.2);

        Assert.AreEqual(0.3, x);
        Assert.AreEqual(0.7, y);
    }

    [TestMethod]
    public void ResolveCenter_Custom_ClampsOutOfRangeValues()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(
            TextOverlayAnchor.Custom, x: -0.5, y: 1.5, margin: 0.1, boxWidthFraction: 0.5, boxHeightFraction: 0.2);

        Assert.AreEqual(0.0, x);
        Assert.AreEqual(1.0, y);
    }

    // ── ResolveCenter: each of the nine anchors lands in the expected half/third ───

    [TestMethod]
    public void ResolveCenter_TopLeft_IsInUpperLeftQuadrant()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.TopLeft, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x < 0.5, $"x was {x}");
        Assert.IsTrue(y < 0.5, $"y was {y}");
    }

    [TestMethod]
    public void ResolveCenter_TopCenter_IsTopMiddle()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.TopCenter, 0, 0, 0.06, 0.3, 0.1);
        Assert.AreEqual(0.5, x);
        Assert.IsTrue(y < 0.5, $"y was {y}");
    }

    [TestMethod]
    public void ResolveCenter_TopRight_IsInUpperRightQuadrant()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.TopRight, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x > 0.5, $"x was {x}");
        Assert.IsTrue(y < 0.5, $"y was {y}");
    }

    [TestMethod]
    public void ResolveCenter_MiddleLeft_IsLeftMiddle()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleLeft, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x < 0.5, $"x was {x}");
        Assert.AreEqual(0.5, y);
    }

    [TestMethod]
    public void ResolveCenter_MiddleCenter_IsExactlyCentre()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleCenter, 0, 0, 0.06, 0.3, 0.1);
        Assert.AreEqual(0.5, x);
        Assert.AreEqual(0.5, y);
    }

    [TestMethod]
    public void ResolveCenter_MiddleRight_IsRightMiddle()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleRight, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x > 0.5, $"x was {x}");
        Assert.AreEqual(0.5, y);
    }

    [TestMethod]
    public void ResolveCenter_BottomLeft_IsInLowerLeftQuadrant()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.BottomLeft, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x < 0.5, $"x was {x}");
        Assert.IsTrue(y > 0.5, $"y was {y}");
    }

    [TestMethod]
    public void ResolveCenter_BottomCenter_IsBottomMiddle()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.BottomCenter, 0, 0, 0.06, 0.3, 0.1);
        Assert.AreEqual(0.5, x);
        Assert.IsTrue(y > 0.5, $"y was {y}");
    }

    [TestMethod]
    public void ResolveCenter_BottomRight_IsInLowerRightQuadrant()
    {
        var (x, y) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.BottomRight, 0, 0, 0.06, 0.3, 0.1);
        Assert.IsTrue(x > 0.5, $"x was {x}");
        Assert.IsTrue(y > 0.5, $"y was {y}");
    }

    // ── ResolveCenter: margin insets the edge further toward the centre ───

    [TestMethod]
    public void ResolveCenter_LargerMargin_MovesLeftAnchorFurtherTowardCentre()
    {
        var (xSmall, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleLeft, 0, 0, 0.05, 0.1, 0.1);
        var (xLarge, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleLeft, 0, 0, 0.20, 0.1, 0.1);

        Assert.IsTrue(xLarge > xSmall, $"xSmall={xSmall}, xLarge={xLarge}");
    }

    [TestMethod]
    public void ResolveCenter_LargerMargin_MovesTopAnchorFurtherTowardCentre()
    {
        var (_, ySmall) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.TopCenter, 0, 0, 0.05, 0.1, 0.1);
        var (_, yLarge) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.TopCenter, 0, 0, 0.20, 0.1, 0.1);

        Assert.IsTrue(yLarge > ySmall, $"ySmall={ySmall}, yLarge={yLarge}");
    }

    // ── ResolveCenter: the box's own size is accounted for ────────────────

    [TestMethod]
    public void ResolveCenter_WiderBox_MovesLeftAnchorFurtherIn()
    {
        var (xNarrow, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleLeft, 0, 0, 0.05, 0.1, 0.1);
        var (xWide, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleLeft, 0, 0, 0.05, 0.6, 0.1);

        Assert.IsTrue(xWide > xNarrow, $"xNarrow={xNarrow}, xWide={xWide}");
    }

    [TestMethod]
    public void ResolveCenter_WiderBox_MovesRightAnchorFurtherIn()
    {
        var (xNarrow, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleRight, 0, 0, 0.05, 0.1, 0.1);
        var (xWide, _) = TextOverlaySegment.ResolveCenter(TextOverlayAnchor.MiddleRight, 0, 0, 0.05, 0.6, 0.1);

        Assert.IsTrue(xWide < xNarrow, $"xNarrow={xNarrow}, xWide={xWide}");
    }

    // ── ResolveCenter: degenerate input ────────────────────────────────────

    [TestMethod]
    public void ResolveCenter_BoxWiderThanFrame_LeftAnchor_StillClampsInsideRange()
    {
        var (x, _) = TextOverlaySegment.ResolveCenter(
            TextOverlayAnchor.MiddleLeft, 0, 0, margin: 0.05, boxWidthFraction: 2.0, boxHeightFraction: 0.1);

        Assert.IsTrue(x is >= 0.0 and <= 1.0, $"x was {x}");
    }

    [TestMethod]
    public void ResolveCenter_BoxWiderThanFrame_RightAnchor_StillClampsInsideRange()
    {
        var (x, _) = TextOverlaySegment.ResolveCenter(
            TextOverlayAnchor.MiddleRight, 0, 0, margin: 0.05, boxWidthFraction: 2.0, boxHeightFraction: 0.1);

        Assert.IsTrue(x is >= 0.0 and <= 1.0, $"x was {x}");
    }

    [TestMethod]
    public void ResolveCenter_BoxTallerThanFrame_TopAnchor_StillClampsInsideRange()
    {
        var (_, y) = TextOverlaySegment.ResolveCenter(
            TextOverlayAnchor.TopCenter, 0, 0, margin: 0.05, boxWidthFraction: 0.1, boxHeightFraction: 2.0);

        Assert.IsTrue(y is >= 0.0 and <= 1.0, $"y was {y}");
    }

    // ── GetActiveTextOverlays: range boundaries ────────────────────────────

    [TestMethod]
    public void GetActiveTextOverlays_TimeInsideRange_IsReturned()
    {
        var model = new TimelineModel();
        var overlay = Overlay(2, 3); // [2,5)
        model.TextOverlays.Add(overlay);

        var active = model.GetActiveTextOverlays(S(3), null);

        Assert.AreEqual(1, active.Count);
        Assert.AreSame(overlay, active[0]);
    }

    [TestMethod]
    public void GetActiveTextOverlays_AtStart_IsIncluded_HalfOpenRange()
    {
        var model = new TimelineModel();
        model.TextOverlays.Add(Overlay(2, 3)); // [2,5)

        Assert.AreEqual(1, model.GetActiveTextOverlays(S(2), null).Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_AtEnd_IsExcluded_HalfOpenRange()
    {
        var model = new TimelineModel();
        model.TextOverlays.Add(Overlay(2, 3)); // [2,5)

        Assert.AreEqual(0, model.GetActiveTextOverlays(S(5), null).Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_JustBeforeEnd_IsIncluded()
    {
        var model = new TimelineModel();
        model.TextOverlays.Add(Overlay(2, 3)); // [2,5)

        Assert.AreEqual(1, model.GetActiveTextOverlays(S(4.99), null).Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_BeforeStart_IsExcluded()
    {
        var model = new TimelineModel();
        model.TextOverlays.Add(Overlay(2, 3)); // [2,5)

        Assert.AreEqual(0, model.GetActiveTextOverlays(S(1.99), null).Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_DisabledOverlay_IsSkipped()
    {
        var model = new TimelineModel();
        var overlay = Overlay(2, 3);
        overlay.Enabled = false;
        model.TextOverlays.Add(overlay);

        Assert.AreEqual(0, model.GetActiveTextOverlays(S(3), null).Count);
    }

    // ── GetActiveTextOverlays: source ownership ────────────────────────────

    [TestMethod]
    public void GetActiveTextOverlays_NullSource_MatchesPrimaryVideoFilePath()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        var overlay = Overlay(0, 5); // SourceVideoFilePath == null
        model.TextOverlays.Add(overlay);

        Assert.AreEqual(1, model.GetActiveTextOverlays(S(1), "primary.mp4").Count);
        Assert.AreEqual(0, model.GetActiveTextOverlays(S(1), "secondary.mp4").Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_NullSource_WhenPrimaryVideoFilePathIsNull_MatchesAnyVideoFilePath()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = null };
        var overlay = Overlay(0, 5); // SourceVideoFilePath == null
        model.TextOverlays.Add(overlay);

        Assert.AreEqual(1, model.GetActiveTextOverlays(S(1), "whatever.mp4").Count);
        Assert.AreEqual(1, model.GetActiveTextOverlays(S(1), null).Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_NamedSource_MatchesOnlyThatFile_CaseInsensitive()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        var overlay = Overlay(0, 5) with { SourceVideoFilePath = "Secondary.MP4" };
        model.TextOverlays.Add(overlay);

        Assert.AreEqual(1, model.GetActiveTextOverlays(S(1), "secondary.mp4").Count, "case-insensitive match expected");
        Assert.AreEqual(0, model.GetActiveTextOverlays(S(1), "primary.mp4").Count);
        Assert.AreEqual(0, model.GetActiveTextOverlays(S(1), "other.mp4").Count);
    }

    [TestMethod]
    public void GetActiveTextOverlays_PrimaryAndSecondaryOverlays_EachOnlyAppearsForItsOwnSource()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        var primaryOverlay = Overlay(0, 5);
        var secondaryOverlay = Overlay(0, 5) with { SourceVideoFilePath = "secondary.mp4" };
        model.TextOverlays.Add(primaryOverlay);
        model.TextOverlays.Add(secondaryOverlay);

        var forPrimary = model.GetActiveTextOverlays(S(1), "primary.mp4");
        Assert.AreEqual(1, forPrimary.Count);
        Assert.AreSame(primaryOverlay, forPrimary[0]);

        var forSecondary = model.GetActiveTextOverlays(S(1), "secondary.mp4");
        Assert.AreEqual(1, forSecondary.Count);
        Assert.AreSame(secondaryOverlay, forSecondary[0]);
    }

    [TestMethod]
    public void GetActiveTextOverlays_ReturnsInTimelineOrder()
    {
        var model = new TimelineModel();
        var first = Overlay(0, 10);
        var second = Overlay(1, 10);
        var third = Overlay(2, 10);
        model.TextOverlays.Add(first);
        model.TextOverlays.Add(second);
        model.TextOverlays.Add(third);

        var active = model.GetActiveTextOverlays(S(5), null);

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            active.Select(o => o.Id).ToArray());
    }

    // ── GetTextOverlayProgress ──────────────────────────────────────────

    [TestMethod]
    public void GetTextOverlayProgress_AtStart_IsZero()
    {
        var overlay = Overlay(2, 4);
        Assert.AreEqual(0.0, TimelineModel.GetTextOverlayProgress(overlay, S(2)));
    }

    [TestMethod]
    public void GetTextOverlayProgress_AtEnd_IsOne()
    {
        var overlay = Overlay(2, 4);
        Assert.AreEqual(1.0, TimelineModel.GetTextOverlayProgress(overlay, overlay.End));
    }

    [TestMethod]
    public void GetTextOverlayProgress_Midway_IsHalf()
    {
        var overlay = Overlay(2, 4);
        Assert.AreEqual(0.5, TimelineModel.GetTextOverlayProgress(overlay, S(4)));
    }

    [TestMethod]
    public void GetTextOverlayProgress_BeforeStart_ClampsToZero()
    {
        var overlay = Overlay(2, 4);
        Assert.AreEqual(0.0, TimelineModel.GetTextOverlayProgress(overlay, S(0)));
    }

    [TestMethod]
    public void GetTextOverlayProgress_AfterEnd_ClampsToOne()
    {
        var overlay = Overlay(2, 4);
        Assert.AreEqual(1.0, TimelineModel.GetTextOverlayProgress(overlay, S(100)));
    }

    [TestMethod]
    public void GetTextOverlayProgress_ZeroDurationOverlay_ReturnsZero_NoDivideByZero()
    {
        var overlay = Overlay(2, 0);
        Assert.AreEqual(0.0, TimelineModel.GetTextOverlayProgress(overlay, S(2)));
    }

    // ── AnimatedTextEngine.ComputeInOutProgress / ComputeEnvelope ─────────
    // Pure static methods: verified device-free (no CanvasDevice touched by these
    // members), so they are safe to test directly with no Win2D device.

    [TestMethod]
    public void ComputeInOutProgress_EntranceReachesFullAtSameElapsedTime_RegardlessOfDuration()
    {
        // Both durations are well above 2 * EntranceSeconds (0.6s * 2 = 1.2s), so the
        // entrance window is the full constant 0.6s for both, independent of duration.
        const double elapsedSeconds = 0.3; // half of the 0.6s entrance window
        double progressFor10s = elapsedSeconds / 10.0;
        double progressFor3s = elapsedSeconds / 3.0;

        var (inP10, _) = AnimatedTextEngine.ComputeInOutProgress(progressFor10s, 10.0);
        var (inP3, _) = AnimatedTextEngine.ComputeInOutProgress(progressFor3s, 3.0);

        Assert.AreEqual(inP10, inP3, 1e-9, $"inP10={inP10}, inP3={inP3}");
        Assert.AreEqual(0.5, inP10, 1e-9);
    }

    [TestMethod]
    public void ComputeInOutProgress_ShortDuration_PhasesShrinkProportionally_NoOverlapPastMidpoint()
    {
        // duration 0.4s < 2*0.6s: in/out windows are clamped to dur*0.45 = 0.18s each.
        const double duration = 0.4;

        // At the midpoint (elapsed = 0.2s) entrance has fully finished and exit has not
        // yet begun — the two phases never overlap past the midpoint.
        var (inMid, outMid) = AnimatedTextEngine.ComputeInOutProgress(0.5, duration);
        Assert.AreEqual(1.0, inMid, 1e-9);
        Assert.AreEqual(0.0, outMid, 1e-9);

        // Just before the midpoint (elapsed = 0.18s) entrance has just reached full.
        var (inAtWindowEnd, _) = AnimatedTextEngine.ComputeInOutProgress(0.18 / duration, duration);
        Assert.AreEqual(1.0, inAtWindowEnd, 1e-9);
    }

    [TestMethod]
    public void ComputeEnvelope_FadeIn_OpacityIsZeroAtStartAndEnd_OneInMiddle()
    {
        double opacityStart = AnimatedTextEngine.ComputeEnvelope(
            TextSlideAnimation.FadeIn, 0.0, 10.0, 1920, 1080).Opacity;
        double opacityMid = AnimatedTextEngine.ComputeEnvelope(
            TextSlideAnimation.FadeIn, 0.5, 10.0, 1920, 1080).Opacity;
        double opacityEnd = AnimatedTextEngine.ComputeEnvelope(
            TextSlideAnimation.FadeIn, 1.0, 10.0, 1920, 1080).Opacity;

        Assert.AreEqual(0.0, opacityStart, 1e-9, $"start opacity was {opacityStart}");
        Assert.AreEqual(1.0, opacityMid, 1e-9, $"mid opacity was {opacityMid}");
        Assert.AreEqual(0.0, opacityEnd, 1e-9, $"end opacity was {opacityEnd}");
    }

    [TestMethod]
    public void ComputeEnvelope_TypeWriter_OpacityIsAlwaysOne()
    {
        foreach (double progress in new[] { 0.0, 0.5, 1.0 })
        {
            var env = AnimatedTextEngine.ComputeEnvelope(TextSlideAnimation.TypeWriter, progress, 10.0, 1920, 1080);
            Assert.AreEqual(1.0, env.Opacity, $"progress={progress}");
        }
    }

    [TestMethod]
    public void ComputeEnvelope_TypewriterCaret_OpacityIsAlwaysOne()
    {
        foreach (double progress in new[] { 0.0, 0.5, 1.0 })
        {
            var env = AnimatedTextEngine.ComputeEnvelope(TextSlideAnimation.TypewriterCaret, progress, 10.0, 1920, 1080);
            Assert.AreEqual(1.0, env.Opacity, $"progress={progress}");
        }
    }

    [TestMethod]
    public void ComputeEnvelope_PerCharacterAnimation_ReturnsIdentityTransform()
    {
        Assert.IsTrue(AnimatedTextEngine.IsPerCharacter(TextSlideAnimation.CascadeFadeUp));

        var env = AnimatedTextEngine.ComputeEnvelope(TextSlideAnimation.CascadeFadeUp, 0.5, 10.0, 1920, 1080);

        Assert.AreEqual(1f, env.Scale);
        Assert.AreEqual(0f, env.Tx);
        Assert.AreEqual(0f, env.Ty);
    }

    [TestMethod]
    public void IsPerCharacter_ReturnsFalseForWholeTextAnimations()
    {
        Assert.IsFalse(AnimatedTextEngine.IsPerCharacter(TextSlideAnimation.FadeIn));
        Assert.IsFalse(AnimatedTextEngine.IsPerCharacter(TextSlideAnimation.SlideUp));
        Assert.IsFalse(AnimatedTextEngine.IsPerCharacter(TextSlideAnimation.TypeWriter));
    }
}
