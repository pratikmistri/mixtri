using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Timeline;
using Windows.Foundation;

namespace Musio.Core.Processing;

/// <summary>
/// Wraps <see cref="FrameCompositor"/> at reduced resolution for real-time preview playback.
/// </summary>
public class PreviewRenderer : IDisposable
{
    private FrameCompositor? _compositor;
    private int _outputFps;
    private bool _disposed;
    private bool _suppressZoom;

    /// <summary>
    /// Composes at rest (1x) without disturbing the rest of the composition — see
    /// <see cref="FrameCompositor.SuppressZoom"/>. Survives a re-initialization, so the
    /// zoom-region picker keeps its rest frame across a compositor rebuild.
    /// </summary>
    public bool SuppressZoom
    {
        get => _suppressZoom;
        set
        {
            _suppressZoom = value;
            if (_compositor is not null) _compositor.SuppressZoom = value;
        }
    }

    /// <summary>
    /// The rect the source frame occupies inside the composed output, in output pixels.
    /// See <see cref="FrameCompositor.SourceAreaRect"/>.
    /// </summary>
    public Rect SourceAreaRect => _compositor?.SourceAreaRect ?? default;

    /// <summary>
    /// The source pixels visible in a frame composed at rest.
    /// See <see cref="FrameCompositor.RestSourceViewport"/>.
    /// </summary>
    public Rect RestSourceViewport => _compositor?.RestSourceViewport ?? default;

    /// <summary>
    /// The source pixels a zoom of the given level and normalised centre would show.
    /// See <see cref="FrameCompositor.ComputeRegionViewport"/>.
    /// </summary>
    public Rect ComputeRegionViewport(float zoomLevel, float centerXNormalized, float centerYNormalized)
        => _compositor?.ComputeRegionViewport(zoomLevel, centerXNormalized, centerYNormalized) ?? default;

    /// <summary>
    /// The area of the composed output a zoom region is chosen within.
    /// See <see cref="FrameCompositor.RegionCanvasRect"/>.
    /// </summary>
    public Rect RegionCanvasRect => _compositor?.RegionCanvasRect ?? default;

    /// <summary>
    /// The region of the composed output a zoom would show, in output pixels.
    /// See <see cref="FrameCompositor.ComputeRegionOutputRect"/>.
    /// </summary>
    public Rect ComputeRegionOutputRect(float zoomLevel, float centerXNormalized, float centerYNormalized)
        => _compositor?.ComputeRegionOutputRect(zoomLevel, centerXNormalized, centerYNormalized) ?? default;

    /// <summary>
    /// The range a zoom's normalised centre can occupy before the camera clamps.
    /// See <see cref="FrameCompositor.ComputeRegionCenterBounds"/>.
    /// </summary>
    public (double MinX, double MaxX, double MinY, double MaxY)? ComputeRegionCenterBounds(float zoomLevel)
        => _compositor?.ComputeRegionCenterBounds(zoomLevel);

    /// <summary>Preview output width (half of full export resolution).</summary>
    public int PreviewWidth { get; private set; }

    /// <summary>Preview output height (half of full export resolution).</summary>
    public int PreviewHeight { get; private set; }

    /// <summary>Compositor output width (includes padding).</summary>
    public int OutputWidth => _compositor?.OutputWidth ?? PreviewWidth;

    /// <summary>Compositor output height (includes padding).</summary>
    public int OutputHeight => _compositor?.OutputHeight ?? PreviewHeight;

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
        _compositor = new FrameCompositor(config) { SuppressZoom = _suppressZoom };
        await _compositor.InitializeAsync(mouseData, sourceWidth, sourceHeight, duration,
            mouseToVideoOffsetSeconds, cropOffsetX, cropOffsetY, dpiScale);
    }

    /// <summary>
    /// Warms the background-image cache for the configured background style so preview
    /// rendering never blocks the UI thread on file I/O or GPU decode.
    /// <see cref="InitializeAsync"/> already does this; call it again after any live
    /// background configuration change. A no-op when the image is already cached.
    /// </summary>
    public Task PrewarmBackgroundAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _compositor?.PrewarmBackgroundAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// True when the configured background can be rendered at full fidelity without
    /// blocking (non-image backgrounds are always ready).
    /// </summary>
    public bool IsBackgroundReady => _compositor?.IsBackgroundReady ?? true;

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
    /// Syncs the text overlays belonging to this preview's source recording to the
    /// compositor. Call this whenever the active/current preview recording changes and
    /// whenever a text overlay is added, edited, removed, or reordered (including
    /// undo/redo) — the same events that already drive <see cref="UpdateZoomKeyframes"/>.
    /// Callers should select the overlays with
    /// <see cref="Export.SegmentFrameComposer.SelectTextOverlays"/> so preview uses the
    /// identical per-source ownership rule export does.
    /// </summary>
    public void UpdateTextOverlays(IReadOnlyList<TextOverlaySegment> overlays)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compositor?.SyncTextOverlays(overlays);
    }

    /// <summary>
    /// Where the cursor is rendered at <paramref name="position"/>, in capture-frame pixels.
    /// See <see cref="FrameCompositor.TryGetCursorPosition"/>.
    /// </summary>
    public bool TryGetCursorPosition(TimeSpan position, out double x, out double y)
    {
        x = y = 0;
        return !_disposed
            && _compositor is not null
            && _compositor.TryGetCursorPosition(position.TotalSeconds, out x, out y);
    }

    /// <summary>
    /// The box the cursor drawn at <paramref name="position"/> occupies, relative to its
    /// hotspot and in compositor OUTPUT pixels.
    /// See <see cref="FrameCompositor.TryGetDrawnCursorBounds"/>.
    /// </summary>
    public bool TryGetDrawnCursorBounds(TimeSpan position, out Rect bounds)
    {
        bounds = default;
        return !_disposed
            && _compositor is not null
            && _compositor.TryGetDrawnCursorBounds(position.TotalSeconds, out bounds);
    }

    /// <summary>
    /// Syncs the cursor anchors belonging to this preview's source recording to the
    /// compositor. Call this whenever the active/current preview recording changes and
    /// whenever an anchor is added, moved, or removed (including undo/redo) — the same
    /// events that already drive <see cref="UpdateZoomKeyframes"/>.
    /// Callers should select the anchors with
    /// <see cref="Export.SegmentFrameComposer.SelectCursorAnchors"/> so preview uses the
    /// identical per-source ownership rule export does.
    /// </summary>
    public void UpdateCursorAnchors(IReadOnlyList<Timeline.CursorAnchor> anchors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compositor?.SyncCursorAnchors(anchors);
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

    /// <summary>
    /// Updates the webcam overlay style (position, size) without rebuilding the compositor.
    /// </summary>
    public void UpdateWebcamStyle(WebcamOverlayStyle style)
    {
        _compositor?.UpdateWebcamStyle(style);
    }

    /// <summary>
    /// Sets the webcam fullscreen-animation factor in <c>[0,1]</c> for the next render.
    /// </summary>
    public void SetWebcamFullscreenFactor(float factor)
    {
        _compositor?.SetWebcamFullscreenFactor(factor);
    }

    /// <summary>
    /// Sets the webcam overlay opacity in <c>[0,1]</c> for the next render (fade in/out).
    /// </summary>
    public void SetWebcamOverlayOpacity(float opacity)
    {
        _compositor?.SetWebcamOverlayOpacity(opacity);
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
