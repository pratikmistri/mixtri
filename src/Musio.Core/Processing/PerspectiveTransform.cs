using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Musio.Core.Models;

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

        // Default camera distance: proportional to the larger dimension for a
        // natural perspective. Closer camera = more dramatic foreshortening.
        if (cameraDistance <= 0)
            cameraDistance = Math.Max(width, height) * 1.5f;

        // Clamp rotation to prevent extreme distortion or behind-camera artifacts
        rotationYDegrees = Math.Clamp(rotationYDegrees, -MaxRotationDegrees, MaxRotationDegrees);
        rotationXDegrees = Math.Clamp(rotationXDegrees, -MaxRotationDegrees, MaxRotationDegrees);

        float radY = rotationYDegrees * MathF.PI / 180f;
        float radX = rotationXDegrees * MathF.PI / 180f;

        // Build the 4x4 transform:
        // 1. Translate to center the image at origin
        // 2. Rotate around Y axis, then X axis
        // 3. Apply perspective (camera at -cameraDistance looking at origin)
        var transform = BuildPerspectiveMatrix(width, height, radY, radX, cameraDistance);

        var device = source.Device;
        var output = new CanvasRenderTarget(device, width, height, 96);

        using (var ds = output.CreateDrawingSession())
        {
            // Clear with transparent — the caller's background is already in the source
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            using var effect = new Transform3DEffect
            {
                Source = source,
                TransformMatrix = transform,
                InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
            };

            ds.DrawImage(effect);
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
        // Rotation around Y axis (left-right tilt)
        var rotY = Matrix4x4.CreateRotationY(radiansY);

        // Rotation around X axis (top-bottom tilt)
        var rotX = Matrix4x4.CreateRotationX(radiansX);

        // Combined rotation: Y first, then X
        var rotation = rotY * rotX;

        // Perspective projection: the D2D 3DTransform effect operates on
        // coordinates centered at the image middle. After rotation, some
        // pixels have z != 0. We add a perspective term so that z > 0
        // (further from camera) appears smaller and z < 0 (closer) appears larger.
        //
        // The perspective matrix maps w = 1 + z/d, so after division:
        //   x' = x / (1 + z/d),  y' = y / (1 + z/d)
        var perspective = Matrix4x4.Identity;
        perspective.M34 = -1f / cameraDistance;

        return rotation * perspective;
    }
}
