namespace Mixtri.Core.Export;

/// <summary>Bounds encoder-held surfaces; a slot is returned only after sample consumption.</summary>
internal sealed class ExportFrameLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly object _sync = new();
    private readonly HashSet<Slot> _active = [];
    private bool _disposed;

    internal int Capacity { get; }
    internal int ActiveCount { get { lock (_sync) return _active.Count; } }

    public ExportFrameLimiter(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long frameBytes = checked((long)width * height * 4);
        if (frameBytes > 128L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "An encoder surface exceeds the export memory budget.");
        Capacity = (int)Math.Clamp(128L * 1024 * 1024 / frameBytes, 1, 3);
        _gate = new SemaphoreSlim(Capacity, Capacity);
    }

    public async Task<Slot> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed)
            {
                _gate.Release();
                throw new ObjectDisposedException(nameof(ExportFrameLimiter));
            }
            var slot = new Slot(this);
            _active.Add(slot);
            return slot;
        }
    }

    private void Release(Slot slot)
    {
        lock (_sync) _active.Remove(slot);
        _gate.Release();
    }

    public void Dispose()
    {
        Slot[] active;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active.ToArray();
        }
        foreach (var slot in active)
        {
            try { slot.Dispose(); }
            catch (Exception ex)
            {
                Mixtri.Core.Diagnostics.DiagLog.Write("Export", $"encoder surface release failed: {ex}");
            }
        }
        // Queued producers are cancelled and drained by the encoder; never dispose their gate.
    }

    internal sealed class Slot(ExportFrameLimiter owner) : IDisposable
    {
        private readonly object _sync = new();
        private IDisposable? _resource;
        private bool _disposed;

        public void Attach(IDisposable resource)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_resource is not null)
                    throw new InvalidOperationException("An export slot already owns a surface.");
                _resource = resource;
            }
        }

        public void Dispose()
        {
            IDisposable? resource;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                resource = _resource;
                _resource = null;
            }
            try { resource?.Dispose(); }
            finally { owner.Release(this); }
        }
    }
}
