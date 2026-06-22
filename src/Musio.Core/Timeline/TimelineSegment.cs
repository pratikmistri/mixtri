namespace Musio.Core.Timeline;

using Musio.Core.Processing;

/// <summary>
/// Base class for all segments on the output timeline.
/// The timeline is an ordered sequence of segments; each segment
/// occupies a contiguous time range on the output.
/// </summary>
public abstract record TimelineSegment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Position on the output timeline where this segment starts.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>Duration of this segment on the output timeline.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Position on the output timeline where this segment ends.</summary>
    public TimeSpan End => Start + Duration;

    /// <summary>Optional transition applied when entering this segment.</summary>
    public TransitionConfig? InTransition { get; set; }
}

/// <summary>
/// A segment backed by a recorded video file with associated cursor, audio, and webcam data.
/// </summary>
public record VideoSegment : TimelineSegment
{
    /// <summary>Path to the source video file for this segment.</summary>
    public string VideoFilePath { get; init; } = "";

    /// <summary>Path to the binary cursor recording data.</summary>
    public string? CursorDataFilePath { get; init; }

    /// <summary>Path to the webcam recording, if any.</summary>
    public string? WebcamFilePath { get; init; }

    /// <summary>Path to the keyboard recording data, if any.</summary>
    public string? KeyboardDataFilePath { get; init; }

    /// <summary>Audio file paths (system audio, mic, etc.).</summary>
    public List<string> AudioFilePaths { get; init; } = [];

    /// <summary>Start position within the source video.</summary>
    public TimeSpan SourceStart { get; init; }

    /// <summary>How much source content this segment represents.</summary>
    public TimeSpan SourceDuration { get; init; }

    /// <summary>Playback speed factor. 1.0 = normal, 2.0 = 2× fast, 0.5 = half speed.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>Source video width in pixels.</summary>
    public int SourceWidth { get; init; }

    /// <summary>Source video height in pixels.</summary>
    public int SourceHeight { get; init; }

    /// <summary>Source video frame rate.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Time offset between mouse recording start and video frame 0.</summary>
    public double MouseToVideoOffsetSeconds { get; init; }

    /// <summary>Time offset between audio recording start and video frame 0.</summary>
    public double AudioToVideoOffsetSeconds { get; init; }

    /// <summary>DPI scale of the monitor where the recording was made.</summary>
    public float DpiScale { get; init; }

    /// <summary>Screen-absolute X offset of the captured area.</summary>
    public int CropOffsetX { get; init; }

    /// <summary>Screen-absolute Y offset of the captured area.</summary>
    public int CropOffsetY { get; init; }

    /// <summary>Text overlays rendered on top of this video segment.</summary>
    public List<TextOverlay> TextOverlays { get; init; } = [];

    /// <summary>
    /// Per-segment frame style (background) override. When null, the global
    /// composition background is used. Frame style is a per-segment property.
    /// </summary>
    public BackgroundStyle? FrameStyleOverride { get; set; }

    /// <summary>
    /// Per-segment cursor style override. When null, the global composition cursor
    /// style is used. Cursor style is a per-segment property.
    /// </summary>
    public CursorStyle? CursorStyleOverride { get; set; }
}

/// <summary>
/// A full-screen text/title card segment (not backed by a video file).
/// </summary>
public record TextSlideSegment : TimelineSegment
{
    public string Text { get; set; } = "Title";
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 72;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Horizontal text alignment.</summary>
    public SlideTextAlignment TextAlignment { get; set; } = SlideTextAlignment.Center;

    /// <summary>Normalized horizontal center of the text block (0..1).</summary>
    public double TextX { get; set; } = 0.5;

    /// <summary>Normalized vertical center of the text block (0..1).</summary>
    public double TextY { get; set; } = 0.5;

    // ── Background ──
    /// <summary>How the slide background is rendered.</summary>
    public SlideBackgroundType BackgroundType { get; set; } = SlideBackgroundType.Solid;

    /// <summary>Solid color, and the start color of a gradient.</summary>
    public string BackgroundColor { get; set; } = "#1E1E1E";

    /// <summary>End color of a gradient background.</summary>
    public string GradientEndColor { get; set; } = "#16213E";

    /// <summary>Gradient direction in degrees.</summary>
    public double GradientAngle { get; set; } = 135;

    /// <summary>Path to an image used as the background (Image type).</summary>
    public string? BackgroundImagePath { get; set; }

    public TextSlideAnimation Animation { get; set; } = TextSlideAnimation.ZoomBlurIn;
}

/// <summary>
/// A time-ranged camera (webcam) overlay segment living on its own track.
/// Like zoom keyframes, its range is expressed in <b>source-video time</b>
/// (<see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/> are
/// reused as the source in/out range) so it stays aligned with the recording and
/// survives video reorder/trim through the same source↔output mapping. The webcam
/// overlay is shown only while the playhead's source time is inside an
/// <see cref="Enabled"/> segment; each segment can override the overlay style
/// (position, size, shape, mirror) independently.
/// </summary>
public record CameraSegment : TimelineSegment
{
    /// <summary>Path to the webcam recording this segment draws from.</summary>
    public string? WebcamFilePath { get; init; }

    /// <summary>Whether the webcam overlay is shown for this segment's range.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-segment overlay style. When null the global/default style is used.</summary>
    public WebcamOverlayStyle? StyleOverride { get; set; }

    /// <summary>
    /// When true, the camera overlay animates from its normal position/size up to
    /// <b>cover the entire screen</b> at the start of the segment, holds fullscreen
    /// for the body, then animates back to its original position/size at the end.
    /// </summary>
    public bool FullscreenEnabled { get; set; }

    /// <summary>Eased ramp-up duration (in source time) from overlay to fullscreen.</summary>
    public static readonly TimeSpan FullscreenInDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>Eased ramp-down duration (in source time) from fullscreen back to overlay.</summary>
    public static readonly TimeSpan FullscreenOutDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Resolves the effective overlay style for this segment, layering any
    /// <see cref="StyleOverride"/> on top of a base style.
    /// </summary>
    public WebcamOverlayStyle ResolveStyle(WebcamOverlayStyle? baseStyle)
        => StyleOverride ?? baseStyle ?? new WebcamOverlayStyle();

    /// <summary>
    /// Computes the fullscreen interpolation factor in <c>[0,1]</c> for a given
    /// source-video time, where <c>0</c> = the normal overlay layout and <c>1</c> =
    /// covering the whole screen. Ramps in over <see cref="FullscreenInDuration"/> at
    /// the start, holds at <c>1</c>, then ramps out over <see cref="FullscreenOutDuration"/>
    /// before the end, easing both ramps with <see cref="CubicBezierEasing.EaseInOutCinematic"/>.
    /// When the segment is too short for both ramps they are scaled down proportionally
    /// so they never overlap. Returns <c>0</c> when fullscreen is disabled or the time
    /// is outside the segment.
    /// </summary>
    public float ComputeFullscreenFactor(TimeSpan sourceTime)
    {
        if (!FullscreenEnabled) return 0f;

        double dur = Duration.TotalSeconds;
        if (dur <= 0) return 0f;

        double t = (sourceTime - Start).TotalSeconds;
        if (t <= 0 || t >= dur) return 0f;

        double inR = FullscreenInDuration.TotalSeconds;
        double outR = FullscreenOutDuration.TotalSeconds;
        double total = inR + outR;
        if (total > dur && total > 0)
        {
            double scale = dur / total;
            inR *= scale;
            outR *= scale;
        }

        if (inR > 0 && t < inR)
            return CubicBezierEasing.EaseInOutCinematic((float)(t / inR));

        if (outR > 0 && t > dur - outR)
            return CubicBezierEasing.EaseInOutCinematic((float)((dur - t) / outR));

        return 1f;
    }
}

/// <summary>
/// Background fill modes available for a <see cref="TextSlideSegment"/>.
/// </summary>
public enum SlideBackgroundType
{
    Solid,
    Gradient,
    Image,
}

/// <summary>Horizontal text alignment for a text slide.</summary>
public enum SlideTextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// A text element overlaid on top of a <see cref="VideoSegment"/> for a time range.
/// Positions are normalized (0..1) relative to the output canvas.
/// </summary>
public record TextOverlay
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "Text";

    /// <summary>Horizontal position (0 = left, 0.5 = center, 1 = right).</summary>
    public double X { get; set; } = 0.5;

    /// <summary>Vertical position (0 = top, 0.5 = center, 1 = bottom).</summary>
    public double Y { get; set; } = 0.5;

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 48;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string TextColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#00000000"; // transparent default
    public TimeSpan StartTime { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3);
    public TextSlideAnimation Animation { get; set; } = TextSlideAnimation.FadeIn;
}

/// <summary>
/// Animation style for text slides and text overlays.
/// </summary>
public enum TextSlideAnimation
{
    None,

    // ── Whole-text opacity ──
    FadeIn,

    // ── Whole-text position ──
    SlideUp,
    SlideDown,
    SlideLeft,
    SlideRight,

    // ── Whole-text scale / cinematic ──
    ScalePop,      // scales in with an overshoot bounce
    ZoomBlurIn,    // zooms from large + blur, settling sharp (cinematic title)
    Reveal,        // left-to-right mask wipe reveal

    // ── Typewriter ──
    TypeWriter,
    TypewriterCaret, // typewriter with a blinking caret

    // ── Per-character kinetic typography ──
    CascadeFadeUp,   // characters fade + rise in, staggered
    CascadePop,      // characters scale-pop in, staggered (overshoot)
    Wave,            // characters bob continuously in a sine wave
    TrackingIn,      // letters expand from condensed to normal spacing + fade
    RotateIn,        // characters rotate/spin into place, staggered
    BounceIn,        // characters drop in with a bounce, staggered
}

/// <summary>
/// Configuration for a transition effect between two segments.
/// Applied as the "in-transition" of the incoming segment.
/// </summary>
public record TransitionConfig
{
    public TransitionType Type { get; init; } = TransitionType.None;
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Types of transition effects available between segments.
/// </summary>
public enum TransitionType
{
    None,
    Fade,
    CrossFade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Wipe,
}
