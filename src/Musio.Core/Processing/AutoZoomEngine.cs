using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Core.Processing;

public record AutoZoomConfig
{
    public bool Enabled { get; init; } = true;
    public float DefaultZoomLevel { get; init; } = 2.0f;
    public float PreClickDuration { get; init; } = 0.3f;
    public float HoldDuration { get; init; } = 0.5f;
    public float EaseOutDuration { get; init; } = 0.5f;
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
}

public class AutoZoomEngine
{
    private AutoZoomConfig _config;
    private readonly List<ZoomKeyframe> _manualKeyframes = [];
    private List<ZoomSegment> _autoSegments = [];
    private int _sourceWidth;
    private int _sourceHeight;

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
    /// <paramref name="coordScaleX"/> and <paramref name="coordScaleY"/> convert
    /// mouse hook coordinates (logical pixels) to capture frame coordinates (physical pixels).
    /// </summary>
    public void BuildZoomTimeline(
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        double tickFrequency,
        float coordScaleX = 1.0f,
        float coordScaleY = 1.0f,
        double timeOffsetSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(mouseData);
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _autoSegments.Clear();

        if (!_config.Enabled) return;

        var clicks = mouseData.Clicks
            .Where(c => c.IsDown)
            .OrderBy(c => c.TimestampTicks)
            .ToList();

        if (clicks.Count == 0) return;

        long startTick = mouseData.StartTimestampTicks;

        var rawSegments = new List<ZoomSegment>();
        foreach (var click in clicks)
        {
            double clickTime = (click.TimestampTicks - startTick) / tickFrequency - timeOffsetSeconds;

            rawSegments.Add(new ZoomSegment
            {
                ZoomInStart = clickTime - _config.PreClickDuration,
                ZoomInEnd = clickTime,
                HoldEnd = clickTime + _config.HoldDuration,
                ZoomOutEnd = clickTime + _config.HoldDuration + _config.EaseOutDuration,
                TargetZoom = _config.DefaultZoomLevel,
                CenterX = Math.Clamp(click.X * coordScaleX, 0f, sourceWidth - 1f),
                CenterY = Math.Clamp(click.Y * coordScaleY, 0f, sourceHeight - 1f),
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
    /// Get the zoom state at any timestamp. Manual keyframes override auto-zoom.
    /// </summary>
    public ZoomState GetZoomState(double timeSeconds)
    {
        // Manual keyframes take priority over auto-generated segments
        var manualResult = EvaluateManualKeyframes(timeSeconds);
        if (manualResult.HasValue)
            return ComputeViewport(manualResult.Value);

        var autoResult = EvaluateAutoSegments(timeSeconds);
        return ComputeViewport(autoResult);
    }

    private (float zoom, float cx, float cy)? EvaluateManualKeyframes(double timeSeconds)
    {
        foreach (var kf in _manualKeyframes)
        {
            double kfTime = kf.Timestamp.TotalSeconds;
            double preStart = kfTime - kf.PreDuration.TotalSeconds;
            double holdEnd = kfTime + kf.HoldDuration.TotalSeconds;
            double postEnd = holdEnd + kf.PostDuration.TotalSeconds;

            if (timeSeconds < preStart || timeSeconds > postEnd)
                continue;

            float zoom;
            if (timeSeconds < kfTime)
            {
                double progress = (kfTime - preStart) > 0
                    ? (timeSeconds - preStart) / (kfTime - preStart)
                    : 1.0;
                zoom = SpringEase(1.0f, (float)kf.ZoomLevel, (float)progress);
            }
            else if (timeSeconds <= holdEnd)
            {
                zoom = (float)kf.ZoomLevel;
            }
            else
            {
                double progress = (postEnd - holdEnd) > 0
                    ? (timeSeconds - holdEnd) / (postEnd - holdEnd)
                    : 1.0;
                zoom = SpringEase((float)kf.ZoomLevel, 1.0f, (float)progress);
            }

            float cx = (float)(kf.CenterX * _sourceWidth);
            float cy = (float)(kf.CenterY * _sourceHeight);
            return (zoom, cx, cy);
        }

        return null;
    }

    private (float zoom, float cx, float cy) EvaluateAutoSegments(double timeSeconds)
    {
        foreach (var seg in _autoSegments)
        {
            if (timeSeconds < seg.ZoomInStart || timeSeconds > seg.ZoomOutEnd)
                continue;

            float zoom;
            if (timeSeconds < seg.ZoomInEnd)
            {
                double duration = seg.ZoomInEnd - seg.ZoomInStart;
                double progress = duration > 0 ? (timeSeconds - seg.ZoomInStart) / duration : 1.0;
                zoom = SpringEase(1.0f, seg.TargetZoom, (float)progress);
            }
            else if (timeSeconds <= seg.HoldEnd)
            {
                zoom = seg.TargetZoom;
            }
            else
            {
                double duration = seg.ZoomOutEnd - seg.HoldEnd;
                double progress = duration > 0 ? (timeSeconds - seg.HoldEnd) / duration : 1.0;
                zoom = SpringEase(seg.TargetZoom, 1.0f, (float)progress);
            }

            return (zoom, seg.CenterX, seg.CenterY);
        }

        // No active zoom segment — return default (no zoom)
        float defaultCx = _sourceWidth / 2f;
        float defaultCy = _sourceHeight / 2f;
        return (1.0f, defaultCx, defaultCy);
    }

    /// <summary>
    /// Analytical spring-based easing from <paramref name="from"/> to <paramref name="to"/>
    /// at normalized progress <paramref name="t"/> ∈ [0, 1].
    /// Uses the engine's configured spring constant and damping for consistent feel.
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
    private ZoomState ComputeViewport((float zoom, float cx, float cy) state)
    {
        float zoom = Math.Max(state.zoom, 1.0f);
        float vpWidth = _sourceWidth / zoom;
        float vpHeight = _sourceHeight / zoom;

        // Position viewport so the click point is at a consistent relative position.
        // Instead of always centering, bias toward keeping the click visible
        // by clamping the click's position within the viewport to 20%-80% range.
        float vpX = state.cx - vpWidth / 2f;
        float vpY = state.cy - vpHeight / 2f;

        // Clamp viewport to source bounds
        vpX = Math.Clamp(vpX, 0f, Math.Max(0f, _sourceWidth - vpWidth));
        vpY = Math.Clamp(vpY, 0f, Math.Max(0f, _sourceHeight - vpHeight));

        // Recompute actual center after clamping (so downstream code uses the real center)
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
