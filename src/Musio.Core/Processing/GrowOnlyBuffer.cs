using Microsoft.Graphics.Canvas;

namespace Musio.Core.Processing;

/// <summary>
/// Owns a lazily-(re)allocated <see cref="CanvasRenderTarget"/> scratch buffer, unifying the
/// "if null or wrong size → allocate new → swap → dispose old" idiom that used to be
/// hand-rolled per class (<c>FrameCompositor.EnsureCompositeBuffer</c>/<c>EnsureCroppedBuffer</c>,
/// <c>TextOverlayRenderer</c>/<c>AnimatedTextEngine</c>'s <c>EnsureBlurScratch</c>,
/// <c>CursorRenderer</c>'s shutter-blur scratch).
/// </summary>
/// <remarks>
/// Two growth policies are exposed because the original call sites used two different ones
/// and neither is being unified (that would be an unmarked behavior change):
/// <list type="bullet">
/// <item><see cref="Ensure"/> — exact-size match: reallocates whenever the requested size (or
/// device) differs at all from the current buffer, including on shrink. This is what
/// <c>FrameCompositor</c> and <c>TextOverlayRenderer</c>/<c>AnimatedTextEngine</c> did.</item>
/// <item><see cref="TryEnsureAtLeast"/> — grow-only: reallocates only when the current buffer
/// is smaller than requested in either dimension; a request for a smaller size than what is
/// already allocated reuses the existing (larger) buffer as-is. This is what
/// <c>CursorRenderer</c>'s shutter-blur scratch did, and only makes sense for a caller (like
/// that one) that always clears the buffer before drawing into it and never relies on its
/// exact reported size. Failure is reported via a <c>bool</c> return instead of throwing, to
/// match the caller's existing graceful-degradation behavior.</item>
/// </list>
/// Both methods allocate the replacement target BEFORE disposing the old one, so a failed
/// allocation (which throws) never leaves this instance pointing at an already-disposed
/// resource — the old, still-valid target (if any) is left untouched.
/// This type is used on the GPU hot path: the steady-state case (unchanged device and size)
/// is a handful of field reads/compares and returns the cached target — no allocation, no
/// boxing, no LINQ, no tuples.
/// </remarks>
public sealed class GrowOnlyBuffer : IDisposable
{
    private CanvasRenderTarget? _target;
    private CanvasDevice? _device;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>The currently-allocated target, or null if <see cref="Ensure"/>/
    /// <see cref="TryEnsureAtLeast"/> has never been called (or allocation always failed).</summary>
    public CanvasRenderTarget? Current => _target;

    /// <summary>
    /// Returns a target sized exactly <paramref name="width"/> x <paramref name="height"/> on
    /// <paramref name="device"/>, reallocating only when the device or either dimension has
    /// changed since the last call.
    /// </summary>
    public CanvasRenderTarget Ensure(CanvasDevice device, int width, int height, string purpose, float dpi = 96f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_target is null || !ReferenceEquals(_device, device) || _width != width || _height != height)
        {
            var next = Win2DUtils.CreateRenderTarget(device, width, height, dpi, purpose);
            _target?.Dispose();
            _target = next;
            _device = device;
            _width = width;
            _height = height;
        }

        return _target;
    }

    /// <summary>
    /// Returns a target at least <paramref name="width"/> x <paramref name="height"/> on
    /// <paramref name="device"/>, reallocating only when the current buffer is smaller (in
    /// either dimension) than requested, or does not exist yet. Returns <see langword="false"/>
    /// (leaving <paramref name="target"/> null and this instance's cached target cleared) if
    /// allocation fails with an out-of-memory or COM error, instead of throwing — matching
    /// <c>CursorRenderer</c>'s existing fall-back-to-direct-compositing behavior.
    /// </summary>
    public bool TryEnsureAtLeast(
        CanvasDevice device, int width, int height, string purpose,
        out CanvasRenderTarget? target, float dpi = 96f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_target is null || !ReferenceEquals(_device, device) || _width < width || _height < height)
        {
            CanvasRenderTarget next;
            try
            {
                next = Win2DUtils.CreateRenderTarget(device, width, height, dpi, purpose);
            }
            catch (InvalidOperationException)
            {
                // Win2DUtils.CreateRenderTarget already wraps OutOfMemoryException/COMException
                // into this type; treat it the same way the original inline
                // `catch (Exception ex) when (ex is OutOfMemoryException or COMException)` did:
                // drop the (now-stale) cached target and report failure without throwing.
                _target?.Dispose();
                _target = null;
                _device = null;
                _width = 0;
                _height = 0;
                target = null;
                return false;
            }

            _target?.Dispose();
            _target = next;
            _device = device;
            _width = width;
            _height = height;
        }

        target = _target;
        return true;
    }

    /// <summary>
    /// Disposes the current target (if any) and resets to the empty state, but leaves this
    /// instance usable for further <see cref="Ensure"/>/<see cref="TryEnsureAtLeast"/> calls
    /// — unlike <see cref="Dispose"/>, which permanently retires the instance. Mirrors the
    /// original call sites' behavior of unconditionally dropping cached render targets (e.g.
    /// on an allocation failure elsewhere, or on device loss) while remaining able to
    /// reallocate on the next frame.
    /// </summary>
    public void Clear()
    {
        _target?.Dispose();
        _target = null;
        _device = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Clear();
            _disposed = true;
        }
    }
}
