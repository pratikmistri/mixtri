using System.Diagnostics;
using System.Text.Json;

namespace Musio.Core.Projects;

/// <summary>
/// One entry in the recent-projects list.
/// </summary>
public sealed class RecentProject
{
    /// <summary>Full path to the <c>.musio</c> file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Project name as saved in the package manifest.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the project was last saved or opened.</summary>
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Recording duration, for display.</summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Persists the list of <c>.musio</c> projects the user has saved or opened.
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

    /// <summary>Path of the index file.</summary>
    public static string IndexPath => _indexPathOverride ?? DefaultIndexPath;

    private static string DefaultIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musio", "recent-projects.json");

    private static string? _indexPathOverride;

    /// <summary>
    /// Redirects the index to <paramref name="path"/>. Tests use this so they neither
    /// interfere with each other nor touch the developer's real project list.
    /// </summary>
    internal static void SetIndexPathForTesting(string? path)
    {
        lock (Gate)
        {
            _indexPathOverride = path;
        }
    }

    /// <summary>
    /// Loads the list, most recently used first, omitting entries whose file no longer
    /// exists.
    /// </summary>
    public static List<RecentProject> Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(IndexPath))
                    return [];

                var json = File.ReadAllText(IndexPath);
                var entries = JsonSerializer.Deserialize<List<RecentProject>>(json, JsonOptions) ?? [];

                return entries
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
                var entries = LoadRaw();

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
                var entries = LoadRaw();
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

    private static List<RecentProject> LoadRaw()
    {
        if (!File.Exists(IndexPath))
            return [];

        try
        {
            var json = File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<List<RecentProject>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // A corrupt index is a cache miss, not a failure worth surfacing.
            return [];
        }
    }

    private static void Save(List<RecentProject> entries)
    {
        var dir = Path.GetDirectoryName(IndexPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }
}
