using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Musio.Core.Models;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace Musio.Core.Processing;

public enum CursorType { Default, System, Custom }

public record CursorStyle
{
    public CursorType Type { get; init; } = CursorType.Default;
    public string? CustomImagePath { get; init; }
    public float Scale { get; init; } = 1.0f;         // 0.5 - 3.0
    public bool MotionBlurEnabled { get; init; } = false;
    public float MotionBlurStrength { get; init; } = 0.5f;
    public bool AutoHideEnabled { get; init; } = true;
    public float AutoHideDelaySeconds { get; init; } = 2.0f;
    public float AutoHideFadeDuration { get; init; } = 0.3f;
    public bool ClickAnimationEnabled { get; init; } = true;
    public bool ClickHighlightEnabled { get; init; } = true;
    public string ClickHighlightColor { get; init; } = "#3B82F6"; // blue
    public float ClickHighlightRadius { get; init; } = 30f;
}

/// <summary>
/// Renders a custom cursor onto video frames using Win2D, with click animations,
/// ripple highlights, motion blur, and auto-hide support.
/// </summary>
public class CursorRenderer
{
    private CursorStyle _style;
    private CanvasBitmap? _cursorBitmap;
    private CanvasGeometry? _defaultCursorGeometry;

    /// <summary>Recording start timestamp in ticks (from MouseRecordingData.StartTimestampTicks).</summary>
    public long StartTimestampTicks { get; set; }

    /// <summary>Tick frequency from the recording (from MouseRecordingData.TickFrequency).</summary>
    public double TickFrequency { get; set; } = 1.0;

    private const float ClickDownDurationSeconds = 0.1f;   // 100ms press
    private const float ClickUpDurationSeconds = 0.2f;     // 200ms release
    private const float RippleDurationSeconds = 0.4f;      // 400ms ripple
    private const float RippleInitialOpacity = 0.6f;
    private const float RippleStrokeWidth = 2f;
    private const float MotionBlurVelocityThreshold = 200f; // px/s
    private const int MotionBlurGhostCount = 4;

    public CursorRenderer(CursorStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
    }

    /// <summary>
    /// Load cursor resources. Call once with a <see cref="CanvasDevice"/> before rendering.
    /// </summary>
    public async Task LoadCursorAsync(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (_style.Type == CursorType.Custom && !string.IsNullOrEmpty(_style.CustomImagePath))
        {
            _cursorBitmap = await CanvasBitmap.LoadAsync(device, _style.CustomImagePath);
        }

        // Always create the default geometry as fallback
        _defaultCursorGeometry = CreateDefaultCursorGeometry(device);
    }

    /// <summary>
    /// Render cursor at a specific frame with click animations and effects.
    /// </summary>
    public void RenderFrame(
        CanvasDrawingSession session,
        SmoothedPosition position,
        List<ClickEvent> activeClicks,
        double currentTimeSeconds,
        double lastMoveTimeSeconds)
    {
        float autoHideOpacity = GetAutoHideOpacity(currentTimeSeconds, lastMoveTimeSeconds);
        if (autoHideOpacity <= 0f) return;

        float x = (float)position.X;
        float y = (float)position.Y;

        float clickScale = _style.ClickAnimationEnabled
            ? GetClickScale(currentTimeSeconds, activeClicks, TickFrequency)
            : 1.0f;

        float finalScale = Math.Clamp(_style.Scale, 0.5f, 3.0f) * clickScale;

        // Click highlight ripples (drawn behind cursor)
        if (_style.ClickHighlightEnabled)
        {
            RenderRipples(session, activeClicks, currentTimeSeconds);
        }

        // Motion blur ghosts (drawn behind cursor)
        if (_style.MotionBlurEnabled)
        {
            RenderMotionBlur(session, position, finalScale, autoHideOpacity);
        }

        // Main cursor
        RenderCursor(session, x, y, finalScale, autoHideOpacity);
    }

    /// <summary>
    /// Evaluates the current cursor scale factor based on active click animations.
    /// On mouse-down: 1.0 → 0.8 over 100ms (EaseInOut).
    /// On mouse-up:   0.8 → 1.0 over 200ms (SpringOut).
    /// </summary>
    public float GetClickScale(double currentTime, List<ClickEvent> clicks, double tickFrequency)
    {
        if (clicks == null || clicks.Count == 0 || tickFrequency <= 0)
            return 1.0f;

        // Find the most recent down and up events before currentTime
        double latestDownTime = double.MinValue;
        double latestUpTime = double.MinValue;

        foreach (var click in clicks)
        {
            double clickTime = (click.TimestampTicks - StartTimestampTicks) / tickFrequency;
            if (clickTime > currentTime) continue;

            if (click.IsDown)
            {
                if (clickTime > latestDownTime)
                    latestDownTime = clickTime;
            }
            else
            {
                if (clickTime > latestUpTime)
                    latestUpTime = clickTime;
            }
        }

        if (latestDownTime == double.MinValue) return 1.0f;

        // Button was released after the most recent press — spring back
        if (latestUpTime > latestDownTime)
        {
            double upElapsed = currentTime - latestUpTime;
            if (upElapsed < ClickUpDurationSeconds)
            {
                float t = Math.Clamp((float)(upElapsed / ClickUpDurationSeconds), 0f, 1f);
                float eased = CubicBezierEasing.SpringOut(t);
                return 0.8f + 0.2f * eased;
            }
            return 1.0f; // animation complete
        }

        // Button is still held — press animation
        double downElapsed = currentTime - latestDownTime;
        if (downElapsed < ClickDownDurationSeconds)
        {
            float t = Math.Clamp((float)(downElapsed / ClickDownDurationSeconds), 0f, 1f);
            float eased = CubicBezierEasing.EaseInOut(t);
            return 1.0f - 0.2f * eased;
        }

        return 0.8f; // fully pressed, held down
    }

    #region Ripple Rendering

    private void RenderRipples(CanvasDrawingSession session, List<ClickEvent> clicks, double currentTimeSeconds)
    {
        if (clicks == null) return;

        Color highlightBase = ParseHexColor(_style.ClickHighlightColor);

        foreach (var click in clicks)
        {
            if (!click.IsDown) continue;

            double clickTime = (click.TimestampTicks - StartTimestampTicks) / TickFrequency;
            double elapsed = currentTimeSeconds - clickTime;

            if (elapsed < 0 || elapsed > RippleDurationSeconds) continue;

            float progress = (float)(elapsed / RippleDurationSeconds);
            float radius = _style.ClickHighlightRadius * progress;
            float opacity = RippleInitialOpacity * (1f - progress);

            var color = Color.FromArgb(
                (byte)(opacity * 255),
                highlightBase.R,
                highlightBase.G,
                highlightBase.B);

            session.DrawCircle(click.X, click.Y, radius, color, RippleStrokeWidth);
        }
    }

    #endregion

    #region Motion Blur

    private void RenderMotionBlur(
        CanvasDrawingSession session, SmoothedPosition position,
        float scale, float opacity)
    {
        float vx = (float)position.VelocityX;
        float vy = (float)position.VelocityY;
        float speed = MathF.Sqrt(vx * vx + vy * vy);

        if (speed < MotionBlurVelocityThreshold) return;

        // Direction of motion (normalized)
        float nx = vx / speed;
        float ny = vy / speed;

        // Trail distance scales with velocity and user strength setting
        float blurDistance = speed * _style.MotionBlurStrength * 0.02f;

        for (int i = MotionBlurGhostCount; i >= 1; i--)
        {
            float fraction = (float)i / MotionBlurGhostCount;
            float ghostOpacity = opacity * (1f - fraction) * 0.4f;

            // Offset along the negative velocity vector (trailing behind)
            float gx = (float)position.X - nx * blurDistance * fraction;
            float gy = (float)position.Y - ny * blurDistance * fraction;

            RenderCursor(session, gx, gy, scale, ghostOpacity);
        }
    }

    #endregion

    #region Auto-Hide

    private float GetAutoHideOpacity(double currentTime, double lastMoveTime)
    {
        if (!_style.AutoHideEnabled) return 1f;

        double idleTime = currentTime - lastMoveTime;
        if (idleTime <= _style.AutoHideDelaySeconds) return 1f;

        // Linear fade over AutoHideFadeDuration
        float fadeProgress = (float)((idleTime - _style.AutoHideDelaySeconds) / _style.AutoHideFadeDuration);
        return Math.Clamp(1f - fadeProgress, 0f, 1f);
    }

    #endregion

    #region Cursor Drawing

    private void RenderCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity)
    {
        if (opacity <= 0f) return;

        if (_cursorBitmap != null && _style.Type == CursorType.Custom)
        {
            DrawBitmapCursor(session, x, y, scale, opacity);
        }
        else
        {
            DrawDefaultCursor(session, x, y, scale, opacity);
        }
    }

    private void DrawBitmapCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity)
    {
        if (_cursorBitmap == null) return;

        float w = (float)_cursorBitmap.Size.Width * scale;
        float h = (float)_cursorBitmap.Size.Height * scale;

        using (session.CreateLayer(opacity))
        {
            session.DrawImage(_cursorBitmap, new Rect(x, y, w, h));
        }
    }

    /// <summary>
    /// Draws a built-in default cursor as a white arrow with black outline (~32x32 logical pixels).
    /// Uses <see cref="CanvasPathBuilder"/> to create the classic pointer arrow shape.
    /// </summary>
    public void DrawDefaultCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity)
    {
        if (opacity <= 0f) return;

        // Lazily create geometry if LoadCursorAsync wasn't called
        _defaultCursorGeometry ??= CreateDefaultCursorGeometry(session);

        var savedTransform = session.Transform;
        session.Transform =
            Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation(x, y)
            * savedTransform;

        var fillColor = Color.FromArgb((byte)(opacity * 255), 255, 255, 255);
        var strokeColor = Color.FromArgb((byte)(opacity * 255), 30, 30, 30);

        session.FillGeometry(_defaultCursorGeometry, fillColor);
        session.DrawGeometry(_defaultCursorGeometry, strokeColor, 1.5f / scale);

        session.Transform = savedTransform;
    }

    /// <summary>
    /// Creates the classic pointer arrow shape using <see cref="CanvasPathBuilder"/>.
    /// The shape is defined at the origin with the hotspot (tip) at (0, 0).
    /// </summary>
    private static CanvasGeometry CreateDefaultCursorGeometry(ICanvasResourceCreator creator)
    {
        using var builder = new CanvasPathBuilder(creator);

        // Classic arrow cursor (~15×27 pixels, tip at origin)
        builder.BeginFigure(0f, 0f);        // tip
        builder.AddLine(0f, 24f);           // left edge down
        builder.AddLine(5.5f, 18.5f);       // inner notch
        builder.AddLine(9.5f, 27f);         // handle bottom-left
        builder.AddLine(13f, 25.5f);        // handle bottom-right
        builder.AddLine(9f, 17f);           // inner notch right
        builder.AddLine(15f, 15.5f);        // right wing
        builder.EndFigure(CanvasFigureLoop.Closed);

        return CanvasGeometry.CreatePath(builder);
    }

    #endregion

    #region Helpers

    private static Color ParseHexColor(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan().TrimStart('#');
        if (span.Length < 6)
            return Color.FromArgb(255, 59, 130, 246); // fallback to default blue

        byte r = byte.Parse(span[..2], NumberStyles.HexNumber);
        byte g = byte.Parse(span[2..4], NumberStyles.HexNumber);
        byte b = byte.Parse(span[4..6], NumberStyles.HexNumber);
        return Color.FromArgb(255, r, g, b);
    }

    #endregion
}
