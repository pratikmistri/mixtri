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
public readonly record struct AudioPlacement(
    string SourcePath,
    AudioSourceKind Kind,
    TimeSpan TrimFromStart,
    TimeSpan? TakeDuration,
    TimeSpan Delay,
    bool PlaysAtNativeRateOnSpeedAdjustedSegment = false,
    TimeSpan FadeOutDuration = default,
    TimeSpan FadeInDuration = default);

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
/// <para><b>Known limitation — speed-adjusted segments.</b> The mux pass is built on
/// <c>MediaComposition</c>/<c>BackgroundAudioTrack</c>, which expose trimming, delay,
/// volume and effects but <b>no playback-rate/time-scale API</b>; audio can therefore not
/// be re-timed to match a segment's <see cref="VideoSegment.SpeedFactor"/> (doing so would
/// require decoding, time-stretching and re-encoding every affected track offline). Such a
/// segment's audio is muxed at its native rate: it starts in sync with the segment, drifts
/// as the segment plays, and is cut at the segment boundary so it can never overlap the
/// next segment. Placements affected by this are flagged with
/// <see cref="AudioPlacement.PlaysAtNativeRateOnSpeedAdjustedSegment"/> so the exporter can
/// report the limitation rather than imply full A/V sync.</para>
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

        return videoSegments is { Count: > 0 }
            ? BuildFromSegments(project, timeline!, videoSegments)
            : BuildLegacy(project, mapper);
    }

    private static List<AudioPlacement> BuildFromSegments(
        Project project, TimelineModel timeline, List<VideoSegment> segments)
    {
        // Resolve every boundary on the COMPLETE ordered timeline (timeline.Segments, not
        // just the video-only `segments` list), so a transition whose INCOMING or OUTGOING
        // side is a TextSlideSegment is captured too — e.g. a video segment dissolving into
        // a following text slide must still get its own audio extended/faded, exactly like
        // a video-to-video boundary, even though the slide itself contributes no audio
        // placement of its own. Resolving at each full-timeline segment's own Start (for
        // every index but the first, which can never be a transition's incoming side) and
        // reading back TransitionResolver's own resolved Outgoing/IncomingSegment references
        // — rather than manually indexing "segments[i-1]" ourselves — means this is correct
        // regardless of how TransitionResolver internally locates adjacency.
        var fullSegments = timeline.Segments;
        var videoIndexByRef = new Dictionary<VideoSegment, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < segments.Count; i++)
            videoIndexByRef[segments[i]] = i;

        // Indexed by position in `segments` (the video-only list): how much trailing room a
        // transition at the NEXT full-timeline boundary allows this video's own audio to
        // bleed into (whatever sits on the other side — another video, or a text slide),
        // and how long this video's own incoming fade-in should ramp for, respectively.
        var trailingExtension = new TimeSpan[segments.Count];
        var fadeInDuration = new TimeSpan[segments.Count];
        for (int j = 1; j < fullSegments.Count; j++)
        {
            var resolution = TransitionResolver.Resolve(timeline, fullSegments[j].Start);
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

        var placements = new List<AudioPlacement>();
        // Contiguous [Start, Start+Count) range of `placements` contributed by each
        // segment, so the fade pass below can revisit them without re-deriving paths.
        var ranges = new (int Start, int Count)[segments.Count];

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            int rangeStart = placements.Count;
            bool isPrimary = IsPrimarySource(segment.VideoFilePath, project, timeline);
            var extension = trailingExtension[i];

            // Audio embedded in the recording is inherently aligned with its own video
            // frames, so it needs no extra offset.
            var embedded = BuildPlacement(
                segment.VideoFilePath, AudioSourceKind.EmbeddedVideoTrack, segment,
                offsetSeconds: 0, extension);
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
                        path, AudioSourceKind.AudioFile, segment, offsetSeconds, extension);
                    if (placement is { } audioPlacement)
                        placements.Add(audioPlacement);
                }
            }

            ranges[i] = (rangeStart, placements.Count - rangeStart);
        }

        ApplyTransitionFadeMetadata(segments, placements, ranges, trailingExtension, fadeInDuration);

        return placements;
    }

    /// <summary>
    /// Marks WHERE the equal-power crossfade curve (see
    /// <see cref="Musio.Core.Audio.EqualPowerCrossfade"/>) should run, on both sides of
    /// every active transition boundary: <see cref="AudioPlacement.FadeInDuration"/> on the
    /// incoming video's own placements, and <see cref="AudioPlacement.FadeOutDuration"/> on
    /// the outgoing video's — both sides looked up via <paramref name="fadeInDuration"/>/
    /// <paramref name="trailingExtension"/>, which <see cref="BuildFromSegments"/> already
    /// populated by resolving every boundary on the COMPLETE timeline (so a boundary whose
    /// other side is a <see cref="TextSlideSegment"/>, not just another
    /// <see cref="VideoSegment"/>, is still captured for whichever side IS a video).
    /// <see cref="AudioPlacement.TakeDuration"/> for the outgoing side was already extended
    /// (or deliberately left alone for a speed-adjusted segment) while placements were
    /// built, in <see cref="BuildPlacement"/>; this pass never touches it, it only sets the
    /// fade-curve metadata to match.
    /// </summary>
    private static void ApplyTransitionFadeMetadata(
        List<VideoSegment> segments,
        List<AudioPlacement> placements,
        (int Start, int Count)[] ranges,
        TimeSpan[] trailingExtension,
        TimeSpan[] fadeInDuration)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var fadeIn = fadeInDuration[i];
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

            var fadeOut = trailingExtension[i];
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

        return new AudioPlacement(
            sourcePath, kind, audioStart, take, segment.Start + outputLead, speedAdjusted);
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
