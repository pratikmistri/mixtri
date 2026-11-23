using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Services;

/// <summary>
/// Best-effort pruning of the extracted-project cache (<c>OpenProjects\&lt;projectId&gt;</c>,
/// including its <c>alt-*</c> relocations and <c>.part</c> scratch files). Nothing else ever
/// removes those folders, so without this every project ever opened stays on disk.
/// </summary>
/// <remarks>
/// A cache folder is only an extraction of a <c>.mixtri</c> file, so deleting one costs a
/// re-extract on the next open and never loses user work. The rules are still conservative:
/// a folder is removed only when EVERYTHING in it is older than the cutoff (reuse of an
/// extraction does not touch it, so age is by last write of the newest file), and folders
/// the caller names as in use are never touched. Never throws.
/// </remarks>
public static class ProjectCacheCleanupService
{
    /// <summary>Default age after which an unused project cache folder is pruned.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    /// <summary>A <c>.part</c> file this old is a dead extraction, not one still being written.</summary>
    public static readonly TimeSpan StalePartAge = TimeSpan.FromHours(24);

    // Serialises "decide a folder is stale and delete it" against a project open claiming
    // that folder. An open takes a lease (under the gate) before it stamps or extracts into
    // the folder; the prune re-checks both the lease and the age under the same gate right
    // before deleting, so a folder an open has touched is never removed under it.
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, int> Leases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks the cache folder <paramref name="folderName"/> (the project id in "N" format) as
    /// in use until the returned handle is disposed. Blocks while a prune is deleting it, so
    /// the caller then simply re-creates and re-extracts.
    /// </summary>
    public static IDisposable Lease(string folderName)
    {
        lock (CacheGate)
        {
            Leases[folderName] = Leases.TryGetValue(folderName, out int n) ? n + 1 : 1;
        }
        return new LeaseHandle(folderName);
    }

    private sealed class LeaseHandle(string folderName) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (CacheGate)
            {
                if (Leases.TryGetValue(folderName, out int n) && n > 1)
                    Leases[folderName] = n - 1;
                else
                    Leases.Remove(folderName);
            }
        }
    }

    /// <summary>
    /// Prunes <paramref name="cacheRoot"/>. <paramref name="inUseFolderNames"/> are top-level
    /// folder names (the project id in "N" format) that must be kept regardless of age.
    /// Returns the number of folders removed.
    /// </summary>
    public static int PruneStaleProjects(
        string cacheRoot,
        TimeSpan maxAge,
        IReadOnlyCollection<string>? inUseFolderNames = null,
        DateTime? nowUtc = null)
    {
        int removed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
                return 0;

            var now = nowUtc ?? DateTime.UtcNow;
            var projectCutoff = now - maxAge;
            var partCutoff = now - StalePartAge;

            foreach (var folder in Directory.EnumerateDirectories(cacheRoot))
            {
                try
                {
                    var name = Path.GetFileName(folder);
                    if (inUseFolderNames is not null
                        && inUseFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // Reparse points are never followed or deleted through.
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if (NewestWriteUtc(folder) >= projectCutoff)
                    {
                        // A live extraction writes .part files stamped with the zip entry's
                        // ORIGINAL time before renaming them, so they can look stale: never
                        // touch a leased folder's parts, and decide under the gate.
                        lock (CacheGate)
                        {
                            if (!Leases.ContainsKey(name))
                                DeleteStaleParts(folder, partCutoff);
                        }
                        continue;
                    }

                    lock (CacheGate)
                    {
                        // Re-check under the gate: an open may have leased or stamped the
                        // folder since the scan above.
                        if (Leases.ContainsKey(name) || NewestWriteUtc(folder) >= projectCutoff)
                            continue;

                        Directory.Delete(folder, recursive: true);
                    }
                    removed++;
                    DiagLog.Write("ProjectCache", $"Pruned stale project cache '{name}'.");
                }
                catch (Exception ex)
                {
                    DiagLog.Write("ProjectCache", $"Could not prune '{folder}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("ProjectCache", $"Project cache prune failed: {ex.Message}");
        }

        return removed;
    }

    private static DateTime NewestWriteUtc(string folder)
    {
        var newest = Directory.GetLastWriteTimeUtc(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
            catch
            {
                // Unreadable means unknown: treat as fresh so it is kept.
                return DateTime.MaxValue;
            }
        }

        return newest;
    }

    private static void DeleteStaleParts(string folder, DateTime partCutoffUtc)
    {
        foreach (var part in Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(part) < partCutoffUtc)
                    File.Delete(part);
            }
            catch
            {
                // Locked or already gone; the next startup tries again.
            }
        }
    }
}
