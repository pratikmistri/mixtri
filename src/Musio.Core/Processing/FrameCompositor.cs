using System.Numerics;
using Microsoft.Graphics.Canvas;
using Musio.Core.AI;
using Musio.Core.Capture;
using Musio.Core.Diagnostics;
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

    /// <summary>Shutter-based camera motion blur for cursor, zoom, and pan movement.</summary>
    public MotionBlurSettings MotionBlur { get; init; } = new();

    /// <summary>Continuous subtle zoom/pan motion applied while a zoom segment is active.</summary>
    public CameraDriftSettings CameraDrift { get; init; } = new();

    public SmoothingAlgorithm SmoothingAlgorithm { get; init; } = SmoothingAlgorithm.ZeroPhaseSpring;
    public SmoothingStrength SmoothingStrength { get; init; } = SmoothingStrength.Smooth;
    public int OutputFps { get; init; } = 30;
    public AspectRatio AspectRatio { get; init; } = AspectRatio.Auto;
    public FitMode FitMode { get; init; } = FitMode.Contain;
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
    private readonly DeviceLostGuard _deviceLostGuard;

    private const long MaxEstimatedRenderTargetBytes = 1_610_612_736L;

    // Optional overlay renderers
    private WebcamCompositor? _webcamCompositor;
    private KeyboardOverlayRenderer? _keyboardRenderer;
    private SubtitleBurner? _subtitleBurner;
    private List<KeyPressEvent>? _keyboardEvents;
    private double _tickFrequency;

    // Text overlays authored against this compositor's source recording (see
    // SyncTextOverlays). The renderer is created lazily — only once a non-empty
    // list is synced — so projects with no text overlays never pay for it.
    private TextOverlayRenderer? _textOverlayRenderer;
    private IReadOnlyList<TextOverlaySegment> _textOverlays = [];

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
    private readonly GrowOnlyBuffer _croppedBufferHolder = new();
    private CanvasRenderTarget? _croppedBuffer => _croppedBufferHolder.Current;

    // Reusable buffer for post-composite zoom (used when padding > 0)
    private readonly GrowOnlyBuffer _compositeBufferHolder = new();
    private CanvasRenderTarget? _compositeBuffer => _compositeBufferHolder.Current;

    // Tick value corresponding to video time 0, for rebasing keyboard events.
    private long _videoStartTick;

    // Offset between mouse-frame indices and video-frame indices.
    // smoothedPositions[videoFrame + _videoFrameOffset] gives the cursor
    // position at video time = videoFrame / outputFps.
    private int _videoFrameOffset;

    public int TotalFrames { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>
    /// Composes every frame at rest (1x, no camera drift) while leaving the rest of the
    /// composition — background, padding, aspect-ratio fit, cursor, overlays — exactly as
    /// it would be rendered.
    /// <para>
    /// The zoom-region picker needs both halves of that: the frame has to look like the
    /// finished render (framing a region against raw capture pixels is what made the picker
    /// and the preview disagree), but the zoom being edited must not fight the rectangle
    /// being dragged. Applied inside <see cref="ResolveZoomState"/>, so every consumer of
    /// the zoom state — crop, post-composite path, motion blur, camera velocity — sees the
    /// same rest state for a given instant.
    /// </para>
    /// </summary>
    public bool SuppressZoom { get; set; }

    /// <summary>
    /// The rect the source frame occupies inside the composed output, in OUTPUT pixels.
    /// Everything outside it is background (user padding plus any aspect-ratio-fit gap) —
    /// there is no separate letterbox container. See <see cref="ComputeContentDimensions"/>.
    /// </summary>
    public Rect SourceAreaRect => _initialized
        ? new Rect(_sourceAreaOffsetX, _sourceAreaOffsetY, _sourceAreaWidth, _sourceAreaHeight)
        : default;

    /// <summary>
    /// The region of the SOURCE frame that is visible in a frame composed at rest, in source
    /// pixels. Equals the whole source frame except in <see cref="FitMode.Cover"/>, where the
    /// viewport is cropped to the target aspect ratio around the configured anchor.
    /// </summary>
    public Rect RestSourceViewport => _initialized ? ComputeEffectiveViewport(RestZoomState()) : default;

    /// <summary>
    /// The region of the SOURCE frame a zoom of <paramref name="zoomLevel"/> centred on
    /// (<paramref name="centerXNormalized"/>, <paramref name="centerYNormalized"/>) would
    /// show, in source pixels — clamping and aspect-ratio-fit crop included.
    /// <para>
    /// Exists so the zoom-region picker can draw the rectangle the compositor will actually
    /// render rather than re-deriving that geometry beside it, which is how the two drifted
    /// apart before.
    /// </para>
    /// </summary>
    public Rect ComputeRegionViewport(float zoomLevel, float centerXNormalized, float centerYNormalized)
    {
        if (!_initialized) return default;

        var state = _zoomEngine.ComputeViewportForCenter(
            zoomLevel,
            centerXNormalized * _sourceWidth,
            centerYNormalized * _sourceHeight);
        return ComputeEffectiveViewport(state);
    }

    /// <summary>
    /// The area of the composed output a zoom region is chosen WITHIN, in output pixels:
    /// the whole canvas under <see cref="ZoomScope.Frame"/> (which magnifies background and
    /// padding along with the source), and just the source area under
    /// <see cref="ZoomScope.Source"/> (which leaves that chrome at a fixed size).
    /// <para>
    /// The picker dims everything in here that falls outside the region, so getting the scope
    /// wrong would tell the user their background is about to be cropped when it is not.
    /// </para>
    /// </summary>
    public Rect RegionCanvasRect
    {
        get
        {
            if (!_initialized) return default;
            return _config.ZoomScope == ZoomScope.Frame
                ? new Rect(0, 0, OutputWidth, OutputHeight)
                : SourceAreaRect;
        }
    }

    /// <summary>
    /// The region of the composed OUTPUT frame that a zoom of <paramref name="zoomLevel"/>
    /// centred on (<paramref name="centerXNormalized"/>, <paramref name="centerYNormalized"/>)
    /// will end up showing, in output pixels.
    /// <para>
    /// Scope-aware, because the two scopes crop different things:
    /// <see cref="ZoomScope.Frame"/> (the default) magnifies the whole canvas, so its visible
    /// region includes the background/padding around the source area, while
    /// <see cref="ZoomScope.Source"/> keeps that chrome fixed and crops only the source.
    /// The zoom-region picker draws this rect, so what it frames is what gets rendered.
    /// </para>
    /// </summary>
    public Rect ComputeRegionOutputRect(float zoomLevel, float centerXNormalized, float centerYNormalized)
    {
        if (!_initialized) return default;

        var state = _zoomEngine.ComputeViewportForCenter(
            Math.Max(1f, zoomLevel),
            centerXNormalized * _sourceWidth,
            centerYNormalized * _sourceHeight);
        var viewport1x = ComputeEffectiveViewport(RestZoomState());

        if (_config.ZoomScope == ZoomScope.Frame)
        {
            // At rest the entire canvas renders, background included — reporting only the
            // source area here would tell the picker the padding gets cropped when it does not.
            return state.ZoomLevel > 1.01f
                ? ComputeCompositeCropRect(state, viewport1x)
                : new Rect(0, 0, OutputWidth, OutputHeight);
        }

        // Source scope: the cropped source is redrawn into the (fixed) source area, so the
        // visible region is that crop mapped through the at-rest source→output transform.
        // Clipped to the source area because a Cover crop can put the requested region
        // outside the pixels the at-rest frame shows, and extrapolating past the frame would
        // park the rectangle (and its handles) off screen.
        var vp = ComputeEffectiveViewport(state);
        var topLeft = MapSourcePointToOutput(new Vector2((float)vp.X, (float)vp.Y), viewport1x);
        double scaleX = _sourceAreaWidth / viewport1x.Width;
        double scaleY = _sourceAreaHeight / viewport1x.Height;
        return ClipToSourceArea(
            new Rect(topLeft.X, topLeft.Y, vp.Width * scaleX, vp.Height * scaleY));
    }

    private Rect ClipToSourceArea(Rect rect)
    {
        double left = Math.Max(rect.X, _sourceAreaOffsetX);
        double top = Math.Max(rect.Y, _sourceAreaOffsetY);
        double right = Math.Min(rect.X + rect.Width, _sourceAreaOffsetX + (double)_sourceAreaWidth);
        double bottom = Math.Min(rect.Y + rect.Height, _sourceAreaOffsetY + (double)_sourceAreaHeight);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The range a zoom's normalised centre can occupy at <paramref name="zoomLevel"/> before
    /// the compositor clamps the camera, in the same 0..1 source space as
    /// <see cref="Timeline.ZoomKeyframe.CenterX"/>/<c>CenterY</c>.
    /// <para>
    /// The intersection of TWO clamps, which is the whole subtlety here. Every centre first
    /// passes through <see cref="AutoZoomEngine.ComputeViewportForCenter"/>, which clamps the
    /// un-narrowed viewport into the source frame — so half a viewport in from each source edge
    /// is an outer bound no scope escapes. On top of that, <see cref="ZoomScope.Frame"/> crops
    /// in OUTPUT space, which under a Cover crop bites first, and <see cref="ZoomScope.Source"/>
    /// can only usefully frame pixels the at-rest composition actually shows.
    /// </para>
    /// <para>
    /// Taking either clamp alone leaves the picker with a dead zone: the rectangle stops while
    /// the pointer keeps travelling, because the value it is storing no longer changes what
    /// renders.
    /// </para>
    /// </summary>
    public (double MinX, double MaxX, double MinY, double MaxY) ComputeRegionCenterBounds(float zoomLevel)
    {
        if (!_initialized) return (0.0, 1.0, 0.0, 1.0);

        zoomLevel = Math.Max(1f, zoomLevel);
        var viewport1x = ComputeEffectiveViewport(RestZoomState());

        // The camera engine's own clamp: the un-narrowed viewport is srcW/zoom wide, so its
        // centre lives half of that in from each edge, whatever the scope or fit mode.
        double minX = 1.0 / (2 * zoomLevel), maxX = 1.0 - minX;
        double minY = minX, maxY = maxX;

        if (_config.ZoomScope == ZoomScope.Frame && zoomLevel > 1.01f)
        {
            double cropW = OutputWidth / (double)zoomLevel;
            double cropH = OutputHeight / (double)zoomLevel;

            minX = Math.Max(minX, OutputToNormalizedSourceX(cropW / 2));
            maxX = Math.Min(maxX, OutputToNormalizedSourceX(OutputWidth - cropW / 2));
            minY = Math.Max(minY, OutputToNormalizedSourceY(cropH / 2));
            maxY = Math.Min(maxY, OutputToNormalizedSourceY(OutputHeight - cropH / 2));
        }
        else
        {
            // Source scope keeps the region inside the pixels the at-rest frame shows, which
            // under a Cover crop is narrower than the source frame.
            var vp = ComputeEffectiveViewport(
                _zoomEngine.ComputeViewportForCenter(zoomLevel, _sourceWidth / 2f, _sourceHeight / 2f));

            minX = Math.Max(minX, (viewport1x.X + vp.Width / 2) / _sourceWidth);
            maxX = Math.Min(maxX, (viewport1x.X + viewport1x.Width - vp.Width / 2) / _sourceWidth);
            minY = Math.Max(minY, (viewport1x.Y + vp.Height / 2) / _sourceHeight);
            maxY = Math.Min(maxY, (viewport1x.Y + viewport1x.Height - vp.Height / 2) / _sourceHeight);
        }

        // An extreme ratio can leave no room at all; collapse to the one reachable point
        // rather than handing back an inverted range that Math.Clamp would throw on.
        if (maxX < minX) minX = maxX = (minX + maxX) / 2;
        if (maxY < minY) minY = maxY = (minY + maxY) / 2;

        return (minX, maxX, minY, maxY);

        double OutputToNormalizedSourceX(double outputX) =>
            ((outputX - _sourceAreaOffsetX) * viewport1x.Width / _sourceAreaWidth + viewport1x.X) / _sourceWidth;

        double OutputToNormalizedSourceY(double outputY) =>
            ((outputY - _sourceAreaOffsetY) * viewport1x.Height / _sourceAreaHeight + viewport1x.Y) / _sourceHeight;
    }

    /// <summary>The un-zoomed (1x, whole-frame) camera state.</summary>
    private ZoomState RestZoomState() =>
        _zoomEngine.ComputeViewportForCenter(1f, _sourceWidth / 2f, _sourceHeight / 2f);

    public FrameCompositor(CompositionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _device = GpuContext.GetSharedDevice();
        _bgCompositor = new BackgroundCompositor();
        _deviceLostGuard = new DeviceLostGuard(
            _device,
            "The graphics device was lost while compositing frames. Retry the export after closing other GPU-heavy applications.",
            // Cached GPU resources belong to the lost device — drop them so a rebuilt
            // compositor reloads the wallpaper on the new device instead of drawing
            // (or disposing) a dead surface.
            onLost: () => _bgCompositor.InvalidateImageCache());
        _cursorRenderer = new CursorRenderer(config.Cursor);
        _zoomEngine = new AutoZoomEngine(config.Zoom);
        _smoother = new CursorSmoother
        {
            Algorithm = config.SmoothingAlgorithm,
            Strength = config.SmoothingStrength,
            // De-stutter is disabled: its arc-length re-timing pre-pass flattens the
            // cursor's velocity profile, dropping peak speed to ~17-20% of real speed
            // (measured) regardless of ease strength — which makes short, quick moves play
            // in slow motion. The Spring filter's low-pass already smooths trackpad
            // micro-stalls without that speed penalty.
            DestutterEnabled = false,
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
                Shape = p.Shape,
            };
        }

        // Store offset for click timestamp alignment
        _mouseTimeOffset = mouseToVideoOffsetSeconds;

        // Compute the index offset between mouse frames and video frames.
        // Mouse frame 0 = mouse start; video frame 0 = video start.
        // Video frame N needs the cursor at mouse time (N/fps + offset),
        // which is mouse frame (N + offset*fps).
        _videoFrameOffset = FrameTimeConverter.TimeToFrameRounded(mouseToVideoOffsetSeconds, _config.OutputFps);

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
            TotalFrames = FrameTimeConverter.TimeToFrameFloor(duration.Value.TotalSeconds, _config.OutputFps);
            int requiredPositions = TotalFrames + Math.Max(0, _videoFrameOffset);
            AdjustSmoothedPositionsToFrameCount(requiredPositions);
        }
        else
        {
            TotalFrames = _smoothedPositions.Count;
        }

        // Precompute per-frame "last move" timestamps for cursor auto-hide
        PrecomputeLastMoveTimes();

        // Build auto-zoom timeline with scaled coordinates and time offset + capture latency.
        // The duration is passed so clicks outside the video (e.g. a click just before
        // capture started) don't generate zoom the editor has no segment for.
        _zoomEngine.BuildZoomTimeline(
            mouseData, sourceWidth, sourceHeight, mouseData.TickFrequency,
            coordScaleX, coordScaleY, mouseToVideoOffsetSeconds,
            cropOffsetX, cropOffsetY,
            duration?.TotalSeconds ?? 0);

        // Load cursor bitmap / geometry
        _cursorRenderer.StartTimestampTicks = mouseData.StartTimestampTicks;
        _cursorRenderer.TickFrequency = mouseData.TickFrequency;
        // Needed by the shutter motion-blur path to convert relative velocity
        // (px/s) into a per-frame travel distance.
        _cursorRenderer.OutputFps = _config.OutputFps;
        await _cursorRenderer.LoadCursorAsync(_device);

        // Warm the background wallpaper so the synchronous composite path never blocks
        // on file I/O / GPU decode. A failure here is not fatal — the background renders
        // with its solid-colour fallback — but it must not silently force that fallback
        // for the whole export: the failure is logged here, and the render path retries
        // the key on a bounded backoff so a transient setup failure still recovers.
        await _bgCompositor.PreloadAsync(_device, _config.Background);
        ReportBackgroundImageFailure("InitializeAsync");

        _initialized = true;
    }

    /// <summary>
    /// The last background-image load failure, or null when the configured background
    /// image loaded (or the style does not use one). Non-null after
    /// <see cref="InitializeAsync"/> means frames will composite with the solid-colour
    /// fallback until a retry succeeds.
    /// </summary>
    public BackgroundImageLoadFailure? BackgroundImageFailure => _bgCompositor.LastImageLoadFailure;

    private void ReportBackgroundImageFailure(string phase)
    {
        var failure = _bgCompositor.LastImageLoadFailure;
        if (failure is null) return;

        DiagLog.Write(
            "FrameCompositor",
            $"{phase}: background image '{failure.Path}' unavailable ({failure.Reason}, "
            + $"attempt {failure.Attempts}); compositing with the solid-colour fallback"
            + (failure.WillRetry ? " and retrying in the background." : " — no retries left."));
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
                    Shape = lastPos.Shape,
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
    /// Sets the webcam fullscreen-animation factor in <c>[0,1]</c> for the next render.
    /// </summary>
    public void SetWebcamFullscreenFactor(float factor)
    {
        _webcamCompositor?.SetFullscreenFactor(factor);
    }

    /// <summary>
    /// Sets the webcam overlay opacity in <c>[0,1]</c> for the next render (fade in/out).
    /// </summary>
    public void SetWebcamOverlayOpacity(float opacity)
    {
        _webcamCompositor?.SetOverlayOpacity(opacity);
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
    /// Sets the text overlays that belong to this compositor's source recording.
    /// Call this when the user adds, edits, or removes a text overlay in the editor,
    /// and once up front when a compositor is created for preview or export. The
    /// <see cref="TextOverlayRenderer"/> is created lazily the first time a
    /// non-empty list is supplied, so a project with no overlays never allocates
    /// one or touches the GPU for it.
    /// </summary>
    public void SyncTextOverlays(IReadOnlyList<TextOverlaySegment> overlays)
    {
        _textOverlays = overlays ?? [];
        if (_textOverlays.Count > 0)
            _textOverlayRenderer ??= new TextOverlayRenderer(_device);
    }

    /// <summary>
    /// Asynchronously warms the background-image cache for the configured background
    /// style so <see cref="ComposeFrame(CanvasBitmap, double)"/> never blocks on file I/O
    /// or GPU decode. Already called by <see cref="InitializeAsync"/>; expose it for
    /// callers that change background configuration on a live compositor. Safe to call
    /// repeatedly — it is a no-op once the image is cached for this device.
    /// </summary>
    public Task PrewarmBackgroundAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bgCompositor.PreloadAsync(_device, _config.Background, cancellationToken);
    }

    /// <summary>
    /// True when the configured background can be drawn at full fidelity without
    /// blocking (non-image backgrounds are always ready).
    /// </summary>
    public bool IsBackgroundReady =>
        !_disposed && _bgCompositor.IsBackgroundImageReady(_device, _config.Background);

    /// <summary>
    /// Compose a single output frame at the given frame index.
    /// Returns a <see cref="CanvasRenderTarget"/> that the caller must dispose.
    /// </summary>
    public CanvasRenderTarget ComposeFrame(CanvasBitmap sourceFrame, int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= TotalFrames)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        double timeSeconds = FrameTimeConverter.FrameToTime(frameIndex, _config.OutputFps);
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
        int cursorIndex = ResolveCursorIndex(sourceTimeSeconds);
        var cursorPos = _smoothedPositions[cursorIndex];

        var zoomState = ResolveZoomState(timeSeconds);

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
    /// Maps a point in timeline time to the index of the closest sample in
    /// <see cref="_smoothedPositions"/>: mouse frame = (videoTime + offset) * fps, rounded
    /// to the nearest sample and clamped to the smoothed path's bounds. Shared by
    /// <see cref="ComposeFrame(CanvasBitmap, double)"/> and <see cref="ResolveZoomState"/>,
    /// which must always agree on the cursor position for a given instant.
    /// </summary>
    private int ResolveCursorIndex(double timeSeconds) => Math.Clamp(
        FrameTimeConverter.TimeToFrameRounded(timeSeconds + _mouseTimeOffset, _config.OutputFps),
        0, _smoothedPositions.Count - 1);

    /// <summary>
    /// Resolves the zoom/pan state for an arbitrary point in timeline time,
    /// including the cursor-center override and continuous camera drift.
    /// <para>
    /// This is a <b>pure function of <paramref name="timeSeconds"/></b> — it reads
    /// only stable per-clip state (smoothed cursor path, zoom engine, source
    /// dimensions) and mutates nothing. That purity is what lets the shutter-blur
    /// accumulation below sample several sub-frame times per output frame and get
    /// exactly the same answer preview scrubbing, playback, and export would each
    /// produce for that instant on their own.
    /// </para>
    /// </summary>
    private ZoomState ResolveZoomState(double timeSeconds)
    {
        // Zoom held at rest for the region picker — see SuppressZoom. Returning here rather
        // than at the ComposeFrame call site keeps the motion-blur and camera-velocity
        // samplers, which resolve their own states, from reintroducing the zoom.
        if (SuppressZoom) return RestZoomState();

        // Same cursor-index formula as ComposeFrame.
        int cursorIndex = ResolveCursorIndex(timeSeconds);
        var cursorPos = _smoothedPositions[cursorIndex];

        // Get zoom state — blend the smoothed cursor position into the center so an
        // auto segment keeps the cursor in view.
        var zoomState = _zoomEngine.GetZoomState(timeSeconds);
        float cursorWeight = float.IsFinite(zoomState.CursorFollowWeight)
            ? Math.Clamp(zoomState.CursorFollowWeight, 0f, 1f)
            : 1f;
        if (zoomState.ZoomLevel > 1.01f && cursorWeight > 0f)
        {
            // Auto (click-driven) zooms center on the actual cursor; manual segments keep
            // their user-defined center. The weight eases between the two across a handoff
            // rather than switching abruptly — the two kinds of shot take their focal point
            // from different sources, so a hard switch at the join would snap the camera.
            // ComputeViewportForCenter returns a fresh state, so the segment identity
            // has to be carried across by hand — losing it here would silently
            // disable camera drift for every auto zoom, which is the common case.
            float centerX = zoomState.CenterX + ((float)cursorPos.X - zoomState.CenterX) * cursorWeight;
            float centerY = zoomState.CenterY + ((float)cursorPos.Y - zoomState.CenterY) * cursorWeight;

            var recentred = _zoomEngine.ComputeViewportForCenter(
                zoomState.ZoomLevel, centerX, centerY);
            recentred.HasSegment = zoomState.HasSegment;
            recentred.SegmentProgress = zoomState.SegmentProgress;
            recentred.SegmentHeadingX = zoomState.SegmentHeadingX;
            recentred.SegmentHeadingY = zoomState.SegmentHeadingY;
            recentred.DriftScale = zoomState.DriftScale;
            recentred.CursorFollowWeight = zoomState.CursorFollowWeight;
            zoomState = recentred;
        }

        return ApplyCameraDrift(zoomState);
    }

    /// <summary>
    /// Layers continuous "living camera" drift on top of an already-resolved zoom
    /// state. No-ops for an un-zoomed frame (<see cref="CameraDrift.Window"/> is
    /// exactly 0 at 1×), which is what guarantees drift never touches a frame
    /// outside a zoom segment and a segment always returns to its exact original
    /// framing as the zoom releases.
    /// </summary>
    private ZoomState ApplyCameraDrift(ZoomState zoomState)
    {
        if (!_config.CameraDrift.Enabled || zoomState.ZoomLevel <= 1f || !zoomState.HasSegment)
            return zoomState;

        var vp = _zoomEngine.ComputeViewportForCenter(
            zoomState.ZoomLevel, zoomState.CenterX, zoomState.CenterY);

        // Room left between the viewport and the nearer source edge on each axis —
        // bounds the float layer so it can never push the viewport into the clamp,
        // which would stall the motion dead instead of settling naturally.
        float slackX = Math.Max(0f, Math.Min(vp.ViewportX, _sourceWidth - vp.ViewportX - vp.ViewportWidth));
        float slackY = Math.Max(0f, Math.Min(vp.ViewportY, _sourceHeight - vp.ViewportY - vp.ViewportHeight));

        var drift = Musio.Core.Processing.CameraDrift.Evaluate(
            _config.CameraDrift, zoomState.SegmentProgress, zoomState.ZoomLevel,
            vp.ViewportWidth, vp.ViewportHeight, slackX, slackY, zoomState.SegmentHeadingX, zoomState.SegmentHeadingY);
        if (!drift.IsActive) return zoomState;

        float driftScale = float.IsFinite(zoomState.DriftScale)
            ? Math.Clamp(zoomState.DriftScale, 0f, 1f)
            : 1f;
        var scaledDrift = new CameraDriftResult(
            1f + ((drift.ZoomFactor - 1f) * driftScale),
            drift.OffsetX * driftScale,
            drift.OffsetY * driftScale);

        // ApplyZoom (never a plain multiply) preserves the 1x == 1x invariant.
        float driftedZoom = Musio.Core.Processing.CameraDrift.ApplyZoom(zoomState.ZoomLevel, scaledDrift);
        float cx = zoomState.CenterX + scaledDrift.OffsetX;
        float cy = zoomState.CenterY + scaledDrift.OffsetY;

        var drifted = _zoomEngine.ComputeViewportForCenter(driftedZoom, cx, cy);
        drifted.HasSegment = zoomState.HasSegment;
        drifted.SegmentProgress = zoomState.SegmentProgress;
        drifted.SegmentHeadingX = zoomState.SegmentHeadingX;
        drifted.SegmentHeadingY = zoomState.SegmentHeadingY;
        drifted.DriftScale = zoomState.DriftScale;
        drifted.CursorFollowWeight = zoomState.CursorFollowWeight;
        return drifted;
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

        // Crop source frame to the effective viewport, scaled to content dimensions.
        // Only the cropped source content is candidate for blur here — the
        // background/padding is unaffected, which is correct: in ZoomScope.Source
        // only the source content itself zooms/pans.
        var croppedFrame = CropSourceFrameWithMotionBlur(sourceFrame, viewport, timeSeconds);

        // Camera velocity relative to the frame, for the cursor's own blur (Step E).
        var cameraVelocity = ComputeCameraVelocityDirect(timeSeconds, viewport);

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
            RenderCursorOverlay(
                ds, cursorPos, viewport, timeSeconds, cursorIndex, _config.MotionBlur, cameraVelocity);

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

        // Text overlays are drawn after the drawing session above has closed (not inside
        // it) because TextOverlayRenderer's frosted-blur background samples the pixels of
        // the already-composed frame — it needs `output` fully flushed and readable, not
        // an in-progress drawing session. They stay fixed on screen and are never scaled
        // by a zoom, the same rule webcam/keyboard/subtitle overlays follow above (there is
        // no zoom in this direct-composition path to begin with, but the rule still holds
        // if that ever changes).
        RenderTextOverlays(output, timeSeconds);
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

        // 3. Compute the frame-time crop rect up front — needed to pick interpolation
        //    and to drive the shutter-blur accumulation below.
        var frameCropRect = ComputeCompositeCropRect(zoomState, viewport1x);

        // The cursor is drawn INTO the 1x composite buffer, so the shutter
        // accumulation in step 5 already smears it by the camera's motion along with
        // everything else in that buffer. Its own blur must therefore be its absolute
        // velocity within the buffer, with no camera term: subtracting camera velocity
        // here would double-count it. (The ZoomScope.Source path is the opposite case —
        // there the cursor is drawn after the crop, in output space, so it does need
        // the subtraction. See ComputeCameraVelocityDirect.)
        var cursorCameraVelocity = Vector2.Zero;

        // 4. Render 1x composite: background + content + cursor
        EnsureCompositeBuffer();
        using (var ds = _compositeBuffer!.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _bgCompositor.CompositeFrame(
                ds, croppedFrame, OutputWidth, OutputHeight,
                _sourceAreaOffsetX, _sourceAreaOffsetY, _sourceAreaWidth, _sourceAreaHeight,
                _config.Background);
            RenderCursorOverlay(
                ds, cursorPos, viewport1x, timeSeconds, cursorIndex, _config.MotionBlur, cursorCameraVelocity);
        }

        // 5. Draw zoomed composite (shutter-blurred when the camera is moving
        //    fast enough) + fixed overlays
        var output = CreateRenderTarget(OutputWidth, OutputHeight, "post-composite zoom output");
        using (var ds = output.CreateDrawingSession())
        {
            // Use high-quality interpolation only when zoomed; linear is cheaper at 1:1
            var interpolation = (frameCropRect.Width < OutputWidth * 0.95
                || frameCropRect.Height < OutputHeight * 0.95)
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;

            DrawCompositeWithMotionBlur(ds, viewport1x, frameCropRect, timeSeconds, interpolation);

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

        // Text overlays, like the fixed overlays above, are drawn outside the drawing
        // session and after the zoom: outside because TextOverlayRenderer's
        // frosted-blur background samples the already-composed `output` pixels (it
        // needs a closed, flushed drawing session to read from, not an open one), and
        // after the zoom because overlays stay fixed on screen and are never scaled by
        // it — the same rule the webcam/keyboard/subtitle overlays follow just above.
        RenderTextOverlays(output, timeSeconds);
        return output;
    }

    /// <summary>
    /// Draws every active text overlay onto an already-finished output frame. Shared by
    /// both compose paths, called after their drawing session has closed. No-ops
    /// immediately (no allocation, no branch into GPU work) when no overlays have been
    /// synced via <see cref="SyncTextOverlays"/>, which keeps the hot path free for the
    /// common case of a project with no text overlays.
    /// </summary>
    private void RenderTextOverlays(CanvasRenderTarget output, double timeSeconds)
    {
        if (_textOverlayRenderer is null || _textOverlays.Count == 0)
            return;

        var sourceTime = TimeSpan.FromSeconds(timeSeconds);
        _textOverlayRenderer.Render(output, _textOverlays, sourceTime, OutputWidth, OutputHeight);
    }

    /// <summary>
    /// Ensures the composite buffer for post-composite zoom is allocated
    /// at the current output dimensions.
    /// </summary>
    private void EnsureCompositeBuffer()
    {
        try
        {
            _compositeBufferHolder.Ensure(_device, OutputWidth, OutputHeight, "post-composite zoom buffer");
        }
        catch (InvalidOperationException)
        {
            ReleaseCachedRenderTargets();
            throw;
        }
    }

    #region Camera Motion Blur (shutter accumulation)

    /// <summary>
    /// Computes the zoomed crop rect (in <see cref="_compositeBuffer"/> pixel
    /// space) for a given zoom state — the same math
    /// <see cref="ComposeFramePostCompositeZoom"/> used to do inline, factored out
    /// so the shutter-blur accumulation can re-evaluate it at several sub-frame times.
    /// </summary>
    private Rect ComputeCompositeCropRect(ZoomState zoomState, Rect viewport1x)
    {
        // _sourceAreaOffsetX/Y already includes user-padding plus any AR-fit gap,
        // so no separate +padding is needed here.
        float cxComp = (float)((zoomState.CenterX - viewport1x.X)
            * _sourceAreaWidth / viewport1x.Width + _sourceAreaOffsetX);
        float cyComp = (float)((zoomState.CenterY - viewport1x.Y)
            * _sourceAreaHeight / viewport1x.Height + _sourceAreaOffsetY);
        float cropW = OutputWidth / zoomState.ZoomLevel;
        float cropH = OutputHeight / zoomState.ZoomLevel;
        float cropX = Math.Clamp(
            cxComp - cropW / 2f, 0f, Math.Max(0f, OutputWidth - cropW));
        float cropY = Math.Clamp(
            cyComp - cropH / 2f, 0f, Math.Max(0f, OutputHeight - cropH));
        return new Rect(cropX, cropY, cropW, cropH);
    }

    /// <summary>
    /// Maps a point in <see cref="_compositeBuffer"/> pixel space to output-canvas
    /// pixel space through a given crop rect. This is exactly the inverse of the
    /// crop-and-scale <see cref="Windows.Foundation.Rect"/> pair Win2D's
    /// <c>DrawImage(dest, source)</c> overload applies.
    /// </summary>
    private Vector2 MapCompositeToOutput(Vector2 compositePoint, Rect cropRect)
    {
        return new Vector2(
            (float)((compositePoint.X - cropRect.X) * (OutputWidth / cropRect.Width)),
            (float)((compositePoint.Y - cropRect.Y) * (OutputHeight / cropRect.Height)));
    }

    /// <summary>
    /// Maps a point in source-frame pixel space to output-canvas pixel space
    /// through a given viewport, mirroring the transform
    /// <see cref="RenderCursorOverlay"/> applies to the cursor position.
    /// </summary>
    private Vector2 MapSourcePointToOutput(Vector2 sourcePoint, Rect viewport)
    {
        float scaleX = (float)(_sourceAreaWidth / viewport.Width);
        float scaleY = (float)(_sourceAreaHeight / viewport.Height);
        return new Vector2(
            (float)(sourcePoint.X - viewport.X) * scaleX + _sourceAreaOffsetX,
            (float)(sourcePoint.Y - viewport.Y) * scaleY + _sourceAreaOffsetY);
    }

    /// <summary>
    /// Estimates how far the camera swept across a shutter interval by mapping the
    /// four corners of <paramref name="referenceRect"/> through the shutter-open
    /// (<paramref name="mapStart"/>) and shutter-close (<paramref name="mapEnd"/>)
    /// camera transforms and taking the largest displacement. Corners (not just the
    /// centre) are what catch pure zoom motion — under a scale change alone the
    /// centre barely moves, but the corners sweep a long way. The centre's own
    /// displacement is reported separately as the pan component.
    /// </summary>
    private static (float maxCornerTravel, float panTravel) EstimateShutterTravel(
        Rect referenceRect, Func<Vector2, Vector2> mapStart, Func<Vector2, Vector2> mapEnd)
    {
        Vector2 topLeft = new((float)referenceRect.X, (float)referenceRect.Y);
        Vector2 topRight = new((float)(referenceRect.X + referenceRect.Width), (float)referenceRect.Y);
        Vector2 bottomLeft = new((float)referenceRect.X, (float)(referenceRect.Y + referenceRect.Height));
        Vector2 bottomRight = new(
            (float)(referenceRect.X + referenceRect.Width), (float)(referenceRect.Y + referenceRect.Height));
        Vector2 center = new(
            (float)(referenceRect.X + referenceRect.Width / 2.0),
            (float)(referenceRect.Y + referenceRect.Height / 2.0));

        float maxCornerTravel = 0f;
        foreach (var corner in new[] { topLeft, topRight, bottomLeft, bottomRight })
            maxCornerTravel = Math.Max(maxCornerTravel, (mapEnd(corner) - mapStart(corner)).Length());

        float panTravel = (mapEnd(center) - mapStart(center)).Length();
        return (maxCornerTravel, panTravel);
    }

    /// <summary>
    /// Blends the pan/zoom channel strengths by how much each contributed to the
    /// estimated travel, then resolves how many shutter samples to average and how
    /// much of the shutter interval to actually sweep. Scaling the shutter itself
    /// (not just the sample count) is what makes e.g. <c>ZoomStrength = 0</c>
    /// genuinely suppress zoom blur rather than just averaging fewer samples across
    /// the same physical smear.
    /// </summary>
    private static (int sampleCount, double shutterSeconds) ResolveShutterSamples(
        MotionBlurSettings motionBlur, double shutter, float maxCornerTravel, float panTravel)
    {
        float zoomTravel = Math.Max(0f, maxCornerTravel - panTravel);
        const float epsilon = 1e-4f;
        float channelScale = (panTravel * motionBlur.PanStrength + zoomTravel * motionBlur.ZoomStrength)
            / Math.Max(epsilon, panTravel + zoomTravel);

        float effectiveTravel = maxCornerTravel * channelScale;
        int sampleCount = motionBlur.ResolveSampleCount(effectiveTravel);
        double scaledShutter = shutter * channelScale;

        // Never emit a smear we cannot sample smoothly. ResolveSampleCount caps at
        // MaxSamples, so a fast camera would otherwise be rendered as a handful of
        // widely-spaced copies — which on a small high-contrast element like the
        // cursor reads as discrete duplicate pointers rather than as blur. When the
        // travel exceeds what the available samples can cover at the target spacing,
        // shorten the shutter instead of letting the samples spread apart: a slightly
        // shorter blur is invisible, banding is not.
        if (sampleCount > 1)
        {
            float renderableTravel = (sampleCount - 1) * Math.Max(0.25f, motionBlur.SampleSpacingPixels);
            if (effectiveTravel > renderableTravel)
                scaledShutter *= renderableTravel / effectiveTravel;
        }

        return (sampleCount, scaledShutter);
    }

    /// <summary>
    /// Runs the shared progressive-average shutter-sample loop used by both
    /// <see cref="DrawCompositeWithMotionBlur"/> and
    /// <see cref="CropSourceFrameWithMotionBlur"/>: <paramref name="sampleCount"/> samples
    /// spread evenly across the shutter interval centered on <paramref name="timeSeconds"/>,
    /// each handed to <paramref name="drawSample"/> at the exact sample time and the
    /// progressive-averaging opacity <c>1/(i+1)</c> (see the callers' remarks for why that
    /// opacity sequence — not a uniform <c>1/N</c> — yields an exact running mean). The two
    /// callers differ only in what they draw per sample (a composite crop rect vs. a
    /// direct source-crop sample), which is why this factors out the loop and sample-time
    /// math but not the draw call itself.
    /// </summary>
    private static void DrawProgressiveShutterSamples(
        int sampleCount, double timeSeconds, double shutterSeconds, Action<double, float> drawSample)
    {
        for (int i = 0; i < sampleCount; i++)
        {
            double sampleTime = timeSeconds + shutterSeconds * ((i + 0.5) / sampleCount - 0.5);
            drawSample(sampleTime, 1f / (i + 1));
        }
    }

    /// <summary>
    /// Draws the 1x composite buffer into <paramref name="ds"/>, cropped/scaled to
    /// <paramref name="frameCropRect"/>. When the camera travels far enough during
    /// the virtual shutter interval, this instead averages several sub-frame crop
    /// samples (temporal supersampling) rather than a single draw. That one
    /// mechanism covers both zoom and pan motion — directional/Gaussian blur would
    /// be physically wrong for the radial smear a zoom produces.
    /// </summary>
    private void DrawCompositeWithMotionBlur(
        CanvasDrawingSession ds,
        Rect viewport1x,
        Rect frameCropRect,
        double timeSeconds,
        CanvasImageInterpolation interpolation)
    {
        var motionBlur = _config.MotionBlur;
        double shutter = motionBlur.ShutterFraction / _config.OutputFps;
        if (!motionBlur.Enabled || shutter <= 0)
        {
            ds.DrawImage(_compositeBuffer,
                new Rect(0, 0, OutputWidth, OutputHeight), frameCropRect, 1f, interpolation);
            return;
        }

        double halfShutter = shutter / 2.0;
        var cropStart = ComputeCompositeCropRect(ResolveZoomState(timeSeconds - halfShutter), viewport1x);
        var cropEnd = ComputeCompositeCropRect(ResolveZoomState(timeSeconds + halfShutter), viewport1x);

        var compositeRect = new Rect(0, 0, OutputWidth, OutputHeight);
        var (maxCornerTravel, panTravel) = EstimateShutterTravel(
            compositeRect,
            pt => MapCompositeToOutput(pt, cropStart),
            pt => MapCompositeToOutput(pt, cropEnd));

        var (sampleCount, shutterSeconds) = ResolveShutterSamples(motionBlur, shutter, maxCornerTravel, panTravel);
        if (sampleCount <= 1)
        {
            ds.DrawImage(_compositeBuffer,
                new Rect(0, 0, OutputWidth, OutputHeight), frameCropRect, 1f, interpolation);
            return;
        }

        // Progressive averaging: drawing sample i at opacity 1/(i+1) makes ordinary
        // source-over blending compute an exact running mean of the samples drawn
        // so far — after k draws the buffer holds the average of the first k
        // samples. A uniform 1/N opacity would under-cover and wash the frame out.
        // (Valid here only because every sample covers the entire opaque canvas; see
        // CursorRenderer, where sparse sprites need additive accumulation instead.)
        //
        // Samples use Linear interpolation regardless of the sharp-frame choice: the
        // result is being averaged into a smear, so the extra bandwidth of
        // HighQualityCubic buys nothing visible and multiplies the per-frame cost by
        // the sample count — which is what made playback stutter during zooms.
        DrawProgressiveShutterSamples(sampleCount, timeSeconds, shutterSeconds, (sampleTime, opacity) =>
        {
            var sampleCrop = ComputeCompositeCropRect(ResolveZoomState(sampleTime), viewport1x);
            ds.DrawImage(_compositeBuffer,
                new Rect(0, 0, OutputWidth, OutputHeight), sampleCrop, opacity,
                CanvasImageInterpolation.Linear);
        });
    }

    /// <summary>
    /// Camera velocity in output-canvas pixels/second for the direct composition
    /// path (<see cref="ZoomScope.Source"/> and unzoomed frames). Here the cursor is
    /// drawn onto the output <i>after</i> the source crop, so — unlike the
    /// post-composite zoom path, where the cursor rides inside the buffer being
    /// blurred — nothing else smears it by the camera's motion. Subtracting this
    /// from the cursor's own velocity is what keeps a cursor that the camera is
    /// panning to follow looking sharp, since it is barely moving on screen.
    /// </summary>
    private Vector2 ComputeCameraVelocityDirect(double timeSeconds, Rect viewport)
    {
        if (timeSeconds <= 0) return Vector2.Zero;

        double prevTime = timeSeconds - 1.0 / _config.OutputFps;
        var prevViewport = ComputeEffectiveViewport(ResolveZoomState(prevTime));

        var point = new Vector2(
            (float)(viewport.X + viewport.Width / 2.0),
            (float)(viewport.Y + viewport.Height / 2.0));

        var mappedNow = MapSourcePointToOutput(point, viewport);
        var mappedPrev = MapSourcePointToOutput(point, prevViewport);
        return (mappedNow - mappedPrev) * (float)_config.OutputFps;
    }

    #endregion

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
            return Win2DUtils.CreateRenderTarget(_device, width, height, 96, purpose);
        }
        catch (InvalidOperationException)
        {
            ReleaseCachedRenderTargets();
            throw;
        }
    }

    private void ReleaseCachedRenderTargets()
    {
        _croppedBufferHolder.Clear();
        _compositeBufferHolder.Clear();
    }

    private void ThrowIfDeviceLost() => _deviceLostGuard.ThrowIfLost();

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
        float targetRatio = AspectRatioHelper.GetRatioValue(_config.AspectRatio);

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
        float targetRatio = AspectRatioHelper.GetRatioValue(_config.AspectRatio);
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
        EnsureCroppedBuffer();
        var interpolation = ResolveCropInterpolation(source, viewport);

        using var ds = _croppedBuffer!.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        DrawSourceCropSample(ds, source, viewport, interpolation, 1f);
        return _croppedBuffer;
    }

    /// <summary>
    /// Like <see cref="CropSourceFrame"/>, but when the camera (viewport) travels
    /// far enough during the virtual shutter interval, averages several sub-frame
    /// viewport samples into the buffer instead of drawing once. Used only by the
    /// direct composition path (<see cref="ZoomScope.Source"/>, or an unzoomed
    /// frame): only the cropped source content is blurred here, since in this
    /// scope the background/padding never zooms or pans in the first place.
    /// </summary>
    private CanvasRenderTarget CropSourceFrameWithMotionBlur(CanvasBitmap source, Rect viewport, double timeSeconds)
    {
        var motionBlur = _config.MotionBlur;
        double shutter = motionBlur.ShutterFraction / _config.OutputFps;
        if (!motionBlur.Enabled || shutter <= 0)
            return CropSourceFrame(source, viewport);

        double halfShutter = shutter / 2.0;
        var viewportStart = ComputeEffectiveViewport(ResolveZoomState(timeSeconds - halfShutter));
        var viewportEnd = ComputeEffectiveViewport(ResolveZoomState(timeSeconds + halfShutter));

        var (maxCornerTravel, panTravel) = EstimateShutterTravel(
            viewport,
            pt => MapSourcePointToOutput(pt, viewportStart),
            pt => MapSourcePointToOutput(pt, viewportEnd));

        var (sampleCount, shutterSeconds) = ResolveShutterSamples(motionBlur, shutter, maxCornerTravel, panTravel);
        if (sampleCount <= 1)
            return CropSourceFrame(source, viewport);

        EnsureCroppedBuffer();

        using var ds = _croppedBuffer!.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        // Progressive averaging — see DrawCompositeWithMotionBlur for why 1/(i+1)
        // (not a uniform 1/N) opacity gives an exact running mean. Samples use Linear
        // rather than the sharp-path interpolation for the same reason as there: the
        // result is a smear, so the cost of HighQualityCubic per sample buys nothing.
        DrawProgressiveShutterSamples(sampleCount, timeSeconds, shutterSeconds, (sampleTime, opacity) =>
        {
            var sampleViewport = ComputeEffectiveViewport(ResolveZoomState(sampleTime));
            DrawSourceCropSample(ds, source, sampleViewport, CanvasImageInterpolation.Linear, opacity);
        });

        return _croppedBuffer;
    }

    /// <summary>
    /// Ensures the reusable source-crop buffer is allocated at the current
    /// source-area dimensions.
    /// </summary>
    private void EnsureCroppedBuffer()
    {
        try
        {
            _croppedBufferHolder.Ensure(_device, _sourceAreaWidth, _sourceAreaHeight, "source crop buffer");
        }
        catch (InvalidOperationException)
        {
            ReleaseCachedRenderTargets();
            throw;
        }
    }

    /// <summary>
    /// Adaptive interpolation: HighQualityCubic is a 4-tap bicubic filter,
    /// significantly more expensive than Linear. It only meaningfully improves
    /// quality when actually resampling (zoom in/out or aspect-ratio crop with
    /// non-unit scale). At ~1:1 the bicubic filter produces output essentially
    /// identical to Linear, so use the cheaper path. Use the same explicit
    /// near-unit threshold form as ComposeFramePostCompositeZoom so the two
    /// paths stay aligned over time.
    /// </summary>
    private CanvasImageInterpolation ResolveCropInterpolation(CanvasBitmap source, Rect viewport)
    {
        var bitmapViewport = ToBitmapViewport(source, viewport);
        double scaleX = _sourceAreaWidth / bitmapViewport.Width;
        double scaleY = _sourceAreaHeight / bitmapViewport.Height;
        const double nearUnitScaleMinimum = 0.95;
        const double nearUnitScaleMaximum = 1.05;
        bool nearUnitScale =
            scaleX >= nearUnitScaleMinimum && scaleX <= nearUnitScaleMaximum &&
            scaleY >= nearUnitScaleMinimum && scaleY <= nearUnitScaleMaximum;
        return nearUnitScale
            ? CanvasImageInterpolation.Linear
            : CanvasImageInterpolation.HighQualityCubic;
    }

    /// <summary>
    /// Draws one viewport sample of the source bitmap into the crop buffer at the
    /// given opacity. Shared by the single-draw path and the shutter-blur
    /// accumulation, which calls this once per sub-frame sample.
    /// </summary>
    private void DrawSourceCropSample(
        CanvasDrawingSession ds, CanvasBitmap source, Rect viewport,
        CanvasImageInterpolation interpolation, float opacity)
    {
        var bitmapViewport = ToBitmapViewport(source, viewport);
        ds.DrawImage(source,
            new Rect(0, 0, _sourceAreaWidth, _sourceAreaHeight),
            bitmapViewport,
            opacity, interpolation);
    }

    /// <summary>
    /// Converts a viewport expressed in logical source pixels to the source
    /// bitmap's own pixel space (they can differ, e.g. preview's reduced-resolution
    /// frame source vs full-resolution export).
    /// </summary>
    private Rect ToBitmapViewport(CanvasBitmap source, Rect viewport)
    {
        double bitmapScaleX = source.SizeInPixels.Width / (double)_sourceWidth;
        double bitmapScaleY = source.SizeInPixels.Height / (double)_sourceHeight;
        return new Rect(
            viewport.X * bitmapScaleX,
            viewport.Y * bitmapScaleY,
            viewport.Width * bitmapScaleX,
            viewport.Height * bitmapScaleY);
    }

    #endregion

    #region Cursor

    private void RenderCursorOverlay(
        CanvasDrawingSession session,
        SmoothedPosition cursorPos,
        Rect viewport,
        double timeSeconds,
        int frameIndex,
        MotionBlurSettings? motionBlur = null,
        Vector2 cameraVelocity = default)
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
            Shape = cursorPos.Shape,
        };

        // Collect temporally-relevant clicks with transformed positions
        var activeClicks = GetActiveClicks(timeSeconds, viewport, scaleX, scaleY);

        double lastMoveTime = _lastMoveTimes[frameIndex];

        _cursorRenderer.RenderFrame(
            session, transformedPos, activeClicks, timeSeconds, lastMoveTime, motionBlur, cameraVelocity);
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
            _deviceLostGuard.Dispose();
            ReleaseCachedRenderTargets();
            _bgCompositor.Dispose();
            _cursorRenderer.Dispose();
            _webcamCompositor?.Dispose();
            _textOverlayRenderer?.Dispose();
            _smoothedPositions = [];
            _lastMoveTimes = [];
            _mouseData = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
