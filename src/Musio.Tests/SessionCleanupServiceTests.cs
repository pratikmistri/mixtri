using Musio.Core.Capture;
using Musio.Core.Services;

namespace Musio.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SessionCleanupServiceTests
{
    private string _testRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"musio_cleanup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    private string CreateSessionFolder(string name, bool withVideo = true, bool withFrames = false,
        bool withMarker = false, long videoSize = 1024, bool finalized = true)
    {
        var dir = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(dir);

        if (withVideo)
        {
            var videoPath = Path.Combine(dir, "video.mp4");
            File.WriteAllBytes(videoPath, new byte[videoSize]);
        }

        if (withFrames)
        {
            var framesDir = Path.Combine(dir, ".frames");
            Directory.CreateDirectory(framesDir);
            File.WriteAllBytes(Path.Combine(framesDir, "frame001.bin"), new byte[500]);
            File.WriteAllBytes(Path.Combine(framesDir, "frame002.bin"), new byte[500]);
        }

        if (withMarker)
        {
            File.WriteAllText(Path.Combine(dir, "exported.marker"), DateTimeOffset.UtcNow.ToString("O"));
        }

        // Proves the MP4 came from a completed run of the current encoder. Without it the
        // session is treated as legacy and its frames are never released.
        if (withVideo && finalized)
        {
            File.WriteAllText(
                Path.Combine(dir, Musio.Core.Capture.VideoWriter.FinalizedMarkerName),
                $"{{\"encoderVersion\":{Musio.Core.Capture.VideoWriter.EncoderVersion}}}");
        }

        return dir;
    }

    #region MarkSessionExported

    [TestMethod]
    public void MarkSessionExported_CreatesMarkerFile()
    {
        var sessionDir = CreateSessionFolder("session_mark");

        SessionCleanupService.MarkSessionExported(sessionDir);

        Assert.IsTrue(File.Exists(Path.Combine(sessionDir, "exported.marker")));
    }

    [TestMethod]
    public void MarkSessionExported_NonExistentFolder_DoesNotThrow()
    {
        var fakeDir = Path.Combine(_testRoot, "nonexistent");

        // Should not throw — it swallows exceptions
        SessionCleanupService.MarkSessionExported(fakeDir);
    }

    #endregion

    #region HasValidVideo

    [TestMethod]
    public void HasValidVideo_WithVideo_ReturnsTrue()
    {
        var dir = CreateSessionFolder("session_valid", withVideo: true);
        Assert.IsTrue(SessionCleanupService.HasValidVideo(dir));
    }

    [TestMethod]
    public void HasValidVideo_NoVideo_ReturnsFalse()
    {
        var dir = CreateSessionFolder("session_novideo", withVideo: false);
        Assert.IsFalse(SessionCleanupService.HasValidVideo(dir));
    }

    [TestMethod]
    public void HasValidVideo_EmptyVideo_ReturnsFalse()
    {
        var dir = CreateSessionFolder("session_empty", withVideo: true, videoSize: 0);
        Assert.IsFalse(SessionCleanupService.HasValidVideo(dir));
    }

    [TestMethod]
    public void HasValidVideo_NonExistentFolder_ReturnsFalse()
    {
        Assert.IsFalse(SessionCleanupService.HasValidVideo(Path.Combine(_testRoot, "nope")));
    }

    #endregion

    #region CleanupSession

    [TestMethod]
    public void CleanupSession_WithFrames_DeletesAndReturnsBytes()
    {
        var dir = CreateSessionFolder("session_cleanup", withVideo: true, withFrames: true);

        long reclaimed = SessionCleanupService.CleanupSession(dir);

        Assert.IsTrue(reclaimed > 0, "Should reclaim bytes from .frames/");
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, ".frames")), ".frames should be deleted");
    }

    [TestMethod]
    public void CleanupSession_NoVideo_ReturnsZero()
    {
        var dir = CreateSessionFolder("session_novid_cleanup", withVideo: false, withFrames: true);

        long reclaimed = SessionCleanupService.CleanupSession(dir);

        Assert.AreEqual(0, reclaimed, "Should not clean up without valid video");
        Assert.IsTrue(Directory.Exists(Path.Combine(dir, ".frames")), ".frames should still exist");
    }

    [TestMethod]
    public void CleanupSession_NoFrames_ReturnsZero()
    {
        var dir = CreateSessionFolder("session_noframes", withVideo: true, withFrames: false);

        long reclaimed = SessionCleanupService.CleanupSession(dir);

        Assert.AreEqual(0, reclaimed);
    }

    [TestMethod]
    public void CleanupSession_WithDebugLog_DeletesIt()
    {
        var dir = CreateSessionFolder("session_debuglog", withVideo: true);
        var logPath = Path.Combine(dir, "finalize_debug.log");
        File.WriteAllText(logPath, "debug info");

        long reclaimed = SessionCleanupService.CleanupSession(dir);

        Assert.IsTrue(reclaimed > 0, "Should count the log file bytes");
        Assert.IsFalse(File.Exists(logPath), "Debug log should be deleted");
    }

    #endregion

    #region CleanupExportedSessions

    [TestMethod]
    public void CleanupExportedSessions_CleansEverySessionWithAFinalizedVideo()
    {
        CreateSessionFolder("session_a", withVideo: true, withFrames: true, withMarker: true);
        CreateSessionFolder("session_b", withVideo: true, withFrames: true, withMarker: false);

        long reclaimed = SessionCleanupService.CleanupExportedSessions(_testRoot);

        Assert.IsTrue(reclaimed > 0, "Should reclaim from both sessions");
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, "session_a", ".frames")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, "session_b", ".frames")),
            "Export is no longer a precondition — a finalized MP4 keeps the project editable");
    }

    [TestMethod]
    public void CleanupExportedSessions_PreservesFramesWhenVideoIsMissing()
    {
        CreateSessionFolder("session_unfinalized", withVideo: false, withFrames: true, withMarker: true);

        long reclaimed = SessionCleanupService.CleanupExportedSessions(_testRoot);

        Assert.AreEqual(0, reclaimed);
        Assert.IsTrue(Directory.Exists(Path.Combine(_testRoot, "session_unfinalized", ".frames")),
            "Frames are the only copy of a recording whose MP4 never finalized");
    }

    [TestMethod]
    public void CleanupExportedSessions_PreservesFramesOfLegacySessions()
    {
        // No finalized marker: the MP4 predates the orientation fix, so it is vertically
        // flipped and the JPEGs are the only correctly oriented copy.
        CreateSessionFolder("session_legacy", withVideo: true, withFrames: true, finalized: false);

        long reclaimed = SessionCleanupService.CleanupExportedSessions(_testRoot);

        Assert.AreEqual(0, reclaimed);
        Assert.IsTrue(Directory.Exists(Path.Combine(_testRoot, "session_legacy", ".frames")),
            "A legacy session's frames must never be deleted — its MP4 is unusable");
    }

    [TestMethod]
    public void CanReleaseFrames_RequiresBothVideoAndFinalizedMarker()
    {
        var complete = CreateSessionFolder("session_complete", withVideo: true);
        var legacy = CreateSessionFolder("session_nomarker", withVideo: true, finalized: false);
        var novideo = CreateSessionFolder("session_novideo", withVideo: false);

        Assert.IsTrue(SessionCleanupService.CanReleaseFrames(complete));
        Assert.IsFalse(SessionCleanupService.CanReleaseFrames(legacy));
        Assert.IsFalse(SessionCleanupService.CanReleaseFrames(novideo));
    }

    [TestMethod]
    public void CleanupExportedSessions_NonExistentFolder_ReturnsZero()
    {
        Assert.AreEqual(0, SessionCleanupService.CleanupExportedSessions(
            Path.Combine(_testRoot, "doesntexist")));
    }

    #endregion

    #region GetReclaimableSpace

    [TestMethod]
    public void GetReclaimableSpace_CalculatesCorrectly()
    {
        CreateSessionFolder("session_reclaimable", withVideo: true, withFrames: true, withMarker: true);

        long space = SessionCleanupService.GetReclaimableSpace(_testRoot);

        Assert.IsTrue(space > 0);
    }

    [TestMethod]
    public void GetReclaimableSpace_NoMarker_StillCountsFinalizedSession()
    {
        CreateSessionFolder("session_nomarker", withVideo: true, withFrames: true, withMarker: false);

        long space = SessionCleanupService.GetReclaimableSpace(_testRoot);

        Assert.IsTrue(space > 0, "A finalized MP4 is what makes frames reclaimable, not an export");
    }

    [TestMethod]
    public void GetReclaimableSpace_NoVideo_ReturnsZero()
    {
        CreateSessionFolder("session_novideo", withVideo: false, withFrames: true, withMarker: true);

        long space = SessionCleanupService.GetReclaimableSpace(_testRoot);

        Assert.AreEqual(0, space);
    }

    [TestMethod]
    public void GetReclaimableSpace_LegacySession_ReturnsZero()
    {
        CreateSessionFolder("session_legacy", withVideo: true, withFrames: true, finalized: false);

        long space = SessionCleanupService.GetReclaimableSpace(_testRoot);

        Assert.AreEqual(0, space, "a legacy session's frames are not reclaimable");
    }

    [TestMethod]
    public void GetReclaimableSpace_NonExistentFolder_ReturnsZero()
    {
        Assert.AreEqual(0, SessionCleanupService.GetReclaimableSpace(
            Path.Combine(_testRoot, "nope")));
    }

    #endregion

    #region CleanupOrphanedImports

    /// <summary>
    /// Creates an <c>import_&lt;guid&gt;</c> folder like <c>VideoImportService</c> would, then
    /// optionally ages it by back-dating the folder and every file in it.
    /// </summary>
    private string CreateImportFolder(
        bool withVideo, bool withPartial = false, TimeSpan? age = null,
        string? explicitName = null, long payloadSize = 4096)
    {
        var name = explicitName ?? (SessionPaths.ImportFolderPrefix + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(dir);

        if (withVideo)
        {
            File.WriteAllBytes(Path.Combine(dir, "video.mp4"), new byte[payloadSize]);
            File.WriteAllText(
                Path.Combine(dir, Musio.Core.Capture.VideoWriter.FinalizedMarkerName),
                $"{{\"encoderVersion\":{Musio.Core.Capture.VideoWriter.EncoderVersion}}}");
        }
        if (withPartial)
        {
            File.WriteAllBytes(Path.Combine(dir, "video.mp4.partial"), new byte[payloadSize]);
        }

        if (age is { } a)
        {
            var when = DateTime.UtcNow - a;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(f, when);
            Directory.SetLastWriteTimeUtc(dir, when);
        }

        return dir;
    }

    [TestMethod]
    public void CleanupOrphanedImports_StaleFailedImport_IsReclaimed()
    {
        // Old, no finished video.mp4 — only a leftover partial: a provably-abandoned import.
        var dir = CreateImportFolder(withVideo: false, withPartial: true, age: TimeSpan.FromDays(3));

        long reclaimed = SessionCleanupService.CleanupOrphanedImports(_testRoot);

        Assert.IsFalse(Directory.Exists(dir), "a stale, video-less import folder should be deleted");
        Assert.IsTrue(reclaimed > 0, "the reclaimed byte count should reflect the deleted partial");
    }

    [TestMethod]
    public void CleanupOrphanedImports_CompletedImport_IsNeverDeletedEvenWhenOld()
    {
        // The critical safety case: a finished import can be the live media of an unsaved
        // project the user is still editing. Age must NOT make it eligible for deletion.
        var dir = CreateImportFolder(withVideo: true, age: TimeSpan.FromDays(365));

        long reclaimed = SessionCleanupService.CleanupOrphanedImports(_testRoot);

        Assert.IsTrue(Directory.Exists(dir),
            "an import folder holding a valid video.mp4 must never be deleted, however old");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "video.mp4")));
        Assert.AreEqual(0, reclaimed);
    }

    [TestMethod]
    public void CleanupOrphanedImports_FreshFailedImport_IsPreserved()
    {
        // No video.mp4 but recent — this could be an import in flight right now. Age gate spares it.
        var dir = CreateImportFolder(withVideo: false, withPartial: true, age: TimeSpan.FromMinutes(5));

        long reclaimed = SessionCleanupService.CleanupOrphanedImports(_testRoot);

        Assert.IsTrue(Directory.Exists(dir), "a recent import folder must be preserved (may be in flight)");
        Assert.AreEqual(0, reclaimed);
    }

    [TestMethod]
    public void CleanupOrphanedImports_IgnoresNonImportFolders()
    {
        // A session folder and a foreign folder that merely starts with the prefix but is not
        // one of our GUID-named imports must both be left untouched.
        var session = CreateSessionFolder("session_keep", withVideo: false, withFrames: true);
        var foreign = CreateImportFolder(
            withVideo: false, withPartial: true, age: TimeSpan.FromDays(10),
            explicitName: "import_not_a_guid");

        long reclaimed = SessionCleanupService.CleanupOrphanedImports(_testRoot);

        Assert.IsTrue(Directory.Exists(session), "session folders are not the import sweep's business");
        Assert.IsTrue(Directory.Exists(foreign), "a non-GUID 'import_*' folder is not one of ours — leave it");
        Assert.AreEqual(0, reclaimed);
    }

    [TestMethod]
    public void CleanupOrphanedImports_IsFoldedIntoStartupSweep()
    {
        // The backstop must actually run from the startup entry point App calls, not only when
        // invoked directly — otherwise nothing ever reclaims orphaned imports.
        var stale = CreateImportFolder(withVideo: false, withPartial: true, age: TimeSpan.FromDays(3));

        SessionCleanupService.CleanupExportedSessions(_testRoot);

        Assert.IsFalse(Directory.Exists(stale),
            "CleanupExportedSessions should also sweep stale orphaned imports");
    }

    [TestMethod]
    public void CleanupOrphanedImports_NonExistentFolder_ReturnsZero()
    {
        Assert.AreEqual(0, SessionCleanupService.CleanupOrphanedImports(
            Path.Combine(_testRoot, "nope")));
    }

    #endregion
}
