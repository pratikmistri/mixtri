using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

[TestClass]
public sealed class TextSlideAnimationWindowTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void DefaultTextWindow_ResolvesToLegacyWholeSlideTiming()
    {
        var longSlide = new TextSlideSegment { Duration = S(10) };
        Assert.AreEqual(TimeSpan.Zero, longSlide.ResolveTextInStart());
        Assert.AreEqual(TimeSpan.FromMilliseconds(600), longSlide.ResolveTextInDuration());
        Assert.AreEqual(S(10), longSlide.ResolveTextOutEnd());
        Assert.AreEqual(TimeSpan.FromMilliseconds(600), longSlide.ResolveTextOutDuration());

        var shortSlide = new TextSlideSegment { Duration = S(1) };
        Assert.AreEqual(TimeSpan.Zero, shortSlide.ResolveTextInStart());
        Assert.AreEqual(TimeSpan.FromMilliseconds(450), shortSlide.ResolveTextInDuration());
        Assert.AreEqual(S(1), shortSlide.ResolveTextOutEnd());
        Assert.AreEqual(TimeSpan.FromMilliseconds(450), shortSlide.ResolveTextOutDuration());
    }

    [TestMethod]
    public void ResolveTextWindow_ClampsAdversarialInputWithoutThrowing()
    {
        var cases = new[]
        {
            new TextSlideSegment { Duration = S(5), TextInStart = S(4), TextOutEnd = S(2) },
            new TextSlideSegment
            {
                Duration = S(1),
                TextInStart = S(0.2),
                TextInDuration = S(0.9),
                TextOutEnd = S(0.7),
                TextOutDuration = S(0.9),
            },
            new TextSlideSegment
            {
                Duration = S(5),
                TextInStart = S(-1),
                TextInDuration = S(-0.1),
                TextOutEnd = S(-2),
                TextOutDuration = S(-0.3),
            },
            new TextSlideSegment
            {
                Duration = TimeSpan.Zero,
                TextInStart = S(-10),
                TextInDuration = S(-1),
                TextOutEnd = S(-5),
                TextOutDuration = S(-1),
            },
        };

        foreach (var slide in cases)
            AssertValidResolvedWindow(slide);
    }

    [TestMethod]
    public void AnimatedTextEngine_AutoWindowMatchesLegacyOverload()
    {
        foreach (double duration in new[] { 0, 0.05, 0.5, 1, 10 })
        {
            foreach (double progress in new[] { -0.2, 0, 0.1, 0.45, 0.5, 0.9, 1, 1.2 })
            {
                var legacy = AnimatedTextEngine.ComputeInOutProgress(progress, duration);
                var auto = AnimatedTextEngine.ComputeInOutProgress(
                    progress,
                    duration,
                    TextAnimationWindow.Auto(duration));

                Assert.AreEqual(legacy.InP, auto.InP, 1e-12, $"InP duration={duration} progress={progress}");
                Assert.AreEqual(legacy.OutP, auto.OutP, 1e-12, $"OutP duration={duration} progress={progress}");
            }
        }
    }

    [TestMethod]
    public void AnimatedTextEngine_CustomWindowGatesTextBeforeAndAfterWindow()
    {
        var window = new TextAnimationWindow(
            InStartSeconds: 2,
            InDurationSeconds: 1,
            OutEndSeconds: 7,
            OutDurationSeconds: 1);

        var before = AnimatedTextEngine.ComputeInOutProgress(0.19, 10, window);
        var after = AnimatedTextEngine.ComputeInOutProgress(0.8, 10, window);

        Assert.AreEqual(0, before.InP, 1e-12);
        Assert.AreEqual(1, after.OutP, 1e-12);
    }

    [TestMethod]
    public void SetTextSlideTextWindowOperation_EnforcesMinimumWindowAndUndoes()
    {
        var slide = new TextSlideSegment
        {
            Duration = S(2),
            TextInStart = S(0.5),
            TextOutEnd = S(1.5),
        };
        var model = new TimelineModel();
        model.Segments.Add(slide);

        var op = new SetTextSlideTextWindowOperation(slide.Id, S(1.9), S(1.95));
        op.Execute(model);

        Assert.AreEqual(S(1.8), slide.TextInStart);
        Assert.AreEqual(S(2), slide.TextOutEnd);

        op.Undo(model);

        Assert.AreEqual(S(0.5), slide.TextInStart);
        Assert.AreEqual(S(1.5), slide.TextOutEnd);
    }

    [TestMethod]
    public void UpdateTextSlideOperation_UpdatesAndUndoesTextWindowValues()
    {
        var slide = new TextSlideSegment
        {
            Text = "Before",
            Duration = S(5),
            TextInStart = S(0.2),
            TextInDuration = S(0.3),
            TextOutEnd = S(4),
            TextOutDuration = S(0.4),
        };
        var model = new TimelineModel();
        model.Segments.Add(slide);

        var op = new UpdateTextSlideOperation(
            slide.Id,
            "After", "Segoe UI", 72,
            isBold: false, isItalic: false,
            textColor: "#FFFFFF", backgroundColor: "#000000",
            duration: S(6), animation: TextSlideAnimation.ZoomBlurIn,
            textInStart: S(1), textInDuration: S(0.25),
            textOutEnd: S(3), textOutDuration: S(0.35));

        op.Execute(model);

        Assert.AreEqual(S(1), slide.TextInStart);
        Assert.AreEqual(S(0.25), slide.TextInDuration);
        Assert.AreEqual(S(3), slide.TextOutEnd);
        Assert.AreEqual(S(0.35), slide.TextOutDuration);

        op.Undo(model);

        Assert.AreEqual(S(0.2), slide.TextInStart);
        Assert.AreEqual(S(0.3), slide.TextInDuration);
        Assert.AreEqual(S(4), slide.TextOutEnd);
        Assert.AreEqual(S(0.4), slide.TextOutDuration);
    }

    private static void AssertValidResolvedWindow(TextSlideSegment slide)
    {
        var duration = slide.Duration < TimeSpan.Zero ? TimeSpan.Zero : slide.Duration;
        var inStart = slide.ResolveTextInStart();
        var inDuration = slide.ResolveTextInDuration();
        var outEnd = slide.ResolveTextOutEnd();
        var outDuration = slide.ResolveTextOutDuration();

        Assert.IsTrue(inStart >= TimeSpan.Zero);
        Assert.IsTrue(outEnd >= inStart);
        Assert.IsTrue(outEnd <= duration);
        Assert.IsTrue(inDuration >= TimeSpan.Zero);
        Assert.IsTrue(outDuration >= TimeSpan.Zero);
        Assert.IsTrue(inDuration + outDuration <= outEnd - inStart);
    }
}
