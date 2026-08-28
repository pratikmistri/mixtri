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
    public bool ZoomOnScroll { get; init; } = false;
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
    /// <summary>
    /// True when the focal point comes from a keyframe's authored centre and should not be
    /// overridden by the cursor. Derived from <see cref="CursorFollowWeight"/> rather than
    /// stored, so it cannot be forgotten at one of the sites that rebuild this struct — a
    /// silent-failure mode this repo has hit before with exactly this kind of field.
    /// </summary>
    public readonly bool IsManualOverride => CursorFollowWeight <= 0f;

    /// <summary>
    /// How strongly the compositor should re-centre on the live cursor instead of on
    /// <see cref="CenterX"/>/<see cref="CenterY"/>: <c>1</c> for a purely auto (click-driven)
    /// zoom, <c>0</c> for a purely manual one, and eased in between across a handoff.
    /// <para>
    /// This is a weight rather than a bool because manual and auto shots resolve their focal
    /// point from different sources — the live cursor versus the keyframe's stored centre —
    /// so switching between them abruptly at a piece boundary would snap the camera. Defaults
    /// to 1 so a plain <c>ZoomState</c> keeps the historical cursor-following behaviour.
    /// </para>
    /// </summary>
    public float CursorFollowWeight;

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

    /// <summary>
    /// Drift settings owned by the active zoom segment, or null for the built-in defaults.
    /// Carried on the state (rather than read from CompositionConfig) because drift is a
    /// per-segment property: two shots a second apart can legitimately drift differently.
    /// </summary>
    public CameraDriftSettings? DriftSettings;
}

public class AutoZoomEngine
{
    private AutoZoomConfig _config;
    private readonly List<ZoomKeyframe> _manualKeyframes = [];
    private List<ZoomSegment> _autoSegments = [];
    private ZoomCameraPath _path = ZoomCameraPath.Empty;
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

        // RebuildAutoSegments ends with RebuildPath on every path, and that rebuild picks up
        // BOTH the manual keyframes and the freshly-built auto segments using the dimensions
        // just cached above — so it also covers the case where SetManualKeyframes ran before
        // this call, when the source size was still unknown. Rebuilding here as well would be
        // redundant work, and would briefly publish a path built from the NEW dimensions but
        // the STALE auto segments.
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
            RebuildPath();
            return;
        }

        var clicks = _lastMouseData.Clicks
            .Where(c => c.IsDown && !_suppressedClickTicks.Contains(c.TimestampTicks))
            .OrderBy(c => c.TimestampTicks)
            .ToList();

        if (clicks.Count == 0)
        {
            RebuildPath();
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
        RebuildPath();
    }

    public void AddManualKeyframe(ZoomKeyframe keyframe)
    {
        _manualKeyframes.Add(keyframe);
        _manualKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        RebuildPath();
    }

    public void RemoveManualKeyframe(TimeSpan timestamp)
    {
        _manualKeyframes.RemoveAll(k => k.Timestamp == timestamp);
        RebuildPath();
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
        RebuildPath();
    }

    /// <summary>
    /// Builds a stable, process-independent seed from a segment's start time, so the
    /// same segment always drifts the same way in preview and in export.
    /// </summary>
    private static int SegmentSeedFromStart(double startSeconds)
        => (int)Math.Round(startSeconds * 1000.0);

    /// <summary>
    /// Get the zoom state at any timestamp, resolved from the single chained camera path
    /// that carries both manual keyframes and auto (click-driven) segments. Pure function
    /// of time: the path is prebuilt, so this only reads immutable state.
    /// </summary>
    public ZoomState GetZoomState(double timeSeconds)
    {
        if (_path.TryEvaluate(timeSeconds, out var sample))
            return ComputeViewport(sample);

        return ComputeViewportForCenter(1.0f, _sourceWidth / 2f, _sourceHeight / 2f);
    }

    /// <summary>
    /// Rebuilds the single camera path from BOTH manual keyframes and auto (click-driven)
    /// segments.
    /// <para>
    /// These used to be two independent paths evaluated with hard precedence — manual first,
    /// auto only as a fallback. That produced a hard cut whenever a manual segment overlapped
    /// an auto one: the manual path became active at its own ramp start, which begins at 1×,
    /// so the camera flashed from the auto zoom out to full frame and immediately dove back
    /// in. Two independent paths can never hand off to each other, which is exactly what the
    /// chained-path model exists to do.
    /// </para>
    /// <para>
    /// They are now one ordered shot list, so a manual and an auto shot hand off to each other
    /// like any other linked pair. Manual still wins where the two genuinely conflict: an auto
    /// shot whose hold sits inside a manual shot's hold is dropped (see
    /// <see cref="IsCoveredByManualHold"/>), so an explicit segment is never chopped into
    /// waypoints by the clicks underneath it.
    /// </para>
    /// </summary>
    private void RebuildPath()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0)
        {
            _path = ZoomCameraPath.Empty;
            return;
        }

        var manualShots = _manualKeyframes.Select(ToZoomShot).ToList();
        var shots = new List<ZoomShot>(manualShots);

        foreach (var segment in _autoSegments)
        {
            var autoShot = ToZoomShot(segment);
            if (!IsCoveredByManualHold(autoShot, manualShots))
                shots.Add(autoShot);
        }

        _path = shots.Count == 0
            ? ZoomCameraPath.Empty
            : ZoomCameraPath.Build(shots);
    }

    /// <summary>
    /// True when <paramref name="autoShot"/> is the SAME zoom moment as some manual shot —
    /// they settle at effectively the same instant — so the user's explicit segment should
    /// replace the click-driven one rather than both firing.
    /// <para>
    /// This is deliberately a same-moment test and not an overlap test. Auto segments span
    /// ~3.9s while clicks are often ~1s apart, so consecutive auto shots overlap each other
    /// heavily as a matter of course. Suppressing on overlap meant that editing one segment's
    /// length — which promotes it to manual — silently deleted its neighbours on both sides,
    /// which is exactly the "it gets weird when I start editing the segment lengths" report.
    /// </para>
    /// <para>
    /// In the normal editor flow this is belt-and-braces anyway: promoting an auto keyframe also
    /// adds its source click to <c>SuppressedClickTicks</c>, so the matching auto shot is never
    /// generated in the first place. This covers the paths that bypass that, such as adding a
    /// manual keyframe straight onto the engine.
    /// </para>
    /// </summary>
    private static bool IsCoveredByManualHold(ZoomShot autoShot, List<ZoomShot> manualShots)
    {
        const double sameMomentSeconds = 0.05;

        foreach (var manual in manualShots)
        {
            if (Math.Abs(manual.HoldStart - autoShot.HoldStart) <= sameMomentSeconds)
                return true;
        }

        return false;
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
            SegmentSeedFromStart(rampStart),
            // A manual keyframe only pins its framing if the user actually authored a region.
            // One promoted just by being moved or resized keeps following the cursor, exactly
            // as it did before the edit.
            HasFixedCenter: keyframe.UsesAuthoredCenter,
            DriftScale: (float)Math.Clamp(keyframe.DriftScale, 0.0, 1.0),
            Drift: keyframe.Drift);
    }

    private static ZoomShot ToZoomShot(ZoomSegment segment)
        // Engine-generated click zooms have no authoring surface for drift settings, so they
        // pass no Drift and resolve to CameraDriftSettings.Default.
        => new(
            segment.ZoomInStart,
            segment.ZoomInEnd,
            segment.HoldEnd,
            segment.ZoomOutEnd,
            segment.TargetZoom,
            segment.CenterX,
            segment.CenterY,
            SegmentSeedFromStart(segment.ZoomInStart),
            HasFixedCenter: false);

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
        state.DriftSettings = sample.Drift;
        state.CursorFollowWeight = sample.CursorFollowWeight;
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
            CursorFollowWeight = 1f,
        };
    }
}
