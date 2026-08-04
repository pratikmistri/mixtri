using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Musio.Core.Export;

/// <summary>
/// Settings for video export.
/// </summary>
public record ExportSettings
{
    public VideoResolution Resolution { get; init; } = VideoResolution.HD1080;
    public int Fps { get; init; } = 30;
    public VideoFormat Format { get; init; } = VideoFormat.MP4;
    public VideoQuality Quality { get; init; } = VideoQuality.High;
    public AspectRatio AspectRatio { get; init; } = AspectRatio.Auto;
}

/// <summary>
/// Progress information for an ongoing export operation.
/// </summary>
public record ExportProgress(
    int CurrentFrame,
    int TotalFrames,
    double PercentComplete,
    TimeSpan Elapsed,
    TimeSpan EstimatedRemaining);

/// <summary>
/// Exports composited video frames directly to an MP4 file using
/// <see cref="MediaStreamSource"/> + <see cref="MediaTranscoder"/>,
/// mirroring the editor preview pipeline. No intermediate temp files.
/// Audio is muxed from the source recording in a fast second pass.
/// </summary>
public class VideoEncoder : IDisposable
{
    private readonly ExportSettings _settings;
    private bool _disposed;
    private volatile bool _deviceLost;
    private CanvasDevice? _deviceWithDeviceLostHandler;

    private const long MaxEstimatedRenderTargetBytes = 1_610_612_736L;
    private static readonly TimeSpan MainTranscodeTimeout = TimeSpan.FromHours(2);

    // Serializes frame compositing so the shared SegmentFrameComposer (and the
    // per-source compositors it owns) is never accessed concurrently by overlapping
    // SampleRequested callbacks from the MediaStreamSource pipeline.
    private readonly SemaphoreSlim _frameSemaphore = new(1, 1);

    public VideoEncoder(ExportSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Exports a recording by compositing each frame in real-time (like the editor
    /// preview) and streaming directly to the H.264 encoder via
    /// <see cref="MediaStreamSource"/>. No temp files are written.
    /// Audio is muxed from source recordings in a second pass.
    /// </summary>
    public async Task ExportAsync(
        Project project,
        MouseRecordingData mouseData,
        CompositionConfig compositionConfig,
        int targetWidth,
        int targetHeight,
        string outputPath,
        TimelineMapper? timelineMapper = null,
        TimelineModel? timeline = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.VideoFilePath);
        ArgumentNullException.ThrowIfNull(mouseData);
        ArgumentNullException.ThrowIfNull(compositionConfig);
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stopwatch = Stopwatch.StartNew();
        var device = CanvasDevice.GetSharedDevice();
        _deviceLost = false;
        device.DeviceLost += OnCanvasDeviceLost;
        _deviceWithDeviceLostHandler = device;

        try
        {
        ThrowIfDeviceLost();

        // Segment-aware frame composition, shared with the GIF exporter. It owns the
        // per-source render contexts (frame reader + compositor + webcam) so appended
        // recordings render from their own files, with their own cursor data, zoom
        // keyframes, and per-segment frame/cursor style overrides.
        using var composer = await SegmentFrameComposer.CreateAsync(
            project, mouseData, compositionConfig, timeline, timelineMapper, _settings.Fps, ct);

        // Total output frames based on the EXPORT fps, not the compositor's
        // internal fps (which is capped at 30 for cursor/click timing).
        int totalFrames = timelineMapper?.TotalOutputFrames
            ?? (int)(project.Duration.TotalSeconds * _settings.Fps);
        int compositorWidth = composer.OutputWidth;
        int compositorHeight = composer.OutputHeight;

        // Encode at the user-selected resolution while preserving the
        // compositor's aspect ratio (the compositor has already applied
        // AspectRatio + padding). Never upscale beyond the compositor's
        // native size. Floored to mod-16 for H.264 macroblock alignment.
        var (encW, encH) = AspectRatioHelper.ComputeExportDimensions(
            compositorWidth, compositorHeight, _settings.Resolution);
        targetWidth = encW;
        targetHeight = encH;
        bool needsScaling = targetWidth != compositorWidth || targetHeight != compositorHeight;
        PreflightRenderTargetMemory(targetWidth, targetHeight, needsScaling);

        // Resolve which audio sources the edited timeline actually needs, and where each
        // belongs on the output. Placements whose media has no audio are dropped here so
        // a silent recording never pays for the mux pass.
        var audioPlacements = await ResolveAvailableAudioAsync(
            ExportAudioPlan.Build(project, timeline, timelineMapper));
        bool hasAudio = audioPlacements.Count > 0;
        WarnAboutSpeedAdjustedAudio(audioPlacements);
        WarnAboutTransitionFadeAudio(audioPlacements);

        // Video-only output path (audio muxed in second pass if needed)
        string videoOnlyPath = hasAudio
            ? Path.Combine(Path.GetDirectoryName(outputPath)!, $".musio_video_{Guid.NewGuid():N}.mp4")
            : outputPath;

        // Tracks in-flight frame tasks so the finally block can guarantee none is still
        // using the composer when it is disposed (including on the failure path).
        var pendingSamples = new List<Task>();
        var pendingSamplesLock = new object();

        try
        {
            // ── Pass 1: Direct composited-frame encoding (no temp files) ──
            int currentFrame = 0;
            // Captures the first exception thrown inside ProduceSampleAsync so the
            // export can fail loudly with a real error instead of silently producing
            // a truncated video (the MediaStreamSource treats Sample=null as EOS,
            // which previously caused failed exports to appear as ~1-second videos).
            Exception? firstFrameError = null;
            int firstFrameErrorIndex = -1;
            var frameErrorLock = new object();
            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            // Create uncompressed video stream for the MediaStreamSource
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)targetWidth, (uint)targetHeight);
            videoProps.FrameRate.Numerator = (uint)_settings.Fps;
            videoProps.FrameRate.Denominator = 1;

            Debug.WriteLine($"[VideoEncoder] Dimensions: {targetWidth}x{targetHeight}");

            var videoDesc = new VideoStreamDescriptor(videoProps);

            var streamSource = new MediaStreamSource(videoDesc);
            streamSource.Duration = TimeSpan.FromSeconds((double)totalFrames / _settings.Fps);
            streamSource.BufferTime = TimeSpan.Zero;

            streamSource.Starting += (MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
            {
                args.Request.SetActualStartPosition(TimeSpan.Zero);
            };

            streamSource.SampleRequested += (MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args) =>
            {
                // Atomically reserve a frame index to avoid duplicate/skipped frames
                int frame = Interlocked.Increment(ref currentFrame) - 1;
                if (frame >= totalFrames)
                {
                    args.Request.Sample = null; // end of stream
                    return;
                }

                var deferral = args.Request.GetDeferral();
                var task = ProduceSampleAsync(
                    args.Request, deferral, frame, totalFrames,
                    composer, device,
                    compositorWidth, compositorHeight,
                    targetWidth, targetHeight,
                    needsScaling,
                    progress, stopwatch, ct,
                    onError: (ex, frameIdx) =>
                    {
                        lock (frameErrorLock)
                        {
                            if (firstFrameError is null)
                            {
                                firstFrameError = ex;
                                firstFrameErrorIndex = frameIdx;
                            }
                        }
                    });

                lock (pendingSamplesLock)
                {
                    // Remove completed tasks to prevent unbounded list growth
                    pendingSamples.RemoveAll(t => t.IsCompleted);
                    pendingSamples.Add(task);
                }
            };

            // Transcode: composited BGRA8 frames → H.264 MP4
            // Use software encoding to avoid hardware encoder quirks with
            // non-standard dimensions and D3D surface interop.
            var transcoder = new MediaTranscoder();
            transcoder.HardwareAccelerationEnabled = false;
            var profile = CreateEncodingProfile(targetWidth, targetHeight);

            // Remove audio from first-pass profile
            profile.Audio = null;

            var outputFile = await CreateOutputFileAsync(videoOnlyPath);
            using var outputStream = await outputFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);

            var prepResult = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                streamSource, outputStream, profile);

            if (!prepResult.CanTranscode)
                throw new InvalidOperationException(
                    $"Transcoder cannot encode: {prepResult.FailureReason}");

            await TranscodeWithTimeoutAsync(prepResult, ct);

            // Drain any still-running sample tasks before disposing shared state
            Task[] snapshot;
            lock (pendingSamplesLock)
            {
                snapshot = pendingSamples.ToArray();
            }
            await Task.WhenAll(snapshot).ConfigureAwait(false);

            // If any frame failed during compositing, fail the export loudly.
            // Without this, MediaStreamSource silently treats Sample=null as EOS,
            // producing a truncated (often ~1 second) video on the first error.
            // Read under the same lock used by the producer tasks so the read is
            // explicitly synchronized (Task.WhenAll already establishes happens-
            // before, but reading under the lock makes the intent obvious).
            Exception? capturedError;
            int capturedIndex;
            lock (frameErrorLock)
            {
                capturedError = firstFrameError;
                capturedIndex = firstFrameErrorIndex;
            }
            if (capturedError is not null)
            {
                throw new InvalidOperationException(
                    $"Export failed while compositing frame {capturedIndex}: " +
                    $"{capturedError.Message}",
                    capturedError);
            }

            // ── Pass 2: Mux audio (fast — no frame re-compositing) ──
            if (hasAudio)
            {
                progress?.Report(new ExportProgress(
                    totalFrames, totalFrames, 99, stopwatch.Elapsed, TimeSpan.FromSeconds(2)));

                await MuxAudioAsync(videoOnlyPath, outputPath, audioPlacements, ct);
            }
        }
        finally
        {
            // Never dispose the composer (owned by the enclosing `using`) while a sample
            // task might still be compositing with it — including on the failure path,
            // where the transcode threw before the drain above.
            Task[] outstanding;
            lock (pendingSamplesLock)
            {
                outstanding = pendingSamples.ToArray();
            }
            if (outstanding.Length > 0)
            {
                try
                {
                    await Task.WhenAll(outstanding).ConfigureAwait(false);
                }
                catch
                {
                    // Frame failures are reported through firstFrameError; here we only
                    // need the tasks to have stopped touching shared state.
                }
            }

            // Clean up temp video-only file
            if (hasAudio)
            {
                try { File.Delete(videoOnlyPath); }
                catch { /* best-effort */ }
            }
        }
        }
        finally
        {
            device.DeviceLost -= OnCanvasDeviceLost;
            if (ReferenceEquals(_deviceWithDeviceLostHandler, device))
                _deviceWithDeviceLostHandler = null;
        }
    }

    /// <summary>
    /// Produces a single composited video sample for the <see cref="MediaStreamSource"/>.
    /// Frame composition itself lives in <see cref="SegmentFrameComposer"/> (shared with
    /// the GIF exporter); this method only scales and hands the surface to the encoder.
    /// </summary>
    private async Task ProduceSampleAsync(
        MediaStreamSourceSampleRequest request,
        MediaStreamSourceSampleRequestDeferral deferral,
        int frameIndex, int totalFrames,
        SegmentFrameComposer composer,
        CanvasDevice device,
        int compositorWidth, int compositorHeight,
        int targetWidth, int targetHeight,
        bool needsScaling,
        IProgress<ExportProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken ct,
        Action<Exception, int>? onError = null)
    {
        CanvasRenderTarget? outputSurface = null;
        CanvasRenderTarget? composedFrame = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDeviceLost();

            // Serialize frame production: the composer's per-source compositors,
            // frame readers, and webcam compositions are not thread-safe and can
            // corrupt frames if accessed concurrently by overlapping SampleRequested
            // callbacks.
            bool semaphoreAcquired = false;
            try
            {
            await _frameSemaphore.WaitAsync(ct).ConfigureAwait(false);
            semaphoreAcquired = true;

            var frameDuration = TimeSpan.FromSeconds(1.0 / _settings.Fps);

            composedFrame = await composer.ComposeFrameAsync(frameIndex, ct);

            // Build the output surface. For scaling we need a separate render
            // target; otherwise the composed frame IS the output surface.
            // Each frame gets its own surface so the encoder can read it async
            // after we release the semaphore.
            if (needsScaling)
            {
                outputSurface = CreateRenderTarget(device, targetWidth, targetHeight, "scaled encoder output");
                using (var ds = outputSurface.CreateDrawingSession())
                {
                    ds.DrawImage(composedFrame,
                        new Windows.Foundation.Rect(0, 0, targetWidth, targetHeight),
                        new Windows.Foundation.Rect(0, 0, compositorWidth, compositorHeight));
                }
                composedFrame.Dispose();
                composedFrame = null;
            }
            else
            {
                outputSurface = composedFrame;
                composedFrame = null;
            }

            // Give the D3D11 surface directly to the encoder — bypasses all
            // pixel extraction and stride alignment issues entirely.
            var timestamp = TimeSpan.FromSeconds((double)frameIndex / _settings.Fps);
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(outputSurface, timestamp);
            sample.Duration = frameDuration;

            // Dispose the GPU surface after the encoder has consumed it.
            // Capture in a local to avoid double-dispose if the catch block also disposes.
            var surfaceToDispose = outputSurface;
            outputSurface = null; // prevent catch block from disposing
            sample.Processed += (s, e) => surfaceToDispose.Dispose();

            request.Sample = sample;

            }
            finally
            {
                if (semaphoreAcquired)
                    _frameSemaphore.Release();
            }

            // Report progress
            if (progress is not null)
            {
                double percent = (double)(frameIndex + 1) / totalFrames * 100.0;
                var elapsed = stopwatch.Elapsed;
                var perFrame = elapsed / (frameIndex + 1);
                var remaining = perFrame * (totalFrames - frameIndex - 1);
                progress.Report(new ExportProgress(frameIndex + 1, totalFrames, percent, elapsed, remaining));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoEncoder] Frame {frameIndex} error: {ex}");
            // Notify caller so the export can fail loudly rather than truncating.
            onError?.Invoke(ex, frameIndex);
            // Dispose GPU surface if it was created but never handed to the encoder
            outputSurface?.Dispose();
            // Dispose the composed frame too if an exception hit before it was scaled
            // into outputSurface or handed off (it is nulled at those handoff points).
            composedFrame?.Dispose();
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Applies a planned placement to a background audio track: where playback starts
    /// inside the source, how much of it plays, and where it lands on the output.
    /// </summary>
    private static void ApplyPlacement(BackgroundAudioTrack track, AudioPlacement placement)
    {
        var originalDuration = track.OriginalDuration;

        var trimStart = placement.TrimFromStart;
        if (trimStart < TimeSpan.Zero) trimStart = TimeSpan.Zero;
        if (trimStart > originalDuration) trimStart = originalDuration;
        track.TrimTimeFromStart = trimStart;

        if (ExportTakeDuration(placement) is { } take && take > TimeSpan.Zero)
        {
            var sourceEnd = trimStart + take;
            if (sourceEnd < originalDuration)
                track.TrimTimeFromEnd = originalDuration - sourceEnd;
        }

        // Place the audio at its segment's position on the output timeline so text
        // slides (and deleted ranges) become silent gaps instead of shifting audio.
        track.Delay = placement.Delay;
    }

    /// <summary>
    /// The take duration actually muxed into the export — deliberately NOT always the same
    /// as <see cref="AudioPlacement.TakeDuration"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the two can differ.</b> <see cref="ExportAudioPlan"/> extends an
    /// outgoing placement's <see cref="AudioPlacement.TakeDuration"/> by a transition's
    /// duration so it COULD keep playing underneath the incoming segment's own audio
    /// during a dissolve (see <see cref="AudioPlacement.FadeOutDuration"/>). That is only
    /// safe to mux as-is if something also ramps this track's gain down (and the incoming
    /// track's gain up) across the overlap — otherwise the two tracks simply sum at full
    /// volume, an audible glitch strictly worse than the pre-existing hard cut.</para>
    /// <para><b>Investigated: a custom <c>IBasicAudioEffect</c> gain ramp.</b>
    /// <see cref="BackgroundAudioTrack.AudioEffectDefinitions"/> does exist and does accept
    /// a custom effect by activatable-class name, so this was seriously considered.
    /// It doesn't work in THIS pipeline because: (1) implementing
    /// <c>Windows.Media.Effects.IBasicAudioEffect</c> so the WinRT activation system (the
    /// same <c>RoGetActivationFactory</c> path <see cref="MediaComposition"/> itself uses)
    /// can construct it by name requires the class live in a genuine Windows Runtime
    /// Component project (producing a <c>.winmd</c>) — <c>Musio.Core</c> is a plain
    /// <c>Microsoft.NET.Sdk</c> class library and cannot host a WinRT-activatable class
    /// no matter how the interface is implemented in C#; and (2) even with such a project,
    /// the class must be registered as an in-process server via a
    /// <c>windows.activatableClass.inProcessServer</c> extension in
    /// <c>Musio.App\Package.appxmanifest</c>. Both are structural, cross-project changes —
    /// far outside this task's file scope, and (2) is a single shared file every other
    /// concurrent task in this feature could also need to touch. No in-scope workaround
    /// was found, so — per this feature's explicitly pre-approved fallback — export keeps
    /// the pre-existing hard cut. Live preview (<see cref="Musio.Core.Audio.AudioPlaybackEngine"/>)
    /// has a working ramp implementation too, but as of today nothing wires real fade
    /// windows into it either (see <see cref="ExportAudioPlan"/>'s class remarks) — so
    /// NEITHER pipeline actually crossfades yet; this is the export half of that honest
    /// status, not a claim preview already differs.</para>
    /// <para><b>How the hard cut is reproduced exactly.</b> Subtracting
    /// <see cref="AudioPlacement.FadeOutDuration"/> back out of
    /// <see cref="AudioPlacement.TakeDuration"/> exactly reconstructs the take
    /// <see cref="ExportAudioPlan"/> would have produced with no active transition,
    /// because the extension and the (duration-capped) fade are computed from the exact
    /// same clamped transition duration — see <c>ExportAudioPlan.BuildPlacement</c>'s
    /// remarks. This is skipped for
    /// <see cref="AudioPlacement.PlaysAtNativeRateOnSpeedAdjustedSegment"/> placements:
    /// their <see cref="AudioPlacement.TakeDuration"/> was never extended in the first
    /// place (a speed-adjusted segment keeps its pre-existing native-rate cap), so
    /// subtracting here would wrongly shorten an already-correct take.
    /// <see cref="AudioPlacement.FadeInDuration"/> needs no equivalent correction: a
    /// fade-in never changes <see cref="AudioPlacement.TrimFromStart"/> or
    /// <see cref="AudioPlacement.Delay"/>, so ignoring it here already reproduces the hard
    /// cut.</para>
    /// <para><b>Marked <c>internal</c>, not <c>private</c>, deliberately.</b> This is the
    /// single highest-consequence line in the whole feature: if it were ever bypassed (e.g.
    /// a future refactor calls <see cref="ApplyPlacement"/> with the raw, un-corrected
    /// <see cref="AudioPlacement.TakeDuration"/>), every export touching a transition
    /// boundary would mux two full-volume, un-ramped, OVERLAPPING tracks — audibly worse
    /// than the pre-existing hard cut it is meant to reproduce. It is exposed so
    /// <c>Musio.Tests</c> (via this assembly's <c>InternalsVisibleTo</c>) can assert the
    /// round trip directly against <see cref="ExportAudioPlan.Build"/> output, instead of
    /// only indirectly through a full WinRT mux.</para>
    /// </remarks>
    internal static TimeSpan? ExportTakeDuration(AudioPlacement placement)
    {
        if (placement.TakeDuration is not { } take) return null;
        if (placement.FadeOutDuration <= TimeSpan.Zero) return take;
        if (placement.PlaysAtNativeRateOnSpeedAdjustedSegment) return take;

        var hardCutTake = take - placement.FadeOutDuration;
        return hardCutTake > TimeSpan.Zero ? hardCutTake : TimeSpan.Zero;
    }

    /// <summary>
    /// Clamps a placement's trim/take/fade metadata against the SOURCE's real total
    /// duration, now that <see cref="ResolveAvailableAudioAsync"/> has actually opened the
    /// media to confirm it exists (or, for an embedded track, that it has audio).
    /// </summary>
    /// <remarks>
    /// <see cref="ExportAudioPlan"/> is deliberately pure (no I/O — see its class remarks)
    /// and therefore has no way to know a source's true remaining length past a segment's
    /// own recorded metadata (<see cref="VideoSegment.SourceDuration"/> etc.). A transition's
    /// extension (see <see cref="AudioPlacement.FadeOutDuration"/>) can therefore, in
    /// principle, claim more tail than the file actually has — e.g. a segment trimmed to
    /// end exactly at a 10s file's own EOF, with a 1s transition, plans an 11s take. Today
    /// that particular case is harmless in practice because <see cref="ExportTakeDuration"/>
    /// always subtracts the fade back out before muxing rather than trusting the extended
    /// take directly. This clamp exists so that safety holds STRUCTURALLY, not by
    /// accident: it runs before <em>either</em> <see cref="ExportTakeDuration"/>'s
    /// subtraction or any hypothetical future consumer (e.g. a real gain-envelope effect)
    /// ever sees this placement's fields, so none of them can be told to play/ramp past
    /// what the file actually contains, without needing to independently remember to
    /// re-derive that bound.
    /// <para>
    /// When <paramref name="sourceDuration"/> is <c>null</c> (the duration probe failed —
    /// see <see cref="TryGetMediaDurationAsync"/>), this returns <paramref name="placement"/>
    /// unchanged: without a known real length there is nothing safe to clamp against, so
    /// the placement keeps whatever bound <see cref="ExportAudioPlan"/> itself computed.
    /// </para>
    /// </remarks>
    internal static AudioPlacement ClampToSourceDuration(AudioPlacement placement, TimeSpan? sourceDuration)
    {
        if (sourceDuration is not { } duration || duration <= TimeSpan.Zero)
            return placement;

        var trimStart = placement.TrimFromStart;
        if (trimStart < TimeSpan.Zero) trimStart = TimeSpan.Zero;
        if (trimStart > duration) trimStart = duration;

        var realAvailable = duration - trimStart;

        // A null take already means "play to the end of the file", which the file itself
        // bounds — materialising an explicit take here would only risk re-deriving that
        // bound wrongly, and would hand ExportTakeDuration a value to subtract a fade from
        // that it would otherwise have left alone.
        TimeSpan? take = placement.TakeDuration;
        var clampedAway = TimeSpan.Zero;
        if (take is { } planned && planned > realAvailable)
        {
            clampedAway = planned - realAvailable;
            take = realAvailable;
        }

        // The transition extension is the ONLY reason a planned take can overrun the file:
        // ExportAudioPlan appends exactly FadeOutDuration of extra tail. So whatever the
        // real file could not satisfy has to come off the fade as well. Without this,
        // ExportTakeDuration subtracts a fade that is no longer present in the take and the
        // muxed audio ends BEFORE the original hard cut — e.g. a 10s segment ending at a 10s
        // file's EOF plans an 11s take with a 1s fade, clamps to 10s, and would then mux 9s,
        // silently truncating a second of audio off the end of every such export.
        var fadeOut = placement.FadeOutDuration > clampedAway
            ? placement.FadeOutDuration - clampedAway
            : TimeSpan.Zero;

        var bound = take ?? realAvailable;
        if (fadeOut > bound) fadeOut = bound;
        var fadeIn = placement.FadeInDuration > bound ? bound : placement.FadeInDuration;

        if (trimStart == placement.TrimFromStart
            && take == placement.TakeDuration
            && fadeOut == placement.FadeOutDuration
            && fadeIn == placement.FadeInDuration)
        {
            return placement;
        }

        return placement with
        {
            TrimFromStart = trimStart,
            TakeDuration = take,
            FadeOutDuration = fadeOut,
            FadeInDuration = fadeIn,
        };
    }

    /// <summary>
    /// Reports the transition-crossfade limitation this pipeline cannot fix: exported
    /// audio at a transition boundary is muxed as the ORIGINAL hard cut (see
    /// <see cref="ExportTakeDuration"/>'s remarks for exactly why). Live preview does not
    /// crossfade either as of today (see <see cref="ExportAudioPlan"/>'s class remarks for
    /// why — the ramp exists in <see cref="Musio.Core.Audio.AudioPlaybackEngine"/> but is
    /// not yet wired up), so this is currently a hard cut everywhere, not a divergence from
    /// a genuinely crossfaded preview. Logged rather than swallowed for the same reason
    /// <see cref="WarnAboutSpeedAdjustedAudio"/> logs its sibling limitation: so this is
    /// never mistaken for a working crossfade once preview alone eventually gains one.
    /// </summary>
    private static void WarnAboutTransitionFadeAudio(IReadOnlyList<AudioPlacement> placements)
    {
        int affected = placements.Count(
            p => p.FadeOutDuration > TimeSpan.Zero || p.FadeInDuration > TimeSpan.Zero);
        if (affected == 0) return;

        Debug.WriteLine(
            $"[VideoEncoder] {affected} audio track(s) touch a transition boundary. " +
            "MediaComposition/BackgroundAudioTrack expose no gain-envelope API (a custom " +
            "IBasicAudioEffect would need its own Windows Runtime Component project plus a " +
            "Package.appxmanifest activation extension — out of scope here), so exported " +
            "audio is hard-cut at these boundaries exactly as it was before this feature; " +
            "live preview does not crossfade yet either (the ramp exists in " +
            "AudioPlaybackEngine but nothing currently feeds it real fade windows).");
    }

    /// <summary>
    /// Reports the one A/V-sync limitation this pipeline cannot fix: the Windows media
    /// editing APIs used for muxing (<see cref="MediaComposition"/> /
    /// <see cref="BackgroundAudioTrack"/>) expose no playback-rate or time-scale property,
    /// so audio under a speed-adjusted segment plays at its native rate — in sync at the
    /// segment start, drifting as it plays, and cut at the segment boundary so it cannot
    /// overlap the next segment. Logged rather than swallowed so the behaviour is never
    /// mistaken for full synchronization.
    /// </summary>
    private static void WarnAboutSpeedAdjustedAudio(IReadOnlyList<AudioPlacement> placements)
    {
        int affected = placements.Count(p => p.PlaysAtNativeRateOnSpeedAdjustedSegment);
        if (affected == 0) return;

        Debug.WriteLine(
            $"[VideoEncoder] {affected} audio track(s) belong to speed-adjusted segments. " +
            "Audio cannot be time-scaled by MediaComposition/BackgroundAudioTrack (no " +
            "playback-rate API), so those tracks are muxed at their native rate and are " +
            "trimmed at the segment boundary: they start in sync but drift within the segment.");
    }

    /// <summary>
    /// Drops planned placements whose media does not exist or carries no audio, so a
    /// silent recording never triggers the (expensive) mux pass. Also probes each
    /// surviving placement's real source duration and clamps its trim/take/fade metadata
    /// against it via <see cref="ClampToSourceDuration"/> — see that method's remarks for
    /// why that clamp belongs here rather than in the (deliberately pure)
    /// <see cref="ExportAudioPlan"/>.
    /// </summary>
    private static async Task<List<AudioPlacement>> ResolveAvailableAudioAsync(
        IReadOnlyList<AudioPlacement> placements)
    {
        var available = new List<AudioPlacement>();
        var embeddedAudioByPath = new Dictionary<string, (bool HasAudio, TimeSpan? Duration)>(
            StringComparer.OrdinalIgnoreCase);
        var audioFileDurationByPath = new Dictionary<string, TimeSpan?>(StringComparer.OrdinalIgnoreCase);

        foreach (var placement in placements)
        {
            if (placement.Kind == AudioSourceKind.AudioFile)
            {
                if (!File.Exists(placement.SourcePath))
                    continue;

                if (!audioFileDurationByPath.TryGetValue(placement.SourcePath, out var duration))
                {
                    duration = await TryGetMediaDurationAsync(placement.SourcePath);
                    audioFileDurationByPath[placement.SourcePath] = duration;
                }

                available.Add(ClampToSourceDuration(placement, duration));
                continue;
            }

            if (!embeddedAudioByPath.TryGetValue(placement.SourcePath, out var probe))
            {
                probe = await ProbeEmbeddedAudioAsync(placement.SourcePath);
                embeddedAudioByPath[placement.SourcePath] = probe;
            }

            if (probe.HasAudio)
                available.Add(ClampToSourceDuration(placement, probe.Duration));
        }

        return available;
    }

    /// <summary>
    /// Opens <paramref name="videoPath"/> once to report both whether it has any embedded
    /// audio track and its real <see cref="MediaClip.OriginalDuration"/> (used as the
    /// embedded track's own duration — a screen recording's audio and video tracks start
    /// and end together, so the whole clip's duration is a reasonable, cheap proxy for the
    /// embedded audio track's own length without opening it as a separate
    /// <see cref="BackgroundAudioTrack"/> just to ask).
    /// </summary>
    private static async Task<(bool HasAudio, TimeSpan? Duration)> ProbeEmbeddedAudioAsync(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return (false, null);

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
            var clip = await MediaClip.CreateFromFileAsync(file);
            return (clip.EmbeddedAudioTracks.Count > 0, clip.OriginalDuration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[VideoEncoder] Could not inspect audio tracks of '{videoPath}': {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// Resolves a standalone audio file's real total duration for
    /// <see cref="ClampToSourceDuration"/>. Returns <c>null</c> (rather than throwing) on
    /// any failure, in which case the caller leaves the placement's planner-computed
    /// metadata unclamped — see <see cref="ClampToSourceDuration"/>'s remarks for why that
    /// is an acceptable, explicitly-handled degradation rather than a silent bug.
    /// </summary>
    private static async Task<TimeSpan?> TryGetMediaDurationAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var clip = await MediaClip.CreateFromFileAsync(file);
            return clip.OriginalDuration;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[VideoEncoder] Could not resolve the duration of '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Muxes the planned audio placements into the video-only MP4.
    /// Uses <see cref="MediaComposition"/> for reliable audio handling.
    /// </summary>
    private async Task MuxAudioAsync(
        string videoOnlyPath, string finalOutputPath,
        IReadOnlyList<AudioPlacement> placements, CancellationToken ct)
    {
        var muxComp = new MediaComposition();

        // Add the video-only file as the video track
        var videoFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoOnlyPath));
        var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
        muxComp.Clips.Add(videoClip);

        // One clip per source file, reused by every placement cut from it.
        var sourceClips = new Dictionary<string, MediaClip>(StringComparer.OrdinalIgnoreCase);

        foreach (var placement in placements)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                BackgroundAudioTrack track;
                if (placement.Kind == AudioSourceKind.EmbeddedVideoTrack)
                {
                    if (!sourceClips.TryGetValue(placement.SourcePath, out var clip))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(
                            Path.GetFullPath(placement.SourcePath));
                        clip = await MediaClip.CreateFromFileAsync(file);
                        sourceClips[placement.SourcePath] = clip;
                    }

                    if (clip.EmbeddedAudioTracks.Count == 0)
                        continue;

                    track = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(clip.EmbeddedAudioTracks[0]);
                }
                else
                {
                    var audioFile = await StorageFile.GetFileFromPathAsync(
                        Path.GetFullPath(placement.SourcePath));
                    track = await BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                }

                ApplyPlacement(track, placement);
                muxComp.BackgroundAudioTracks.Add(track);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single unreadable track must not discard an otherwise complete
                // export; the remaining tracks still mux at their planned positions.
                Debug.WriteLine(
                    $"[VideoEncoder] Failed to add audio track '{placement.SourcePath}': {ex.Message}");
            }
        }

        // Render final output with audio
        var profile = CreateEncodingProfile(
            (int)videoClip.GetVideoEncodingProperties().Width,
            (int)videoClip.GetVideoEncodingProperties().Height);
        var outputFile = await CreateOutputFileAsync(finalOutputPath);

        var renderOp = muxComp.RenderToFileAsync(outputFile, MediaTrimmingPreference.Fast, profile);
        var tcs = new TaskCompletionSource<object?>();
        using var reg = ct.Register(() => renderOp.Cancel());
        renderOp.Completed = (info, status) =>
        {
            if (status == Windows.Foundation.AsyncStatus.Completed)
                tcs.TrySetResult(null);
            else if (status == Windows.Foundation.AsyncStatus.Canceled)
                tcs.TrySetCanceled();
            else
                tcs.TrySetException(info.ErrorCode ?? new InvalidOperationException("Audio mux failed."));
        };
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
        if (await Task.WhenAny(tcs.Task, timeoutTask) != tcs.Task)
        {
            ct.ThrowIfCancellationRequested();
            renderOp.Cancel();
            throw new TimeoutException("Audio mux operation timed out after 5 minutes.");
        }

        try
        {
            await tcs.Task; // propagate any exception
        }
        finally
        {
            // Release MediaComposition native resources (and the source file handles)
            // whether or not the render succeeded.
            muxComp.Clips.Clear();
            muxComp.BackgroundAudioTracks.Clear();
            sourceClips.Clear();
        }
    }

    private async Task TranscodeWithTimeoutAsync(PrepareTranscodeResult prepResult, CancellationToken ct)
    {
        var transcodeOp = prepResult.TranscodeAsync();
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => transcodeOp.Cancel());
        transcodeOp.Completed = (info, status) =>
        {
            if (status == Windows.Foundation.AsyncStatus.Completed)
                tcs.TrySetResult(null);
            else if (status == Windows.Foundation.AsyncStatus.Canceled)
                tcs.TrySetCanceled(ct);
            else
                tcs.TrySetException(info.ErrorCode ?? new InvalidOperationException("Video transcode failed."));
        };

        var timeoutTask = Task.Delay(MainTranscodeTimeout);
        if (await Task.WhenAny(tcs.Task, timeoutTask) != tcs.Task)
        {
            ct.ThrowIfCancellationRequested();
            transcodeOp.Cancel();
            throw new TimeoutException(
                $"Video transcode operation timed out after {MainTranscodeTimeout.TotalHours:0.#} hours.");
        }

        await tcs.Task;
        ThrowIfDeviceLost();
    }

    private void PreflightRenderTargetMemory(int targetWidth, int targetHeight, bool needsScaling)
    {
        long estimatedBytes = needsScaling ? EstimateBgraBytes(targetWidth, targetHeight, 1) : 0;
        if (estimatedBytes > MaxEstimatedRenderTargetBytes)
            throw new InvalidOperationException(FormatRenderTargetMemoryLimitMessage(estimatedBytes));
    }

    private CanvasRenderTarget CreateRenderTarget(CanvasDevice device, int width, int height, string purpose)
    {
        ThrowIfDeviceLost();
        try
        {
            return new CanvasRenderTarget(device, width, height, 96);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or COMException)
        {
            throw new InvalidOperationException(
                $"Failed to allocate {purpose} render target ({width}x{height}). " +
                "Reduce export resolution or close other GPU-heavy applications.", ex);
        }
    }

    private void OnCanvasDeviceLost(CanvasDevice sender, object args)
    {
        _deviceLost = true;
    }

    private void ThrowIfDeviceLost()
    {
        if (_deviceLost)
            throw new RecoverableDeviceLostException(
                "The graphics device was lost while exporting video. Retry the export after closing other GPU-heavy applications.");
    }

    private static long EstimateBgraBytes(int width, int height, int surfaceCount)
    {
        return (long)width * height * 4 * surfaceCount;
    }

    private static string FormatRenderTargetMemoryLimitMessage(long estimatedBytes)
    {
        long mb = estimatedBytes / (1024 * 1024);
        long maxMb = MaxEstimatedRenderTargetBytes / (1024 * 1024);
        return $"Estimated render target memory ({mb} MB) exceeds safe limit ({maxMb} MB). Reduce export resolution or close other GPU-heavy applications.";
    }

    private MediaEncodingProfile CreateEncodingProfile(int width, int height)
    {
        // Use HD1080p as a well-formed template with valid Video + Audio
        // properties. Override all resolution-dependent fields explicitly.
        // The encoder auto-selects the correct H.264 Level for the actual
        // dimensions (Level 5.1+ for 2.8K/4K). The scaled bitrate ensures
        // high-res exports have adequate quality.
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video!.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)_settings.Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = ComputeBitrate(width, height);
        profile.Video.Subtype = "H264";

        return profile;
    }

    /// <summary>
    /// Scales bitrate proportionally to pixel count relative to 1080p so
    /// high-resolution exports (2.8K, 4K) receive adequate bitrate.
    /// </summary>
    private uint ComputeBitrate(int width, int height)
    {
        uint baseBitrate = _settings.Quality switch
        {
            VideoQuality.Draft => 5_000_000,
            VideoQuality.Standard => 10_000_000,
            VideoQuality.High => 20_000_000,
            VideoQuality.Ultra => 50_000_000,
            _ => 20_000_000,
        };

        const double baselinePixels = 1920.0 * 1080.0;
        double actualPixels = (double)width * height;
        double scale = Math.Max(1.0, actualPixels / baselinePixels);

        return (uint)(baseBitrate * scale);
    }

    private static async Task<StorageFile> CreateOutputFileAsync(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string fileName = Path.GetFileName(outputPath);
        Directory.CreateDirectory(dir);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_deviceWithDeviceLostHandler is not null)
            {
                _deviceWithDeviceLostHandler.DeviceLost -= OnCanvasDeviceLost;
                _deviceWithDeviceLostHandler = null;
            }

            _frameSemaphore.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class RecoverableDeviceLostException : Exception
{
    public RecoverableDeviceLostException(string message)
        : base(message)
    {
    }
}
