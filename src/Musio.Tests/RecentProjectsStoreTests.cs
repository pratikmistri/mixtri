using Musio.Core.Projects;

namespace Musio.Tests;

/// <summary>
/// The recent-projects index is a convenience cache, so its contract is mostly about
/// degrading quietly: a moved file, a corrupt index, or a duplicate save must never
/// surface as an error.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RecentProjectsStoreTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_recent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // Each test gets its own index, so runs never collide or disturb the real one.
        RecentProjectsStore.SetIndexPathForTesting(Path.Combine(_root, "recent-projects.json"));
    }

    [TestCleanup]
    public void TearDown()
    {
        RecentProjectsStore.SetIndexPathForTesting(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateProjectFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "stub");
        return path;
    }

    [TestMethod]
    public void Remember_ThenLoad_ReturnsTheEntry()
    {
        var path = CreateProjectFile("a.musio");

        RecentProjectsStore.Remember(path, "Project A", TimeSpan.FromSeconds(12));
        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(path, entries[0].Path);
        Assert.AreEqual("Project A", entries[0].Name);
        Assert.AreEqual(TimeSpan.FromSeconds(12), entries[0].Duration);
    }

    [TestMethod]
    public void Remember_MostRecentComesFirst()
    {
        var a = CreateProjectFile("a.musio");
        var b = CreateProjectFile("b.musio");

        RecentProjectsStore.Remember(a, "A", TimeSpan.Zero);
        RecentProjectsStore.Remember(b, "B", TimeSpan.Zero);

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(b, entries[0].Path);
        Assert.AreEqual(a, entries[1].Path);
    }

    [TestMethod]
    public void Remember_SamePathTwice_DoesNotDuplicate()
    {
        var path = CreateProjectFile("a.musio");

        RecentProjectsStore.Remember(path, "First", TimeSpan.Zero);
        RecentProjectsStore.Remember(path, "Renamed", TimeSpan.FromSeconds(5));

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("Renamed", entries[0].Name, "the newer save should win");
    }

    [TestMethod]
    public void Load_OmitsEntriesWhoseFileIsGone()
    {
        var kept = CreateProjectFile("kept.musio");
        var removed = CreateProjectFile("removed.musio");

        RecentProjectsStore.Remember(kept, "Kept", TimeSpan.Zero);
        RecentProjectsStore.Remember(removed, "Removed", TimeSpan.Zero);
        File.Delete(removed);

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(kept, entries[0].Path);
    }

    [TestMethod]
    public void Forget_RemovesTheEntryButNotTheFile()
    {
        var path = CreateProjectFile("a.musio");
        RecentProjectsStore.Remember(path, "A", TimeSpan.Zero);

        RecentProjectsStore.Forget(path);

        Assert.AreEqual(0, RecentProjectsStore.Load().Count);
        Assert.IsTrue(File.Exists(path), "forgetting must never delete the user's project");
    }

    [TestMethod]
    public void Load_CorruptIndex_ReturnsEmptyRatherThanThrowing()
    {
        var dir = Path.GetDirectoryName(RecentProjectsStore.IndexPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(RecentProjectsStore.IndexPath, "{ this is not json");

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void Remember_IgnoresBlankPaths()
    {
        RecentProjectsStore.Remember("", "Nothing", TimeSpan.Zero);
        RecentProjectsStore.Remember("   ", "Nothing", TimeSpan.Zero);

        Assert.AreEqual(0, RecentProjectsStore.Load().Count);
    }
}
