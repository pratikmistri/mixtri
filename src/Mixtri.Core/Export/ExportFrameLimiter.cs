using Windows.Media.Core;

namespace Mixtri.Core.Export;

/// <summary>Tracks encoder surface memory without blocking the native pipeline's input requests.</summary>
internal sealed class ExportFrameLimiter : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<Slot> _active = [];
    private bool _disposed;

    internal int Capacity { get; }
    internal int ActiveCount { get { lock (_sync) return _active.Count; } }
    internal int PeakCount { get; private set; }

    public ExportFrameLimiter(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        long frameBytes = checked((long)width * height * 4);
        if (frameBytes > 128L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "An encoder surface exceeds the export memory budget.");
        Capacity = (int)(128L * 1024 * 1024 / frameBytes);
    }

    public Slot Reserve(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A codec may retain old samples until it receives additional input.
            // Waiting here for Processed would deadlock that request/consume cycle.
            if (_active.Count >= Capacity)
                throw new InvalidOperationException(
                    "The video encoder exceeded the 128 MiB buffered-frame budget. " +
                    "Reduce the export resolution and retry.");
            var slot = new Slot(this);
            _active.Add(slot);
            PeakCount = Math.Max(PeakCount, _active.Count);
            return slot;
        }
    }

    private void Release(Slot slot)
    {
        lock (_sync) _active.Remove(slot);
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
    }

    internal sealed class Slot(ExportFrameLimiter owner) : IDisposable
    {
        private readonly object _sync = new();
        private IDisposable? _resource;
        private MediaStreamSample? _sample;
        private byte[]? _pixels;
        private bool _disposed;

        public void Attach(IDisposable? resource, MediaStreamSample? sample = null, byte[]? pixels = null)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_resource is not null || _sample is not null || _pixels is not null)
                    throw new InvalidOperationException("An export slot already owns a surface.");
                // CsWinRT event state is tied to the managed sample wrapper. Retain it
                // with the surface so GC cannot revoke Processed before consumption.
                _sample = sample;
                if (sample is not null) sample.Processed += OnProcessed;
                _resource = resource;
                _pixels = pixels;
            }
        }

        public void Dispose()
        {
            IDisposable? resource;
            MediaStreamSample? sample;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                resource = _resource;
                _resource = null;
                sample = _sample;
                _sample = null;
                _pixels = null;
            }
            try
            {
                if (sample is not null) sample.Processed -= OnProcessed;
            }
            finally
            {
                try { resource?.Dispose(); }
                finally { owner.Release(this); }
            }
        }

        private void OnProcessed(MediaStreamSample sender, object args) => Dispose();
    }
}
