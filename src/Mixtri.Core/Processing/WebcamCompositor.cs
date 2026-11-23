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

    /// <summary>Shadows fainter than this (after the overlay fade) are not drawn at all.</summary>
    private const float MinVisibleShadowAlpha = 1f / 255f;

    /// <summary>Margin around the shadow sprite's shape: covers ~3σ of the blur.</summary>
    private const float ShadowSpritePad = ShadowBlurAmount * 3f + 1f;

    // Cached GPU resources. Both keys carry the device, so a device change rebuilds.
    // The clip geometry is cheap and tracks the exact destination. The shadow is a sprite
    // sized to the shape (not the canvas) and, while the fullscreen animation runs, is
    // reused scaled to the moving rect instead of being re-rendered every frame — the
    // shadow fades out as the overlay grows, so the approximation is never prominent.
    private CanvasGeometry? _cachedClipGeometry;
    // Devices are compared with Win2DUtils.SameDevice beside these keys, never inside them.
    private (Rect dest, float radius) _clipKey;
    private CanvasDevice? _clipDevice;
    private CanvasRenderTarget? _cachedShadow;
    private (float width, float height, float radius) _shadowKey;
    private CanvasDevice? _shadowDevice;

    public WebcamCompositor(WebcamOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
    }

    /// <summary>
    /// Updates the overlay style (position, size) without recreating the compositor.
    /// </summary>
    public void UpdateStyle(WebcamOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        InvalidateCache();
    }

    /// <summary>
    /// Sets the fullscreen-animation factor in <c>[0,1]</c> applied on the next render:
    /// <c>0</c> = the normal overlay, <c>1</c> = covering the entire canvas.
    /// </summary>
    public void SetFullscreenFactor(float factor)
    {
        // No invalidation: the clip cache is keyed on the resolved destination, and the
        // shadow sprite is deliberately reused (scaled) while the animation runs.
        _fullscreenFactor = Math.Clamp(factor, 0f, 1f);
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

        // Fully faded out — nothing to draw, so nothing to (re)build either.
        if (_overlayOpacity <= 0f)
            return;

        var device = session.Device;
        EnsureClipGeometry(device, destRect, layout.CornerRadius);

        // Optional shadow behind the overlay (drawn from cache, faded out as it grows)
        float shadowAlpha = layout.ShadowAlpha * _overlayOpacity;
        if (shadowAlpha >= MinVisibleShadowAlpha)
        {
            var shadow = EnsureShadowSprite(device, destRect, layout.CornerRadius);
            DrawShadowSprite(session, shadow, destRect, shadowAlpha);
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

    private void EnsureClipGeometry(CanvasDevice device, Rect dest, float radius)
    {
        var key = (dest, radius);
        if (_cachedClipGeometry is not null && _clipKey == key
            && Win2DUtils.SameDevice(_clipDevice, device))
            return;

        _cachedClipGeometry?.Dispose();
        _cachedClipGeometry = null;
        _cachedClipGeometry = CreateClipGeometry(device, dest, radius);
        _clipKey = key;
        _clipDevice = device;
    }

    /// <summary>
    /// Returns the shadow sprite for the shape, rebuilding it only when the device or the
    /// resting shape changes. During the fullscreen animation the existing sprite is kept
    /// and drawn scaled (see <see cref="DrawShadowSprite"/>).
    /// </summary>
    private CanvasRenderTarget EnsureShadowSprite(CanvasDevice device, Rect dest, float radius)
    {
        var key = ((float)dest.Width, (float)dest.Height, radius);
        if (_cachedShadow is not null && Win2DUtils.SameDevice(_shadowDevice, device)
            && (_fullscreenFactor > 0f || _shadowKey == key))
        {
            return _cachedShadow;
        }

        _cachedShadow?.Dispose();
        _cachedShadow = null;

        float w = Math.Max(1f, (float)dest.Width);
        float h = Math.Max(1f, (float)dest.Height);
        // Whole-pixel sprite so its DIP size (96 DPI) is exactly what DrawShadowSprite maps.
        var sprite = Win2DUtils.CreateRenderTarget(
            device, MathF.Ceiling(w + ShadowSpritePad * 2), MathF.Ceiling(h + ShadowSpritePad * 2),
            96, "webcam shadow");
        try
        {
            var local = new Rect(ShadowSpritePad, ShadowSpritePad, w, h);
            using var clipGeometry = CreateClipGeometry(device, local, radius);
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

            // Render the shadow without offset — offset is applied when drawing the sprite
            using (var ds = sprite.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.DrawImage(shadowEffect);
            }
        }
        catch
        {
            sprite.Dispose();
            throw;
        }

        _cachedShadow = sprite;
        _shadowKey = (w, h, radius);
        _shadowDevice = device;
        return sprite;
    }

    /// <summary>
    /// Draws the shadow sprite so its shape lands on <paramref name="dest"/> (shifted down by
    /// the shadow offset). At rest the sprite matches the rect exactly and this is a 1:1 blit;
    /// while animating it is stretched to the current rect.
    /// </summary>
    private void DrawShadowSprite(
        CanvasDrawingSession session, CanvasRenderTarget sprite, Rect dest, float alpha)
    {
        // Scale from the shape the sprite was rendered for to the current destination.
        double shapeW = _shadowKey.width;
        double shapeH = _shadowKey.height;
        if (shapeW <= 0 || shapeH <= 0) return;

        var spriteSize = sprite.Size;
        double sx = dest.Width / shapeW;
        double sy = dest.Height / shapeH;
        var target = new Rect(
            dest.X - ShadowSpritePad * sx,
            dest.Y - ShadowSpritePad * sy + ShadowOffsetY,
            spriteSize.Width * sx,
            spriteSize.Height * sy);

        session.DrawImage(sprite, target, sprite.Bounds, alpha);
    }

    private void InvalidateCache()
    {
        _cachedClipGeometry?.Dispose();
        _cachedClipGeometry = null;
        _clipKey = default;
        _clipDevice = null;
        _cachedShadow?.Dispose();
        _cachedShadow = null;
        _shadowKey = default;
        _shadowDevice = null;
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
