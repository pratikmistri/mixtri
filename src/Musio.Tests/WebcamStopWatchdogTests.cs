using System.Diagnostics;
using Musio.Core.Capture;

namespace Musio.Tests;

/// <summary>
/// Watchdog guards for webcam shutdown.
/// </summary>
/// <remarks>
/// <c>MediaCapture.StopRecordAsync</c> is a driver call: a wedged camera can leave it
/// pending forever. Recording stop used to await it directly, so one bad driver could pin
/// the app in the stopping state with no way out. These tests drive the bounded wait
/// directly, so the timeout, cancellation and failure paths are covered without a camera.
/// </remarks>
[TestClass]
public class WebcamStopWatchdogTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public async Task StopThatCompletes_ReportsCompleted()
    {
        var outcome = await WebcamCaptureEngine.WaitForStopAsync(
            Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(WebcamStopOutcome.Completed, outcome);
    }

    [TestMethod]
    public async Task StalledDriver_TimesOutInsteadOfHangingTheStop()
    {
        var stalled = new TaskCompletionSource();

        var sw = Stopwatch.StartNew();
        var outcome = await WebcamCaptureEngine.WaitForStopAsync(
            stalled.Task, ShortTimeout, CancellationToken.None);
        sw.Stop();

        Assert.AreEqual(WebcamStopOutcome.TimedOut, outcome);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"the stop must give up near its budget, but waited {sw.Elapsed}");

        stalled.SetResult();
    }

    [TestMethod]
    public async Task Cancellation_AbandonsTheStopWithoutThrowing()
    {
        var stalled = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var outcome = await WebcamCaptureEngine.WaitForStopAsync(
            stalled.Task, TimeSpan.FromMinutes(5), cts.Token);

        Assert.AreEqual(WebcamStopOutcome.Canceled, outcome,
            "cancellation must end the wait as a reported outcome, not an exception, "
            + "so the rest of session cleanup still runs");

        stalled.SetResult();
    }

    [TestMethod]
    public async Task FailingDriver_IsReportedRatherThanThrown()
    {
        var failed = Task.FromException(new InvalidOperationException("camera fell over"));

        var outcome = await WebcamCaptureEngine.WaitForStopAsync(
            failed, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(WebcamStopOutcome.Failed, outcome);
    }

    [TestMethod]
    public async Task AbandonedStop_DoesNotRaiseAnUnobservedTaskException()
    {
        var stalled = new TaskCompletionSource();

        var outcome = await WebcamCaptureEngine.WaitForStopAsync(
            stalled.Task, ShortTimeout, CancellationToken.None);
        Assert.AreEqual(WebcamStopOutcome.TimedOut, outcome);

        // The abandoned call finishing badly must not tear the process down later.
        stalled.SetException(new InvalidOperationException("late driver failure"));

        var observed = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
            => observed.Add(e.Exception);

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.AreEqual(0, observed.Count,
            "the abandoned stop task must stay observed by the engine");
    }

    [TestMethod]
    public async Task StopWithoutRecording_IsANoOp()
    {
        using var engine = new WebcamCaptureEngine();

        var outcome = await engine.StopAsync(ShortTimeout);

        Assert.AreEqual(WebcamStopOutcome.NotRecording, outcome);
        Assert.AreEqual(WebcamStopOutcome.NotRecording, engine.LastStopOutcome);
        Assert.IsFalse(engine.IsRecording);
    }
}
