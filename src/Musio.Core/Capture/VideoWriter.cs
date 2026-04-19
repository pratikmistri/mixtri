using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Musio.Core.Capture;

/// <summary>
/// Captures Direct3D frames to JPEG images during recording, then assembles
/// them into a valid MP4 file in <see cref="FinalizeAsync"/>.
/// </summary>
public sealed class VideoWriter : IDisposable
{
    private readonly string _outputPath;
    private readonly string _framesDir;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly CanvasDevice _device;

    private long _frameCount;
    private bool _finalized;
    private bool _disposed;

    // Track actual timestamps for each frame to handle variable capture rate
    private readonly List<TimeSpan> _frameTimestamps = new(1000);
    private readonly object _tsLock = new();

    // Serializes all frame writes (WriteFrame + FillGapFrames) so concurrent
    // capture callbacks cannot interleave frame indices.
    private readonly object _writeLock = new();

    public string OutputPath => _outputPath;
    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Actual recording duration based on frame timestamps (diagnostic only).</summary>
    public TimeSpan ActualDuration
    {
        get
        {
            lock (_tsLock)
            {
                if (_frameTimestamps.Count <= 1)
                    return TimeSpan.Zero;

                // Include the last frame's display time so the duration covers
                // all captured frames, not just the span between first and last.
                var frameDuration = _frameTimestamps.Count >= 2
                    ? _frameTimestamps[^1] - _frameTimestamps[^2]
                    : TimeSpan.FromSeconds(1.0 / _fps);

                return _frameTimestamps[^1] - _frameTimestamps[0] + frameDuration;
            }
        }
    }

    /// <summary>Actual average FPS based on frame timestamps (diagnostic only).</summary>
    public double ActualFps
    {
        get
        {
            var dur = ActualDuration.TotalSeconds;
            return dur > 0 ? (FrameCount - 1) / dur : _fps;
        }
    }

    /// <summary>CFR duration: FrameCount / FPS. Use this for project metadata.</summary>
    public TimeSpan CfrDuration => _fps > 0
        ? TimeSpan.FromSeconds((double)Interlocked.Read(ref _frameCount) / _fps)
        : TimeSpan.Zero;

    public VideoWriter(string outputPath, int width, int height, int fps, IDirect3DDevice? captureDevice = null)
    {
        _outputPath = outputPath;
        _width = width;
        _height = height;
        _fps = fps;

        // Use the same D3D device as the capture engine to avoid cross-device failures
        if (captureDevice is not null)
            _device = CanvasDevice.CreateFromDirect3D11Device(captureDevice);
        else
            _device = CanvasDevice.GetSharedDevice();

        _framesDir = Path.Combine(Path.GetDirectoryName(outputPath)!, ".frames");
        Directory.CreateDirectory(_framesDir);
    }

    /// <summary>
    /// Copies the GPU surface to a JPEG file on disk.
    /// Called from the capture engine's thread-pool callback.
    /// </summary>
    public void WriteFrame(IDirect3DSurface surface, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
            throw new InvalidOperationException("Writer has been finalized.");

        try
        {
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);

            lock (_writeLock)
            {
                long index = Interlocked.Increment(ref _frameCount) - 1;

                lock (_tsLock)
                {
                    _frameTimestamps.Add(timestamp);
                }

                string framePath = Path.Combine(_framesDir, $"frame_{index:D8}.jpg");

                using var stream = new FileStream(framePath, FileMode.Create, FileAccess.Write);
                bitmap.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.85f)
                      .AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // Log frame write failures prominently for debugging
            System.Diagnostics.Debug.WriteLine($"[VideoWriter] ERROR frame {Interlocked.Read(ref _frameCount)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fills missed frame slots by duplicating the most recently written JPEG.
    /// This preserves true CFR timing so frame N always corresponds to
    /// wall-clock time N/fps after the first frame. Must be called BEFORE
    /// <see cref="WriteFrame"/> for the current frame.
    /// </summary>
    public void FillGapFrames(int count)
    {
        if (count <= 0) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_writeLock)
        {
            long prevIndex = Interlocked.Read(ref _frameCount) - 1;
            if (prevIndex < 0) return;

            string srcPath = Path.Combine(_framesDir, $"frame_{prevIndex:D8}.jpg");
            if (!File.Exists(srcPath)) return;

            for (int i = 0; i < count; i++)
            {
                long gapIndex = Interlocked.Increment(ref _frameCount) - 1;
                string dstPath = Path.Combine(_framesDir, $"frame_{gapIndex:D8}.jpg");

                try
                {
                    File.Copy(srcPath, dstPath, overwrite: true);

                    // Synthetic timestamp: interpolate between previous and next slot
                    lock (_tsLock)
                    {
                        var lastTs = _frameTimestamps.Count > 0
                            ? _frameTimestamps[^1]
                            : TimeSpan.Zero;
                        _frameTimestamps.Add(lastTs + TimeSpan.FromSeconds(1.0 / _fps));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VideoWriter] Gap fill failed at index {gapIndex}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Assembles all captured JPEG frames into an MP4 file using <see cref="MediaComposition"/>.
    /// </summary>
    public async Task FinalizeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
            return;

        _finalized = true;

        long totalFrames = Interlocked.Read(ref _frameCount);
        if (totalFrames == 0)
            return;

        var composition = new MediaComposition();

        // Use constant frame duration for true CFR output.
        // The slot-based capture throttling ensures frames arrive at ~1/fps intervals,
        // so constant duration matches real wall-clock time.
        var constantDuration = TimeSpan.FromSeconds(1.0 / _fps);

        for (long i = 0; i < totalFrames; i++)
        {
            string framePath = Path.Combine(_framesDir, $"frame_{i:D8}.jpg");
            if (!File.Exists(framePath))
                continue;

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(framePath));
            var clip = await MediaClip.CreateFromImageFileAsync(file, constantDuration);
            composition.Clips.Add(clip);
        }

        if (composition.Clips.Count == 0)
            return;

        // Encode to MP4
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        if (profile.Video is not null)
        {
            profile.Video.Width = (uint)_width;
            profile.Video.Height = (uint)_height;
            profile.Video.FrameRate.Numerator = (uint)_fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = 20_000_000;
        }

        string dir = Path.GetDirectoryName(_outputPath)!;
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(dir));
        var outputFile = await folder.CreateFileAsync(
            Path.GetFileName(_outputPath), CreationCollisionOption.ReplaceExisting);

        var renderOp = composition.RenderToFileAsync(
            outputFile, MediaTrimmingPreference.Precise, profile);

        var tcs = new TaskCompletionSource<object?>();
        renderOp.Completed = (info, status) =>
        {
            if (status == Windows.Foundation.AsyncStatus.Completed)
                tcs.TrySetResult(null);
            else if (status == Windows.Foundation.AsyncStatus.Canceled)
                tcs.TrySetCanceled();
            else
                tcs.TrySetException(
                    info.ErrorCode ?? new InvalidOperationException("MP4 render failed."));
        };

        await tcs.Task;

        // Keep frame images for editor preview — they'll be cleaned up
        // when the project is explicitly deleted or on next recording.
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_finalized)
        {
            // Frames directory is kept for editor preview
        }
    }
}
