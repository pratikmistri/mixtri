using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
            using (CrossProcessLock.Acquire(IndexPath))
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
            using (CrossProcessLock.Acquire(IndexPath))
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

    /// <summary>Suffix of the previous good index, kept by every <see cref="Save"/>.</summary>
    internal const string BackupSuffix = ".bak";

    /// <summary>Suffix a corrupt index is moved aside to, so a later save cannot erase it.</summary>
    internal const string CorruptSuffix = ".corrupt";

    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Reads an index, recovering from corruption.
    /// </summary>
    /// <remarks>
    /// A corrupt index used to read as an empty list, and the next <see cref="Remember"/>
    /// then overwrote it with a single entry — the whole list gone for one bad write. Now
    /// the corrupt file is moved aside (<see cref="CorruptSuffix"/>) for diagnosis and the
    /// previous good copy (<see cref="BackupSuffix"/>) is used instead. Only the index this
    /// store writes is moved; the read-only legacy index is merely skipped.
    /// </remarks>
    private static List<RecentProject> ReadIndex(string path, bool recover = true)
    {
        if (!File.Exists(path))
            return [];

        if (TryParse(path, out var entries))
            return entries;

        if (!recover)
            return [];

        try
        {
            File.Move(path, path + CorruptSuffix, overwrite: true);
            Debug.WriteLine($"[RecentProjects] Index was corrupt; moved aside to '{path + CorruptSuffix}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[RecentProjects] Could not move corrupt index aside: {ex.Message}");
        }

        var backup = path + BackupSuffix;
        try
        {
            return File.Exists(backup) && TryParse(backup, out var restored) ? restored : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[RecentProjects] Could not read index backup: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Parses an index. Only unparseable CONTENT counts as corruption; an I/O failure (the
    /// file momentarily locked by another process's save) propagates, so the caller fails
    /// this one operation instead of treating a good index as corrupt and replacing it.
    /// </summary>
    private static bool TryParse(string path, out List<RecentProject> entries)
    {
        var json = File.ReadAllText(path);
        try
        {
            entries = JsonSerializer.Deserialize<List<RecentProject>>(json, JsonOptions) ?? [];
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            entries = [];
            return false;
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

        List<RecentProject> legacy;
        try
        {
            legacy = ReadIndex(legacyPath, recover: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The legacy index is a one-time migration source; an unreadable one is skipped.
            Debug.WriteLine($"[RecentProjects] Could not read legacy index: {ex.Message}");
            legacy = [];
        }
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

        // Write-then-swap: a crash or full disk mid-write leaves the previous index intact
        // rather than a truncated file, and the replaced copy is kept as the backup that
        // ReadIndex falls back to.
        var indexPath = IndexPath;
        var tempPath = indexPath + TempSuffix;
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions));

        try
        {
            if (File.Exists(indexPath))
            {
                try
                {
                    File.Replace(tempPath, indexPath, indexPath + BackupSuffix, ignoreMetadataErrors: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
                {
                    // File.Replace is unavailable on some volumes; fall back to a plain move
                    // after taking the backup by copy.
                    Debug.WriteLine($"[RecentProjects] File.Replace failed, falling back: {ex.Message}");
                    File.Copy(indexPath, indexPath + BackupSuffix, overwrite: true);
                }
            }

            File.Move(tempPath, indexPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Serialises read-modify-write of one index across PROCESSES. <see cref="Gate"/> only
    /// covers threads in this process, and two Mixtri windows saving at once would otherwise
    /// each read the list, add their own entry and overwrite the other's.
    /// </summary>
    /// <remarks>
    /// Best effort: if the mutex cannot be had within a couple of seconds (or at all) the
    /// write proceeds unguarded rather than losing the entry. The name is derived from the
    /// index path so redirected test indexes never contend with the real one.
    /// </remarks>
    private sealed class CrossProcessLock : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        private readonly Mutex? _mutex;

        private CrossProcessLock(Mutex? mutex) => _mutex = mutex;

        public static CrossProcessLock Acquire(string indexPath)
        {
            Mutex? mutex = null;
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(indexPath).ToUpperInvariant())))[..16];
                mutex = new Mutex(initiallyOwned: false, $@"Local\Mixtri.RecentProjects.{hash}");

                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(Timeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died mid-write; the atomic swap means the file is
                    // still whole, and we now own the mutex.
                    acquired = true;
                }

                if (acquired)
                    return new CrossProcessLock(mutex);

                Debug.WriteLine("[RecentProjects] Timed out waiting for the index lock; writing unguarded.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or WaitHandleCannotBeOpenedException or ArgumentException)
            {
                Debug.WriteLine($"[RecentProjects] Index lock unavailable: {ex.Message}");
            }

            mutex?.Dispose();
            return new CrossProcessLock(null);
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (should not happen) — nothing to release.
            }
            _mutex.Dispose();
        }
    }
}
