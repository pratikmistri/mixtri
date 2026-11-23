using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Mixtri.Core.Capture;
using System.Numerics;

namespace Mixtri.Core.Processing;

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
public class KeyboardOverlayRenderer : IDisposable
{
    private readonly KeyboardOverlayStyle _style;

    // The style is immutable, so one format serves every frame for both measuring and
    // drawing. (Drawing used to construct a fresh, never-disposed CanvasTextFormat per
    // frame — a native-object leak for every frame a combo was on screen.)
    private CanvasTextFormat? _format;

    // Whether the event list last seen is in timestamp order, so the active combo can be
    // found by binary search. Re-checked only when a different list (or a changed count)
    // arrives; an out-of-order list falls back to the linear scan.
    private List<KeyPressEvent>? _orderCheckedEvents;
    private int _orderCheckedCount = -1;
    private bool _orderCheckedSorted;

    public KeyboardOverlayRenderer(KeyboardOverlayStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
    }

    /// <summary>
    /// Renders any active key combo at the given timestamp onto the canvas.
    /// <paramref name="baseTimestampTicks"/> is the tick value that corresponds to
    /// video time 0, aligning keyboard event timestamps with the video timeline.
    /// </summary>
    public void RenderKeyOverlay(CanvasDrawingSession session, List<KeyPressEvent> events,
                                  double currentTimeSeconds, double tickFrequency,
                                  int canvasWidth, int canvasHeight,
                                  long baseTimestampTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (events is null || events.Count == 0 || tickFrequency <= 0)
            return;

        // Find the most recent combo to display
        var combo = FindActiveCombo(events, currentTimeSeconds, tickFrequency, baseTimestampTicks);
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
        List<KeyPressEvent> events, double currentTimeSeconds, double tickFrequency,
        long baseTimestampTicks)
    {
        // Walk events in reverse to find the most recent key-down event
        // that qualifies for display.
        // Use the caller-provided base tick as t=0 so keyboard events align
        // with the video timeline. Falls back to first event if no base given.
        long originTick = baseTimestampTicks != 0
            ? baseTimestampTicks
            : (events.Count > 0 ? events[0].TimestampTicks : 0);

        string? bestComboText = null;
        double bestComboTime = double.MinValue;
        double totalDuration = _style.DisplayDurationSeconds + _style.FadeDurationSeconds;

        // In a time-ordered list, start at the last event not after the playhead and stop
        // at the first one older than the display window, instead of scanning the whole
        // recording from the end on every frame.
        bool sorted = IsTimeOrdered(events);
        int startIndex = sorted
            ? LastIndexAtOrBefore(events, currentTimeSeconds, originTick, tickFrequency)
            : events.Count - 1;

        for (int i = startIndex; i >= 0; i--)
        {
            var evt = events[i];
            double eventTime = (evt.TimestampTicks - originTick) / tickFrequency;

            // Only consider events within the display window
            double elapsed = currentTimeSeconds - eventTime;
            if (sorted && elapsed > totalDuration)
                break;

            if (!evt.IsDown) continue;
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

    private bool IsTimeOrdered(List<KeyPressEvent> events)
    {
        if (!ReferenceEquals(events, _orderCheckedEvents) || events.Count != _orderCheckedCount)
        {
            bool sorted = true;
            for (int i = 1; i < events.Count; i++)
            {
                if (events[i].TimestampTicks < events[i - 1].TimestampTicks)
                {
                    sorted = false;
                    break;
                }
            }

            _orderCheckedEvents = events;
            _orderCheckedCount = events.Count;
            _orderCheckedSorted = sorted;
        }

        return _orderCheckedSorted;
    }

    /// <summary>
    /// Index of the last event whose time is at or before <paramref name="currentTimeSeconds"/>
    /// (−1 when none), using the same time expression as the scan so the boundary agrees.
    /// </summary>
    private static int LastIndexAtOrBefore(
        List<KeyPressEvent> events, double currentTimeSeconds, long originTick, double tickFrequency)
    {
        int lo = 0, hi = events.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            double eventTime = (events[mid].TimestampTicks - originTick) / tickFrequency;
            if (currentTimeSeconds - eventTime >= 0)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
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

        var format = _format ??= new CanvasTextFormat
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
            format);
    }

    public void Dispose()
    {
        _format?.Dispose();
        _format = null;
        GC.SuppressFinalize(this);
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
