using Microsoft.Graphics.Canvas;

namespace Mixtri.Core.Processing;

/// <summary>A read-only frame whose lifetime extends through cache eviction or reader disposal.</summary>
public sealed class FrameLease : IDisposable
{
    private Action? _release;
    public CanvasBitmap Bitmap { get; }

    internal FrameLease(CanvasBitmap bitmap, Action release)
    {
        Bitmap = bitmap;
        _release = release;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
