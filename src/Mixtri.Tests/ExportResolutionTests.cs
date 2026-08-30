using Mixtri.Core.Export;
using Mixtri.Core.Settings;

namespace Mixtri.Tests;

[TestClass]
public sealed class ExportResolutionTests
{
    [TestMethod]
    [DataRow(VideoResolution.HD720, 1280, 720)]
    [DataRow(VideoResolution.HD1080, 1920, 1080)]
    [DataRow(VideoResolution.QHD, 2560, 1440)]
    [DataRow(VideoResolution.UHD4K, 3840, 2160)]
    public void GetResolutionDimensions_ReturnsExpected(VideoResolution resolution, int expectedW, int expectedH)
    {
        var (w, h) = ExportEngine.GetResolutionDimensions(resolution);
        Assert.AreEqual(expectedW, w);
        Assert.AreEqual(expectedH, h);
    }

    [TestMethod]
    public void GetResolutionDimensions_UnknownValue_FallsBackTo1080p()
    {
        var (w, h) = ExportEngine.GetResolutionDimensions((VideoResolution)999);
        Assert.AreEqual(1920, w);
        Assert.AreEqual(1080, h);
    }

    [TestMethod]
    public void GetResolutionDimensions_AllValues_HavePositiveDimensions()
    {
        foreach (var res in Enum.GetValues<VideoResolution>())
        {
            var (w, h) = ExportEngine.GetResolutionDimensions(res);
            Assert.IsTrue(w > 0 && h > 0, $"{res} should have positive dimensions");
            Assert.AreEqual(0, w % 2, $"{res} width should be even");
            Assert.AreEqual(0, h % 2, $"{res} height should be even");
        }
    }
}
