using Windows.Foundation;

namespace Musio.Core.Processing;

/// <summary>
/// Resolved, GPU-free geometry describing how a webcam frame should be drawn for a
/// given fullscreen-animation factor. Factor <c>0</c> reproduces the normal overlay
/// (square, positioned per <see cref="WebcamOverlayStyle"/>); factor <c>1</c> covers
/// the entire canvas.
/// </summary>
public readonly record struct WebcamLayout(
    Rect Destination,
    Rect SourceCrop,
    float CornerRadius,
    float BorderWidth,
    float ShadowAlpha)
{
    /// <summary>True when the shape is a perfect circle (square dest with radius = half side).</summary>
    public bool IsCircle =>
        CornerRadius > 0f &&
        Math.Abs(Destination.Width - Destination.Height) < 0.01 &&
        Math.Abs(CornerRadius - (float)Destination.Width / 2f) < 0.01f;
}

/// <summary>
/// Pure interpolation math for the webcam overlay. Kept GPU-free so it can be unit
/// tested independently of <see cref="WebcamCompositor"/>.
/// </summary>
public static class WebcamLayoutCalculator
{
    /// <summary>
    /// Computes the animated webcam layout for a given canvas, frame size, and
    /// fullscreen factor in <c>[0,1]</c>. The clip is always expressed as a rounded
    /// rectangle whose corner radius collapses to <c>0</c> at fullscreen, so a circle
    /// base morphs smoothly into a full-screen rectangle.
    /// </summary>
    public static WebcamLayout ComputeAnimatedLayout(
        WebcamOverlayStyle style, int canvasWidth, int canvasHeight,
        float frameWidth, float frameHeight, float factor)
    {
        ArgumentNullException.ThrowIfNull(style);

        factor = Math.Clamp(factor, 0f, 1f);

        float size = style.Size;
        var (baseX, baseY) = CalculatePosition(style, canvasWidth, canvasHeight, size, style.Margin);

        // Destination: lerp the base square overlay → full canvas.
        float destX = Lerp(baseX, 0f, factor);
        float destY = Lerp(baseY, 0f, factor);
        float destW = Lerp(size, canvasWidth, factor);
        float destH = Lerp(size, canvasHeight, factor);
        var destination = new Rect(destX, destY, destW, destH);

        // Corner radius as a fraction of the shorter side, collapsing to 0 at fullscreen.
        float baseRadiusFraction = style.Shape switch
        {
            WebcamShape.Circle => 0.5f,
            WebcamShape.RoundedRect => 0.1f,
            _ => 0f,
        };
        float radiusFraction = Lerp(baseRadiusFraction, 0f, factor);
        float cornerRadius = radiusFraction * Math.Min(destW, destH);

        // Source crop: cover the current destination aspect ratio (square at factor 0).
        var sourceCrop = ComputeCoverCrop(frameWidth, frameHeight, destW, destH);

        float borderWidth = Lerp(style.BorderWidth, 0f, factor);
        float shadowAlpha = style.ShadowEnabled ? Lerp(1f, 0f, factor) : 0f;

        return new WebcamLayout(destination, sourceCrop, cornerRadius, borderWidth, shadowAlpha);
    }

    /// <summary>
    /// Top-left origin of the normal (non-animated) square overlay, matching the
    /// legacy positioning rules (normalized override or corner preset, clamped).
    /// </summary>
    public static (float x, float y) CalculatePosition(
        WebcamOverlayStyle style, int canvasWidth, int canvasHeight, float size, float margin)
    {
        if (style.NormalizedX.HasValue && style.NormalizedY.HasValue)
        {
            float x = style.NormalizedX.Value * canvasWidth;
            float y = style.NormalizedY.Value * canvasHeight;
            x = Math.Clamp(x, 0, Math.Max(0, canvasWidth - size));
            y = Math.Clamp(y, 0, Math.Max(0, canvasHeight - size));
            return (x, y);
        }

        return style.Position switch
        {
            WebcamPosition.TopLeft => (margin, margin),
            WebcamPosition.TopRight => (canvasWidth - size - margin, margin),
            WebcamPosition.BottomLeft => (margin, canvasHeight - size - margin),
            WebcamPosition.BottomRight => (canvasWidth - size - margin, canvasHeight - size - margin),
            _ => (canvasWidth - size - margin, canvasHeight - size - margin),
        };
    }

    /// <summary>
    /// Center "cover" crop of a source frame for a target aspect ratio (no stretching).
    /// </summary>
    public static Rect ComputeCoverCrop(float srcW, float srcH, float targetW, float targetH)
    {
        if (srcW <= 0 || srcH <= 0 || targetW <= 0 || targetH <= 0)
            return new Rect(0, 0, Math.Max(1, srcW), Math.Max(1, srcH));

        float targetAspect = targetW / targetH;
        float srcAspect = srcW / srcH;

        float cropW, cropH;
        if (srcAspect > targetAspect)
        {
            // Source is wider than target → crop the sides.
            cropH = srcH;
            cropW = srcH * targetAspect;
        }
        else
        {
            // Source is taller than target → crop the top/bottom.
            cropW = srcW;
            cropH = srcW / targetAspect;
        }

        float cropX = (srcW - cropW) / 2f;
        float cropY = (srcH - cropH) / 2f;
        return new Rect(cropX, cropY, cropW, cropH);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
