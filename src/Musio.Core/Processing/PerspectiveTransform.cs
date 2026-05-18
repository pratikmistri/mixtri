using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace Musio.Core.Processing;

/// <summary>
/// Applies a true perspective transform to a composed frame by projecting it
/// as a flat plane rotated in 3D space. Uses strip-based rendering: divides
/// the frame into vertical/horizontal strips and draws each at its projected
/// position and scale. Produces geometrically correct perspective projection
/// (far side shrinks, near side enlarges).
/// </summary>
public sealed class PerspectiveTransform
{
    private const float MaxRotationDegrees = 30f;
    private const int StripCount = 120;

    /// <summary>
    /// Applies 3D camera rotation to the composed frame.
    /// </summary>
    public CanvasRenderTarget Apply(
        CanvasRenderTarget source,
        float rotationYDegrees,
        float rotationXDegrees,
        float cameraDistance = 0)
    {
        int width = (int)source.SizeInPixels.Width;
        int height = (int)source.SizeInPixels.Height;

        if (cameraDistance <= 0)
            cameraDistance = Math.Max(width, height) * 1.2f;

        rotationYDegrees = Math.Clamp(rotationYDegrees, -MaxRotationDegrees, MaxRotationDegrees);
        rotationXDegrees = Math.Clamp(rotationXDegrees, -MaxRotationDegrees, MaxRotationDegrees);

        float radY = rotationYDegrees * MathF.PI / 180f;
        float radX = rotationXDegrees * MathF.PI / 180f;

        var device = source.Device;
        var output = new CanvasRenderTarget(device, width, height, 96);

        using var ds = output.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        ds.Antialiasing = Microsoft.Graphics.Canvas.CanvasAntialiasing.Antialiased;

        if (MathF.Abs(radY) > 0.001f)
            RenderYRotation(ds, source, width, height, radY, cameraDistance);
        else if (MathF.Abs(radX) > 0.001f)
            RenderXRotation(ds, source, width, height, radX, cameraDistance);

        return output;
    }

    /// <summary>
    /// Renders the frame rotated around the Y axis using vertical strip projection.
    /// Each strip's left and right edges are independently projected for correct
    /// perspective (far side shrinks, near side grows).
    /// </summary>
    private static void RenderYRotation(
        CanvasDrawingSession ds, CanvasRenderTarget source,
        int width, int height, float radians, float camDist)
    {
        float cosA = MathF.Cos(radians);
        float sinA = MathF.Sin(radians);
        float halfW = width / 2f;
        float halfH = height / 2f;
        float stripWidth = (float)width / StripCount;

        for (int i = 0; i < StripCount; i++)
        {
            float srcLeft = i * stripWidth;
            float srcRight = srcLeft + stripWidth;

            // Project left and right edges independently
            float projLeft = ProjectX(srcLeft - halfW, cosA, sinA, camDist) + halfW;
            float projRight = ProjectX(srcRight - halfW, cosA, sinA, camDist) + halfW;

            float destW = projRight - projLeft;
            if (destW < 0.1f) continue;

            // Height scales with depth at strip center
            float centerX = (srcLeft + srcRight) / 2f - halfW;
            float z = centerX * sinA;
            float scale = camDist / (camDist + z);
            float destH = height * scale;
            float destY = halfH - destH / 2f;

            ds.DrawImage(source,
                new Rect(projLeft, destY, destW, destH),
                new Rect(srcLeft, 0, stripWidth, height),
                1f, CanvasImageInterpolation.Linear);
        }
    }

    /// <summary>
    /// Renders the frame rotated around the X axis using horizontal strip projection.
    /// </summary>
    private static void RenderXRotation(
        CanvasDrawingSession ds, CanvasRenderTarget source,
        int width, int height, float radians, float camDist)
    {
        float cosA = MathF.Cos(radians);
        float sinA = MathF.Sin(radians);
        float halfW = width / 2f;
        float halfH = height / 2f;
        float stripHeight = (float)height / StripCount;

        for (int i = 0; i < StripCount; i++)
        {
            float srcTop = i * stripHeight;
            float srcBottom = srcTop + stripHeight;

            float projTop = ProjectX(srcTop - halfH, cosA, sinA, camDist) + halfH;
            float projBottom = ProjectX(srcBottom - halfH, cosA, sinA, camDist) + halfH;

            float destH = projBottom - projTop;
            if (destH < 0.1f) continue;

            float centerY = (srcTop + srcBottom) / 2f - halfH;
            float z = centerY * sinA;
            float scale = camDist / (camDist + z);
            float destW = width * scale;
            float destX = halfW - destW / 2f;

            ds.DrawImage(source,
                new Rect(destX, projTop, destW, destH),
                new Rect(0, srcTop, width, stripHeight),
                1f, CanvasImageInterpolation.Linear);
        }
    }

    /// <summary>
    /// Projects a 1D coordinate through Y-axis rotation + perspective.
    /// Input: offset from center. Output: projected offset from center.
    /// </summary>
    private static float ProjectX(float offset, float cosA, float sinA, float camDist)
    {
        float rotated = offset * cosA;
        float z = offset * sinA;
        float scale = camDist / (camDist + z);
        return rotated * scale;
    }

    /// <summary>
    /// Builds a 4x4 perspective matrix (kept for test compatibility).
    /// </summary>
    public static Matrix4x4 BuildPerspectiveMatrix(
        int width, int height,
        float radiansY, float radiansX,
        float cameraDistance)
    {
        var rotY = Matrix4x4.CreateRotationY(radiansY);
        var rotX = Matrix4x4.CreateRotationX(radiansX);
        var rotation = rotY * rotX;
        var perspective = Matrix4x4.Identity;
        perspective.M34 = 1f / cameraDistance;
        return rotation * perspective;
    }
}
