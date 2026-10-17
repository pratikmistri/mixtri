using System.Diagnostics;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;

namespace Mixtri.Tests;

[TestClass]
[DoNotParallelize]
public class ExportWorkerCancellationTests
{
    private static ExportWorkerClient UnresponsiveWorker(bool readInitialJob)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = (readInitialJob ? "$null = [Console]::ReadLine(); " : "")
            + "Start-Sleep -Seconds 120";
        return new ExportWorkerClient(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
    }

    private static ProjectStateSnapshot Snapshot(string name = "Unresponsive worker") =>
        ProjectStateSnapshot.Capture(new Project { Name = name }, new CompositionConfig(), null);

    [TestMethod]
    [Timeout(30000)]
    public async Task Cancel_BlockedPreviewWriteTerminatesWithoutDependingOnWriter()
    {
        var client = UnresponsiveWorker(readInitialJob: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var exporting = client.ExportAsync(Snapshot(), new ExportSettings(), Path.GetTempPath(),
            null, cancellation.Token);
        try
        {
            await WaitForBlockedPreviewAsync(client, exporting);
            Assert.IsTrue(client.ActiveProcessId.HasValue);
            using var worker = Process.GetProcessById(client.ActiveProcessId.Value);
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            await AssertCancelledAsync(exporting);
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(8),
                "Cancellation must not wait for the blocked Preview write.");
            Assert.IsTrue(worker.HasExited);
            Assert.IsNull(client.ActiveProcessId);
        }
        finally
        {
            cancellation.Cancel();
            try { await exporting.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task Cancel_WorkerThatNeverReadsOrWritesDoesNotStrandStartup()
    {
        var client = UnresponsiveWorker(readInitialJob: false);
        using var cancellation = new CancellationTokenSource();
        var exporting = client.ExportAsync(Snapshot(new string('x', 256 * 1024)),
            new ExportSettings(), Path.GetTempPath(), null, cancellation.Token);
        Assert.IsTrue(client.ActiveProcessId.HasValue);
        using var worker = Process.GetProcessById(client.ActiveProcessId.Value);
        cancellation.CancelAfter(150);
        await AssertCancelledAsync(exporting);
        Assert.IsTrue(worker.HasExited);
        Assert.IsNull(client.ActiveProcessId);
    }

    [TestMethod]
    [Timeout(25000)]
    public async Task PreviewWrite_TimeoutFailsExplicitlyAndReapsTheWorker()
    {
        var client = UnresponsiveWorker(readInitialJob: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exporting = client.ExportAsync(Snapshot(), new ExportSettings(), Path.GetTempPath(),
            null, cancellation.Token);
        Assert.IsTrue(client.ActiveProcessId.HasValue);
        using var worker = Process.GetProcessById(client.ActiveProcessId.Value);
        var error = await Assert.ThrowsExceptionAsync<TimeoutException>(async () =>
            await exporting.WaitAsync(TimeSpan.FromSeconds(18)));
        StringAssert.Contains(error.Message, "stopped accepting preview controls");
        Assert.IsTrue(worker.HasExited);
        Assert.IsNull(client.ActiveProcessId);
    }

    private static async Task WaitForBlockedPreviewAsync(ExportWorkerClient client, Task exporting)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (exporting.IsCompleted)
            {
                await exporting;
                Assert.Fail("The unresponsive worker exited before pipe backpressure was exercised.");
            }
            var writing = client.PendingPreviewWrite;
            if (writing is { IsCompleted: false })
            {
                await Task.Delay(150);
                if (ReferenceEquals(writing, client.PendingPreviewWrite) && !writing.IsCompleted)
                    return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The fixture did not produce a blocked Preview write.");
    }

    private static async Task AssertCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Fail("The export did not acknowledge cancellation.");
        }
        catch (OperationCanceledException) { }
    }
}
