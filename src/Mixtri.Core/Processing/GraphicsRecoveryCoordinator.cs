using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Processing;

/// <summary>Serializes device recovery and repaints only after resource rebuilding ends.</summary>
public sealed class GraphicsRecoveryCoordinator
{
    private readonly Func<Func<Task>, bool> _enqueue;
    private readonly Func<Task> _recover;
    private readonly Func<Task> _repaint;
    private readonly Func<bool> _isUnloaded;
    private readonly Action<Exception> _onFailure;
    private int _requested;
    private int _queued;
    private volatile bool _inProgress;

    public GraphicsRecoveryCoordinator(
        Func<Func<Task>, bool> enqueue, Func<Task> recover, Func<Task> repaint,
        Func<bool> isUnloaded, Action<Exception> onFailure)
    {
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _repaint = repaint ?? throw new ArgumentNullException(nameof(repaint));
        _isUnloaded = isUnloaded ?? throw new ArgumentNullException(nameof(isUnloaded));
        _onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
    }

    public bool IsRecoveryInProgress => _inProgress || Volatile.Read(ref _requested) != 0;

    public void Request()
    {
        if (_isUnloaded()) return;
        Interlocked.Exchange(ref _requested, 1);
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;

        if (!_enqueue(RunAsync))
        {
            Interlocked.Exchange(ref _queued, 0);
            Interlocked.Exchange(ref _requested, 0);
            _onFailure(new InvalidOperationException("Could not enqueue editor graphics recovery."));
        }
    }

    private async Task RunAsync()
    {
        int recoveryPasses = 0;
        try
        {
            do
            {
                Interlocked.Exchange(ref _requested, 0);
                if (_isUnloaded()) return;
                if (++recoveryPasses > 3)
                    throw new InvalidOperationException("Editor graphics did not stabilize after three recovery passes.");

                // A second device loss during initialization is transient, not a reason
                // to leave the editor permanently torn down. Keep retries bounded.
                for (int attempt = 0; ; attempt++)
                {
                    _inProgress = true;
                    try
                    {
                        try
                        {
                            await _recover();
                        }
                        finally
                        {
                            _inProgress = false;
                        }

                        if (!_isUnloaded())
                            await _repaint();
                        break;
                    }
                    catch (Exception ex) when (attempt < 2 && !_isUnloaded())
                    {
                        DiagLog.Write("Editor", $"graphics recovery retry {attempt + 1}: {ex}");
                        _inProgress = true;
                        await Task.Delay(100 * (attempt + 1));
                    }
                    finally
                    {
                        _inProgress = false;
                    }
                }
            }
            while (!_isUnloaded() && Volatile.Read(ref _requested) != 0);
        }
        catch (Exception ex)
        {
            // A failed rebuild must not pretend to have recovered, nor spin forever.
            Interlocked.Exchange(ref _requested, 0);
            _onFailure(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _queued, 0);
        }

        // A request between the last loop check and releasing queue ownership still
        // needs a pass. Requests made after that release enqueue themselves.
        if (!_isUnloaded() && Volatile.Read(ref _requested) != 0)
            Request();
    }
}
