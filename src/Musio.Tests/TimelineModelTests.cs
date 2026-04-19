namespace Musio.Tests;

using Musio.Core.Timeline;

[TestClass]
public sealed class TimelineModelTests
{
    #region EffectiveDuration

    [TestMethod]
    public void EffectiveDuration_NoTrim_EqualsFullDuration()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(10), model.EffectiveDuration);
    }

    [TestMethod]
    public void EffectiveDuration_WithTrimStart_ReducesDuration()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(2),
            TrimEnd = TimeSpan.FromSeconds(10),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(8), model.EffectiveDuration);
    }

    [TestMethod]
    public void EffectiveDuration_WithTrimEnd_ReducesDuration()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(7),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(7), model.EffectiveDuration);
    }

    [TestMethod]
    public void EffectiveDuration_BothTrims_CalculatesCorrectly()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(60),
            TrimStart = TimeSpan.FromSeconds(10),
            TrimEnd = TimeSpan.FromSeconds(50),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(40), model.EffectiveDuration);
    }

    [TestMethod]
    public void EffectiveDuration_ZeroLength_ReturnsZero()
    {
        var model = new TimelineModel
        {
            TrimStart = TimeSpan.FromSeconds(5),
            TrimEnd = TimeSpan.FromSeconds(5),
        };

        Assert.AreEqual(TimeSpan.Zero, model.EffectiveDuration);
    }

    #endregion

    #region TimelineModel Defaults

    [TestMethod]
    public void TimelineModel_Defaults_AreCorrect()
    {
        var model = new TimelineModel();

        Assert.AreEqual(TimeSpan.Zero, model.Duration);
        Assert.AreEqual(30, model.Fps);
        Assert.AreEqual(TimeSpan.Zero, model.PlayheadPosition);
        Assert.AreEqual(1.0, model.ZoomLevel);
        Assert.AreEqual(0.0, model.ScrollOffset);
        Assert.AreEqual(0, model.Clips.Count);
        Assert.AreEqual(0, model.ZoomKeyframes.Count);
        Assert.AreEqual(0, model.SpeedSegments.Count);
    }

    #endregion

    #region ZoomKeyframe

    [TestMethod]
    public void ZoomKeyframe_DefaultValues_AreCorrect()
    {
        var kf = new ZoomKeyframe();

        Assert.AreEqual(TimeSpan.Zero, kf.Timestamp);
        Assert.AreEqual(2.0, kf.ZoomLevel);
        Assert.AreEqual(0.0, kf.CenterX);
        Assert.AreEqual(0.0, kf.CenterY);
        Assert.AreEqual(TimeSpan.FromMilliseconds(300), kf.PreDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), kf.HoldDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), kf.PostDuration);
    }

    [TestMethod]
    public void ZoomKeyframe_CustomValues_AreRetained()
    {
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            ZoomLevel = 3.5,
            CenterX = 0.75,
            CenterY = 0.25,
            PreDuration = TimeSpan.FromMilliseconds(200),
            HoldDuration = TimeSpan.FromSeconds(1),
            PostDuration = TimeSpan.FromMilliseconds(400),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(5), kf.Timestamp);
        Assert.AreEqual(3.5, kf.ZoomLevel);
        Assert.AreEqual(0.75, kf.CenterX);
        Assert.AreEqual(0.25, kf.CenterY);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), kf.PreDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(1), kf.HoldDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), kf.PostDuration);
    }

    [TestMethod]
    public void ZoomKeyframe_RecordEquality_WorksOnValues()
    {
        var id = "test-id";
        var a = new ZoomKeyframe { Id = id, Timestamp = TimeSpan.FromSeconds(1), ZoomLevel = 2.0, CenterX = 0.5, CenterY = 0.5 };
        var b = new ZoomKeyframe { Id = id, Timestamp = TimeSpan.FromSeconds(1), ZoomLevel = 2.0, CenterX = 0.5, CenterY = 0.5 };

        Assert.AreEqual(a, b);
    }

    #endregion

    #region SpeedSegment

    [TestMethod]
    public void SpeedSegment_Construction_HasCorrectValues()
    {
        var segment = new SpeedSegment(
            Start: TimeSpan.FromSeconds(2),
            End: TimeSpan.FromSeconds(8),
            Speed: 0.5);

        Assert.AreEqual(TimeSpan.FromSeconds(2), segment.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(8), segment.End);
        Assert.AreEqual(0.5, segment.Speed);
    }

    [TestMethod]
    public void SpeedSegment_Equality_WorksForRecords()
    {
        var a = new SpeedSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), 2.0);
        var b = new SpeedSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), 2.0);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void SpeedSegment_DifferentSpeed_NotEqual()
    {
        var a = new SpeedSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), 1.0);
        var b = new SpeedSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), 2.0);

        Assert.AreNotEqual(a, b);
    }

    #endregion

    #region TimelineClip

    [TestMethod]
    public void TimelineClip_Construction_HasCorrectValues()
    {
        var clip = new TimelineClip(
            Start: TimeSpan.FromSeconds(1),
            End: TimeSpan.FromSeconds(5),
            Label: "Intro");

        Assert.AreEqual(TimeSpan.FromSeconds(1), clip.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(5), clip.End);
        Assert.AreEqual("Intro", clip.Label);
    }

    [TestMethod]
    public void TimelineClip_Equality_WorksForRecords()
    {
        var a = new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Clip");
        var b = new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Clip");

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void TimelineModel_AddClipsAndKeyframes_Works()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(30),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(30),
        };

        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Part 1"));
        model.Clips.Add(new TimelineClip(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Part 2"));
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(5), ZoomLevel = 2.5 });
        model.SpeedSegments.Add(new SpeedSegment(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), 0.5));

        Assert.AreEqual(2, model.Clips.Count);
        Assert.AreEqual(1, model.ZoomKeyframes.Count);
        Assert.AreEqual(1, model.SpeedSegments.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(30), model.EffectiveDuration);
    }

    #endregion
}
