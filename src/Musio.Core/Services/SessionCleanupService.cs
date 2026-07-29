using System.Diagnostics;
using Musio.Core.Processing;

namespace Musio.Core.Services;

/// <summary>
/// Reclaims disk space from recording session folders by removing the transient
/// <c>.frames/</c> scratch directories.
/// </summary>
/// <remarks>
/// <para>
/// <c>VideoWriter</c> normally deletes its own frames the moment the MP4 finalizes, so
/// this is a backstop for sessions that were interrupted — a crash between capture and
/// finalization, or an app kill — plus a migration path for sessions recorded before the
/// editor could decode MP4s.
/// </para>
/// <para>
/// The safety rule is absolute: frames are only ever deleted when a non-empty
/// <c>video.mp4</c> exists beside them. Until it does, they are the only copy of the
/// recording. Export is no longer a precondition, because a project stays fully editable
/// from its MP4.
/// </para>
/// </remarks>
public static class SessionCleanupService
{
    private const string ExportMarker = "exported.marker";
    private static readonly string FramesDir = VideoFrameReader.FramesDirectoryName;

    /// <summary>
    /// Writes a marker file recording that this session has been exported at least once.
    /// Retained for capture history; it is no longer a precondition for cleanup.
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
    /// Deletes the <c>.frames/</c> directory and finalize_debug.log from a session folder.
    /// Only proceeds if video.mp4 exists and is non-empty, so an interrupted recording
    /// never loses its only copy.
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
    /// Cleans up leftover <c>.frames/</c> from every session folder that has a finalized
    /// MP4. Runs synchronously on a background thread — call via Task.Run.
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
                // Skip sessions whose .frames/ was already released
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
    /// Calculates total reclaimable space from sessions that still have a <c>.frames/</c>
    /// directory alongside a finalized MP4.
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
