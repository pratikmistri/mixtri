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
    public float Size { get; init; } = 150f;
    public float Margin { get; init; } = 20f;
    public float BorderWidth { get; init; } = 3f;
    public string BorderColor { get; init; } = "#FFFFFF";
    public bool ShadowEnabled { get; init; } = true;
}

/// <summary>
/// Composites a webcam frame onto the output canvas with configurable shape,
/// position, border, and optional drop shadow.
/// </summary>
public class WebcamCompositor
{
    private readonly WebcamOverlayStyle _style;

    public WebcamCompositor(WebcamOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
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

        // Optional shadow behind the overlay
        if (_style.ShadowEnabled)
        {
            RenderShadow(session, x, y, size);
        }

        // Clip webcam frame to the configured shape
        using var clipGeometry = CreateClipGeometry(session.Device, x, y, size);
        using (session.CreateLayer(1.0f, clipGeometry))
        {
            session.DrawImage(webcamFrame, destRect);
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
        return _style.Position switch
        {
            WebcamPosition.TopLeft => (margin, margin),
            WebcamPosition.TopRight => (canvasWidth - size - margin, margin),
            WebcamPosition.BottomLeft => (margin, canvasHeight - size - margin),
            WebcamPosition.BottomRight => (canvasWidth - size - margin, canvasHeight - size - margin),
            _ => (canvasWidth - size - margin, canvasHeight - size - margin),
        };
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

    private void RenderShadow(CanvasDrawingSession session, float x, float y, float size)
    {
        var device = session.Device;
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
            BlurAmount = 8f,
            ShadowColor = Windows.UI.Color.FromArgb(100, 0, 0, 0),
        };

        session.DrawImage(shadowEffect, new Vector2(0, 4));
    }
}
