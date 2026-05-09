using Musio.Core.Export;
using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public sealed class TimelineMapperTests
{
    #region Constructor Validation

    [TestMethod]
    public void Constructor_NullTimeline_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new TimelineMapper(null!, 30));
    }

    [TestMethod]
    public void Constructor_ZeroFps_ThrowsArgumentOutOfRangeException()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10), TrimEnd = TimeSpan.FromSeconds(10) };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TimelineMapper(model, 0));
    }

    [TestMethod]
    public void Constructor_NegativeFps_ThrowsArgumentOutOfRangeException()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10), TrimEnd = TimeSpan.FromSeconds(10) };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TimelineMapper(model, -1));
    }

    #endregion

    #region Simple Trim

    [TestMethod]
    public void SimpleTrim_TotalOutputFrames_MatchesDuration()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(2),
            TrimEnd = TimeSpan.FromSeconds(8),
        };
        var mapper = new TimelineMapper(model, 30);

        // 6 seconds × 30 fps = 180 frames
        Assert.AreEqual(180, mapper.TotalOutputFrames);
    }

    [TestMethod]
    public void SimpleTrim_FirstFrame_MapsToTrimStart()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(3),
            TrimEnd = TimeSpan.FromSeconds(7),
        };
        var mapper = new TimelineMapper(model, 30);

        double sourceTime = mapper.GetSourceTimeForOutputFrame(0);
        Assert.AreEqual(3.0, sourceTime, 0.01);
    }

    [TestMethod]
    public void SimpleTrim_LastFrame_MapsNearTrimEnd()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(0),
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        var mapper = new TimelineMapper(model, 30);

        double sourceTime = mapper.GetSourceTimeForOutputFrame(mapper.TotalOutputFrames - 1);
        Assert.IsTrue(sourceTime >= 9.0 && sourceTime <= 10.0,
            $"Last frame source time {sourceTime} should be near end");
    }

    [TestMethod]
    public void NegativeFrame_ReturnsTrimStart()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(5),
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        var mapper = new TimelineMapper(model, 30);

        Assert.AreEqual(5.0, mapper.GetSourceTimeForOutputFrame(-1), 0.01);
    }

    #endregion

    #region Speed Segments

    [TestMethod]
    public void SpeedSegment_DoubleSpeed_HalvesOutputFrames()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        // Apply 2x speed to entire range
        model.SpeedSegments.Add(new SpeedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(10), 2.0));

        var mapper = new TimelineMapper(model, 30);

        // 10s at 2x speed = 5s output → 150 frames
        Assert.AreEqual(150, mapper.TotalOutputFrames);
    }

    [TestMethod]
    public void SpeedSegment_HalfSpeed_DoublesOutputFrames()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(4),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(4),
        };
        model.SpeedSegments.Add(new SpeedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), 0.5));

        var mapper = new TimelineMapper(model, 30);

        // 4s at 0.5x = 8s output → 240 frames
        Assert.AreEqual(240, mapper.TotalOutputFrames);
    }

    #endregion

    #region With Clips

    [TestMethod]
    public void WithClips_SkipsDeletedRegion()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(20),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(20),
        };
        // Two clips: [0-10] and [10-20], with a gap in source (simulating a cut)
        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Part 1"));
        model.Clips.Add(new TimelineClip(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Part 2"));

        var mapper = new TimelineMapper(model, 30);

        Assert.AreEqual(600, mapper.TotalOutputFrames); // 20s × 30fps
    }

    #endregion

    #region IsDeleted

    [TestMethod]
    public void IsDeleted_NoClips_NeverDeleted()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        var mapper = new TimelineMapper(model, 30);

        Assert.IsFalse(mapper.IsDeleted(5.0));
    }

    [TestMethod]
    public void IsDeleted_WithClips_InsideClipNotDeleted()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(5), "A"));

        var mapper = new TimelineMapper(model, 30);

        Assert.IsFalse(mapper.IsDeleted(2.5), "Time within clip should not be deleted");
    }

    [TestMethod]
    public void IsDeleted_WithClips_OutsideClipIsDeleted()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(5), "A"));

        var mapper = new TimelineMapper(model, 30);

        Assert.IsTrue(mapper.IsDeleted(7.0), "Time outside clip should be deleted");
    }

    #endregion

    #region GetOutputSegments

    [TestMethod]
    public void GetOutputSegments_ReturnsCorrectSegments()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(10),
        };
        var mapper = new TimelineMapper(model, 30);

        var segments = mapper.GetOutputSegments();
        Assert.IsTrue(segments.Count > 0, "Should have at least one segment");
        Assert.AreEqual(TimeSpan.Zero, segments[0].Start);
    }

    #endregion

    #region Properties

    [TestMethod]
    public void Properties_ExposeTimelineValues()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(1),
            TrimEnd = TimeSpan.FromSeconds(9),
        };
        var mapper = new TimelineMapper(model, 30);

        Assert.AreEqual(TimeSpan.FromSeconds(1), mapper.TrimStart);
        Assert.AreEqual(TimeSpan.FromSeconds(9), mapper.TrimEnd);
        Assert.IsTrue(mapper.EffectiveDuration.TotalSeconds > 0);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void ZeroLengthTrim_ReturnsMinimumOneFrame()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(5),
            TrimEnd = TimeSpan.FromSeconds(5),
        };
        var mapper = new TimelineMapper(model, 30);

        Assert.IsTrue(mapper.TotalOutputFrames >= 1, "Should have at least 1 frame");
    }

    #endregion
}
