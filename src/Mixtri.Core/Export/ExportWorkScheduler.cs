namespace Mixtri.Core.Export;

/// <summary>Yields export work to interactive preview without changing output quality.</summary>
public sealed class ExportWorkScheduler
{
    private int _interactive;
    private int _previewMilliseconds;

    public void UpdatePreviewActivity(bool interactive, double frameMilliseconds)
    {
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        Volatile.Write(ref _previewMilliseconds, (int)Math.Min(frameMilliseconds, 1000));
        Volatile.Write(ref _interactive, interactive ? 1 : 0);
    }

    internal int YieldMilliseconds => Volatile.Read(ref _interactive) == 0
        ? 0
        : Math.Clamp(Volatile.Read(ref _previewMilliseconds), 16, 100);

    public Task WaitForTurnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int delay = YieldMilliseconds;
        return delay == 0 ? Task.CompletedTask : Task.Delay(delay, ct);
    }
}
