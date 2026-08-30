using Microsoft.Graphics.Canvas;
using Mixtri.Core.Capture;

namespace Mixtri.Core.Processing;

/// <summary>
/// Provides frame-at-time lookup for the editor preview and the export compositor,
/// backed by either a session's captured JPEGs or its finalized MP4.
/// </summary>
/// <remarks>
/// <para>
/// The <c>.frames/</c> JPEG directory is preferred when present because it needs no
/// decoding, but it is transient — it is deleted as soon as the MP4 finalizes. The MP4
/// is the durable source and is what keeps a project editable indefinitely. Callers do
/// not care which one is in use.
/// </para>
/// <para>
/// Decoding an MP4 frame is far more expensive than loading a JPEG, so a small LRU of
/// recently decoded frames sits in front of the source. The editor re-requests the frame
/// at the current playhead on every property change, which would otherwise re-seek the
/// decoder for an identical result.
/// </para>
/// </remarks>
public sealed class VideoFrameReader : IDisposable
{
    /// <summary>Name of the transient per-session captured-frame scratch directory.</summary>
    public const string FramesDirectoryName = ".frames";

    private const int DefaultCacheCapacity = 12;
    private const long PreviewCacheBudgetBytes = 192L * 1024 * 1024;

    private readonly IFrameSource _source;
    private readonly int _fps;
    private readonly long _cacheBudgetBytes;

    private readonly Dictionary<int, CanvasBitmap> _cache = [];
    private readonly LinkedList<int> _cacheOrder = new();
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private long _cachedBytes;

    private bool _disposed;

    public int FrameCount => _source.FrameCount;
    public int Fps => _fps;

    /// <summary>Which backing store frames are being read from.</summary>
    public FrameSourceKind SourceKind => _source.Kind;

    public TimeSpan Duration => _fps > 0
        ? TimeSpan.FromSeconds((double)_source.FrameCount / _fps)
        : TimeSpan.Zero;

    private VideoFrameReader(IFrameSource source, int fps, long cacheBudgetBytes = 0)
    {
        _source = source;
        _fps = fps;
        _cacheBudgetBytes = cacheBudgetBytes;
    }

    /// <summary>
    /// Opens a session folder, preferring its captured JPEGs and falling back to the
    /// finalized <c>video.mp4</c>. Returns null when neither is usable.
    /// </summary>
    public static async Task<VideoFrameReader?> OpenSessionAsync(string sessionFolder, int fps)
    {
        if (string.IsNullOrEmpty(sessionFolder) || fps <= 0)
            return null;

        var device = CanvasDevice.GetSharedDevice();

        var jpeg = JpegFrameSource.Open(sessionFolder, device);
        if (jpeg is not null)
            return new VideoFrameReader(jpeg, fps);

        var videoPath = Path.Combine(sessionFolder, "video.mp4");
        var mp4 = await Mp4FrameSource.OpenAsync(
            videoPath, fps, device, RecordingMarker.NeedsVerticalFlip(videoPath))
            .ConfigureAwait(false);
        return mp4 is not null ? new VideoFrameReader(mp4, fps) : null;
    }

    /// <summary>
    /// Opens the recording that <paramref name="videoFilePath"/> belongs to, preferring the
    /// captured JPEGs alongside it and falling back to decoding the file itself.
    /// </summary>
    public static async Task<VideoFrameReader?> OpenFromVideoPathAsync(string videoFilePath, int fps)
    {
        if (string.IsNullOrEmpty(videoFilePath) || fps <= 0)
            return null;

        var device = CanvasDevice.GetSharedDevice();

        var dir = Path.GetDirectoryName(videoFilePath);
        if (dir is not null)
        {
            var jpeg = JpegFrameSource.Open(dir, device);
            if (jpeg is not null)
                return new VideoFrameReader(jpeg, fps);
        }

        var mp4 = await Mp4FrameSource.OpenAsync(
            videoFilePath, fps, device, RecordingMarker.NeedsVerticalFlip(videoFilePath))
            .ConfigureAwait(false);
        return mp4 is not null ? new VideoFrameReader(mp4, fps) : null;
    }

    /// <summary>
    /// Opens a reduced-resolution, low-latency reader for interactive editor preview.
    /// Export and other correctness-first callers continue using
    /// <see cref="OpenFromVideoPathAsync"/>.
    /// </summary>
    public static async Task<VideoFrameReader?> OpenPreviewFromVideoPathAsync(
        string videoFilePath, int fps, int maxWidth, int maxHeight,
        long cacheBudgetBytes = PreviewCacheBudgetBytes)
    {
        if (string.IsNullOrEmpty(videoFilePath) || fps <= 0)
            return null;

        var device = CanvasDevice.GetSharedDevice();
        var dir = Path.GetDirectoryName(videoFilePath);
        if (dir is not null)
        {
            var jpeg = JpegFrameSource.Open(dir, device);
            if (jpeg is not null)
                return new VideoFrameReader(jpeg, fps);
        }

        var mp4 = await Mp4FrameSource.OpenAsync(
            videoFilePath,
            fps,
            device,
            RecordingMarker.NeedsVerticalFlip(videoFilePath),
            FrameSourceOptions.CreatePreview(maxWidth, maxHeight)).ConfigureAwait(false);

        return mp4 is not null
            ? new VideoFrameReader(mp4, fps, Math.Max(0, cacheBudgetBytes))
            : null;
    }

    /// <summary>
    /// Gets the frame index for a given timestamp.
    /// </summary>
    public int GetFrameIndex(TimeSpan time)
    {
        int index = (int)(time.TotalSeconds * _fps);
        return Math.Clamp(index, 0, Math.Max(0, _source.FrameCount - 1));
    }

    /// <summary>
    /// Loads a frame as a CanvasBitmap at the given index.
    /// Caller must dispose the returned bitmap.
    /// </summary>
    public async Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
    {
        if (_disposed || frameIndex < 0 || frameIndex >= _source.FrameCount)
            return null;

        // JPEG loads are cheap and already backed by the OS file cache; adding a GPU-side
        // cache on top would cost memory for no gain.
        if (_source.Kind == FrameSourceKind.CapturedJpeg)
            return await _source.LoadFrameAsync(frameIndex).ConfigureAwait(false);

        if (!await TryEnterCacheAsync().ConfigureAwait(false))
            return null;
        try
        {
            if (_cache.TryGetValue(frameIndex, out var cached))
            {
                Touch(frameIndex);
                return Clone(cached);
            }
        }
        finally
        {
            ReleaseCache();
        }

        var decoded = await _source.LoadFrameAsync(frameIndex).ConfigureAwait(false);
        if (decoded is null)
            return null;

        if (!await TryEnterCacheAsync().ConfigureAwait(false))
        {
            // Disposed while decoding — the caller still owns this bitmap.
            return decoded;
        }
        try
        {
            if (_cache.TryGetValue(frameIndex, out var raced))
            {
                // Another caller cached this frame while we were decoding.
                Touch(frameIndex);
                decoded.Dispose();
                return Clone(raced);
            }

            _cache[frameIndex] = decoded;
            _cachedBytes += EstimateBytes(decoded);
            _cacheOrder.AddFirst(frameIndex);
            Evict();

            return Clone(decoded);
        }
        finally
        {
            ReleaseCache();
        }
    }

    /// <summary>
    /// Acquires the cache lock, returning false when the reader has been disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> takes the same lock before tearing the cache down, so holding
    /// it guarantees no cached bitmap is disposed while it is being cloned. Callers treat
    /// a disposed reader as "no frame" rather than an error, matching the pre-existing
    /// non-throwing contract of this method.
    /// </remarks>
    private async Task<bool> TryEnterCacheAsync()
    {
        if (_disposed)
            return false;

        try
        {
            await _cacheGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (_disposed)
        {
            ReleaseCache();
            return false;
        }

        return true;
    }

    private void ReleaseCache()
    {
        try { _cacheGate.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Loads the frame at the given timestamp.
    /// Caller must dispose the returned bitmap.
    /// </summary>
    public Task<CanvasBitmap?> LoadFrameAtTimeAsync(TimeSpan time)
        => LoadFrameAsync(GetFrameIndex(time));

    private void Touch(int frameIndex)
    {
        _cacheOrder.Remove(frameIndex);
        _cacheOrder.AddFirst(frameIndex);
    }

    private void Evict()
    {
        while (_cacheBudgetBytes > 0
            ? _cachedBytes > _cacheBudgetBytes
            : _cacheOrder.Count > DefaultCacheCapacity)
        {
            var oldest = _cacheOrder.Last!.Value;
            _cacheOrder.RemoveLast();
            if (_cache.Remove(oldest, out var bitmap))
            {
                _cachedBytes -= EstimateBytes(bitmap);
                bitmap.Dispose();
            }
        }
    }

    internal static long EstimateBytes(CanvasBitmap bitmap)
        => checked((long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4);

    /// <summary>
    /// Returns an independent copy so cache eviction can never dispose a bitmap a caller
    /// is still drawing with.
    /// </summary>
    private static CanvasBitmap Clone(CanvasBitmap source)
    {
        var copy = Win2DUtils.CreateRenderTarget(
            GpuContext.GetSharedDevice(),
            source.SizeInPixels.Width,
            source.SizeInPixels.Height,
            96,
            "video frame cache clone");

        using (var ds = copy.CreateDrawingSession())
            ds.DrawImage(source);

        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Wait for any in-flight clone to finish before disposing the bitmaps it is
        // reading from. The gate is only ever held for GPU blits, so this is brief.
        bool held = false;
        try
        {
            held = _cacheGate.Wait(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException) { }

        try
        {
            foreach (var bitmap in _cache.Values)
                bitmap.Dispose();
            _cache.Clear();
            _cacheOrder.Clear();
            _cachedBytes = 0;
        }
        finally
        {
            if (held) ReleaseCache();
        }

        _source.Dispose();

        // _cacheGate is deliberately NOT disposed: SemaphoreSlim.Dispose does not release
        // callers already parked in WaitAsync, so disposing it would strand them forever.
    }
}
