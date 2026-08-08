using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Core.Processing;

public record AutoZoomConfig
{
    public bool Enabled { get; init; } = true;
    public float DefaultZoomLevel { get; init; } = 2.0f;
    public float PreClickDuration { get; init; } = 1.0f;    // graceful anticipatory zoom-in
    public float HoldDuration { get; init; } = 1.333f;      // settled dwell on the focal point
    public float EaseOutDuration { get; init; } = 1.556f;   // slow, elegant release back to full frame
    public float SpringConstant { get; init; } = 200f;
    public float SpringDamping { get; init; } = 20f;
    public bool ZoomOnScroll { get; init; } = false;
    public float MinTimeBetweenZooms { get; init; } = 0.5f; // retained for config compatibility; chained paths now handle close clicks
}

public struct ZoomState
{
    public float ZoomLevel;
    public float CenterX;
    public float CenterY;
    public float ViewportX;
    public float ViewportY;
    public float ViewportWidth;
    public float ViewportHeight;
    /// <summary>True when the zoom center comes from a manual keyframe and should not be overridden.</summary>
    public bool IsManualOverride;

    /// <summary>True when a zoom segment (auto or manual) is active at this instant.</summary>
    public bool HasSegment;

    /// <summary>
    /// Normalized progress <c>[0,1]</c> through the active camera path piece.
    /// Camera drift is driven from this rather than from absolute time: a zoom shot
    /// lasts only a few seconds, so an absolute-time oscillator can sit on a
    /// stationary point for the entire hold and leave the camera visibly parked.
    /// </summary>
    public float SegmentProgress;

    /// <summary>
    /// Drift heading for the active zoom shot, as a vector so linked handoffs can
    /// interpolate headings component-wise. Averaging or interpolating raw angles
    /// breaks across the ±π wrap and can snap the living-camera drift mid-move.
    /// </summary>
    public float SegmentHeadingX;

    /// <summary>Y component of <see cref="SegmentHeadingX"/>'s heading vector.</summary>
    public float SegmentHeadingY;

    /// <summary>
    /// Multiplier for downstream camera-drift amplitude. The chained path lowers it
    /// during deliberate handoff moves so the added drift yields to the authored
    /// camera move, then restores it on settled holds.
    /// </summary>
    public float DriftScale;
}

public class AutoZoomEngine
{
    private AutoZoomConfig _config;
    private readonly List<ZoomKeyframe> _manualKeyframes = [];
    private List<ZoomSegment> _autoSegments = [];
    private ZoomCameraPath _manualPath = ZoomCameraPath.Empty;
    private ZoomCameraPath _autoPath = ZoomCameraPath.Empty;
    private int _sourceWidth;
    private int _sourceHeight;

    // Stored build parameters for rebuilding auto segments when suppressed set changes
    private MouseRecordingData? _lastMouseData;
    private double _lastTickFrequency;
    private float _lastCoordScaleX = 1f;
    private float _lastCoordScaleY = 1f;
    private float _lastCropOffsetX;
    private float _lastCropOffsetY;
    private double _lastTimeOffsetSeconds;
    private double _lastDurationSeconds;
    private HashSet<long> _suppressedClickTicks = [];

    private struct ZoomSegment
    {
        public double ZoomInStart;
        public double ZoomInEnd;
        public double HoldEnd;
        public double ZoomOutEnd;
        public float TargetZoom;
        public float CenterX;
        public float CenterY;
    }

    public AutoZoomEngine(AutoZoomConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Pre-compute zoom timeline from click events and manual keyframes.
    /// Mouse hook coordinates are already in physical pixels (PerMonitorV2),
    /// so <paramref name="coordScaleX"/> and <paramref name="coordScaleY"/> should
    /// normally be 1.0f — no DPI scaling is required.
    /// </summary>
    /// <param name="durationSeconds">
    /// Length of the video the clicks are being mapped onto. Clicks landing past it are
    /// ignored; pass 0 when the length is unknown to skip only the upper bound. Clicks
    /// before the video starts are always ignored. See <see cref="RebuildAutoSegments"/>.
    /// </param>
    public void BuildZoomTimeline(
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        double tickFrequency,
        float coordScaleX = 1.0f,
        float coordScaleY = 1.0f,
        double timeOffsetSeconds = 0,
        float cropOffsetX = 0,
        float cropOffsetY = 0,
        double durationSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(mouseData);
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;

        // Cache build parameters for rebuilding when suppressed set changes
        _lastMouseData = mouseData;
        _lastTickFrequency = tickFrequency;
        _lastCoordScaleX = coordScaleX;
        _lastCoordScaleY = coordScaleY;
        _lastCropOffsetX = cropOffsetX;
        _lastCropOffsetY = cropOffsetY;
        _lastTimeOffsetSeconds = timeOffsetSeconds;
        _lastDurationSeconds = durationSeconds;

        RebuildManualPath();
        RebuildAutoSegments();
    }

    /// <summary>
    /// Sets the source click ticks that should be suppressed (excluded) from
    /// auto-zoom segment generation. Triggers a rebuild of auto segments.
    /// </summary>
    public void SetSuppressedClickTicks(IReadOnlyCollection<long> suppressedTicks)
    {
        _suppressedClickTicks = suppressedTicks is null
            ? []
            : suppressedTicks is HashSet<long> hs
                ? new HashSet<long>(hs)
                : [.. suppressedTicks];
        if (_lastMouseData is not null)
            RebuildAutoSegments();
    }

    private void RebuildAutoSegments()
    {
        _autoSegments.Clear();

        if (!_config.Enabled || _lastMouseData is null)
        {
            RebuildAutoPath();
            return;
        }

        var clicks = _lastMouseData.Clicks
            .Where(c => c.IsDown && !_suppressedClickTicks.Contains(c.TimestampTicks))
            .OrderBy(c => c.TimestampTicks)
            .ToList();

        if (clicks.Count == 0)
        {
            RebuildAutoPath();
            return;
        }

        long startTick = _lastMouseData.StartTimestampTicks;

        var rawSegments = new List<ZoomSegment>();
        foreach (var click in clicks)
        {
            double clickTime = (click.TimestampTicks - startTick) / _lastTickFrequency - _lastTimeOffsetSeconds;

            // Ignore clicks that land outside the video, mirroring the editor's own
            // keyframe generation. A click recorded just before capture started still
            // produces a segment whose hold/ease-out reaches into the visible range, so
            // the preview zooms with nothing on the zoom track for the user to select —
            // and because no segment is shown, its tick can never be suppressed by
            // deleting it. Keeping both sides on the same rule avoids that shadow zoom.
            if (clickTime < 0) continue;
            if (_lastDurationSeconds > 0 && clickTime > _lastDurationSeconds) continue;

            rawSegments.Add(new ZoomSegment
            {
                ZoomInStart = clickTime - _config.PreClickDuration,
                ZoomInEnd = clickTime,
                HoldEnd = clickTime + _config.HoldDuration,
                ZoomOutEnd = clickTime + _config.HoldDuration + _config.EaseOutDuration,
                TargetZoom = _config.DefaultZoomLevel,
                CenterX = Math.Clamp(click.X * _lastCoordScaleX - _lastCropOffsetX, 0f, _sourceWidth - 1f),
                CenterY = Math.Clamp(click.Y * _lastCoordScaleY - _lastCropOffsetY, 0f, _sourceHeight - 1f),
            });
        }

        _autoSegments = rawSegments;
        RebuildAutoPath();
    }

    public void AddManualKeyframe(ZoomKeyframe keyframe)
    {
        _manualKeyframes.Add(keyframe);
        _manualKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        RebuildManualPath();
    }

    public void RemoveManualKeyframe(TimeSpan timestamp)
    {
        _manualKeyframes.RemoveAll(k => k.Timestamp == timestamp);
        RebuildManualPath();
    }

    /// <summary>
    /// Replaces all manual keyframes with the provided list.
    /// Auto-generated segments from <see cref="BuildZoomTimeline"/> are not affected.
    /// </summary>
    public void SetManualKeyframes(IReadOnlyList<ZoomKeyframe> keyframes)
    {
        _manualKeyframes.Clear();
        if (keyframes is { Count: > 0 })
        {
            _manualKeyframes.AddRange(keyframes);
            _manualKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        RebuildManualPath();
    }

    /// <summary>
    /// Builds a stable, process-independent seed from a segment's start time, so the
    /// same segment always drifts the same way in preview and in export.
    /// </summary>
    private static int SegmentSeedFromStart(double startSeconds)
        => (int)Math.Round(startSeconds * 1000.0);

    /// <summary>
    /// Get the zoom state at any timestamp. Manual keyframes are evaluated as their
    /// own precomputed camera path and override the auto-click path whenever active.
    /// </summary>
    public ZoomState GetZoomState(double timeSeconds)
    {
        if (_manualPath.TryEvaluate(timeSeconds, out var manualSample))
        {
            var state = ComputeViewport(manualSample);
            state.IsManualOverride = true;
            return state;
        }

        if (_autoPath.TryEvaluate(timeSeconds, out var autoSample))
            return ComputeViewport(autoSample);

        return ComputeViewportForCenter(1.0f, _sourceWidth / 2f, _sourceHeight / 2f);
    }

    private void RebuildManualPath()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0 || _manualKeyframes.Count == 0)
        {
            _manualPath = ZoomCameraPath.Empty;
            return;
        }

        _manualPath = ZoomCameraPath.Build(_manualKeyframes.Select(ToZoomShot), _sourceWidth, _sourceHeight);
    }

    private void RebuildAutoPath()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0 || _autoSegments.Count == 0)
        {
            _autoPath = ZoomCameraPath.Empty;
            return;
        }

        _autoPath = ZoomCameraPath.Build(_autoSegments.Select(ToZoomShot), _sourceWidth, _sourceHeight);
    }

    private ZoomShot ToZoomShot(ZoomKeyframe keyframe)
    {
        double rampStart = keyframe.Start.TotalSeconds;
        return new ZoomShot(
            rampStart,
            keyframe.Timestamp.TotalSeconds,
            (keyframe.Timestamp + keyframe.HoldDuration).TotalSeconds,
            keyframe.End.TotalSeconds,
            (float)keyframe.ZoomLevel,
            (float)(keyframe.CenterX * _sourceWidth),
            (float)(keyframe.CenterY * _sourceHeight),
            SegmentSeedFromStart(rampStart));
    }

    private static ZoomShot ToZoomShot(ZoomSegment segment)
        => new(
            segment.ZoomInStart,
            segment.ZoomInEnd,
            segment.HoldEnd,
            segment.ZoomOutEnd,
            segment.TargetZoom,
            segment.CenterX,
            segment.CenterY,
            SegmentSeedFromStart(segment.ZoomInStart));

    /// <summary>
    /// Step-based spring interpolation helper for real-time use.
    /// Exponentially decays <paramref name="current"/> toward <paramref name="target"/>.
    /// </summary>
    public static float SpringInterpolate(float current, float target, float springK, float damping, float dt)
    {
        float omega = MathF.Sqrt(springK);
        float zeta = damping / (2f * omega);
        float decay = MathF.Exp(-zeta * omega * dt);
        return target + (current - target) * decay;
    }

    /// <summary>
    /// Compute the visible viewport rectangle from zoom level and center, clamped to source bounds.
    /// </summary>
    private ZoomState ComputeViewport(ZoomCameraSample sample)
    {
        var state = ComputeViewportForCenter(sample.Zoom, sample.CenterX, sample.CenterY);
        state.HasSegment = true;
        state.SegmentProgress = sample.SegmentProgress;
        state.SegmentHeadingX = sample.HeadingX;
        state.SegmentHeadingY = sample.HeadingY;
        state.DriftScale = sample.DriftScale;
        return state;
    }

    /// <summary>
    /// Public API: compute viewport for a given zoom level and center point.
    /// Used by FrameCompositor to override zoom center with cursor position.
    /// </summary>
    public ZoomState ComputeViewportForCenter(float zoom, float cx, float cy)
    {
        zoom = Math.Max(zoom, 1.0f);
        float vpWidth = _sourceWidth / zoom;
        float vpHeight = _sourceHeight / zoom;

        float vpX = cx - vpWidth / 2f;
        float vpY = cy - vpHeight / 2f;

        // Clamp viewport to source bounds
        vpX = Math.Clamp(vpX, 0f, Math.Max(0f, _sourceWidth - vpWidth));
        vpY = Math.Clamp(vpY, 0f, Math.Max(0f, _sourceHeight - vpHeight));

        float actualCx = vpX + vpWidth / 2f;
        float actualCy = vpY + vpHeight / 2f;

        return new ZoomState
        {
            ZoomLevel = zoom,
            CenterX = actualCx,
            CenterY = actualCy,
            ViewportX = vpX,
            ViewportY = vpY,
            ViewportWidth = vpWidth,
            ViewportHeight = vpHeight,
            DriftScale = 1f,
        };
    }
}
