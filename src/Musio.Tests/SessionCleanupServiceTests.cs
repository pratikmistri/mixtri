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
        bool withMarker = false, long videoSize = 1024)
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
    public void GetReclaimableSpace_NonExistentFolder_ReturnsZero()
    {
        Assert.AreEqual(0, SessionCleanupService.GetReclaimableSpace(
            Path.Combine(_testRoot, "nope")));
    }

    #endregion
}
