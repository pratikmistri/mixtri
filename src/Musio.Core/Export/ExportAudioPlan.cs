using Musio.Core.Audio;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Core.Export;

// NOTE on the T7/T9 "audio crossfade" feature (see class remarks below for the full writeup):
// FadeInDuration/FadeOutDuration on AudioPlacement, and the extended TakeDuration computed
// in BuildFromSegments, describe the semantically-correct crossfade. As of today NEITHER
// pipeline actually ramps gain a listener can hear: VideoEncoder's export mux cannot ramp
// gain on a BackgroundAudioTrack at all (no envelope API — see VideoEncoder.ApplyPlacement's
// remarks) and instead subtracts the fade back out before muxing, reproducing the exact
// pre-feature hard cut; AudioPlaybackEngine (live preview) HAS a working, tested
// equal-power ramp implementation (Musio.Core.Audio.EqualPowerCrossfade applied via its
// EqualPowerFadeSampleProvider), but T9 investigated wiring it to this data (from
// EditorPage.xaml.cs) and found it is NOT feasible without producing a crossfade that
// drifts: AudioPlaybackEngine plays raw, project-level source files continuously through a
// constant output-time offset, not per-segment placements, so a TransitionResolver
// boundary's output-timeline instant cannot be converted into that engine's native-file
// time once the timeline actually has the cut/trim/reorder/insertion a transition boundary
// implies. Every preview call site therefore still calls AudioPlaybackEngine.Load(paths)
// with no fade windows, deliberately, so preview stays a hard cut too, exactly like export
// — see AudioPlaybackEngine's class remarks for the full three-point reasoning.

/// <summary>Which kind of media a placement reads its audio from.</summary>
public enum AudioSourceKind
{
    /// <summary>The audio track embedded in a recorded video file.</summary>
    EmbeddedVideoTrack,

    /// <summary>A separately recorded audio file (system audio or microphone).</summary>
    AudioFile,
}

/// <summary>
/// The time-stretch a placement's segment still needs before it can be muxed: read
/// <see cref="SourceDuration"/> of source starting at <see cref="SourceStart"/>, re-time it
/// by <see cref="Speed"/>, and the result lasts exactly <see cref="OutputDuration"/>.
/// </summary>
/// <remarks>
/// <para>
/// Present only on placements belonging to a speed-adjusted segment whose
/// <see cref="VideoSegment.AudioMode"/> is <see cref="SegmentAudioMode.TimeStretch"/>.
/// <see cref="ExportAudioPlan"/> is deliberately I/O-free, so it can describe the stretch
/// but not perform it: the placement it emits is the ORIGINAL native-rate one, and this
/// record rides alongside as a request. <c>VideoEncoder</c> renders it (via
/// <c>SegmentAudioRenderer</c>) and substitutes the rendered file before muxing.
/// </para>
/// <para>
/// That "native placement plus a request" shape is what makes the feature fail soft: if the
/// render fails for any reason, the caller simply drops the request and muxes the placement
/// it was already holding — which is exactly the behaviour speed-adjusted audio had before
/// time-stretching existed.
/// </para>
/// </remarks>
/// <param name="Speed">The segment's speed factor; &gt;1 shortens the audio, &lt;1 lengthens it.</param>
/// <param name="SourceStart">Where to start reading inside the source file.</param>
/// <param name="SourceDuration">How much source audio to consume.</param>
/// <param name="OutputDuration">How long the stretched result must be (never overruns the segment).</param>
public readonly record struct SegmentAudioStretch(
    double Speed,
    TimeSpan SourceStart,
    TimeSpan SourceDuration,
    TimeSpan OutputDuration);

/// <summary>
/// One audio source positioned on the exported timeline.
/// </summary>
/// <param name="SourcePath">Video file (embedded track) or audio file to read from.</param>
/// <param name="Kind">How <paramref name="SourcePath"/> should be opened.</param>
/// <param name="TrimFromStart">Where playback starts inside the source file.</param>
/// <param name="TakeDuration">
/// How much of the source to play; <c>null</c> means "to the end of the file".
/// </param>
/// <param name="Delay">Where the trimmed audio starts on the output timeline.</param>
/// <param name="PlaysAtNativeRateOnSpeedAdjustedSegment">
/// <c>true</c> when the owning segment is speed-adjusted. Neither
/// <c>MediaComposition</c> nor <c>BackgroundAudioTrack</c> exposes a playback-rate or
/// time-scale API, so this audio is muxed at its native rate: it starts in sync with the
/// segment but drifts as the segment plays, and is cut at the segment boundary (see
/// <see cref="ExportAudioPlan"/>). Exposed so callers can report the limitation instead
/// of implying the export is fully synchronized.
/// </param>
/// <param name="FadeOutDuration">
/// <para>
/// How long, at the END of this placement's <paramref name="TakeDuration"/>, its gain
/// should ramp from 1 down to 0 using an equal-power curve (see
/// <see cref="Musio.Core.Audio.EqualPowerCrossfade"/>). <see cref="TimeSpan.Zero"/> (the
/// default) means "no fade — hard stop", which is what every placement gets unless it sits
/// on the OUTGOING side of an active <see cref="TransitionResolver"/> boundary.
/// </para>
/// <para>
/// When non-zero, <paramref name="TakeDuration"/> has already been extended by this same
/// amount (clamped to what <see cref="ExportAudioPlan"/> can determine is actually
/// available — see its class remarks), so the audio keeps playing underneath the
/// following segment's own audio for the length of the dissolve instead of stopping dead
/// at the cut. Consumers that cannot also ramp gain (see
/// <see cref="Export.VideoEncoder"/>'s remarks) must either apply the ramp or subtract
/// this duration back out of <paramref name="TakeDuration"/> before use, or the two
/// overlapping tracks will simply sum at full volume.
/// </para>
/// </param>
/// <param name="FadeInDuration">
/// How long, at the START of this placement's <paramref name="TakeDuration"/>, its gain
/// should ramp from 0 up to 1 using an equal-power curve. <see cref="TimeSpan.Zero"/> (the
/// default) means "no fade — full volume from the first sample", which is every placement
/// unless it sits on the INCOMING side of an active <see cref="TransitionResolver"/>
/// boundary. Unlike <paramref name="FadeOutDuration"/>, this never changes
/// <see cref="TrimFromStart"/> or <see cref="Delay"/> — the fade runs entirely inside audio
/// that was already going to be placed there.
/// </param>
/// <param name="Volume">
/// <para>
/// Constant playback gain in the 0..1 range <c>BackgroundAudioTrack.Volume</c> accepts.
/// <c>1.0</c> (the default) is every placement cut from a recording — recorded audio is
/// muxed at the level it was captured, and the editor's per-source control is a mute, not
/// a fader.
/// </para>
/// <para>
/// Below 1.0 only for an inserted <see cref="Musio.Core.Models.AudioTrack"/> (voice-over
/// or music bed), whose whole point is sitting under the recording. Unlike the fade fields
/// above this IS actually applied on export: a CONSTANT gain needs no envelope API, which
/// is exactly the capability <c>BackgroundAudioTrack</c> lacks.
/// </para>
/// </param>
/// <param name="Stretch">
/// Non-null when this placement's segment is speed-adjusted and set to
/// <see cref="SegmentAudioMode.TimeStretch"/>: the caller must render the described
/// time-stretch and substitute it before muxing (see <see cref="SegmentAudioStretch"/>).
/// The placement's own fields still describe the untouched native-rate audio, so dropping
/// the request degrades cleanly to <see cref="SegmentAudioMode.Native"/>.
/// </param>
public readonly record struct AudioPlacement(
    string SourcePath,
    AudioSourceKind Kind,
    TimeSpan TrimFromStart,
    TimeSpan? TakeDuration,
    TimeSpan Delay,
    bool PlaysAtNativeRateOnSpeedAdjustedSegment = false,
    TimeSpan FadeOutDuration = default,
    TimeSpan FadeInDuration = default,
    double Volume = 1.0,
    SegmentAudioStretch? Stretch = null);

/// <summary>
/// Pure mapping from a project + timeline to the set of audio tracks the exporter must
/// mux, and where each one belongs on the output timeline.
///
/// <para>Whenever the timeline has video segments (every project edited in the segment
/// editor does), audio follows the segments: each segment contributes its own source
/// range, delayed to that segment's output position. This is what makes trims, splits,
/// deletes, reorders, inserted text slides, and appended recordings produce matching
/// audio — muxing the untouched source file would replay deleted or reordered content.
/// Projects with no segments keep the legacy trim-based placement.</para>
///
/// <para><b>Speed-adjusted segments.</b> The mux pass is built on
/// <c>MediaComposition</c>/<c>BackgroundAudioTrack</c>, which expose trimming, delay,
/// volume and effects but <b>no playback-rate/time-scale API</b>, so nothing here can
/// re-time audio to match a segment's <see cref="VideoSegment.SpeedFactor"/> by itself.
/// An audible speed-adjusted segment therefore emits its placement with a
/// <see cref="AudioPlacement.Stretch"/> request describing the offline WSOLA re-time the
/// caller must render and substitute (see <see cref="SegmentAudioStretch"/>). The
/// placement's own fields still describe the untouched native-rate audio, so a failed
/// render degrades to muxing it at its native rate — flagged with
/// <see cref="AudioPlacement.PlaysAtNativeRateOnSpeedAdjustedSegment"/> so the exporter can
/// report the limitation rather than imply full A/V sync — instead of losing the export.
/// A segment set to <see cref="SegmentAudioMode.Muted"/> emits no placement at all, at any
/// speed.</para>
///
/// <para><b>Transition crossfades (T7) and why export still hard-cuts.</b> Every boundary
/// <see cref="TransitionResolver"/> reports as active gets an outgoing placement
/// whose <see cref="AudioPlacement.TakeDuration"/> is extended by the transition's clamped
/// <see cref="TransitionResolution.Duration"/> (so its audio keeps rolling
/// underneath the incoming segment, mirroring the way
/// <see cref="TransitionResolution.OutgoingLocalOffset"/> already lets the
/// OUTGOING VIDEO keep rolling past its own cut for the video renderers), plus matching
/// <see cref="AudioPlacement.FadeOutDuration"/>/<see cref="AudioPlacement.FadeInDuration"/>
/// so a consumer can ramp gain with an equal-power curve
/// (<see cref="Musio.Core.Audio.EqualPowerCrossfade"/>) instead of a linear one, which
/// audibly dips in the middle. This class stays pure (no I/O), so it never verifies the
/// extension against the source file's real length — see <see cref="BuildPlacement"/>'s
/// remarks for exactly what bound it does apply. <b>That is NOT, by itself, a guarantee
/// the extension never exceeds the source's actual remaining length</b> (e.g. a segment
/// trimmed to end exactly at a 10s file's own EOF, with a 1s transition, plans an 11s
/// take) — <see cref="Export.VideoEncoder"/>, the one caller that actually opens the
/// media, is responsible for clamping <see cref="AudioPlacement.TakeDuration"/>/
/// <see cref="AudioPlacement.FadeOutDuration"/>/<see cref="AudioPlacement.FadeInDuration"/>
/// against the real duration once it knows it (see
/// <see cref="Export.VideoEncoder"/>'s <c>ClampToSourceDuration</c> remarks) BEFORE any
/// consumer — including a hypothetical future real gain-envelope effect, not just today's
/// hard-cut fallback — reads those fields. Callers that skip that step must not trust this
/// class's fields as an authoritative bound on the source file's real length.
/// <para>
/// <b>Investigated and rejected: a custom gain-ramp effect for export.</b> The only
/// in-framework way to ramp a <c>BackgroundAudioTrack</c>'s gain over time is a custom
/// <c>Windows.Media.Effects.IBasicAudioEffect</c> registered via
/// <c>BackgroundAudioTrack.AudioEffectDefinitions</c>. That was investigated and rejected
/// for THIS pipeline: authoring one requires (1) a dedicated Windows Runtime Component
/// project — a plain <c>Microsoft.NET.Sdk</c> class library like <c>Musio.Core</c> cannot
/// produce a WinRT-activatable class — and (2) declaring the class as an activatable
/// in-process server via a <c>windows.activatableClass.inProcessServer</c> extension in
/// <c>Musio.App\Package.appxmanifest</c>. Both are structural, cross-project changes far
/// outside a single feature task's file scope, and (2) especially is a shared,
/// contended file that would risk stepping on every other agent's work in this
/// repository. No workaround inside <c>Musio.Core</c>/<c>Musio.App</c>'s existing project
/// shapes was found. Per this feature's pre-approved fallback, exported audio therefore
/// keeps the placements' extended <see cref="AudioPlacement.TakeDuration"/>/fade fields as
/// PURE DATA (so they stay correct, tested, and ready for a future task that adds a real
/// WinRT component), but <see cref="Export.VideoEncoder.ApplyPlacement"/> deliberately
/// subtracts the fade back out before muxing — summing two full-volume, un-ramped tracks
/// across the dissolve window would be an audible glitch strictly worse than the
/// pre-existing hard cut, so export reproduces that exact hard cut unchanged.
/// </para>
/// <para>
/// <b>Preview does not crossfade either — this was investigated (T9) and found NOT
/// feasible with the engine's current architecture, not merely unwired.</b> <see
/// cref="Musio.Core.Audio.AudioPlaybackEngine"/> DOES contain a working, tested
/// equal-power ramp (<c>EqualPowerFadeSampleProvider</c>, driven by the same
/// <see cref="Musio.Core.Audio.EqualPowerCrossfade"/> curve), and its
/// <c>Load(paths, fadeWindowsByPath)</c> overload will actually ramp gain a listener can
/// hear IF given fade windows expressed in the loaded file's own native time. The blocker
/// is producing those windows correctly: that engine plays raw, project-level source files
/// continuously, position-mapped from output time through a single constant per-file
/// offset (<c>EditorPage.AudioPositionForVideo</c>), not per-segment placements the way
/// <see cref="BuildFromSegments"/> does above. A <see cref="TransitionResolver"/> boundary
/// exists only where the timeline has a cut/trim/reorder/insertion, which is exactly where
/// that constant-offset mapping stops corresponding to the right instant in the raw file —
/// converting the boundary's output-time window into native-file time would place the ramp
/// over unrelated audio, not the actual moment of the edit, and a <c>VideoSegment</c> ↔
/// <c>TextSlideSegment</c> boundary (the common legacy-fallback case) has no recorded audio
/// on one side at all regardless of offset arithmetic. See
/// <see cref="Musio.Core.Audio.AudioPlaybackEngine"/>'s remarks for the complete
/// three-point reasoning and what a real fix would require (making that engine
/// placement-aware, mirroring this class's per-segment model). That pre-existing
/// limitation predates this feature; T9 confirmed it still blocks wiring fade windows in
/// today, so live preview stays a hard cut, identically to export.
/// </para>
/// </para>
/// </summary>
public static class ExportAudioPlan
{
    /// <summary>Speed factors within this distance of 1.0 are treated as unmodified.</summary>
    private const double SpeedEpsilon = 0.001;

    /// <summary>
    /// Whether <paramref name="segment"/> plays at anything other than its native rate, and so
    /// cannot have its audio extended past the boundary (see
    /// <see cref="AudioPlacement.PlaysAtNativeRateOnSpeedAdjustedSegment"/>).
    /// </summary>
    /// <remarks>
    /// Shared by the transition-metadata pass and <see cref="BuildPlacement"/> deliberately: if
    /// the two ever disagreed about what counts as speed-adjusted, the plan would record fade
    /// metadata for an overlap that was never actually applied (or vice versa).
    /// </remarks>
    private static bool IsSpeedAdjusted(VideoSegment segment)
    {
        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        return Math.Abs(speed - 1.0) > SpeedEpsilon;
    }

    /// <summary>
    /// Builds the ordered audio placements for an export. Nothing here touches the file
    /// system: callers resolve which placements actually have media (see
    /// <see cref="VideoEncoder"/>) before muxing.
    /// </summary>
    public static IReadOnlyList<AudioPlacement> Build(
        Project project, TimelineModel? timeline, TimelineMapper? mapper)
    {
        ArgumentNullException.ThrowIfNull(project);

        var videoSegments = timeline?.Segments
            .OfType<VideoSegment>()
            .OrderBy(s => s.Start)
            .ToList();

        var recorded = videoSegments is { Count: > 0 }
            ? BuildFromSegments(project, timeline!, videoSegments)
            : BuildLegacy(project, mapper);

        // Apply the per-channel mix to everything cut from a recording. Done HERE, in the one
        // place placements are built, rather than by the caller filtering file lists: the
        // export view model used to drop muted files from Project.AudioFilePaths, which
        // BuildFromSegments never reads (it uses each segment's own list), so muting a track
        // and exporting an edited project silently kept the muted audio.
        if (timeline is not null)
            recorded = ApplyRecordedMix(recorded, timeline);

        // Inserted tracks are built into a SEPARATE list and concatenated only on the way
        // out, so they can never reach ApplyRecordedMix. That separation is load-bearing, not
        // stylistic: an inserted clip is also an AudioSourceKind.AudioFile, so the classifier
        // would read its file name (typically `audio.wav`) as system capture and REPLACE its
        // carefully computed clip×lane gain with the system channel's — or drop it entirely
        // when system audio is muted. Keeping them in one list would leave that correctness
        // resting on nothing but the order of two statements; two guard tests pin it.
        var inserted = timeline is not null ? BuildInsertedTracks(timeline) : [];

        recorded.AddRange(inserted);
        return recorded;
    }

    /// <summary>
    /// Scales each recorded placement by its channel's gain, dropping the ones that are
    /// silent.
    /// </summary>
    /// <remarks>
    /// Only <see cref="AudioSourceKind.AudioFile"/> placements are classified: those are the
    /// recorder's own <c>system_*</c>/<c>mic_*</c> captures.
    /// <see cref="AudioSourceKind.EmbeddedVideoTrack"/> is an imported clip's own soundtrack,
    /// which belongs to neither channel and is left untouched.
    /// </remarks>
    private static List<AudioPlacement> ApplyRecordedMix(
        List<AudioPlacement> placements, TimelineModel timeline)
    {
        var mixed = new List<AudioPlacement>(placements.Count);

        foreach (var placement in placements)
        {
            if (placement.Kind != AudioSourceKind.AudioFile)
            {
                mixed.Add(placement);
                continue;
            }

            double volume = timeline.EffectiveVolume(RecordedAudio.Classify(placement.SourcePath));
            if (volume <= 0) continue;      // muted or silenced: never worth muxing

            mixed.Add(volume >= 1.0 ? placement : placement with { Volume = volume });
        }

        return mixed;
    }

    /// <summary>
    /// Placements for the timeline's inserted <see cref="AudioTrack"/>s (voice-over, music).
    /// </summary>
    /// <remarks>
    /// These are the one kind of audio that is NOT derived from a segment: an inserted
    /// track's <see cref="AudioTrack.StartTime"/> is already an output-timeline instant, so
    /// it maps to <see cref="AudioPlacement.Delay"/> directly, with no trim/speed/transition
    /// arithmetic. That is the entire point of the type — a voice-over must stay where the
    /// user put it when the footage under it is re-cut, where recorded audio must follow its
    /// segment. Muted, silenced and zero-length tracks are dropped here rather than muxed at
    /// volume 0, so a disabled track costs nothing in the export.
    /// </remarks>
    private static List<AudioPlacement> BuildInsertedTracks(TimelineModel timeline)
    {
        var placements = new List<AudioPlacement>();
        if (timeline.AudioTracks is not { Count: > 0 }) return placements;

        foreach (var track in timeline.AudioTracks)
        {
            if (track is null || !track.IsAudible) continue;

            // The clip's own gain scaled by its lane's. Two independent controls: a lane
            // fader that rides every clip on it, and each clip's own level — which is what
            // lets one loud bed be pulled down without touching a carefully set voice-over.
            double laneVolume = timeline.EffectiveVolume(RecordedAudio.ChannelFor(track.Kind));
            double volume = track.EffectiveVolume * laneVolume;
            if (volume <= 0) continue;

            var trimStart = track.TrimStart < TimeSpan.Zero ? TimeSpan.Zero : track.TrimStart;
            var delay = track.StartTime < TimeSpan.Zero ? TimeSpan.Zero : track.StartTime;

            placements.Add(new AudioPlacement(
                track.FilePath,
                AudioSourceKind.AudioFile,
                trimStart,
                track.EffectiveDuration,
                delay,
                Volume: volume));
        }

        return placements;
    }

    private static List<AudioPlacement> BuildFromSegments(
        Project project, TimelineModel timeline, List<VideoSegment> segments)
    {
        // Resolve every boundary on the COMPLETE BASE timeline (not just the video-only
        // `segments` list), so a transition whose INCOMING or OUTGOING side is a
        // TextSlideSegment is captured too — e.g. a video segment dissolving into a
        // following text slide must still get its own audio extended/faded, exactly like a
        // video-to-video boundary, even though the slide itself contributes no audio
        // placement of its own. Overlay tracks are absolute covers, not adjacent edits, so
        // they intentionally do not participate in this boundary pass.
        var baseSegments = timeline.BaseSegments.ToList();
        var videoIndexByRef = new Dictionary<VideoSegment, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < segments.Count; i++)
            videoIndexByRef[segments[i]] = i;

        // Indexed by position in `segments` (the video-only list): how much trailing room a
        // transition at the NEXT full-timeline boundary allows this video's own audio to
        // bleed into (whatever sits on the other side — another video, or a text slide),
        // and how long this video's own incoming fade-in should ramp for, respectively.
        var trailingExtension = new TimeSpan[segments.Count];
        var fadeInDuration = new TimeSpan[segments.Count];
        for (int j = 1; j < baseSegments.Count; j++)
        {
            var resolution = TransitionResolver.Resolve(timeline, baseSegments[j].Start);
            if (!resolution.Active || resolution.Duration <= TimeSpan.Zero)
                continue;

            var outgoingVideo = resolution.OutgoingSegment as VideoSegment;

            // A speed-adjusted OUTGOING segment cannot lend any overlap room: BuildPlacement
            // refuses to extend its take (its audio has no reliable output/source time
            // correspondence and keeps its existing hard cap at the boundary — see
            // PlaysAtNativeRateOnSpeedAdjustedSegment). Recording fade metadata anyway would
            // describe a crossfade that cannot exist: a future gain-envelope consumer would
            // ramp the outgoing segment's OWN final audio down to silence rather than the
            // extra tail it never got, and ramp the incoming side up from silence with
            // nothing underneath it — a dip in the middle of the boundary instead of a
            // dissolve. So neither side records anything for such a boundary, keeping the
            // metadata an exact description of the overlap that was actually applied.
            if (outgoingVideo is not null && IsSpeedAdjusted(outgoingVideo))
                continue;

            if (outgoingVideo is not null
                && videoIndexByRef.TryGetValue(outgoingVideo, out int outIdx))
            {
                trailingExtension[outIdx] = resolution.Duration;
            }

            if (resolution.IncomingSegment is VideoSegment incomingVideo
                && videoIndexByRef.TryGetValue(incomingVideo, out int inIdx))
            {
                fadeInDuration[inIdx] = resolution.Duration;
            }
        }

        var audibleRanges = new List<(VideoSegment Segment, TimeSpan FadeOut, TimeSpan FadeIn)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            foreach (var visible in timeline.VisibleRanges(segment))
            {
                var visibleSegment = ProjectVisibleRange(segment, visible.Start, visible.End);
                if (visibleSegment is null)
                    continue;

                // Transition metadata belongs only to the visible range that touches the
                // base-chain boundary. A middle slice revealed between two overlays is just
                // ordinary segment audio and must not borrow fade data from another edge.
                var fadeOut = visible.End == segment.End ? trailingExtension[i] : TimeSpan.Zero;
                var fadeIn = visible.Start == segment.Start ? fadeInDuration[i] : TimeSpan.Zero;
                audibleRanges.Add((visibleSegment, fadeOut, fadeIn));
            }
        }

        var placements = new List<AudioPlacement>();
        // Contiguous [Start, Start+Count) range of `placements` contributed by each
        // visible segment range, so the fade pass below can revisit them without
        // re-deriving paths.
        var ranges = new (int Start, int Count)[audibleRanges.Count];

        for (int i = 0; i < audibleRanges.Count; i++)
        {
            var (segment, fadeOut, _) = audibleRanges[i];
            int rangeStart = placements.Count;
            bool isPrimary = IsPrimarySource(segment.VideoFilePath, project, timeline);

            // Audio embedded in the recording is inherently aligned with its own video
            // frames, so it needs no extra offset.
            var embedded = BuildPlacement(
                segment.VideoFilePath, AudioSourceKind.EmbeddedVideoTrack, segment,
                offsetSeconds: 0, fadeOut);
            if (embedded is { } embeddedPlacement)
                placements.Add(embeddedPlacement);

            // Separately recorded audio: the primary recording's tracks live on the
            // project (the export view model filters muted ones there), appended
            // recordings carry their own.
            var audioPaths = isPrimary ? project.AudioFilePaths : segment.AudioFilePaths;
            if (audioPaths is not null)
            {
                double offsetSeconds = isPrimary
                    ? project.AudioToVideoOffsetSeconds
                    : segment.AudioToVideoOffsetSeconds;

                foreach (var path in audioPaths)
                {
                    var placement = BuildPlacement(
                        path, AudioSourceKind.AudioFile, segment, offsetSeconds, fadeOut);
                    if (placement is { } audioPlacement)
                        placements.Add(audioPlacement);
                }
            }

            ranges[i] = (rangeStart, placements.Count - rangeStart);
        }

        ApplyTransitionFadeMetadata(audibleRanges, placements, ranges);

        return placements;
    }

    /// <summary>
    /// Marks WHERE the equal-power crossfade curve (see
    /// <see cref="Musio.Core.Audio.EqualPowerCrossfade"/>) should run, on both sides of
    /// every active transition boundary: <see cref="AudioPlacement.FadeInDuration"/> on the
    /// incoming video's own placements, and <see cref="AudioPlacement.FadeOutDuration"/> on
    /// the outgoing video's — both sides are already attached to the visible range that
    /// touches that base-chain boundary by <see cref="BuildFromSegments"/> (so a boundary
    /// whose other side is a <see cref="TextSlideSegment"/>, not just another
    /// <see cref="VideoSegment"/>, is still captured for whichever side IS a video).
    /// <see cref="AudioPlacement.TakeDuration"/> for the outgoing side was already extended
    /// (or deliberately left alone for a speed-adjusted segment) while placements were
    /// built, in <see cref="BuildPlacement"/>; this pass never touches it, it only sets the
    /// fade-curve metadata to match.
    /// </summary>
    private static void ApplyTransitionFadeMetadata(
        List<(VideoSegment Segment, TimeSpan FadeOut, TimeSpan FadeIn)> segmentRanges,
        List<AudioPlacement> placements,
        (int Start, int Count)[] ranges)
    {
        for (int i = 0; i < segmentRanges.Count; i++)
        {
            var fadeIn = segmentRanges[i].FadeIn;
            if (fadeIn > TimeSpan.Zero)
            {
                var (start, count) = ranges[i];
                for (int p = start; p < start + count; p++)
                {
                    var placement = placements[p];
                    var take = placement.TakeDuration ?? TimeSpan.Zero;
                    var clamped = fadeIn <= take ? fadeIn : take;
                    if (clamped > TimeSpan.Zero)
                        placements[p] = placement with { FadeInDuration = clamped };
                }
            }

            var fadeOut = segmentRanges[i].FadeOut;
            if (fadeOut > TimeSpan.Zero)
            {
                var (start, count) = ranges[i];
                for (int p = start; p < start + count; p++)
                {
                    var placement = placements[p];
                    var take = placement.TakeDuration ?? TimeSpan.Zero;
                    var clamped = fadeOut <= take ? fadeOut : take;
                    if (clamped > TimeSpan.Zero)
                        placements[p] = placement with { FadeOutDuration = clamped };
                }
            }
        }
    }

    /// <summary>
    /// Re-expresses one visible output-time slice of a segment as a segment-shaped value so
    /// the existing trim/offset/speed audio arithmetic can be reused unchanged.
    /// </summary>
    private static VideoSegment? ProjectVisibleRange(VideoSegment segment, TimeSpan visibleStart, TimeSpan visibleEnd)
    {
        if (visibleEnd <= visibleStart)
            return null;

        var localStart = visibleStart - segment.Start;
        var localEnd = visibleEnd - segment.Start;
        if (localEnd <= TimeSpan.Zero || localStart >= segment.Duration)
            return null;

        if (localStart < TimeSpan.Zero) localStart = TimeSpan.Zero;
        if (localEnd > segment.Duration) localEnd = segment.Duration;
        if (localEnd <= localStart)
            return null;

        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        var visibleDuration = localEnd - localStart;
        var sourceStart = segment.SourceStart + ScaleDuration(localStart, speed);
        var sourceDuration = ScaleDuration(visibleDuration, speed);
        if (sourceDuration <= TimeSpan.Zero)
            return null;

        return segment with
        {
            Start = segment.Start + localStart,
            Duration = visibleDuration,
            SourceStart = sourceStart,
            SourceDuration = sourceDuration,
        };
    }

    private static TimeSpan ScaleDuration(TimeSpan value, double scale)
        => TimeSpan.FromTicks((long)(value.Ticks * scale));

    /// <summary>
    /// Places one source's audio for a single video segment.
    ///
    /// <para>The segment covers source-video times <c>[SourceStart, SourceStart +
    /// SourceDuration)</c>. For a separately recorded audio file, the position inside that
    /// file for source-video time <c>T</c> is <c>T + offsetSeconds</c>, so the aligned
    /// audio interval is <c>[SourceStart + offset, SourceStart + SourceDuration + offset)</c>
    /// — exactly as long as the segment's source range, never longer. That interval is then
    /// clipped to the file's own domain (it cannot start before 0), and the clipped head is
    /// converted back into an output delay through the segment's speed factor.</para>
    ///
    /// <para><paramref name="trailingExtension"/> is the one exception to "never longer":
    /// when non-zero, it is added to BOTH ends of that arithmetic — the aligned interval's
    /// end AND the output room the take is capped against — so an active transition at the
    /// NEXT boundary can let this segment's audio bleed exactly that much further, while
    /// every existing invariant keeps applying identically to the combined (base +
    /// extension) window. In particular the existing <c>available &lt; outputRoom</c> cap
    /// (which stops a speed-adjusted segment's native-rate audio from ever overlapping the
    /// next segment) still applies to the extended window — which is exactly why callers
    /// must never pass a non-zero <paramref name="trailingExtension"/> for a speed-adjusted
    /// segment (see <see cref="BuildFromSegments"/>): that cap is the "do not extend past
    /// what the existing code already deliberately caps" rule, and this class still has no
    /// I/O with which to check the source file's TRUE remaining length beyond that — see
    /// <see cref="ExportAudioPlan"/>'s class remarks for why that is not, by itself, a
    /// promise that the extension fits inside the real file, and which downstream caller is
    /// responsible for the real clamp.</para>
    ///
    /// Returns <c>null</c> when the segment has no audible range (degenerate segment, or
    /// audio that only starts after the segment ends).
    /// </summary>
    private static AudioPlacement? BuildPlacement(
        string sourcePath, AudioSourceKind kind, VideoSegment segment, double offsetSeconds,
        TimeSpan trailingExtension = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (segment.SourceDuration <= TimeSpan.Zero || segment.Duration <= TimeSpan.Zero) return null;

        double speed = segment.SpeedFactor > 0 ? segment.SpeedFactor : 1.0;
        bool speedAdjusted = IsSpeedAdjusted(segment);

        // Mute is NOT a speed concept: a segment the user silenced stays silent whatever
        // rate it plays at, so this is checked before anything speed-related. (The other two
        // modes genuinely are — at 1.0 "re-time the audio" and "play it at its native rate"
        // describe the same audio, so neither is consulted there.)
        if (segment.AudioMode == SegmentAudioMode.Muted) return null;

        // Only apply the extension at native speed: a speed-adjusted segment has no
        // reliable output/source time correspondence to begin with (see
        // PlaysAtNativeRateOnSpeedAdjustedSegment) and must keep its existing hard cap.
        var extension = !speedAdjusted && trailingExtension > TimeSpan.Zero
            ? trailingExtension
            : TimeSpan.Zero;

        var offset = TimeSpan.FromSeconds(offsetSeconds);
        var videoStart = segment.SourceStart;
        var videoEnd = segment.SourceStart + segment.SourceDuration + extension;

        // The aligned interval inside the audio source, clipped to the file's domain.
        var audioStart = videoStart + offset;
        var audioEnd = videoEnd + offset;
        if (audioStart < TimeSpan.Zero) audioStart = TimeSpan.Zero;
        if (audioEnd <= audioStart) return null;

        // Source-video time at which audio becomes available, and the matching lead on
        // the output timeline (scaled by the segment's speed).
        var firstAudibleVideoTime = audioStart - offset;
        var sourceLead = firstAudibleVideoTime - videoStart;
        var outputLead = sourceLead > TimeSpan.Zero
            ? TimeSpan.FromTicks((long)(sourceLead.Ticks / speed))
            : TimeSpan.Zero;
        if (outputLead >= segment.Duration) return null;

        var available = audioEnd - audioStart;
        // The extension is exactly the room a transition dissolve is allowed to bleed into
        // the next segment; everywhere else the cap stays segment.Duration, so audio can
        // never overlap a following segment with no active transition (unchanged
        // behaviour when extension is zero).
        var outputRoom = segment.Duration - outputLead + extension;

        // Audio is muxed at its native rate (no time-scale API exists), so a sped-up
        // segment consumes source audio faster than the output advances. Capping the take
        // at the remaining output room guarantees it can never bleed into the following
        // segment; the tail is dropped instead.
        var take = available < outputRoom ? available : outputRoom;
        if (take <= TimeSpan.Zero) return null;

        // ...unless the segment is speed-adjusted, in which case the take above is only the
        // fallback: audible audio on such a segment is always re-timed to fit. The stretch
        // consumes as much source as maps onto the remaining output room (all of it, when the
        // file has that much left), and the rendered result is exactly that room long — so a
        // 2x segment keeps its whole second half instead of dropping it, and a 0.5x segment
        // has no trailing silence.
        SegmentAudioStretch? stretch = null;
        if (speedAdjusted)
        {
            var stretchSource = ScaleDuration(outputRoom, speed);
            if (available < stretchSource) stretchSource = available;

            var stretchOutput = TimeSpan.FromTicks((long)(stretchSource.Ticks / speed));
            if (stretchOutput > outputRoom) stretchOutput = outputRoom;

            if (stretchSource > TimeSpan.Zero && stretchOutput > TimeSpan.Zero)
                stretch = new SegmentAudioStretch(speed, audioStart, stretchSource, stretchOutput);
        }

        return new AudioPlacement(
            sourcePath, kind, audioStart, take, segment.Start + outputLead, speedAdjusted,
            Stretch: stretch);
    }

    /// <summary>
    /// Legacy (no segments) placement: the whole recording trimmed to the timeline's
    /// trim range and played from the start of the output.
    /// </summary>
    private static List<AudioPlacement> BuildLegacy(Project project, TimelineMapper? mapper)
    {
        var trimStart = TimeSpan.Zero;
        TimeSpan? take = null;

        if (mapper is not null)
        {
            trimStart = mapper.TrimStart;
            var span = mapper.TrimEnd - mapper.TrimStart;
            if (span > TimeSpan.Zero) take = span;
        }

        var placements = new List<AudioPlacement>();

        if (!string.IsNullOrWhiteSpace(project.VideoFilePath))
        {
            placements.Add(new AudioPlacement(
                project.VideoFilePath, AudioSourceKind.EmbeddedVideoTrack, trimStart, take, TimeSpan.Zero));
        }

        if (project.AudioFilePaths is not null)
        {
            foreach (var path in project.AudioFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                placements.Add(new AudioPlacement(
                    path, AudioSourceKind.AudioFile, trimStart, take, TimeSpan.Zero));
            }
        }

        return placements;
    }

    private static bool IsPrimarySource(string videoFilePath, Project project, TimelineModel timeline)
        => string.Equals(videoFilePath, project.VideoFilePath, StringComparison.OrdinalIgnoreCase)
        || (timeline.PrimaryVideoFilePath is { } primary
            && string.Equals(videoFilePath, primary, StringComparison.OrdinalIgnoreCase));
}
