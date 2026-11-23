using System.Diagnostics;
using Mixtri.Core.Capture;

namespace Mixtri.Tests;

[TestClass]
public sealed class FirstFrameWatchdogTests
{
    private static long Secs(double s) => (long)(s * Stopwatch.Frequency);

    [TestMethod]
    public void NotOverdue_BeforeTimeout()
        => Assert.IsFalse(RecordingSession.IsFirstFrameOverdue(Secs(10), Secs(12), false, TimeSpan.FromSeconds(3)));

    [TestMethod]
    public void Overdue_AfterTimeoutWithoutFrame()
        => Assert.IsTrue(RecordingSession.IsFirstFrameOverdue(Secs(10), Secs(13.5), false, TimeSpan.FromSeconds(3)));

    [TestMethod]
    public void NotOverdue_WhenFrameSeen()
        => Assert.IsFalse(RecordingSession.IsFirstFrameOverdue(Secs(10), Secs(60), true, TimeSpan.FromSeconds(3)));

    [TestMethod]
    public void NotOverdue_WhenGateNeverOpened()
        => Assert.IsFalse(RecordingSession.IsFirstFrameOverdue(0, Secs(60), false, TimeSpan.FromSeconds(3)));
}
