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
    /// Computes the total output dimensions including padding on all sides.
    /// </summary>
    public (int width, int height) CalculateOutputSize(int sourceWidth, int sourceHeight, BackgroundStyle style)
    {
        int totalPadding = style.Padding * 2;
        return (sourceWidth + totalPadding, sourceHeight + totalPadding);
    }

    /// <summary>
    /// Renders one complete composited frame: background → shadow → screen content → border.
    /// </summary>
    public void CompositeFrame(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        int outputWidth,
        int outputHeight,
        BackgroundStyle style)
    {
        float w = outputWidth;
        float h = outputHeight;
        float padding = style.Padding;
        float contentX = padding;
        float contentY = padding;
        float contentW = w - padding * 2;
        float contentH = h - padding * 2;
        float radius = style.CornerRadius;

        // 1. Draw background
        DrawBackground(session, screenFrame, w, h, contentW, contentH, contentX, contentY, style);

        // 2. Draw shadow behind content
        if (style.ShadowEnabled)
        {
            DrawShadow(session, contentX, contentY, contentW, contentH, radius, style);
        }

        // 3. Draw screen content (clipped to rounded rect if needed)
        DrawContent(session, screenFrame, contentX, contentY, contentW, contentH, radius);

        // 4. Draw border
        if (style.BorderEnabled && style.BorderWidth > 0)
        {
            DrawBorder(session, contentX, contentY, contentW, contentH, radius, style);
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
            _cachedBackgroundImage = CanvasBitmap.LoadAsync(session.Device, style.BackgroundImagePath).GetAwaiter().GetResult();
            _cachedBackgroundPath = style.BackgroundImagePath;
            _cachedDevice = session.Device;
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
            // Clip to rounded rectangle
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
