using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Musio.Core.Capture;
using System.Numerics;

namespace Musio.Core.Processing;

public enum KeyboardOverlayPosition { TopCenter, BottomCenter, TopLeft, BottomLeft }

public record KeyboardOverlayStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public float FontSize { get; init; } = 18f;
    public string TextColor { get; init; } = "#FFFFFF";
    public string BackgroundColor { get; init; } = "#CC000000";
    public float CornerRadius { get; init; } = 8f;
    public float Padding { get; init; } = 12f;
    public KeyboardOverlayPosition Position { get; init; } = KeyboardOverlayPosition.BottomCenter;
    public float FadeDurationSeconds { get; init; } = 0.5f;
    public float DisplayDurationSeconds { get; init; } = 2.0f;
    public bool ModifierCombosOnly { get; init; } = true;
}

/// <summary>
/// Renders keyboard shortcut overlays (e.g., "Ctrl + S") as pill/badge shapes
/// with fade-in/out animation on a Win2D drawing session.
/// </summary>
public class KeyboardOverlayRenderer
{
    private readonly KeyboardOverlayStyle _style;

    public KeyboardOverlayRenderer(KeyboardOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
    }

    /// <summary>
    /// Renders any active key combo at the given timestamp onto the canvas.
    /// </summary>
    public void RenderKeyOverlay(CanvasDrawingSession session, List<KeyPressEvent> events,
                                  double currentTimeSeconds, double tickFrequency,
                                  int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (events is null || events.Count == 0 || tickFrequency <= 0)
            return;

        // Find the most recent combo to display
        var combo = FindActiveCombo(events, currentTimeSeconds, tickFrequency);
        if (combo is null)
            return;

        float opacity = CalculateOpacity(combo.Value.elapsed, combo.Value.duration);
        if (opacity <= 0f)
            return;

        DrawPill(session, combo.Value.text, opacity, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// Scans events to find the most recent displayable key combo and its timing.
    /// </summary>
    private (string text, double elapsed, double duration)? FindActiveCombo(
        List<KeyPressEvent> events, double currentTimeSeconds, double tickFrequency)
    {
        // Walk events in reverse to find the most recent key-down event
        // that qualifies for display.
        long firstEventTick = events.Count > 0 ? events[0].TimestampTicks : 0;

        string? bestComboText = null;
        double bestComboTime = double.MinValue;

        for (int i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (!evt.IsDown) continue;

            double eventTime = (evt.TimestampTicks - firstEventTick) / tickFrequency;

            // Only consider events within the display window
            double elapsed = currentTimeSeconds - eventTime;
            double totalDuration = _style.DisplayDurationSeconds + _style.FadeDurationSeconds;
            if (elapsed < 0 || elapsed > totalDuration)
                continue;

            // Filter: skip non-modifier combos if ModifierCombosOnly
            if (_style.ModifierCombosOnly && !evt.IsCtrl && !evt.IsAlt && !evt.IsShift && !evt.IsWin)
                continue;

            // Skip if this key IS a modifier key itself (we want the non-modifier key in the combo)
            if (IsModifierKey(evt.VirtualKeyCode))
            {
                // Unless ONLY modifiers are pressed — still skip, we want a "real" combo
                continue;
            }

            if (eventTime > bestComboTime)
            {
                bestComboTime = eventTime;
                bestComboText = BuildComboString(evt);
            }

            // We found the most recent qualifying event; stop searching
            break;
        }

        if (bestComboText is null)
            return null;

        double bestElapsed = currentTimeSeconds - bestComboTime;
        double bestDuration = _style.DisplayDurationSeconds + _style.FadeDurationSeconds;
        return (bestComboText, bestElapsed, bestDuration);
    }

    private static bool IsModifierKey(int vk) => vk is
        0xA0 or 0xA1 or // LShift, RShift
        0xA2 or 0xA3 or // LCtrl, RCtrl
        0xA4 or 0xA5 or // LAlt, RAlt
        0x5B or 0x5C or // LWin, RWin
        0x10 or 0x11 or 0x12; // Shift, Ctrl, Alt (generic)

    private static string BuildComboString(KeyPressEvent evt)
    {
        var parts = new List<string>(4);

        if (evt.IsCtrl) parts.Add("Ctrl");
        if (evt.IsAlt) parts.Add("Alt");
        if (evt.IsShift) parts.Add("Shift");
        if (evt.IsWin) parts.Add("Win");

        parts.Add(evt.KeyName);

        return string.Join(" + ", parts);
    }

    private float CalculateOpacity(double elapsed, double totalDuration)
    {
        float fadeDuration = _style.FadeDurationSeconds;
        float displayDuration = _style.DisplayDurationSeconds;

        if (elapsed < 0)
            return 0f;

        // Fade in
        if (elapsed < fadeDuration)
            return (float)(elapsed / fadeDuration);

        // Fully visible
        if (elapsed < fadeDuration + displayDuration)
            return 1f;

        // Fade out
        double fadeOutElapsed = elapsed - fadeDuration - displayDuration;
        if (fadeOutElapsed < fadeDuration)
            return 1f - (float)(fadeOutElapsed / fadeDuration);

        return 0f;
    }

    private void DrawPill(CanvasDrawingSession session, string text, float opacity,
                          int canvasWidth, int canvasHeight)
    {
        var textColor = ColorHelper.ParseColor(_style.TextColor);
        textColor = Windows.UI.Color.FromArgb((byte)(opacity * textColor.A), textColor.R, textColor.G, textColor.B);

        var bgColor = ColorHelper.ParseColor(_style.BackgroundColor);
        bgColor = Windows.UI.Color.FromArgb((byte)(opacity * bgColor.A), bgColor.R, bgColor.G, bgColor.B);

        using var format = new CanvasTextFormat
        {
            FontFamily = _style.FontFamily,
            FontSize = _style.FontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // Measure text to determine pill size
        using var layout = new CanvasTextLayout(session, text, format, canvasWidth, canvasHeight);
        float textWidth = (float)layout.DrawBounds.Width;
        float textHeight = (float)layout.DrawBounds.Height;

        float pillWidth = textWidth + _style.Padding * 2;
        float pillHeight = textHeight + _style.Padding * 2;

        // Position the pill
        var (pillX, pillY) = CalculatePillPosition(canvasWidth, canvasHeight, pillWidth, pillHeight);

        float radius = _style.CornerRadius;

        // Draw background pill
        using var pillGeometry = CanvasGeometry.CreateRoundedRectangle(
            session.Device, pillX, pillY, pillWidth, pillHeight, radius, radius);
        session.FillGeometry(pillGeometry, bgColor);

        // Draw text centered within the pill
        session.DrawText(
            text,
            new System.Numerics.Vector2(pillX + pillWidth / 2f, pillY + pillHeight / 2f),
            textColor,
            new CanvasTextFormat
            {
                FontFamily = _style.FontFamily,
                FontSize = _style.FontSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            });
    }

    private (float x, float y) CalculatePillPosition(int canvasWidth, int canvasHeight,
                                                       float pillWidth, float pillHeight)
    {
        const float edgeMargin = 20f;

        return _style.Position switch
        {
            KeyboardOverlayPosition.TopCenter => (
                (canvasWidth - pillWidth) / 2f,
                edgeMargin),
            KeyboardOverlayPosition.BottomCenter => (
                (canvasWidth - pillWidth) / 2f,
                canvasHeight - pillHeight - edgeMargin),
            KeyboardOverlayPosition.TopLeft => (
                edgeMargin,
                edgeMargin),
            KeyboardOverlayPosition.BottomLeft => (
                edgeMargin,
                canvasHeight - pillHeight - edgeMargin),
            _ => (
                (canvasWidth - pillWidth) / 2f,
                canvasHeight - pillHeight - edgeMargin),
        };
    }
}
