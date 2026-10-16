using Mixtri.Core.Export;

namespace Mixtri.Tests;

[TestClass]
public class ExportWorkloadTests
{
    [TestMethod]
    public void FrameLimiter_BoundsCountAndBytes()
    {
        using var small = new ExportFrameLimiter(320, 192);
        using var large = new ExportFrameLimiter(7680, 4320);
        Assert.AreEqual(3, small.Capacity);
        Assert.AreEqual(1, large.Capacity);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ExportFrameLimiter(20000, 20000));
    }

    [TestMethod]
    public async Task FrameLimiter_WaitsForConsumptionAndReleasesExactlyOnce()
    {
        using var limiter = new ExportFrameLimiter(7680, 4320);
        var first = await limiter.AcquireAsync(CancellationToken.None);
        var resource = new TrackedResource();
        first.Attach(resource);
        var waiting = limiter.AcquireAsync(CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(1, limiter.ActiveCount);
        first.Dispose();
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, resource.DisposalCount);
        Assert.AreEqual(1, limiter.ActiveCount);
    }

    [TestMethod]
    public async Task FrameLimiter_CancellationDoesNotConsumeCapacity()
    {
        using var limiter = new ExportFrameLimiter(7680, 4320);
        using var first = await limiter.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await waiting);
        Assert.AreEqual(1, limiter.ActiveCount);
    }

    [TestMethod]
    public async Task FrameLimiter_DrainReleasesSurfacesAndRejectsQueuedProducers()
    {
        var limiter = new ExportFrameLimiter(7680, 4320);
        var first = await limiter.AcquireAsync(CancellationToken.None);
        var resource = new TrackedResource();
        first.Attach(resource);
        var waiting = limiter.AcquireAsync(CancellationToken.None);
        limiter.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await waiting);
        Assert.AreEqual(1, resource.DisposalCount);
        Assert.AreEqual(0, limiter.ActiveCount);
    }

    [TestMethod]
    public void Scheduler_YieldsToPlaybackAndRestoresIdleThroughput()
    {
        var scheduler = new ExportWorkScheduler();
        Assert.AreEqual(0, scheduler.YieldMilliseconds);
        scheduler.UpdatePreviewActivity(true, 8);
        Assert.AreEqual(16, scheduler.YieldMilliseconds);
        scheduler.UpdatePreviewActivity(true, 80);
        Assert.AreEqual(80, scheduler.YieldMilliseconds);
        scheduler.UpdatePreviewActivity(true, 800);
        Assert.AreEqual(100, scheduler.YieldMilliseconds);
        scheduler.UpdatePreviewActivity(false, 800);
        Assert.AreEqual(0, scheduler.YieldMilliseconds);
    }

    [TestMethod]
    public async Task Scheduler_CancellationInterruptsInteractiveYield()
    {
        var scheduler = new ExportWorkScheduler();
        scheduler.UpdatePreviewActivity(true, 100);
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.WaitForTurnAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiting);
    }

    private sealed class TrackedResource : IDisposable
    {
        public int DisposalCount { get; private set; }
        public void Dispose() => DisposalCount++;
    }
}
