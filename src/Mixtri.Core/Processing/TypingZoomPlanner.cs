using Mixtri.Core.Capture;
using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Processing;

/// <summary>
/// Builds fixed-centre zoom shots around the recorded insertion-caret path for each typing
/// burst. Native controls use their caret and client bounds; when those are unavailable and
/// mouse data is provided, custom-rendered controls use a broad recent-click or pointer fallback.
/// </summary>
public static class TypingZoomPlanner
{
    public const double MaximumZoom = 1.75;
    public const double PointerFallbackMaximumZoom = 1.4;
    public const double MinimumUsefulZoom = 1.08;
    public const double TypingDriftScale = 0.35;
    public const double PointerFallbackDriftScale = 0.20;
    public const double ViewportSafetyFraction = 0.10;
    public const double PointerClickLookbackSeconds = 3.0;

    public static IReadOnlyList<ZoomKeyframe> Build(
        IReadOnlyList<KeyPressEvent> events,
        IReadOnlyList<TypingActivityRange> ranges,
        long recordingStartTicks,
        double tickFrequency,
        double recordingToVideoOffsetSeconds,
        int sourceWidth,
        int sourceHeight,
        int cropOffsetX,
        int cropOffsetY,
        string? sourceVideoFilePath,
        MouseRecordingData? mouseData = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(ranges);
        if (tickFrequency <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return [];

        var timedFocus = events
            .Where(e => e.IsDown && e.TextFocus.HasValue)
            .Select(e => new TimedFocus(
                TypingActivityDetector.MapTimestampToVideoTime(
                    e.TimestampTicks,
                    recordingStartTicks,
                    tickFrequency,
                    recordingToVideoOffsetSeconds),
                e.TextFocus!.Value))
            .Where(e => double.IsFinite(e.TimeSeconds))
            .OrderBy(e => e.TimeSeconds)
            .ToList();
        if (timedFocus.Count == 0 && mouseData is null)
            return [];

        var keyframes = new List<ZoomKeyframe>();
        foreach (var range in ranges)
        {
            var samples = timedFocus
                .Where(e => e.TimeSeconds >= range.Start.TotalSeconds
                    && e.TimeSeconds <= range.End.TotalSeconds)
                .ToList();

            var focusBounds = ResolveFocusBounds(
                samples, sourceWidth, sourceHeight, cropOffsetX, cropOffsetY);
            bool usesPointerFallback = false;
            if (focusBounds is null && mouseData is not null)
            {
                focusBounds = ResolvePointerFallback(
                    range,
                    mouseData,
                    recordingToVideoOffsetSeconds,
                    sourceWidth,
                    sourceHeight,
                    cropOffsetX,
                    cropOffsetY);
                usesPointerFallback = focusBounds is not null;
            }
            if (focusBounds is null) continue;

            var (zoom, centerX, centerY) = ResolveViewport(
                focusBounds.Value,
                sourceWidth,
                sourceHeight,
                usesPointerFallback ? PointerFallbackMaximumZoom : MaximumZoom);
            if (zoom < MinimumUsefulZoom) continue;

            keyframes.Add(ZoomKeyframe.FromRange(
                range.Start,
                range.End,
                zoom,
                centerX / sourceWidth,
                centerY / sourceHeight) with
            {
                SourceVideoFilePath = sourceVideoFilePath,
                HasAuthoredCenter = true,
                DriftScale = usesPointerFallback
                    ? PointerFallbackDriftScale
                    : TypingDriftScale,
            });
        }

        return keyframes;
    }

    private static FocusBounds? ResolveFocusBounds(
        IReadOnlyList<TimedFocus> samples,
        int sourceWidth,
        int sourceHeight,
        int cropOffsetX,
        int cropOffsetY)
    {
        double left = double.PositiveInfinity;
        double top = double.PositiveInfinity;
        double right = double.NegativeInfinity;
        double bottom = double.NegativeInfinity;
        bool foundCaret = false;

        foreach (var sample in samples)
        {
            double caretX = sample.Focus.CaretX - cropOffsetX;
            double caretY = sample.Focus.CaretY - cropOffsetY;
            if (caretX < 0 || caretX > sourceWidth || caretY < 0 || caretY > sourceHeight)
                continue;

            Include(caretX, caretY, caretX, caretY);
            foundCaret = true;

            double boundsLeft = Math.Clamp(sample.Focus.BoundsLeft - cropOffsetX, 0, sourceWidth);
            double boundsTop = Math.Clamp(sample.Focus.BoundsTop - cropOffsetY, 0, sourceHeight);
            double boundsRight = Math.Clamp(sample.Focus.BoundsRight - cropOffsetX, 0, sourceWidth);
            double boundsBottom = Math.Clamp(sample.Focus.BoundsBottom - cropOffsetY, 0, sourceHeight);
            double width = boundsRight - boundsLeft;
            double height = boundsBottom - boundsTop;

            // A caret HWND is sometimes the top-level application window. That is not an
            // input box and including it would collapse the planned zoom to effectively 1x.
            if (width >= 4 && height >= 4
                && width <= sourceWidth * 0.75
                && height <= sourceHeight * 0.55)
            {
                Include(boundsLeft, boundsTop, boundsRight, boundsBottom);
            }
        }

        return foundCaret ? new FocusBounds(left, top, right, bottom) : null;

        void Include(double x1, double y1, double x2, double y2)
        {
            left = Math.Min(left, x1);
            top = Math.Min(top, y1);
            right = Math.Max(right, x2);
            bottom = Math.Max(bottom, y2);
        }
    }

    private static FocusBounds? ResolvePointerFallback(
        TypingActivityRange range,
        MouseRecordingData mouseData,
        double mouseToVideoOffsetSeconds,
        int sourceWidth,
        int sourceHeight,
        int cropOffsetX,
        int cropOffsetY)
    {
        if (mouseData.TickFrequency <= 0) return null;

        double firstTypingTime = Math.Min(
            range.End.TotalSeconds,
            range.Start.TotalSeconds + TypingActivityDetector.LeadPaddingSeconds);
        var click = mouseData.Clicks
            .Where(c => c.IsDown && c.Button == MouseButton.Left)
            .Select(c => new
            {
                Click = c,
                Time = (c.TimestampTicks - mouseData.StartTimestampTicks)
                    / mouseData.TickFrequency - mouseToVideoOffsetSeconds,
            })
            .Where(c => c.Time <= firstTypingTime
                && c.Time >= range.Start.TotalSeconds - PointerClickLookbackSeconds)
            .OrderByDescending(c => c.Time)
            .FirstOrDefault();

        double x;
        double y;
        if (click is not null)
        {
            x = click.Click.X - cropOffsetX;
            y = click.Click.Y - cropOffsetY;
        }
        else
        {
            var sample = mouseData.FindSampleNearest(firstTypingTime + mouseToVideoOffsetSeconds);
            if (sample is null) return null;
            x = sample.Value.X - cropOffsetX;
            y = sample.Value.Y - cropOffsetY;
        }

        if (x < 0 || x > sourceWidth || y < 0 || y > sourceHeight)
            return null;

        // A wide, right-biased context gives left-to-right typing room to advance without
        // pretending we know the custom control's real bounds. The lower maximum zoom and
        // drift scale keep this fallback stable when the true caret is unavailable.
        return new FocusBounds(
            Math.Clamp(x - (sourceWidth * 0.10), 0, sourceWidth),
            Math.Clamp(y - (sourceHeight * 0.15), 0, sourceHeight),
            Math.Clamp(x + (sourceWidth * 0.35), 0, sourceWidth),
            Math.Clamp(y + (sourceHeight * 0.15), 0, sourceHeight));
    }

    internal static (double Zoom, double CenterX, double CenterY) ResolveViewport(
        FocusBounds focus,
        int sourceWidth,
        int sourceHeight,
        double maximumZoom = MaximumZoom)
    {
        maximumZoom = Math.Max(1.0, maximumZoom);
        double usableFraction = 1.0 - (2.0 * ViewportSafetyFraction);
        double focusWidth = Math.Max(1, focus.Right - focus.Left);
        double focusHeight = Math.Max(1, focus.Bottom - focus.Top);
        double viewportWidth = Math.Max(sourceWidth / maximumZoom, focusWidth / usableFraction);
        double viewportHeight = Math.Max(sourceHeight / maximumZoom, focusHeight / usableFraction);
        double zoom = Math.Min(
            maximumZoom,
            Math.Min(sourceWidth / viewportWidth, sourceHeight / viewportHeight));
        zoom = Math.Max(1.0, zoom);

        viewportWidth = sourceWidth / zoom;
        viewportHeight = sourceHeight / zoom;
        double desiredX = (focus.Left + focus.Right) / 2.0;
        double desiredY = (focus.Top + focus.Bottom) / 2.0;

        double centerX = ClampCenterWithSafety(
            desiredX, focus.Left, focus.Right, viewportWidth, sourceWidth);
        double centerY = ClampCenterWithSafety(
            desiredY, focus.Top, focus.Bottom, viewportHeight, sourceHeight);
        return (zoom, centerX, centerY);
    }

    private static double ClampCenterWithSafety(
        double desired,
        double focusStart,
        double focusEnd,
        double viewportSize,
        double sourceSize)
    {
        double sourceMin = viewportSize / 2.0;
        double sourceMax = sourceSize - sourceMin;
        double safety = viewportSize * ViewportSafetyFraction;
        double safeMin = Math.Max(sourceMin, focusEnd - (viewportSize / 2.0) + safety);
        double safeMax = Math.Min(sourceMax, focusStart + (viewportSize / 2.0) - safety);

        if (safeMin <= safeMax)
            return Math.Clamp(desired, safeMin, safeMax);

        double containMin = Math.Max(sourceMin, focusEnd - (viewportSize / 2.0));
        double containMax = Math.Min(sourceMax, focusStart + (viewportSize / 2.0));
        return containMin <= containMax
            ? Math.Clamp(desired, containMin, containMax)
            : Math.Clamp(desired, sourceMin, sourceMax);
    }

    private readonly record struct TimedFocus(double TimeSeconds, TextInputFocus Focus);

    internal readonly record struct FocusBounds(
        double Left,
        double Top,
        double Right,
        double Bottom);
}
