using System.Runtime.InteropServices;
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
    public SmoothingStrength SmoothingStrength { get; init; } = SmoothingStrength.UltraSmooth;
    public int OutputFps { get; init; } = 30;
    public AspectRatio AspectRatio { get; init; } = AspectRatio.Auto;
    public FitMode FitMode { get; init; } = FitMode.Cover;
    public double CropAnchorX { get; init; } = 0.5;
    public double CropAnchorY { get; init; } = 0.5;
    public ZoomScope ZoomScope { get; init; } = ZoomScope.Frame;
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
    private volatile bool _deviceLost;

    private const long MaxEstimatedRenderTargetBytes = 1_610_612_736L;

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
    // Output canvas dims (target aspect ratio, padding-independent).
    private int _contentWidth;
    private int _contentHeight;
    // The actual source frame rect inside the output canvas. The offsets equal
    // user-padding plus any letterbox/pillarbox gap when the source aspect ratio
    // doesn't match the canvas (Contain mode). Everything outside this rect is
    // background — there is no separate "inner content" container.
    private int _sourceAreaWidth;
    private int _sourceAreaHeight;
    private int _sourceAreaOffsetX;
    private int _sourceAreaOffsetY;
    private bool _initialized;
    private bool _disposed;
    private float _coordScaleX = 1.0f;
    private float _coordScaleY = 1.0f;
    private float _cropOffsetX;
    private float _cropOffsetY;
    private double _mouseTimeOffset;

    // Reusable scratch buffer for CropSourceFrame to avoid per-frame GPU allocation
    private CanvasRenderTarget? _croppedBuffer;

    // Reusable buffer for post-composite zoom (used when padding > 0)
    private CanvasRenderTarget? _compositeBuffer;

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
        _device.DeviceLost += OnCanvasDeviceLost;
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
        double mouseToVideoOffsetSeconds = 0,
        int cropOffsetX = 0,
        int cropOffsetY = 0,
        float dpiScale = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfDeviceLost();
        ArgumentNullException.ThrowIfNull(mouseData);
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        _mouseData = mouseData;

        // Ensure clicks are sorted by timestamp for binary search in GetActiveClicks
        _mouseData.Clicks.Sort((a, b) => a.TimestampTicks.CompareTo(b.TimestampTicks));

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _tickFrequency = mouseData.TickFrequency;

        // Mouse hook (WH_MOUSE_LL) in a PerMonitorV2 process reports physical
        // screen coordinates, and crop offsets are already physical pixels.
        // No DPI scaling is needed — both are in the same coordinate space.
        float coordScaleX = 1.0f;
        float coordScaleY = 1.0f;
        _coordScaleX = coordScaleX;
        _coordScaleY = coordScaleY;
        _cropOffsetX = cropOffsetX;
        _cropOffsetY = cropOffsetY;

        // Compute content dimensions based on aspect ratio (center-crop)
        ComputeContentDimensions();

        // Output = content + padding on all sides
        var (outW, outH) = _bgCompositor.CalculateOutputSize(
            _contentWidth, _contentHeight, _config.Background);
        OutputWidth = outW;
        OutputHeight = outH;
        PreflightRenderTargetMemory();

        // Smooth cursor path at the target FPS, subtract crop offset for
        // region recordings, and apply time offset. Mouse hook coordinates
        // are already in physical pixels (PerMonitorV2), matching the capture frame space.
        _smoothedPositions = _smoother.SmoothPath(mouseData, _config.OutputFps);
        for (int i = 0; i < _smoothedPositions.Count; i++)
        {
            var p = _smoothedPositions[i];
            _smoothedPositions[i] = new SmoothedPosition
            {
                X = p.X * coordScaleX - cropOffsetX,
                Y = p.Y * coordScaleY - cropOffsetY,
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
            coordScaleX, coordScaleY, mouseToVideoOffsetSeconds,
            cropOffsetX, cropOffsetY);

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
    /// Updates the webcam overlay style (position, size) without rebuilding the compositor.
    /// </summary>
    public void UpdateWebcamStyle(WebcamOverlayStyle style)
    {
        if (_webcamCompositor is not null)
            _webcamCompositor.UpdateStyle(style);
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
        ThrowIfDeviceLost();
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

        // Choose zoom path based on user-selected ZoomScope. Frame zoom (default)
        // operates on the entire composed canvas; Source zoom keeps the
        // background/letterbox/padding constant while zooming only the source.
        if (zoomState.ZoomLevel > 1.01f && _config.ZoomScope == ZoomScope.Frame)
            return ComposeFramePostCompositeZoom(
                sourceFrame, zoomState, cursorPos, cursorIndex, timeSeconds);

        // No padding or no zoom — direct composition (fast path)
        return ComposeFrameDirect(
            sourceFrame, zoomState, cursorPos, cursorIndex, timeSeconds);
    }

    /// <summary>
    /// Direct composition fast path: zoom crops the source frame directly,
    /// and any padding remains at a constant size. Used whenever
    /// post-composite zoom is not needed, including when padding = 0 or
    /// when padding &gt; 0 and zoom is inactive (for example,
    /// <c>zoomState.ZoomLevel &lt;= 1.01f</c>).
    /// </summary>
    private CanvasRenderTarget ComposeFrameDirect(
        CanvasBitmap sourceFrame,
        ZoomState zoomState,
        SmoothedPosition cursorPos,
        int cursorIndex,
        double timeSeconds)
    {
        // Adjust viewport for target aspect ratio (center-crop within viewport)
        var viewport = ComputeEffectiveViewport(zoomState);

        // Crop source frame to the effective viewport, scaled to content dimensions
        var croppedFrame = CropSourceFrame(sourceFrame, viewport);

        // Create output render target
        var output = CreateRenderTarget(OutputWidth, OutputHeight, "direct composition output");
        using (var ds = output.CreateDrawingSession())
        {
            // Background + shadow + content + border
            _bgCompositor.CompositeFrame(
                ds, croppedFrame, OutputWidth, OutputHeight,
                _sourceAreaOffsetX, _sourceAreaOffsetY, _sourceAreaWidth, _sourceAreaHeight,
                _config.Background);

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

    /// <summary>
    /// Post-composite zoom path: compose everything at 1x into a buffer, then
    /// crop+scale the buffer according to the zoom state. This makes the zoom
    /// operate on the entire framed output (content + padding), so the padding
    /// scales proportionally and the device-frame illusion is maintained.
    /// Webcam, keyboard, and subtitle overlays are rendered after the zoom so
    /// they remain fixed on screen.
    /// </summary>
    private CanvasRenderTarget ComposeFramePostCompositeZoom(
        CanvasBitmap sourceFrame,
        ZoomState zoomState,
        SmoothedPosition cursorPos,
        int cursorIndex,
        double timeSeconds)
    {
        // 1. Compute 1x viewport (no zoom, aspect-ratio adjusted)
        var noZoomState = _zoomEngine.ComputeViewportForCenter(
            1.0f, _sourceWidth / 2f, _sourceHeight / 2f);
        var viewport1x = ComputeEffectiveViewport(noZoomState);

        // 2. Crop source at 1x
        var croppedFrame = CropSourceFrame(sourceFrame, viewport1x);

        // 3. Render 1x composite: background + content + cursor
        EnsureCompositeBuffer();
        using (var ds = _compositeBuffer!.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _bgCompositor.CompositeFrame(
                ds, croppedFrame, OutputWidth, OutputHeight,
                _sourceAreaOffsetX, _sourceAreaOffsetY, _sourceAreaWidth, _sourceAreaHeight,
                _config.Background);
            RenderCursorOverlay(ds, cursorPos, viewport1x, timeSeconds, cursorIndex);
        }

        // 4. Compute zoom viewport in composite space. _sourceAreaOffsetX/Y already
        //    includes user-padding plus any AR-fit gap, so no separate +padding.
        float cx_comp = (float)((zoomState.CenterX - viewport1x.X)
            * _sourceAreaWidth / viewport1x.Width + _sourceAreaOffsetX);
        float cy_comp = (float)((zoomState.CenterY - viewport1x.Y)
            * _sourceAreaHeight / viewport1x.Height + _sourceAreaOffsetY);
        float cropW = OutputWidth / zoomState.ZoomLevel;
        float cropH = OutputHeight / zoomState.ZoomLevel;
        float cropX = Math.Clamp(
            cx_comp - cropW / 2f, 0f, Math.Max(0f, OutputWidth - cropW));
        float cropY = Math.Clamp(
            cy_comp - cropH / 2f, 0f, Math.Max(0f, OutputHeight - cropH));

        // 5. Draw zoomed composite + fixed overlays
        var output = CreateRenderTarget(OutputWidth, OutputHeight, "post-composite zoom output");
        using (var ds = output.CreateDrawingSession())
        {
            // Use high-quality interpolation only when zoomed; linear is cheaper at 1:1
            var interpolation = (cropW < OutputWidth * 0.95f || cropH < OutputHeight * 0.95f)
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;
            ds.DrawImage(_compositeBuffer,
                new Rect(0, 0, OutputWidth, OutputHeight),
                new Rect(cropX, cropY, cropW, cropH),
                1f, interpolation);

            // Webcam overlay (fixed position, not zoomed)
            if (_webcamCompositor is not null && _webcamFrame is not null)
            {
                _webcamCompositor.RenderWebcam(ds, _webcamFrame, OutputWidth, OutputHeight);
            }

            // Keyboard shortcut overlay (fixed position, not zoomed)
            if (_keyboardRenderer is not null && _keyboardEvents is not null)
            {
                _keyboardRenderer.RenderKeyOverlay(
                    ds, _keyboardEvents, timeSeconds, _tickFrequency,
                    OutputWidth, OutputHeight, _videoStartTick);
            }

            // Subtitle overlay (fixed position, not zoomed)
            _subtitleBurner?.RenderSubtitle(ds, timeSeconds, OutputWidth, OutputHeight);
        }

        return output;
    }

    /// <summary>
    /// Ensures the composite buffer for post-composite zoom is allocated
    /// at the current output dimensions.
    /// </summary>
    private void EnsureCompositeBuffer()
    {
        if (_compositeBuffer is null
            || _compositeBuffer.SizeInPixels.Width != (uint)OutputWidth
            || _compositeBuffer.SizeInPixels.Height != (uint)OutputHeight)
        {
            _compositeBuffer?.Dispose();
            _compositeBuffer = null;
            _compositeBuffer = CreateRenderTarget(OutputWidth, OutputHeight, "post-composite zoom buffer");
        }
    }

    private void PreflightRenderTargetMemory()
    {
        long estimatedBytes = EstimateBgraBytes(OutputWidth, OutputHeight, 2)
            + EstimateBgraBytes(_sourceAreaWidth, _sourceAreaHeight, 1);
        if (estimatedBytes > MaxEstimatedRenderTargetBytes)
            throw new InvalidOperationException(FormatRenderTargetMemoryLimitMessage(estimatedBytes));
    }

    private CanvasRenderTarget CreateRenderTarget(int width, int height, string purpose)
    {
        ThrowIfDeviceLost();
        try
        {
            return new CanvasRenderTarget(_device, width, height, 96);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or COMException)
        {
            ReleaseCachedRenderTargets();
            throw new InvalidOperationException(
                $"Failed to allocate {purpose} render target ({width}x{height}). " +
                "Reduce export resolution or close other GPU-heavy applications.", ex);
        }
    }

    private void ReleaseCachedRenderTargets()
    {
        _croppedBuffer?.Dispose();
        _croppedBuffer = null;
        _compositeBuffer?.Dispose();
        _compositeBuffer = null;
    }

    private void OnCanvasDeviceLost(CanvasDevice sender, object args)
    {
        _deviceLost = true;
    }

    private void ThrowIfDeviceLost()
    {
        if (_deviceLost)
            throw new RecoverableDeviceLostException(
                "The graphics device was lost while compositing frames. Retry the export after closing other GPU-heavy applications.");
    }

    private static long EstimateBgraBytes(int width, int height, int surfaceCount)
    {
        return (long)width * height * 4 * surfaceCount;
    }

    private static string FormatRenderTargetMemoryLimitMessage(long estimatedBytes)
    {
        long mb = estimatedBytes / (1024 * 1024);
        long maxMb = MaxEstimatedRenderTargetBytes / (1024 * 1024);
        return $"Estimated render target memory ({mb} MB) exceeds safe limit ({maxMb} MB). Reduce export resolution or close other GPU-heavy applications.";
    }

    #region Aspect Ratio

    private void ComputeContentDimensions()
    {
        float targetRatio = GetAspectRatioValue(_config.AspectRatio);

        // Step 1: compute the output canvas at the target aspect ratio, sized to fit
        // within the source bounds. Independent of padding.
        if (targetRatio <= 0f)
        {
            _contentWidth = _sourceWidth;
            _contentHeight = _sourceHeight;
        }
        else
        {
            float sourceRatio = (float)_sourceWidth / _sourceHeight;
            if (sourceRatio > targetRatio)
            {
                _contentHeight = _sourceHeight;
                _contentWidth = (int)Math.Round(_sourceHeight * (double)targetRatio);
            }
            else
            {
                _contentWidth = _sourceWidth;
                _contentHeight = (int)Math.Round(_sourceWidth / (double)targetRatio);
            }
        }

        // Step 2: the user-padding setting reserves at least that many pixels of
        // background on each side. The remaining "max content box" is where the
        // source frame may live.
        int padding = Math.Max(0, _config.Background.Padding);
        int maxContentW = Math.Max(1, _contentWidth - 2 * padding);
        int maxContentH = Math.Max(1, _contentHeight - 2 * padding);

        // Step 3: size the actual source frame within the max content box per FitMode,
        // preserving the AR of the content being drawn. Subtracting padding equally from
        // width and height of an already-AR-matched canvas would otherwise stretch the
        // source non-uniformly (e.g. 1920x1080 minus 48px padding = 1824x984, ratio 1.85
        // instead of 1.78). Any leftover gap on opposing sides simply becomes more
        // background — there is no separate letterbox/pillarbox container.
        float effectiveAr;
        if (_config.FitMode == FitMode.Cover && targetRatio > 0f)
        {
            // Cover: the upstream viewport was cropped to the target AR, so preserve targetAr.
            effectiveAr = targetRatio;
        }
        else
        {
            // Auto or Contain: the source frame keeps its native AR.
            effectiveAr = (float)_sourceWidth / _sourceHeight;
        }

        float boxAr = (float)maxContentW / maxContentH;
        if (effectiveAr > boxAr)
        {
            _sourceAreaWidth = maxContentW;
            _sourceAreaHeight = (int)Math.Round(maxContentW / (double)effectiveAr);
        }
        else
        {
            _sourceAreaHeight = maxContentH;
            _sourceAreaWidth = (int)Math.Round(maxContentH * (double)effectiveAr);
        }

        // Guard against zero/negative sizes from extreme ratios + tiny canvases
        // (e.g. maxContentW=1 with effectiveAr>1 rounds to 0). A 0-pixel render
        // target throws when allocated downstream.
        if (_sourceAreaWidth < 1) _sourceAreaWidth = 1;
        if (_sourceAreaHeight < 1) _sourceAreaHeight = 1;

        // Step 4: center the source frame within the canvas. The offset is the total
        // background gap on the left/top — user-padding plus any AR-fit gap.
        _sourceAreaOffsetX = (_contentWidth - _sourceAreaWidth) / 2;
        _sourceAreaOffsetY = (_contentHeight - _sourceAreaHeight) / 2;
    }

    private static float GetAspectRatioValue(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Landscape16x9 => 16f / 9f,
        AspectRatio.Portrait9x16 => 9f / 16f,
        AspectRatio.Square1x1 => 1f,
        AspectRatio.Classic4x3 => 4f / 3f,
        AspectRatio.Tall3x4 => 3f / 4f,
        AspectRatio.Cinematic21x9 => 21f / 9f,
        AspectRatio.Instagram4x5 => 4f / 5f,
        _ => -1f, // Auto — no constraint
    };

    /// <summary>
    /// Adjusts the zoom viewport to match the target aspect ratio.
    /// For <see cref="FitMode.Cover"/> the viewport is narrowed to the target ratio
    /// using <see cref="CompositionConfig.CropAnchorX"/>/<see cref="CompositionConfig.CropAnchorY"/>.
    /// For <see cref="FitMode.Contain"/> the viewport keeps the source aspect ratio
    /// (it represents the source region drawn into the letterboxed sub-rect).
    /// </summary>
    private Rect ComputeEffectiveViewport(ZoomState zoomState)
    {
        float vpX = zoomState.ViewportX;
        float vpY = zoomState.ViewportY;
        float vpW = zoomState.ViewportWidth;
        float vpH = zoomState.ViewportHeight;

        if (_config.AspectRatio == AspectRatio.Auto || _config.FitMode == FitMode.Contain)
            return new Rect(vpX, vpY, vpW, vpH);

        // Cover: narrow the zoom viewport to the target ratio using the configured anchor.
        float targetRatio = GetAspectRatioValue(_config.AspectRatio);
        float vpRatio = vpW / vpH;

        float newW, newH;
        if (vpRatio > targetRatio)
        {
            // Viewport wider than target — narrow horizontally
            newH = vpH;
            newW = vpH * targetRatio;
        }
        else
        {
            // Viewport taller than target — shorten vertically
            newW = vpW;
            newH = vpW / targetRatio;
        }

        float anchorX = (float)Math.Clamp(_config.CropAnchorX, 0.0, 1.0);
        float anchorY = (float)Math.Clamp(_config.CropAnchorY, 0.0, 1.0);

        float newX = vpX + (vpW - newW) * anchorX;
        float newY = vpY + (vpH - newH) * anchorY;

        // Clamp to source bounds
        newX = Math.Clamp(newX, 0f, Math.Max(0f, _sourceWidth - newW));
        newY = Math.Clamp(newY, 0f, Math.Max(0f, _sourceHeight - newH));

        return new Rect(newX, newY, newW, newH);
    }

    #endregion

    #region Source Cropping

    /// <summary>
    /// Draws the viewport region of the source frame into a reusable buffer sized
    /// exactly to the source-area rect. The buffer contains only the visible source
    /// pixels — no letterbox/padding container — and is drawn at the source-area
    /// position by the background compositor.
    /// The returned target is owned by the compositor — callers must NOT dispose it.
    /// </summary>
    private CanvasRenderTarget CropSourceFrame(CanvasBitmap source, Rect viewport)
    {
        if (_croppedBuffer is null
            || _croppedBuffer.SizeInPixels.Width != (uint)_sourceAreaWidth
            || _croppedBuffer.SizeInPixels.Height != (uint)_sourceAreaHeight)
        {
            _croppedBuffer?.Dispose();
            _croppedBuffer = null;
            _croppedBuffer = CreateRenderTarget(_sourceAreaWidth, _sourceAreaHeight, "source crop buffer");
        }

        // Adaptive interpolation: HighQualityCubic is a 4-tap bicubic filter,
        // significantly more expensive than Linear. It only meaningfully improves
        // quality when actually resampling (zoom in/out or aspect-ratio crop with
        // non-unit scale). At ~1:1 the bicubic filter produces output essentially
        // identical to Linear, so use the cheaper path. Use the same explicit
        // near-unit threshold form as ComposeFramePostCompositeZoom so the two
        // paths stay aligned over time.
        double scaleX = _sourceAreaWidth / viewport.Width;
        double scaleY = _sourceAreaHeight / viewport.Height;
        const double nearUnitScaleMinimum = 0.95;
        const double nearUnitScaleMaximum = 1.05;
        bool nearUnitScale =
            scaleX >= nearUnitScaleMinimum && scaleX <= nearUnitScaleMaximum &&
            scaleY >= nearUnitScaleMinimum && scaleY <= nearUnitScaleMaximum;
        var interpolation = nearUnitScale
            ? CanvasImageInterpolation.Linear
            : CanvasImageInterpolation.HighQualityCubic;

        using var ds = _croppedBuffer.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        ds.DrawImage(source,
            new Rect(0, 0, _sourceAreaWidth, _sourceAreaHeight),
            viewport,
            1f, interpolation);
        return _croppedBuffer;
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
        // Scale factors from source-viewport space → output (canvas) space.
        // Source area is positioned at (_sourceAreaOffsetX, _sourceAreaOffsetY) in the
        // canvas — that offset already includes user-padding plus any AR-fit gap.
        float scaleX = (float)(_sourceAreaWidth / viewport.Width);
        float scaleY = (float)(_sourceAreaHeight / viewport.Height);

        // Transform cursor from source coords to output coords
        var transformedPos = new SmoothedPosition
        {
            X = (cursorPos.X - viewport.X) * scaleX + _sourceAreaOffsetX,
            Y = (cursorPos.Y - viewport.Y) * scaleY + _sourceAreaOffsetY,
            TimestampSeconds = cursorPos.TimestampSeconds,
            VelocityX = cursorPos.VelocityX * scaleX,
            VelocityY = cursorPos.VelocityY * scaleY,
        };

        // Collect temporally-relevant clicks with transformed positions
        var activeClicks = GetActiveClicks(timeSeconds, viewport, scaleX, scaleY);

        double lastMoveTime = _lastMoveTimes[frameIndex];

        _cursorRenderer.RenderFrame(session, transformedPos, activeClicks, timeSeconds, lastMoveTime);
    }

    /// <summary>
    /// Returns click events within ±1.5 seconds of the current time, with positions
    /// transformed from source coordinates to output coordinates.
    /// Uses binary search for efficient lookup (clicks are sorted by TimestampTicks).
    /// The wider window (vs ±1s) ensures touch cursor chains can see upcoming clicks
    /// needed for smooth transitions between consecutive taps.
    /// </summary>
    private List<ClickEvent> GetActiveClicks(
        double timeSeconds, Rect viewport,
        float scaleX, float scaleY)
    {
        if (_mouseData is null) return [];

        const double windowSeconds = 1.5;
        var result = new List<ClickEvent>();
        var clicks = _mouseData.Clicks;
        if (clicks.Count == 0) return result;

        long startTick = _mouseData.StartTimestampTicks;
        double tickFreq = _mouseData.TickFrequency;

        // Convert time window to tick space for binary search
        double windowStartTime = timeSeconds - windowSeconds + _mouseTimeOffset;
        double windowEndTime = timeSeconds + windowSeconds + _mouseTimeOffset;
        long windowStartTicks = startTick + (long)(windowStartTime * tickFreq);
        long windowEndTicks = startTick + (long)(windowEndTime * tickFreq);

        // Binary search for the first click in the window
        int lo = 0, hi = clicks.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (clicks[mid].TimestampTicks < windowStartTicks)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Iterate only the clicks within the time window
        for (int i = lo; i < clicks.Count; i++)
        {
            var click = clicks[i];
            if (click.TimestampTicks > windowEndTicks)
                break;

            // Transform click position from logical to physical, subtract crop offset, then to output space.
            // _sourceAreaOffsetX/Y already includes user-padding plus any AR-fit gap.
            int cx = (int)((click.X * _coordScaleX - _cropOffsetX - viewport.X) * scaleX + _sourceAreaOffsetX);
            int cy = (int)((click.Y * _coordScaleY - _cropOffsetY - viewport.Y) * scaleY + _sourceAreaOffsetY);

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
            _device.DeviceLost -= OnCanvasDeviceLost;
            ReleaseCachedRenderTargets();
            _bgCompositor.Dispose();
            _cursorRenderer.Dispose();
            _webcamCompositor?.Dispose();
            _smoothedPositions = [];
            _lastMoveTimes = [];
            _mouseData = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class RecoverableDeviceLostException : Exception
{
    public RecoverableDeviceLostException(string message)
        : base(message)
    {
    }
}
