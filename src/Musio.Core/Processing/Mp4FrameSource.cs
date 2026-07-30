using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Musio.Core.Processing;

/// <summary>
/// Decodes frames on demand from a finalized MP4 using <see cref="MediaPlayer"/>'s
/// frame-server mode.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a project editable after its <c>.frames/</c> scratch directory is
/// gone. The MP4 carries the same 8-bit 4:2:0 content the JPEGs did, at a fraction of the
/// size, so nothing of substance is lost by preferring it.
/// </para>
/// <para>
/// Frame-server mode is used rather than <c>Windows.Media.Editing</c> deliberately —
/// <c>MediaClip</c>/<c>MediaComposition</c> reject fragmented MP4s and spawn a COM object
/// per frame on long recordings. See the H.264 playbook in <c>learnings.md</c>.
/// </para>
/// <para>
/// Access is serialized: the decoder has exactly one position, so concurrent requests
/// would race each other's seeks. Sequential requests (the overwhelmingly common pattern
/// for both preview playback and export) take a cheap <c>StepForwardOneFrame</c> path
/// instead of a full seek.
/// </para>
/// </remarks>
public sealed class Mp4FrameSource : IFrameSource
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for one seek attempt before re-issuing it. Sequential decodes
    /// land in ~20 ms and a cold seek across a GOP in a few hundred, so a stall past this
    /// means the seek produced no frame at all rather than a slow one.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>Number of times a frame request is re-issued before giving up.</summary>
    private const int SeekAttempts = 3;

    /// <summary>
    /// How long to wait for a single frame step. Steps are the cheap path (a few
    /// milliseconds); anything past this is a stall, not slow decoding.
    /// </summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Largest forward gap walked with steps rather than a seek. Sized for the editor
    /// dropping a few frames under load; beyond this a seek genuinely is cheaper.
    /// </summary>
    private const int MaxStepAhead = 8;

    /// <summary>
    /// How long a frame is given to arrive after the decoder reports the seek finished.
    /// </summary>
    private static readonly TimeSpan PostSeekGrace = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long to let an abandoned frame arrive before issuing the next request after a
    /// timeout, so it cannot be mistaken for the answer to that request.
    /// </summary>
    private static readonly TimeSpan DrainDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long disposal waits for an in-flight request to finish.</summary>
    private static readonly TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(5);

    private readonly MediaPlayer _player;
    private readonly CanvasDevice _device;
    private readonly CanvasRenderTarget _surface;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _fps;
    private readonly bool _flipVertical;

    private TaskCompletionSource<bool>? _framePending;
    private int _lastDeliveredIndex = -1;
    private bool _needsDrain;
    private bool _disposed;

    public int FrameCount { get; }
    public int Width { get; }
    public int Height { get; }
    public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;

    private Mp4FrameSource(
        MediaPlayer player, CanvasDevice device, int width, int height, int fps, int frameCount,
        bool flipVertical)
    {
        _player = player;
        _device = device;
        _fps = fps;
        _flipVertical = flipVertical;
        Width = width;
        Height = height;
        FrameCount = frameCount;

        _surface = new CanvasRenderTarget(device, width, height, 96);
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
    }

    /// <summary>
    /// Opens <paramref name="videoFilePath"/> for frame-accurate random access, or returns
    /// null when the file is missing, unreadable, or not a decodable video.
    /// </summary>
    /// <param name="fps">
    /// The recording FPS that frame indices are expressed in. Frame index N maps to
    /// presentation time N / fps.
    /// </param>
    /// <param name="flipVertical">
    /// Compensates for recordings written by the pre-orientation-fix encoder, whose MP4s
    /// are vertically mirrored. See <see cref="Musio.Core.Capture.RecordingMarker"/>.
    /// </param>
    public static async Task<Mp4FrameSource?> OpenAsync(
        string videoFilePath, int fps, CanvasDevice device, bool flipVertical = false)
    {
        if (string.IsNullOrEmpty(videoFilePath) || fps <= 0)
            return null;

        MediaPlayer? player = null;
        try
        {
            var fullPath = Path.GetFullPath(videoFilePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length == 0)
                return null;

            var file = await StorageFile.GetFileFromPathAsync(fullPath);
            var source = MediaSource.CreateFromStorageFile(file);

            player = new MediaPlayer
            {
                IsVideoFrameServerEnabled = true,
                IsMuted = true,
                AutoPlay = false,
                RealTimePlayback = false,
            };
            player.CommandManager.IsEnabled = false;

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(MediaPlayer s, object a) => opened.TrySetResult(true);
            void OnFailed(MediaPlayer s, MediaPlayerFailedEventArgs a) => opened.TrySetResult(false);

            player.MediaOpened += OnOpened;
            player.MediaFailed += OnFailed;
            try
            {
                player.Source = new MediaPlaybackItem(source);

                if (await Task.WhenAny(opened.Task, Task.Delay(OpenTimeout)) != opened.Task
                    || !await opened.Task)
                {
                    Debug.WriteLine($"[Mp4FrameSource] Could not open '{videoFilePath}' for decoding.");
                    player.Dispose();
                    return null;
                }
            }
            finally
            {
                player.MediaOpened -= OnOpened;
                player.MediaFailed -= OnFailed;
            }

            var session = player.PlaybackSession;
            int width = (int)session.NaturalVideoWidth;
            int height = (int)session.NaturalVideoHeight;
            var duration = session.NaturalDuration;

            if (width <= 0 || height <= 0 || duration <= TimeSpan.Zero)
            {
                Debug.WriteLine(
                    $"[Mp4FrameSource] '{videoFilePath}' has no usable video track " +
                    $"({width}x{height}, {duration}).");
                player.Dispose();
                return null;
            }

            // Frame indices are CFR against the recording FPS, matching how VideoWriter
            // laid the JPEGs out, so the two sources stay interchangeable.
            int frameCount = Math.Max(1, (int)Math.Round(duration.TotalSeconds * fps));

            var reader = new Mp4FrameSource(
                player, device, width, height, fps, frameCount, flipVertical);
            player = null; // ownership transferred

            await reader.PrimeAsync();
            return reader;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] Failed to open '{videoFilePath}': {ex.Message}");
            player?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Forces the first frame through the pipeline so later seeks land on a warm decoder.
    /// Failure is non-fatal — the first real request will simply pay the warm-up cost.
    /// </summary>
    private async Task PrimeAsync()
    {
        try
        {
            using var first = await LoadFrameAsync(0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] Priming failed: {ex.Message}");
        }
    }

    private TimeSpan TimeForFrame(int frameIndex, int attempt)
    {
        // Land inside the frame's display interval rather than on its boundary, where
        // rounding inside the decoder can resolve to the neighbouring frame. Successive
        // attempts pick a different instant in the same interval so that re-assigning
        // Position is a real seek and not an ignored no-op, while still decoding the
        // frame that was asked for.
        double[] offsets = [0.5, 0.35, 0.65, 0.2];
        double offset = offsets[attempt % offsets.Length];
        return TimeSpan.FromSeconds((frameIndex + offset) / _fps);
    }

    public async Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
    {
        if (_disposed || frameIndex < 0 || frameIndex >= FrameCount)
            return null;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while this request was queued.
            return null;
        }

        try
        {
            if (_disposed)
                return null;

            // A frame abandoned by a previous timeout can still land later and would
            // otherwise satisfy this request with stale content — a wrong frame in an
            // export, not just a preview glitch. Let it drain first.
            await DrainIfNeededAsync().ConfigureAwait(false);
            if (_disposed)
                return null;

            if (!await PositionAtAsync(frameIndex).ConfigureAwait(false))
                return null;

            _lastDeliveredIndex = frameIndex;

            // _surface is reused across calls, so hand back an independent copy the
            // caller can hold and dispose on its own schedule.
            var copy = new CanvasRenderTarget(_device, Width, Height, 96);
            using (var ds = copy.CreateDrawingSession())
            {
                // Legacy recordings are stored upside down; correcting it here keeps the
                // preview, the export compositor and the filmstrip consistent, because
                // they all read frames through this one path.
                if (_flipVertical)
                {
                    ds.Transform =
                        System.Numerics.Matrix3x2.CreateScale(1, -1) *
                        System.Numerics.Matrix3x2.CreateTranslation(0, Height);
                }

                ds.DrawImage(_surface);
            }

            return copy;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] Failed to decode frame {frameIndex}: {ex.Message}");
            _lastDeliveredIndex = -1;
            _needsDrain = true;
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _framePending, null);
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Advances the decoder so <c>_surface</c> holds <paramref name="frameIndex"/>.
    /// </summary>
    /// <remarks>
    /// Stepping and seeking are not interchangeable. Assigning <c>Position</c> flushes the
    /// decoder and restarts from the enclosing keyframe, costing 10–100× a step, and on a
    /// paused player it sometimes raises no frame at all. So any small forward gap is
    /// walked with steps instead.
    /// <para>
    /// Small forward gaps are the norm, not the exception: the editor coalesces render
    /// requests it could not keep up with, so falling one frame behind turns the next
    /// request into <c>last + 2</c>. Treating that as a seek made playback progressively
    /// worse the further behind it got — the stutter fed itself.
    /// </para>
    /// </remarks>
    private async Task<bool> PositionAtAsync(int frameIndex)
    {
        int gap = frameIndex - _lastDeliveredIndex;

        if (_lastDeliveredIndex >= 0 && gap > 0 && gap <= MaxStepAhead)
        {
            for (int i = 0; i < gap; i++)
            {
                if (!await IssueAndWaitAsync(() => _player.StepForwardOneFrame(), StepTimeout)
                        .ConfigureAwait(false))
                {
                    // Stepping stalled. The abandoned step is for an index BEFORE the
                    // target, so its late frame must be drained before seeking or it can
                    // satisfy the seek's request and silently yield the wrong frame —
                    // an off-by-N that would be invisible in an export.
                    await DrainIfNeededAsync().ConfigureAwait(false);
                    if (_disposed)
                        return false;

                    _lastDeliveredIndex = -1;
                    return await SeekToAsync(frameIndex).ConfigureAwait(false);
                }
            }
            return true;
        }

        return await SeekToAsync(frameIndex).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits out a frame abandoned by a previous timeout, so it cannot be mistaken for
    /// the answer to the next request.
    /// </summary>
    private async Task DrainIfNeededAsync()
    {
        if (!_needsDrain)
            return;

        _needsDrain = false;
        await Task.Delay(DrainDelay).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeks to <paramref name="frameIndex"/>, re-issuing the seek a few times because a
    /// paused player intermittently completes a seek without rendering anything.
    /// </summary>
    /// <remarks>
    /// <see cref="MediaPlaybackSession.SeekCompleted"/> is what makes this bounded. Without
    /// it there is no way to tell a slow seek from one that finished and silently produced
    /// no frame, so every failure had to burn a full fixed timeout. Watching for it means a
    /// dead seek is re-issued as soon as it is known to be dead.
    /// </remarks>
    private async Task<bool> SeekToAsync(int frameIndex)
    {
        if (await TrySeekRoundAsync(frameIndex).ConfigureAwait(false))
            return true;

        // Every attempt completed without producing a frame. On a paused frame-server
        // player the pipeline can settle into a state where re-assigning Position never
        // wakes it again, and no number of extra seeks helps — but a step does. Step to
        // force it to produce, drop that frame (it is for the wrong index), then seek
        // again so what is finally delivered is still exactly the frame asked for. The
        // exporter reads through this path too, where accepting the stepped frame would
        // be a silently wrong frame rather than a preview glitch.
        if (_disposed)
            return false;

        // Wait out any frame abandoned by the failed attempts first, so it cannot satisfy
        // the wake step or the re-seek below and pass for the frame that was asked for.
        await DrainIfNeededAsync().ConfigureAwait(false);
        if (_disposed)
            return false;

        if (await IssueAndWaitAsync(() => _player.StepForwardOneFrame(), StepTimeout)
                .ConfigureAwait(false))
        {
            _lastDeliveredIndex = -1;

            // The wake step delivered a frame for the WRONG index. OnVideoFrameAvailable
            // does not guarantee exactly one raise per command, so a duplicate could
            // otherwise complete the re-seek's request and be handed back as the frame
            // that was asked for — an off-by-N baked silently into an export.
            _needsDrain = true;
            await DrainIfNeededAsync().ConfigureAwait(false);
            if (_disposed)
                return false;

            if (await TrySeekRoundAsync(frameIndex).ConfigureAwait(false))
                return true;
        }

        Debug.WriteLine(
            $"[Mp4FrameSource] Gave up seeking to frame {frameIndex} after {SeekAttempts} attempts.");
        _lastDeliveredIndex = -1;
        _needsDrain = true;
        return false;
    }

    /// <summary>
    /// One round of <see cref="SeekAttempts"/> seek attempts. Returns true as soon as a
    /// frame for <paramref name="frameIndex"/> lands.
    /// </summary>
    private async Task<bool> TrySeekRoundAsync(int frameIndex)
    {
        var session = _player.PlaybackSession;

        for (int attempt = 0; attempt < SeekAttempts; attempt++)
        {
            var seekDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSeekCompleted(MediaPlaybackSession s, object a) => seekDone.TrySetResult(true);
            session.SeekCompleted += OnSeekCompleted;

            try
            {
                var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _framePending, pending);

                // Each attempt targets a different instant *within the same frame's display
                // interval*: re-assigning an identical Position is a no-op that would never
                // wake the decoder, while every one of these offsets still resolves to
                // `frameIndex`.
                session.Position = TimeForFrame(frameIndex, attempt);

                // The frame usually arrives with, or shortly after, seek completion.
                var settled = await Task.WhenAny(
                    pending.Task,
                    Task.WhenAll(seekDone.Task, Task.Delay(PostSeekGrace)),
                    Task.Delay(AttemptTimeout)).ConfigureAwait(false);

                if (settled == pending.Task && await pending.Task.ConfigureAwait(false))
                    return true;

                // Seek finished (or timed out) without producing a frame; give the frame a
                // last short chance before re-issuing.
                if (await Task.WhenAny(pending.Task, Task.Delay(PostSeekGrace)).ConfigureAwait(false)
                        == pending.Task
                    && await pending.Task.ConfigureAwait(false))
                {
                    return true;
                }

                Interlocked.Exchange(ref _framePending, null);
                _needsDrain = true;

                if (_disposed)
                    return false;
            }
            finally
            {
                session.SeekCompleted -= OnSeekCompleted;
            }
        }

        return false;
    }

    /// <summary>
    /// Issues one decoder command and waits for the resulting frame to land in
    /// <c>_surface</c>.
    /// </summary>
    private async Task<bool> IssueAndWaitAsync(Action issue, TimeSpan timeout)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _framePending, pending);

        issue();

        if (await Task.WhenAny(pending.Task, Task.Delay(timeout)).ConfigureAwait(false) != pending.Task)
        {
            // Abandon this request so a late frame cannot satisfy the next one.
            Interlocked.Exchange(ref _framePending, null);
            _needsDrain = true;
            return false;
        }

        return await pending.Task.ConfigureAwait(false);
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        // Consume the request atomically. A single seek or step is not guaranteed to raise
        // this exactly once, and a second raise would overwrite _surface while the waiting
        // thread is already drawing from it — a silently wrong frame in an export.
        var pending = Interlocked.Exchange(ref _framePending, null);
        if (pending is null)
            return;

        try
        {
            sender.CopyFrameToVideoSurface(_surface);
            pending.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] CopyFrameToVideoSurface failed: {ex.Message}");
            pending.TrySetResult(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Exchange(ref _framePending, null)?.TrySetResult(false);

        // Wait for any in-flight request to leave the critical section before tearing
        // down the player and surface it is reading from.
        bool held = false;
        try
        {
            held = _gate.Wait(DisposeGateTimeout);
        }
        catch (ObjectDisposedException) { }

        try
        {
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.Source = null;
            _player.Dispose();
            _surface.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] Dispose failed: {ex.Message}");
        }
        finally
        {
            if (held) _gate.Release();
        }

        // _gate is deliberately NOT disposed. SemaphoreSlim.Dispose does not release
        // callers already parked in WaitAsync, so disposing it would strand any queued
        // request forever — a hung export rather than a dropped frame. The _disposed
        // check inside the critical section makes queued callers return null instead.
    }
}
