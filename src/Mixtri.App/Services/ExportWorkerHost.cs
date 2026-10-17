using System.Diagnostics;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Export;

namespace Mixtri_App.Services;

internal static class ExportWorkerHost
{
    public static async Task<int> RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        using var cancellation = new CancellationTokenSource();
        using var stopControls = new CancellationTokenSource();
        var writeGate = new object();
        void Send(ExportWorkerMessage message)
        {
            lock (writeGate) output.WriteLine(ExportWorkerProtocol.Serialize(message));
        }
        Task controls = Task.CompletedTask;
        try
        {
            var line = await input.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var request = ExportWorkerProtocol.Deserialize(
                line ?? throw new InvalidDataException("The export worker received no job."));
            if (request.Kind != ExportWorkerMessageKind.Start || request.Snapshot is null
                || request.Settings is null || string.IsNullOrWhiteSpace(request.OutputFolder))
                throw new InvalidDataException("The export worker received an incomplete job.");
            DiagLog.Write("Export", $"worker {Environment.ProcessId} starting {request.Settings.Format} export");

            var scheduler = new ExportWorkScheduler();
            controls = ReadControlsAsync(input, scheduler, cancellation, stopControls.Token);
            var lastProgress = Stopwatch.StartNew();
            var progress = new WorkerProgress(value =>
            {
                lock (writeGate)
                {
                    if (lastProgress.ElapsedMilliseconds < 100 && value.PercentComplete < 100) return;
                    lastProgress.Restart();
                    Send(new ExportWorkerMessage(ExportWorkerMessageKind.Progress) { Progress = value });
                }
            });
            var snapshot = request.Snapshot;
            var export = new ExportEngine().ExportProjectAsync(
                snapshot.Project, request.Settings, snapshot.Composition, request.OutputFolder,
                snapshot.Timeline, progress, cancellation.Token,
                new WindowsVideoSuperResolutionProvider(), scheduler);

            if (await Task.WhenAny(export, controls) == controls)
            {
                cancellation.Cancel();
                await controls;
            }
            var path = await export;
            DiagLog.Write("Export", $"worker {Environment.ProcessId} completed export");
            Send(new ExportWorkerMessage(ExportWorkerMessageKind.Completed) { OutputPath = path });
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DiagLog.Write("Export", $"worker {Environment.ProcessId} cancelled export");
            Send(new ExportWorkerMessage(ExportWorkerMessageKind.Cancelled));
            return 0;
        }
        catch (Exception ex)
        {
            cancellation.Cancel();
            DiagLog.Write("Export", $"worker failed: {ex}");
            Send(new ExportWorkerMessage(ExportWorkerMessageKind.Failed) { Error = ex.Message });
            return 1;
        }
        finally
        {
            stopControls.Cancel();
            try { await controls; }
            catch (OperationCanceledException) when (stopControls.IsCancellationRequested) { }
            catch (Exception ex) when (controls.IsFaulted)
            {
                DiagLog.Write("Export", $"worker control failed: {ex}");
            }
        }
    }

    private static async Task ReadControlsAsync(
        StreamReader input, ExportWorkScheduler scheduler,
        CancellationTokenSource cancellation, CancellationToken stop)
    {
        while (await input.ReadLineAsync(stop) is { } line)
        {
            var message = ExportWorkerProtocol.Deserialize(line);
            switch (message.Kind)
            {
                case ExportWorkerMessageKind.Cancel:
                    cancellation.Cancel();
                    return;
                case ExportWorkerMessageKind.Preview:
                    scheduler.UpdatePreviewActivity(message.Interactive, message.PreviewMilliseconds);
                    break;
                default:
                    throw new InvalidDataException($"Unexpected export control: {message.Kind}.");
            }
        }
        cancellation.Cancel();
    }

    private sealed class WorkerProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
