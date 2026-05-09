using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public sealed class ZoomKeyframeExtendedTests
{
    #region FromRange

    [TestMethod]
    public void FromRange_StartAndEnd_MatchInput()
    {
        var start = TimeSpan.FromSeconds(2);
        var end = TimeSpan.FromSeconds(6);

        var kf = ZoomKeyframe.FromRange(start, end, 2.5);

        Assert.AreEqual(start, kf.Start, $"Start should be {start}, got {kf.Start}");
        Assert.AreEqual(end, kf.End, $"End should be {end}, got {kf.End}");
    }

    [TestMethod]
    public void FromRange_ZoomLevel_IsRetained()
    {
        var kf = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 3.0);
        Assert.AreEqual(3.0, kf.ZoomLevel);
    }

    [TestMethod]
    public void FromRange_CenterDefaults_AreMidpoint()
    {
        var kf = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), 2.0);
        Assert.AreEqual(0.5, kf.CenterX);
        Assert.AreEqual(0.5, kf.CenterY);
    }

    [TestMethod]
    public void FromRange_CustomCenter_IsRetained()
    {
        var kf = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), 2.0,
            centerX: 0.8, centerY: 0.2);
        Assert.AreEqual(0.8, kf.CenterX);
        Assert.AreEqual(0.2, kf.CenterY);
    }

    [TestMethod]
    public void FromRange_IsManual_True()
    {
        var kf = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), 2.0);
        Assert.IsTrue(kf.IsManual);
    }

    [TestMethod]
    public void FromRange_HasUniqueId()
    {
        var kf1 = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), 2.0);
        var kf2 = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), 2.0);
        Assert.AreNotEqual(kf1.Id, kf2.Id, "Each keyframe should have a unique Id");
    }

    #endregion

    #region TotalDuration

    [TestMethod]
    public void TotalDuration_SumsAllPhases()
    {
        var kf = new ZoomKeyframe
        {
            PreDuration = TimeSpan.FromMilliseconds(200),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(300),
        };

        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), kf.TotalDuration);
    }

    #endregion

    #region Start / End Computed Properties

    [TestMethod]
    public void Start_IsTimestampMinusPreDuration()
    {
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            PreDuration = TimeSpan.FromSeconds(1),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(4), kf.Start);
    }

    [TestMethod]
    public void End_IsTimestampPlusHoldPlusPost()
    {
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(6), kf.End);
    }

    #endregion

    #region MinSegmentDuration

    [TestMethod]
    public void MinSegmentDuration_IsPositive()
    {
        Assert.IsTrue(ZoomKeyframe.MinSegmentDuration > TimeSpan.Zero,
            "MinSegmentDuration should be positive");
    }

    #endregion

    #region TimelineClip Properties

    [TestMethod]
    public void TimelineClip_SourceDuration_DefaultSpeed_EqualsDuration()
    {
        var clip = new TimelineClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10), "Test");

        Assert.AreEqual(TimeSpan.FromSeconds(10), clip.SourceDuration);
    }

    [TestMethod]
    public void TimelineClip_SourceDuration_DoubleSpeed_DoubleSourceDuration()
    {
        var clip = new TimelineClip(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "Fast")
        {
            SpeedFactor = 2.0,
        };

        // 5s output at 2x speed = 10s of source
        Assert.AreEqual(TimeSpan.FromSeconds(10), clip.SourceDuration);
    }

    [TestMethod]
    public void TimelineClip_EffectiveSourceStart_DefaultsToStart()
    {
        var clip = new TimelineClip(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Test");

        Assert.AreEqual(TimeSpan.FromSeconds(5), clip.EffectiveSourceStart);
    }

    [TestMethod]
    public void TimelineClip_EffectiveSourceStart_UsesSourceStartWhenSet()
    {
        var clip = new TimelineClip(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Test")
        {
            SourceStart = TimeSpan.FromSeconds(5),
        };

        Assert.AreEqual(TimeSpan.FromSeconds(5), clip.EffectiveSourceStart);
    }

    [TestMethod]
    public void TimelineClip_SpeedFactor_DefaultsToOne()
    {
        var clip = new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Test");
        Assert.AreEqual(1.0, clip.SpeedFactor);
    }

    #endregion
}
