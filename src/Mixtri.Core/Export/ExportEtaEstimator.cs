namespace Mixtri.Core.Export;

/// <summary>
/// Turns a stream of export progress reports into a time-remaining estimate based on the
/// RECENT rate of progress rather than the whole-export average.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only reports inside a sliding window (default 10 s) contribute, so slow setup
/// (AI upscaler, audio pre-pass) and early decoder warm-up stop skewing the estimate once
/// the export settles.</item>
/// <item>A change of phase (<see cref="ExportProgress.Status"/>) restarts the window: each
/// phase runs at its own rate.</item>
/// <item>Reports whose percentage goes backwards are out of order (progress is reported
/// from several encoder threads and marshalled asynchronously) and are rejected so the bar
/// and the estimate only move forward.</item>
/// </list>
/// Not thread-safe; call from one thread (the UI thread in the app).
/// </remarks>
public sealed class ExportEtaEstimator
{
    private readonly TimeSpan _window;
    private readonly Queue<(TimeSpan At, double Percent)> _samples = new();
    private double _lastPercent = double.NegativeInfinity;
    private string? _phase;
    private (TimeSpan At, double Percent) _last;

    public ExportEtaEstimator(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(10);
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
    }

    /// <summary>Highest percentage accepted so far, or 0 before the first report.</summary>
    public double LastPercent => double.IsNegativeInfinity(_lastPercent) ? 0 : _lastPercent;

    public void Reset()
    {
        _samples.Clear();
        _lastPercent = double.NegativeInfinity;
        _phase = null;
    }

    /// <summary>
    /// Records a report observed at <paramref name="at"/> (any monotonic clock).
    /// Returns <c>false</c> — and ignores the report — when it is out of order.
    /// </summary>
    public bool TryAdd(TimeSpan at, double percent, string? phase = null)
    {
        if (double.IsNaN(percent) || percent < _lastPercent) return false;
        _lastPercent = percent;

        if (!string.Equals(phase, _phase, StringComparison.Ordinal))
        {
            _phase = phase;
            _samples.Clear();
        }

        _samples.Enqueue((at, percent));
        _last = (at, percent);
        while (_samples.Count > 2 && at - _samples.Peek().At > _window)
            _samples.Dequeue();
        return true;
    }

    /// <summary>
    /// Estimated time until 100%, from the rate across the current window, or <c>null</c>
    /// while there is not yet enough recent progress to measure a rate.
    /// </summary>
    public TimeSpan? EstimateRemaining()
    {
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var last = _last;
        double percentDelta = last.Percent - first.Percent;
        double seconds = (last.At - first.At).TotalSeconds;
        if (percentDelta <= 0 || seconds <= 0) return null;

        double remainingSeconds = (100.0 - last.Percent) / (percentDelta / seconds);
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds)) return null;
        return TimeSpan.FromSeconds(Math.Clamp(remainingSeconds, 0, TimeSpan.FromDays(1).TotalSeconds));
    }
}
