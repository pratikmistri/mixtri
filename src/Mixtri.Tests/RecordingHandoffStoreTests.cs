using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedReplacementPublicationRetainsAcknowledgedState(bool legacyMarker)
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_publish_failure_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        byte[] original = await File.ReadAllBytesAsync(path);
        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.AcknowledgeAsync(request);
        Assert.IsTrue(File.Exists(path + ".done"));
        if (legacyMarker) await File.WriteAllBytesAsync(path + ".done", []);

        var failure = new IOException("Injected replacement publication failure.");
        bool attempted = false;
        store.HandoffPublisherOverride = (_, _) =>
        {
            attempted = true;
            throw failure;
        };
        var replacement = request with { EditorId = Guid.NewGuid() };
        var actual = await Assert.ThrowsExceptionAsync<IOException>(() => store.SaveAsync(replacement));
        Assert.AreSame(failure, actual);
        Assert.IsTrue(attempted);
        Assert.IsTrue(File.Exists(path), "The previous handoff must survive a failed atomic replacement.");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(File.Exists(path + ".done"), "Its acknowledgement must also survive.");
        CollectionAssert.AreEqual(SHA256.HashData(original), await File.ReadAllBytesAsync(path + ".done"));
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());

        store.HandoffPublisherOverride = null;
        await store.SaveAsync(replacement);
        Assert.AreEqual(replacement.EditorId,
            (await new RecordingHandoffStore(directory.Path).ReadPendingAsync()).Single().EditorId);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task SaveAfterAcknowledgement_RemovesObsoleteTombstone(bool handoffSurvives, bool legacyMarker)
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_replace_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");

        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.AcknowledgeAsync(request);
        Assert.IsTrue(File.Exists(path + ".done"));
        if (legacyMarker) await File.WriteAllBytesAsync(path + ".done", []);
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
            await store.AcknowledgeAsync(request);
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
    public async Task LockedAcknowledgedFile_DoesNotPublishReplacementOrLoseMarker()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_locked_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        byte[] original = await File.ReadAllBytesAsync(path);
        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await store.AcknowledgeAsync(request);
            try
            {
                await store.SaveAsync(request with { EditorId = Guid.NewGuid() });
                Assert.Fail("Replacing a file held without delete sharing must fail.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
    public async Task PublishedReplacementSurvivesAnObsoleteMarkerThatCannotBeDeleted()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_marker_cleanup_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        byte[] original = await File.ReadAllBytesAsync(path);
        using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.AcknowledgeAsync(request);
        var replacement = request with { EditorId = Guid.NewGuid() };

        using (var markerPin = new FileStream(path + ".done", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await store.SaveAsync(replacement);
            Assert.IsTrue(File.Exists(path + ".done"));
            CollectionAssert.AreEqual(SHA256.HashData(original), await File.ReadAllBytesAsync(path + ".done"));
            var restarted = new RecordingHandoffStore(directory.Path);
            Assert.AreEqual(replacement.EditorId, (await restarted.ReadPendingAsync()).Single().EditorId,
                "A marker for the old bytes must not consume the published replacement.");
            Assert.IsTrue(File.Exists(path));
        }

        Assert.AreEqual(replacement.EditorId, (await store.ReadPendingAsync()).Single().EditorId);
        Assert.IsFalse(File.Exists(path + ".done"));
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task LegacyMarkerUpgradeFailureLeavesBothOldFilesUntouched()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_legacy_marker_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        byte[] original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path + ".done", []);
        bool publicationAttempted = false;
        store.HandoffPublisherOverride = (_, _) => publicationAttempted = true;

        using (var pin = new FileStream(path + ".done", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await store.SaveAsync(request with { EditorId = Guid.NewGuid() });
                Assert.Fail("The pinned legacy marker cannot be replaced.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Assert.IsFalse(publicationAttempted, "An unscoped legacy marker must not coexist with newly published contents.");
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(0L, new FileInfo(path + ".done").Length);
        }
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task CorruptAcknowledgementDoesNotQuarantineOrDeleteValidHandoff()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_corrupt_marker_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        string path = Path.Combine(directory.Path, $"{request.Id:N}.json");
        await File.WriteAllTextAsync(path + ".done", "invalid");
        Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".bad"));
        File.Delete(path + ".done");
        Assert.AreEqual(request.Id, (await store.ReadPendingAsync()).Single().Id);
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

    [TestMethod]
    public async Task AcknowledgementOfOlderSnapshotPreservesTheNewDestination()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_stale_ack_");
        var firstStore = new RecordingHandoffStore(directory.Path);
        var otherStore = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await firstStore.SaveAsync(request);
        var delivered = (await firstStore.ReadPendingAsync()).Single();
        var newer = request with { EditorId = Guid.NewGuid(), Message = "New destination" };
        await otherStore.SaveAsync(newer);

        await Assert.ThrowsExceptionAsync<RecordingHandoffChangedException>(() =>
            firstStore.AcknowledgeAsync(delivered));
        var pending = (await otherStore.ReadPendingAsync()).Single();
        Assert.AreEqual(newer.EditorId, pending.EditorId);
        Assert.AreEqual(newer.Message, pending.Message);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, $"{request.Id:N}.json.done")));

        await otherStore.AcknowledgeAsync(pending);
        await firstStore.AcknowledgeAsync(pending);
        Assert.AreEqual(0, (await firstStore.ReadPendingAsync()).Count);
    }

    [TestMethod]
    public async Task AcknowledgementComparesTheWholeDeliveredPayload()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_payload_ack_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        var delivered = (await store.ReadPendingAsync()).Single();
        var newer = request with { AppendToProjectId = Guid.NewGuid() };
        await store.SaveAsync(newer);

        await Assert.ThrowsExceptionAsync<RecordingHandoffChangedException>(() =>
            store.AcknowledgeAsync(delivered));
        Assert.AreEqual(newer.AppendToProjectId, (await store.ReadPendingAsync()).Single().AppendToProjectId);
    }

    [TestMethod]
    [DataRow("save")]
    [DataRow("read")]
    [DataRow("acknowledge")]
    public async Task StoreOperationsWaitForTheOtherProcess(string operation)
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_process_lock_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        var mutexName = (string)typeof(RecordingHandoffStore)
            .GetField("_processLockName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(
            $"$m = [System.Threading.Mutex]::new($false, '{mutexName}'); " +
            "try { $null = $m.WaitOne(); [Console]::WriteLine('locked'); " +
            "[Console]::Out.Flush(); $null = [Console]::ReadLine(); $m.ReleaseMutex() } " +
            "finally { $m.Dispose() }");
        using var owner = Process.Start(start) ?? throw new AssertFailedException("Could not start mutex owner.");
        Task? pending = null;
        try
        {
            Assert.AreEqual("locked", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            pending = operation switch
            {
                "save" => store.SaveAsync(request),
                "read" => store.ReadPendingAsync(),
                _ => store.AcknowledgeAsync(request),
            };
            await Task.WhenAny(pending, Task.Delay(100));
            Assert.IsFalse(pending.IsCompleted, "A process-local semaphore cannot protect this operation.");
        }
        finally
        {
            if (!owner.HasExited)
            {
                await owner.StandardInput.WriteLineAsync("release");
                try { await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException)
                {
                    owner.Kill(entireProcessTree: true);
                    await owner.WaitForExitAsync();
                    throw;
                }
            }
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(0, owner.ExitCode, await owner.StandardError.ReadToEndAsync());
    }

    [TestMethod]
    public void EquivalentDirectorySpellingsUseTheSameProcessLock()
    {
        using var directory = new TempDirectoryFixture("mixtri_handoff_lock_name_");
        var field = typeof(RecordingHandoffStore)
            .GetField("_processLockName", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = new RecordingHandoffStore(directory.Path);
        var equivalent = new RecordingHandoffStore(directory.Path.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.AreEqual(field.GetValue(first), field.GetValue(equivalent));
    }
}
