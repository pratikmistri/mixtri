using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Models;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Musio_App.Controls;

public sealed partial class TimelineControl : UserControl
{
    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(nameof(Model), typeof(TimelineModel), typeof(TimelineControl),
            new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty PlayheadPositionProperty =
        DependencyProperty.Register(nameof(PlayheadPosition), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnPlayheadPositionChanged));

    public TimelineModel? Model
    {
        get => (TimelineModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public TimeSpan PlayheadPosition
    {
        get => (TimeSpan)GetValue(PlayheadPositionProperty);
        set => SetValue(PlayheadPositionProperty, value);
    }

    private enum DragMode { None, Playhead, TrimStart, TrimEnd }
    private DragMode _dragMode = DragMode.None;

    // Colors
    private static readonly Color RulerBackground = Color.FromArgb(255, 40, 40, 40);
    private static readonly Color RulerTickColor = Color.FromArgb(255, 160, 160, 160);
    private static readonly Color RulerTextColor = Color.FromArgb(255, 200, 200, 200);
    private static readonly Color VideoTrackBackground = Color.FromArgb(255, 30, 30, 30);
    private static readonly Color VideoClipColor = Color.FromArgb(255, 60, 120, 200);
    private static readonly Color SpeedUpOverlayColor = Color.FromArgb(200, 230, 160, 50);
    private static readonly Color SlowDownOverlayColor = Color.FromArgb(200, 60, 130, 230);
    private static readonly Color TrimHandleColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color ZoomTrackBackground = Color.FromArgb(255, 35, 35, 35);
    private static readonly Color ZoomKeyframeColor = Color.FromArgb(255, 100, 200, 100);
    private static readonly Color ZoomCurveColor = Color.FromArgb(180, 100, 200, 100);
    private static readonly Color AudioTrackBackground = Color.FromArgb(255, 35, 35, 35);
    private static readonly Color AudioPlaceholderColor = Color.FromArgb(255, 80, 160, 80);
    private static readonly Color AudioWaveformColor = Color.FromArgb(220, 80, 180, 80);
    private static readonly Color AudioEnvelopeColor = Color.FromArgb(100, 120, 220, 120);
    private static readonly Color PlayheadColor = Color.FromArgb(255, 255, 50, 50);
    private static readonly Color CutLineColor = Color.FromArgb(200, 255, 255, 100);
    private static readonly Color CursorTrackBackground = Color.FromArgb(255, 35, 35, 35);
    private static readonly Color CursorPathXColor = Color.FromArgb(220, 100, 180, 255);
    private static readonly Color CursorPathYColor = Color.FromArgb(220, 255, 160, 100);
    private static readonly Color CursorClickColor = Color.FromArgb(255, 255, 80, 80);

    private const double TrimHandleWidth = 8;

    public TimelineControl()
    {
        InitializeComponent();
    }

    public void Refresh() => InvalidateAll();

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl control)
            control.InvalidateAll();
    }

    private static void OnPlayheadPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl control)
        {
            if (control.Model is { } model)
                model.PlayheadPosition = (TimeSpan)e.NewValue;
            control.UpdatePlayheadVisual();
            control.InvalidateAll();
        }
    }

    // --- Coordinate helpers ---

    private double TimeToX(TimeSpan time)
    {
        var model = Model;
        if (model is null || model.Duration.TotalSeconds <= 0)
            return 0;

        double totalSeconds = model.Duration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = (canvasWidth / totalSeconds) * model.ZoomLevel;
        return (time.TotalSeconds - model.ScrollOffset) * pixelsPerSecond;
    }

    private TimeSpan XToTime(double x)
    {
        var model = Model;
        if (model is null || model.Duration.TotalSeconds <= 0)
            return TimeSpan.Zero;

        double totalSeconds = model.Duration.TotalSeconds;
        double canvasWidth = TimeRulerCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = ActualWidth;
        double pixelsPerSecond = (canvasWidth / totalSeconds) * model.ZoomLevel;
        if (pixelsPerSecond <= 0) return TimeSpan.Zero;

        double seconds = (x / pixelsPerSecond) + model.ScrollOffset;
        seconds = Math.Clamp(seconds, 0, totalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void InvalidateAll()
    {
        TimeRulerCanvas?.Invalidate();
        VideoTrackCanvas?.Invalidate();
        CursorTrackCanvas?.Invalidate();
        ZoomTrackCanvas?.Invalidate();
        AudioTrackCanvas?.Invalidate();
        UpdatePlayheadVisual();
    }

    private void UpdatePlayheadVisual()
    {
        if (PlayheadLine is null) return;
        double x = TimeToX(PlayheadPosition);
        PlayheadLine.Margin = new Thickness(x, 0, 0, 0);
    }

    // --- Time Ruler ---

    private void TimeRulerCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(RulerBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        double totalSeconds = model.Duration.TotalSeconds;
        double pixelsPerSecond = (w / totalSeconds) * model.ZoomLevel;

        // Choose tick interval based on zoom
        double tickInterval = ChooseTickInterval(pixelsPerSecond);
        double minorInterval = tickInterval / 5;

        double startSec = Math.Max(0, model.ScrollOffset - tickInterval);
        double endSec = Math.Min(totalSeconds, model.ScrollOffset + w / pixelsPerSecond + tickInterval);

        // Minor ticks
        for (double t = Math.Floor(startSec / minorInterval) * minorInterval; t <= endSec; t += minorInterval)
        {
            float x = (float)((t - model.ScrollOffset) * pixelsPerSecond);
            if (x < 0 || x > w) continue;
            ds.DrawLine(x, h * 0.7f, x, h, RulerTickColor);
        }

        // Major ticks + labels
        for (double t = Math.Floor(startSec / tickInterval) * tickInterval; t <= endSec; t += tickInterval)
        {
            float x = (float)((t - model.ScrollOffset) * pixelsPerSecond);
            if (x < -50 || x > w + 50) continue;
            ds.DrawLine(x, h * 0.4f, x, h, RulerTickColor);

            string label = FormatTime(TimeSpan.FromSeconds(t));
            ds.DrawText(label, x + 3, 1, RulerTextColor,
                new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 11,
                    FontFamily = "Segoe UI"
                });
        }
    }

    private static double ChooseTickInterval(double pixelsPerSecond)
    {
        double[] intervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60];
        foreach (double interval in intervals)
        {
            if (interval * pixelsPerSecond >= 60)
                return interval;
        }
        return 60;
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return $"0:{t.Seconds:D2}.{t.Milliseconds / 100}";
    }

    // --- Video Track ---

    private void VideoTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(VideoTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        // Draw clips
        foreach (var clip in model.Clips)
        {
            float x1 = (float)TimeToX(clip.Start);
            float x2 = (float)TimeToX(clip.End);
            if (x2 < 0 || x1 > w) continue;
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, VideoClipColor);
        }

        // If no clips, draw the full duration as one clip
        if (model.Clips.Count == 0)
        {
            float x1 = (float)TimeToX(model.TrimStart);
            float x2 = (float)TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, VideoClipColor);
        }

        // Speed segments overlay (orange = sped up, blue = slowed down)
        foreach (var seg in model.SpeedSegments)
        {
            float x1 = (float)TimeToX(seg.Start);
            float x2 = (float)TimeToX(seg.End);
            if (x2 < 0 || x1 > w) continue;

            var segColor = seg.Speed > 1.0 ? SpeedUpOverlayColor : SlowDownOverlayColor;
            ds.FillRectangle(x1, 4, x2 - x1, h - 8, segColor);

            string speedLabel = $"{seg.Speed:0.##}x";
            ds.DrawText(speedLabel, x1 + 4, h / 2 - 7, Colors.White,
                new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 12,
                    FontFamily = "Segoe UI",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
        }

        // Trim handles
        DrawTrimHandle(ds, model.TrimStart, h);
        DrawTrimHandle(ds, model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration, h);

        // Cut points (gaps between clips)
        for (int i = 1; i < model.Clips.Count; i++)
        {
            float x = (float)TimeToX(model.Clips[i].Start);
            ds.DrawLine(x, 0, x, h, CutLineColor, 1.5f);
        }

        // Playhead on this track
        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    private void DrawTrimHandle(CanvasDrawingSession ds, TimeSpan time, float trackHeight)
    {
        float x = (float)TimeToX(time);
        ds.FillRectangle(x - (float)TrimHandleWidth / 2, 0, (float)TrimHandleWidth, trackHeight, TrimHandleColor);
        ds.DrawRectangle(x - (float)TrimHandleWidth / 2, 0, (float)TrimHandleWidth, trackHeight,
            Color.FromArgb(255, 100, 100, 100), 1);
    }

    // --- Zoom Track ---

    // --- Zoom Track (enhanced with intensity markers) ---

    private void ZoomTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(ZoomTrackBackground);

        if (model is null || model.ZoomKeyframes.Count == 0)
        {
            ds.DrawLine(0, h - 2, w, h - 2, ZoomCurveColor, 1);
            return;
        }

        // Baseline (1x zoom)
        ds.DrawLine(0, h - 2, w, h - 2, ZoomCurveColor, 0.5f);

        var sorted = model.ZoomKeyframes.OrderBy(k => k.Timestamp).ToList();
        if (sorted.Count > 1)
        {
            var pathBuilder = new CanvasPathBuilder(sender);
            float firstX = (float)TimeToX(sorted[0].Timestamp);
            float firstY = ZoomLevelToY(sorted[0].ZoomLevel, h);
            pathBuilder.BeginFigure(firstX, firstY);

            for (int i = 1; i < sorted.Count; i++)
            {
                float nx = (float)TimeToX(sorted[i].Timestamp);
                float ny = ZoomLevelToY(sorted[i].ZoomLevel, h);
                float cx = (firstX + nx) / 2;
                pathBuilder.AddCubicBezier(
                    new Vector2(cx, firstY),
                    new Vector2(cx, ny),
                    new Vector2(nx, ny));
                firstX = nx;
                firstY = ny;
            }

            pathBuilder.EndFigure(CanvasFigureLoop.Open);
            var geometry = CanvasGeometry.CreatePath(pathBuilder);
            ds.DrawGeometry(geometry, ZoomCurveColor, 2);
        }

        // Diamond markers with height proportional to zoom level
        foreach (var kf in sorted)
        {
            float cx = (float)TimeToX(kf.Timestamp);
            float cy = ZoomLevelToY(kf.ZoomLevel, h);

            // Size scales with zoom level: higher zoom = larger marker
            float size = (float)(4 + Math.Clamp((kf.ZoomLevel - 1.0) / 3.0, 0, 1) * 8);

            var pathBuilder = new CanvasPathBuilder(sender);
            pathBuilder.BeginFigure(cx, cy - size);
            pathBuilder.AddLine(cx + size, cy);
            pathBuilder.AddLine(cx, cy + size);
            pathBuilder.AddLine(cx - size, cy);
            pathBuilder.EndFigure(CanvasFigureLoop.Closed);

            var diamond = CanvasGeometry.CreatePath(pathBuilder);

            // Fill intensity also scales with zoom level
            byte alpha = (byte)(150 + Math.Clamp((kf.ZoomLevel - 1.0) / 3.0, 0, 1) * 105);
            var fillColor = Color.FromArgb(alpha, ZoomKeyframeColor.R, ZoomKeyframeColor.G, ZoomKeyframeColor.B);
            ds.FillGeometry(diamond, fillColor);
            ds.DrawGeometry(diamond, Colors.White, 1);
        }

        // Playhead
        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    private static float ZoomLevelToY(double zoomLevel, float trackHeight)
    {
        // Map zoom 1.0 → bottom, zoom 4.0 → top
        double normalized = Math.Clamp((zoomLevel - 1.0) / 3.0, 0, 1);
        return (float)(trackHeight - 4 - normalized * (trackHeight - 8));
    }

    // --- Audio Track ---

    // --- Audio Track (real waveform rendering) ---

    private void AudioTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(AudioTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
            return;

        float x1 = (float)TimeToX(model.TrimStart);
        float x2 = (float)TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
        float centerY = h / 2f;

        var waveform = model.AudioWaveformSamples;
        if (waveform is not null && waveform.Length > 0)
        {
            float trackWidth = x2 - x1;
            if (trackWidth <= 0) trackWidth = w;
            float barWidth = Math.Max(1f, trackWidth / waveform.Length);

            // Draw filled waveform bars (mirrored around center)
            for (int i = 0; i < waveform.Length; i++)
            {
                float bx = x1 + (i * trackWidth / waveform.Length);
                if (bx > w || bx + barWidth < 0) continue;

                float amplitude = Math.Clamp(waveform[i], 0f, 1f);
                float barHeight = amplitude * (h * 0.45f);

                ds.FillRectangle(bx, centerY - barHeight, barWidth, barHeight * 2, AudioWaveformColor);
            }

            // Volume envelope line
            if (waveform.Length > 1)
            {
                var envBuilder = new CanvasPathBuilder(sender);
                float startX = x1;
                float startY = centerY - waveform[0] * (h * 0.45f);
                envBuilder.BeginFigure(startX, startY);

                for (int i = 1; i < waveform.Length; i++)
                {
                    float ex = x1 + (i * trackWidth / waveform.Length);
                    float ey = centerY - Math.Clamp(waveform[i], 0f, 1f) * (h * 0.45f);
                    envBuilder.AddLine(ex, ey);
                }

                envBuilder.EndFigure(CanvasFigureLoop.Open);
                var envGeometry = CanvasGeometry.CreatePath(envBuilder);
                ds.DrawGeometry(envGeometry, AudioEnvelopeColor, 1.5f);
            }
        }
        else
        {
            // Fallback: placeholder bar when no waveform data
            ds.FillRectangle(x1, h * 0.3f, x2 - x1, h * 0.4f, AudioPlaceholderColor);
        }

        // Center line
        ds.DrawLine(x1, centerY, x2, centerY, Color.FromArgb(100, 255, 255, 255), 0.5f);

        // Playhead
        float px = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(px, 0, px, h, PlayheadColor, 2);
    }

    // --- Cursor Path Track ---

    private void CursorTrackCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var model = Model;
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;

        ds.Clear(CursorTrackBackground);

        if (model is null || model.Duration.TotalSeconds <= 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, Color.FromArgb(60, 255, 255, 255), 0.5f);
            float ppx = (float)TimeToX(PlayheadPosition);
            ds.DrawLine(ppx, 0, ppx, h, PlayheadColor, 2);
            return;
        }

        var cursorData = model.CursorData;
        if (cursorData is null || cursorData.Samples.Count == 0)
        {
            ds.DrawLine(0, h / 2, w, h / 2, Color.FromArgb(60, 255, 255, 255), 0.5f);
            float ppx = (float)TimeToX(PlayheadPosition);
            ds.DrawLine(ppx, 0, ppx, h, PlayheadColor, 2);
            return;
        }

        // Determine cursor coordinate ranges for normalization
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var sample in cursorData.Samples)
        {
            if (sample.X < minX) minX = sample.X;
            if (sample.X > maxX) maxX = sample.X;
            if (sample.Y < minY) minY = sample.Y;
            if (sample.Y > maxY) maxY = sample.Y;
        }

        int rangeX = Math.Max(1, maxX - minX);
        int rangeY = Math.Max(1, maxY - minY);
        float margin = 4f;
        float drawHeight = h - margin * 2;
        double tickFreq = cursorData.TickFrequency > 0 ? cursorData.TickFrequency : 1.0;
        long startTicks = cursorData.StartTimestampTicks;
        double mouseOffset = model.MouseToVideoOffsetSeconds;

        // Draw X-position path (blue) and Y-position path (orange)
        if (cursorData.Samples.Count > 1)
        {
            var xPathBuilder = new CanvasPathBuilder(sender);
            var yPathBuilder = new CanvasPathBuilder(sender);
            bool xStarted = false, yStarted = false;

            foreach (var sample in cursorData.Samples)
            {
                double timeSec = (sample.TimestampTicks - startTicks) / tickFreq - mouseOffset;
                float px = (float)TimeToX(TimeSpan.FromSeconds(timeSec));
                if (px < -1 || px > w + 1) continue;

                float normX = (float)(sample.X - minX) / rangeX;
                float normY = (float)(sample.Y - minY) / rangeY;
                float yPosX = margin + (1f - normX) * drawHeight;
                float yPosY = margin + normY * drawHeight;

                if (!xStarted) { xPathBuilder.BeginFigure(px, yPosX); xStarted = true; }
                else xPathBuilder.AddLine(px, yPosX);

                if (!yStarted) { yPathBuilder.BeginFigure(px, yPosY); yStarted = true; }
                else yPathBuilder.AddLine(px, yPosY);
            }

            if (xStarted)
            {
                xPathBuilder.EndFigure(CanvasFigureLoop.Open);
                ds.DrawGeometry(CanvasGeometry.CreatePath(xPathBuilder), CursorPathXColor, 1.2f);
            }
            if (yStarted)
            {
                yPathBuilder.EndFigure(CanvasFigureLoop.Open);
                ds.DrawGeometry(CanvasGeometry.CreatePath(yPathBuilder), CursorPathYColor, 1.2f);
            }
        }

        // Draw click events as dots
        foreach (var click in cursorData.Clicks)
        {
            if (!click.IsDown) continue;
            double timeSec = (click.TimestampTicks - startTicks) / tickFreq - mouseOffset;
            float cx = (float)TimeToX(TimeSpan.FromSeconds(timeSec));
            if (cx < -4 || cx > w + 4) continue;

            float normY = (float)(click.Y - minY) / rangeY;
            float cy = margin + normY * drawHeight;
            ds.FillCircle(cx, cy, 3f, CursorClickColor);
            ds.DrawCircle(cx, cy, 3f, Colors.White, 0.8f);
        }

        // Playhead
        float playX = (float)TimeToX(PlayheadPosition);
        ds.DrawLine(playX, 0, playX, h, PlayheadColor, 2);
    }

    // --- Interaction ---

    private void Track_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Click on ruler, zoom, or audio track → move playhead
        if (sender is CanvasControl canvas)
        {
            var pos = e.GetCurrentPoint(canvas).Position;
            PlayheadPosition = XToTime(pos.X);
            _dragMode = DragMode.Playhead;
            canvas.CapturePointer(e.Pointer);
        }
    }

    private void VideoTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null || sender is not CanvasControl canvas) return;

        var pos = e.GetCurrentPoint(canvas).Position;

        // Check trim handles first
        double trimStartX = TimeToX(model.TrimStart);
        double trimEndX = TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);

        if (Math.Abs(pos.X - trimStartX) <= TrimHandleWidth)
        {
            _dragMode = DragMode.TrimStart;
            canvas.CapturePointer(e.Pointer);
            return;
        }

        if (Math.Abs(pos.X - trimEndX) <= TrimHandleWidth)
        {
            _dragMode = DragMode.TrimEnd;
            canvas.CapturePointer(e.Pointer);
            return;
        }

        // Otherwise, move playhead
        PlayheadPosition = XToTime(pos.X);
        _dragMode = DragMode.Playhead;
        canvas.CapturePointer(e.Pointer);
    }

    private void VideoTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null || sender is not CanvasControl canvas) return;

        var pos = e.GetCurrentPoint(canvas).Position;

        // Update cursor for trim handles
        if (_dragMode == DragMode.None)
        {
            double trimStartX = TimeToX(model.TrimStart);
            double trimEndX = TimeToX(model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration);
            bool nearHandle = Math.Abs(pos.X - trimStartX) <= TrimHandleWidth ||
                              Math.Abs(pos.X - trimEndX) <= TrimHandleWidth;
            SetCursor(nearHandle
                ? InputSystemCursorShape.SizeWestEast
                : InputSystemCursorShape.Arrow);
        }

        switch (_dragMode)
        {
            case DragMode.Playhead:
                PlayheadPosition = XToTime(pos.X);
                break;

            case DragMode.TrimStart:
                var newStart = XToTime(pos.X);
                var maxStart = (model.TrimEnd > TimeSpan.Zero ? model.TrimEnd : model.Duration)
                    - TimeSpan.FromMilliseconds(100);
                if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
                if (newStart > maxStart) newStart = maxStart;
                model.TrimStart = newStart;
                InvalidateAll();
                break;

            case DragMode.TrimEnd:
                var newEnd = XToTime(pos.X);
                if (newEnd < model.TrimStart + TimeSpan.FromMilliseconds(100))
                    newEnd = model.TrimStart + TimeSpan.FromMilliseconds(100);
                if (newEnd > model.Duration) newEnd = model.Duration;
                model.TrimEnd = newEnd;
                InvalidateAll();
                break;
        }
    }

    private void VideoTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragMode = DragMode.None;
        if (sender is CanvasControl canvas)
            canvas.ReleasePointerCapture(e.Pointer);
    }

    private void Grid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var model = Model;
        if (model is null) return;

        var props = e.GetCurrentPoint(this).Properties;
        int delta = props.MouseWheelDelta;

        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            // Ctrl+Scroll → zoom
            double factor = delta > 0 ? 1.15 : 1.0 / 1.15;
            model.ZoomLevel = Math.Clamp(model.ZoomLevel * factor, 0.1, 50.0);
        }
        else
        {
            // Scroll → horizontal scroll
            double scrollDelta = -delta / 120.0 * 0.5; // 0.5 seconds per notch
            model.ScrollOffset = Math.Clamp(
                model.ScrollOffset + scrollDelta,
                0,
                Math.Max(0, model.Duration.TotalSeconds - 1));
        }

        InvalidateAll();
        e.Handled = true;
    }

    private void SetCursor(InputSystemCursorShape shape)
    {
        if (ProtectedCursor is InputSystemCursor current && current.CursorShape == shape)
            return;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
