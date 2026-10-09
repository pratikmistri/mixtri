using Mixtri.Core.Processing;

namespace Mixtri.Tests;

[TestClass]
public class GraphicsRecoveryCoordinatorTests
{
    [TestMethod]
    public async Task RequestsBeforeDispatch_CoalesceAndSuspendRenderingImmediately()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, repaints = 0;
        GraphicsRecoveryCoordinator? coordinator = null;
        coordinator = new(
            work => { queue.Enqueue(work); return true; },
            () =>
            {
                Assert.IsTrue(coordinator!.IsRecoveryInProgress);
                rebuilds++;
                return Task.CompletedTask;
            },
            () =>
            {
                Assert.IsFalse(coordinator!.IsRecoveryInProgress);
                repaints++;
                return Task.CompletedTask;
            }, () => false, ex => Assert.Fail(ex.ToString()));

        for (int i = 0; i < 9; i++) coordinator.Request();
        Assert.IsTrue(coordinator.IsRecoveryInProgress);
        Assert.AreEqual(1, queue.Count);
        await queue.Dequeue()();
        Assert.AreEqual(1, rebuilds);
        Assert.AreEqual(1, repaints);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }

    [TestMethod]
    public async Task DeviceLossDuringRepaint_RunsAnotherRecovery()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, repaints = 0;
        GraphicsRecoveryCoordinator? coordinator = null;
        coordinator = new(
            work => { queue.Enqueue(work); return true; },
            () => { rebuilds++; return Task.CompletedTask; },
            () =>
            {
                if (++repaints == 1) coordinator!.Request();
                return Task.CompletedTask;
            }, () => false, ex => Assert.Fail(ex.ToString()));

        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(2, rebuilds);
        Assert.AreEqual(2, repaints);
        Assert.AreEqual(0, queue.Count);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }

    [TestMethod]
    public async Task TransientRebuildFailure_RetriesBeforeRepainting()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, repaints = 0;
        var coordinator = new GraphicsRecoveryCoordinator(
            work => { queue.Enqueue(work); return true; },
            () => ++rebuilds == 1
                ? Task.FromException(new InvalidOperationException("device replaced during rebuild"))
                : Task.CompletedTask,
            () => { repaints++; return Task.CompletedTask; },
            () => false, ex => Assert.Fail(ex.ToString()));

        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(2, rebuilds);
        Assert.AreEqual(1, repaints);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }

    [TestMethod]
    public async Task PermanentRebuildFailure_IsBoundedAndLeavesFutureRequestsUsable()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, repaints = 0, failures = 0;
        bool broken = true;
        var coordinator = new GraphicsRecoveryCoordinator(
            work => { queue.Enqueue(work); return true; },
            () =>
            {
                rebuilds++;
                return broken ? Task.FromException(new InvalidOperationException("unavailable"))
                    : Task.CompletedTask;
            },
            () => { repaints++; return Task.CompletedTask; },
            () => false, _ => failures++);

        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(3, rebuilds);
        Assert.AreEqual(1, failures);
        Assert.AreEqual(0, repaints);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
        broken = false;
        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(4, rebuilds);
        Assert.AreEqual(1, repaints);
    }

    [TestMethod]
    public async Task RejectedDispatcherRequest_ReportsFailureAndCanBeRetried()
    {
        var queue = new Queue<Func<Task>>();
        bool accept = false;
        int failures = 0, rebuilds = 0;
        var coordinator = new GraphicsRecoveryCoordinator(
            work =>
            {
                if (!accept) return false;
                queue.Enqueue(work);
                return true;
            },
            () => { rebuilds++; return Task.CompletedTask; },
            () => Task.CompletedTask, () => false, _ => failures++);

        coordinator.Request();
        Assert.AreEqual(1, failures);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
        accept = true;
        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(1, rebuilds);
    }

    [TestMethod]
    public async Task RepaintFailure_RetriesTheRebuildBeforeRepaintingAgain()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, repaints = 0;
        var coordinator = new GraphicsRecoveryCoordinator(
            work => { queue.Enqueue(work); return true; },
            () => { rebuilds++; return Task.CompletedTask; },
            () => ++repaints == 1
                ? Task.FromException(new InvalidOperationException("replacement device lost during repaint"))
                : Task.CompletedTask,
            () => false, ex => Assert.Fail(ex.ToString()));

        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(2, rebuilds);
        Assert.AreEqual(2, repaints);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }

    [TestMethod]
    public async Task RequestWhileRebuildAwaits_IsDrainedAndNeverRendersInsideRecovery()
    {
        var queue = new Queue<Func<Task>>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rebuilds = 0, repaints = 0;
        GraphicsRecoveryCoordinator? coordinator = null;
        coordinator = new(
            work => { queue.Enqueue(work); return true; },
            async () =>
            {
                if (++rebuilds == 1) await release.Task;
                Assert.IsTrue(coordinator!.IsRecoveryInProgress);
            },
            () => { repaints++; return Task.CompletedTask; },
            () => false, ex => Assert.Fail(ex.ToString()));

        coordinator.Request();
        var run = queue.Dequeue()();
        coordinator.Request();
        Assert.IsTrue(coordinator.IsRecoveryInProgress);
        Assert.AreEqual(0, repaints);
        release.SetResult();
        await run;
        Assert.AreEqual(2, rebuilds);
        Assert.AreEqual(2, repaints);
    }

    [TestMethod]
    public async Task RepeatedLossRequestsDuringRecovery_AreBoundedAndReportFailure()
    {
        var queue = new Queue<Func<Task>>();
        int rebuilds = 0, failures = 0;
        bool broken = true;
        GraphicsRecoveryCoordinator? coordinator = null;
        coordinator = new(
            work => { queue.Enqueue(work); return true; },
            () =>
            {
                rebuilds++;
                if (broken) coordinator!.Request();
                return Task.CompletedTask;
            },
            () => Task.CompletedTask, () => false, _ => failures++);

        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(3, rebuilds);
        Assert.AreEqual(1, failures);
        Assert.AreEqual(0, queue.Count);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
        broken = false;
        coordinator.Request();
        await queue.Dequeue()();
        Assert.AreEqual(4, rebuilds);
    }

    [TestMethod]
    public async Task UnloadDuringRecovery_DoesNotRepaintOrRequeue()
    {
        var queue = new Queue<Func<Task>>();
        bool unloaded = false;
        int repaints = 0;
        var coordinator = new GraphicsRecoveryCoordinator(
            work => { queue.Enqueue(work); return true; },
            () => { unloaded = true; return Task.CompletedTask; },
            () => { repaints++; return Task.CompletedTask; },
            () => unloaded, ex => Assert.Fail(ex.ToString()));

        coordinator.Request();
        await queue.Dequeue()();
        coordinator.Request();
        Assert.AreEqual(0, repaints);
        Assert.AreEqual(0, queue.Count);
        Assert.IsFalse(coordinator.IsRecoveryInProgress);
    }
}
