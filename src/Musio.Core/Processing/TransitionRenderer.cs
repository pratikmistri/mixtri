using Microsoft.Graphics.Canvas;
using Musio.Core.Timeline;

namespace Musio.Core.Processing;

/// <summary>
/// Renders transition effects between two frames (outgoing → incoming)
/// using Win2D compositing operations.
/// </summary>
public class TransitionRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private bool _disposed;

    public TransitionRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
    }

    /// <summary>
    /// Blends an outgoing frame and incoming frame based on the transition type and progress.
    /// </summary>
    /// <param name="outgoing">The frame being transitioned away from (can be null for fade-from-black).</param>
    /// <param name="incoming">The frame being transitioned to.</param>
    /// <param name="type">The transition effect type.</param>
    /// <param name="progress">Normalized progress of the transition (0 = fully outgoing, 1 = fully incoming).</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    public CanvasRenderTarget Render(
        CanvasBitmap? outgoing,
        CanvasBitmap incoming,
        TransitionType type,
        double progress,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = new CanvasRenderTarget(_device, width, height, 96);
        using var ds = target.CreateDrawingSession();

        progress = Math.Clamp(progress, 0, 1);

        switch (type)
        {
            case TransitionType.Fade:
                RenderFade(ds, outgoing, incoming, progress, width, height);
                break;
            case TransitionType.CrossFade:
                RenderCrossFade(ds, outgoing, incoming, progress, width, height);
                break;
            case TransitionType.SlideLeft:
                RenderSlide(ds, outgoing, incoming, progress, width, height, -1, 0);
                break;
            case TransitionType.SlideRight:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 1, 0);
                break;
            case TransitionType.SlideUp:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 0, -1);
                break;
            case TransitionType.SlideDown:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 0, 1);
                break;
            case TransitionType.Wipe:
                RenderWipe(ds, outgoing, incoming, progress, width, height);
                break;
            default:
                // No transition — just draw incoming
                ds.DrawImage(incoming);
                break;
        }

        return target;
    }

    /// <summary>Fade through black: outgoing fades out, then incoming fades in.</summary>
    private static void RenderFade(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        if (progress < 0.5)
        {
            // First half: outgoing fades out
            if (outgoing is not null)
            {
                float opacity = (float)(1.0 - progress * 2);
                ds.DrawImage(outgoing, new Windows.Foundation.Rect(0, 0, width, height),
                    new Windows.Foundation.Rect(0, 0, outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height),
                    opacity);
            }
        }
        else
        {
            // Second half: incoming fades in
            float opacity = (float)((progress - 0.5) * 2);
            ds.DrawImage(incoming, new Windows.Foundation.Rect(0, 0, width, height),
                new Windows.Foundation.Rect(0, 0, incoming.SizeInPixels.Width, incoming.SizeInPixels.Height),
                opacity);
        }
    }

    /// <summary>Direct crossfade: outgoing and incoming blend simultaneously.</summary>
    private static void RenderCrossFade(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        var destRect = new Windows.Foundation.Rect(0, 0, width, height);

        if (outgoing is not null)
        {
            var srcRect = new Windows.Foundation.Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
            ds.DrawImage(outgoing, destRect, srcRect, (float)(1.0 - progress));
        }

        var inSrcRect = new Windows.Foundation.Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        ds.DrawImage(incoming, destRect, inSrcRect, (float)progress);
    }

    /// <summary>Slide transition: incoming slides in from a direction, pushing outgoing out.</summary>
    private static void RenderSlide(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, int dirX, int dirY)
    {
        // Ease with smoothstep for natural motion
        double t = SmoothStep(progress);

        double offsetX = dirX * width * (1.0 - t);
        double offsetY = dirY * height * (1.0 - t);

        // Draw outgoing shifted away
        if (outgoing is not null)
        {
            double outOffsetX = -dirX * width * t;
            double outOffsetY = -dirY * height * t;
            var outDest = new Windows.Foundation.Rect(outOffsetX, outOffsetY, width, height);
            var outSrc = new Windows.Foundation.Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
            ds.DrawImage(outgoing, outDest, outSrc);
        }

        // Draw incoming sliding in
        var inDest = new Windows.Foundation.Rect(offsetX, offsetY, width, height);
        var inSrc = new Windows.Foundation.Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        ds.DrawImage(incoming, inDest, inSrc);
    }

    /// <summary>Horizontal wipe: incoming is revealed left-to-right.</summary>
    private static void RenderWipe(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        double t = SmoothStep(progress);
        int wipeX = (int)(width * t);

        // Draw outgoing (full)
        if (outgoing is not null)
        {
            ds.DrawImage(outgoing, new Windows.Foundation.Rect(0, 0, width, height),
                new Windows.Foundation.Rect(0, 0,
                    outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height));
        }
        else
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        }

        // Draw incoming clipped to the revealed area
        if (wipeX > 0)
        {
            using var layer = ds.CreateLayer(1.0f,
                new Windows.Foundation.Rect(0, 0, wipeX, height));
            ds.DrawImage(incoming, new Windows.Foundation.Rect(0, 0, width, height),
                new Windows.Foundation.Rect(0, 0,
                    incoming.SizeInPixels.Width, incoming.SizeInPixels.Height));
        }
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
