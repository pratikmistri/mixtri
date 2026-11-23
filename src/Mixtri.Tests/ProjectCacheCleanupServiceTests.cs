using Mixtri.Core.Services;

namespace Mixtri.Tests;

[TestClass]
public class ProjectCacheCleanupServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "mixtri-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeProject(string name, TimeSpan age, string file = "video.mp4")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
        return folder;
    }

    [TestMethod]
    public void RemovesOldFolder_KeepsRecentOne()
    {
        var old = MakeProject("old", TimeSpan.FromDays(30));
        var fresh = MakeProject("fresh", TimeSpan.FromDays(1));

        var removed = ProjectCacheCleanupService.PruneStaleProjects(_root, TimeSpan.FromDays(7));

        Assert.AreEqual(1, removed);
        Assert.IsFalse(Directory.Exists(old));
        Assert.IsTrue(Directory.Exists(fresh));
    }

    [TestMethod]
    public void KeepsOldFolder_WhenInUse()
    {
        var old = MakeProject("abc123", TimeSpan.FromDays(30));

        var removed = ProjectCacheCleanupService.PruneStaleProjects(
            _root, TimeSpan.FromDays(7), new[] { "ABC123" });

        Assert.AreEqual(0, removed);
        Assert.IsTrue(Directory.Exists(old));
    }

    [TestMethod]
    public void KeepsFolder_WhenAnyNestedFileIsRecent()
    {
        var folder = MakeProject("proj", TimeSpan.FromDays(30));
        var alt = Path.Combine(folder, "alt-1");
        Directory.CreateDirectory(alt);
        File.WriteAllText(Path.Combine(alt, "audio.wav"), "x");

        var removed = ProjectCacheCleanupService.PruneStaleProjects(_root, TimeSpan.FromDays(7));

        Assert.AreEqual(0, removed);
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public void DeletesStalePartFiles_InKeptFolder_ButNotFreshOnes()
    {
        var folder = MakeProject("proj", TimeSpan.FromDays(1));
        var stale = Path.Combine(folder, "a.wav.part");
        var live = Path.Combine(folder, "b.wav.part");
        File.WriteAllText(stale, "x");
        File.WriteAllText(live, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));

        ProjectCacheCleanupService.PruneStaleProjects(_root, TimeSpan.FromDays(7));

        Assert.IsFalse(File.Exists(stale));
        Assert.IsTrue(File.Exists(live));
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public void MissingRoot_ReturnsZeroWithoutThrowing()
    {
        Assert.AreEqual(0, ProjectCacheCleanupService.PruneStaleProjects(
            Path.Combine(_root, "nope"), TimeSpan.FromDays(7)));
    }
}
