using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace Mixtri.Core.Export;

/// <summary>One export's borrowed pixel array; commit the current GIF frame before refilling it.</summary>
internal sealed class GifFramePixels : IDisposable
{
    private byte[]? _pixels;
    private IBuffer? _buffer;
    private (double Width, double Height, DirectXPixelFormat Format) _key;
    private bool _disposed;

    internal int AllocationCount { get; private set; }
    internal long AllocatedPixelBytes { get; private set; }
    internal int CachedPixelBytes => _pixels?.Length ?? 0;

    internal byte[] Read(CanvasBitmap frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        var size = frame.SizeInPixels;
        var key = (size.Width, size.Height, frame.Format);
        if (_pixels is null || _key != key)
        {
            // Let Win2D size the first array so non-default pixel formats retain their existing behavior.
            var pixels = frame.GetPixelBytes();
            var buffer = pixels.AsBuffer();
            _pixels = pixels;
            _buffer = buffer;
            _key = key;
            AllocationCount++;
            AllocatedPixelBytes += pixels.LongLength;
        }
        else
        {
            frame.GetPixelBytes(_buffer!);
        }
        return _pixels;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer = null;
        _pixels = null;
        _key = default;
    }
}
