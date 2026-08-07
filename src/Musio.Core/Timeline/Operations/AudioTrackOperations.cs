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

    public MoveAudioTrackOperation(string trackId, TimeSpan newStart)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _newStart = newStart;
    }

    public void Execute(TimelineModel model)
    {
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

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
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

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
    private AudioTrack? _originalState;
    private AudioTrack? _createdRightHalf;

    /// <summary>Id of the right half, or null when the split was rejected.</summary>
    public string? CreatedId => _createdRightHalf?.Id;

    public string Description => "Split Audio Track";

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
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null || !CanSplit(track, _splitAt)) return;

        _originalState = track.Clone();

        var leftDuration = _splitAt - track.StartTime;
        var rightDuration = track.End - _splitAt;

        var right = track.Clone();
        right.Id = Guid.NewGuid().ToString("N");
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

    public RemoveAudioTrackOperation(string trackId)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
    }

    public void Execute(TimelineModel model)
    {
        _removed = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
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

    public UpdateAudioTrackPropertiesOperation(string trackId, bool? isMuted = null, double? volume = null)
    {
        _trackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        _isMuted = isMuted;
        _volume = volume;
    }

    public void Execute(TimelineModel model)
    {
        var track = model.AudioTracks.FirstOrDefault(t => t.Id == _trackId);
        if (track is null) return;

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
