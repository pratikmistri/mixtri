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
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to let an abandoned frame arrive before issuing the next request after a
    /// timeout, so it cannot be mistaken for the answer to that request.
    /// </summary>
    private static readonly TimeSpan DrainDelay = TimeSpan.FromMilliseconds(250);

    private readonly MediaPlayer _player;
    private readonly CanvasDevice _device;
    private readonly CanvasRenderTarget _surface;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _fps;

    private TaskCompletionSource<bool>? _framePending;
    private int _lastDeliveredIndex = -1;
    private bool _needsDrain;
    private bool _disposed;

    public int FrameCount { get; }
    public int Width { get; }
    public int Height { get; }
    public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;

    private Mp4FrameSource(
        MediaPlayer player, CanvasDevice device, int width, int height, int fps, int frameCount)
    {
        _player = player;
        _device = device;
        _fps = fps;
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
    public static async Task<Mp4FrameSource?> OpenAsync(string videoFilePath, int fps, CanvasDevice device)
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

            var reader = new Mp4FrameSource(player, device, width, height, fps, frameCount);
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

    private TimeSpan TimeForFrame(int frameIndex)
    {
        // Aim at the middle of the frame's display interval. Landing exactly on a boundary
        // lets rounding inside the decoder resolve to the neighbouring frame.
        double seconds = (frameIndex + 0.5) / _fps;
        return TimeSpan.FromSeconds(seconds);
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
            if (_needsDrain)
            {
                _needsDrain = false;
                await Task.Delay(DrainDelay).ConfigureAwait(false);
                if (_disposed)
                    return null;
            }

            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _framePending = pending;

            var session = _player.PlaybackSession;

            // Stepping is not just an optimization. Assigning Position flushes the decoder
            // and restarts from the enclosing keyframe, so a sequential walk over a 1s GOP
            // would re-decode up to `fps` frames per frame — quadratic over an export.
            if (frameIndex == _lastDeliveredIndex + 1 && _lastDeliveredIndex >= 0)
                _player.StepForwardOneFrame();
            else
                session.Position = TimeForFrame(frameIndex);

            if (await Task.WhenAny(pending.Task, Task.Delay(FrameTimeout)).ConfigureAwait(false) != pending.Task)
            {
                Debug.WriteLine($"[Mp4FrameSource] Timed out waiting for frame {frameIndex}.");
                _lastDeliveredIndex = -1;
                _needsDrain = true;
                return null;
            }

            if (!await pending.Task.ConfigureAwait(false))
            {
                _lastDeliveredIndex = -1;
                return null;
            }

            _lastDeliveredIndex = frameIndex;

            // _surface is reused across calls, so hand back an independent copy the
            // caller can hold and dispose on its own schedule.
            var copy = new CanvasRenderTarget(_device, Width, Height, 96);
            using (var ds = copy.CreateDrawingSession())
                ds.DrawImage(_surface);

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

        try
        {
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.Source = null;
            _player.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mp4FrameSource] Dispose failed: {ex.Message}");
        }

        _surface.Dispose();
        _gate.Dispose();
    }
}
