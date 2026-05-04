using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Timeline;

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
        double mouseToVideoOffsetSeconds = 0,
        int cropOffsetX = 0,
        int cropOffsetY = 0,
        float dpiScale = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PreviewWidth = sourceWidth;
        PreviewHeight = sourceHeight;
        _outputFps = config.OutputFps;

        _compositor?.Dispose();
        _compositor = new FrameCompositor(config);
        await _compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, duration,
            mouseToVideoOffsetSeconds, cropOffsetX, cropOffsetY, dpiScale);
    }

    /// <summary>
    /// Renders a single preview frame at the given playback position.
    /// Uses the exact playback time for cursor, click, and zoom alignment
    /// rather than deriving time from a frame index.
    /// Returns a <see cref="CanvasRenderTarget"/> the caller must dispose, or null if not initialized.
    /// </summary>
    public CanvasRenderTarget? RenderPreviewFrame(CanvasBitmap sourceFrame, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_compositor is null || TotalFrames <= 0) return null;

        return _compositor.ComposeFrame(sourceFrame, position.TotalSeconds);
    }

    /// <summary>
    /// Syncs manual zoom keyframes from the editor model to the compositor's zoom engine.
    /// Call this when zoom keyframes are added, removed, or changed (including undo/redo).
    /// </summary>
    public void UpdateZoomKeyframes(IReadOnlyList<Timeline.ZoomKeyframe> keyframes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compositor?.SyncManualZoomKeyframes(keyframes);
    }

    /// <summary>
    /// Syncs the set of suppressed auto-zoom click ticks to the compositor.
    /// Call this when auto-generated zoom segments are deleted or restored (undo).
    /// </summary>
    public void UpdateSuppressedClickTicks(IReadOnlyCollection<long> suppressedTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compositor?.SyncSuppressedClickTicks(suppressedTicks);
    }

    /// <summary>
    /// Sets the current webcam frame for overlay compositing.
    /// </summary>
    public void SetWebcamFrame(CanvasBitmap? webcamFrame)
    {
        _compositor?.SetWebcamFrame(webcamFrame);
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
