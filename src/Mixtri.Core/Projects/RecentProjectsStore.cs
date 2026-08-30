using System.Diagnostics;
using System.Text.Json;

namespace Mixtri.Core.Projects;

/// <summary>
/// One entry in the recent-projects list.
/// </summary>
public sealed class RecentProject
{
    /// <summary>Full path to the <c>.mixtri</c> file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Project name as saved in the package manifest.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the project was last saved or opened.</summary>
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Recording duration, for display.</summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Persists the list of <c>.mixtri</c> projects the user has saved or opened.
/// </summary>
/// <remarks>
/// Projects are ordinary files the user can put anywhere, so there is no folder to scan.
/// This index is what lets the Open page show them without asking the user to go find
/// them again. It is a convenience cache, never a source of truth: entries whose file has
/// since been moved or deleted are dropped on load rather than reported as errors.
/// </remarks>
public static class RecentProjectsStore
{
    private const int MaxEntries = 60;
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Path of the index file. This is always where writes go.</summary>
    public static string IndexPath => _indexPathOverride ?? DefaultIndexPath;

    private static string DefaultIndexPath => AppDataPaths.Resolve("recent-projects.json");

    /// <summary>
    /// The pre-rename index, or null when a test has redirected <see cref="IndexPath"/>.
    /// </summary>
    /// <remarks>
    /// Read-only, and merged into — never replaced by — the current index, so a user upgrading
    /// across the Musio → Mixtri rename keeps ONE list containing everything they had opened
    /// under either name. Suppressed under a test override so redirected tests stay hermetic
    /// and never see the developer's real project list.
    /// </remarks>
    private static string? LegacyIndexPath => _indexPathOverride is null
        ? AppDataPaths.ResolveLegacy("recent-projects.json")
        : _legacyIndexPathOverride;

    private static string? _indexPathOverride;
    private static string? _legacyIndexPathOverride;

    /// <summary>
    /// Redirects the index to <paramref name="path"/>. Tests use this so they neither
    /// interfere with each other nor touch the developer's real project list.
    /// </summary>
    /// <param name="legacyPath">
    /// Optional pre-rename index to merge from. Defaults to null — a redirected test sees NO
    /// legacy index unless it asks for one, so the merge can never silently pull in the
    /// developer's real <c>LocalAppData\Musio</c> list.
    /// </param>
    internal static void SetIndexPathForTesting(string? path, string? legacyPath = null)
    {
        lock (Gate)
        {
            _indexPathOverride = path;
            _legacyIndexPathOverride = legacyPath;
        }
    }

    /// <summary>
    /// Loads the list, most recently used first, omitting entries whose file no longer
    /// exists.
    /// </summary>
    /// <remarks>
    /// Spans both the current and pre-rename indexes, so projects opened as Musio and as Mixtri
    /// appear in one list. Entries are keyed by path, keeping the most recent timestamp when
    /// both indexes name the same project.
    /// </remarks>
    public static List<RecentProject> Load()
    {
        try
        {
            lock (Gate)
            {
                return MergedRaw()
                    .Where(e => !string.IsNullOrWhiteSpace(e.Path) && File.Exists(e.Path))
                    .OrderByDescending(e => e.LastUsedUtc)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentProjects] Failed to load: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Records that <paramref name="packagePath"/> was just saved or opened, moving it to
    /// the top of the list.
    /// </summary>
    public static void Remember(string packagePath, string name, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return;

        try
        {
            lock (Gate)
            {
                var entries = MergedRaw();

                entries.RemoveAll(e =>
                    string.Equals(e.Path, packagePath, StringComparison.OrdinalIgnoreCase));

                entries.Insert(0, new RecentProject
                {
                    Path = packagePath,
                    Name = name,
                    Duration = duration,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                });

                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

                Save(entries);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentProjects] Failed to remember '{packagePath}': {ex.Message}");
        }
    }

    /// <summary>Removes an entry from the list. The file itself is not touched.</summary>
    public static void Forget(string packagePath)
    {
        try
        {
            lock (Gate)
            {
                var entries = MergedRaw();
                if (entries.RemoveAll(e =>
                        string.Equals(e.Path, packagePath, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    Save(entries);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentProjects] Failed to forget '{packagePath}': {ex.Message}");
        }
    }

    private static List<RecentProject> ReadIndex(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RecentProject>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // A corrupt index is a cache miss, not a failure worth surfacing.
            return [];
        }
    }

    /// <summary>
    /// Raw entries from the current index, merged with the pre-rename index while the migration
    /// is still outstanding. Callers must hold <see cref="Gate"/>.
    /// </summary>
    /// <remarks>
    /// The legacy index is consulted only while the current one does not yet exist. The first
    /// write materialises the merged list at <see cref="IndexPath"/> and thereby completes the
    /// migration — which is also what stops an entry the user has since removed from
    /// reappearing, since the legacy file is never written to and would otherwise keep
    /// re-supplying it.
    /// </remarks>
    private static List<RecentProject> MergedRaw()
    {
        var current = ReadIndex(IndexPath);

        if (File.Exists(IndexPath) || LegacyIndexPath is not { } legacyPath)
            return current;

        var legacy = ReadIndex(legacyPath);
        if (legacy.Count == 0)
            return current;

        // Same project under both names keeps the later timestamp, so merging never
        // demotes something the user opened recently.
        var byPath = new Dictionary<string, RecentProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current.Concat(legacy))
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            if (byPath.TryGetValue(entry.Path, out var existing)
                && existing.LastUsedUtc >= entry.LastUsedUtc)
                continue;

            byPath[entry.Path] = entry;
        }

        // Ordered because Remember trims to MaxEntries from the END: an arbitrary
        // dictionary order would drop a recent project instead of the oldest one.
        return byPath.Values.OrderByDescending(e => e.LastUsedUtc).ToList();
    }

    private static void Save(List<RecentProject> entries)
    {
        var dir = Path.GetDirectoryName(IndexPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }
}
