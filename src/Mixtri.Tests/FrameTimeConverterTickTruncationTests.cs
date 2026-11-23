using Mixtri.Core.Processing;

namespace Mixtri.Tests;

/// <summary>
/// Frame times round-trip through <see cref="TimeSpan"/>, which truncates to 100 ns ticks, so a
/// frame's own start time used to resolve to the PREVIOUS frame for most indices — duplicated
/// and skipped frames on every export.
/// </summary>
[TestClass]
public sealed class FrameTimeConverterTickTruncationTests
{
    [TestMethod]
    [DataRow(24)]
    [DataRow(30)]
    [DataRow(60)]
    public void FrameStartTimeThroughTimeSpan_ResolvesToThatFrame(int fps)
    {
        for (int n = 0; n < 3000; n++)
        {
            var time = TimeSpan.FromSeconds(FrameTimeConverter.FrameToTime(n, fps));
            Assert.AreEqual(n, FrameTimeConverter.TimeToFrameFloor(time.TotalSeconds, fps),
                $"frame {n} at {fps} fps");
        }
    }

    [TestMethod]
    public void RealSubFrameOffsetBeforeBoundary_StillTruncates()
    {
        // 1 ms before frame 10 at 30 fps is genuinely still frame 9.
        double t = 10.0 / 30 - 0.001;
        Assert.AreEqual(9, FrameTimeConverter.TimeToFrameFloor(t, 30));
    }
}
