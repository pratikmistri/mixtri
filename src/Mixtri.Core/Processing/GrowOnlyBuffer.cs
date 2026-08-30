using Microsoft.Graphics.Canvas;

namespace Mixtri.Core.Processing;

/// <summary>
/// Order in which a <see cref="GrowOnlyBuffer"/> swaps in a replacement target.
/// </summary>
/// <remarks>
/// The call sites this type replaced did NOT agree on this, and the difference is not
/// cosmetic — it decides whether both the old and the new target are alive at the same
/// moment. For a full-resolution compositor buffer that is tens of megabytes of GPU memory,
/// so the policy must stay per-site rather than being unified.
/// </remarks>
public enum BufferReplacePolicy
{
    /// <summary>
    /// Free the old target, then allocate the replacement — the lowest possible peak GPU
    /// memory, because the two never coexist. This is what <c>FrameCompositor</c>'s
    /// composite/crop buffers and <c>CursorRenderer</c>'s shutter scratch have always done,
    /// and it matters most for exactly those: they are the largest buffers in the pipeline
    /// and they resize whenever preview resolution, zoom scope or aspect ratio changes.
    /// </summary>
    DisposeThenAllocate,

    /// <summary>
    /// Allocate the replacement, then free the old one. Costs double the peak memory for
    /// the duration of the swap, in exchange for never leaving the field pointing at a
    /// disposed target when allocation throws. Appropriate only for small buffers — this is
    /// what <c>TextOverlayRenderer</c>'s blur scratch has always done.
    /// </summary>
    AllocateThenDispose,
}

/// <summary>
/// Owns a lazily-(re)allocated <see cref="CanvasRenderTarget"/> scratch buffer, unifying the
/// "if null or wrong size → allocate → swap → dispose" idiom that used to be
/// hand-rolled per class (<c>FrameCompositor.EnsureCompositeBuffer</c>/<c>EnsureCroppedBuffer</c>,
/// <c>TextOverlayRenderer</c>'s <c>EnsureBlurScratch</c>, <c>CursorRenderer</c>'s shutter-blur
/// scratch).
/// </summary>
/// <remarks>
/// Two axes of behavior are deliberately kept configurable, because the original call sites
/// disagreed on both and unifying either one is a silent behavior change:
/// <list type="bullet">
/// <item><b>Growth</b> — <see cref="Ensure"/> matches the requested size exactly (reallocating
/// even on shrink), which is what <c>FrameCompositor</c> and <c>TextOverlayRenderer</c> did;
/// <see cref="TryEnsureAtLeast"/> is grow-only and reuses a larger existing buffer, which is
/// what <c>CursorRenderer</c>'s shutter scratch did. That one also degrades gracefully via a
/// <c>bool</c> return instead of throwing, matching its original fall-back-to-direct-compositing
/// behavior.</item>
/// <item><b>Replacement order</b> — see <see cref="BufferReplacePolicy"/>. Defaults to
/// <see cref="BufferReplacePolicy.DisposeThenAllocate"/>, the lower-peak-memory order used by
/// the large compositor buffers.</item>
/// </list>
/// This type is used on the GPU hot path: the steady-state case (unchanged device and size)
/// is a handful of field reads/compares and returns the cached target — no allocation, no
/// boxing, no LINQ, no tuples.
/// </remarks>
public sealed class GrowOnlyBuffer : IDisposable
{
    private readonly BufferReplacePolicy _replacePolicy;
    private CanvasRenderTarget? _target;
    private CanvasDevice? _device;
    private int _width;
    private int _height;
    private bool _disposed;

    public GrowOnlyBuffer(BufferReplacePolicy replacePolicy = BufferReplacePolicy.DisposeThenAllocate)
        => _replacePolicy = replacePolicy;

    /// <summary>The currently-allocated target, or null if <see cref="Ensure"/>/
    /// <see cref="TryEnsureAtLeast"/> has never been called (or allocation always failed).</summary>
    public CanvasRenderTarget? Current => _target;

    /// <summary>
    /// Returns a target sized exactly <paramref name="width"/> x <paramref name="height"/> on
    /// <paramref name="device"/>, reallocating only when the device or either dimension has
    /// changed since the last call.
    /// </summary>
    /// <remarks>
    /// The device comparison uses Win2D's own equality rather than <see cref="object.ReferenceEquals"/>:
    /// reading a device back (e.g. from <c>CanvasDrawingSession.Device</c>) can hand out a
    /// different projection wrapper around the same underlying device, so a reference check
    /// reports a spurious change and reallocates a full-size render target on every single
    /// frame.
    /// </remarks>
    public CanvasRenderTarget Ensure(CanvasDevice device, int width, int height, string purpose, float dpi = 96f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_target is null || _device != device || _width != width || _height != height)
        {
            if (_replacePolicy == BufferReplacePolicy.DisposeThenAllocate)
            {
                // Free first so the old and new targets never coexist. Null the field before
                // allocating: if the allocation throws, the caller must not be left holding a
                // reference to a disposed target.
                _target?.Dispose();
                _target = null;
                _device = null;
                _width = 0;
                _height = 0;
                _target = Win2DUtils.CreateRenderTarget(device, width, height, dpi, purpose);
            }
            else
            {
                var next = Win2DUtils.CreateRenderTarget(device, width, height, dpi, purpose);
                _target?.Dispose();
                _target = next;
            }

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

        if (_target is null || _device != device || _width < width || _height < height)
        {
            // CursorRenderer always freed the old scratch before allocating, so the two never
            // coexisted; preserve that, since this buffer grows toward full frame size.
            if (_replacePolicy == BufferReplacePolicy.DisposeThenAllocate)
            {
                _target?.Dispose();
                _target = null;
                _device = null;
                _width = 0;
                _height = 0;
            }

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
