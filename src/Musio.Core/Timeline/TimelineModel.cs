using System.Text.Json.Serialization;
using Musio.Core.Audio;
using Musio.Core.Models;
using Musio.Core.Processing;

namespace Musio.Core.Timeline;

/// <summary>
/// The editable state of a project's timeline. Serialized verbatim into a
/// <c>.musio</c> package, so its collection properties are populated in place on
/// deserialization rather than replaced.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class TimelineModel
{
    /// <summary>
    /// The base track is the only track that keeps the historical contiguous reflow behaviour;
    /// higher tracks are absolute-time overlays that cover, rather than shift, the base edit.
    /// </summary>
    public const int BaseTrackIndex = 0;

    /// <summary>
    /// Ruler length an empty editor shows. A zero-length timeline collapses every track draw
    /// (they all return early on a non-positive <see cref="DisplayDuration"/>), which leaves the
    /// editor looking broken rather than merely empty, so the empty state is given a nominal
    /// span to lay its placeholder out in.
    /// </summary>
    public static readonly TimeSpan EmptyDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timeline an editor with no project shows. Shared so that "nothing recorded yet" and
    /// "the last clip was just deleted" are the same state rather than two that drift apart —
    /// the second used to produce a zero-length model, and therefore a blank editor instead of
    /// the inviting empty one.
    /// </summary>
    public static TimelineModel CreateEmpty() => new()
    {
        Duration = EmptyDuration,
        TrimEnd = EmptyDuration,
    };

    public TimeSpan Duration { get; set; }
    public int Fps { get; set; } = 30;
    public TimeSpan PlayheadPosition { get; set; }
    public double ZoomLevel { get; set; } = 1.0; // 1.0 = fit to width
    public double ScrollOffset { get; set; } // horizontal scroll in seconds

    public List<TimelineClip> Clips { get; } = [];
    public List<ZoomKeyframe> ZoomKeyframes { get; } = [];
    public List<SpeedSegment> SpeedSegments { get; } = [];

    /// <summary>
    /// User-authored overrides of where the recorded pointer is at particular instants (see
    /// <see cref="CursorAnchor"/>). Each anchor's <see cref="CursorAnchor.Timestamp"/> is a
    /// SOURCE-video time, so anchors stay attached to the footage through trims and reorders,
    /// and <see cref="CursorAnchor.SourceVideoFilePath"/> scopes them to one recording.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ZoomKeyframes"/>, these are deliberately NOT counted as timeline
    /// content by <see cref="HasNonSegmentContent"/>: an anchor decorates footage rather than
    /// standing on its own, so a timeline holding nothing but anchors has nothing left to
    /// decorate and is still empty.
    /// </remarks>
    public List<CursorAnchor> CursorAnchors { get; } = [];

    /// <summary>
    /// Ordered list of segments on the output timeline.
    /// Base-track segments are reflowed contiguously in list order; overlay-track segments keep
    /// absolute starts and render above lower tracks. When empty, the legacy
    /// <see cref="Clips"/> / trim-based pipeline is used.
    /// </summary>
    public List<TimelineSegment> Segments { get; } = [];

    /// <summary>
    /// Full-frame segments on the contiguous base track, in list order. This is the timeline
    /// chain used for transitions and source↔output mapping so overlay inserts never destabilize
    /// cursor, click, or zoom visualizations tied to the original recording.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<TimelineSegment> BaseSegments => Segments.Where(s => s.TrackIndex == BaseTrackIndex);

    /// <summary>
    /// Full-frame overlay segments ordered by visual lane and absolute start; overlays are
    /// independent of the base-track reflow and may intentionally overlap.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<TimelineSegment> OverlaySegments =>
        Segments.Where(s => s.TrackIndex > BaseTrackIndex)
            .OrderBy(s => s.TrackIndex)
            .ThenBy(s => s.Start);

    /// <summary>
    /// Number of full-frame video lanes that need to be displayed. It is always at least one
    /// so an empty or legacy project still has a base lane.
    /// </summary>
    [JsonIgnore]
    public int VideoTrackCount =>
        Segments.Count == 0
            ? 1
            : Math.Max(1, Segments.Max(s => s.TrackIndex) + 1);

    /// <summary>
    /// Camera (webcam) overlay segments on the independent camera track. Each
    /// segment's <see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/>
    /// are interpreted as a <b>source-video</b> time range, so they stay aligned with
    /// the recording (and survive video reorder/trim) via the source↔output mapping.
    /// When empty, the legacy single global webcam overlay behaviour is used.
    /// </summary>
    public List<CameraSegment> CameraSegments { get; } = [];

    /// <summary>
    /// Animated text overlays drawn on top of the video on the independent text track.
    /// Each segment's <see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/>
    /// are interpreted as a <b>source-video</b> time range (same convention as
    /// <see cref="CameraSegments"/>), so they stay aligned with the recording (and survive
    /// video reorder/trim) via the source↔output mapping. When empty, no overlays are drawn.
    /// </summary>
    public List<TextOverlaySegment> TextOverlays { get; } = [];

    /// <summary>
    /// Inserted voice-over and music tracks (see <see cref="AudioTrack"/>) on the two
    /// independent audio lanes.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CameraSegments"/> and <see cref="TextOverlays"/>, whose ranges are
    /// SOURCE-video times, an inserted track's <see cref="AudioTrack.StartTime"/> is an
    /// OUTPUT-timeline time. That difference is the feature: a camera overlay decorates the
    /// footage and must follow it through trims and reorders, where a voice-over is pinned
    /// to a moment in the finished video and must NOT move when the footage under it changes.
    /// </remarks>
    public List<AudioTrack> AudioTracks { get; } = [];

    /// <summary>
    /// Returns every enabled text overlay whose source range contains <paramref name="sourceTime"/>
    /// and which belongs to <paramref name="videoFilePath"/>'s source-time space. Overlays are
    /// returned in timeline order so stacking is deterministic.
    /// </summary>
    public List<TextOverlaySegment> GetActiveTextOverlays(TimeSpan sourceTime, string? videoFilePath)
    {
        var result = new List<TextOverlaySegment>();
        foreach (var overlay in TextOverlays)
        {
            if (!overlay.Enabled) continue;
            if (sourceTime < overlay.Start || sourceTime >= overlay.End) continue;

            bool belongsToSource = overlay.SourceVideoFilePath is null
                ? PrimaryVideoFilePath is null
                  || string.Equals(videoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(videoFilePath, overlay.SourceVideoFilePath, StringComparison.OrdinalIgnoreCase);

            if (belongsToSource)
                result.Add(overlay);
        }
        return result;
    }

    /// <summary>
    /// Computes normalized progress (0..1) through <paramref name="overlay"/> at
    /// <paramref name="sourceTime"/>, used to drive its entrance/exit animation.
    /// Returns 0 for a zero-length overlay.
    /// </summary>
    public static double GetTextOverlayProgress(TextOverlaySegment overlay, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        double dur = overlay.Duration.TotalSeconds;
        if (dur <= 0) return 0.0;

        double t = (sourceTime - overlay.Start).TotalSeconds / dur;
        return Math.Clamp(t, 0.0, 1.0);
    }

    /// <summary>
    /// Returns the enabled camera segment whose source range contains
    /// <paramref name="sourceTime"/>, or null when none is active.
    /// </summary>
    public CameraSegment? GetCameraSegmentAtSourceTime(TimeSpan sourceTime)
    {
        foreach (var seg in CameraSegments)
        {
            if (seg.Enabled && sourceTime >= seg.Start && sourceTime < seg.End)
                return seg;
        }
        return null;
    }

    /// <summary>
    /// Computes the camera overlay opacity for the <paramref name="active"/> segment at
    /// <paramref name="sourceTime"/>, suppressing the start/end fade where it would cause
    /// a visible artifact: no fade-in when another enabled camera segment covers the
    /// instant just before this one (adjacent/overlapping → avoids a flash), no fade-out
    /// when one continues right after, and no fade-in when the camera starts fullscreen
    /// (→ avoids a fade from black).
    /// </summary>
    public float GetCameraOverlayOpacity(CameraSegment active, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(active);

        bool hasPredecessor = CameraSegments.Any(s => !ReferenceEquals(s, active) && s.Enabled
            && s.Start < active.Start && s.End >= active.Start);
        bool hasSuccessor = CameraSegments.Any(s => !ReferenceEquals(s, active) && s.Enabled
            && s.End > active.End && s.Start <= active.End);

        bool fadeIn = !hasPredecessor && !active.StartsFullscreen;
        bool fadeOut = !hasSuccessor;
        return active.ComputeAppearOpacity(sourceTime, fadeIn, fadeOut);
    }

    /// <summary>
    /// Path of the primary recording's video file. Used to identify which
    /// video segments carry the cursor/zoom data so source↔output time mapping
    /// only considers the primary recording's segments.
    /// </summary>
    public string? PrimaryVideoFilePath { get; set; }

    /// <summary>
    /// Total output duration computed from the furthest segment end across all tracks.
    /// Falls back to <see cref="EffectiveDuration"/> when no segments exist.
    /// </summary>
    [JsonIgnore]
    public TimeSpan TotalSegmentsDuration =>
        Segments.Count > 0
            ? Segments.Aggregate(TimeSpan.Zero, (acc, s) => s.End > acc ? s.End : acc)
            : EffectiveDuration;

    // Trim handles
    public TimeSpan TrimStart { get; set; }
    public TimeSpan TrimEnd { get; set; } // default = Duration

    // Cursor recording data for cursor-path visualization track.
    // Reloaded from the recording's .mcur file, so it is never persisted.
    [JsonIgnore]
    public MouseRecordingData? CursorData { get; set; }

    /// <summary>
    /// Source click ticks whose auto-zoom segments have been suppressed (deleted or
    /// converted to manual by the user). Persisted so the suppression survives
    /// undo/redo and is applied during both preview and export.
    /// </summary>
    public HashSet<long> SuppressedClickTicks { get; } = [];

    /// <summary>
    /// Time offset in seconds between mouse recording start and video frame 0.
    /// Used to align cursor-path and click visualizations with the video track.
    /// </summary>
    public double MouseToVideoOffsetSeconds { get; set; }

    // Audio waveform peak samples (normalized 0..1) for waveform rendering.
    // A regenerable render cache — recomputed from the WAV files on load rather than
    // bloating the project file with thousands of floats.
    [JsonIgnore]
    public float[]? SystemAudioWaveformSamples { get; set; }
    [JsonIgnore]
    public float[]? MicAudioWaveformSamples { get; set; }

    // Audio track mute state
    public bool IsSystemAudioMuted { get; set; }
    public bool IsMicAudioMuted { get; set; }

    // Per-channel playback gain, 0..1. Persisted alongside the mute flags: together they are
    // the project's whole audio mix, and losing either reopens the project at the wrong level.
    public double SystemAudioVolume { get; set; } = 1.0;
    public double MicAudioVolume { get; set; } = 1.0;

    /// <summary>Mute state of the inserted voice-over lane (independent of each clip's own).</summary>
    public bool IsVoiceOverMuted { get; set; }
    public double VoiceOverVolume { get; set; } = 1.0;

    /// <summary>Mute state of the inserted music lane (independent of each clip's own).</summary>
    public bool IsMusicMuted { get; set; }
    public double MusicVolume { get; set; } = 1.0;

    /// <summary>Whether <paramref name="channel"/> is muted.</summary>
    public bool IsMuted(AudioMixChannel channel) => channel switch
    {
        AudioMixChannel.System => IsSystemAudioMuted,
        AudioMixChannel.Mic => IsMicAudioMuted,
        AudioMixChannel.VoiceOver => IsVoiceOverMuted,
        AudioMixChannel.Music => IsMusicMuted,
        _ => false,
    };

    /// <summary>The stored gain for <paramref name="channel"/>, ignoring mute.</summary>
    public double GetVolume(AudioMixChannel channel) => channel switch
    {
        AudioMixChannel.System => SystemAudioVolume,
        AudioMixChannel.Mic => MicAudioVolume,
        AudioMixChannel.VoiceOver => VoiceOverVolume,
        AudioMixChannel.Music => MusicVolume,
        _ => 1.0,
    };

    public void SetMuted(AudioMixChannel channel, bool muted)
    {
        switch (channel)
        {
            case AudioMixChannel.System: IsSystemAudioMuted = muted; break;
            case AudioMixChannel.Mic: IsMicAudioMuted = muted; break;
            case AudioMixChannel.VoiceOver: IsVoiceOverMuted = muted; break;
            case AudioMixChannel.Music: IsMusicMuted = muted; break;
        }
    }

    public void SetVolume(AudioMixChannel channel, double volume)
    {
        volume = Math.Clamp(volume, 0.0, 1.0);
        switch (channel)
        {
            case AudioMixChannel.System: SystemAudioVolume = volume; break;
            case AudioMixChannel.Mic: MicAudioVolume = volume; break;
            case AudioMixChannel.VoiceOver: VoiceOverVolume = volume; break;
            case AudioMixChannel.Music: MusicVolume = volume; break;
        }
    }

    /// <summary>
    /// The gain to actually apply for <paramref name="channel"/>: the stored volume with mute
    /// folded in and clamped to 0..1.
    /// </summary>
    /// <remarks>
    /// The single answer to "how loud is this channel", used by the preview engine, the export
    /// plan and the label flyouts alike — so mute can never be honoured in one place and
    /// ignored in another, which is exactly what happened while export filtered muted files by
    /// name in the view model instead.
    /// </remarks>
    public double EffectiveVolume(AudioMixChannel channel)
        => IsMuted(channel) ? 0.0 : Math.Clamp(GetVolume(channel), 0.0, 1.0);

    // Get the effective (trimmed) duration
    [JsonIgnore]
    public TimeSpan EffectiveDuration => TrimEnd - TrimStart;

    /// <summary>
    /// Content the user placed on the timeline that is NOT a segment: audio tracks, text
    /// overlays, camera segments, and legacy clips.
    /// </summary>
    /// <remarks>
    /// Deleting the last video segment does not mean the timeline is finished with — a music
    /// bed, a voice-over, an overlay or a camera track can easily outlive every clip, and each
    /// one is persisted work. Anything that reacts to "the timeline is empty now" has to ask
    /// about these too, or it silently discards them.
    /// <para>
    /// Zoom keyframes and <see cref="CursorAnchors"/> are deliberately EXCLUDED. Both decorate
    /// footage rather than standing on their own: with no segments left there is nothing to
    /// zoom into and no cursor to reposition, so a timeline holding only those is still empty.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool HasNonSegmentContent =>
        Clips.Count > 0
        || AudioTracks.Count > 0
        || TextOverlays.Count > 0
        || CameraSegments.Count > 0;

    /// <summary>
    /// True when the timeline holds nothing at all — no segments and no
    /// <see cref="HasNonSegmentContent"/>. The single definition of "empty", shared by the
    /// editor's empty-state call-out, the reset-to-zero-state path, and the "treat this as no
    /// project yet" rule that lets the next recording become the primary.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => Segments.Count == 0 && !HasNonSegmentContent;

    /// <summary>
    /// The total display duration for the timeline ruler and coordinate system.
    /// Uses segment duration when segments exist, otherwise falls back to source Duration.
    /// </summary>
    [JsonIgnore]
    public TimeSpan DisplayDuration =>
        Segments.Count > 0 ? TotalSegmentsDuration : Duration;

    /// <summary>
    /// Recalculates <see cref="TimelineSegment.Start"/> only for base-track segments so they
    /// remain contiguous, while overlay tracks keep their authored absolute starts. Call after
    /// segment edits so the legacy base chain stays magnetic without turning overlays into
    /// accidental ripple edits.
    /// </summary>
    public void RecalculateSegmentPositions()
    {
        var position = TimeSpan.Zero;
        foreach (var segment in Segments)
        {
            if (segment.TrackIndex == BaseTrackIndex)
            {
                segment.Start = position;
                position += segment.Duration;
            }
            else if (segment.Start < TimeSpan.Zero)
            {
                segment.Start = TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Finds the topmost full-frame segment for a given output time position. Higher tracks win,
    /// and segments on the same track use later list position as the tie-break so z-order is
    /// deterministic without a second stacking property.
    /// </summary>
    public (TimelineSegment? Segment, TimeSpan LocalOffset) GetSegmentAtTime(TimeSpan outputTime)
    {
        TimelineSegment? best = null;
        int bestTrack = int.MinValue;

        foreach (var segment in Segments)
        {
            if (outputTime >= segment.Start && outputTime < segment.End)
            {
                if (best is null || segment.TrackIndex >= bestTrack)
                {
                    best = segment;
                    bestTrack = segment.TrackIndex;
                }
            }
        }

        if (best is not null)
            return (best, outputTime - best.Start);

        // Past the end of every segment: fall back to whichever segment ends LAST, so a
        // playhead parked at the timeline end resolves to the clip that actually finishes
        // there. List position is not a proxy for that any more — overlay inserts append to
        // the list, so `Segments[^1]` is routinely a short clip sitting in the middle.
        TimelineSegment? latest = null;
        foreach (var segment in Segments)
        {
            if (latest is null
                || segment.End > latest.End
                || (segment.End == latest.End && segment.TrackIndex >= latest.TrackIndex))
            {
                latest = segment;
            }
        }

        if (latest is not null && outputTime >= latest.End)
            return (latest, latest.Duration);

        return (null, TimeSpan.Zero);
    }

    /// <summary>
    /// Finds the base-track segment and local offset for an output time. Transitions and
    /// source↔output mapping use this contiguous chain so overlay-only edits cannot perturb
    /// source-time visualizations.
    /// </summary>
    public (TimelineSegment? Segment, TimeSpan LocalOffset) GetBaseSegmentAtTime(TimeSpan outputTime)
    {
        TimelineSegment? lastBase = null;
        foreach (var segment in Segments)
        {
            if (segment.TrackIndex != BaseTrackIndex) continue;
            lastBase = segment;
            if (outputTime >= segment.Start && outputTime < segment.End)
                return (segment, outputTime - segment.Start);
        }

        if (lastBase is not null && outputTime >= lastBase.End)
            return (lastBase, lastBase.Duration);
        return (null, TimeSpan.Zero);
    }

    /// <summary>
    /// Returns true when a strictly higher full-frame track hides <paramref name="segment"/> at
    /// <paramref name="outputTime"/>. This shared visibility rule is consumed by preview/export
    /// callers so they agree on which layer is actually visible.
    /// </summary>
    public bool IsCoveredByHigherTrack(TimelineSegment segment, TimeSpan outputTime)
    {
        ArgumentNullException.ThrowIfNull(segment);

        foreach (var candidate in Segments)
        {
            if (candidate.TrackIndex <= segment.TrackIndex) continue;
            if (outputTime >= candidate.Start && outputTime < candidate.End)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the absolute output-time sub-ranges of <paramref name="segment"/> that are not
    /// covered by any higher full-frame track. Covers are coalesced before subtraction so
    /// overlapping overlays do not create duplicate or inverted visible spans.
    /// </summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> VisibleRanges(TimelineSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var segmentStart = segment.Start;
        var segmentEnd = segment.End;
        if (segmentEnd <= segmentStart)
            return [];

        var covers = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var candidate in Segments)
        {
            if (candidate.TrackIndex <= segment.TrackIndex) continue;
            var coverStart = Max(segmentStart, candidate.Start);
            var coverEnd = Min(segmentEnd, candidate.End);
            if (coverStart < coverEnd)
                covers.Add((coverStart, coverEnd));
        }

        if (covers.Count == 0)
            return [(segmentStart, segmentEnd)];

        covers.Sort(static (a, b) =>
        {
            int start = a.Start.CompareTo(b.Start);
            return start != 0 ? start : a.End.CompareTo(b.End);
        });

        var merged = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var cover in covers)
        {
            if (merged.Count == 0 || cover.Start > merged[^1].End)
            {
                merged.Add(cover);
                continue;
            }

            if (cover.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, cover.End);
        }

        var visible = new List<(TimeSpan Start, TimeSpan End)>();
        var cursor = segmentStart;
        foreach (var cover in merged)
        {
            if (cover.Start > cursor)
                visible.Add((cursor, cover.Start));
            if (cover.End > cursor)
                cursor = cover.End;
        }

        if (cursor < segmentEnd)
            visible.Add((cursor, segmentEnd));
        return visible;
    }

    /// <summary>
    /// Finds the first overlay track whose half-open range does not intersect an existing
    /// segment on that track. Touching endpoints are allowed so adjacent overlays do not burn
    /// extra lanes.
    /// </summary>
    public int FindFreeOverlayTrack(TimeSpan start, TimeSpan duration)
    {
        var rangeStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        var rangeEnd = rangeStart + (duration < TimeSpan.Zero ? TimeSpan.Zero : duration);

        for (int track = 1; ; track++)
        {
            bool collides = false;
            foreach (var segment in Segments)
            {
                if (segment.TrackIndex != track) continue;
                if (RangesIntersect(rangeStart, rangeEnd, segment.Start, segment.End))
                {
                    collides = true;
                    break;
                }
            }

            if (!collides)
                return track;
        }
    }

    /// <summary>
    /// Primary recording's video segments (those carrying cursor/zoom data),
    /// in their base-track order on the output timeline. Overlay video segments are excluded
    /// because they cover the edit visually but do not participate in source↔output mapping for
    /// cursor, click, or zoom visualization stability.
    /// </summary>
    private IEnumerable<VideoSegment> PrimaryVideoSegments =>
        BaseSegments.OfType<VideoSegment>()
            .Where(v => PrimaryVideoFilePath is null ||
                        string.Equals(v.VideoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a source-video time (e.g. a zoom keyframe or cursor timestamp) to its
    /// position on the output timeline, accounting for inserted text slides and
    /// reordered/trimmed base-track video segments. Overlay segments intentionally do not
    /// participate. Identity when no segments exist.
    /// Unlike <see cref="TrySourceToOutputTime"/>, this always returns a value,
    /// falling back to the nearest kept segment boundary when the source time has
    /// been trimmed out of the timeline.
    /// </summary>
    public TimeSpan SourceToOutputTime(TimeSpan sourceTime)
    {
        if (Segments.Count == 0) return sourceTime;
        if (TrySourceToOutputTime(sourceTime, out var output))
            return output;

        // Source time is not present in any kept segment (trimmed out). Map it to
        // the nearest kept primary segment boundary so visualizations clamp sanely.
        VideoSegment? nearest = null;
        double nearestDist = double.MaxValue;
        bool nearestBefore = false;
        foreach (var v in PrimaryVideoSegments)
        {
            var srcStart = v.SourceStart;
            var srcEnd = v.SourceStart + v.SourceDuration;
            bool before = sourceTime < srcStart;
            double dist = before
                ? (srcStart - sourceTime).TotalSeconds
                : (sourceTime - srcEnd).TotalSeconds;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = v;
                nearestBefore = before;
            }
        }

        if (nearest is null) return sourceTime;
        return nearestBefore ? nearest.Start : nearest.End;
    }

    /// <summary>
    /// Attempts to map a source-video time to its position on the output timeline,
    /// walking base-track segments in their actual timeline order. Returns <c>false</c> when
    /// the source time falls outside every kept primary video segment (i.e. it was
    /// trimmed out and has no corresponding output position). Callers that draw
    /// source-time visualizations (zoom/click/cursor) should skip rendering when
    /// this returns <c>false</c>.
    /// </summary>
    public bool TrySourceToOutputTime(TimeSpan sourceTime, out TimeSpan outputTime)
    {
        if (Segments.Count == 0)
        {
            outputTime = sourceTime;
            return true;
        }

        foreach (var v in PrimaryVideoSegments)
        {
            var srcStart = v.SourceStart;
            var srcEnd = v.SourceStart + v.SourceDuration;

            if (sourceTime >= srcStart && sourceTime <= srcEnd)
            {
                var localSrc = sourceTime - srcStart;
                var localOut = v.SpeedFactor != 0
                    ? TimeSpan.FromTicks((long)(localSrc.Ticks / v.SpeedFactor))
                    : localSrc;
                outputTime = v.Start + localOut;
                return true;
            }
        }

        outputTime = sourceTime;
        return false;
    }

    /// <summary>
    /// Maps <paramref name="sourceTime"/> through the occurrence of a recording identified
    /// by <paramref name="owner"/>. Times outside the owner may cross directly adjacent
    /// pieces only when those pieces are contiguous in source, output, track, and list order.
    /// </summary>
    /// <remarks>
    /// This preserves occurrence ownership for duplicated/reordered recordings while allowing
    /// a zoom or overlay span to cross the 1x/1.5x pieces created by automatic typing speed.
    /// If no contiguous piece contains the time, the result deliberately falls back to the
    /// historical owner extrapolation so slightly overflowing authored ranges retain their
    /// width instead of collapsing onto an unrelated occurrence of the same source file.
    /// </remarks>
    public TimeSpan MapSourceTimeFromOwningSegment(VideoSegment owner, TimeSpan sourceTime)
    {
        ArgumentNullException.ThrowIfNull(owner);

        int ownerIndex = Segments.FindIndex(s => s.Id == owner.Id);
        if (ownerIndex < 0)
            return MapWithin(owner, sourceTime);

        VideoSegment target = owner;
        if (sourceTime < owner.SourceStart)
        {
            var current = owner;
            for (int i = ownerIndex - 1; i >= 0; i--)
            {
                if (Segments[i] is not VideoSegment previous
                    || !AreContiguousSourcePieces(previous, current))
                {
                    break;
                }

                current = previous;
                if (ContainsSourceTime(current, sourceTime))
                {
                    target = current;
                    break;
                }
            }
        }
        else if (sourceTime > owner.SourceStart + owner.SourceDuration)
        {
            var current = owner;
            for (int i = ownerIndex + 1; i < Segments.Count; i++)
            {
                if (Segments[i] is not VideoSegment next
                    || !AreContiguousSourcePieces(current, next))
                {
                    break;
                }

                current = next;
                if (ContainsSourceTime(current, sourceTime))
                {
                    target = current;
                    break;
                }
            }
        }

        return MapWithin(target, sourceTime);
    }

    private static bool ContainsSourceTime(VideoSegment segment, TimeSpan sourceTime) =>
        sourceTime >= segment.SourceStart
        && sourceTime <= segment.SourceStart + segment.SourceDuration;

    /// <summary>
    /// Whether <paramref name="sourceVideoFilePath"/> names the same recording as
    /// <paramref name="segment"/>. Null means the primary recording — the convention shared
    /// by <see cref="ZoomKeyframe.SourceVideoFilePath"/> and
    /// <see cref="TextOverlaySegment.SourceVideoFilePath"/>.
    /// </summary>
    public bool SourceMatchesSegment(string? sourceVideoFilePath, VideoSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return sourceVideoFilePath is null
            ? PrimaryVideoFilePath is null
              || string.Equals(segment.VideoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(segment.VideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> actually SHOWS any part of a source-time span —
    /// same recording, and the FULL span overlaps the footage the segment keeps. This is the
    /// test for "does this authored thing still have a home on the timeline"; anything anchored
    /// to a source time (zoom keyframes, text overlays) resolves its position through a segment
    /// that shows it, and has no meaningful position when none does.
    /// </summary>
    public bool SourceSpanIntersectsSegment(
        string? sourceVideoFilePath, TimeSpan spanStart, TimeSpan spanEnd, VideoSegment segment)
    {
        if (!SourceMatchesSegment(sourceVideoFilePath, segment)) return false;

        // A degenerate (empty) span — e.g. the zero-duration probe the text-overlay create
        // preview maps through — has no width to overlap with; test it as the point it is.
        if (spanEnd <= spanStart) return ContainsSourceTime(segment, spanStart);

        var srcStart = segment.SourceStart;
        var srcEnd = segment.SourceStart + segment.SourceDuration;
        return spanEnd > srcStart && spanStart < srcEnd;
    }

    /// <summary>
    /// Whether <paramref name="segment"/> shows any part of <paramref name="keyframe"/>. The
    /// anchor <see cref="ZoomKeyframe.Timestamp"/> counts on its own too, so a keyframe whose
    /// eases overflow the segment it is anchored in still belongs there.
    /// </summary>
    public bool ZoomKeyframeIntersects(ZoomKeyframe keyframe, VideoSegment segment)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        ArgumentNullException.ThrowIfNull(segment);

        return SourceSpanIntersectsSegment(
                   keyframe.SourceVideoFilePath, keyframe.Start, keyframe.End, segment)
            || (SourceMatchesSegment(keyframe.SourceVideoFilePath, segment)
                && ContainsSourceTime(segment, keyframe.Timestamp));
    }

    /// <summary>
    /// Whether any video segment still on the timeline shows <paramref name="keyframe"/>.
    /// A keyframe that no segment shows is unreachable: it renders nowhere sensible (its
    /// source time maps through no kept range) and affects no exported frame.
    /// </summary>
    public bool IsZoomKeyframeShown(ZoomKeyframe keyframe)
    {
        foreach (var segment in Segments.OfType<VideoSegment>())
        {
            if (ZoomKeyframeIntersects(keyframe, segment)) return true;
        }
        return false;
    }

    private static bool AreContiguousSourcePieces(VideoSegment left, VideoSegment right) =>
        left.TrackIndex == right.TrackIndex
        && string.Equals(left.VideoFilePath, right.VideoFilePath, StringComparison.OrdinalIgnoreCase)
        && left.SourceStart + left.SourceDuration == right.SourceStart
        && left.End == right.Start;

    private static TimeSpan MapWithin(VideoSegment segment, TimeSpan sourceTime)
    {
        var localSource = sourceTime - segment.SourceStart;
        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        return segment.Start + TimeSpan.FromTicks((long)(localSource.Ticks / speed));
    }

    /// <summary>
    /// Inverse of <see cref="SourceToOutputTime"/>: maps an output-timeline time
    /// to the corresponding base-track source-video time. Returns null when the output time
    /// falls on a text slide (no underlying source frame); overlay segments are deliberately
    /// ignored so visualizations remain pinned to the base recording chain.
    /// </summary>
    public TimeSpan? OutputToSourceTime(TimeSpan outputTime)
    {
        if (Segments.Count == 0) return outputTime;

        foreach (var segment in BaseSegments)
        {
            if (outputTime >= segment.Start && outputTime < segment.End)
            {
                if (segment is VideoSegment v &&
                    (PrimaryVideoFilePath is null ||
                     string.Equals(v.VideoFilePath, PrimaryVideoFilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var localOut = outputTime - segment.Start;
                    return v.SourceStart + TimeSpan.FromTicks((long)(localOut.Ticks * v.SpeedFactor));
                }
                return null; // text slide or non-primary segment
            }
        }
        return null;
    }

    private static bool RangesIntersect(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd) =>
        aStart < bEnd && bStart < aEnd;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Finds the video segment among <paramref name="candidates"/> whose start or end
    /// (on the output timeline) is closest to <paramref name="outputTime"/>, and reports
    /// whether the nearer edge is the segment's start. Used by timeline-gesture code
    /// (camera/zoom create, move, resize) to clamp a pointer gesture onto a valid
    /// boundary when the output time under the pointer is unmappable for the relevant
    /// source/track (e.g. it lands on a text slide, or a video segment for a different
    /// recording) — instead of falling back to raw output time, which would mix output
    /// time into a source-time domain. Returns <c>(null, false)</c> when
    /// <paramref name="candidates"/> is empty.
    /// </summary>
    public static (VideoSegment? Segment, bool AtStart) NearestVideoSegmentEdge(
        IEnumerable<VideoSegment> candidates, TimeSpan outputTime)
    {
        VideoSegment? nearest = null;
        bool atStart = false;
        double bestDist = double.MaxValue;
        foreach (var seg in candidates)
        {
            double distToStart = Math.Abs((outputTime - seg.Start).TotalSeconds);
            if (distToStart < bestDist)
            {
                bestDist = distToStart;
                nearest = seg;
                atStart = true;
            }
            double distToEnd = Math.Abs((outputTime - seg.End).TotalSeconds);
            if (distToEnd < bestDist)
            {
                bestDist = distToEnd;
                nearest = seg;
                atStart = false;
            }
        }
        return (nearest, atStart);
    }
}

public record TimelineClip(TimeSpan Start, TimeSpan End, string Label)
{
    /// <summary>Speed factor applied to this clip. 1.0 = normal, 2.0 = 2x fast, 0.5 = half speed.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>
    /// Original source time position for the first frame of this clip.
    /// Used by the mapper when speed changes have shifted clip boundaries.
    /// When null, defaults to Start (no speed adjustment has occurred).
    /// </summary>
    public TimeSpan? SourceStart { get; init; }

    /// <summary>The effective source start position.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveSourceStart => SourceStart ?? Start;

    /// <summary>Source duration (how much source content this clip represents).</summary>
    [JsonIgnore]
    public TimeSpan SourceDuration
    {
        get
        {
            double ticks = (End - Start).Ticks * SpeedFactor;
            if (double.IsNaN(ticks) || double.IsInfinity(ticks))
                return End - Start;
            ticks = Math.Clamp(ticks, long.MinValue, long.MaxValue);
            return TimeSpan.FromTicks((long)ticks);
        }
    }
}

public record ZoomKeyframe
{
    /// <summary>Minimum total segment duration to prevent degenerate segments.</summary>
    public static readonly TimeSpan MinSegmentDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>Stable identity for selection and undo/redo tracking.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TimeSpan Timestamp { get; init; }
    public double ZoomLevel { get; init; } = 2.0;
    public double CenterX { get; init; } // normalized 0-1
    public double CenterY { get; init; } // normalized 0-1
    public TimeSpan PreDuration { get; init; } = TimeSpan.FromMilliseconds(1000);  // graceful anticipatory zoom-in
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMilliseconds(1333); // settled dwell on the focal point
    public TimeSpan PostDuration { get; init; } = TimeSpan.FromMilliseconds(1556); // slow, elegant release back to full frame

    /// <summary>
    /// Per-shot multiplier for continuous camera drift. Automatic typing zooms lower this
    /// while keeping drift enabled so the insertion caret retains a generous safety margin.
    /// </summary>
    public double DriftScale { get; init; } = 1.0;

    /// <summary>
    /// Camera drift for THIS zoom segment. Null means the built-in defaults, which is what
    /// every keyframe created before drift became per-segment carries.
    /// </summary>
    public CameraDriftSettings? Drift { get; init; }

    /// <summary>Resolved drift settings for this segment (never null).</summary>
    [JsonIgnore]
    public CameraDriftSettings EffectiveDrift => Drift ?? CameraDriftSettings.Default;

    /// <summary>
    /// True for keyframes added by the user via the editor UI.
    /// False for keyframes auto-generated from click events (visualization only).
    /// <para>
    /// This governs OWNERSHIP — whether the keyframe is editable and persisted. It does NOT
    /// govern where the focal point comes from; see <see cref="HasAuthoredCenter"/>. Keeping
    /// the two apart is what lets you lengthen a click-driven zoom without it also stopping
    /// following the cursor.
    /// </para>
    /// </summary>
    public bool IsManual { get; init; }

    /// <summary>
    /// Whether <see cref="CenterX"/>/<see cref="CenterY"/> are a framing the user actually
    /// chose, and so should be held, rather than a byproduct of where a click happened.
    /// <para>
    /// When this is false the compositor keeps re-centring the shot on the live cursor, which
    /// is what makes a click-driven zoom follow what you are doing. It is set ONLY by an
    /// explicit region edit — not by creating, moving, resizing, or restyling a segment.
    /// Creating a segment says <i>when</i> to zoom, not <i>where</i> to look. Before this
    /// existed, <see cref="IsManual"/> served both purposes, so dragging a segment's edge
    /// silently converted it from cursor-following to pinned-on-the-click-point: a framing
    /// change the user never asked for.
    /// </para>
    /// <para>
    /// Nullable for back-compat: projects saved before this existed carry no value, and
    /// <see cref="UsesAuthoredCenter"/> resolves those to <see cref="IsManual"/> so they keep
    /// rendering exactly as they did.
    /// </para>
    /// </summary>
    public bool? HasAuthoredCenter { get; init; }

    /// <summary>
    /// Resolved answer to "hold this centre, or follow the cursor?", applying the legacy
    /// fallback described on <see cref="HasAuthoredCenter"/>.
    /// </summary>
    [JsonIgnore]
    public bool UsesAuthoredCenter => HasAuthoredCenter ?? IsManual;

    /// <summary>
    /// The raw <see cref="ClickEvent.TimestampTicks"/> of the source click that
    /// generated this auto-zoom keyframe. Null for manually-added keyframes.
    /// Used as a stable identity to suppress the corresponding auto-zoom segment
    /// when the user deletes or edits this keyframe.
    /// </summary>
    public long? SourceClickTicks { get; init; }

    /// <summary>
    /// Source video file this keyframe belongs to. Null means the primary recording.
    /// Used to map the keyframe's source time into the correct (primary or appended)
    /// video segment's output range, so zoom segments show on the right clip and are
    /// available for appended recordings.
    /// </summary>
    public string? SourceVideoFilePath { get; init; }

    /// <summary>When the zoom-in animation begins.</summary>
    [JsonIgnore]
    public TimeSpan Start => Timestamp - PreDuration;

    /// <summary>When the zoom-out animation completes.</summary>
    [JsonIgnore]
    public TimeSpan End => Timestamp + HoldDuration + PostDuration;

    /// <summary>Total segment duration from Start to End.</summary>
    [JsonIgnore]
    public TimeSpan TotalDuration => PreDuration + HoldDuration + PostDuration;

    /// <summary>
    /// Creates a ZoomKeyframe from a segment start/end range, using default ease durations.
    /// </summary>
    public static ZoomKeyframe FromRange(TimeSpan start, TimeSpan end, double zoomLevel,
        double centerX = 0.5, double centerY = 0.5)
    {
        var total = end - start;
        if (total < MinSegmentDuration)
            total = MinSegmentDuration;

        var pre = TimeSpan.FromMilliseconds(Math.Min(1000, total.TotalMilliseconds * 0.2));
        var post = TimeSpan.FromMilliseconds(Math.Min(1556, total.TotalMilliseconds * 0.3));
        var hold = total - pre - post;
        if (hold < TimeSpan.Zero) hold = TimeSpan.Zero;

        return new ZoomKeyframe
        {
            Timestamp = start + pre,
            ZoomLevel = zoomLevel,
            CenterX = Math.Clamp(centerX, 0, 1),
            CenterY = Math.Clamp(centerY, 0, 1),
            PreDuration = pre,
            HoldDuration = hold,
            PostDuration = post,
            IsManual = true,
            // Creating a segment says WHEN to zoom, not WHERE to look — so it keeps following
            // the cursor. CenterX/CenterY are seeded from the cursor at the segment's midpoint
            // and kept as a fallback (they are what a cursorless clip uses, and what gets held
            // if the region is later authored), but they are only a snapshot. Following the
            // live cursor is strictly better than freezing that snapshot. Only an explicit
            // region edit pins the framing.
            HasAuthoredCenter = false,
        };
    }
}

public record SpeedSegment(TimeSpan Start, TimeSpan End, double Speed);
