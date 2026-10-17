using System.Diagnostics;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Projects;

namespace Mixtri.Core.Export;

/// <summary>Owns one headless export process and its private, redirected control channel.</summary>
public sealed class ExportWorkerClient
{
    private static readonly TimeSpan ControlWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);
    private readonly string _executablePath;
    private readonly string[] _arguments;
    private volatile bool _playing;
    private long _lastPreviewTimestamp;
    private int _previewMilliseconds;
    private int _running;
    private int _processId;
    private Task? _inputWrite;
    private Task? _previewWrite;
    internal Task? PendingPreviewWrite => Volatile.Read(ref _previewWrite);

    public ExportWorkerClient(string executablePath)
        : this(executablePath, [ExportWorkerProtocol.Argument])
    {
    }

    internal ExportWorkerClient(string executablePath, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _arguments = arguments.ToArray();
    }

    internal int? ActiveProcessId
    {
        get
        {
            int id = Volatile.Read(ref _processId);
            return id == 0 ? null : id;
        }
    }

    public void SetPlaybackActive(bool playing) => _playing = playing;

    public void ReportPreviewFrame(TimeSpan elapsed)
    {
        Volatile.Write(ref _previewMilliseconds, (int)Math.Clamp(elapsed.TotalMilliseconds, 0, 1000));
        Volatile.Write(ref _lastPreviewTimestamp, Stopwatch.GetTimestamp());
    }

    public async Task<string> ExportAsync(
        ProjectStateSnapshot snapshot, ExportSettings settings, string outputFolder,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("An export is already running.");
        try
        {
            return await ExportCoreAsync(snapshot, settings, outputFolder, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _processId, 0);
            Volatile.Write(ref _inputWrite, null);
            Volatile.Write(ref _previewWrite, null);
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<string> ExportCoreAsync(
        ProjectStateSnapshot snapshot, ExportSettings settings, string outputFolder,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in _arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the export worker.");
        Volatile.Write(ref _processId, process.Id);
        using var stopControls = new CancellationTokenSource();
        Task controls = Task.CompletedTask;
        Task cancellationWatch = Task.CompletedTask;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            cancellationWatch = WatchCancellationAsync(process, ct, stopControls.Token);
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            process.StandardInput.AutoFlush = true;
            await WriteInput(process,
                new ExportWorkerMessage(ExportWorkerMessageKind.Start)
                {
                    Snapshot = snapshot, Settings = settings, OutputFolder = outputFolder,
                }, ct).WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            controls = SendControlsAsync(process, ct, stopControls.Token);
            string? outputPath = null;
            string? failure = null;
            while (true)
            {
                var nextLine = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(nextLine, controls).ConfigureAwait(false) == controls)
                    await controls.ConfigureAwait(false);
                if (await Task.WhenAny(nextLine, cancellationWatch).ConfigureAwait(false) == cancellationWatch)
                    await cancellationWatch.ConfigureAwait(false);
                var line = await nextLine.ConfigureAwait(false);
                if (line is null) break;
                var message = ExportWorkerProtocol.Deserialize(line);
                switch (message.Kind)
                {
                    case ExportWorkerMessageKind.Progress when message.Progress is not null:
                        progress?.Report(message.Progress);
                        break;
                    case ExportWorkerMessageKind.Completed:
                        outputPath = message.OutputPath;
                        break;
                    case ExportWorkerMessageKind.Failed:
                        failure = message.Error ?? "The export worker failed.";
                        break;
                    case ExportWorkerMessageKind.Cancelled:
                        failure = "The export worker cancelled unexpectedly.";
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected export worker message: {message.Kind}.");
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var errorText = await errors.ConfigureAwait(false);
            if (failure is not null)
                throw new InvalidOperationException(failure);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                throw new InvalidOperationException(
                    $"The export worker exited without completing the export (exit {process.ExitCode}). {errorText}");
            return outputPath;
        }
        finally
        {
            stopControls.Cancel();
            Terminate(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await controls.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopControls.IsCancellationRequested) { }
            catch (IOException ex)
            {
                DiagLog.Write("Export", $"worker control channel closed during shutdown: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                DiagLog.Write("Export", $"worker control timed out before shutdown: {ex.Message}");
            }
            try { await cancellationWatch.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopControls.IsCancellationRequested) { }
            var inputWrite = Volatile.Read(ref _inputWrite);
            if (inputWrite is not null)
            {
                try { await inputWrite.ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || stopControls.IsCancellationRequested) { }
                catch (IOException ex)
                {
                    DiagLog.Write("Export", $"worker input closed during shutdown: {ex.Message}");
                }
            }
            var errorText = await errors.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(errorText))
                DiagLog.Write("Export", $"worker stderr: {errorText}");
        }
    }

    private async Task SendControlsAsync(Process process, CancellationToken exportCancellation, CancellationToken stop)
    {
        using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(exportCancellation, stop);
        while (!process.HasExited)
        {
            stop.ThrowIfCancellationRequested();
            if (exportCancellation.IsCancellationRequested)
            {
                try
                {
                    await WriteInput(process, new ExportWorkerMessage(ExportWorkerMessageKind.Cancel), stop)
                        .WaitAsync(ControlWriteTimeout, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    DiagLog.Write("Export", $"worker cancellation delivery failed: {ex.Message}");
                }
                return;
            }

            long last = Volatile.Read(ref _lastPreviewTimestamp);
            bool interactive = _playing || (last != 0
                && Stopwatch.GetElapsedTime(last) < TimeSpan.FromMilliseconds(500));
            Task? write = null;
            try
            {
                write = WriteInput(process,
                    new ExportWorkerMessage(ExportWorkerMessageKind.Preview)
                    {
                        Interactive = interactive,
                        PreviewMilliseconds = Volatile.Read(ref _previewMilliseconds),
                    }, previewCancellation.Token);
                Volatile.Write(ref _previewWrite, write);
                await write.WaitAsync(ControlWriteTimeout, previewCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested && !stop.IsCancellationRequested)
            {
                // Do not start a second StreamWriter operation over an uncancellable
                // pipe write. The independent watchdog still owns the kill deadline.
                if (write?.IsCompleted == true) continue;
                return;
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException("The export worker stopped accepting preview controls.", ex);
            }
            catch (IOException)
            {
                // The worker can close stdin just before the OS publishes its exit.
                var exited = process.WaitForExitAsync(stop);
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(1), stop)).ConfigureAwait(false) == exited)
                    return;
                throw;
            }
            await Task.Delay(100, stop).ConfigureAwait(false);
        }
    }

    private Task WriteInput(Process process, ExportWorkerMessage message, CancellationToken ct)
    {
        var write = process.StandardInput.WriteLineAsync(ExportWorkerProtocol.Serialize(message).AsMemory(), ct);
        Volatile.Write(ref _inputWrite, write);
        return write;
    }

    private static async Task WatchCancellationAsync(Process process, CancellationToken cancellation, CancellationToken stop)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, wake.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
        if (stop.IsCancellationRequested || !cancellation.IsCancellationRequested || process.HasExited) return;
        var exit = process.WaitForExitAsync(stop);
        if (await Task.WhenAny(exit, Task.Delay(CancellationGrace, stop)).ConfigureAwait(false) != exit
            && !stop.IsCancellationRequested && !process.HasExited)
        {
            DiagLog.Write("Export", "worker did not drain cancellation; terminating its process");
            Terminate(process);
        }
    }

    private static void Terminate(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}
