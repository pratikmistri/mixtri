using Microsoft.Graphics.Canvas;
using Musio.Core.AI;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Windows.Foundation;

namespace Musio.Core.Processing;

public record CompositionConfig
{
    public BackgroundStyle Background { get; init; } = new();
    public CursorStyle Cursor { get; init; } = new();
    public AutoZoomConfig Zoom { get; init; } = new();
    public SmoothingAlgorithm SmoothingAlgorithm { get; init; } = SmoothingAlgorithm.SpringPhysics;
    public SmoothingStrength SmoothingStrength { get; init; } = SmoothingStrength.Smooth;
    public int OutputFps { get; init; } = 30;
    public AspectRatio AspectRatio { get; init; } = AspectRatio.Auto;
    public WebcamOverlayStyle? WebcamStyle { get; init; }
    public KeyboardOverlayStyle? KeyboardStyle { get; init; }
    public SubtitleStyle? SubtitleStyle { get; init; }
    public List<SubtitleSegment>? Subtitles { get; init; }
    public List<KeyPressEvent>? KeyboardEvents { get; init; }
}

/// <summary>
/// Master compositor that combines background, zoomed screen content, and cursor overlay
/// into final output frames. Call <see cref="InitializeAsync"/> once, then
/// <see cref="ComposeFrame"/> for each frame index.
/// </summary>
public class FrameCompositor : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly BackgroundCompositor _bgCompositor;
    private readonly CursorRenderer _cursorRenderer;
    private readonly AutoZoomEngine _zoomEngine;
    private readonly CursorSmoother _smoother;
    private readonly CompositionConfig _config;

    // Optional overlay renderers
    private WebcamCompositor? _webcamCompositor;
    private KeyboardOverlayRenderer? _keyboardRenderer;
    private SubtitleBurner? _subtitleBurner;
    private List<KeyPressEvent>? _keyboardEvents;
    private double _tickFrequency;

    private List<SmoothedPosition> _smoothedPositions = [];
    private double[] _lastMoveTimes = [];
    private MouseRecordingData? _mouseData;
    private CanvasBitmap? _webcamFrame;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _contentWidth;
    private int _contentHeight;
    private bool _initialized;
    private bool _disposed;
    private float _coordScaleX = 1.0f;
    private float _coordScaleY = 1.0f;
    private double _mouseTimeOffset;

    // Tick value corresponding to video time 0, for rebasing keyboard events.
    private long _videoStartTick;

    // Offset between mouse-frame indices and video-frame indices.
    // smoothedPositions[videoFrame + _videoFrameOffset] gives the cursor
    // position at video time = videoFrame / outputFps.
    private int _videoFrameOffset;

    public int TotalFrames { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    public FrameCompositor(CompositionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _device = CanvasDevice.GetSharedDevice();
        _bgCompositor = new BackgroundCompositor();
        _cursorRenderer = new CursorRenderer(config.Cursor);
        _zoomEngine = new AutoZoomEngine(config.Zoom);
        _smoother = new CursorSmoother
        {
            Algorithm = config.SmoothingAlgorithm,
            Strength = config.SmoothingStrength,
        };

        // Initialize optional overlay components from config
        if (config.WebcamStyle is not null)
            _webcamCompositor = new WebcamCompositor(config.WebcamStyle);

        if (config.KeyboardStyle is not null)
        {
            _keyboardRenderer = new KeyboardOverlayRenderer(config.KeyboardStyle);
            _keyboardEvents = config.KeyboardEvents;
        }

        if (config.SubtitleStyle is not null && config.Subtitles is not null)
            _subtitleBurner = new SubtitleBurner(config.Subtitles, config.SubtitleStyle);
    }

    /// <summary>
    /// Processes mouse data, builds the zoom timeline, smooths the cursor path,
    /// and loads cursor resources. Must be called before <see cref="ComposeFrame"/>.
    /// The <paramref name="duration"/> parameter controls the total export duration
    /// (typically the project's video duration). If mouse data covers less time,
    /// the last cursor position is held; if more, it is truncated.
    /// </summary>
    public async Task InitializeAsync(
        MouseRecordingData mouseData,
        int sourceWidth,
        int sourceHeight,
        TimeSpan? duration = null,
        double mouseToVideoOffsetSeconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mouseData);
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        _mouseData = mouseData;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _tickFrequency = mouseData.TickFrequency;

        // Detect DPI scale: mouse hook reports logical coords, capture is physical pixels.
        // Use actual screen metrics for reliable scale detection.
        float coordScaleX = GetSystemDpiScale(sourceWidth, isWidth: true);
        float coordScaleY = GetSystemDpiScale(sourceHeight, isWidth: false);
        _coordScaleX = coordScaleX;
        _coordScaleY = coordScaleY;

        // Compute content dimensions based on aspect ratio (center-crop)
        ComputeContentDimensions();

        // Output = content + padding on all sides
        var (outW, outH) = _bgCompositor.CalculateOutputSize(
            _contentWidth, _contentHeight, _config.Background);
        OutputWidth = outW;
        OutputHeight = outH;

        // Smooth cursor path at the target FPS, then scale to physical coordinates
        // and apply time offset to align mouse timeline with video frames
        _smoothedPositions = _smoother.SmoothPath(mouseData, _config.OutputFps);
        for (int i = 0; i < _smoothedPositions.Count; i++)
        {
            var p = _smoothedPositions[i];
            _smoothedPositions[i] = new SmoothedPosition
            {
                X = p.X * coordScaleX,
                Y = p.Y * coordScaleY,
                TimestampSeconds = p.TimestampSeconds - mouseToVideoOffsetSeconds,
                VelocityX = p.VelocityX * coordScaleX,
                VelocityY = p.VelocityY * coordScaleY,
            };
        }

        // Store offset for click timestamp alignment
        _mouseTimeOffset = mouseToVideoOffsetSeconds;

        // Compute the index offset between mouse frames and video frames.
        // Mouse frame 0 = mouse start; video frame 0 = video start.
        // Video frame N needs the cursor at mouse time (N/fps + offset),
        // which is mouse frame (N + offset*fps).
        _videoFrameOffset = (int)Math.Round(mouseToVideoOffsetSeconds * _config.OutputFps);

        // Compute the tick corresponding to video time 0 for keyboard overlay alignment.
        // Video t=0 is mouseStart + offset in tick space.
        _videoStartTick = mouseData.StartTimestampTicks
            + (long)(mouseToVideoOffsetSeconds * mouseData.TickFrequency);

        // Compute TotalFrames from the authoritative duration (video/project).
        // Keep enough smoothed positions so the cursor has valid data for
        // the entire video, including positions that are offset-shifted.
        // Without the extra positions, the cursor freezes near the video end.
        if (duration.HasValue && duration.Value.TotalSeconds > 0)
        {
            TotalFrames = (int)(duration.Value.TotalSeconds * _config.OutputFps);
            int requiredPositions = TotalFrames + Math.Max(0, _videoFrameOffset);
            AdjustSmoothedPositionsToFrameCount(requiredPositions);
        }
        else
        {
            TotalFrames = _smoothedPositions.Count;
        }

        // Precompute per-frame "last move" timestamps for cursor auto-hide
        PrecomputeLastMoveTimes();

        // Build auto-zoom timeline with scaled coordinates and time offset + capture latency
        _zoomEngine.BuildZoomTimeline(
            mouseData, sourceWidth, sourceHeight, mouseData.TickFrequency,
            coordScaleX, coordScaleY, mouseToVideoOffsetSeconds);

        // Load cursor bitmap / geometry
        _cursorRenderer.StartTimestampTicks = mouseData.StartTimestampTicks;
        _cursorRenderer.TickFrequency = mouseData.TickFrequency;
        await _cursorRenderer.LoadCursorAsync(_device);

        _initialized = true;
    }

    /// <summary>
    /// Gets the system DPI scale factor by comparing logical screen size to
    /// the capture frame dimensions. Uses P/Invoke to get actual screen bounds.
    /// </summary>
    private static float GetSystemDpiScale(int capturedDimension, bool isWidth)
    {
        try
        {
            // Get the primary monitor's logical resolution
            int logicalSize = isWidth
                ? GetSystemMetrics(SM_CXSCREEN)
                : GetSystemMetrics(SM_CYSCREEN);

            if (logicalSize > 0 && capturedDimension > logicalSize)
                return (float)capturedDimension / logicalSize;
        }
        catch { }

        return 1.0f;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Adjusts the smoothed positions list to match the desired frame count.
    /// If the mouse data produced fewer frames than the video duration requires,
    /// the last known position is held. If it produced more, excess frames are truncated.
    /// </summary>
    private void AdjustSmoothedPositionsToFrameCount(int targetFrameCount)
    {
        if (_smoothedPositions.Count == targetFrameCount)
            return;

        if (_smoothedPositions.Count > targetFrameCount)
        {
            // Mouse data is longer than video — truncate
            _smoothedPositions = _smoothedPositions.GetRange(0, targetFrameCount);
        }
        else if (_smoothedPositions.Count < targetFrameCount && _smoothedPositions.Count > 0)
        {
            // Mouse data is shorter than video — hold last position
            var lastPos = _smoothedPositions[^1];
            double dt = 1.0 / _config.OutputFps;

            for (int i = _smoothedPositions.Count; i < targetFrameCount; i++)
            {
                _smoothedPositions.Add(new SmoothedPosition
                {
                    X = lastPos.X,
                    Y = lastPos.Y,
                    TimestampSeconds = i * dt,
                    VelocityX = 0,
                    VelocityY = 0,
                });
            }
        }
        else if (_smoothedPositions.Count == 0 && targetFrameCount > 0)
        {
            // No mouse data at all — generate static positions at origin
            double dt = 1.0 / _config.OutputFps;
            for (int i = 0; i < targetFrameCount; i++)
            {
                _smoothedPositions.Add(new SmoothedPosition
                {
                    X = _sourceWidth / 2.0,
                    Y = _sourceHeight / 2.0,
                    TimestampSeconds = i * dt,
                    VelocityX = 0,
                    VelocityY = 0,
                });
            }
        }
    }

    /// <summary>
    /// Sets the current webcam frame to be composited. The caller is responsible for
    /// updating this each frame when webcam overlay is enabled.
    /// The compositor does NOT own this bitmap — the caller manages its lifetime.
    /// </summary>
    public void SetWebcamFrame(CanvasBitmap? webcamFrame)
    {
        _webcamFrame = webcamFrame;
    }

    /// <summary>
    /// Replaces the zoom engine's manual keyframes with the provided list.
    /// Call this when the user adds or removes zoom keyframes in the editor.
    /// </summary>
    public void SyncManualZoomKeyframes(IReadOnlyList<Timeline.ZoomKeyframe> keyframes)
    {
        _zoomEngine.SetManualKeyframes(keyframes);
    }

    /// <summary>
    /// Updates which auto-generated click zooms are suppressed (i.e. deleted by the user).
    /// Triggers a rebuild of the auto-zoom segments, excluding suppressed clicks.
    /// </summary>
    public void SyncSuppressedClickTicks(IReadOnlyCollection<long> suppressedTicks)
    {
        _zoomEngine.SetSuppressedClickTicks(suppressedTicks);
    }

    /// <summary>
    /// Compose a single output frame at the given frame index.
    /// Returns a <see cref="CanvasRenderTarget"/> that the caller must dispose.
    /// </summary>
    public CanvasRenderTarget ComposeFrame(CanvasBitmap sourceFrame, int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= TotalFrames)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        double timeSeconds = (double)frameIndex / _config.OutputFps;
        return ComposeFrame(sourceFrame, timeSeconds);
    }

    /// <summary>
    /// Compose a single output frame at an explicit source time (in seconds,
    /// relative to video start). Use this overload during export to avoid
    /// frame-index truncation drift between the visual frame and effects.
    /// </summary>
    public CanvasRenderTarget ComposeFrame(CanvasBitmap sourceFrame, double sourceTimeSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("Call InitializeAsync before compositing frames.");
        ArgumentNullException.ThrowIfNull(sourceFrame);

        double timeSeconds = sourceTimeSeconds;

        // Compute cursor index from source time directly, avoiding integer
        // frame-index truncation. Mouse frame = (videoTime + offset) * fps.
        int cursorIndex = Math.Clamp(
            (int)Math.Round((sourceTimeSeconds + _mouseTimeOffset) * _config.OutputFps),
            0, _smoothedPositions.Count - 1);
        var cursorPos = _smoothedPositions[cursorIndex];

        // Get zoom state — use smoothed cursor position as center hint
        // so the viewport always keeps the cursor in view
        var zoomState = _zoomEngine.GetZoomState(timeSeconds);
        if (zoomState.ZoomLevel > 1.01f && !zoomState.IsManualOverride)
        {
            // Override zoom center with actual cursor position for auto segments.
            // Manual segments keep their user-defined center.
            zoomState = _zoomEngine.ComputeViewportForCenter(
                zoomState.ZoomLevel, (float)cursorPos.X, (float)cursorPos.Y);
        }

        // Adjust viewport for target aspect ratio (center-crop within viewport)
        var viewport = ComputeEffectiveViewport(zoomState);

        // Crop source frame to the effective viewport, scaled to content dimensions
        using var croppedFrame = CropSourceFrame(sourceFrame, viewport);

        // Create output render target
        var output = new CanvasRenderTarget(_device, OutputWidth, OutputHeight, 96);
        using (var ds = output.CreateDrawingSession())
        {
            // Background + shadow + content + border
            _bgCompositor.CompositeFrame(
                ds, croppedFrame, OutputWidth, OutputHeight, _config.Background);

            // Cursor overlay with position transformed to output space
            RenderCursorOverlay(ds, cursorPos, viewport, timeSeconds, cursorIndex);

            // Webcam overlay
            if (_webcamCompositor is not null && _webcamFrame is not null)
            {
                _webcamCompositor.RenderWebcam(ds, _webcamFrame, OutputWidth, OutputHeight);
            }

            // Keyboard shortcut overlay
            if (_keyboardRenderer is not null && _keyboardEvents is not null)
            {
                _keyboardRenderer.RenderKeyOverlay(
                    ds, _keyboardEvents, timeSeconds, _tickFrequency,
                    OutputWidth, OutputHeight, _videoStartTick);
            }

            // Subtitle overlay
            _subtitleBurner?.RenderSubtitle(ds, timeSeconds, OutputWidth, OutputHeight);
        }

        return output;
    }

    #region Aspect Ratio

    private void ComputeContentDimensions()
    {
        float targetRatio = GetAspectRatioValue(_config.AspectRatio);

        if (targetRatio <= 0f)
        {
            // Auto — content matches source
            _contentWidth = _sourceWidth;
            _contentHeight = _sourceHeight;
            return;
        }

        float sourceRatio = (float)_sourceWidth / _sourceHeight;

        if (sourceRatio > targetRatio)
        {
            // Source is wider than target — crop width
            _contentHeight = _sourceHeight;
            _contentWidth = (int)Math.Round(_sourceHeight * (double)targetRatio);
        }
        else
        {
            // Source is taller than target — crop height
            _contentWidth = _sourceWidth;
            _contentHeight = (int)Math.Round(_sourceWidth / (double)targetRatio);
        }
    }

    private static float GetAspectRatioValue(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Landscape16x9 => 16f / 9f,
        AspectRatio.Portrait9x16 => 9f / 16f,
        AspectRatio.Square1x1 => 1f,
        AspectRatio.Classic4x3 => 4f / 3f,
        AspectRatio.Tall3x4 => 3f / 4f,
        _ => -1f, // Auto — no constraint
    };

    /// <summary>
    /// Adjusts the zoom viewport to match the content aspect ratio by center-cropping
    /// within the viewport rectangle, then clamps to source bounds.
    /// </summary>
    private Rect ComputeEffectiveViewport(ZoomState zoomState)
    {
        float vpX = zoomState.ViewportX;
        float vpY = zoomState.ViewportY;
        float vpW = zoomState.ViewportWidth;
        float vpH = zoomState.ViewportHeight;

        if (_config.AspectRatio == AspectRatio.Auto)
            return new Rect(vpX, vpY, vpW, vpH);

        float contentRatio = (float)_contentWidth / _contentHeight;
        float vpRatio = vpW / vpH;

        float newW, newH;
        if (vpRatio > contentRatio)
        {
            // Viewport wider than target — narrow horizontally
            newH = vpH;
            newW = vpH * contentRatio;
        }
        else
        {
            // Viewport taller than target — shorten vertically
            newW = vpW;
            newH = vpW / contentRatio;
        }

        float newX = vpX + (vpW - newW) / 2f;
        float newY = vpY + (vpH - newH) / 2f;

        // Clamp to source bounds
        newX = Math.Clamp(newX, 0f, Math.Max(0f, _sourceWidth - newW));
        newY = Math.Clamp(newY, 0f, Math.Max(0f, _sourceHeight - newH));

        return new Rect(newX, newY, newW, newH);
    }

    #endregion

    #region Source Cropping

    /// <summary>
    /// Draws the viewport region of the source frame into a content-sized render target.
    /// </summary>
    private CanvasRenderTarget CropSourceFrame(CanvasBitmap source, Rect viewport)
    {
        var cropped = new CanvasRenderTarget(_device, _contentWidth, _contentHeight, 96);
        using var ds = cropped.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        ds.DrawImage(source, new Rect(0, 0, _contentWidth, _contentHeight), viewport);
        return cropped;
    }

    #endregion

    #region Cursor

    private void RenderCursorOverlay(
        CanvasDrawingSession session,
        SmoothedPosition cursorPos,
        Rect viewport,
        double timeSeconds,
        int frameIndex)
    {
        float padding = _config.Background.Padding;

        // Scale factors from source-viewport space → content space
        float scaleX = (float)(_contentWidth / viewport.Width);
        float scaleY = (float)(_contentHeight / viewport.Height);

        // Transform cursor from source coords to output coords
        var transformedPos = new SmoothedPosition
        {
            X = (cursorPos.X - viewport.X) * scaleX + padding,
            Y = (cursorPos.Y - viewport.Y) * scaleY + padding,
            TimestampSeconds = cursorPos.TimestampSeconds,
            VelocityX = cursorPos.VelocityX * scaleX,
            VelocityY = cursorPos.VelocityY * scaleY,
        };

        // Collect temporally-relevant clicks with transformed positions
        var activeClicks = GetActiveClicks(timeSeconds, viewport, scaleX, scaleY, padding);

        double lastMoveTime = _lastMoveTimes[frameIndex];

        _cursorRenderer.RenderFrame(session, transformedPos, activeClicks, timeSeconds, lastMoveTime);
    }

    /// <summary>
    /// Returns click events within ±1 second of the current time, with positions
    /// transformed from source coordinates to output coordinates.
    /// </summary>
    private List<ClickEvent> GetActiveClicks(
        double timeSeconds, Rect viewport,
        float scaleX, float scaleY, float padding)
    {
        if (_mouseData is null) return [];

        const double windowSeconds = 1.0;
        var result = new List<ClickEvent>();

        long startTick = _mouseData.StartTimestampTicks;
        double tickFreq = _mouseData.TickFrequency;

        foreach (var click in _mouseData.Clicks)
        {
            // Convert click timestamp to video time:
            // 1. Subtract mouse start to get relative time
            // 2. Subtract mouse→video offset to align timelines
            // 3. Add capture latency so click animation matches when the
            //    screen visually updates (click effect appears a few frames late)
            double clickTime = (click.TimestampTicks - startTick) / tickFreq
                - _mouseTimeOffset;

            if (Math.Abs(clickTime - timeSeconds) > windowSeconds)
                continue;

            // Transform click position from logical to physical, then to output space
            int cx = (int)((click.X * _coordScaleX - viewport.X) * scaleX + padding);
            int cy = (int)((click.Y * _coordScaleY - viewport.Y) * scaleY + padding);

            // Create adjusted click event with shifted timestamp for the renderer
            long adjustedTicks = click.TimestampTicks
                - (long)(_mouseTimeOffset * tickFreq);

            result.Add(new ClickEvent(adjustedTicks, cx, cy, click.Button, click.IsDown));
        }

        return result;
    }

    /// <summary>
    /// Precomputes the last time the cursor was moving for each frame, used by
    /// the cursor renderer's auto-hide logic.
    /// </summary>
    private void PrecomputeLastMoveTimes()
    {
        const double velocityThreshold = 5.0; // px/s minimum to count as "moving"
        _lastMoveTimes = new double[_smoothedPositions.Count];

        double lastMove = 0;
        for (int i = 0; i < _smoothedPositions.Count; i++)
        {
            var pos = _smoothedPositions[i];
            double speed = Math.Sqrt(
                pos.VelocityX * pos.VelocityX + pos.VelocityY * pos.VelocityY);

            // Use video-relative time so auto-hide is consistent with
            // the video timebase used for all other animations.
            double videoTime = (double)(i - _videoFrameOffset) / _config.OutputFps;

            if (speed > velocityThreshold)
                lastMove = videoTime;

            _lastMoveTimes[i] = lastMove;
        }
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _smoothedPositions = [];
            _lastMoveTimes = [];
            _mouseData = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
