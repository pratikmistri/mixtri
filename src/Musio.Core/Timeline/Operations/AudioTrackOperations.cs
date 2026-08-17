namespace Musio.Core.Timeline;

using Musio.Core.Models;

// ── Inserted audio (voice-over / music) track operations ──
// These act on TimelineModel.AudioTracks, whose StartTime is an OUTPUT-timeline time —
// unlike the camera and text-overlay tracks, whose ranges are source-video times. So
// nothing here maps through segments, and none of it calls RecalculateSegmentPositions:
// an inserted track deliberately does not move when the footage under it is re-cut.

/// <summary>Shared invariants for the inserted-audio operations.</summary>
public static class AudioTrackEditing
{
    /// <summary>
    /// Shortest an inserted track may be made by trimming or splitting. Matches
    /// <see cref="TrimCameraSegmentOperation.MinDuration"/>: below about this length a block
    /// is too small to grab again with the mouse, so a smaller one could never be undone by
    /// hand.
    /// </summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>Orders tracks by start time, so a lane always draws and hit-tests left to right.</summary>
    public static void Sort(List<AudioTrack> tracks)
        => tracks.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
}

/// <summary>
/// One recorded capture to lift off a segment, as
/// <see cref="DetachSegmentAudioOperation"/> needs it described.
/// </summary>
/// <param name="FilePath">The recorded WAV (a <c>system_*</c>/<c>mic_*</c> capture).</param>
/// <param name="Name">Label for the resulting block.</param>
/// <param name="OffsetSeconds">
/// That recording's audio-to-video offset — the project's for the primary recording, the
/// segment's own for an appended one. Exactly what <c>ExportAudioPlan</c> aligns with.
/// </param>
/// <param name="SourceDuration">
/// Real length of the file, so the resulting block knows where trimming must stop.
/// <see cref="TimeSpan.Zero"/> means "not measured" and leaves the clamp to the decoder.
/// </param>
public readonly record struct DetachedAudioSource(
    string FilePath,
    string Name,
    double OffsetSeconds,
    TimeSpan SourceDuration);

/// <summary>
/// Lifts a segment's recorded audio off the footage: each capture becomes a free-standing
/// timeline block the user can move and trim, and the segment itself goes silent so the
/// audio is never heard twice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the result is an ordinary <see cref="AudioTrack"/>.</b> That type already means
/// exactly this — audio anchored to a point on the finished timeline rather than to the
/// footage under it — and it already has the move/trim operations, the drag gestures, the
/// preview engine and the export path. Detaching therefore reuses all of it instead of
/// growing a second, parallel notion of a movable audio block.
/// </para>
/// <para>
/// <b>The block gets the segment's WHOLE aligned source range, not the part that was
/// audible.</b> A 2x segment only plays half its audio while bound (the rest is cut at the
/// boundary); the entire point of detaching is to reposition what the cut threw away, so the
/// block starts at the segment's own start and runs for as long as the source really does.
/// It may therefore extend past the segment — which is legal for an inserted track, and is
/// the L-cut this makes possible.
/// </para>
/// <para>
/// <b>Silencing the segment reuses <see cref="SegmentAudioMode.Muted"/>.</b> Preview and
/// export already honour it per segment, so no second suppression rule (and no way for the
/// two pipelines to disagree about one) has to exist.
/// </para>
/// </remarks>
public class DetachSegmentAudioOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private readonly IReadOnlyList<DetachedAudioSource> _sources;

    /// <summary>
    /// The blocks this created, built once and re-added verbatim on redo.
    /// </summary>
    /// <remarks>
    /// Ids must survive an undo/redo round trip: anything else holding one (the timeline's
    /// selection, a later move or trim operation sitting above this on the redo stack) would
    /// be pointing at a block that no longer exists after a redo. Reusing the instances is
    /// how <see cref="AddAudioTrackOperation"/> already achieves it, and why
    /// <see cref="SplitAudioTrackOperation"/> pins its half's id up front.
    /// </remarks>
    private readonly List<AudioTrack> _created = [];

    private int _index = -1;
    private SegmentAudioMode _previousMode;

    public override string Description => "Detach Segment Audio";

    public DetachSegmentAudioOperation(string segmentId, IReadOnlyList<DetachedAudioSource> sources)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    /// <summary>Ids of the blocks this created, so the caller can select one.</summary>
    public IReadOnlyList<string> CreatedTrackIds => [.. _created.Select(t => t.Id)];

    /// <summary>
    /// Where a segment's recorded audio sits, in the coordinates an
    /// <see cref="AudioTrack"/> uses: an output-timeline start, an offset into the source
    /// file, and how much of that file the segment covers.
    /// </summary>
    /// <remarks>
    /// The alignment mirrors <c>ExportAudioPlan.BuildPlacement</c> exactly — the file
    /// position for source-video time <c>T</c> is <c>T + offset</c>, clipped to the file's own
    /// domain, with the clipped head converted back into an output delay through the
    /// segment's speed. What it deliberately does NOT copy is that method's take cap: this
    /// keeps everything the segment covers, because the user is detaching it precisely to
    /// place the part the cap would have discarded.
    /// </remarks>
    public static (TimeSpan Start, TimeSpan TrimStart, TimeSpan Duration)? ResolveWindow(
        VideoSegment segment, double offsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.SourceDuration <= TimeSpan.Zero || segment.Duration <= TimeSpan.Zero) return null;

        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        var offset = TimeSpan.FromSeconds(offsetSeconds);

        var audioStart = segment.SourceStart + offset;
        var audioEnd = segment.SourceStart + segment.SourceDuration + offset;
        if (audioStart < TimeSpan.Zero) audioStart = TimeSpan.Zero;
        if (audioEnd <= audioStart) return null;

        var sourceLead = (audioStart - offset) - segment.SourceStart;
        var outputLead = sourceLead > TimeSpan.Zero
            ? TimeSpan.FromTicks((long)(sourceLead.Ticks / speed))
            : TimeSpan.Zero;

        return (segment.Start + outputLead, audioStart, audioEnd - audioStart);
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_index < 0) return NothingToDo();
        if (model.Segments[_index] is not VideoSegment video) return NothingToDo();
        if (_sources.Count == 0) return NothingToDo();

        // A redo re-adds the very blocks the undo removed, ids and all.
        if (_created.Count == 0)
        {
            foreach (var source in _sources)
            {
                if (string.IsNullOrWhiteSpace(source.FilePath)) continue;
                if (ResolveWindow(video, source.OffsetSeconds) is not { } window) continue;

                _created.Add(new AudioTrack
                {
                    FilePath = source.FilePath,
                    Name = string.IsNullOrWhiteSpace(source.Name) ? "Recorded audio" : source.Name,

                    // Voice-over rather than music: this is the recording's own speech/system
                    // audio, so it must sit at full level, not ducked under itself.
                    Kind = AudioTrackKind.VoiceOver,
                    StartTime = window.Start,
                    TrimStart = window.TrimStart,
                    Duration = window.Duration,
                    SourceDuration = source.SourceDuration,
                    Volume = 1.0,
                    DetachedFromSegmentId = _segmentId,
                });
            }
        }

        if (_created.Count == 0) return NothingToDo();

        foreach (var track in _created)
        {
            if (!model.AudioTracks.Any(t => t.Id == track.Id))
                model.AudioTracks.Add(track);
        }

        AudioTrackEditing.Sort(model.AudioTracks);

        _previousMode = video.AudioMode;
        video.AudioMode = SegmentAudioMode.Muted;

        // Audio-only: no segment moved, so no re-flow is owed.
        return false;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        foreach (var track in _created)
            model.AudioTracks.RemoveAll(t => t.Id == track.Id);

        if (_index >= 0 && _index < model.Segments.Count
            && model.Segments[_index] is VideoSegment video)
        {
            video.AudioMode = _previousMode;
        }

        return false;
    }
}

/// <summary>
/// The reverse of <see cref="DetachSegmentAudioOperation"/>: removes the blocks that were
/// lifted off a segment and lets the segment play its own audio again.
/// </summary>
/// <remarks>
/// This exists so "unmute" cannot resurrect audio that is already on the timeline. A detached
/// segment is muted precisely because its recording is playing from its own blocks; unmuting
/// it while they remain would sum the same capture twice, in preview and in the export. So
/// the menu offers this instead of a plain unmute whenever detached blocks are present, and
/// the two edits stay a matched pair.
/// </remarks>
public class ReattachSegmentAudioOperation : SegmentEditOperationBase
{
    private readonly string _segmentId;
    private readonly List<AudioTrack> _removed = [];

    private int _index = -1;
    private SegmentAudioMode _previousMode;

    public override string Description => "Re-attach Segment Audio";

    public ReattachSegmentAudioOperation(string segmentId)
    {
        _segmentId = segmentId ?? throw new ArgumentNullException(nameof(segmentId));
    }

    /// <summary>Whether any block on <paramref name="model"/> was detached from this segment.</summary>
    public static bool HasDetachedAudio(TimelineModel model, string segmentId)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.AudioTracks.Any(t => t.DetachedFromSegmentId == segmentId);
    }

    protected override bool ExecuteCore(TimelineModel model)
    {
        _index = model.Segments.FindIndex(s => s.Id == _segmentId);
        if (_index < 0) return NothingToDo();
        if (model.Segments[_index] is not VideoSegment video) return NothingToDo();

        _removed.Clear();
        _removed.AddRange(model.AudioTracks.Where(t => t.DetachedFromSegmentId == _segmentId));
        if (_removed.Count == 0) return NothingToDo();

        model.AudioTracks.RemoveAll(t => t.DetachedFromSegmentId == _segmentId);

        _previousMode = video.AudioMode;

        // Back to audible: detaching is the only thing that muted it, so undoing that has to
        // restore sound, not leave a silent segment with nothing playing over it.
        video.AudioMode = SegmentAudioMode.TimeStretch;

        return false;
    }

    protected override bool UndoCore(TimelineModel model)
    {
        if (_removed.Count == 0) return false;

        model.AudioTracks.AddRange(_removed);
        AudioTrackEditing.Sort(model.AudioTracks);

        if (_index >= 0 && _index < model.Segments.Count
            && model.Segments[_index] is VideoSegment video)
        {
            video.AudioMode = _previousMode;
        }

        return false;
    }
}

/// <summary>Inserts an audio track (a completed voice-over/music import) onto the timeline.</summary>
public class AddAudioTrackOperation : IEditOperation
{
    private readonly AudioTrack _track;

    public string CreatedId => _track.Id;
    public string Description => _track.Kind == AudioTrackKind.Music ? "Insert Music" : "Insert Voice Over";

    public AddAudioTrackOperation(AudioTrack track)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));
    }

    public void Execute(TimelineModel model)
    {
        model.AudioTracks.Add(_track);
        AudioTrackEditing.Sort(model.AudioTracks);
    }

    public void Undo(TimelineModel model)
    {
        model.AudioTracks.RemoveAll(t => t.Id == _track.Id);
    }
}

/// <summary>Moves an inserted track to a new OUTPUT-timeline start, preserving its content.</summary>
/// <remarks>
/// Only <see cref="AudioTrack.StartTime"/> changes: the trim and duration describe which
/// part of the source file plays, which a move must not disturb.
/// </remarks>
public class MoveAudioTrackOperation : IEditOperation
{
    private readonly string _trackId;
    private readonly TimeSpan _newStart;
    private TimeSpan _previousStart;

    public string Description => "Move Audio Track";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public MoveAudioTrackOperation(string trackId, TimeSpan newStart)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) { _changed = false; return; }

        _previousStart = track.StartTime;
        track.StartTime = _newStart < TimeSpan.Zero ? TimeSpan.Zero : _newStart;
        AudioTrackEditing.Sort(model.AudioTracks);
    }

    public void Undo(TimelineModel model)
    {
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

        track.StartTime = _previousStart;
        AudioTrackEditing.Sort(model.AudioTracks);
    }
}

/// <summary>Trims one edge of an inserted track.</summary>
/// <remarks>
/// <para>
/// The left edge is the interesting one. Dragging it right must reveal LATER audio, not
/// replay the same audio further along the timeline — so <see cref="AudioTrack.TrimStart"/>
/// advances by exactly the same amount <see cref="AudioTrack.StartTime"/> does, keeping every
/// remaining sample pinned to the timeline instant it was already at. That is standard NLE
/// behaviour, and the reason this cannot be expressed as a plain start/duration edit.
/// </para>
/// <para>
/// The right edge only shortens or lengthens <see cref="AudioTrack.Duration"/>, and can never
/// exceed <see cref="AudioTrack.AvailableAfterTrim"/> — there is no more file to play.
/// </para>
/// </remarks>
public class TrimAudioTrackOperation : IEditOperation
{
    /// <summary>Minimum length an inserted track may be trimmed to.</summary>
    public static TimeSpan MinDuration => AudioTrackEditing.MinDuration;

    private readonly string _trackId;
    private readonly bool _fromStart;
    private readonly TimeSpan _newEdge;
    private TimeSpan _previousStart;
    private TimeSpan _previousTrimStart;
    private TimeSpan? _previousDuration;

    public string Description => "Trim Audio Track";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    /// <param name="trackId">Track to trim.</param>
    /// <param name="fromStart">True for the left edge, false for the right edge.</param>
    /// <param name="newEdgeOutputTime">New OUTPUT-timeline position of the dragged edge.</param>
    public TrimAudioTrackOperation(string trackId, bool fromStart, TimeSpan newEdgeOutputTime)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _fromStart = fromStart;
        _newEdge = newEdgeOutputTime;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) { _changed = false; return; }

        _previousStart = track.StartTime;
        _previousTrimStart = track.TrimStart;
        _previousDuration = track.Duration;

        if (_fromStart)
        {
            var end = track.End;
            var newStart = _newEdge;

            // Dragging left can only reveal audio that exists before the current trim
            // point; past that there is nothing to uncover, so the edge stops.
            var earliest = track.StartTime - track.TrimStart;
            if (newStart < earliest) newStart = earliest;
            if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
            if (newStart > end - AudioTrackEditing.MinDuration)
                newStart = end - AudioTrackEditing.MinDuration;

            var delta = newStart - track.StartTime;
            track.StartTime = newStart;
            // Same delta on both, so the audio under the surviving part does not shift.
            track.TrimStart = track.TrimStart + delta;
            if (track.TrimStart < TimeSpan.Zero) track.TrimStart = TimeSpan.Zero;
            track.Duration = end - newStart;
        }
        else
        {
            var newEnd = _newEdge;
            if (newEnd < track.StartTime + AudioTrackEditing.MinDuration)
                newEnd = track.StartTime + AudioTrackEditing.MinDuration;

            var requested = newEnd - track.StartTime;
            var available = track.AvailableAfterTrim;
            if (available > TimeSpan.Zero && requested > available)
                requested = available;

            track.Duration = requested;
        }

        AudioTrackEditing.Sort(model.AudioTracks);
    }

    public void Undo(TimelineModel model)
    {
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

        track.StartTime = _previousStart;
        track.TrimStart = _previousTrimStart;
        track.Duration = _previousDuration;
        AudioTrackEditing.Sort(model.AudioTracks);
    }
}

/// <summary>Splits an inserted track in two at an OUTPUT-timeline instant.</summary>
/// <remarks>
/// The left half keeps the original's id (so a selection survives the split), and the right
/// half is a new track whose <see cref="AudioTrack.TrimStart"/> is advanced by the length of
/// the left half — the two together therefore play exactly the audio the single track did,
/// until the user moves one of them. A split point that would leave either half shorter than
/// <see cref="AudioTrackEditing.MinDuration"/> is rejected outright rather than clamped: the
/// user asked to cut at a specific instant, and silently cutting somewhere else is worse than
/// not cutting.
/// </remarks>
public class SplitAudioTrackOperation : IEditOperation
{
    private readonly string _trackId;
    private readonly TimeSpan _splitAt;

    /// <summary>
    /// Id the right half will always be given, allocated once at construction.
    /// </summary>
    /// <remarks>
    /// Minting it inside <see cref="Execute"/> would give the right half a DIFFERENT id on
    /// every redo, so a selection (or anything else holding the id from the first split)
    /// would silently stop resolving after undo/redo. Every sibling operation that creates
    /// something — <see cref="AddAudioTrackOperation"/>, <c>AddCameraSegmentOperation</c> —
    /// likewise fixes the created object's identity up front rather than per execution.
    /// </remarks>
    private readonly string _rightHalfId = Guid.NewGuid().ToString("N");

    private AudioTrack? _originalState;
    private AudioTrack? _createdRightHalf;

    /// <summary>Id of the right half, or null when the split was rejected.</summary>
    public string? CreatedId => _createdRightHalf?.Id;

    public string Description => "Split Audio Track";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public SplitAudioTrackOperation(string trackId, TimeSpan splitAtOutputTime)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _splitAt = splitAtOutputTime;
    }

    /// <summary>
    /// Whether <paramref name="splitAt"/> falls far enough inside <paramref name="track"/>
    /// for both halves to survive. Exposed so the UI can hide/disable a Split action instead
    /// of offering one that will silently do nothing.
    /// </summary>
    public static bool CanSplit(AudioTrack track, TimeSpan splitAt)
    {
        ArgumentNullException.ThrowIfNull(track);
        var min = AudioTrackEditing.MinDuration;
        return splitAt >= track.StartTime + min && splitAt <= track.End - min;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null || !CanSplit(track, _splitAt)) { _changed = false; return; }

        _originalState = track.Clone();

        var leftDuration = _splitAt - track.StartTime;
        var rightDuration = track.End - _splitAt;

        var right = track.Clone();
        right.Id = _rightHalfId;
        right.StartTime = _splitAt;
        right.TrimStart = track.TrimStart + leftDuration;
        right.Duration = rightDuration;

        track.Duration = leftDuration;

        _createdRightHalf = right;
        model.AudioTracks.Add(right);
        AudioTrackEditing.Sort(model.AudioTracks);
    }

    public void Undo(TimelineModel model)
    {
        if (_originalState is null) return;

        if (_createdRightHalf is not null)
            model.AudioTracks.RemoveAll(t => t.Id == _createdRightHalf.Id);

        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is not null)
        {
            track.StartTime = _originalState.StartTime;
            track.TrimStart = _originalState.TrimStart;
            track.Duration = _originalState.Duration;
        }

        AudioTrackEditing.Sort(model.AudioTracks);
    }
}

/// <summary>Removes an inserted track.</summary>
/// <remarks>
/// The normalised WAV is deliberately left on disk: undo has to be able to bring the track
/// back, and the orphan sweep reclaims import folders no saved project references.
/// </remarks>
public class RemoveAudioTrackOperation : IEditOperation
{
    private readonly string _trackId;
    private AudioTrack? _removed;

    public string Description => "Remove Audio Track";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public RemoveAudioTrackOperation(string trackId)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        _changed = _removed is not null;
        if (_removed is not null)
            model.AudioTracks.RemoveAll(t => t.Id == _trackId);
    }

    public void Undo(TimelineModel model)
    {
        if (_removed is null) return;
        model.AudioTracks.Add(_removed);
        AudioTrackEditing.Sort(model.AudioTracks);
    }
}

/// <summary>Updates an inserted track's mute state and/or volume.</summary>
public class UpdateAudioTrackPropertiesOperation : IEditOperation
{
    private readonly string _trackId;
    private readonly bool? _isMuted;
    private readonly double? _volume;
    private bool _previousIsMuted;
    private double _previousVolume;

    public string Description => "Update Audio Track";

    private bool _changed = true;
    /// <inheritdoc />
    public bool ChangedModel => _changed;

    public UpdateAudioTrackPropertiesOperation(string trackId, bool? isMuted = null, double? volume = null)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _isMuted = isMuted;
        _volume = volume;
    }

    public void Execute(TimelineModel model)
    {
        _changed = true;
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) { _changed = false; return; }

        _previousIsMuted = track.IsMuted;
        _previousVolume = track.Volume;

        if (_isMuted is { } muted) track.IsMuted = muted;
        if (_volume is { } volume) track.Volume = Math.Clamp(volume, 0.0, 1.0);
    }

    public void Undo(TimelineModel model)
    {
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

        track.IsMuted = _previousIsMuted;
        track.Volume = _previousVolume;
    }
}
