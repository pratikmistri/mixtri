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
            ShellProcessRequest? request = null;
            string? invalid = null;
            try
            {
                request = JsonSerializer.Deserialize<ShellProcessRequest>(File.ReadAllBytes(path));
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
                Quarantine(path, invalid);
                continue;
            }

            yield return request!;
        }
    }

    private static void Quarantine(string path, string reason)
    {
        DiagLog.Write("Shell", $"Discarding unreadable recording handoff '{path}': {reason}");
        try { File.Move(path, $"{path}.bad", overwrite: true); }
        catch (Exception ex)
        {
            DiagLog.Write("Shell", $"Could not quarantine '{path}': {ex.Message}");
            try { File.Delete(path); } catch { /* it will be retried on the next open */ }
        }
    }

    public void Acknowledge(Guid id)
    {
        try { File.Delete(GetPath(id)); }
        catch (Exception ex) { DiagLog.Write("Shell", $"Could not clear handoff {id:N}: {ex.Message}"); }
    }

    private string GetPath(Guid id) => Path.Combine(root, $"{id:N}.json");
}
