using System.Linq;
using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri_App.Controls;

/// <summary>
/// Timeline time↔X coordinate math shared by <see cref="TimelineControl"/>'s track canvases
/// and gesture handlers. Extracted verbatim from <c>TimelineControl.TimeToX</c> /
/// <c>XToTime</c> / <c>SourceTimeToX</c> / <c>XToPrimarySourceTime</c> so future per-track
/// gesture handlers can share the same conversions without depending on
/// <see cref="TimelineControl"/> instance state. Every method takes the live
/// <see cref="TimelineModel"/> plus the two widths the original methods read
/// (<c>TimeRulerCanvas.ActualWidth</c>, falling back to the control's own
/// <c>ActualWidth</c>) as explicit parameters — the arithmetic is unchanged.
/// </summary>
internal static class TimeCoordinateConverter
{
    /// <summary>Horizontal inset so rounded clip edges aren't clipped at the canvas boundary.</summary>
    public const double TrackContentInset = 4;

    public static double TimeToX(TimelineModel? model, TimeSpan time, double rulerCanvasWidth, double controlWidth)
    {
        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return TrackContentInset;

        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double canvasWidth = rulerCanvasWidth;
        if (canvasWidth <= 0) canvasWidth = controlWidth;
        double pixelsPerSecond = ((canvasWidth - TrackContentInset * 2) / totalSeconds) * model.ZoomLevel;
        return TrackContentInset + (time.TotalSeconds - model.ScrollOffset) * pixelsPerSecond;
    }

    public static TimeSpan XToTime(TimelineModel? model, double x, double rulerCanvasWidth, double controlWidth)
    {
        if (model is null || model.DisplayDuration.TotalSeconds <= 0)
            return TimeSpan.Zero;

        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double canvasWidth = rulerCanvasWidth;
        if (canvasWidth <= 0) canvasWidth = controlWidth;
        double pixelsPerSecond = ((canvasWidth - TrackContentInset * 2) / totalSeconds) * model.ZoomLevel;
        if (pixelsPerSecond <= 0) return TimeSpan.Zero;

        double seconds = ((x - TrackContentInset) / pixelsPerSecond) + model.ScrollOffset;
        seconds = Math.Clamp(seconds, 0, totalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Seconds of output time the track viewport shows at the model's current zoom. Width
    /// independent: pixels-per-second is (usable width / duration) × zoom, so the usable width
    /// always spans exactly duration / zoom seconds.
    /// </summary>
    public static double VisibleSpanSeconds(TimelineModel? model, double? zoomLevel = null)
    {
        if (model is null) return 0;
        double totalSeconds = model.DisplayDuration.TotalSeconds;
        double zoom = zoomLevel ?? model.ZoomLevel;
        if (totalSeconds <= 0 || zoom <= 0) return 0;
        return totalSeconds / zoom;
    }

    /// <summary>
    /// Largest scroll offset (seconds) that still keeps the viewport inside
    /// <see cref="TimelineModel.DisplayDuration"/>. Zero at (or below) fit-to-width zoom.
    /// </summary>
    public static double MaxScrollOffset(TimelineModel? model, double? zoomLevel = null)
    {
        if (model is null) return 0;
        double totalSeconds = model.DisplayDuration.TotalSeconds;
        if (totalSeconds <= 0) return 0;
        return Math.Max(0, totalSeconds - VisibleSpanSeconds(model, zoomLevel));
    }

    /// <summary>Clamps a scroll offset into [0, <see cref="MaxScrollOffset"/>].</summary>
    public static double ClampScrollOffset(TimelineModel? model, double scrollOffset, double? zoomLevel = null)
    {
        if (double.IsNaN(scrollOffset) || double.IsInfinity(scrollOffset)) return 0;
        return Math.Clamp(scrollOffset, 0, MaxScrollOffset(model, zoomLevel));
    }

    /// <summary>
    /// The scroll offset that keeps the time currently under <paramref name="anchorX"/> at that
    /// same X once the zoom changes to <paramref name="newZoomLevel"/> (zoom-at-pointer). Not
    /// clamped — pass the result through <see cref="ClampScrollOffset"/> with the new zoom.
    /// </summary>
    public static double ScrollOffsetForZoomAnchor(
        TimelineModel model, double anchorX, double newZoomLevel, double rulerCanvasWidth, double controlWidth)
    {
        double totalSeconds = model.DisplayDuration.TotalSeconds;
        if (totalSeconds <= 0 || model.ZoomLevel <= 0 || newZoomLevel <= 0) return model.ScrollOffset;

        double canvasWidth = rulerCanvasWidth;
        if (canvasWidth <= 0) canvasWidth = controlWidth;
        double basePixelsPerSecond = (canvasWidth - TrackContentInset * 2) / totalSeconds;
        if (basePixelsPerSecond <= 0) return model.ScrollOffset;

        double oldPixelsPerSecond = basePixelsPerSecond * model.ZoomLevel;
        double newPixelsPerSecond = basePixelsPerSecond * newZoomLevel;
        double anchorOffset = anchorX - TrackContentInset;
        double anchorSeconds = anchorOffset / oldPixelsPerSecond + model.ScrollOffset;
        return anchorSeconds - anchorOffset / newPixelsPerSecond;
    }

    /// <summary>
    /// Converts a source-video time (zoom keyframe / cursor timestamp) to an X
    /// coordinate, mapping through segments so it stays aligned with the video
    /// after text slides shift later content.
    /// </summary>
    public static double SourceTimeToX(TimelineModel? model, TimeSpan sourceTime, double rulerCanvasWidth, double controlWidth)
    {
        if (model is null || model.Segments.Count == 0)
            return TimeToX(model, sourceTime, rulerCanvasWidth, controlWidth);
        return TimeToX(model, model.SourceToOutputTime(sourceTime), rulerCanvasWidth, controlWidth);
    }

    /// <summary>
    /// Converts an X coordinate to a source-video time in the PRIMARY recording's time
    /// domain, for camera-track gestures (<see cref="CameraSegment"/> ranges are always
    /// expressed in primary source time). When the X lands on a text slide or a
    /// non-primary video segment — where <see cref="TimelineModel.OutputToSourceTime"/>
    /// has no primary source time to return — this clamps to the nearest primary video
    /// segment boundary (by output-time distance) instead of mixing raw output time into
    /// the primary source-time domain. Returns <c>null</c> only when the timeline has no
    /// primary video segment at all to clamp against.
    /// </summary>
    public static TimeSpan? XToPrimarySourceTime(TimelineModel? model, double x, double rulerCanvasWidth, double controlWidth)
    {
        var outputTime = XToTime(model, x, rulerCanvasWidth, controlWidth);
        if (model is null || model.Segments.Count == 0)
            return outputTime;

        var mapped = model.OutputToSourceTime(outputTime);
        if (mapped is not null)
            return mapped.Value;

        var primarySegments = model.Segments.OfType<VideoSegment>()
            .Where(v => model.PrimaryVideoFilePath is null ||
                string.Equals(v.VideoFilePath, model.PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase));
        var (nearest, atStart) = TimelineModel.NearestVideoSegmentEdge(primarySegments, outputTime);
        if (nearest is null) return null;
        return atStart ? nearest.SourceStart : nearest.SourceStart + nearest.SourceDuration;
    }
}
