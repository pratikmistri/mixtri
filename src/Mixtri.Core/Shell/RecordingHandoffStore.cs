using System.Text.Json;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Shell;

/// <summary>Keeps a completed take recoverable until an editor acknowledges ownership.</summary>
public sealed class RecordingHandoffStore(string root)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Persists a completed take. Re-entrant saves of the same id are serialized and each uses
    /// its own scratch file: CompleteRecordingAsync is not covered by the coordinator's open
    /// gate, so a recovery save can overlap an in-flight one. Without both, the two writes
    /// collide on the scratch name and their concurrent replaces of the same destination fail
    /// with a sharing violation — reporting an error for a recording that is perfectly valid.
    /// </summary>
    public async Task SaveAsync(ShellProcessRequest request)
    {
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Project is null || request.Id == Guid.Empty)
            throw new ArgumentException("A completed recording is required.", nameof(request));
        Directory.CreateDirectory(root);
        string path = GetPath(request.Id);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(request)).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* the move already consumed it */ }
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Yields every recoverable handoff. A malformed file is quarantined rather than thrown
    /// from, because this is a lazy iterator: throwing would abandon every later handoff and
    /// leave the bad file in place to fail the same way on every subsequent open.
    /// </summary>
    public IEnumerable<ShellProcessRequest> ReadPending()
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal))
        {
            // An acknowledged handoff whose delete failed still carries its tombstone.
            // Replaying it would re-apply the take — for Record More, appending it twice.
            if (File.Exists(TombstonePath(path)))
            {
                ClearAcknowledged(path);
                continue;
            }

            ShellProcessRequest? request = null;
            string? invalid = null;
            byte[]? raw = null;
            try
            {
                raw = File.ReadAllBytes(path);
                request = JsonSerializer.Deserialize<ShellProcessRequest>(raw);
                if (request?.Command != ShellProcessCommand.RecordingCompleted || request.Project is null
                    || request.Id == Guid.Empty || !string.Equals(path, GetPath(request.Id), StringComparison.OrdinalIgnoreCase))
                {
                    request = null;
                    invalid = "the file does not describe a completed recording";
                }
            }
            catch (Exception ex)
            {
                request = null;
                invalid = ex.Message;
            }

            if (invalid is not null)
            {
                Quarantine(path, invalid, raw);
                continue;
            }

            yield return request!;
        }

        // Drop tombstones whose handoff is already gone, so the folder cannot grow forever.
        foreach (var marker in Directory.EnumerateFiles(root, "*.done"))
        {
            string handoff = marker[..^TombstoneSuffix.Length];
            if (!File.Exists(handoff))
                try { File.Delete(marker); } catch { /* retried on the next open */ }
        }
    }

    /// <summary>
    /// Moves an unreadable handoff aside.
    /// </summary>
    /// <remarks>
    /// Reads are not serialized with <see cref="SaveAsync"/>, so between validation failing
    /// and this call a save can legitimately replace the file with a valid handoff. Moving it
    /// then would discard the only durable copy of a good recording. The bytes that failed
    /// validation are therefore re-checked first, and anything that changed is left alone for
    /// the next read to pick up.
    /// </remarks>
    private static void Quarantine(string path, string reason, byte[]? failedContent)
    {
        try
        {
            if (failedContent is null || !File.ReadAllBytes(path).AsSpan().SequenceEqual(failedContent))
            {
                DiagLog.Write("Shell", $"Skipping quarantine of '{path}': it changed since it failed to parse.");
                return;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("Shell", $"Skipping quarantine of '{path}': {ex.Message}");
            return;
        }

        DiagLog.Write("Shell", $"Discarding unreadable recording handoff '{path}': {reason}");
        try { File.Move(path, $"{path}.bad", overwrite: true); }
        catch (Exception ex)
        {
            DiagLog.Write("Shell", $"Could not quarantine '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Marks a delivered recording as consumed.
    /// </summary>
    /// <remarks>
    /// Writes a tombstone BEFORE removing the handoff. Deleting alone is not enough: the
    /// in-memory request dedup does not survive a process restart, so if the delete failed
    /// and the handoff stayed on disk, the next start would replay it and
    /// <c>AppendRecording</c> would add the same take a second time. The tombstone is what
    /// makes acknowledgement durable; failing to write one is reported rather than swallowed,
    /// because the caller must not treat the handoff as consumed.
    ///
    /// Runs under the same gate as <see cref="SaveAsync"/>: otherwise a save could move a new
    /// valid handoff into this path between the tombstone write and the delete, and the
    /// delete would destroy the only durable copy of that recording.
    /// </remarks>
    public async Task AcknowledgeAsync(Guid id)
    {
        string path = GetPath(id);
        string tombstone = TombstonePath(path);

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllBytes(tombstone, []);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Could not record acknowledgement for recording {id:N}; it was left pending to avoid replaying it.", ex);
            }

            ClearAcknowledged(path);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Removes an acknowledged handoff and its tombstone, in that order.</summary>
    private static void ClearAcknowledged(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex)
        {
            // The tombstone stays behind and suppresses the replay until this succeeds.
            DiagLog.Write("Shell", $"Could not clear acknowledged handoff '{path}': {ex.Message}");
            return;
        }
        try { File.Delete(TombstonePath(path)); } catch { /* swept on the next read */ }
    }

    private const string TombstoneSuffix = ".done";
    private static string TombstonePath(string handoffPath) => handoffPath + TombstoneSuffix;
    private string GetPath(Guid id) => Path.Combine(root, $"{id:N}.json");
}
