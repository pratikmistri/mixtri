using System.Reflection;
using Mixtri.Core.Models;
using Mixtri.Core.Shell;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public sealed class RecordingHandoffStoreTests
{
    private static ShellProcessRequest Request() => new()
    {
        Command = ShellProcessCommand.RecordingCompleted,
        EditorId = Guid.NewGuid(),
        Project = new Project { VideoFilePath = @"C:\recording\video.mp4" },
    };

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SaveAfterAcknowledgement_RemovesObsoleteTombstone(bool handoffSurvives)
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_replace_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");

        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.AcknowledgeAsync(request.Id);
        Assert.IsTrue(File.Exists(path + ".done"));
        if (!handoffSurvives) File.Delete(path);

        var replacement = request with { EditorId = Guid.NewGuid() };
        await store.SaveAsync(replacement);
        var restored = (await new RecordingHandoffStore(directory.Path).ReadPendingAsync()).Single();
        Assert.AreEqual(replacement.EditorId, restored.EditorId);
        Assert.AreEqual(request.Id, restored.Id);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".done"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RecoveryWaitsForReplacement_BeforeCleanupOrQuarantine(bool acknowledged)
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_gated_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        if (acknowledged)
        {
            using var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await store.AcknowledgeAsync(request.Id);
        }
        else
        {
            await File.WriteAllTextAsync(path, "{ truncated");
        }

        // Hold the actual store gate to queue a replacement ahead of recovery.
        var gate = (SemaphoreSlim)typeof(RecordingHandoffStore)
            .GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        var replacement = request with { EditorId = Guid.NewGuid() };
        var save = store.SaveAsync(replacement);
        var read = store.ReadPendingAsync();
        try
        {
            Assert.IsFalse(save.IsCompleted);
            Assert.IsFalse(read.IsCompleted, "Recovery must wait, not clean up files while a save owns the gate.");
        }
        finally
        {
            gate.Release();
            await Task.WhenAll(save, read).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.AreEqual(replacement.EditorId, (await read).Single().EditorId);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".done"));
        Assert.IsFalse(File.Exists(path + ".bad"));
    }

    [TestMethod]
    public async Task SnapshotDoesNotHoldStoreGate_WhileCallerPersistsARedirect()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_snapshot_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        var snapshot = await store.ReadPendingAsync();
        var replacement = request with { EditorId = Guid.NewGuid() };
        foreach (var pending in snapshot)
        {
            Assert.AreEqual(request.EditorId, pending.EditorId);
            await store.SaveAsync(replacement).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(replacement.EditorId, (await store.ReadPendingAsync()).Single().EditorId);
        Assert.AreEqual(request.EditorId, snapshot.Single().EditorId);
    }

    [TestMethod]
    public async Task FailedAcknowledgedCleanup_DoesNotPublishReplacementOrLoseMarker()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_locked_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        byte[] original = await File.ReadAllBytesAsync(path);
        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await store.AcknowledgeAsync(request.Id);
            await Assert.ThrowsExceptionAsync<IOException>(() =>
                store.SaveAsync(request with { EditorId = Guid.NewGuid() }));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
            Assert.IsTrue(File.Exists(path + ".done"));
            Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
            Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
        }
        Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".done"));
    }

    [TestMethod]
    public async Task TransientReadFailure_LeavesHandoffForRetryRatherThanQuarantining()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_read_lock_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".bad"));
        }
        Assert.AreEqual(request.Id, (await store.ReadPendingAsync()).Single().Id);
    }
}
