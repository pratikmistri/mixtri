namespace Musio.Core.Timeline;

using Musio.Core.Processing;

// ── Camera (webcam) track operations ──
// Camera segments live on their own track; their Start/Duration are source-video
// time ranges (independent of the primary segment list), so these operations never
// call RecalculateSegmentPositions.

/// <summary>Adds a new camera overlay segment over a source-time range.</summary>
public class AddCameraSegmentOperation : IEditOperation
{
    private readonly CameraSegment _segment;

    public string CreatedId => _segment.Id;
    public string Description => "Add Camera Segment";

    public AddCameraSegmentOperation(CameraSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
    }

    public AddCameraSegmentOperation(TimeSpan sourceStart, TimeSpan sourceDuration,
        string? webcamFilePath = null, WebcamOverlayStyle? style = null)
    {
        _segment = new CameraSegment
        {
            Start = sourceStart,
            Duration = sourceDuration,
            WebcamFilePath = webcamFilePath,
            StyleOverride = style,
        };
    }

    public void Execute(TimelineModel model)
    {
        model.CameraSegments.Add(_segment);
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        model.CameraSegments.RemoveAll(s => s.Id == _segment.Id);
    }
}

/// <summary>Moves a camera segment to a new source-time start, preserving its duration.</summary>
public class MoveCameraSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly TimeSpan _newStart;
    private TimeSpan _previousStart;

    public string Description => "Move Camera Segment";

    public MoveCameraSegmentOperation(string segmentId, TimeSpan newStart)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        _previousStart = seg.Start;
        seg.Start = _newStart < TimeSpan.Zero ? TimeSpan.Zero : _newStart;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Start = _previousStart;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Trims one edge of a camera segment (source-time range).</summary>
public class TrimCameraSegmentOperation : IEditOperation
{
    /// <summary>Minimum camera segment duration.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    private readonly string _segmentId;
    private readonly bool _fromStart;
    private readonly TimeSpan _newEdge;
    private TimeSpan _previousStart;
    private TimeSpan _previousDuration;

    public string Description => "Trim Camera Segment";

    /// <param name="segmentId">Segment to trim.</param>
    /// <param name="fromStart">True for the left edge, false for the right edge.</param>
    /// <param name="newEdgeSourceTime">New source-time position of the dragged edge.</param>
    public TrimCameraSegmentOperation(string segmentId, bool fromStart, TimeSpan newEdgeSourceTime)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _fromStart = fromStart;
        _newEdge = newEdgeSourceTime;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;

        _previousStart = seg.Start;
        _previousDuration = seg.Duration;

        if (_fromStart)
        {
            var end = seg.End;
            var newStart = _newEdge < TimeSpan.Zero ? TimeSpan.Zero : _newEdge;
            if (newStart > end - MinDuration) newStart = end - MinDuration;
            seg.Start = newStart;
            seg.Duration = end - newStart;
        }
        else
        {
            var newEnd = _newEdge;
            if (newEnd < seg.Start + MinDuration) newEnd = seg.Start + MinDuration;
            seg.Duration = newEnd - seg.Start;
        }

        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Start = _previousStart;
        seg.Duration = _previousDuration;
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Removes a camera segment.</summary>
public class RemoveCameraSegmentOperation : IEditOperation
{
    private readonly string _segmentId;
    private CameraSegment? _removed;

    public string Description => "Remove Camera Segment";

    public RemoveCameraSegmentOperation(string segmentId)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (_removed is not null)
            model.CameraSegments.RemoveAll(s => s.Id == _segmentId);
    }

    public void Undo(TimelineModel model)
    {
        if (_removed is null) return;
        model.CameraSegments.Add(_removed);
        model.CameraSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}

/// <summary>Updates the enabled flag and/or style override of a camera segment.</summary>
public class UpdateCameraSegmentPropertiesOperation : IEditOperation
{
    private readonly string _segmentId;
    private readonly bool? _enabled;
    private readonly bool? _fullscreenEnabled;
    private readonly CameraFullscreenMode? _fullscreenMode;
    private readonly WebcamOverlayStyle? _styleOverride;
    private readonly bool _setStyle;

    private bool _previousEnabled;
    private bool _previousFullscreenEnabled;
    private CameraFullscreenMode _previousFullscreenMode;
    private WebcamOverlayStyle? _previousStyle;

    public string Description => "Update Camera Segment";

    public UpdateCameraSegmentPropertiesOperation(string segmentId, bool? enabled = null,
        WebcamOverlayStyle? styleOverride = null, bool setStyle = false,
        bool? fullscreenEnabled = null, CameraFullscreenMode? fullscreenMode = null)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _enabled = enabled;
        _styleOverride = styleOverride;
        _setStyle = setStyle;
        _fullscreenEnabled = fullscreenEnabled;
        _fullscreenMode = fullscreenMode;
    }

    public void Execute(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;

        _previousEnabled = seg.Enabled;
        _previousFullscreenEnabled = seg.FullscreenEnabled;
        _previousFullscreenMode = seg.FullscreenMode;
        _previousStyle = seg.StyleOverride;

        if (_enabled is bool en) seg.Enabled = en;
        if (_fullscreenEnabled is bool fs) seg.FullscreenEnabled = fs;
        if (_fullscreenMode is CameraFullscreenMode fm) seg.FullscreenMode = fm;
        if (_setStyle) seg.StyleOverride = _styleOverride;
    }

    public void Undo(TimelineModel model)
    {
        var seg = model.CameraSegments.FirstOrDefault(s => s.Id == _segmentId);
        if (seg is null) return;
        seg.Enabled = _previousEnabled;
        seg.FullscreenEnabled = _previousFullscreenEnabled;
        seg.FullscreenMode = _previousFullscreenMode;
        seg.StyleOverride = _previousStyle;
    }
}
