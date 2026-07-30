using Microsoft.Graphics.Canvas;

namespace Musio.Core.Processing;

/// <summary>
/// Reads frames from the loose JPEGs that <c>VideoWriter</c> writes into a session's
/// <c>.frames/</c> directory during capture.
/// </summary>
/// <remarks>
/// This directory is a write-ahead scratch buffer, not an archive: it is deleted as soon
/// as the MP4 finalizes successfully, and survives only when finalization failed. Treat
/// its presence as an optimization and never as a requirement.
/// </remarks>
public sealed class JpegFrameSource : IFrameSource
{
    private readonly string[] _framePaths;
    private readonly CanvasDevice _device;
    private bool _disposed;

    public int FrameCount => _framePaths.Length;
    public int Width { get; }
    public int Height { get; }
    public FrameSourceKind Kind => FrameSourceKind.CapturedJpeg;

    private JpegFrameSource(string[] framePaths, CanvasDevice device)
    {
        _framePaths = framePaths;
        _device = device;
    }

    /// <summary>
    /// Opens the <c>.frames/</c> directory under <paramref name="sessionFolder"/>, or
    /// returns null when it is absent or empty.
    /// </summary>
    public static JpegFrameSource? Open(string sessionFolder, CanvasDevice device)
    {
        try
        {
            var framesDir = Path.Combine(sessionFolder, VideoFrameReader.FramesDirectoryName);
            if (!Directory.Exists(framesDir))
                return null;

            var frames = Directory.GetFiles(framesDir, "frame_*.jpg");
            Array.Sort(frames, StringComparer.Ordinal);

            return frames.Length == 0 ? null : new JpegFrameSource(frames, device);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"[JpegFrameSource] Failed to open '{sessionFolder}': {ex.Message}");
            return null;
        }
    }

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

    public void Dispose() => _disposed = true;
}
