using Mixtri.Core.Models;
using Mixtri.Core.Shell;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public sealed class RecordingApplicationTests
{
    private static ShellProcessRequest Request() => new()
    {
        Command = ShellProcessCommand.RecordingCompleted,
        EditorId = Guid.NewGuid(),
        AppendToProjectId = Guid.NewGuid(),
        Project = new Project { VideoFilePath = @"C:\recording\video.mp4" },
    };

    [TestMethod]
    public async Task FailedSenderAcknowledgement_DoesNotApplyAgainAfterReceiverRestart()
    {
        using var directory = new TempDirectoryFixture("mixtri_application_receipt_");
        string receipts = Path.Combine(directory.Path, "receipts");
        var store = new RecordingHandoffStore(Path.Combine(directory.Path, "handoffs"));
        var request = Request();
        await store.SaveAsync(request);
        int applications = 0;
        var first = new RecordingApplication().Apply(request, receipts, () => null, () => applications++);
        Assert.IsTrue(first.Success);

        string marker = Path.Combine(directory.Path, "handoffs", $"{request.Id:N}.json.done");
        Directory.CreateDirectory(marker);
        await Assert.ThrowsExceptionAsync<IOException>(() => store.AcknowledgeAsync(request.Id));
        var replay = (await store.ReadPendingAsync()).Single();
        var restarted = new RecordingApplication();
        var result = restarted.Apply(replay, receipts,
            () => throw new AssertFailedException("A completed receipt must be checked before current project rejection."),
            () => applications++);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, applications);
        Directory.Delete(marker);
        await store.AcknowledgeAsync(request.Id);
        Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
    }

    [TestMethod]
    public void InterruptedApplication_RemainsExplicitlyUncertainWithoutReapplying()
    {
        using var directory = new TempDirectoryFixture("mixtri_application_interrupted_");
        var request = Request();
        int applications = 0;
        var result = new RecordingApplication().Apply(request, directory.Path, () => null, () =>
        {
            applications++;
            throw new IOException("Interrupted after mutation.");
        });
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.OutcomeUnknown);
        var replay = new RecordingApplication().Apply(request, directory.Path, () => null, () => applications++);
        Assert.IsFalse(replay.Success);
        Assert.IsTrue(replay.OutcomeUnknown);
        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public void ReceiptPersistenceFailure_RetriesTheReceiptNotTheApplication()
    {
        using var directory = new TempDirectoryFixture("mixtri_application_commit_");
        var request = Request();
        string completed = Path.Combine(directory.Path, $"{request.Id:N}.applied");
        var receiver = new RecordingApplication();
        int applications = 0;
        var result = receiver.Apply(request, directory.Path, () => null, () =>
        {
            applications++;
            Directory.CreateDirectory(completed);
        });
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.OutcomeUnknown);
        Directory.Delete(completed);
        var replay = receiver.Apply(request, directory.Path, () => null, () => applications++);
        Assert.IsTrue(replay.Success);
        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public void RejectionBeforeApplication_DoesNotClaimAReceipt()
    {
        using var directory = new TempDirectoryFixture("mixtri_application_rejected_");
        var request = Request();
        var result = new RecordingApplication().Apply(request, directory.Path, () => "Project is busy.",
            () => Assert.Fail("Rejected requests must not be applied."));
        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.OutcomeUnknown);
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path).Any());
    }

    [TestMethod]
    public async Task ConcurrentReceiversCannotBothApplyTheSameRequest()
    {
        using var directory = new TempDirectoryFixture("mixtri_application_claim_");
        var request = Request();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int applications = 0;
        var first = Task.Run(() => new RecordingApplication().Apply(request, directory.Path, () => null, () =>
        {
            Interlocked.Increment(ref applications);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test receiver was not released.");
        }));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            var second = new RecordingApplication().Apply(request, directory.Path, () => null,
                () => Interlocked.Increment(ref applications));
            Assert.IsFalse(second.Success);
            Assert.IsTrue(second.OutcomeUnknown);
            Assert.AreEqual(1, applications);
        }
        finally
        {
            release.Set();
            Assert.IsTrue((await first).Success);
        }
    }

    [TestMethod]
    public async Task UncertainReceiptDoesNotRedirectToAnotherEditor()
    {
        var request = Request();
        int attempts = 0;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            RecordingDelivery.DeliverAsync(request,
                _ =>
                {
                    attempts++;
                    return Task.FromResult<ShellProcessResponse?>(new(false, "Application outcome is unknown.")
                    { OutcomeUnknown = true });
                },
                () => throw new AssertFailedException("An uncertain application must not be redirected."),
                _ => throw new AssertFailedException("An uncertain application must keep its existing destination."),
                2, TimeSpan.Zero));
        Assert.AreEqual(3, attempts);
    }
}
