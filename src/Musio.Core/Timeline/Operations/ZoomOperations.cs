namespace Musio.Core.Timeline;

public class MoveZoomKeyframeOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly TimeSpan _newTimestamp;
    private TimeSpan _previousTimestamp;
    private bool _previousIsManual;

    public string Description => "Move Zoom Point";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public MoveZoomKeyframeOperation(string keyframeId, TimeSpan newTimestamp)
    {
        _keyframeId = keyframeId;
        _newTimestamp = newTimestamp;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) { _changed = false; return; }
        _previousTimestamp = model.ZoomKeyframes[index].Timestamp;
        _previousIsManual = model.ZoomKeyframes[index].IsManual;
        var sourceClickTicks = model.ZoomKeyframes[index].SourceClickTicks;
        model.ZoomKeyframes[index] = model.ZoomKeyframes[index] with
        {
            Timestamp = _newTimestamp,
            IsManual = true,
            // Moving is not a framing decision. Carry the effective value across so promotion
            // cannot flip a click-driven zoom to a pinned one via the IsManual fallback.
            HasAuthoredCenter = model.ZoomKeyframes[index].UsesAuthoredCenter,
        };

        // When converting an auto-generated keyframe to manual, suppress the
        // original auto-zoom click so it doesn't double-fire.
        ZoomOperationHelpers.SuppressClickIfAutoToManual(model, _previousIsManual, sourceClickTicks);
    }

    public void Undo(TimelineModel model)
    {
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state before reverting the keyframe
        ZoomOperationHelpers.RestoreClickSuppression(model, _previousIsManual, model.ZoomKeyframes[index].SourceClickTicks);

        model.ZoomKeyframes[index] = model.ZoomKeyframes[index] with { Timestamp = _previousTimestamp, IsManual = _previousIsManual };
    }
}

public class RemoveZoomKeyframeOperation : IEditOperation
{
    private readonly string _keyframeId;
    private ZoomKeyframe? _removedKeyframe;
    private int _removedIndex;

    public string Description => "Remove Zoom Segment";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public RemoveZoomKeyframeOperation(string keyframeId)
    {
        _keyframeId = keyframeId;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        _removedIndex = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (_removedIndex < 0) { _changed = false; return; }
        _removedKeyframe = model.ZoomKeyframes[_removedIndex];
        model.ZoomKeyframes.RemoveAt(_removedIndex);

        // Suppress the underlying auto-zoom click so the engine no longer
        // generates a zoom segment for it during preview or export.
        ZoomOperationHelpers.SuppressClickIfAutoToManual(model, _removedKeyframe.IsManual, _removedKeyframe.SourceClickTicks);
    }

    public void Undo(TimelineModel model)
    {
        if (_removedKeyframe is not null)
        {
            int insertIdx = Math.Min(_removedIndex, model.ZoomKeyframes.Count);
            model.ZoomKeyframes.Insert(insertIdx, _removedKeyframe);

            // Restore the auto-zoom click suppression
            ZoomOperationHelpers.RestoreClickSuppression(model, _removedKeyframe.IsManual, _removedKeyframe.SourceClickTicks);
        }
    }
}

public class AddZoomSegmentOperation : IEditOperation
{
    private readonly ZoomKeyframe _keyframe;

    public string Description => "Add Zoom Segment";

    public AddZoomSegmentOperation(TimeSpan start, TimeSpan end, double zoomLevel,
        double centerX = 0.5, double centerY = 0.5)
    {
        _keyframe = ZoomKeyframe.FromRange(start, end, zoomLevel, centerX, centerY);
    }

    public AddZoomSegmentOperation(ZoomKeyframe keyframe)
    {
        _keyframe = keyframe;
    }

    /// <summary>The Id of the created keyframe, available after Execute.</summary>
    public string CreatedId => _keyframe.Id;

    public void Execute(TimelineModel model)
    {
        model.ZoomKeyframes.Add(_keyframe);
        model.ZoomKeyframes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public void Undo(TimelineModel model)
    {
        model.ZoomKeyframes.RemoveAll(k => k.Id == _keyframe.Id);
    }
}

public class ResizeZoomSegmentOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly bool _resizeStart; // true = left edge, false = right edge
    private readonly TimeSpan _newEdgeTime;
    private ZoomKeyframe? _previousKeyframe;

    public string Description => "Resize Zoom Segment";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public ResizeZoomSegmentOperation(string keyframeId, bool resizeStart, TimeSpan newEdgeTime)
    {
        _keyframeId = keyframeId;
        _resizeStart = resizeStart;
        _newEdgeTime = newEdgeTime;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) { _changed = false; return; }

        _previousKeyframe = model.ZoomKeyframes[index];
        var kf = _previousKeyframe;

        // Suppress the auto-zoom click when converting auto→manual via resize
        ZoomOperationHelpers.SuppressClickIfAutoToManual(model, kf.IsManual, kf.SourceClickTicks);

        if (_resizeStart)
        {
            // Moving left edge: change Start → recalculate PreDuration and Timestamp
            var newStart = _newEdgeTime;
            if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;

            // Clamp: new Start can't go past the hold-end point
            var holdEnd = kf.Timestamp + kf.HoldDuration;
            if (newStart > holdEnd - ZoomKeyframe.MinSegmentDuration)
                newStart = holdEnd - ZoomKeyframe.MinSegmentDuration;

            var newPre = kf.Timestamp - newStart;
            if (newPre < TimeSpan.Zero)
            {
                // Dragged past the timestamp — shift timestamp and reduce hold
                var newTimestamp = newStart + TimeSpan.FromMilliseconds(50);
                var newHold = holdEnd - newTimestamp;
                if (newHold < TimeSpan.Zero) newHold = TimeSpan.Zero;
                model.ZoomKeyframes[index] = kf with
                {
                    Timestamp = newTimestamp,
                    PreDuration = TimeSpan.FromMilliseconds(50),
                    HoldDuration = newHold,
                    IsManual = true,
                    // Resizing is not a framing decision — see MoveZoomKeyframeOperation.
                    HasAuthoredCenter = kf.UsesAuthoredCenter,
                };
            }
            else
            {
                model.ZoomKeyframes[index] = kf with
                {
                    PreDuration = newPre,
                    IsManual = true,
                    HasAuthoredCenter = kf.UsesAuthoredCenter,
                };
            }
        }
        else
        {
            // Moving right edge: change End → recalculate HoldDuration (keep PostDuration fixed)
            var newEnd = _newEdgeTime;

            // Clamp: new End can't go before Timestamp + minimum
            var minEnd = kf.Timestamp + ZoomKeyframe.MinSegmentDuration;
            if (newEnd < minEnd) newEnd = minEnd;

            // Clamp to timeline duration
            if (newEnd > model.Duration) newEnd = model.Duration;

            var totalAfterTimestamp = newEnd - kf.Timestamp;
            var newPost = kf.PostDuration;
            var newHold = totalAfterTimestamp - newPost;
            if (newHold < TimeSpan.Zero)
            {
                newHold = TimeSpan.Zero;
                newPost = totalAfterTimestamp;
            }

            model.ZoomKeyframes[index] = kf with
            {
                HoldDuration = newHold,
                PostDuration = newPost,
                IsManual = true,
                HasAuthoredCenter = kf.UsesAuthoredCenter,
            };
        }
    }

    public void Undo(TimelineModel model)
    {
        if (_previousKeyframe is null) return;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state
        ZoomOperationHelpers.RestoreClickSuppression(model, _previousKeyframe.IsManual, _previousKeyframe.SourceClickTicks);

        model.ZoomKeyframes[index] = _previousKeyframe;
    }
}

public class UpdateZoomSegmentPropertiesOperation : IEditOperation
{
    private readonly string _keyframeId;
    private readonly double? _newZoomLevel;
    private readonly double? _newCenterX;
    private readonly double? _newCenterY;
    private readonly bool? _newHasAuthoredCenter;
    private ZoomKeyframe? _previousKeyframe;

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public string Description => _newHasAuthoredCenter == false
        ? "Follow Mouse"
        : "Update Zoom Properties";

    /// <param name="hasAuthoredCenter">
    /// Explicitly sets whether the segment holds its own centre. Pass <c>false</c> to hand the
    /// framing back to the live cursor. Leave null to apply the normal rule, where supplying a
    /// centre pins the framing and anything else preserves it.
    /// </param>
    public UpdateZoomSegmentPropertiesOperation(string keyframeId,
        double? zoomLevel = null, double? centerX = null, double? centerY = null,
        bool? hasAuthoredCenter = null)
    {
        _keyframeId = keyframeId;
        _newZoomLevel = zoomLevel;
        _newCenterX = centerX;
        _newCenterY = centerY;
        _newHasAuthoredCenter = hasAuthoredCenter;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) { _changed = false; return; }

        _previousKeyframe = model.ZoomKeyframes[index];
        var kf = _previousKeyframe;

        // Suppress the auto-zoom click when converting auto→manual via property edit
        ZoomOperationHelpers.SuppressClickIfAutoToManual(model, kf.IsManual, kf.SourceClickTicks);

        model.ZoomKeyframes[index] = kf with
        {
            ZoomLevel = _newZoomLevel ?? kf.ZoomLevel,
            CenterX = Math.Clamp(_newCenterX ?? kf.CenterX, 0, 1),
            CenterY = Math.Clamp(_newCenterY ?? kf.CenterY, 0, 1),
            IsManual = true,
            // An explicit request wins — that is how "follow mouse" hands the framing back to
            // the cursor. Otherwise the normal rule: authoring a region pins it, and anything
            // else carries the EFFECTIVE value across (see MoveZoomKeyframeOperation for why
            // the effective value rather than the raw one).
            HasAuthoredCenter = _newHasAuthoredCenter
                ?? ((_newCenterX is not null || _newCenterY is not null) || kf.UsesAuthoredCenter),
        };
    }

    public void Undo(TimelineModel model)
    {
        if (_previousKeyframe is null) return;
        int index = model.ZoomKeyframes.FindIndex(k => k.Id == _keyframeId);
        if (index < 0) return;

        // Restore suppression state
        ZoomOperationHelpers.RestoreClickSuppression(model, _previousKeyframe.IsManual, _previousKeyframe.SourceClickTicks);

        model.ZoomKeyframes[index] = _previousKeyframe;
    }
}

