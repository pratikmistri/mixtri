using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;

namespace Musio.Core.Processing;

/// <summary>
/// Wraps <see cref="FrameCompositor"/> at reduced resolution for real-time preview playback.
/// </summary>
public class PreviewRenderer : IDisposable
{
    private FrameCompositor? _compositor;
    private int _outputFps;
    private bool _disposed;

    /// <summary>Preview output width (half of full export resolution).</summary>
    public int PreviewWidth { get; private set; }

    /// <summary>Preview output height (half of full export resolution).</summary>
    public int PreviewHeight { get; private set; }

    /// <summary>Total number of frames available after initialization.</summary>
    public int TotalFrames => _compositor?.TotalFrames ?? 0;

    /// <summary>
    /// Initializes the preview pipeline. Uses the full source resolution
    /// for correct compositing — performance is managed via lower FPS.
    /// </summary>
    public async Task InitializeAsync(
        MouseRecordingData mouseData,
        CompositionConfig config,
        int sourceWidth,
        int sourceHeight,
        TimeSpan? duration = null,
        double mouseToVideoOffsetSeconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PreviewWidth = sourceWidth;
        PreviewHeight = sourceHeight;

        var previewConfig = config with
        {
            OutputFps = Math.Min(config.OutputFps, 30)
        };
        _outputFps = previewConfig.OutputFps;

        _compositor?.Dispose();
        _compositor = new FrameCompositor(previewConfig);
        await _compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, duration, mouseToVideoOffsetSeconds);
    }

    /// <summary>
    /// Renders a single preview frame at the given playback position.
    /// Computes the correct compositor frame index from the position using the
    /// preview output FPS, avoiding mismatches with the source video's FPS.
    /// Returns a <see cref="CanvasRenderTarget"/> the caller must dispose, or null if not initialized.
    /// </summary>
    public CanvasRenderTarget? RenderPreviewFrame(CanvasBitmap sourceFrame, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_compositor is null || TotalFrames <= 0) return null;

        int frameIndex = (int)(position.TotalSeconds * _outputFps);
        frameIndex = Math.Clamp(frameIndex, 0, TotalFrames - 1);

        return _compositor.ComposeFrame(sourceFrame, frameIndex);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _compositor?.Dispose();
            _compositor = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
