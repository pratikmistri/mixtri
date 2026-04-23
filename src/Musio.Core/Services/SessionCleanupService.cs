using System.Diagnostics;

namespace Musio.Core.Services;

/// <summary>
/// Cleans up disk space from recording session folders by removing
/// raw .frames/ directories after a successful export. Preserves
/// video.mp4 and metadata files for future capture history.
/// </summary>
public static class SessionCleanupService
{
    private const string ExportMarker = "exported.marker";
    private const string FramesDir = ".frames";

    /// <summary>
    /// Writes a marker file indicating this session has been successfully exported.
    /// Call after a successful export so startup cleanup knows it's safe.
    /// </summary>
    public static void MarkSessionExported(string sessionFolder)
    {
        try
        {
            var markerPath = Path.Combine(sessionFolder, ExportMarker);
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionCleanup] Failed to write export marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the session has a valid video.mp4 (exists, non-zero size).
    /// </summary>
    public static bool HasValidVideo(string sessionFolder)
    {
        var videoPath = Path.Combine(sessionFolder, "video.mp4");
        try
        {
            var fi = new FileInfo(videoPath);
            return fi.Exists && fi.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the .frames/ directory and finalize_debug.log from a session folder.
    /// Only proceeds if video.mp4 exists and is non-empty.
    /// </summary>
    /// <returns>Bytes reclaimed, or 0 if nothing was cleaned.</returns>
    public static long CleanupSession(string sessionFolder)
    {
        if (!HasValidVideo(sessionFolder))
            return 0;

        long reclaimed = 0;

        var framesPath = Path.Combine(sessionFolder, FramesDir);
        if (Directory.Exists(framesPath))
        {
            reclaimed += GetDirectorySize(framesPath);
            try
            {
                Directory.Delete(framesPath, recursive: true);
                Debug.WriteLine($"[SessionCleanup] Deleted {framesPath} ({FormatBytes(reclaimed)})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCleanup] Failed to delete frames: {ex.Message}");
                reclaimed = 0;
            }
        }

        // Also remove the debug log
        var debugLog = Path.Combine(sessionFolder, "finalize_debug.log");
        if (File.Exists(debugLog))
        {
            try
            {
                var logSize = new FileInfo(debugLog).Length;
                File.Delete(debugLog);
                reclaimed += logSize;
            }
            catch { }
        }

        return reclaimed;
    }

    /// <summary>
    /// Cleans up .frames/ from all session folders that have an export marker.
    /// Runs synchronously on a background thread — call via Task.Run.
    /// </summary>
    /// <returns>Total bytes reclaimed.</returns>
    public static long CleanupExportedSessions(string outputFolder)
    {
        if (!Directory.Exists(outputFolder))
            return 0;

        long totalReclaimed = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(outputFolder, "session_*"))
            {
                var markerPath = Path.Combine(dir, ExportMarker);
                if (!File.Exists(markerPath))
                    continue;

                // Skip sessions whose .frames/ was already cleaned
                var framesPath = Path.Combine(dir, FramesDir);
                if (!Directory.Exists(framesPath))
                    continue;

                totalReclaimed += CleanupSession(dir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionCleanup] Error during bulk cleanup: {ex.Message}");
        }

        if (totalReclaimed > 0)
            Debug.WriteLine($"[SessionCleanup] Startup cleanup reclaimed {FormatBytes(totalReclaimed)}");

        return totalReclaimed;
    }

    /// <summary>
    /// Calculates total reclaimable space from exported sessions that still have .frames/.
    /// </summary>
    public static long GetReclaimableSpace(string outputFolder)
    {
        if (!Directory.Exists(outputFolder))
            return 0;

        long total = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(outputFolder, "session_*"))
            {
                var markerPath = Path.Combine(dir, ExportMarker);
                if (!File.Exists(markerPath))
                    continue;

                var framesPath = Path.Combine(dir, FramesDir);
                if (Directory.Exists(framesPath) && HasValidVideo(dir))
                    total += GetDirectorySize(framesPath);
            }
        }
        catch { }

        return total;
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
            _ => $"{bytes} B"
        };
    }
}
