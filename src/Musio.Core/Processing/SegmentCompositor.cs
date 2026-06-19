using Microsoft.Graphics.Canvas;
using Musio.Core.Export;
using Musio.Core.Timeline;

namespace Musio.Core.Processing;

/// <summary>
/// High-level compositor that renders output frames from a segment-based timeline.
/// Routes each frame to the appropriate renderer based on segment type:
/// <see cref="VideoSegment"/> → <see cref="FrameCompositor"/>,
/// <see cref="TextSlideSegment"/> → <see cref="TextSlideRenderer"/>.
/// Handles transitions between segments via <see cref="TransitionRenderer"/>.
/// </summary>
public class SegmentCompositor : IDisposable
{
    private readonly TimelineModel _timeline;
    private readonly TimelineMapper _mapper;
    private readonly TextSlideRenderer _textSlideRenderer;
    private readonly TransitionRenderer _transitionRenderer;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private bool _disposed;

    /// <summary>
    /// Video frame provider delegate. Given a <see cref="VideoSegment"/> and source
    /// time in seconds, returns the composed frame (background + cursor + overlays).
    /// The caller is responsible for disposing the returned bitmap.
    /// </summary>
    public delegate CanvasBitmap VideoFrameProvider(VideoSegment segment, double sourceTimeSeconds);

    /// <summary>
    /// Optional callback that the caller sets to provide composed video frames.
    /// When null, video segments produce black frames.
    /// </summary>
    public VideoFrameProvider? GetVideoFrame { get; set; }

    public SegmentCompositor(
        TimelineModel timeline,
        TimelineMapper mapper,
        int outputWidth,
        int outputHeight,
        CanvasDevice? device = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;

        _textSlideRenderer = new TextSlideRenderer(device);
        _transitionRenderer = new TransitionRenderer(device);
    }

    /// <summary>
    /// Composes a single output frame for the given output frame index.
    /// Returns a <see cref="CanvasRenderTarget"/> that the caller must dispose.
    /// </summary>
    public CanvasRenderTarget ComposeOutputFrame(int outputFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var frameRef = _mapper.GetSegmentFrameRef(outputFrame);

        // Handle transition blending
        if (frameRef.IsInTransition && frameRef.OutgoingSegment is not null
            && frameRef.Segment.InTransition is { } transition)
        {
            using var outgoingFrame = RenderSegmentFrame(
                frameRef.OutgoingSegment,
                frameRef.OutgoingSourceTimeSeconds,
                progress: 1.0); // outgoing is at its tail

            using var incomingFrame = RenderSegmentFrame(
                frameRef.Segment,
                frameRef.SourceTimeSeconds,
                frameRef.Progress);

            return _transitionRenderer.Render(
                outgoingFrame, incomingFrame,
                transition.Type, frameRef.TransitionProgress,
                _outputWidth, _outputHeight);
        }

        // No transition — render the segment directly
        return RenderSegmentFrame(frameRef.Segment, frameRef.SourceTimeSeconds, frameRef.Progress);
    }

    private CanvasRenderTarget RenderSegmentFrame(
        TimelineSegment segment, double sourceTimeSeconds, double progress)
    {
        return segment switch
        {
            TextSlideSegment slide => _textSlideRenderer.RenderSlide(
                slide, progress, _outputWidth, _outputHeight),

            VideoSegment video => RenderVideoSegmentFrame(video, sourceTimeSeconds),

            _ => CreateBlackFrame(),
        };
    }

    private CanvasRenderTarget RenderVideoSegmentFrame(VideoSegment video, double sourceTimeSeconds)
    {
        if (GetVideoFrame is not null)
        {
            var frame = GetVideoFrame(video, sourceTimeSeconds);
            if (frame is CanvasRenderTarget rt)
                return rt;

            // If the provider returned a non-RenderTarget bitmap, copy it
            var device = CanvasDevice.GetSharedDevice();
            var target = new CanvasRenderTarget(device, _outputWidth, _outputHeight, 96);
            using var ds = target.CreateDrawingSession();
            ds.DrawImage(frame, new Windows.Foundation.Rect(0, 0, _outputWidth, _outputHeight),
                new Windows.Foundation.Rect(0, 0, frame.SizeInPixels.Width, frame.SizeInPixels.Height));
            frame.Dispose();
            return target;
        }

        return CreateBlackFrame();
    }

    private CanvasRenderTarget CreateBlackFrame()
    {
        var device = CanvasDevice.GetSharedDevice();
        var target = new CanvasRenderTarget(device, _outputWidth, _outputHeight, 96);
        using var ds = target.CreateDrawingSession();
        ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        return target;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textSlideRenderer.Dispose();
        _transitionRenderer.Dispose();
        GC.SuppressFinalize(this);
    }
}
