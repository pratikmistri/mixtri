using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Core.Processing;

public record AutoZoomConfig
{
    public bool Enabled { get; init; } = true;
    public float DefaultZoomLevel { get; init; } = 2.0f;
    public float PreClickDuration { get; init; } = 0.345f;  // 300ms + 15%
    public float HoldDuration { get; init; } = 0.575f;      // 500ms + 15%
    public float EaseOutDuration { get; init; } = 0.575f;    // 500ms + 15%
    public float SpringConstant { get; init; } = 200f;
    public float SpringDamping { get; init; } = 20f;
    public bool ZoomOnScroll { get; init; } = false;
    public float MinTimeBetweenZooms { get; init; } = 0.5f;
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
    /// <summary>3D rotation around the vertical (Y) axis in degrees.</summary>
    public float RotationY;
    /// <summary>3D rotation around the horizontal (X) axis in degrees.</summary>
    public float RotationX;
}

public class AutoZoomEngine
{
    private AutoZoomConfig _config;
    private readonly List<ZoomKeyframe> _manualKeyframes = [];
    private List<ZoomSegment> _autoSegments = [];
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
    public void BuildZoomTimeline(
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        double tickFrequency,
        float coordScaleX = 1.0f,
        float coordScaleY = 1.0f,
        double timeOffsetSeconds = 0,
        float cropOffsetX = 0,
        float cropOffsetY = 0)
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

        if (!_config.Enabled || _lastMouseData is null) return;

        var clicks = _lastMouseData.Clicks
            .Where(c => c.IsDown && !_suppressedClickTicks.Contains(c.TimestampTicks))
            .OrderBy(c => c.TimestampTicks)
            .ToList();

        if (clicks.Count == 0) return;

        long startTick = _lastMouseData.StartTimestampTicks;

        var rawSegments = new List<ZoomSegment>();
        foreach (var click in clicks)
        {
            double clickTime = (click.TimestampTicks - startTick) / _lastTickFrequency - _lastTimeOffsetSeconds;

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

        _autoSegments = MergeSegments(rawSegments);
    }

    /// <summary>
    /// Merge overlapping or closely-spaced zoom segments to prevent flickering.
    /// </summary>
    private List<ZoomSegment> MergeSegments(List<ZoomSegment> segments)
    {
        if (segments.Count == 0) return [];

        var merged = new List<ZoomSegment>();
        var current = segments[0];

        for (int i = 1; i < segments.Count; i++)
        {
            var next = segments[i];

            bool overlaps = next.ZoomInStart <= current.ZoomOutEnd;
            bool tooClose = (next.ZoomInStart - current.ZoomOutEnd) < _config.MinTimeBetweenZooms;

            if (overlaps || tooClose)
            {
                // Extend hold to cover the next click, track latest center
                current.HoldEnd = Math.Max(current.HoldEnd, next.HoldEnd);
                current.ZoomOutEnd = current.HoldEnd + _config.EaseOutDuration;
                current.CenterX = next.CenterX;
                current.CenterY = next.CenterY;
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        return merged;
    }

    public void AddManualKeyframe(ZoomKeyframe keyframe)
    {
        _manualKeyframes.Add(keyframe);
        _manualKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public void RemoveManualKeyframe(TimeSpan timestamp)
    {
        _manualKeyframes.RemoveAll(k => k.Timestamp == timestamp);
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
    }

    /// <summary>
    /// Get the zoom state at any timestamp. Manual keyframes override auto-zoom.
    /// </summary>
    public ZoomState GetZoomState(double timeSeconds)
    {
        // Manual keyframes take priority over auto-generated segments
        var manualResult = EvaluateManualKeyframes(timeSeconds);
        if (manualResult.HasValue)
        {
            var state = ComputeViewport(manualResult.Value);
            state.IsManualOverride = true;
            return state;
        }

        var autoResult = EvaluateAutoSegments(timeSeconds);
        return ComputeViewport(autoResult);
    }

    private (float zoom, float cx, float cy, float rotY, float rotX)? EvaluateManualKeyframes(double timeSeconds)
    {
        // When multiple keyframes overlap (e.g. A zooming out while B zooms in),
        // pick the one producing the highest zoom level for a seamless crossover.
        (float zoom, float cx, float cy, float rotY, float rotX)? best = null;

        foreach (var kf in _manualKeyframes)
        {
            double kfTime = kf.Timestamp.TotalSeconds;
            double preStart = kfTime - kf.PreDuration.TotalSeconds;
            double holdEnd = kfTime + kf.HoldDuration.TotalSeconds;
            double postEnd = holdEnd + kf.PostDuration.TotalSeconds;

            if (timeSeconds < preStart || timeSeconds > postEnd)
                continue;

            float zoom;
            float rotY, rotX;
            if (timeSeconds < kfTime)
            {
                double progress = (kfTime - preStart) > 0
                    ? (timeSeconds - preStart) / (kfTime - preStart)
                    : 1.0;
                float p = (float)progress;
                zoom = CubicBezierEase(1.0f, (float)kf.ZoomLevel, p);
                rotY = CubicBezierEase(0f, kf.RotationY, p);
                rotX = CubicBezierEase(0f, kf.RotationX, p);
            }
            else if (timeSeconds <= holdEnd)
            {
                zoom = (float)kf.ZoomLevel;
                rotY = kf.RotationY;
                rotX = kf.RotationX;
            }
            else
            {
                double progress = (postEnd - holdEnd) > 0
                    ? (timeSeconds - holdEnd) / (postEnd - holdEnd)
                    : 1.0;
                float p = (float)progress;
                zoom = CubicBezierEase((float)kf.ZoomLevel, 1.0f, p);
                rotY = CubicBezierEase(kf.RotationY, 0f, p);
                rotX = CubicBezierEase(kf.RotationX, 0f, p);
            }

            float cx = (float)(kf.CenterX * _sourceWidth);
            float cy = (float)(kf.CenterY * _sourceHeight);

            if (!best.HasValue || zoom > best.Value.zoom)
                best = (zoom, cx, cy, rotY, rotX);
        }

        return best;
    }

    private (float zoom, float cx, float cy, float rotY, float rotX) EvaluateAutoSegments(double timeSeconds)
    {
        // When segments overlap (edge cases after merge), pick the highest zoom
        // for a seamless transition — same strategy as manual keyframes.
        float bestZoom = 1.0f;
        float bestCx = _sourceWidth / 2f;
        float bestCy = _sourceHeight / 2f;

        foreach (var seg in _autoSegments)
        {
            if (timeSeconds < seg.ZoomInStart || timeSeconds > seg.ZoomOutEnd)
                continue;

            float zoom;
            if (timeSeconds < seg.ZoomInEnd)
            {
                double duration = seg.ZoomInEnd - seg.ZoomInStart;
                double progress = duration > 0 ? (timeSeconds - seg.ZoomInStart) / duration : 1.0;
                zoom = CubicBezierEase(1.0f, seg.TargetZoom, (float)progress);
            }
            else if (timeSeconds <= seg.HoldEnd)
            {
                zoom = seg.TargetZoom;
            }
            else
            {
                double duration = seg.ZoomOutEnd - seg.HoldEnd;
                double progress = duration > 0 ? (timeSeconds - seg.HoldEnd) / duration : 1.0;
                zoom = CubicBezierEase(seg.TargetZoom, 1.0f, (float)progress);
            }

            if (zoom > bestZoom)
            {
                bestZoom = zoom;
                bestCx = seg.CenterX;
                bestCy = seg.CenterY;
            }
        }

        return (bestZoom, bestCx, bestCy, 0f, 0f);
    }

    /// <summary>
    /// Cubic bezier ease-in-out from <paramref name="from"/> to <paramref name="to"/>
    /// at normalized progress <paramref name="t"/> ∈ [0, 1].
    /// Uses control points (0.25, 0.1, 0.25, 1.0) for a smooth, gradual ease
    /// that feels natural and cinematic.
    /// </summary>
    private static float CubicBezierEase(float from, float to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0f) return from;
        if (t >= 1f) return to;

        // Cubic bezier with control points (0.25, 0.1, 0.25, 1.0)
        // Solve for the bezier parameter u given input t using Newton's method,
        // then evaluate the Y curve for the eased value.
        const float x1 = 0.25f, y1 = 0.1f, x2 = 0.25f, y2 = 1.0f;

        // Find u such that BezierX(u) = t
        float u = t; // initial guess
        for (int i = 0; i < 8; i++)
        {
            float xU = BezierComponent(u, x1, x2) - t;
            float dxU = BezierComponentDerivative(u, x1, x2);
            if (MathF.Abs(dxU) < 1e-6f) break;
            u -= xU / dxU;
            u = Math.Clamp(u, 0f, 1f);
        }

        float eased = BezierComponent(u, y1, y2);
        return from + (to - from) * eased;
    }

    /// <summary>Evaluates a single cubic bezier component: B(t) for points (0, p1, p2, 1).</summary>
    private static float BezierComponent(float t, float p1, float p2)
    {
        float mt = 1f - t;
        return 3f * mt * mt * t * p1 + 3f * mt * t * t * p2 + t * t * t;
    }

    /// <summary>Derivative of BezierComponent with respect to t.</summary>
    private static float BezierComponentDerivative(float t, float p1, float p2)
    {
        float mt = 1f - t;
        return 3f * mt * mt * p1 + 6f * mt * t * (p2 - p1) + 3f * t * t * (1f - p2);
    }

    /// <summary>
    /// Analytical spring-based easing from <paramref name="from"/> to <paramref name="to"/>
    /// at normalized progress <paramref name="t"/> ∈ [0, 1].
    /// Uses the engine's configured spring constant and damping for consistent feel.
    /// Retained for SpringInterpolate and backward compatibility.
    /// </summary>
    private float SpringEase(float from, float to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0f) return from;
        if (t >= 1f) return to;

        float omega = MathF.Sqrt(_config.SpringConstant);
        float zeta = _config.SpringDamping / (2f * omega);

        // Compute settling time so the spring reaches ~98% by t=1
        float settlingTime;
        if (zeta >= 1f)
        {
            float disc = MathF.Sqrt(zeta * zeta - 1f);
            float dominantPole = omega * (-zeta + disc); // negative value
            settlingTime = Math.Max(0.1f, -5f / dominantPole);
        }
        else if (zeta > 0.01f)
        {
            settlingTime = 5f / (zeta * omega);
        }
        else
        {
            settlingTime = 10f / omega;
        }

        float physTime = t * settlingTime;
        float response;

        if (zeta >= 1f)
        {
            if (Math.Abs(zeta - 1f) < 0.001f)
            {
                // Critically damped
                float e = MathF.Exp(-omega * physTime);
                response = 1f - e * (1f + omega * physTime);
            }
            else
            {
                // Overdamped
                float disc = MathF.Sqrt(zeta * zeta - 1f);
                float s1 = -omega * (zeta - disc);
                float s2 = -omega * (zeta + disc);
                response = 1f - (s2 * MathF.Exp(s1 * physTime) - s1 * MathF.Exp(s2 * physTime)) / (s2 - s1);
            }
        }
        else
        {
            // Underdamped — smooth with slight overshoot
            float omegaD = omega * MathF.Sqrt(1f - zeta * zeta);
            float e = MathF.Exp(-zeta * omega * physTime);
            response = 1f - e * (MathF.Cos(omegaD * physTime) +
                zeta * omega / omegaD * MathF.Sin(omegaD * physTime));
        }

        response = Math.Clamp(response, 0f, 1f);
        return from + (to - from) * response;
    }

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
    private ZoomState ComputeViewport((float zoom, float cx, float cy, float rotY, float rotX) state)
    {
        var result = ComputeViewportForCenter(state.zoom, state.cx, state.cy);
        result.RotationY = state.rotY;
        result.RotationX = state.rotX;
        return result;
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
        };
    }
}
