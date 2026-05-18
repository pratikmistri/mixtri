using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace Musio.Core.Processing;

/// <summary>
/// Applies a true 3D camera perspective transform to a composed frame using
/// Win2D's <see cref="Transform3DEffect"/> (wraps D2D's 3D perspective transform).
/// The composed frame is treated as a flat card in 3D space; the camera orbits
/// and the card rotates to produce a cinematic floating-screen effect.
/// </summary>
public sealed class PerspectiveTransform
{
    /// <summary>Maximum supported rotation in degrees to prevent extreme distortion.</summary>
    private const float MaxRotationDegrees = 30f;

    /// <summary>
    /// Applies 3D camera rotation to the composed frame and renders it onto a
    /// new output target with a stable background.
    /// </summary>
    /// <param name="source">The fully composed frame (content + background + cursor).</param>
    /// <param name="rotationYDegrees">Rotation around the Y axis in degrees (positive = right side toward viewer).</param>
    /// <param name="rotationXDegrees">Rotation around the X axis in degrees (positive = top away from viewer).</param>
    /// <param name="cameraDistance">Virtual camera distance from the frame plane, in pixels. Larger = subtler perspective.</param>
    /// <returns>A new <see cref="CanvasRenderTarget"/> with the 3D-transformed frame. Caller must dispose.</returns>
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

        var transform = BuildPerspectiveMatrix(width, height, radY, radX, cameraDistance);

        var device = source.Device;
        var output = new CanvasRenderTarget(device, width, height, 96);

        using (var ds = output.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            using var effect = new Transform3DEffect
            {
                Source = source,
                TransformMatrix = transform,
                InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
            };

            // Draw centered — the effect output may shift due to perspective
            var bounds = effect.GetBounds(ds);
            float offsetX = (float)((width - bounds.Width) / 2 - bounds.X);
            float offsetY = (float)((height - bounds.Height) / 2 - bounds.Y);
            ds.DrawImage(effect, offsetX, offsetY);
        }

        return output;
    }

    /// <summary>
    /// Builds a 4x4 perspective transform matrix for the D2D 3DTransform effect.
    /// The effect treats source pixels as positions relative to the image center,
    /// applies the matrix, performs perspective division (x/w, y/w), and maps
    /// back to pixel coordinates.
    /// </summary>
    public static Matrix4x4 BuildPerspectiveMatrix(
        int width, int height,
        float radiansY, float radiansX,
        float cameraDistance)
    {
        var rotY = Matrix4x4.CreateRotationY(radiansY);
        var rotX = Matrix4x4.CreateRotationX(radiansX);
        var rotation = rotY * rotX;

        // Perspective: w' = 1 + z/d. Far pixels (z > 0) get w' > 1 → appear
        // smaller after division. Near pixels (z < 0) get w' < 1 → appear larger.
        var perspective = Matrix4x4.Identity;
        perspective.M34 = 1f / cameraDistance;

        return rotation * perspective;
    }
}
