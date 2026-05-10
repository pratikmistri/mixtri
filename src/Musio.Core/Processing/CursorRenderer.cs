using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Musio.Core.Models;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace Musio.Core.Processing;

public enum CursorType { Default, System, Custom, Touch }

public record CursorStyle
{
    public CursorType Type { get; init; } = CursorType.Default;
    public string? CustomImagePath { get; init; }
    public float Scale { get; init; } = 3.0f;         // 1.0 - 6.0
    public string Color { get; init; } = "#FFFFFF";
    public bool TiltEnabled { get; init; } = true;
    public bool MotionBlurEnabled { get; init; } = false;
    public float MotionBlurStrength { get; init; } = 0.5f;
    public bool AutoHideEnabled { get; init; } = true;
    public float AutoHideDelaySeconds { get; init; } = 2.0f;
    public float AutoHideFadeDuration { get; init; } = 0.3f;
    public bool ClickAnimationEnabled { get; init; } = true;
}

/// <summary>
/// Renders a custom cursor onto video frames using Win2D, with click animations,
/// ripple highlights, motion blur, and auto-hide support.
/// </summary>
public class CursorRenderer : IDisposable
{
    private CursorStyle _style;
    private CanvasBitmap? _cursorBitmap;
    private CanvasGeometry? _defaultCursorGeometry;
    private bool _disposed;

    /// <summary>Recording start timestamp in ticks (from MouseRecordingData.StartTimestampTicks).</summary>
    public long StartTimestampTicks { get; set; }

    /// <summary>Tick frequency from the recording (from MouseRecordingData.TickFrequency).</summary>
    public double TickFrequency { get; set; } = 1.0;

    private const float ClickDownDurationSeconds = 0.04f;    // 40ms snap down
    private const float ClickUpDurationSeconds = 0.11f;     // 110ms bouncy spring back
    private const float ClickDownScale = 0.55f;             // scale to 55% on press
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
        // Touch cursor: render at click positions only (float up, tap, fade out)
        if (_style.Type == CursorType.Touch)
        {
            RenderTouchClicks(session, activeClicks, currentTimeSeconds);
            return;
        }

        float autoHideOpacity = GetAutoHideOpacity(currentTimeSeconds, lastMoveTimeSeconds);
        if (autoHideOpacity <= 0f) return;

        float x = (float)position.X;
        float y = (float)position.Y;

        float clickScale = _style.ClickAnimationEnabled
            ? GetClickScale(currentTimeSeconds, activeClicks, TickFrequency)
            : 1.0f;

        float finalScale = Math.Clamp(_style.Scale, 1.0f, 6.0f) * clickScale;

        // Motion blur ghosts (drawn behind cursor)
        if (_style.MotionBlurEnabled)
        {
            RenderMotionBlur(session, position, finalScale, autoHideOpacity);
        }

        // Compute velocity-based tilt for cinematic feel
        float tiltAngle = _style.TiltEnabled ? ComputeTiltAngle(position) : 0f;

        // Main cursor
        RenderCursor(session, x, y, finalScale, autoHideOpacity, tiltAngle);
    }

    // Touch animation durations
    private const float TouchPreRoll = 1.0f;             // start animating 1s before click
    private const float TouchTapDownDuration = 0.06f;    // scale down (tap press)
    private const float TouchTapUpDuration = 0.10f;      // scale back up (tap release)
    private const float TouchFadeOutDuration = 0.25f;    // fade away after tap
    private const float TouchTotalDuration = TouchPreRoll + TouchTapDownDuration + TouchTapUpDuration + TouchFadeOutDuration;
    private const float TouchFloatDistance = 40f;         // pixels to float up
    private const float TouchTapScale = 0.6f;            // scale down to 60% on tap

    /// <summary>
    /// Renders touch indicators at click-down positions. Consecutive clicks whose
    /// animations would overlap are merged into a single "chain" so that only one
    /// touch cursor is ever visible: it floats in, taps at each click position
    /// (sliding between them), then fades out after the last tap.
    /// </summary>
    private void RenderTouchClicks(
        CanvasDrawingSession session,
        List<ClickEvent> activeClicks,
        double currentTimeSeconds)
    {
        if (activeClicks is null || activeClicks.Count == 0 || TickFrequency <= 0)
            return;

        float baseScale = Math.Clamp(_style.Scale, 1.0f, 6.0f);

        // Collect click-down events with computed click times (already sorted by timestamp)
        var downClicks = new List<(ClickEvent Click, double ClickTime)>();
        foreach (var click in activeClicks)
        {
            if (!click.IsDown) continue;
            double clickTime = (click.TimestampTicks - StartTimestampTicks) / TickFrequency;
            downClicks.Add((click, clickTime));
        }

        if (downClicks.Count == 0) return;

        // Build chains of temporally overlapping clicks and render each chain.
        // Two consecutive clicks chain when their gap < TouchTotalDuration,
        // meaning the second click's independent animation would overlap the first's.
        int chainStart = 0;
        for (int i = 1; i <= downClicks.Count; i++)
        {
            bool endChain = i == downClicks.Count ||
                downClicks[i].ClickTime - downClicks[i - 1].ClickTime >= TouchTotalDuration;

            if (endChain)
            {
                RenderTouchChain(session, downClicks, chainStart, i, currentTimeSeconds, baseScale);
                chainStart = i;
            }
        }
    }

    /// <summary>
    /// Renders a single chained touch animation spanning clicks[startIdx..endIdx).
    /// Timeline: float-in → [tap → slide]* → tap → fade-out.
    /// </summary>
    private void RenderTouchChain(
        CanvasDrawingSession session,
        List<(ClickEvent Click, double ClickTime)> clicks,
        int startIdx, int endIdx,
        double currentTimeSeconds,
        float baseScale)
    {
        const double tapDuration = TouchTapDownDuration + TouchTapUpDuration;

        var first = clicks[startIdx];
        var last = clicks[endIdx - 1];

        double chainAnimStart = first.ClickTime - TouchPreRoll;
        double chainAnimEnd = last.ClickTime + tapDuration + TouchFadeOutDuration;

        if (currentTimeSeconds < chainAnimStart || currentTimeSeconds > chainAnimEnd)
            return;

        float x, y, opacity, scale;
        float yOffset = 0;

        if (currentTimeSeconds < first.ClickTime)
        {
            // Pre-roll: float up into first click position
            double elapsed = currentTimeSeconds - chainAnimStart;
            float t = (float)(elapsed / TouchPreRoll);
            float eased = CubicBezierEasing.EaseOut(t);

            x = first.Click.X;
            y = first.Click.Y;
            yOffset = TouchFloatDistance * baseScale * (1f - eased);
            opacity = eased;
            scale = baseScale * (0.7f + 0.3f * eased);
        }
        else if (currentTimeSeconds >= last.ClickTime + tapDuration)
        {
            // Fade out after last tap
            double fadeElapsed = currentTimeSeconds - (last.ClickTime + tapDuration);
            float t = (float)(fadeElapsed / TouchFadeOutDuration);
            float eased = CubicBezierEasing.EaseIn(t);

            x = last.Click.X;
            y = last.Click.Y;
            opacity = 1f - eased;
            scale = baseScale * (1f + 0.1f * eased);
        }
        else
        {
            // Tap / transition region — walk the chain to find the active segment
            x = first.Click.X;
            y = first.Click.Y;
            opacity = 1f;
            scale = baseScale;

            for (int i = startIdx; i < endIdx; i++)
            {
                var (click, clickTime) = clicks[i];
                double tapDownEnd = clickTime + TouchTapDownDuration;
                double tapEnd = clickTime + tapDuration;

                if (currentTimeSeconds < tapDownEnd)
                {
                    // Tap down
                    float t = (float)((currentTimeSeconds - clickTime) / TouchTapDownDuration);
                    float eased = CubicBezierEasing.EaseInOut(t);

                    x = click.X;
                    y = click.Y;
                    scale = baseScale * (1f - (1f - TouchTapScale) * eased);
                    break;
                }

                if (currentTimeSeconds < tapEnd)
                {
                    // Tap up — spring back
                    float t = (float)((currentTimeSeconds - tapDownEnd) / TouchTapUpDuration);
                    float eased = CubicBezierEasing.SpringOut(t);

                    x = click.X;
                    y = click.Y;
                    scale = baseScale * (TouchTapScale + (1f - TouchTapScale) * eased);
                    break;
                }

                if (i + 1 < endIdx)
                {
                    var nextClick = clicks[i + 1];
                    if (currentTimeSeconds < nextClick.ClickTime)
                    {
                        // Slide from this click to the next
                        double slideDuration = nextClick.ClickTime - tapEnd;
                        float t = slideDuration > 0
                            ? Math.Clamp((float)((currentTimeSeconds - tapEnd) / slideDuration), 0f, 1f)
                            : 1f;
                        float eased = CubicBezierEasing.EaseInOut(t);

                        x = click.X + (nextClick.Click.X - click.X) * eased;
                        y = click.Y + (nextClick.Click.Y - click.Y) * eased;
                        break;
                    }
                }
            }
        }

        DrawTouchCursor(session, x, y + yOffset, scale, opacity);
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

        // Button was released after the most recent press — bouncy spring back
        if (latestUpTime > latestDownTime)
        {
            double upElapsed = currentTime - latestUpTime;
            if (upElapsed < ClickUpDurationSeconds)
            {
                float t = Math.Clamp((float)(upElapsed / ClickUpDurationSeconds), 0f, 1f);
                // Spring overshoot: goes past 1.0 then settles
                float eased = CubicBezierEasing.SpringOut(t);
                float overshoot = 1.0f + 0.15f * MathF.Sin(t * MathF.PI); // slight bounce past 100%
                return ClickDownScale + (overshoot - ClickDownScale) * eased;
            }
            return 1.0f;
        }

        // Button is still held — quick snap down
        double downElapsed = currentTime - latestDownTime;
        if (downElapsed < ClickDownDurationSeconds)
        {
            float t = Math.Clamp((float)(downElapsed / ClickDownDurationSeconds), 0f, 1f);
            float eased = CubicBezierEasing.EaseInOut(t);
            return 1.0f - (1.0f - ClickDownScale) * eased;
        }

        return ClickDownScale; // fully pressed, held down
    }

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

    #region Velocity Tilt

    private const float MaxTiltRadians = 0.25f;          // ~14° max tilt
    private const float TiltVelocityThreshold = 50f;     // px/s before tilt starts
    private const float TiltVelocitySaturation = 800f;   // px/s at max tilt

    /// <summary>
    /// Computes a slight rotation angle based on horizontal velocity,
    /// tilting the cursor toward the direction of movement.
    /// </summary>
    private static float ComputeTiltAngle(SmoothedPosition position)
    {
        float vx = (float)position.VelocityX;
        float vy = (float)position.VelocityY;
        float speed = MathF.Sqrt(vx * vx + vy * vy);

        if (speed < TiltVelocityThreshold) return 0f;

        // Tilt based on horizontal velocity component (left/right lean)
        float tiltFactor = Math.Clamp(
            (speed - TiltVelocityThreshold) / (TiltVelocitySaturation - TiltVelocityThreshold),
            0f, 1f);

        // Direction: tilt toward movement using atan2 of velocity,
        // but scale down to a subtle lean rather than full rotation
        float angle = MathF.Atan2(vx, -vy); // angle relative to "up" direction
        // Dampen: only use a fraction of the angle, capped at max tilt
        return Math.Clamp(angle * tiltFactor * 0.3f, -MaxTiltRadians, MaxTiltRadians);
    }

    #endregion

    #region Cursor Drawing

    private void RenderCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity, float tiltAngle = 0f)
    {
        if (opacity <= 0f) return;

        if (_cursorBitmap != null && _style.Type == CursorType.Custom)
        {
            DrawBitmapCursor(session, x, y, scale, opacity);
        }
        else if (_style.Type == CursorType.Touch)
        {
            DrawTouchCursor(session, x, y, scale, opacity);
        }
        else
        {
            DrawDefaultCursor(session, x, y, scale, opacity, tiltAngle);
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
    /// Draws a translucent glass-like circle centered on the cursor position.
    /// Draws a translucent glass-like circle centered on the cursor position.
    /// Radial gradient is offset downward to create a crescent highlight,
    /// with a thin reversed gradient stroke (dark top, bright bottom).
    /// </summary>
    private void DrawTouchCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity)
    {
        if (opacity <= 0f) return;

        float radius = 12f * scale;
        var baseColor = ParseCursorColor(1f);

        // Radial gradient fill offset downward — bright spot below center creates a crescent
        float offsetY = radius * 0.45f;
        var fillStops = new CanvasGradientStop[]
        {
            new() { Position = 0.0f, Color = Color.FromArgb((byte)(opacity * 160), baseColor.R, baseColor.G, baseColor.B) },
            new() { Position = 0.5f, Color = Color.FromArgb((byte)(opacity * 90), baseColor.R, baseColor.G, baseColor.B) },
            new() { Position = 1.0f, Color = Color.FromArgb((byte)(opacity * 20), baseColor.R, baseColor.G, baseColor.B) },
        };
        using var fillBrush = new CanvasRadialGradientBrush(session, fillStops)
        {
            Center = new Vector2(x, y + offsetY),
            RadiusX = radius,
            RadiusY = radius,
        };
        session.FillCircle(x, y, radius, fillBrush);

        // Thin reversed gradient stroke: subtle dark top → bright highlight bottom
        byte darkA = (byte)(opacity * 50);
        byte brightA = (byte)(opacity * 180);
        var strokeStops = new CanvasGradientStop[]
        {
            new() { Position = 0.0f, Color = Color.FromArgb(darkA, baseColor.R, baseColor.G, baseColor.B) },
            new() { Position = 0.6f, Color = Color.FromArgb((byte)(opacity * 100), 255, 255, 255) },
            new() { Position = 1.0f, Color = Color.FromArgb(brightA, 255, 255, 255) },
        };
        using var strokeBrush = new CanvasLinearGradientBrush(session, strokeStops)
        {
            StartPoint = new Vector2(x, y - radius),
            EndPoint = new Vector2(x, y + radius),
        };
        session.DrawCircle(x, y, radius, strokeBrush, 1.0f * scale);
    }

    /// <summary>
    /// Draws a built-in default cursor as a colored arrow with contrasting outline (~32x32 logical pixels).
    /// Applies velocity-based tilt rotation for a cinematic feel.
    /// </summary>
    public void DrawDefaultCursor(CanvasDrawingSession session, float x, float y, float scale, float opacity, float tiltAngle = 0f)
    {
        if (opacity <= 0f) return;

        // Lazily create geometry if LoadCursorAsync wasn't called
        _defaultCursorGeometry ??= CreateDefaultCursorGeometry(session);

        var savedTransform = session.Transform;
        session.Transform =
            Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateRotation(tiltAngle)
            * Matrix3x2.CreateTranslation(x, y)
            * savedTransform;

        var fillColor = ParseCursorColor(opacity);
        var strokeColor = GetContrastOutlineColor(fillColor, opacity);

        session.FillGeometry(_defaultCursorGeometry, fillColor);
        session.DrawGeometry(_defaultCursorGeometry, strokeColor, 1.5f / scale);

        session.Transform = savedTransform;
    }

    /// <summary>Parses <see cref="CursorStyle.Color"/> hex string into a <see cref="Color"/> with the given opacity.</summary>
    private Color ParseCursorColor(float opacity)
    {
        byte a = (byte)(opacity * 255);
        string hex = (_style.Color ?? "#FFFFFF").TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(a, r, g, b);
        }
        return Color.FromArgb(a, 255, 255, 255); // fallback white
    }

    /// <summary>Returns a contrasting outline: black for light fills, white for dark fills.</summary>
    private static Color GetContrastOutlineColor(Color fill, float opacity)
    {
        // Perceived luminance (ITU-R BT.601)
        double lum = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B;
        byte a = (byte)(opacity * 255);
        return lum > 140
            ? Color.FromArgb(a, 30, 30, 30)     // dark outline for light fills
            : Color.FromArgb(a, 220, 220, 220);  // light outline for dark fills
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cursorBitmap?.Dispose();
        _cursorBitmap = null;
        _defaultCursorGeometry?.Dispose();
        _defaultCursorGeometry = null;
    }
}
