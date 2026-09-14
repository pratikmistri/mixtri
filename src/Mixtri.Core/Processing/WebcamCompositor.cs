using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Windows.Foundation;

namespace Mixtri.Core.Processing;

public enum WebcamShape { Circle, RoundedRect, Rectangle }
public enum WebcamPosition { TopLeft, TopRight, BottomLeft, BottomRight }

public record WebcamOverlayStyle
{
    public WebcamShape Shape { get; init; } = WebcamShape.Circle;
    public WebcamPosition Position { get; init; } = WebcamPosition.BottomRight;
    public float Size { get; init; } = 300f;
    public float Margin { get; init; } = 20f;
    public float BorderWidth { get; init; } = 8f;
    public string BorderColor { get; init; } = "#FFFFFF";
    public bool ShadowEnabled { get; init; } = true;

    /// <summary>
    /// Custom X position as a fraction (0–1) of the output canvas width.
    /// When non-null, overrides the <see cref="Position"/> enum.
    /// </summary>
    public float? NormalizedX { get; init; }

    /// <summary>
    /// Custom Y position as a fraction (0–1) of the output canvas height.
    /// When non-null, overrides the <see cref="Position"/> enum.
    /// </summary>
    public float? NormalizedY { get; init; }

    /// <summary>
    /// When true, the webcam frame is horizontally flipped (mirror mode).
    /// </summary>
    public bool Mirrored { get; init; } = false;
}

/// <summary>
/// Composites a webcam frame onto the output canvas with configurable shape,
/// position, border, and optional drop shadow.
/// Caches clip geometry and shadow render target to avoid per-frame GPU allocations.
/// </summary>
public class WebcamCompositor : IDisposable
{
    private const float ShadowBlurAmount = 8f;
    private const float ShadowOffsetY = 4f;

    private WebcamOverlayStyle _style;
    private float _fullscreenFactor;
    private float _overlayOpacity = 1f;
    private bool _disposed;

    // Cached GPU resources — invalidated when style or canvas size changes
    private CanvasGeometry? _cachedClipGeometry;
    private CanvasRenderTarget? _cachedShadow;
    private Vector2 _shadowOrigin;
    private CanvasDevice? _cacheDevice;
    private bool _cacheValid;
    private (Rect dest, float radius, int canvasW, int canvasH, bool shadow) _cacheKey;
    internal int CacheBuildCount { get; private set; }
    internal Windows.Graphics.SizeInt32 ShadowSize => _cachedShadow is { } shadow
        ? new() { Width = (int)shadow.SizeInPixels.Width, Height = (int)shadow.SizeInPixels.Height }
        : default;

    public WebcamCompositor(WebcamOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
    }

    /// <summary>
    /// Updates the overlay style (position, size) without recreating the compositor.
    /// </summary>
    public void UpdateStyle(WebcamOverlayStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (_style == style) return;
        _style = style;
        InvalidateCache();
    }

    /// <summary>
    /// Sets the fullscreen-animation factor in <c>[0,1]</c> applied on the next render:
    /// <c>0</c> = the normal overlay, <c>1</c> = covering the entire canvas.
    /// </summary>
    public void SetFullscreenFactor(float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        if (Math.Abs(factor - _fullscreenFactor) > float.Epsilon)
        {
            _fullscreenFactor = factor;
            InvalidateCache();
        }
    }

    /// <summary>
    /// Sets the overlay opacity in <c>[0,1]</c> applied on the next render, used to fade
    /// the camera in at the segment start and out at the end. Opacity is applied at draw
    /// time and does not invalidate the geometry cache.
    /// </summary>
    public void SetOverlayOpacity(float opacity)
    {
        _overlayOpacity = Math.Clamp(opacity, 0f, 1f);
    }

    /// <summary>
    /// Renders a webcam frame onto the drawing session at the configured position,
    /// applying the current fullscreen-animation factor (see <see cref="SetFullscreenFactor"/>).
    /// </summary>
    public void RenderWebcam(CanvasDrawingSession session, CanvasBitmap webcamFrame,
                             int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(webcamFrame);

        float srcW = (float)webcamFrame.SizeInPixels.Width;
        float srcH = (float)webcamFrame.SizeInPixels.Height;

        var layout = WebcamLayoutCalculator.ComputeAnimatedLayout(
            _style, canvasWidth, canvasHeight, srcW, srcH, _fullscreenFactor);

        var destRect = layout.Destination;
        var sourceRect = layout.SourceCrop;

        // Ensure cached resources are valid for current layout
        var key = (destRect, layout.CornerRadius, canvasWidth, canvasHeight, layout.ShadowAlpha > 0f);
        if (!_cacheValid || _cachedClipGeometry is null || _cacheKey != key || _cacheDevice != session.Device)
        {
            RebuildCache(session.Device, destRect, layout.CornerRadius, layout.ShadowAlpha > 0f);
            _cacheKey = key;
        }

        // Fully faded out — nothing to draw.
        if (_overlayOpacity <= 0f)
            return;

        // Optional shadow behind the overlay (drawn from cache, faded out as it grows)
        if (layout.ShadowAlpha > 0f && _cachedShadow is not null)
        {
            session.DrawImage(_cachedShadow, _shadowOrigin + new Vector2(0, ShadowOffsetY),
                _cachedShadow.Bounds, layout.ShadowAlpha * _overlayOpacity);
        }

        // Clip webcam frame to the configured shape (layer opacity fades the camera in/out)
        using (session.CreateLayer(_overlayOpacity, _cachedClipGeometry))
        {
            if (_style.Mirrored)
            {
                float cx = (float)(destRect.X + destRect.Width / 2.0);
                float cy = (float)(destRect.Y + destRect.Height / 2.0);
                var transform = session.Transform;
                session.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(cx, cy));
                session.DrawImage(webcamFrame, destRect, sourceRect);
                session.Transform = transform;
            }
            else
            {
                session.DrawImage(webcamFrame, destRect, sourceRect);
            }
        }

        // Border stroke (fades out as the overlay grows to fullscreen)
        if (layout.BorderWidth > 0)
        {
            var borderColor = ColorHelper.ParseColor(_style.BorderColor);
            borderColor.A = (byte)(borderColor.A * _overlayOpacity);
            DrawBorderStroke(session, destRect, layout.CornerRadius, layout.BorderWidth, borderColor);
        }
    }

    /// <summary>
    /// Rebuilds cached clip geometry and shadow render target for the current layout.
    /// </summary>
    private void RebuildCache(CanvasDevice device, Rect dest, float radius, bool shadowEnabled)
    {
        InvalidateCache();
        _cacheDevice = device;
        CacheBuildCount++;
        _cachedClipGeometry = CreateClipGeometry(device, dest, radius);

        if (shadowEnabled)
        {
            // Pad all edges so the shadow blur is never clipped near a canvas edge.
            // Pad every edge past the blur's own reach so the local surface cannot clip it:
            // a Direct2D Gaussian at BlurAmount s extends ~3s, so 3s + 2 leaves a margin.
            float pad = ShadowBlurAmount * 3 + 2;
            _shadowOrigin = new Vector2(
                MathF.Floor((float)dest.X - pad), MathF.Floor((float)dest.Y - pad));
            float rtW = MathF.Ceiling((float)dest.Right + pad) - _shadowOrigin.X;
            float rtH = MathF.Ceiling((float)dest.Bottom + pad) - _shadowOrigin.Y;
            _cachedShadow = Win2DUtils.CreateRenderTarget(device, rtW, rtH, 96, "webcam shadow");

            using var clipGeometry = CreateClipGeometry(device, dest, radius);
            using var commandList = new CanvasCommandList(device);
            using (var maskSession = commandList.CreateDrawingSession())
            {
                var shadowColor = Windows.UI.Color.FromArgb(128, 0, 0, 0);
                maskSession.FillGeometry(clipGeometry, shadowColor);
            }

            using var shadowEffect = new ShadowEffect
            {
                Source = commandList,
                BlurAmount = ShadowBlurAmount,
                ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0),
            };

            // Render the shadow without offset — offset is applied when drawing the cache
            using (var ds = _cachedShadow.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.Transform = Matrix3x2.CreateTranslation(-_shadowOrigin);
                ds.DrawImage(shadowEffect);
            }
        }
        _cacheValid = true;
    }

    private void InvalidateCache()
    {
        _cacheValid = false;
        _cachedClipGeometry?.Dispose();
        _cachedClipGeometry = null;
        _cachedShadow?.Dispose();
        _cachedShadow = null;
    }

    /// <summary>
    /// Builds the clip geometry as a rounded rectangle. A square rectangle with a
    /// corner radius equal to half its side is a circle, so this covers all shapes
    /// and the morph from circle → fullscreen rectangle as the radius collapses to 0.
    /// </summary>
    private static CanvasGeometry CreateClipGeometry(CanvasDevice device, Rect dest, float radius)
    {
        float maxRadius = (float)Math.Min(dest.Width, dest.Height) / 2f;
        radius = Math.Clamp(radius, 0f, maxRadius);
        if (radius <= 0.01f)
            return CanvasGeometry.CreateRectangle(device, dest);
        return CanvasGeometry.CreateRoundedRectangle(
            device, dest, radius, radius);
    }

    private static void DrawBorderStroke(CanvasDrawingSession session, Rect dest,
                                         float radius, float borderWidth, Windows.UI.Color color)
    {
        float maxRadius = (float)Math.Min(dest.Width, dest.Height) / 2f;
        radius = Math.Clamp(radius, 0f, maxRadius);
        if (radius <= 0.01f)
            session.DrawRectangle(dest, color, borderWidth);
        else
            session.DrawRoundedRectangle(dest, radius, radius, color, borderWidth);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            InvalidateCache();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
