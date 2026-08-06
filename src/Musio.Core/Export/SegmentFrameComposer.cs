using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// Composes output frames for the export pipeline directly from the timeline's
/// <see cref="TimelineModel.Segments"/>: each output frame is routed to the segment
/// that owns it, video segments read from <em>their own</em> source recording, and
/// text slides are rendered by <see cref="TextSlideRenderer"/>.
///
/// <para>This is the single segment-composition path shared by the MP4
/// (<see cref="VideoEncoder"/>) and GIF (<see cref="ExportEngine"/>) exporters, so the
/// two formats cannot drift apart in segment ordering, source selection, trims, speed,
/// text slides, or slide crossfades.</para>
///
/// <para>Per-source render state (frame reader, <see cref="FrameCompositor"/>, webcam
/// composition) is built lazily and cached, keyed by source file plus the effective
/// per-segment frame/cursor style. That keeps per-segment
/// <see cref="VideoSegment.FrameStyleOverride"/> / <see cref="VideoSegment.CursorStyleOverride"/>
/// honoured without mutating a shared compositor, and never rebuilds the expensive
/// cursor-smoothing pipeline per frame.</para>
///
/// <para>Instances are <b>not</b> thread-safe: callers must serialize
/// <see cref="ComposeFrameAsync"/> (the MP4 encoder does so with its frame semaphore,
/// the GIF encoder composes frames sequentially).</para>
/// </summary>
public sealed class SegmentFrameComposer : IDisposable
{
    private readonly Project _project;
    private readonly CompositionConfig _composition;
    private readonly MouseRecordingData _primaryMouseData;
    private readonly TimelineModel? _timeline;
    private readonly TimelineMapper? _mapper;
    private readonly int _fps;
    private readonly CanvasDevice _device;
    private readonly Dictionary<SourceKey, SourceContext> _contexts = [];
    private readonly SourceContext _primaryContext;

    private TextSlideRenderer? _textSlideRenderer;
    private TransitionRenderer? _transitionRenderer;
    private bool _disposed;

    /// <summary>Width of every frame returned by <see cref="ComposeFrameAsync"/>.</summary>
    public int OutputWidth { get; }

    /// <summary>Height of every frame returned by <see cref="ComposeFrameAsync"/>.</summary>
    public int OutputHeight { get; }

    /// <summary>
    /// Number of output frames this composer can produce: the timeline mapper's total
    /// when a timeline is present, otherwise the primary recording's frame count.
    /// </summary>
    public int TotalFrames { get; }

    private SegmentFrameComposer(
        Project project,
        MouseRecordingData primaryMouseData,
        CompositionConfig composition,
        TimelineModel? timeline,
        TimelineMapper? mapper,
        int fps,
        CanvasDevice device,
        SourceKey primaryKey,
        SourceContext primaryContext)
    {
        _project = project;
        _primaryMouseData = primaryMouseData;
        _composition = composition;
        _timeline = timeline;
        _mapper = mapper;
        _fps = fps;
        _device = device;
        _primaryContext = primaryContext;
        _contexts[primaryKey] = primaryContext;

        OutputWidth = primaryContext.Compositor.OutputWidth;
        OutputHeight = primaryContext.Compositor.OutputHeight;
        TotalFrames = mapper?.TotalOutputFrames ?? primaryContext.Compositor.TotalFrames;
    }

    /// <summary>
    /// Builds a composer and eagerly initializes the primary recording's render context
    /// (which defines the canonical output dimensions).
    /// </summary>
    /// <param name="fps">Output frame rate; output frame N maps to output time N/fps.</param>
    public static async Task<SegmentFrameComposer> CreateAsync(
        Project project,
        MouseRecordingData primaryMouseData,
        CompositionConfig composition,
        TimelineModel? timeline,
        TimelineMapper? mapper,
        int fps,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(primaryMouseData);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.VideoFilePath);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        ct.ThrowIfCancellationRequested();

        var device = GpuContext.GetSharedDevice();

        // The key is built from the *unmodified* composition styles so that primary
        // segments without an override resolve back to this same context.
        var primaryKey = new SourceKey(
            NormalizePath(project.VideoFilePath), composition.Background, composition.Cursor);

        SourceContext? primary = null;
        try
        {
            primary = await BuildContextAsync(
                device,
                project.VideoFilePath,
                ApplyCursorAvailability(composition, primaryMouseData),
                primaryMouseData,
                sourceWidth: project.Width,
                sourceHeight: project.Height,
                duration: ResolveSourceDuration(timeline, project.VideoFilePath, project.Duration),
                mouseToVideoOffsetSeconds: project.MouseToVideoOffsetSeconds,
                cropOffsetX: project.CropOffsetX,
                cropOffsetY: project.CropOffsetY,
                dpiScale: project.DpiScale,
                webcamFilePath: project.WebcamFilePath,
                recordingFps: project.Fps > 0 ? project.Fps : fps,
                timeline: timeline,
                ct);

            return new SegmentFrameComposer(
                project, primaryMouseData, composition, timeline, mapper, fps,
                device, primaryKey, primary);
        }
        catch
        {
            primary?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Composes the output frame at <paramref name="frameIndex"/> at
    /// <see cref="OutputWidth"/>×<see cref="OutputHeight"/>. The caller owns (and must
    /// dispose) the returned render target.
    /// </summary>
    public async Task<CanvasRenderTarget> ComposeFrameAsync(int frameIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);

        var frame = await ComposeSegmentFrameAsync(frameIndex, ct);

        if (_timeline is null || _timeline.Segments.Count == 0)
            return frame;

        // Soft cut: dissolve the outgoing segment into the incoming one instead of
        // hard-cutting. TransitionResolver is the shared pure function the preview also
        // calls, so both pipelines dissolve at the same instants with the same effect,
        // duration and easing (configured, or the legacy 500ms slide-only fallback).
        var outputTime = TimeSpan.FromSeconds(FrameTimeConverter.FrameToTime(frameIndex, _fps));
        var resolution = TransitionResolver.Resolve(_timeline, outputTime);
        if (!resolution.Active)
            return frame;

        try
        {
            // The outgoing side is composed straight from OutgoingLocalOffset, a TimeSpan
            // that deliberately rolls past OutgoingSegment's own duration (see its doc
            // comment on TransitionResolution) rather than being converted into an output
            // frame index and re-resolved through the timeline. That supersedes the old
            // Floor-not-Round frame-index dance this block used to need here: the previous
            // legacy path derived a frame index from a frozen "current.Start - 1 tick"
            // instant, and rounding it would have snapped onto the incoming segment's own
            // frame and blended it with itself. There is no frame index to round anymore,
            // so that particular failure mode cannot recur -- this note exists only so a
            // future reader doesn't wonder whether it was overlooked. A *different* form of
            // "blended with itself" can still occur -- a same-source contiguous split, where
            // rolling and freezing land on the identical source instant -- which is why the
            // incoming-side source time is resolved below and handed to
            // ComposeSegmentAtOffsetAsync so it can apply the contiguous-boundary policy
            // (see CollapseContiguousSourceBoundary).
            string? incomingVideoFilePath = null;
            double? incomingSourceTimeSeconds = null;
            double incomingSourceStartSeconds = 0;
            if (resolution.IncomingSegment is VideoSegment incomingVideo)
            {
                // The transition's elapsed local time, re-derived from OutgoingLocalOffset
                // (== OutgoingSegment.Duration + local, per TransitionResolution's contract)
                // rather than recomputed from outputTime, so this always agrees exactly with
                // what the outgoing side is about to sample.
                var local = resolution.OutgoingLocalOffset - resolution.OutgoingSegment!.Duration;
                double incomingSpeed = incomingVideo.SpeedFactor > 0 ? incomingVideo.SpeedFactor : 1.0;
                incomingVideoFilePath = incomingVideo.VideoFilePath;
                incomingSourceStartSeconds = incomingVideo.SourceStart.TotalSeconds;
                incomingSourceTimeSeconds = MapLocalOffsetToSourceTime(
                    incomingVideo.SourceStart.TotalSeconds, local.TotalSeconds, incomingSpeed);
            }

            using var outgoing = await ComposeSegmentAtOffsetAsync(
                resolution.OutgoingSegment!, resolution.OutgoingLocalOffset, ct,
                incomingVideoFilePath, incomingSourceTimeSeconds, incomingSourceStartSeconds);

            _transitionRenderer ??= new TransitionRenderer(_device);
            var blended = _transitionRenderer.Render(
                outgoing, frame, resolution.Type, resolution.EasedProgress,
                OutputWidth, OutputHeight);
            frame.Dispose();
            return blended;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Selects the manual zoom keyframes that belong to <paramref name="videoFilePath"/>.
    /// A keyframe with no <see cref="ZoomKeyframe.SourceVideoFilePath"/> belongs to the
    /// primary recording; every other keyframe belongs to the file it names. This keeps
    /// zooms created on an appended clip off the primary recording's compositor (and vice
    /// versa), matching how the editor maps zoom segments onto clips.
    /// </summary>
    public static List<ZoomKeyframe> SelectManualZoomKeyframes(TimelineModel timeline, string videoFilePath)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        return timeline.ZoomKeyframes
            .Where(k => k.IsManual && BelongsToSource(k, videoFilePath, timeline.PrimaryVideoFilePath))
            .ToList();
    }

    /// <summary>
    /// Selects the text overlays authored against <paramref name="videoFilePath"/>'s
    /// source-time space. An overlay with no <see cref="TextOverlaySegment.SourceVideoFilePath"/>
    /// belongs to the primary recording — the identical ownership convention
    /// <see cref="SelectManualZoomKeyframes"/> uses for <see cref="ZoomKeyframe"/> (both share
    /// the <see cref="BelongsToSource(string?, string, string?)"/> rule below), so a text
    /// overlay authored on an appended clip never bleeds onto the primary recording's
    /// compositor, or vice versa.
    /// </summary>
    public static List<TextOverlaySegment> SelectTextOverlays(TimelineModel timeline, string videoFilePath)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        return timeline.TextOverlays
            .Where(o => BelongsToSource(o.SourceVideoFilePath, videoFilePath, timeline.PrimaryVideoFilePath))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="keyframe"/> was authored against
    /// <paramref name="videoFilePath"/>'s source-time space.
    /// </summary>
    public static bool BelongsToSource(
        ZoomKeyframe keyframe, string videoFilePath, string? primaryVideoFilePath)
    {
        ArgumentNullException.ThrowIfNull(keyframe);

        return BelongsToSource(keyframe.SourceVideoFilePath, videoFilePath, primaryVideoFilePath);
    }

    /// <summary>
    /// Shared ownership rule behind <see cref="BelongsToSource(ZoomKeyframe, string, string?)"/>
    /// and <see cref="SelectTextOverlays"/>: <paramref name="sourceVideoFilePath"/> of
    /// <see langword="null"/> means "the primary recording", otherwise it must name
    /// <paramref name="videoFilePath"/> exactly (case-insensitively, since these are file paths).
    /// </summary>
    private static bool BelongsToSource(
        string? sourceVideoFilePath, string videoFilePath, string? primaryVideoFilePath)
    {
        return sourceVideoFilePath is null
            ? primaryVideoFilePath is null
              || string.Equals(videoFilePath, primaryVideoFilePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(videoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase);
    }

    #region Frame composition

    private async Task<CanvasRenderTarget> ComposeSegmentFrameAsync(int frameIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_timeline is null || _timeline.Segments.Count == 0)
        {
            // Legacy (pre-segment) projects: the whole export is the primary recording,
            // mapped through trim/cut/speed by the timeline mapper.
            double legacyTime = _mapper?.GetSourceTimeForOutputFrame(frameIndex)
                ?? FrameTimeConverter.FrameToTime(frameIndex, _fps);
            return await ComposeWithContextAsync(_primaryContext, legacyTime, applyCameraSegments: true, ct);
        }

        var outputTime = TimeSpan.FromSeconds(FrameTimeConverter.FrameToTime(frameIndex, _fps));
        var (segment, localOffset) = _timeline.GetSegmentAtTime(outputTime);

        // Only this (timeline-resolved) path can ever hand back a null segment -- the
        // rolling transition path below always resolves a real OutgoingSegment/
        // IncomingSegment pair, so ComposeSegmentAtOffsetAsync does not need to handle it.
        if (segment is null)
        {
            throw new InvalidOperationException(
                $"No timeline segment covers output frame {frameIndex} ({outputTime}).");
        }

        return await ComposeSegmentAtOffsetAsync(segment, localOffset, ct);
    }

    /// <summary>
    /// Composes <paramref name="segment"/> at <paramref name="localOffset"/> into its own
    /// local timeline -- the shared body behind both the normal per-frame path
    /// (<see cref="ComposeSegmentFrameAsync"/>, where the offset always lies within the
    /// segment) and the transition path (<see cref="ComposeFrameAsync"/>, where
    /// <paramref name="localOffset"/> is a
    /// <see cref="Timeline.TransitionResolution.OutgoingLocalOffset"/> that deliberately
    /// EXCEEDS the segment's own <see cref="TimelineSegment.Duration"/> so the outgoing side
    /// of a dissolve can keep rolling past its own cut point instead of freezing).
    /// </summary>
    /// <param name="incomingVideoFilePath">
    /// Only meaningful (non-null) when composing the OUTGOING side of an active transition
    /// AND the transition's incoming segment is itself a <see cref="VideoSegment"/>: that
    /// segment's source file, used by the <see cref="VideoSegment"/> branch below to detect a
    /// same-source contiguous boundary. Left null (the default) for the normal per-frame path,
    /// where there is no "incoming side" to collide with.
    /// </param>
    /// <param name="incomingSourceTimeSeconds">
    /// The incoming side's own mapped source-file time for this exact instant, alongside
    /// <paramref name="incomingVideoFilePath"/> -- see
    /// <see cref="CollapseContiguousSourceBoundary"/> for how the two are used together.
    /// </param>
    /// <remarks>
    /// Each segment type is responsible for making an over-long offset degrade sensibly:
    /// <see cref="TextSlideSegment"/> already clamps its animation progress to [0, 1], so it
    /// naturally holds the slide's final animated frame. <see cref="VideoSegment"/> maps the
    /// offset into an absolute source-file time, clamps THAT against whatever footage is
    /// actually readable (see <see cref="ClampSourceTime"/>), and then -- only when
    /// <paramref name="incomingSourceTimeSeconds"/> is supplied -- applies the
    /// contiguous-boundary policy so a same-source split never blends a frame with itself.
    /// </remarks>
    private async Task<CanvasRenderTarget> ComposeSegmentAtOffsetAsync(
        TimelineSegment segment,
        TimeSpan localOffset,
        CancellationToken ct,
        string? incomingVideoFilePath = null,
        double? incomingSourceTimeSeconds = null,
        double incomingSourceStartSeconds = 0)
    {
        ct.ThrowIfCancellationRequested();

        switch (segment)
        {
            case TextSlideSegment slide:
            {
                double progress = slide.Duration.TotalSeconds > 0
                    ? Math.Clamp(localOffset.TotalSeconds / slide.Duration.TotalSeconds, 0, 1)
                    : 0;
                _textSlideRenderer ??= new TextSlideRenderer(_device);
                return _textSlideRenderer.RenderSlide(slide, progress, OutputWidth, OutputHeight);
            }

            case VideoSegment video:
            {
                double speed = video.SpeedFactor > 0 ? video.SpeedFactor : 1.0;
                var context = await GetOrCreateContextAsync(video, ct);
                bool isPrimary = IsPrimarySource(video.VideoFilePath);

                double availableDuration = ResolveAvailableSourceDuration(context, video);
                var (sourceTime, _) = ClampSourceTime(
                    video.SourceStart.TotalSeconds, localOffset.TotalSeconds, speed, availableDuration);

                sourceTime = CollapseContiguousSourceBoundary(
                    sourceTime, video.VideoFilePath,
                    incomingSourceTimeSeconds, incomingVideoFilePath, video.Fps,
                    (video.SourceStart + video.SourceDuration).TotalSeconds,
                    incomingSourceStartSeconds);

                return await ComposeWithContextAsync(context, sourceTime, isPrimary, ct);
            }

            default:
                throw new InvalidOperationException(
                    $"Export does not support timeline segments of type '{segment.GetType().Name}'.");
        }
    }

    /// <summary>
    /// The most reliable, cheaply-available bound on how much of <paramref name="video"/>'s
    /// source FILE can actually be read -- used to clamp a rolling
    /// <see cref="Timeline.TransitionResolution.OutgoingLocalOffset"/> once it dips past the
    /// segment's own edited cut point (<c>SourceStart + SourceDuration</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preferred bound: <see cref="VideoFrameReader.Duration"/>. It is derived from the
    /// reader's actual frame count (captured JPEGs, or the decoded MP4) divided by its fps,
    /// so it reflects footage that genuinely exists in the file -- independent of how far the
    /// timeline happened to trim it. A segment cut well before end-of-file can therefore roll
    /// into the real handle that exists past the cut, exactly as the transition model intends.
    /// <see cref="VideoFrameReader.LoadFrameAtTimeAsync"/> already clamps its frame index to
    /// <c>[0, FrameCount - 1]</c> (see <c>VideoFrameReader.cs</c>), so sampling exactly at (or
    /// even fractionally past) this bound is always safe -- no back-off needed here.
    /// </para>
    /// <para>
    /// Next preference: <see cref="SourceContext.SourceComposition"/>'s own
    /// <c>MediaComposition.Duration</c> (used elsewhere in this file at frame-extraction time,
    /// see <see cref="ExtractFrameFromCompositionAsync"/>) -- synchronously available with no
    /// per-frame I/O, so it costs nothing to consult when <see cref="SourceContext.Reader"/>
    /// is null (the rare composition-only fallback path built by <see cref="BuildContextAsync"/>
    /// when neither captured frames nor an mp4 decoder were available). Unlike the reader
    /// count, this duration is an EXCLUSIVE endpoint -- <c>GetThumbnailAsync</c> has no
    /// clamping of its own for it -- so the bound is backed off by one source frame
    /// (<see cref="VideoSegment.Fps"/>, or 30fps if that is ever non-positive) to land the
    /// held sample just inside the valid range rather than exactly on its exclusive edge.
    /// </para>
    /// <para>
    /// Only when NEITHER of those is available does this fall back to the segment's own
    /// trimmed cut point, which reproduces today's frozen-on-last-frame behaviour for that
    /// source -- never worse than the pre-existing status quo.
    /// </para>
    /// </remarks>
    private static double ResolveAvailableSourceDuration(SourceContext context, VideoSegment video)
    {
        return SelectAvailableSourceDuration(
            context.Reader?.Duration,
            context.SourceComposition?.Duration,
            video.SourceStart + video.SourceDuration,
            video.Fps);
    }

    /// <summary>
    /// Pure precedence rule behind <see cref="ResolveAvailableSourceDuration"/>, extracted so
    /// it is directly unit-testable without a live <see cref="SourceContext"/> (which needs a
    /// GPU <see cref="CanvasDevice"/> and real media to construct) -- see
    /// <c>SegmentFrameComposerTransitionTests</c>.
    /// </summary>
    /// <param name="readerDuration">
    /// <see cref="VideoFrameReader.Duration"/> from <see cref="SourceContext.Reader"/>, or
    /// null when no reader is open. Preferred whenever present and non-zero -- see the
    /// caller's remarks for why.
    /// </param>
    /// <param name="compositionDuration">
    /// <c>MediaComposition.Duration</c> from <see cref="SourceContext.SourceComposition"/>, or
    /// null when no composition is open. Used only when <paramref name="readerDuration"/>
    /// is unavailable; backed off by one source frame since it is an exclusive endpoint.
    /// </param>
    /// <param name="cutPoint">
    /// The segment's own trimmed cut point (<c>SourceStart + SourceDuration</c>) -- the final
    /// fallback when neither of the above is available.
    /// </param>
    /// <param name="fps">The segment's recording fps, for the composition-duration back-off.</param>
    internal static double SelectAvailableSourceDuration(
        TimeSpan? readerDuration, TimeSpan? compositionDuration, TimeSpan cutPoint, int fps)
    {
        if (readerDuration is { } reader && reader > TimeSpan.Zero)
            return reader.TotalSeconds;

        if (compositionDuration is { } composition && composition > TimeSpan.Zero)
        {
            double frameStep = fps > 0 ? 1.0 / fps : 1.0 / 30;
            return Math.Max(0, composition.TotalSeconds - frameStep);
        }

        return cutPoint.TotalSeconds;
    }

    /// <summary>
    /// Maps a local segment offset (in output-time seconds) into an absolute source-file
    /// time, applying the segment's own playback speed. Shared by <see cref="ClampSourceTime"/>
    /// (for the outgoing side, which also clamps against available footage) and
    /// <see cref="ComposeFrameAsync"/> (for the incoming side of an active transition, which
    /// needs the same raw mapping -- unclamped, since the incoming side is never past its own
    /// cut point -- to feed <see cref="CollapseContiguousSourceBoundary"/>).
    /// </summary>
    internal static double MapLocalOffsetToSourceTime(
        double sourceStartSeconds, double localOffsetSeconds, double speed)
        => sourceStartSeconds + localOffsetSeconds * speed;

    /// <summary>
    /// Pure arithmetic behind the <see cref="VideoSegment"/> branch of
    /// <see cref="ComposeSegmentAtOffsetAsync"/>: maps a (possibly past-cut) local segment
    /// offset into an absolute source-file time, then clamps it to whatever footage is
    /// actually readable so a transition can never seek negative or past the end of the file.
    /// Extracted as pure, static, internal-visible arithmetic so it is directly unit-testable
    /// without a GPU device or real media (see <c>SegmentFrameComposerTransitionTests</c>).
    /// </summary>
    /// <param name="sourceStartSeconds">
    /// <see cref="VideoSegment.SourceStart"/> in seconds -- the absolute source-file time the
    /// segment's own local time zero maps to.
    /// </param>
    /// <param name="localOffsetSeconds">
    /// Offset into the segment's own local timeline, in output-time seconds (not yet
    /// speed-adjusted). May exceed the segment's own duration -- see
    /// <see cref="Timeline.TransitionResolution.OutgoingLocalOffset"/>.
    /// </param>
    /// <param name="speed">
    /// Playback speed multiplier. Callers must already resolve this to a positive value
    /// (matching the existing <c>SpeedFactor &gt; 0 ? SpeedFactor : 1.0</c> convention).
    /// </param>
    /// <param name="availableDurationSeconds">
    /// The best known bound on readable source-file footage -- see
    /// <see cref="ResolveAvailableSourceDuration"/> for how call sites choose it. A value
    /// &lt;= 0 means "unknown": only the zero floor applies below, with no upper clamp.
    /// </param>
    /// <returns>
    /// The clamped absolute source-file time to sample, and whether it was held at
    /// <paramref name="availableDurationSeconds"/> because the raw mapped time ran past the
    /// available footage (i.e. this instant is showing a frozen last frame rather than truly
    /// rolling footage).
    /// </returns>
    internal static (double SourceTimeSeconds, bool Held) ClampSourceTime(
        double sourceStartSeconds, double localOffsetSeconds, double speed, double availableDurationSeconds)
    {
        double raw = MapLocalOffsetToSourceTime(sourceStartSeconds, localOffsetSeconds, speed);
        double floored = Math.Max(0, raw);

        if (availableDurationSeconds > 0 && floored > availableDurationSeconds)
            return (availableDurationSeconds, true);

        return (floored, false);
    }

    /// <summary>
    /// Same-source contiguous-boundary policy: when a transition's OUTGOING and INCOMING
    /// sides read the <b>same</b> source file, and the outgoing side's rolled/footage-clamped
    /// source time would land at or past the incoming side's own source time for this exact
    /// instant, there is no independent footage left to roll into -- the "roll" would
    /// definitionally be sampling the same frames the incoming side already shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the split-boundary hazard: a plain (untrimmed) split of one recording into two
    /// segments sets <c>incoming.SourceStart == outgoing.SourceStart + outgoing.SourceDuration</c>,
    /// so <c>OutgoingLocalOffset = outgoing.Duration + local</c> maps to EXACTLY the same
    /// source instant the incoming side maps to for every instant of the transition window
    /// (when both sides share one <see cref="VideoSegment.SpeedFactor"/>; even when they
    /// don't, the two curves still cross at the shared cut point and immediately invert past
    /// it). Left unhandled, the outgoing frame and the incoming frame are pixel-identical (or
    /// momentarily backwards) for the whole dissolve -- CrossFade renders as a static, unmoving
    /// frame, and directional effects (Push/Slide/Wipe) show identical content sliding past
    /// itself. Neither looks like a transition at all.
    /// </para>
    /// <para>
    /// Freezing loses nothing in exactly this situation: there is no "independent outgoing
    /// content" the incoming side isn't already about to show, so this collapses back to the
    /// legacy semantics -- freeze the outgoing side one source frame short of the incoming's
    /// instant -- which renders a real, visible dissolve instead of a silent no-op.
    /// </para>
    /// <para>
    /// A <b>trimmed</b> split (the user cut a gap out of the incoming side, or the two sides
    /// run at different <see cref="VideoSegment.SpeedFactor"/>s) can leave a genuine gap
    /// between the two source times for some or all of the window; rolling is entirely
    /// legitimate there; and the two sources must simply be different files.
    /// </para>
    /// </remarks>
    /// <param name="outgoingSourceTimeSeconds">
    /// The outgoing side's already-mapped, already footage-clamped (via
    /// <see cref="ClampSourceTime"/>) source-file time for this instant.
    /// </param>
    /// <param name="outgoingVideoFilePath">The outgoing segment's source file.</param>
    /// <param name="incomingSourceTimeSeconds">
    /// The incoming side's mapped source-file time for the SAME instant (via
    /// <see cref="MapLocalOffsetToSourceTime"/>), or null when the incoming segment is not a
    /// <see cref="VideoSegment"/> (e.g. a text slide) -- there is no shared source-time space
    /// to collide in, so this method is then a no-op.
    /// </param>
    /// <param name="incomingVideoFilePath">
    /// The incoming segment's source file, or null alongside
    /// <paramref name="incomingSourceTimeSeconds"/>.
    /// </param>
    /// <param name="fps">
    /// The outgoing segment's own recording fps (<see cref="VideoSegment.Fps"/>), used to
    /// compute one frame's worth of time to back off by. Values &lt;= 0 (should not happen --
    /// <see cref="VideoSegment.Fps"/> defaults to 30 -- but guarded defensively since a
    /// division by a bad value here would otherwise corrupt the result) fall back to a 30fps
    /// step.
    /// </param>
    /// <param name="outgoingCutPointSeconds">
    /// The outgoing segment's own cut point in source time
    /// (<c>SourceStart + SourceDuration</c>). Together with
    /// <paramref name="incomingSourceStartSeconds"/> this proves whether the two ranges
    /// actually MEET, which is what makes a boundary a contiguous split rather than merely
    /// two clips that happen to share a file.
    /// </param>
    /// <param name="incomingSourceStartSeconds">The incoming segment's <c>SourceStart</c>.</param>
    /// <returns>
    /// <paramref name="outgoingSourceTimeSeconds"/> unchanged when the two sides read
    /// different files, when their source ranges do not meet at the boundary (reordered or
    /// unrelated ranges), or when there is a genuine gap between the two mapped source times;
    /// otherwise the incoming source time minus one frame step (floored at zero), so the two
    /// sides can never sample identical -- or momentarily reversed -- footage.
    /// </returns>
    /// <remarks>
    /// <b>Public (promoted from <c>internal</c> in T8, the transitions-feature integration
    /// pass):</b> this is the ONE part of the export/preview outgoing-side arithmetic that
    /// genuinely had no substitute elsewhere in <c>Musio.App</c> -- unlike
    /// <see cref="ClampSourceTime"/>, whose footage-availability clamp is already reproduced
    /// for free by <see cref="VideoFrameReader.GetFrameIndex"/>'s own
    /// <c>[0, FrameCount - 1]</c> clamp (see that method's remarks) -- so
    /// <c>EditorPage.xaml.cs</c> (the live-preview pipeline, T3) had re-implemented an
    /// identical private copy rather than leave the same-source-contiguous-split bug fixed on
    /// export only. T8 verified the two copies were byte-for-byte identical and deleted the
    /// preview-side duplicate in favour of this shared, testable implementation, so preview
    /// and export now provably call the exact same code for this policy.
    /// </remarks>
    public static double CollapseContiguousSourceBoundary(
        double outgoingSourceTimeSeconds,
        string outgoingVideoFilePath,
        double? incomingSourceTimeSeconds,
        string? incomingVideoFilePath,
        int fps,
        double outgoingCutPointSeconds,
        double incomingSourceStartSeconds)
    {
        if (incomingSourceTimeSeconds is not { } incomingTime)
            return outgoingSourceTimeSeconds;

        if (!string.Equals(outgoingVideoFilePath, incomingVideoFilePath, StringComparison.OrdinalIgnoreCase))
            return outgoingSourceTimeSeconds;

        double frameStep = fps > 0 ? 1.0 / fps : 1.0 / 30;

        // Sharing a file is NOT enough to prove the two sides are a split of one continuous
        // run. Reordered clips read the same file from unrelated ranges -- e.g. [10s,14s]
        // followed by [2s,6s] -- and there the outgoing frame at 14s is entirely legitimate
        // even though it sits "after" the incoming's 2s. Collapsing on the timestamp
        // comparison alone would yank the outgoing image backwards by twelve seconds at the
        // start of the dissolve. Only a boundary whose ranges actually MEET (the outgoing's
        // cut point is the incoming's in-point) is a contiguous split.
        if (Math.Abs(outgoingCutPointSeconds - incomingSourceStartSeconds) > frameStep)
            return outgoingSourceTimeSeconds;

        if (outgoingSourceTimeSeconds < incomingTime)
            return outgoingSourceTimeSeconds; // Genuine trimmed gap: rolling is legitimate.

        return Math.Max(0, incomingTime - frameStep);
    }

    /// <summary>
    /// Renders one frame of a source recording through its own compositor.
    /// <paramref name="applyCameraSegments"/> is only set for the primary recording:
    /// camera segments are expressed in the primary recording's source time.
    /// </summary>
    private async Task<CanvasRenderTarget> ComposeWithContextAsync(
        SourceContext context, double sourceTimeSeconds, bool applyCameraSegments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        double clampedTime = Math.Max(0, sourceTimeSeconds);
        var position = TimeSpan.FromSeconds(clampedTime);
        var compositor = context.Compositor;

        CanvasBitmap? webcamFrame = null;
        try
        {
            if (context.WebcamComposition is not null)
            {
                bool showWebcam = !applyCameraSegments
                    || ExportEngine.ApplyCameraSegmentState(
                        compositor, _timeline, _composition.WebcamStyle, position);

                if (!showWebcam)
                {
                    compositor.SetWebcamFrame(null);
                }
                else
                {
                    try
                    {
                        webcamFrame = await ExtractFrameFromCompositionAsync(
                            _device, context.WebcamComposition, position,
                            context.WebcamExtractWidth, context.WebcamExtractHeight);
                        compositor.SetWebcamFrame(webcamFrame);
                    }
                    catch (Exception ex)
                    {
                        // A single unreadable webcam thumbnail must not fail the export;
                        // the frame is composited without the overlay.
                        Debug.WriteLine(
                            $"[SegmentFrameComposer] Webcam frame at {position} unavailable: {ex.Message}");
                        compositor.SetWebcamFrame(null);
                    }
                }
            }

            using var sourceFrame = await LoadSourceFrameAsync(context, position);
            return EnsureCanonicalSize(compositor.ComposeFrame(sourceFrame, clampedTime));
        }
        finally
        {
            // The compositor never owns the webcam bitmap — detach before disposing.
            compositor.SetWebcamFrame(null);
            webcamFrame?.Dispose();
        }
    }

    private async Task<CanvasBitmap> LoadSourceFrameAsync(SourceContext context, TimeSpan position)
    {
        if (context.Reader is not null)
        {
            var frame = await context.Reader.LoadFrameAtTimeAsync(position);
            if (frame is not null)
                return frame;
        }

        if (context.SourceComposition is not null)
        {
            return await ExtractFrameFromCompositionAsync(
                _device, context.SourceComposition, position,
                context.SourceWidth, context.SourceHeight);
        }

        // The JPEG frame could not be loaded and there is no open composition —
        // decode the frame straight from the video file.
        return await FallbackExtractFrameAsync(
            _device, context.VideoFilePath, position, context.SourceWidth, context.SourceHeight);
    }

    /// <summary>
    /// Appended recordings can have a different resolution than the primary one, but a
    /// video stream has a single frame size. Frames from such a source are letterboxed
    /// (aspect preserved) into the canonical output size instead of being stretched.
    /// Takes ownership of <paramref name="frame"/>.
    /// </summary>
    private CanvasRenderTarget EnsureCanonicalSize(CanvasRenderTarget frame)
    {
        if (frame.SizeInPixels.Width == (uint)OutputWidth &&
            frame.SizeInPixels.Height == (uint)OutputHeight)
        {
            return frame;
        }

        try
        {
            double sourceWidth = frame.SizeInPixels.Width;
            double sourceHeight = frame.SizeInPixels.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new InvalidOperationException("Composed frame has no pixels.");

            var target = Win2DUtils.CreateRenderTarget(_device, OutputWidth, OutputHeight, 96, "canonical-size letterbox frame");
            try
            {
                double scale = Math.Min(OutputWidth / sourceWidth, OutputHeight / sourceHeight);
                double width = sourceWidth * scale;
                double height = sourceHeight * scale;

                using var ds = target.CreateDrawingSession();
                ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                ds.DrawImage(
                    frame,
                    new Rect((OutputWidth - width) / 2, (OutputHeight - height) / 2, width, height),
                    new Rect(0, 0, sourceWidth, sourceHeight));
            }
            catch
            {
                target.Dispose();
                throw;
            }

            return target;
        }
        finally
        {
            frame.Dispose();
        }
    }

    #endregion

    #region Source contexts

    private async Task<SourceContext> GetOrCreateContextAsync(VideoSegment segment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(segment.VideoFilePath))
            throw new InvalidOperationException("A video segment on the timeline has no source file.");

        var background = segment.FrameStyleOverride ?? _composition.Background;
        var cursor = segment.CursorStyleOverride ?? _composition.Cursor;
        var key = new SourceKey(NormalizePath(segment.VideoFilePath), background, cursor);

        if (_contexts.TryGetValue(key, out var cached))
            return cached;

        bool isPrimary = IsPrimarySource(segment.VideoFilePath);
        var mouseData = isPrimary
            ? _primaryMouseData
            : LoadMouseData(segment.CursorDataFilePath);

        var config = _composition with { Background = background, Cursor = cursor };
        if (!isPrimary)
        {
            // Keyboard events and subtitles are timestamped in the primary recording's
            // timebase, so they must not be replayed over an appended recording.
            config = config with
            {
                KeyboardStyle = null,
                KeyboardEvents = null,
                SubtitleStyle = null,
                Subtitles = null,
            };

            // An appended recording can carry its own camera even when the primary
            // recording has none; the compositor only builds its webcam layer when a
            // style is present.
            if (!string.IsNullOrWhiteSpace(segment.WebcamFilePath) && File.Exists(segment.WebcamFilePath))
                config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };
        }
        config = ApplyCursorAvailability(config, mouseData);

        int recordingFps = isPrimary ? _project.Fps : segment.Fps;
        if (recordingFps <= 0) recordingFps = _fps;

        var context = await BuildContextAsync(
            _device,
            segment.VideoFilePath,
            config,
            mouseData,
            sourceWidth: isPrimary ? _project.Width : segment.SourceWidth,
            sourceHeight: isPrimary ? _project.Height : segment.SourceHeight,
            duration: ResolveSourceDuration(
                _timeline,
                segment.VideoFilePath,
                isPrimary ? _project.Duration : segment.SourceStart + segment.SourceDuration),
            mouseToVideoOffsetSeconds: isPrimary
                ? _project.MouseToVideoOffsetSeconds
                : segment.MouseToVideoOffsetSeconds,
            cropOffsetX: isPrimary ? _project.CropOffsetX : segment.CropOffsetX,
            cropOffsetY: isPrimary ? _project.CropOffsetY : segment.CropOffsetY,
            dpiScale: isPrimary ? _project.DpiScale : segment.DpiScale,
            webcamFilePath: isPrimary ? _project.WebcamFilePath : segment.WebcamFilePath,
            recordingFps: recordingFps,
            timeline: _timeline,
            ct);

        // Disposal can only race a build if the caller broke the "serialize composition"
        // contract (or cancelled without awaiting); release rather than orphan the
        // context's compositor and file handles.
        if (_disposed)
        {
            context.Dispose();
            throw new ObjectDisposedException(nameof(SegmentFrameComposer));
        }

        _contexts[key] = context;
        return context;
    }

    private static async Task<SourceContext> BuildContextAsync(
        CanvasDevice device,
        string videoFilePath,
        CompositionConfig config,
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        TimeSpan duration,
        double mouseToVideoOffsetSeconds,
        int cropOffsetX,
        int cropOffsetY,
        float dpiScale,
        string? webcamFilePath,
        int recordingFps,
        TimelineModel? timeline,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Captured JPEG frames are preferred when they still exist, otherwise frames are
        // decoded from the finalized MP4. Either way they are indexed with the RECORDING fps.
        var reader = await VideoFrameReader.OpenFromVideoPathAsync(
            videoFilePath, recordingFps > 0 ? recordingFps : 30);

        // Only open the video file when it is actually needed: as the frame source when
        // no captured frames exist, or to recover dimensions the recording metadata does
        // not carry. `MediaClip` is not closable — it releases its file handle only when
        // collected — so an unnecessary open would hold a handle for the whole export.
        // Recorded dimensions match the video exactly (both come from the capture size,
        // already forced even), so reading them from the file adds nothing here.
        MediaClip? sourceClip = null;
        if (reader is null || sourceWidth <= 0 || sourceHeight <= 0)
        {
            try
            {
                var sourceFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoFilePath));
                sourceClip = await MediaClip.CreateFromFileAsync(sourceFile);
                var props = sourceClip.GetVideoEncodingProperties();
                if (props.Width > 0 && props.Height > 0)
                {
                    sourceWidth = (int)props.Width;
                    sourceHeight = (int)props.Height;
                }
            }
            catch (Exception ex)
            {
                // The MP4 can be missing or unfinalized; captured JPEG frames can still
                // provide the visuals, so fall back to the recorded dimensions.
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Could not open '{videoFilePath}' (using captured frames): {ex.Message}");
            }
        }

        if (sourceWidth <= 0) sourceWidth = 1920;
        if (sourceHeight <= 0) sourceHeight = 1080;

        var context = new SourceContext
        {
            VideoFilePath = videoFilePath,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Reader = reader,
        };

        try
        {
            if (context.Reader is null)
            {
                if (sourceClip is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot export: no captured frames and no readable video for '{videoFilePath}'.");
                }

                var composition = new MediaComposition();
                composition.Clips.Add(sourceClip);
                context.SourceComposition = composition;
            }

            var compositor = new FrameCompositor(config);
            context.Compositor = compositor;
            await compositor.InitializeAsync(
                mouseData, sourceWidth, sourceHeight, EnsureRenderableDuration(duration),
                mouseToVideoOffsetSeconds, cropOffsetX, cropOffsetY, dpiScale);

            SyncZoomState(compositor, timeline, videoFilePath);
            await AttachWebcamAsync(context, webcamFilePath, config, timeline);

            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static async Task AttachWebcamAsync(
        SourceContext context, string? webcamFilePath, CompositionConfig config, TimelineModel? timeline)
    {
        if (string.IsNullOrWhiteSpace(webcamFilePath) || !File.Exists(webcamFilePath))
            return;

        try
        {
            var webcamFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(webcamFilePath));
            var webcamClip = await MediaClip.CreateFromFileAsync(webcamFile);
            var props = webcamClip.GetVideoEncodingProperties();
            int nativeWidth = (int)props.Width;
            int nativeHeight = (int)props.Height;

            var composition = new MediaComposition();
            composition.Clips.Add(webcamClip);
            context.WebcamComposition = composition;

            // Extract at ~1.5x the overlay display size instead of full resolution.
            // When a camera segment animates to fullscreen, extract at native
            // resolution so the enlarged webcam stays sharp.
            float displaySize = (config.WebcamStyle?.Size ?? 300f) * 1.5f;
            float minDimension = Math.Min(nativeWidth, nativeHeight);
            if (ExportEngine.TimelineHasFullscreenCamera(timeline) || minDimension <= displaySize)
            {
                context.WebcamExtractWidth = nativeWidth;
                context.WebcamExtractHeight = nativeHeight;
            }
            else
            {
                float scale = displaySize / minDimension;
                context.WebcamExtractWidth = Math.Max((int)Math.Ceiling(nativeWidth * scale), 1);
                context.WebcamExtractHeight = Math.Max((int)Math.Ceiling(nativeHeight * scale), 1);
            }
        }
        catch (Exception ex)
        {
            // Without the overlay the export is still correct, just camera-less.
            Debug.WriteLine($"[SegmentFrameComposer] Failed to load webcam '{webcamFilePath}': {ex.Message}");
            context.WebcamComposition?.Clips.Clear();
            context.WebcamComposition = null;
        }
    }

    /// <summary>
    /// Applies every per-source-file piece of state a compositor needs beyond its base
    /// <see cref="CompositionConfig"/>: manual zoom keyframes, suppressed auto-zoom clicks,
    /// and text overlays, each filtered to the ones belonging to
    /// <paramref name="videoFilePath"/>. Called for both the primary recording's context
    /// and every appended/imported recording's context, so overlays and zooms authored
    /// against one source never bleed onto another.
    /// </summary>
    private static void SyncZoomState(FrameCompositor compositor, TimelineModel? timeline, string videoFilePath)
    {
        if (timeline is null) return;

        compositor.SyncManualZoomKeyframes(SelectManualZoomKeyframes(timeline, videoFilePath));
        compositor.SyncTextOverlays(SelectTextOverlays(timeline, videoFilePath));

        // Suppressed ticks are raw click timestamps from the recording that produced
        // them, so they can only ever match that recording's auto-zoom segments.
        if (timeline.SuppressedClickTicks.Count > 0)
            compositor.SyncSuppressedClickTicks(timeline.SuppressedClickTicks);
    }

    private bool IsPrimarySource(string videoFilePath)
    {
        if (string.Equals(videoFilePath, _project.VideoFilePath, StringComparison.OrdinalIgnoreCase))
            return true;

        return _timeline?.PrimaryVideoFilePath is { } primary
            && string.Equals(videoFilePath, primary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Longest source extent used by <paramref name="videoFilePath"/> on the timeline, so
    /// the compositor's cursor/zoom timelines cover every segment cut from that file.
    /// </summary>
    private static TimeSpan ResolveSourceDuration(
        TimelineModel? timeline, string videoFilePath, TimeSpan fallback)
    {
        var longest = fallback;

        if (timeline is not null)
        {
            foreach (var segment in timeline.Segments.OfType<VideoSegment>())
            {
                if (!string.Equals(segment.VideoFilePath, videoFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var end = segment.SourceStart + segment.SourceDuration;
                if (end > longest) longest = end;
            }
        }

        return longest;
    }

    /// <summary>
    /// A compositor builds one cursor sample per output frame from this duration, and
    /// composing a frame requires at least one. Sub-second (or degenerate) source extents
    /// are therefore floored to one second; the extra samples merely hold the last cursor
    /// position and are never rendered.
    /// </summary>
    private static TimeSpan EnsureRenderableDuration(TimeSpan duration)
    {
        var minimum = TimeSpan.FromSeconds(1);
        return duration > minimum ? duration : minimum;
    }

    /// <summary>
    /// When a recording has no cursor samples the compositor synthesizes a static
    /// centre position; drawing a cursor there would be pure fiction, so it is hidden
    /// (a negative auto-hide delay hides it from the very first frame).
    /// </summary>
    private static CompositionConfig ApplyCursorAvailability(
        CompositionConfig config, MouseRecordingData mouseData)
    {
        if (mouseData.Samples is { Count: > 0 })
            return config;

        return config with
        {
            Cursor = config.Cursor with
            {
                AutoHideEnabled = true,
                AutoHideDelaySeconds = -1f,
                AutoHideFadeDuration = 0f,
            },
        };
    }

    private static MouseRecordingData LoadMouseData(string? cursorDataFilePath)
    {
        if (!string.IsNullOrWhiteSpace(cursorDataFilePath) && File.Exists(cursorDataFilePath))
        {
            try
            {
                return MouseHookRecorder.LoadFromFile(cursorDataFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException)
            {
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Unreadable cursor data '{cursorDataFilePath}': {ex.Message}");
            }
        }

        return new MouseRecordingData
        {
            Samples = [],
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = 0,
            TickFrequency = TimeSpan.TicksPerSecond,
        };
    }

    private static string NormalizePath(string path) => path.ToLowerInvariant();

    #endregion

    #region Frame extraction helpers

    private static async Task<CanvasBitmap> ExtractFrameFromCompositionAsync(
        CanvasDevice device, MediaComposition composition, TimeSpan position, int width, int height)
    {
        var clampedPosition = position;
        if (composition.Duration > TimeSpan.Zero && position > composition.Duration)
            clampedPosition = composition.Duration;

        using var thumbnail = await composition.GetThumbnailAsync(
            clampedPosition, width, height, VideoFramePrecision.NearestFrame);

        // ImageStream already implements IRandomAccessStream — pass directly
        return await CanvasBitmap.LoadAsync(device, thumbnail);
    }

    private static async Task<CanvasBitmap> FallbackExtractFrameAsync(
        CanvasDevice device, string videoPath, TimeSpan position, int width, int height)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        try
        {
            return await ExtractFrameFromCompositionAsync(device, composition, position, width, height);
        }
        finally
        {
            // Called per frame — release the clip's file handle immediately.
            composition.Clips.Clear();
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release every context even if one of them throws while disposing, so a single
        // failure cannot orphan the remaining compositors and file handles.
        foreach (var context in _contexts.Values)
        {
            try
            {
                context.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[SegmentFrameComposer] Failed to release '{context.VideoFilePath}': {ex.Message}");
            }
        }
        _contexts.Clear();

        _textSlideRenderer?.Dispose();
        _textSlideRenderer = null;
        _transitionRenderer?.Dispose();
        _transitionRenderer = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Identity of a render context: the same source file rendered with different
    /// per-segment styles needs its own compositor, and segments sharing a file and
    /// style share one.
    /// </summary>
    private readonly record struct SourceKey(string Path, BackgroundStyle Background, CursorStyle Cursor);

    /// <summary>All render state owned for one (source file, style) combination.</summary>
    private sealed class SourceContext : IDisposable
    {
        public required string VideoFilePath { get; init; }
        public required int SourceWidth { get; init; }
        public required int SourceHeight { get; init; }

        /// <summary>Set before the context is published; never null once built.</summary>
        public FrameCompositor Compositor { get; set; } = null!;

        public VideoFrameReader? Reader { get; set; }

        /// <summary>Only used when no captured JPEG frames exist for this source.</summary>
        public MediaComposition? SourceComposition { get; set; }

        public MediaComposition? WebcamComposition { get; set; }
        public int WebcamExtractWidth { get; set; }
        public int WebcamExtractHeight { get; set; }

        /// <summary>
        /// Idempotent: releases the compositor's GPU resources, the frame reader, and the
        /// media compositions' clips (which is what frees the underlying file handles).
        /// The compositions are always cleared, even if disposing the compositor throws.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Compositor?.Dispose();
            }
            finally
            {
                // Null out so a second Dispose is a no-op (the property is non-nullable
                // only because it is always assigned before the context is published).
                Compositor = null!;

                Reader?.Dispose();
                Reader = null;

                // Clearing the clips releases the MediaClips' native resources and the
                // video file handles they hold.
                SourceComposition?.Clips.Clear();
                SourceComposition = null;
                WebcamComposition?.Clips.Clear();
                WebcamComposition = null;
            }
        }
    }
}
