using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Verifies the per-recording tagging of zoom keyframes via
/// <see cref="ZoomKeyframe.SourceVideoFilePath"/> (null = primary), which lets
/// appended recordings carry their own zoom segments independently of the primary.
/// </summary>
[TestClass]
public sealed class ZoomKeyframeSourceFileTests
{
    [TestMethod]
    public void Default_SourceVideoFilePath_IsNull()
    {
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1) };
        Assert.IsNull(kf.SourceVideoFilePath);
    }

    [TestMethod]
    public void With_SetsSourceVideoFilePath()
    {
        var kf = ZoomKeyframe.FromRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2.0)
            with { SourceVideoFilePath = "appended.mp4" };
        Assert.AreEqual("appended.mp4", kf.SourceVideoFilePath);
        Assert.IsTrue(kf.IsManual);
    }

    [TestMethod]
    public void Model_CanHoldPrimaryAndAppendedKeyframes_FilteredByFile()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1) }); // primary
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), SourceVideoFilePath = "appended.mp4" });

        var primary = model.ZoomKeyframes.Where(k => k.SourceVideoFilePath is null).ToList();
        var appended = model.ZoomKeyframes.Where(k => k.SourceVideoFilePath == "appended.mp4").ToList();

        Assert.AreEqual(1, primary.Count);
        Assert.AreEqual(1, appended.Count);
    }

    [TestMethod]
    public void RemoveAll_ByFile_LeavesPrimaryIntact()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1) }); // primary
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), SourceVideoFilePath = "a.mp4" });
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(3), SourceVideoFilePath = "a.mp4" });

        model.ZoomKeyframes.RemoveAll(k => k.SourceVideoFilePath == "a.mp4");

        Assert.AreEqual(1, model.ZoomKeyframes.Count);
        Assert.IsNull(model.ZoomKeyframes[0].SourceVideoFilePath);
    }

    [TestMethod]
    public void ClearPrimaryOnly_KeepsAppended()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = "primary.mp4" };
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(1) }); // primary
        model.ZoomKeyframes.Add(new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), SourceVideoFilePath = "a.mp4" });

        model.ZoomKeyframes.RemoveAll(k => k.SourceVideoFilePath is null);

        Assert.AreEqual(1, model.ZoomKeyframes.Count);
        Assert.AreEqual("a.mp4", model.ZoomKeyframes[0].SourceVideoFilePath);
    }
}
