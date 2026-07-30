using System.Diagnostics;
using Musio.Core.Capture;
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
    /// Minimum age before an import folder with no finished <c>video.mp4</c> is considered an
    /// abandoned orphan rather than an import that might still be running. Generous on purpose:
    /// no single-file import takes anywhere near this long, so the window can never race a
    /// live transcode.
    /// </summary>
    private static readonly TimeSpan MinOrphanImportAge = TimeSpan.FromHours(24);

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
    /// Returns true when the session's captured JPEGs are safe to delete: the MP4 must
    /// exist, be non-empty, and carry the marker proving it came from a completed run of
    /// the current encoder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker is not redundant with the file simply existing. Builds before the
    /// orientation fix wrote a vertically flipped MP4, so for those sessions the JPEGs
    /// are the only correctly oriented copy of the recording — deleting them would leave
    /// a permanently mirrored project with no way back.
    /// </para>
    /// <para>
    /// It also proves the encode ran to completion. Older builds wrote <c>video.mp4</c>
    /// in place, so a session interrupted mid-finalize can leave a non-empty but
    /// truncated file that a length check alone would happily accept.
    /// </para>
    /// </remarks>
    public static bool CanReleaseFrames(string sessionFolder)
    {
        if (!HasValidVideo(sessionFolder))
            return false;

        try
        {
            var videoPath = Path.Combine(sessionFolder, "video.mp4");
            return RecordingMarker.ReadEncoderVersion(videoPath) >= VideoWriter.EncoderVersion;
        }
        catch
        {
            return false;
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
    /// Only proceeds when <see cref="CanReleaseFrames"/> allows it, so an interrupted or
    /// legacy recording never loses its only usable copy.
    /// </summary>
    /// <returns>Bytes reclaimed, or 0 if nothing was cleaned.</returns>
    public static long CleanupSession(string sessionFolder)
    {
        if (!CanReleaseFrames(sessionFolder))
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

        // Backstop: reclaim abandoned import folders in the same root. Imports live under
        // SessionPaths.ImportsRoot (== SessionsRoot), which is the folder App passes here at
        // startup, so folding the sweep in means it runs without a second wiring point. When
        // called for any other folder (e.g. the user's save location) there simply are no
        // import_* folders and this is a no-op.
        totalReclaimed += CleanupOrphanedImports(outputFolder);

        if (totalReclaimed > 0)
            Debug.WriteLine($"[SessionCleanup] Startup cleanup reclaimed {FormatBytes(totalReclaimed)}");

        return totalReclaimed;
    }

    /// <summary>
    /// Reclaims disk from <c>import_*</c> folders left behind by external-video imports that
    /// never finished (a crash or process-kill between creating the folder and producing a
    /// usable <c>video.mp4</c>). Runs synchronously; call via Task.Run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is conservative by design.</b> An imported clip's media lives in its
    /// <c>import_*</c> folder and a project references it by that path
    /// (<c>ProjectService.ImportVideo</c> stores <c>result.VideoFilePath</c> directly). A user
    /// can import a clip and edit the resulting project — unsaved — for as long as they like,
    /// and <b>nothing on disk links that in-memory project back to the folder</b>. Age therefore
    /// cannot tell a live import apart from an abandoned one.
    /// </para>
    /// <para>
    /// So the gate deletes a folder only when it is BOTH old AND has no finished
    /// <c>video.mp4</c> — i.e. a provably-failed import (empty, or only a <c>.partial</c>). A
    /// folder that holds a real <c>video.mp4</c> is always spared, because it may be the only
    /// copy of a clip a project still points at. This mirrors the recorder's rule that frames
    /// are never released without a finalized MP4: when in doubt, keep the media.
    /// </para>
    /// </remarks>
    /// <returns>Total bytes reclaimed.</returns>
    public static long CleanupOrphanedImports(string importsRoot)
    {
        if (!Directory.Exists(importsRoot))
            return 0;

        long reclaimed = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(
                importsRoot, SessionPaths.ImportFolderPrefix + "*"))
            {
                if (!IsReclaimableOrphanImport(dir))
                    continue;

                long size = GetDirectorySize(dir);
                try
                {
                    Directory.Delete(dir, recursive: true);
                    reclaimed += size;
                    Debug.WriteLine(
                        $"[SessionCleanup] Deleted orphaned import '{dir}' ({FormatBytes(size)})");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionCleanup] Failed to delete orphaned import '{dir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionCleanup] Error during import cleanup: {ex.Message}");
        }

        return reclaimed;
    }

    /// <summary>
    /// True when <paramref name="importFolder"/> is safe to delete: it is one of ours (matches
    /// the <c>import_&lt;guid&gt;</c> naming), holds no finished <c>video.mp4</c>, and is older
    /// than <see cref="MinOrphanImportAge"/>. See <see cref="CleanupOrphanedImports"/> for why
    /// all three conditions are required.
    /// </summary>
    internal static bool IsReclaimableOrphanImport(string importFolder)
    {
        try
        {
            // Only ever touch folders this app created, never an unrelated folder that happens
            // to sit under the root and start with the prefix.
            if (!IsImportFolderName(Path.GetFileName(importFolder)))
                return false;

            // A finished import may be the live media of an unsaved project — never delete it.
            if (HasValidVideo(importFolder))
                return false;

            // Old enough that no import could still be writing into it.
            return DateTime.UtcNow - GetNewestWriteUtc(importFolder) > MinOrphanImportAge;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a folder name this app produced for an import:
    /// the <see cref="SessionPaths.ImportFolderPrefix"/> followed by a 32-char hex GUID.
    /// </summary>
    private static bool IsImportFolderName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith(SessionPaths.ImportFolderPrefix, StringComparison.Ordinal))
            return false;

        var suffix = name[SessionPaths.ImportFolderPrefix.Length..];
        return Guid.TryParseExact(suffix, "N", out _);
    }

    /// <summary>
    /// Most recent write time across the folder itself and every file it contains, so a folder
    /// still being written to is never seen as stale even if its own timestamp is old.
    /// </summary>
    private static DateTime GetNewestWriteUtc(string folder)
    {
        var newest = Directory.GetLastWriteTimeUtc(folder);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest)
                    newest = t;
            }
        }
        catch (Exception ex)
        {
            // If we cannot enumerate, fall back to the folder time — the caller's age gate
            // stays conservative because an unreadable folder is not deleted on a throw anyway.
            Debug.WriteLine($"[SessionCleanup] Could not scan '{folder}' for write times: {ex.Message}");
        }
        return newest;
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
                if (Directory.Exists(framesPath) && CanReleaseFrames(dir))
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
