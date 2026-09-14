using System.Buffers.Binary;
using Mixtri.Core.Models;
using Mixtri.Core.Shell;

namespace Mixtri.Tests;

[TestClass]
public sealed class EditorProcessTests
{
    [TestMethod]
    public void DefaultLaunch_IsResidentRecorder()
    {
        var launch = EditorProcessLaunch.Parse(["Mixtri.App.exe"]);
        Assert.IsFalse(launch.IsEditor);
        Assert.IsFalse(launch.Background);
    }

    [TestMethod]
    public void FullWindowLaunch_StartsOnRecordUnlessEditorWasExplicitlyRequested()
    {
        var id = Guid.NewGuid();
        var full = EditorProcessLaunch.Parse(EditorProcessLaunch.Arguments(id));
        Assert.IsTrue(full.IsEditor);
        Assert.IsFalse(full.StartInEditor);
        var editor = EditorProcessLaunch.Parse(EditorProcessLaunch.Arguments(id, startInEditor: true));
        Assert.IsTrue(editor.StartInEditor);
    }

    [TestMethod]
    public void WindowPlacement_PreservesValidBoundsAndClampsDisconnectedMonitor()
    {
        var saved = new FullWindowPlacement(-1800, 100, 1200, 800, true);
        Assert.AreEqual(saved, saved.FitToWorkArea(-1920, 0, 1920, 1080));
        var clamped = saved.FitToWorkArea(0, 0, 1920, 1080);
        Assert.AreEqual(0, clamped.X);
        Assert.AreEqual(100, clamped.Y);
        Assert.AreEqual(1200, clamped.Width);
        Assert.IsTrue(clamped.Maximized);
        var smallerDisplay = saved.FitToWorkArea(0, 0, 800, 600);
        Assert.AreEqual(800, smallerDisplay.Width);
        Assert.AreEqual(600, smallerDisplay.Height);
        Assert.AreEqual(0, smallerDisplay.Y);
    }

    [TestMethod]
    public void EditorLaunch_RoundTripsPathsWithoutCommandLineQuoting()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "a folder", "demo \u65e5\u672c.mixtri");
        var arguments = EditorProcessLaunch.Arguments(id, path).ToArray();
        Assert.IsTrue(arguments.All(argument => !argument.Contains(' ')));
        var launch = EditorProcessLaunch.Parse(arguments);
        Assert.IsTrue(launch.IsEditor);
        Assert.AreEqual(id, launch.EditorId);
        Assert.AreEqual(Path.GetFullPath(path), launch.ProjectPath);
    }

    [TestMethod]
    public void BackgroundRecorder_IsNotAnEditor()
    {
        var launch = EditorProcessLaunch.Parse(["--background-recorder"]);
        Assert.IsTrue(launch.Background);
        Assert.IsFalse(launch.IsEditor);
    }

    [TestMethod]
    [DataRow("--editor=bad")]
    [DataRow("--editor=00000000000000000000000000000000")]
    public void InvalidLaunch_IsRejected(string argument)
    {
        Assert.ThrowsException<ArgumentException>(() => EditorProcessLaunch.Parse([argument]));
    }

    [TestMethod]
    public void InvalidEncodedProject_IsRejected()
    {
        Assert.ThrowsException<FormatException>(() => EditorProcessLaunch.Parse(["--project=not-base64"]));
    }

    [TestMethod]
    public void ProjectActivity_StaysBusyUntilAllOwnersFinish()
    {
        var activity = new ProjectActivity();
        var export = activity.Begin();
        var import = activity.Begin();
        Assert.IsTrue(activity.IsBusy);
        export.Dispose();
        export.Dispose();
        Assert.IsTrue(activity.IsBusy);
        import.Dispose();
        Assert.IsFalse(activity.IsBusy);
    }

    [TestMethod]
    public void ConflictingRoles_AreRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => EditorProcessLaunch.Parse(
            [$"--editor={Guid.NewGuid():N}", "--background-recorder"]));
    }

    [TestMethod]
    public async Task ControlMessage_RoundTripsRecordingMetadata()
    {
        var project = new Project
        {
            VideoFilePath = @"C:\capture\video.mp4",
            Duration = TimeSpan.FromSeconds(2.5),
            DpiScale = 1.5f,
            CropOffsetX = -1920,
            CropOffsetY = 90,
            MouseToVideoOffsetSeconds = 0.0123,
            AudioToVideoOffsetSeconds = -0.5,
            AudioFilePaths = [@"C:\capture\mic.wav"],
        };
        var request = new ShellProcessRequest
        {
            Command = ShellProcessCommand.RecordingCompleted,
            Project = project,
            AppendToProjectId = Guid.NewGuid(),
        };
        using var stream = new MemoryStream();
        await ShellProcessPipe.WriteAsync(stream, request);
        stream.Position = 0;
        var actual = await ShellProcessPipe.ReadAsync<ShellProcessRequest>(stream);
        Assert.AreEqual(request.Id, actual.Id);
        Assert.AreEqual(request.AppendToProjectId, actual.AppendToProjectId);
        Assert.AreEqual(project.Id, actual.Project!.Id);
        Assert.AreEqual(project.Duration, actual.Project.Duration);
        Assert.AreEqual(project.DpiScale, actual.Project.DpiScale);
        Assert.AreEqual(project.CropOffsetX, actual.Project.CropOffsetX);
        Assert.AreEqual(project.CropOffsetY, actual.Project.CropOffsetY);
        Assert.AreEqual(project.AudioToVideoOffsetSeconds, actual.Project.AudioToVideoOffsetSeconds);
        Assert.AreEqual(project.MouseToVideoOffsetSeconds, actual.Project.MouseToVideoOffsetSeconds);
        CollectionAssert.AreEqual(project.AudioFilePaths, actual.Project.AudioFilePaths);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(ShellProcessPipe.MaximumMessageBytes + 1)]
    public async Task InvalidFrameLength_IsRejectedBeforeAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ShellProcessPipe.ReadAsync<ShellProcessRequest>(stream));
    }

    [TestMethod]
    public async Task TruncatedFrame_DoesNotBecomeSuccessfulRequest()
    {
        using var stream = new MemoryStream();
        await ShellProcessPipe.WriteAsync(stream, new ShellProcessRequest());
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => ShellProcessPipe.ReadAsync<ShellProcessRequest>(stream));
    }

    [TestMethod]
    public async Task Pipe_ServesConcurrentRequestsAndRejectsUnknownProtocol()
    {
        string name = "Mixtri-test-" + Guid.NewGuid().ToString("N");
        int calls = 0;
        using var server = new ShellProcessPipe(name, request =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ShellProcessResponse(true));
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ShellProcessPipe.SendAsync(name, new() { Command = ShellProcessCommand.Ping })));
        Assert.IsTrue(results.All(result => result.Success));
        var rejected = await ShellProcessPipe.SendAsync(name, new() { Version = 99 });
        Assert.IsFalse(rejected.Success);
        Assert.AreEqual(4, calls);
    }

    [TestMethod]
    public async Task Pipe_ShutdownStillAcknowledgesTheRequestThatClosedIt()
    {
        string name = "Mixtri-test-" + Guid.NewGuid().ToString("N");
        ShellProcessPipe? server = null;
        server = new ShellProcessPipe(name, request =>
        {
            server!.Dispose();
            return Task.FromResult(new ShellProcessResponse(true));
        });
        using (server)
        {
            var response = await ShellProcessPipe.SendAsync(name, new() { Command = ShellProcessCommand.CloseWorkspace });
            Assert.IsTrue(response.Success);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingRecorder_CanBeCancelled(bool alreadyCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) cancellation.Cancel();
        else cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            await ShellProcessPipe.SendAsync(
                "Mixtri-test-" + Guid.NewGuid().ToString("N"), new(), cancellation.Token);
            Assert.Fail("The missing-recorder request completed without observing cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    [TestMethod]
    public void RecordingDelivery_PreservesUnsavedWorkAndAppendIdentity()
    {
        var current = Guid.NewGuid();
        Assert.IsNotNull(RecordingDeliveryPolicy.GetRejection(current, true, false, null));
        Assert.IsNotNull(RecordingDeliveryPolicy.GetRejection(current, false, false, Guid.NewGuid()));
        Assert.IsNotNull(RecordingDeliveryPolicy.GetRejection(null, false, false, current));
        Assert.IsNotNull(RecordingDeliveryPolicy.GetRejection(current, true, true, current));
        Assert.IsNull(RecordingDeliveryPolicy.GetRejection(current, true, false, current));
        Assert.IsNull(RecordingDeliveryPolicy.GetRejection(null, false, false, null));
    }

    [TestMethod]
    public async Task RecordingHandoff_PersistsUntilAcknowledgedAndCanBeRetried()
    {
        string root = Path.Combine(Path.GetTempPath(), "Mixtri-handoff-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecordingHandoffStore(root);
            var request = new ShellProcessRequest
            {
                Command = ShellProcessCommand.RecordingCompleted,
                Project = new Project { VideoFilePath = @"C:\recording\video.mp4" },
                AppendToProjectId = Guid.NewGuid(),
            };
            await store.SaveAsync(request);
            await store.SaveAsync(request);
            var restored = new RecordingHandoffStore(root).ReadPending().Single();
            Assert.AreEqual(request.Id, restored.Id);
            Assert.AreEqual(request.Project.Id, restored.Project!.Id);
            Assert.AreEqual(request.AppendToProjectId, restored.AppendToProjectId);
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp").Any());
            store.Acknowledge(request.Id);
            Assert.IsFalse(store.ReadPending().Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A malformed handoff must not strand the valid ones behind it. ReadPending is a lazy
    /// iterator, so throwing on the first bad file abandoned every later recording and left
    /// the bad file in place to fail identically on every subsequent open.
    /// </summary>
    [TestMethod]
    public async Task CorruptHandoff_IsQuarantinedAndLaterRecordingsStillRecover()
    {
        string root = Path.Combine(Path.GetTempPath(), "Mixtri-handoff-bad-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecordingHandoffStore(root);
            Directory.CreateDirectory(root);

            // Ordinal ordering puts the corrupt file first.
            string corrupt = Path.Combine(root, $"{new Guid("00000000-0000-0000-0000-0000000000ff"):N}.json");
            await File.WriteAllTextAsync(corrupt, "{ this is not valid json");

            var good = new ShellProcessRequest
            {
                Id = new Guid("ffffffff-0000-0000-0000-000000000001"),
                Command = ShellProcessCommand.RecordingCompleted,
                Project = new Project { VideoFilePath = @"C:\recording\good.mp4" },
            };
            await store.SaveAsync(good);

            var recovered = store.ReadPending().ToList();
            Assert.AreEqual(1, recovered.Count, "the valid handoff behind the corrupt one must still be recovered");
            Assert.AreEqual(good.Id, recovered[0].Id);

            Assert.IsFalse(File.Exists(corrupt), "the corrupt file must not be left to fail again");
            Assert.IsTrue(File.Exists(corrupt + ".bad"), "the corrupt file should be quarantined for diagnosis");

            // A second pass is clean, proving the bad file is no longer in the rotation.
            Assert.AreEqual(1, store.ReadPending().Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Re-entrant saves for one id must not share a scratch file: CompleteRecordingAsync is
    /// not covered by the open gate, so a recovery save can overlap an in-flight one and each
    /// one's cleanup could delete the other's temporary.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentSavesOfTheSameRecording_DoNotCollideOnATemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "Mixtri-handoff-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecordingHandoffStore(root);
            var request = new ShellProcessRequest
            {
                Command = ShellProcessCommand.RecordingCompleted,
                Project = new Project { VideoFilePath = @"C:\recording\video.mp4" },
            };

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.SaveAsync(request)));

            Assert.AreEqual(request.Id, store.ReadPending().Single().Id);
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp").Any(), "no scratch file may survive");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
