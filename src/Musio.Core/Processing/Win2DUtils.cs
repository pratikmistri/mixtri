using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Musio.Core.Diagnostics;

namespace Musio.Core.Processing;

/// <summary>
/// Shared Win2D allocation helpers. <see cref="CreateRenderTarget"/> centralizes the
/// OOM/<see cref="COMException"/>-guarded <c>CanvasRenderTarget</c> allocation that used to
/// be reimplemented ad hoc (or not guarded at all) at each of the ~20 call sites across the
/// codebase — see FIX 1 in the Wave 3 work item.
/// </summary>
public static class Win2DUtils
{
    /// <summary>
    /// Allocates a <see cref="CanvasRenderTarget"/>, wrapping an <see cref="OutOfMemoryException"/>
    /// or <see cref="COMException"/> raised during allocation into a logged, user-friendly
    /// <see cref="InvalidOperationException"/>. <paramref name="purpose"/> is a short
    /// human-readable description of what the target is used for (used only in the
    /// exception message and the diagnostic log line).
    /// </summary>
    /// <remarks>
    /// [FIX 1] Sites that previously let a raw <see cref="OutOfMemoryException"/> or
    /// <see cref="COMException"/> propagate now throw this wrapped exception instead. Sites
    /// that already had their own try/catch around allocation are unaffected in observable
    /// behavior other than the exception type/message seen by that catch (see the per-site
    /// notes in the Wave 3 report for the two sites, <c>CursorRenderer</c> and
    /// <c>VideoThumbnailExtractor</c>, where that distinction matters).
    /// </remarks>
    public static CanvasRenderTarget CreateRenderTarget(
        CanvasDevice device, float width, float height, float dpi, string purpose)
    {
        try
        {
            return new CanvasRenderTarget(device, width, height, dpi);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or COMException)
        {
            DiagLog.Write(
                "Win2DUtils",
                $"Failed to allocate {purpose} render target ({width}x{height}): {ex.Message}");
            throw new InvalidOperationException(
                $"Failed to allocate {purpose} render target ({width}x{height}). " +
                "Try a lower resolution or close other GPU-heavy applications.", ex);
        }
    }
}
