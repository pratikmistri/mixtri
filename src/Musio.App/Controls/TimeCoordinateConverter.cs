using System.Linq;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio_App.Controls;

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
