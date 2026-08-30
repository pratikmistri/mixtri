using Mixtri.Core.Projects;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// The recent-projects index is a convenience cache, so its contract is mostly about
/// degrading quietly: a moved file, a corrupt index, or a duplicate save must never
/// surface as an error.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RecentProjectsStoreTests
{
    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_recent_");

        // Each test gets its own index, so runs never collide or disturb the real one.
        RecentProjectsStore.SetIndexPathForTesting(Path.Combine(_root, "recent-projects.json"));
    }

    [TestCleanup]
    public void TearDown()
    {
        RecentProjectsStore.SetIndexPathForTesting(null);
        _tempDir?.Dispose();
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
        var path = CreateProjectFile("a.mixtri");

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
        var a = CreateProjectFile("a.mixtri");
        var b = CreateProjectFile("b.mixtri");

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
        var path = CreateProjectFile("a.mixtri");

        RecentProjectsStore.Remember(path, "First", TimeSpan.Zero);
        RecentProjectsStore.Remember(path, "Renamed", TimeSpan.FromSeconds(5));

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("Renamed", entries[0].Name, "the newer save should win");
    }

    [TestMethod]
    public void Load_OmitsEntriesWhoseFileIsGone()
    {
        var kept = CreateProjectFile("kept.mixtri");
        var removed = CreateProjectFile("removed.mixtri");

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
        var path = CreateProjectFile("a.mixtri");
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

    // ── Musio → Mixtri rename: the pre-rename index ──────────────────────
    //
    // The index moved from LocalAppData\Musio to LocalAppData\Mixtri with the rename. Without
    // a merge that silently empties an upgrading user's Open page, which is what these pin.

    /// <summary>Points the store at a current and a legacy index, both under the temp root.</summary>
    private string UseLegacyIndex()
    {
        var legacy = Path.Combine(_root, "legacy-recent-projects.json");
        RecentProjectsStore.SetIndexPathForTesting(
            Path.Combine(_root, "recent-projects.json"), legacy);
        return legacy;
    }

    private void WriteIndex(string path, params RecentProject[] entries) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries.ToList()));

    [TestMethod]
    public void Load_ReturnsEntriesFromTheLegacyIndexWhenNoCurrentIndexExists()
    {
        var legacy = UseLegacyIndex();
        var path = CreateProjectFile("old.musio");
        WriteIndex(legacy, new RecentProject
        {
            Path = path,
            Name = "Recorded as Musio",
            LastUsedUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("Recorded as Musio", entries[0].Name);
    }

    [TestMethod]
    public void Load_MergesBothIndexes()
    {
        var legacy = UseLegacyIndex();
        var oldPath = CreateProjectFile("old.musio");
        var newPath = CreateProjectFile("new.mixtri");

        WriteIndex(legacy, new RecentProject
        {
            Path = oldPath,
            Name = "Old",
            LastUsedUtc = DateTimeOffset.UtcNow.AddDays(-2),
        });
        RecentProjectsStore.Remember(newPath, "New", TimeSpan.Zero);

        // Remember() has already written the merged list forward, so both must be present.
        var entries = RecentProjectsStore.Load();

        CollectionAssert.AreEquivalent(
            new[] { oldPath, newPath },
            entries.Select(e => e.Path).ToList());
    }

    [TestMethod]
    public void Load_SameProjectInBothIndexes_KeepsTheMoreRecentTimestamp()
    {
        var legacy = UseLegacyIndex();
        var path = CreateProjectFile("shared.mixtri");
        var newer = DateTimeOffset.UtcNow;

        WriteIndex(legacy, new RecentProject
        {
            Path = path,
            Name = "Stale name",
            LastUsedUtc = newer.AddDays(-5),
        });
        WriteIndex(RecentProjectsStore.IndexPath, new RecentProject
        {
            Path = path,
            Name = "Current name",
            LastUsedUtc = newer,
        });

        var entries = RecentProjectsStore.Load();

        Assert.AreEqual(1, entries.Count, "the same project must not appear twice");
        Assert.AreEqual("Current name", entries[0].Name);
    }

    /// <summary>
    /// The first write completes the migration. Without that, the legacy index — which is
    /// never written to — would keep re-supplying an entry the user had removed.
    /// </summary>
    [TestMethod]
    public void Forget_OnALegacyEntry_SticksAcrossReload()
    {
        var legacy = UseLegacyIndex();
        var keep = CreateProjectFile("keep.musio");
        var drop = CreateProjectFile("drop.musio");

        WriteIndex(legacy,
            new RecentProject { Path = keep, Name = "Keep", LastUsedUtc = DateTimeOffset.UtcNow },
            new RecentProject { Path = drop, Name = "Drop", LastUsedUtc = DateTimeOffset.UtcNow });

        RecentProjectsStore.Forget(drop);

        var entries = RecentProjectsStore.Load();
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(keep, entries[0].Path);
    }

    [TestMethod]
    public void Load_StillDropsLegacyEntriesWhoseFileIsGone()
    {
        var legacy = UseLegacyIndex();
        WriteIndex(legacy, new RecentProject
        {
            Path = Path.Combine(_root, "never-existed.musio"),
            Name = "Missing",
            LastUsedUtc = DateTimeOffset.UtcNow,
        });

        Assert.AreEqual(0, RecentProjectsStore.Load().Count);
    }
}
