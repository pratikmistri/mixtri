using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Musio.Core.Models;
using Windows.UI;

namespace Musio.Core.Processing;

public record BackgroundStyle
{
    public BackgroundType Type { get; init; } = BackgroundType.SolidColor;
    public string Color { get; init; } = "#1a1a2e";
    public string GradientEndColor { get; init; } = "#16213e";
    public double GradientAngle { get; init; } = 135;
    public string? BackgroundImagePath { get; init; }
    public int Padding { get; init; } = 48;
    public int CornerRadius { get; init; } = 12;
    public bool ShadowEnabled { get; init; } = true;
    public int ShadowBlur { get; init; } = 24;
    public double ShadowOpacity { get; init; } = 0.5;
    public string ShadowColor { get; init; } = "#000000";
    public int ShadowOffsetX { get; init; } = 0;
    public int ShadowOffsetY { get; init; } = 4;
    public bool BorderEnabled { get; init; } = false;
    public int BorderWidth { get; init; } = 1;
    public string BorderColor { get; init; } = "#333333";
}

/// <summary>
/// Renders background, padding, corner radius, shadow, and border around a captured screen frame.
/// Operates purely on a caller-provided <see cref="CanvasDrawingSession"/>.
/// </summary>
public sealed class BackgroundCompositor : IDisposable
{
    private CanvasBitmap? _cachedBackgroundImage;
    private string? _cachedBackgroundPath;
    private CanvasDevice? _cachedDevice;
    private bool _disposed;
    /// <summary>
    /// Returns the output canvas dimensions. Padding now insets the content within
    /// the canvas rather than extending it, so the canvas size matches the configured
    /// aspect ratio regardless of padding.
    /// </summary>
    public (int width, int height) CalculateOutputSize(int sourceWidth, int sourceHeight, BackgroundStyle style)
    {
        _ = style;
        return (sourceWidth, sourceHeight);
    }

    /// <summary>
    /// Renders one complete composited frame: background → shadow → screen content → border.
    /// Padding (user setting + any aspect-ratio fit gap) is implicit in the source rect's
    /// position and dimensions — there is no separate inner-content container.
    /// </summary>
    /// <param name="screenFrame">Cropped buffer sized exactly to (srcWidth, srcHeight).</param>
    /// <param name="srcX">X position of the source frame inside the output canvas.</param>
    /// <param name="srcY">Y position of the source frame inside the output canvas.</param>
    /// <param name="srcWidth">Width of the source frame inside the output canvas.</param>
    /// <param name="srcHeight">Height of the source frame inside the output canvas.</param>
    public void CompositeFrame(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        int outputWidth,
        int outputHeight,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        BackgroundStyle style)
    {
        float w = outputWidth;
        float h = outputHeight;
        float sx = srcX;
        float sy = srcY;
        float sw = srcWidth;
        float sh = srcHeight;
        float radius = style.CornerRadius;

        // 1. Background fills the entire output canvas — everything outside the
        //    source rect is one continuous background container.
        DrawBackground(session, screenFrame, w, h, sw, sh, sx, sy, style);

        // 2. Shadow under the source frame.
        if (style.ShadowEnabled)
        {
            DrawShadow(session, sx, sy, sw, sh, radius, style);
        }

        // 3. Source content drawn 1:1 at its rect; rounded-corner clip wraps the frame.
        DrawContent(session, screenFrame, sx, sy, sw, sh, radius);

        // 4. Border around the source frame.
        if (style.BorderEnabled && style.BorderWidth > 0)
        {
            DrawBorder(session, sx, sy, sw, sh, radius, style);
        }
    }

    private void DrawBackground(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float w, float h,
        float contentW, float contentH,
        float contentX, float contentY,
        BackgroundStyle style)
    {
        switch (style.Type)
        {
            case BackgroundType.SolidColor:
                session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                break;

            case BackgroundType.Gradient:
                DrawGradientBackground(session, w, h, style);
                break;

            case BackgroundType.Image:
                DrawImageBackground(session, w, h, style);
                break;

            case BackgroundType.Blur:
                DrawBlurBackground(session, screenFrame, w, h, contentW, contentH, contentX, contentY, style);
                break;
        }
    }

    private static void DrawGradientBackground(
        CanvasDrawingSession session, float w, float h, BackgroundStyle style)
    {
        double angleRad = style.GradientAngle * Math.PI / 180.0;
        float cx = w / 2f;
        float cy = h / 2f;
        float diag = MathF.Sqrt(w * w + h * h) / 2f;
        float dx = diag * MathF.Cos((float)angleRad);
        float dy = diag * MathF.Sin((float)angleRad);

        var startColor = ColorHelper.ParseColor(style.Color);
        var endColor = ColorHelper.ParseColor(style.GradientEndColor);

        using var brush = new CanvasLinearGradientBrush(session, startColor, endColor)
        {
            StartPoint = new Vector2(cx - dx, cy - dy),
            EndPoint = new Vector2(cx + dx, cy + dy)
        };

        session.FillRectangle(0, 0, w, h, brush);
    }

    private void DrawImageBackground(
        CanvasDrawingSession session, float w, float h, BackgroundStyle style)
    {
        if (string.IsNullOrEmpty(style.BackgroundImagePath))
        {
            // Fallback to solid color when no image path is set
            session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
            return;
        }

        // Cache the wallpaper bitmap; reload if path or device changed (device-lost recovery)
        if (_cachedBackgroundImage is null
            || _cachedBackgroundPath != style.BackgroundImagePath
            || _cachedDevice != session.Device)
        {
            _cachedBackgroundImage?.Dispose();
            _cachedBackgroundImage = null;
            _cachedBackgroundPath = null;
            _cachedDevice = null;

            try
            {
                _cachedBackgroundImage = CanvasBitmap.LoadAsync(session.Device, style.BackgroundImagePath).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                _cachedBackgroundPath = style.BackgroundImagePath;
                _cachedDevice = session.Device;
            }
            catch
            {
                // File missing/unreadable/corrupt — fall back to solid color
                session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                return;
            }
        }

        var srcSize = _cachedBackgroundImage.SizeInPixels;

        // Scale-to-fill: compute scale so image covers entire output
        float scaleX = w / srcSize.Width;
        float scaleY = h / srcSize.Height;
        float scale = Math.Max(scaleX, scaleY);
        float drawW = srcSize.Width * scale;
        float drawH = srcSize.Height * scale;
        float drawX = (w - drawW) / 2f;
        float drawY = (h - drawH) / 2f;

        session.DrawImage(_cachedBackgroundImage, new Windows.Foundation.Rect(drawX, drawY, drawW, drawH));
    }

    private static void DrawBlurBackground(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float w, float h,
        float contentW, float contentH,
        float contentX, float contentY,
        BackgroundStyle style)
    {
        // Scale screen frame to fill entire output, then blur it
        float scaleX = w / contentW;
        float scaleY = h / contentH;
        float scale = Math.Max(scaleX, scaleY);

        using var scaleEffect = new Transform2DEffect
        {
            Source = screenFrame,
            TransformMatrix = Matrix3x2.CreateScale(scale)
        };

        using var blurEffect = new GaussianBlurEffect
        {
            Source = scaleEffect,
            BlurAmount = 30f,
            BorderMode = EffectBorderMode.Hard
        };

        session.DrawImage(blurEffect, new Vector2(0, 0));
    }

    private static void DrawShadow(
        CanvasDrawingSession session,
        float x, float y, float w, float h,
        float radius, BackgroundStyle style)
    {
        var device = session.Device;

        // Create a rounded rectangle mask as the shadow source
        using var geometry = CanvasGeometry.CreateRoundedRectangle(device, x, y, w, h, radius, radius);

        using var commandList = new CanvasCommandList(device);
        using (var maskSession = commandList.CreateDrawingSession())
        {
            var shadowBaseColor = ColorHelper.ParseColor(style.ShadowColor);
            var shadowColor = ColorHelper.WithOpacity(shadowBaseColor, style.ShadowOpacity);
            maskSession.FillGeometry(geometry, shadowColor);
        }

        using var shadowEffect = new ShadowEffect
        {
            Source = commandList,
            BlurAmount = style.ShadowBlur,
            ShadowColor = ColorHelper.WithOpacity(
                ColorHelper.ParseColor(style.ShadowColor), style.ShadowOpacity)
        };

        session.DrawImage(shadowEffect, new Vector2(style.ShadowOffsetX, style.ShadowOffsetY));
    }

    private static void DrawContent(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float x, float y, float w, float h,
        float radius)
    {
        var destRect = new Windows.Foundation.Rect(x, y, w, h);

        if (radius > 0)
        {
            using var clipGeometry = CanvasGeometry.CreateRoundedRectangle(
                session.Device, x, y, w, h, radius, radius);
            using var layer = session.CreateLayer(1.0f, clipGeometry);
            session.DrawImage(screenFrame, destRect);
        }
        else
        {
            session.DrawImage(screenFrame, destRect);
        }
    }

    private static void DrawBorder(
        CanvasDrawingSession session,
        float x, float y, float w, float h,
        float radius, BackgroundStyle style)
    {
        float halfBorder = style.BorderWidth / 2f;
        var borderColor = ColorHelper.ParseColor(style.BorderColor);

        // Inset the stroke so it aligns with the content edge
        session.DrawRoundedRectangle(
            x - halfBorder,
            y - halfBorder,
            w + style.BorderWidth,
            h + style.BorderWidth,
            radius,
            radius,
            borderColor,
            style.BorderWidth);
    }

    /// <summary>
    /// Fills the given rectangle with the background style — without any shadow,
    /// content, border, clipping, or padding. Used to fill letterbox/pillarbox bars
    /// inside the content buffer when the canvas is larger than the source frame
    /// (FitMode = Contain).
    /// </summary>
    /// <param name="session">Drawing session for the buffer being filled.</param>
    /// <param name="screenFrame">
    /// Optional screen frame used as the source for blur backgrounds. May be null
    /// when the style is not a blur background.
    /// </param>
    public void FillBackgroundRect(
        CanvasDrawingSession session,
        CanvasBitmap? screenFrame,
        float x, float y, float w, float h,
        BackgroundStyle style)
    {
        if (w <= 0f || h <= 0f) return;

        var previousTransform = session.Transform;
        session.Transform = Matrix3x2.CreateTranslation(x, y) * previousTransform;
        try
        {
            switch (style.Type)
            {
                case BackgroundType.SolidColor:
                    session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                    break;

                case BackgroundType.Gradient:
                    DrawGradientBackground(session, w, h, style);
                    break;

                case BackgroundType.Image:
                    DrawImageBackground(session, w, h, style);
                    break;

                case BackgroundType.Blur:
                    if (screenFrame is not null)
                        DrawBlurFill(session, screenFrame, w, h);
                    else
                        session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                    break;
            }
        }
        finally
        {
            session.Transform = previousTransform;
        }
    }

    private static void DrawBlurFill(
        CanvasDrawingSession session, CanvasBitmap screenFrame, float w, float h)
    {
        var src = screenFrame.SizeInPixels;
        float scaleX = w / src.Width;
        float scaleY = h / src.Height;
        float scale = Math.Max(scaleX, scaleY);

        using var scaleEffect = new Transform2DEffect
        {
            Source = screenFrame,
            TransformMatrix = Matrix3x2.CreateScale(scale)
        };
        using var blurEffect = new GaussianBlurEffect
        {
            Source = scaleEffect,
            BlurAmount = 30f,
            BorderMode = EffectBorderMode.Hard
        };
        session.DrawImage(blurEffect, new Vector2(0, 0));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cachedBackgroundImage?.Dispose();
            _cachedBackgroundImage = null;
            _cachedBackgroundPath = null;
            _cachedDevice = null;
            _disposed = true;
        }
    }
}
