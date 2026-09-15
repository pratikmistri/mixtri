using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Shell;

/// <summary>Keeps a completed take recoverable until an editor acknowledges ownership.</summary>
public sealed class RecordingHandoffStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _processLockName;

    public RecordingHandoffStore(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _processLockName = @"Local\Mixtri-Handoffs-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
    }

    /// <summary>Atomically publishes a completed take or its updated delivery destination.</summary>
    public Task SaveAsync(ShellProcessRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        string path = GetPath(request.Id);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        return WithStoreLockAsync(() =>
        {
            try
            {
                Directory.CreateDirectory(_root);
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(request));
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
            }
            return true;
        }, ct);
    }

    /// <summary>
    /// Reads a recovery snapshot and cleans stale files under the same locks as saves.
    /// The gate is released before callers enumerate the result or attempt delivery.
    /// </summary>
    public Task<IReadOnlyList<ShellProcessRequest>> ReadPendingAsync(CancellationToken ct = default)
        => WithStoreLockAsync<IReadOnlyList<ShellProcessRequest>>(() =>
        {
            if (!Directory.Exists(_root)) return [];
            var pending = new List<ShellProcessRequest>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(TombstonePath(path)))
                {
                    TryClearAcknowledged(path);
                    continue;
                }

                try
                {
                    var raw = File.ReadAllBytes(path);
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

            foreach (var marker in Directory.EnumerateFiles(_root, "*.done"))
            {
                ct.ThrowIfCancellationRequested();
                string handoff = marker[..^TombstoneSuffix.Length];
                if (!File.Exists(handoff))
                    TryClearAcknowledged(handoff);
            }

            return pending;
        }, ct);

    // Validation, quarantine, and cleanup hold both the local gate and the directory's named mutex.
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
    /// Runs under the same process-local and cross-process locks as <see cref="SaveAsync"/>.
    /// The delivered payload must also match the current file: locking acknowledgement alone
    /// cannot protect a snapshot obtained before a newer replacement was published.
    /// </remarks>
    public Task AcknowledgeAsync(ShellProcessRequest delivered, CancellationToken ct = default)
    {
        ValidateRequest(delivered);
        string path = GetPath(delivered.Id);
        string tombstone = TombstonePath(path);

        return WithStoreLockAsync(() =>
        {
            Directory.CreateDirectory(_root);
            ShellProcessRequest? current;
            try
            {
                current = JsonSerializer.Deserialize<ShellProcessRequest>(File.ReadAllBytes(path));
            }
            catch (FileNotFoundException) { return true; }

            // Compare the complete semantic payload, including legacy files' defaulted fields.
            // A newer destination must survive an acknowledgement of an older snapshot.
            if (current is null || !JsonSerializer.SerializeToUtf8Bytes(current).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(delivered)))
                throw new RecordingHandoffChangedException(delivered.Id);
            try
            {
                using var marker = new FileStream(tombstone, FileMode.Create, FileAccess.Write, FileShare.None);
                marker.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not record acknowledgement for recording {delivered.Id:N}; it remains pending.", ex);
            }

            TryClearAcknowledged(path);
            return true;
        }, ct);
    }

    private static void ValidateRequest(ShellProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Project is null || request.Id == Guid.Empty)
            throw new ArgumentException("A completed recording is required.", nameof(request));
    }

    private async Task<T> WithStoreLockAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A Mutex is thread-affine: acquisition, synchronous file work, and release
            // stay in this worker. No await runs while it is owned.
            return await Task.Run(() =>
            {
                using var mutex = new Mutex(false, _processLockName);
                bool held = false;
                try
                {
                    try
                    {
                        int result = WaitHandle.WaitAny([mutex, ct.WaitHandle], TimeSpan.FromSeconds(5));
                        if (result == WaitHandle.WaitTimeout)
                            throw new TimeoutException("Another Mixtri process is updating recording handoffs.");
                        if (result == 1) throw new OperationCanceledException(ct);
                        held = true;
                    }
                    catch (AbandonedMutexException)
                    {
                        held = true;
                        DiagLog.Write("Shell", "Recovered the recording-handoff lock after another process exited.");
                    }
                    ct.ThrowIfCancellationRequested();
                    return operation();
                }
                finally
                {
                    if (held) mutex.ReleaseMutex();
                }
            }, ct).ConfigureAwait(false);
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
    private string GetPath(Guid id) => Path.Combine(_root, $"{id:N}.json");
}

public sealed class RecordingHandoffChangedException(Guid id)
    : IOException($"Recording handoff {id:N} changed before acknowledgement; its newer contents were preserved.");
