using Mixtri.Core.Diagnostics;

namespace Mixtri.Tests;

[TestClass]
public sealed class PerformanceMonitorTests
{
    #region Fresh Monitor

    [TestMethod]
    public void FreshMonitor_Report_HasZeroCounters()
    {
        var monitor = new PerformanceMonitor();
        var report = monitor.GetReport();

        Assert.AreEqual(0, report.TotalFramesCaptured);
        Assert.AreEqual(0, report.DroppedFrames);
        Assert.AreEqual(0.0, report.DropRate);
        Assert.AreEqual(0.0, report.AvgCaptureTimeMs);
        Assert.AreEqual(0.0, report.AvgCompositionTimeMs);
        Assert.AreEqual(0.0, report.AvgEncodingTimeMs);
        Assert.AreEqual(0.0, report.MaxCaptureTimeMs);
        // Memory values are live — just verify they're non-negative
        Assert.IsTrue(report.CurrentMemoryMB >= 0);
        Assert.IsTrue(report.PeakMemoryMB >= 0);
    }

    #endregion

    #region Frame Capture

    [TestMethod]
    public void RecordFrameCapture_IncrementsCount()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(10));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(20));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(30));

        var report = monitor.GetReport();
        Assert.AreEqual(3, report.TotalFramesCaptured);
    }

    [TestMethod]
    public void RecordFrameCapture_CalculatesAverage()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(10));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(30));

        var report = monitor.GetReport();
        Assert.AreEqual(20.0, report.AvgCaptureTimeMs, 0.5);
    }

    [TestMethod]
    public void RecordFrameCapture_TracksMaximum()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(5));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(50));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(25));

        var report = monitor.GetReport();
        Assert.AreEqual(50.0, report.MaxCaptureTimeMs, 0.5);
    }

    #endregion

    #region Frame Drops

    [TestMethod]
    public void RecordFrameDrop_IncreasesDropCount()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameDrop();
        monitor.RecordFrameDrop();

        var report = monitor.GetReport();
        Assert.AreEqual(2, report.DroppedFrames);
    }

    [TestMethod]
    public void DropRate_CalculatedCorrectly()
    {
        var monitor = new PerformanceMonitor();

        // 3 captured + 1 dropped = 25% drop rate
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(10));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(10));
        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(10));
        monitor.RecordFrameDrop();

        var report = monitor.GetReport();
        Assert.AreEqual(0.25, report.DropRate, 0.01);
    }

    [TestMethod]
    public void DropRate_NoFrames_ReturnsZero()
    {
        var monitor = new PerformanceMonitor();
        var report = monitor.GetReport();
        Assert.AreEqual(0.0, report.DropRate);
    }

    #endregion

    #region Composition and Encoding

    [TestMethod]
    public void RecordFrameComposition_CalculatesAverage()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameComposition(TimeSpan.FromMilliseconds(8));
        monitor.RecordFrameComposition(TimeSpan.FromMilliseconds(12));

        var report = monitor.GetReport();
        Assert.AreEqual(10.0, report.AvgCompositionTimeMs, 0.5);
    }

    [TestMethod]
    public void RecordFrameEncoding_CalculatesAverage()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameEncoding(TimeSpan.FromMilliseconds(15));
        monitor.RecordFrameEncoding(TimeSpan.FromMilliseconds(25));

        var report = monitor.GetReport();
        Assert.AreEqual(20.0, report.AvgEncodingTimeMs, 0.5);
    }

    #endregion

    #region Reset

    [TestMethod]
    public void Reset_ClearsAllCounters()
    {
        var monitor = new PerformanceMonitor();

        monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(100));
        monitor.RecordFrameDrop();
        monitor.RecordFrameComposition(TimeSpan.FromMilliseconds(50));
        monitor.RecordFrameEncoding(TimeSpan.FromMilliseconds(30));

        monitor.Reset();
        var report = monitor.GetReport();

        Assert.AreEqual(0, report.TotalFramesCaptured);
        Assert.AreEqual(0, report.DroppedFrames);
        Assert.AreEqual(0.0, report.AvgCaptureTimeMs);
        Assert.AreEqual(0.0, report.AvgCompositionTimeMs);
        Assert.AreEqual(0.0, report.AvgEncodingTimeMs);
        Assert.AreEqual(0.0, report.MaxCaptureTimeMs);
    }

    #endregion

    #region Thread Safety

    [TestMethod]
    public void ConcurrentCaptures_DoNotLoseCount()
    {
        var monitor = new PerformanceMonitor();
        int perThread = 1000;
        int threadCount = 4;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
                monitor.RecordFrameCapture(TimeSpan.FromMilliseconds(1));
        })).ToArray();

        Task.WaitAll(tasks);

        var report = monitor.GetReport();
        Assert.AreEqual(perThread * threadCount, report.TotalFramesCaptured,
            "No frames should be lost under concurrent access");
    }

    #endregion

    #region Memory

    [TestMethod]
    public void GetCurrentMemoryUsageMB_ReturnsPositive()
    {
        var monitor = new PerformanceMonitor();
        long mb = monitor.GetCurrentMemoryUsageMB();
        Assert.IsTrue(mb > 0, "Current process should use some memory");
    }

    [TestMethod]
    public void GetPeakMemoryUsageMB_AtLeastCurrentUsage()
    {
        var monitor = new PerformanceMonitor();
        long current = monitor.GetCurrentMemoryUsageMB();
        long peak = monitor.GetPeakMemoryUsageMB();
        Assert.IsTrue(peak >= current, $"Peak ({peak}) should be >= current ({current})");
    }

    #endregion
}
