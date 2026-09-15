using System.Text.Json;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Shell;

/// <summary>Keeps a completed take recoverable until an editor acknowledges ownership.</summary>
public sealed class RecordingHandoffStore(string root)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Atomically publishes a completed take or its updated delivery destination.</summary>
    public async Task SaveAsync(ShellProcessRequest request, CancellationToken ct = default)
    {
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Project is null || request.Id == Guid.Empty)
            throw new ArgumentException("A completed recording is required.", nameof(request));
        string path = GetPath(request.Id);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(request), ct).ConfigureAwait(false);
            // Remove the old acknowledged pair before publishing, never leave its marker
            // attached to a new pending request. A failed cleanup must leave the new save unpublished.
            if (File.Exists(TombstonePath(path)))
                ClearAcknowledged(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagLog.Write("Shell", $"Could not remove handoff temporary '{temporary}': {ex}");
            }
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads a recovery snapshot and cleans stale files under the same gate as saves.
    /// The gate is released before callers enumerate the result or attempt delivery.
    /// </summary>
    public async Task<IReadOnlyList<ShellProcessRequest>> ReadPendingAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(root)) return [];
            var pending = new List<ShellProcessRequest>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(TombstonePath(path)))
                {
                    TryClearAcknowledged(path);
                    continue;
                }

                try
                {
                    var raw = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize<ShellProcessRequest>(raw);
                    if (request?.Command != ShellProcessCommand.RecordingCompleted || request.Project is null
                        || request.Id == Guid.Empty || !string.Equals(path, GetPath(request.Id), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The file does not describe a completed recording.");
                    pending.Add(request);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    Quarantine(path, ex);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagLog.Write("Shell", $"Could not read recording handoff '{path}'; kept for retry: {ex}");
                }
            }

            foreach (var marker in Directory.EnumerateFiles(root, "*.done"))
            {
                ct.ThrowIfCancellationRequested();
                string handoff = marker[..^TombstoneSuffix.Length];
                if (!File.Exists(handoff))
                    TryClearAcknowledged(handoff);
            }

            return pending;
        }
        finally { _writeGate.Release(); }
    }

    // Validation, quarantine, and marker cleanup all run while _writeGate is held.
    private static void Quarantine(string path, Exception reason)
    {
        DiagLog.Write("Shell", $"Quarantining invalid recording handoff '{path}': {reason}");
        try { File.Move(path, $"{path}.bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagLog.Write("Shell", $"Could not quarantine '{path}'; kept for retry: {ex}");
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
    public async Task AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("A recording request ID is required.", nameof(id));
        string path = GetPath(id);
        string tombstone = TombstonePath(path);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(root);
                using var marker = new FileStream(tombstone, FileMode.Create, FileAccess.Write, FileShare.None);
                marker.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not record acknowledgement for recording {id:N}; it remains pending.", ex);
            }

            TryClearAcknowledged(path);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Removes an acknowledged handoff and its tombstone, in that order.</summary>
    private static void ClearAcknowledged(string path)
    {
        File.Delete(path);
        File.Delete(TombstonePath(path));
    }

    private static void TryClearAcknowledged(string path)
    {
        try { ClearAcknowledged(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagLog.Write("Shell", $"Could not clear acknowledged handoff '{path}'; marker retained: {ex}");
        }
    }

    private const string TombstoneSuffix = ".done";
    private static string TombstonePath(string handoffPath) => handoffPath + TombstoneSuffix;
    private string GetPath(Guid id) => Path.Combine(root, $"{id:N}.json");
}
