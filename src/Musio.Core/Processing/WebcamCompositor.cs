using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Windows.Foundation;

namespace Musio.Core.Processing;

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
    private bool _disposed;

    // Cached GPU resources — invalidated when style or canvas size changes
    private CanvasGeometry? _cachedClipGeometry;
    private CanvasRenderTarget? _cachedShadow;
    private (float x, float y, float size, int canvasW, int canvasH) _cacheKey;

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
    /// Renders a webcam frame onto the drawing session at the configured position.
    /// </summary>
    public void RenderWebcam(CanvasDrawingSession session, CanvasBitmap webcamFrame,
                             int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(webcamFrame);

        float size = _style.Size;
        float margin = _style.Margin;

        // Compute top-left origin of the overlay rectangle
        var (x, y) = CalculatePosition(canvasWidth, canvasHeight, size, margin);

        var destRect = new Rect(x, y, size, size);

        // Center-crop source frame to square (avoid stretching 16:9 into a square)
        float srcW = (float)webcamFrame.SizeInPixels.Width;
        float srcH = (float)webcamFrame.SizeInPixels.Height;
        float cropSize = Math.Min(srcW, srcH);
        var sourceRect = new Rect(
            (srcW - cropSize) / 2f,
            (srcH - cropSize) / 2f,
            cropSize, cropSize);

        // Ensure cached resources are valid for current layout
        var key = (x, y, size, canvasWidth, canvasHeight);
        if (_cachedClipGeometry is null || _cacheKey != key)
        {
            RebuildCache(session.Device, x, y, size);
            _cacheKey = key;
        }

        // Optional shadow behind the overlay (drawn from cache)
        if (_style.ShadowEnabled && _cachedShadow is not null)
        {
            session.DrawImage(_cachedShadow, new Vector2(0, ShadowOffsetY));
        }

        // Clip webcam frame to the configured shape
        using (session.CreateLayer(1.0f, _cachedClipGeometry))
        {
            if (_style.Mirrored)
            {
                var transform = session.Transform;
                session.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(x + size / 2f, y + size / 2f));
                session.DrawImage(webcamFrame, destRect, sourceRect);
                session.Transform = transform;
            }
            else
            {
                session.DrawImage(webcamFrame, destRect, sourceRect);
            }
        }

        // Border stroke
        if (_style.BorderWidth > 0)
        {
            var borderColor = ColorHelper.ParseColor(_style.BorderColor);
            DrawBorderStroke(session, x, y, size, borderColor);
        }
    }

    private (float x, float y) CalculatePosition(int canvasWidth, int canvasHeight, float size, float margin)
    {
        // Custom normalized position overrides the enum preset
        if (_style.NormalizedX.HasValue && _style.NormalizedY.HasValue)
        {
            float x = _style.NormalizedX.Value * canvasWidth;
            float y = _style.NormalizedY.Value * canvasHeight;
            // Clamp to keep overlay within canvas
            x = Math.Clamp(x, 0, Math.Max(0, canvasWidth - size));
            y = Math.Clamp(y, 0, Math.Max(0, canvasHeight - size));
            return (x, y);
        }

        return _style.Position switch
        {
            WebcamPosition.TopLeft => (margin, margin),
            WebcamPosition.TopRight => (canvasWidth - size - margin, margin),
            WebcamPosition.BottomLeft => (margin, canvasHeight - size - margin),
            WebcamPosition.BottomRight => (canvasWidth - size - margin, canvasHeight - size - margin),
            _ => (canvasWidth - size - margin, canvasHeight - size - margin),
        };
    }

    /// <summary>
    /// Rebuilds cached clip geometry and shadow render target for the current layout.
    /// </summary>
    private void RebuildCache(CanvasDevice device, float x, float y, float size)
    {
        InvalidateCache();
        _cachedClipGeometry = CreateClipGeometry(device, x, y, size);

        if (_style.ShadowEnabled)
        {
            // Allocate a full-canvas-sized RT so the shadow offset doesn't clip.
            // Use _cacheKey canvas dimensions if available, otherwise add generous padding.
            float rtW = x + size + ShadowBlurAmount * 2 + 1;
            float rtH = y + size + ShadowBlurAmount * 2 + ShadowOffsetY + 1;
            _cachedShadow = new CanvasRenderTarget(device, rtW, rtH, 96);

            using var clipGeometry = CreateClipGeometry(device, x, y, size);
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
                ds.DrawImage(shadowEffect);
            }
        }
    }

    private void InvalidateCache()
    {
        _cachedClipGeometry?.Dispose();
        _cachedClipGeometry = null;
        _cachedShadow?.Dispose();
        _cachedShadow = null;
    }

    private CanvasGeometry CreateClipGeometry(CanvasDevice device, float x, float y, float size)
    {
        return _style.Shape switch
        {
            WebcamShape.Circle => CanvasGeometry.CreateCircle(device, x + size / 2f, y + size / 2f, size / 2f),
            WebcamShape.RoundedRect => CanvasGeometry.CreateRoundedRectangle(device, x, y, size, size, size * 0.1f, size * 0.1f),
            WebcamShape.Rectangle => CanvasGeometry.CreateRectangle(device, x, y, size, size),
            _ => CanvasGeometry.CreateCircle(device, x + size / 2f, y + size / 2f, size / 2f),
        };
    }

    private void DrawBorderStroke(CanvasDrawingSession session, float x, float y, float size, Windows.UI.Color color)
    {
        float borderWidth = _style.BorderWidth;

        switch (_style.Shape)
        {
            case WebcamShape.Circle:
                session.DrawCircle(x + size / 2f, y + size / 2f, size / 2f, color, borderWidth);
                break;
            case WebcamShape.RoundedRect:
                float radius = size * 0.1f;
                session.DrawRoundedRectangle(x, y, size, size, radius, radius, color, borderWidth);
                break;
            case WebcamShape.Rectangle:
                session.DrawRectangle(x, y, size, size, color, borderWidth);
                break;
        }
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
