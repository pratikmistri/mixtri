using System.Text.Json;
using Mixtri.Core.Projects;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for the recent-projects index write path: writes are atomic and keep a
/// backup, and a corrupt index is moved aside and recovered from that backup instead of
/// being overwritten with a single entry.
/// </summary>
[TestClass]
[DoNotParallelize] // RecentProjectsStore's index path override is process-wide static state.
public sealed class ModelReviewRecentProjectsTests
{
    private TempDirectoryFixture? _tempDir;
    private string Root => _tempDir!.Path;
    private string IndexPath => Path.Combine(Root, "recent-projects.json");

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_recent_review_");
        RecentProjectsStore.SetIndexPathForTesting(IndexPath);
    }

    [TestCleanup]
    public void TearDown()
    {
        RecentProjectsStore.SetIndexPathForTesting(null);
        _tempDir?.Dispose();
    }

    private string CreateProjectFile(string name)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, "stub");
        return path;
    }

    [TestMethod]
    public void Remember_WritesAtomicallyAndKeepsABackup()
    {
        var a = CreateProjectFile("a.mixtri");
        var b = CreateProjectFile("b.mixtri");

        RecentProjectsStore.Remember(a, "A", TimeSpan.Zero);
        RecentProjectsStore.Remember(b, "B", TimeSpan.Zero);

        Assert.IsTrue(File.Exists(IndexPath));
        Assert.IsTrue(File.Exists(IndexPath + RecentProjectsStore.BackupSuffix),
            "replacing the index keeps the previous copy");
        Assert.IsFalse(File.Exists(IndexPath + ".tmp"), "no temp file is left behind");
        Assert.AreEqual(2, RecentProjectsStore.Load().Count);
    }

    [TestMethod]
    public void Remember_OnCorruptIndex_RecoversFromBackupAndKeepsCorruptCopy()
    {
        var a = CreateProjectFile("a.mixtri");
        var b = CreateProjectFile("b.mixtri");
        var c = CreateProjectFile("c.mixtri");

        File.WriteAllText(IndexPath + RecentProjectsStore.BackupSuffix, JsonSerializer.Serialize(new List<RecentProject>
        {
            new() { Path = a, Name = "A", LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new() { Path = b, Name = "B", LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
        }));
        File.WriteAllText(IndexPath, "{ this is not json");

        RecentProjectsStore.Remember(c, "C", TimeSpan.Zero);

        var names = RecentProjectsStore.Load().Select(e => e.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "A", "B", "C" }, names,
            "a corrupt index must not be replaced by a single entry");
        Assert.IsTrue(File.Exists(IndexPath + RecentProjectsStore.CorruptSuffix),
            "the corrupt index is kept aside for diagnosis");
    }

    [TestMethod]
    public void Load_OnCorruptIndexWithoutBackup_ReturnsEmpty()
    {
        File.WriteAllText(IndexPath, "{ this is not json");

        Assert.AreEqual(0, RecentProjectsStore.Load().Count);
    }
}
