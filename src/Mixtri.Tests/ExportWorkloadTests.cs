using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Mixtri.Core.Export;
using Windows.Media.Core;

namespace Mixtri.Tests;

[TestClass]
public class ExportWorkloadTests
{
    [TestMethod]
    public void FrameLimiter_RetainsManagedSampleAndEventUntilConsumption()
    {
        using var limiter = new ExportFrameLimiter(320, 192);
        using var slot = limiter.Reserve(CancellationToken.None);
        var resource = new TrackedResource();
        var weak = AttachSample(slot, resource);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsTrue(weak.IsAlive, "Processed event ownership must survive collection of producer locals.");
        Assert.AreEqual(0, resource.DisposalCount);
        slot.Dispose();
        Assert.AreEqual(1, resource.DisposalCount);
        Assert.AreEqual(0, limiter.ActiveCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(weak.IsAlive, "Consumption must release the event owner as well as its surface.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachSample(ExportFrameLimiter.Slot slot, IDisposable resource)
    {
        var sample = MediaStreamSample.CreateFromBuffer(new byte[4].AsBuffer(), TimeSpan.Zero);
        slot.Attach(resource, sample);
        return new WeakReference(sample);
    }

    [TestMethod]
    public void FrameLimiter_BoundsCountAndBytes()
    {
        using var small = new ExportFrameLimiter(320, 192);
        using var large = new ExportFrameLimiter(7680, 4320);
        Assert.AreEqual((128L * 1024 * 1024) / (320L * 192 * 4), small.Capacity);
        Assert.AreEqual(1, large.Capacity);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ExportFrameLimiter(20000, 20000));
    }

    [TestMethod]
    public void FrameLimiter_AdditionalNativeInputDoesNotWaitForRetainedSamples()
    {
        using var limiter = new ExportFrameLimiter(2304, 1456);
        using var first = limiter.Reserve(CancellationToken.None);
        using var second = limiter.Reserve(CancellationToken.None);
        using var third = limiter.Reserve(CancellationToken.None);
        var resource = new TrackedResource();
        first.Attach(resource);
        using var nextInput = limiter.Reserve(CancellationToken.None);
        Assert.AreEqual(4, limiter.ActiveCount);
        Assert.AreEqual(0, resource.DisposalCount);
        first.Dispose();
        first.Dispose();
        Assert.AreEqual(1, resource.DisposalCount);
        Assert.AreEqual(3, limiter.ActiveCount);
    }

    [TestMethod]
    public void FrameLimiter_BudgetExhaustionFailsImmediatelyInsteadOfWaitingOnCodec()
    {
        using var limiter = new ExportFrameLimiter(7680, 4320);
        using var first = limiter.Reserve(CancellationToken.None);
        var error = Assert.ThrowsException<InvalidOperationException>(
            () => limiter.Reserve(CancellationToken.None));
        StringAssert.Contains(error.Message, "buffered-frame budget");
        first.Dispose();
        using var retry = limiter.Reserve(CancellationToken.None);
        Assert.AreEqual(1, limiter.ActiveCount);
    }

    [TestMethod]
    public void FrameLimiter_CancellationDoesNotConsumeCapacity()
    {
        using var limiter = new ExportFrameLimiter(7680, 4320);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => limiter.Reserve(cancellation.Token));
        Assert.AreEqual(0, limiter.ActiveCount);
    }

    [TestMethod]
    public void FrameLimiter_DrainReleasesSurfacesAndRejectsNewReservations()
    {
        var limiter = new ExportFrameLimiter(7680, 4320);
        var first = limiter.Reserve(CancellationToken.None);
        var resource = new TrackedResource();
        first.Attach(resource);
        limiter.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => limiter.Reserve(CancellationToken.None));
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
