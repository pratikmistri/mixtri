namespace Mixtri.Core.Audio;

/// <summary>One trailing-edge scrub timer, with expiry committed under the transport lock.</summary>
internal sealed class ScrubStopTimer(object transportLock, Action stop, TimeProvider timeProvider) : IDisposable
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(80);
    private readonly object _gate = new();
    private ITimer? _timer;
    private long? _startedAt;
    private bool _disposed;

    internal void Restart()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _startedAt = timeProvider.GetTimestamp();
            _timer ??= timeProvider.CreateTimer(static state => ((ScrubStopTimer)state!).OnTimer(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            _startedAt = null;
            if (!_disposed) _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        lock (transportLock)
        {
            lock (_gate)
            {
                if (_disposed || _startedAt is not { } started) return;
                var remaining = QuietPeriod - timeProvider.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    _timer!.Change(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))),
                        Timeout.InfiniteTimeSpan);
                    return;
                }
                _startedAt = null;
            }
            stop();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _startedAt = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
