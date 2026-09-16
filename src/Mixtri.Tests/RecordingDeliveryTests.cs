using Mixtri.Core.Models;
using Mixtri.Core.Shell;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public sealed class RecordingDeliveryTests
{
    private static ShellProcessRequest Request() => new()
    {
        Command = ShellProcessCommand.RecordingCompleted,
        EditorId = Guid.NewGuid(),
        AppendToProjectId = Guid.NewGuid(),
        Project = new Project { VideoFilePath = @"C:\recording\video.mp4" },
    };

    [TestMethod]
    public async Task OriginalReplyLost_RetriesSameDestinationAndRequest()
    {
        var request = Request();
        int attempts = 0;
        var delivered = await RecordingDelivery.DeliverAsync(
            request,
            pending =>
            {
                Assert.AreSame(request, pending);
                attempts++;
                return Task.FromResult<ShellProcessResponse?>(attempts == 1 ? null : new(true));
            },
            () => throw new AssertFailedException("An unknown outcome must not open another editor."),
            _ => throw new AssertFailedException("The original request should not be redirected."),
            retries: 2, retryDelay: TimeSpan.Zero);
        Assert.AreSame(request, delivered);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task UnknownOriginalOutcome_LeavesDestinationUnchanged()
    {
        var request = Request();
        int attempts = 0;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            RecordingDelivery.DeliverAsync(
                request,
                pending =>
                {
                    Assert.AreEqual(request.EditorId, pending.EditorId);
                    Assert.AreEqual(request.Id, pending.Id);
                    attempts++;
                    return Task.FromResult<ShellProcessResponse?>(null);
                },
                () => throw new AssertFailedException("Only an explicit rejection can redirect a recording."),
                _ => throw new AssertFailedException("An ambiguous delivery must retain its destination."),
                retries: 2, retryDelay: TimeSpan.Zero));
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    public async Task FallbackReplyLoss_PreservesDestinationAcrossRecovery(int lostReplies)
    {
        using var directory = new TempDirectoryFixture("mixtri_delivery_recovery_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        var inMemory = request;
        var fallback = Guid.NewGuid();
        int editorsCreated = 0, originalAttempts = 0, fallbackAttempts = 0, applications = 0;
        var accepted = new HashSet<(Guid EditorId, Guid RequestId)>();

        async Task<ShellProcessResponse?> Send(ShellProcessRequest pending)
        {
            if (pending.EditorId == request.EditorId)
            {
                originalAttempts++;
                return new(false, "The original editor has unsaved work.");
            }
            Assert.AreEqual(fallback, pending.EditorId);
            Assert.AreEqual(request.Id, pending.Id);
            Assert.IsNull(pending.AppendToProjectId);
            var durable = (await new RecordingHandoffStore(directory.Path).ReadPendingAsync()).Single();
            Assert.AreEqual(fallback, durable.EditorId, "The route must be durable before applying the recording.");
            Assert.AreEqual(request.EditorId, durable.RedirectedFromEditorId);
            Assert.AreEqual(fallback, inMemory.EditorId, "In-process recovery must also retain the new route.");
            if (accepted.Add((pending.EditorId, pending.Id))) applications++;
            fallbackAttempts++;
            return fallbackAttempts <= lostReplies ? null : new(true);
        }

        Task<Guid> CreateEditor()
        {
            editorsCreated++;
            return Task.FromResult(fallback);
        }

        async Task Persist(ShellProcessRequest redirected)
        {
            inMemory = redirected;
            await store.SaveAsync(redirected);
        }

        ShellProcessRequest delivered;
        if (lostReplies == 3)
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                RecordingDelivery.DeliverAsync(request, Send, CreateEditor, Persist, 2, TimeSpan.Zero));
            var pending = (await new RecordingHandoffStore(directory.Path).ReadPendingAsync()).Single();
            Assert.AreEqual(fallback, pending.EditorId);
            delivered = await RecordingDelivery.DeliverAsync(pending, Send, CreateEditor, Persist, 2, TimeSpan.Zero);
        }
        else
        {
            delivered = await RecordingDelivery.DeliverAsync(request, Send, CreateEditor, Persist, 2, TimeSpan.Zero);
        }

        Assert.AreEqual(1, originalAttempts);
        Assert.AreEqual(1, editorsCreated, "Recovery must not create a second fallback editor.");
        Assert.AreEqual(1, applications, "A retried fallback must replay its result, not apply the take again.");
        Assert.AreEqual(lostReplies + 1, fallbackAttempts);
        Assert.AreEqual(request.Id, delivered.Id);
        Assert.AreEqual(request.EditorId, delivered.RedirectedFromEditorId);
        await store.AcknowledgeAsync(delivered);
        Assert.AreEqual(0, (await store.ReadPendingAsync()).Count);
    }

    [TestMethod]
    public async Task NoOriginalEditor_PersistsTheNewDestinationBeforeSending()
    {
        var request = Request() with { EditorId = Guid.Empty, AppendToProjectId = null };
        var fallback = Guid.NewGuid();
        ShellProcessRequest? persisted = null;
        int attempts = 0;
        var delivered = await RecordingDelivery.DeliverAsync(
            request,
            pending =>
            {
                Assert.AreSame(persisted, pending);
                Assert.AreEqual(fallback, pending.EditorId);
                Assert.AreEqual(request.Id, pending.Id);
                attempts++;
                return Task.FromResult<ShellProcessResponse?>(new(true));
            },
            () => Task.FromResult(fallback),
            pending => { persisted = pending; return Task.CompletedTask; },
            2, TimeSpan.Zero);
        Assert.AreEqual(Guid.Empty, delivered.RedirectedFromEditorId);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task RedirectPersistenceFailure_DoesNotSendToTheFallback()
    {
        using var directory = new TempDirectoryFixture("mixtri_delivery_persist_failure_");
        var store = new RecordingHandoffStore(directory.Path);
        var request = Request();
        await store.SaveAsync(request);
        int attempts = 0;
        var failure = new IOException("The new route could not be persisted.");
        var actual = await Assert.ThrowsExceptionAsync<IOException>(() =>
            RecordingDelivery.DeliverAsync(
                request,
                pending =>
                {
                    Assert.AreEqual(request.EditorId, pending.EditorId);
                    attempts++;
                    return Task.FromResult<ShellProcessResponse?>(new(false));
                },
                () => Task.FromResult(Guid.NewGuid()),
                _ => throw failure,
                2, TimeSpan.Zero));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(request.EditorId, (await store.ReadPendingAsync()).Single().EditorId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FallbackRejection_KeepsItsRoutePendingWithoutLaunchingInALoop(bool withoutOriginalEditor)
    {
        var request = Request();
        if (withoutOriginalEditor) request = request with { EditorId = Guid.Empty, AppendToProjectId = null };
        using var directory = new TempDirectoryFixture("mixtri_delivery_rejected_");
        var store = new RecordingHandoffStore(directory.Path);
        await store.SaveAsync(request);
        var fallback = Guid.NewGuid();
        int editors = 0, attempts = 0;
        Task<ShellProcessResponse?> Send(ShellProcessRequest pending)
        {
            attempts++;
            return Task.FromResult<ShellProcessResponse?>(new(false, "The recording file is missing."));
        }
        Task<Guid> CreateEditor()
        {
            editors++;
            return Task.FromResult(editors == 1 ? fallback : Guid.NewGuid());
        }
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            RecordingDelivery.DeliverAsync(request, Send, CreateEditor, pending => store.SaveAsync(pending), 2, TimeSpan.Zero));
        Assert.AreEqual(1, editors);
        Assert.AreEqual(withoutOriginalEditor ? 1 : 2, attempts);

        for (int recovery = 0; recovery < 3; recovery++)
        {
            var pending = (await new RecordingHandoffStore(directory.Path).ReadPendingAsync()).Single();
            Assert.AreEqual(fallback, pending.EditorId);
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                RecordingDelivery.DeliverAsync(pending, Send, CreateEditor, next => store.SaveAsync(next), 2, TimeSpan.Zero));
            StringAssert.Contains(error.Message, "The recording file is missing.");
            Assert.AreEqual(1, editors, "A rejected persisted fallback must not launch another editor on recovery.");
            var retained = (await store.ReadPendingAsync()).Single();
            Assert.AreEqual(request.Id, retained.Id);
            Assert.AreEqual(fallback, retained.EditorId);
        }
    }
}
