namespace Mixtri.Core.Capture;

/// <summary>Defers completion until all admitted frame work exits, without waiting on that work.</summary>
internal sealed class FrameSubmissionGate(Action complete)
{
    private readonly object _gate = new();
    private int _active;
    private bool _closed;

    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_closed) return false;
            _active++;
            return true;
        }
    }

    public void Exit()
    {
        bool finished;
        lock (_gate)
        {
            if (_active == 0) throw new InvalidOperationException("No frame submission is active.");
            _active--;
            finished = _closed && _active == 0;
        }
        if (finished) complete();
    }

    public void Close()
    {
        bool finished;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            finished = _active == 0;
        }
        if (finished) complete();
    }
}
