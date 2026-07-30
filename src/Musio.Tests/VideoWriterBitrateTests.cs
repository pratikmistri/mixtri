using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio.Tests;

[TestClass]
public class VideoWriterBitrateTests
{
    private const uint Width1080p = 1920;
    private const uint Height1080p = 1080;

    [TestMethod]
    public void HigherQualityLevels_ProduceHigherBitrates()
    {
        uint balanced = VideoWriter.ComputeCaptureBitrate(
            Width1080p, Height1080p, CaptureQuality.Balanced);
        uint high = VideoWriter.ComputeCaptureBitrate(
            Width1080p, Height1080p, CaptureQuality.HighFidelity);
        uint master = VideoWriter.ComputeCaptureBitrate(
            Width1080p, Height1080p, CaptureQuality.Master);

        Assert.IsTrue(balanced < high, "Balanced should be below HighFidelity");
        Assert.IsTrue(high < master, "HighFidelity should be below Master");
    }

    [TestMethod]
    public void BaseRates_AreQuotedAt1080p()
    {
        Assert.AreEqual(12_000_000u,
            VideoWriter.ComputeCaptureBitrate(Width1080p, Height1080p, CaptureQuality.Balanced));
        Assert.AreEqual(30_000_000u,
            VideoWriter.ComputeCaptureBitrate(Width1080p, Height1080p, CaptureQuality.HighFidelity));
        Assert.AreEqual(60_000_000u,
            VideoWriter.ComputeCaptureBitrate(Width1080p, Height1080p, CaptureQuality.Master));
    }

    [TestMethod]
    public void BitrateScalesWithPixelCount()
    {
        uint hd = VideoWriter.ComputeCaptureBitrate(
            Width1080p, Height1080p, CaptureQuality.HighFidelity);
        uint uhd = VideoWriter.ComputeCaptureBitrate(3840, 2160, CaptureQuality.HighFidelity);

        Assert.AreEqual(hd * 4, uhd, "4K has four times the pixels of 1080p");
    }

    [TestMethod]
    public void SmallCaptures_AreNotStarvedBelowHalfTheBaseRate()
    {
        // A tiny region capture still needs enough bitrate to stay sharp on text.
        uint tiny = VideoWriter.ComputeCaptureBitrate(320, 200, CaptureQuality.HighFidelity);

        Assert.AreEqual(15_000_000u, tiny);
    }

    [TestMethod]
    public void HugeCaptures_AreCappedToAvoidAbsurdBitrates()
    {
        uint huge = VideoWriter.ComputeCaptureBitrate(15360, 8640, CaptureQuality.Master);

        Assert.AreEqual(60_000_000u * 8, huge, "scale is clamped at 8x");
    }

    [TestMethod]
    public void DefaultCaptureQuality_IsHighFidelity()
    {
        // The recording MP4 is the durable master once .frames/ is released, so the
        // default must leave headroom for repeated re-exports.
        Assert.AreEqual(CaptureQuality.HighFidelity, new RecordingSessionConfig().CaptureQuality);
    }
}
