using Microsoft.Graphics.Canvas;

namespace Musio.Core.Processing;

/// <summary>
/// Reads captured JPEG frames from a recording session's .frames directory
/// and provides frame-at-time lookup for the editor preview.
/// </summary>
public sealed class VideoFrameReader : IDisposable
{
    private readonly string[] _framePaths;
    private readonly int _fps;
    private readonly CanvasDevice _device;
    private bool _disposed;

    public int FrameCount => _framePaths.Length;
    public int Fps => _fps;
    public TimeSpan Duration => _fps > 0
        ? TimeSpan.FromSeconds((double)_framePaths.Length / _fps)
        : TimeSpan.Zero;

    private VideoFrameReader(string[] framePaths, int fps, CanvasDevice device)
    {
        _framePaths = framePaths;
        _fps = fps;
        _device = device;
    }

    /// <summary>
    /// Creates a reader from a session folder that contains a .frames/ subdirectory.
    /// </summary>
    public static VideoFrameReader? OpenSession(string sessionFolder, int fps)
    {
        try
        {
            var framesDir = Path.Combine(sessionFolder, ".frames");
            if (!Directory.Exists(framesDir))
                return null;

            var frames = Directory.GetFiles(framesDir, "frame_*.jpg");
            Array.Sort(frames);

            if (frames.Length == 0)
                return null;

            return new VideoFrameReader(frames, fps, CanvasDevice.GetSharedDevice());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoFrameReader] Failed to open session: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a reader from a video file path (looks for .frames/ in the same directory).
    /// </summary>
    public static VideoFrameReader? OpenFromVideoPath(string videoFilePath, int fps)
    {
        var dir = Path.GetDirectoryName(videoFilePath);
        return dir is not null ? OpenSession(dir, fps) : null;
    }

    /// <summary>
    /// Gets the frame index for a given timestamp.
    /// </summary>
    public int GetFrameIndex(TimeSpan time)
    {
        int index = (int)(time.TotalSeconds * _fps);
        return Math.Clamp(index, 0, Math.Max(0, _framePaths.Length - 1));
    }

    /// <summary>
    /// Loads a frame as a CanvasBitmap at the given index.
    /// Caller must dispose the returned bitmap.
    /// </summary>
    public async Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
    {
        if (_disposed || frameIndex < 0 || frameIndex >= _framePaths.Length)
            return null;

        var path = _framePaths[frameIndex];
        if (!File.Exists(path))
            return null;

        try
        {
            return await CanvasBitmap.LoadAsync(_device, path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the frame at the given timestamp.
    /// Caller must dispose the returned bitmap.
    /// </summary>
    public Task<CanvasBitmap?> LoadFrameAtTimeAsync(TimeSpan time)
        => LoadFrameAsync(GetFrameIndex(time));

    public void Dispose()
    {
        _disposed = true;
    }
}
