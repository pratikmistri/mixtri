namespace Musio.Core.Timeline;

using System.Text.Json.Serialization;
using Musio.Core.Processing;
using Windows.Foundation;

/// <summary>
/// Base class for all segments on the output timeline.
/// The timeline is an ordered sequence of segments; each segment
/// occupies a contiguous time range on the output.
/// </summary>
/// <remarks>
/// The <c>$kind</c> discriminators are part of the on-disk <c>.musio</c> format. Renaming
/// one silently breaks every project file already saved with it.
/// <para>
/// A retired <c>VideoSegment.TextOverlays</c> property (and its <c>TextOverlay</c> record)
/// once existed here and was removed when text overlays moved to
/// <see cref="TimelineModel.TextOverlays"/>. That was safe to drop outright rather than
/// migrate: nothing in the app ever wrote to it — no producer, and its renderer entry point
/// had no call sites — so no saved project can carry overlay data in that shape. Old files
/// do still contain the serialized empty array, which deserialization ignores as an unknown
/// member; <c>MusioPackageTests.Open_ToleratesLegacyPerSegmentTextOverlays</c> pins that,
/// so opting <see cref="MusioPackage.JsonOptions"/> into strict member handling would fail
/// there first rather than in the field.
/// </para>
/// </remarks>
[JsonDerivedType(typeof(VideoSegment), "video")]
[JsonDerivedType(typeof(TextSlideSegment), "textSlide")]
[JsonDerivedType(typeof(CameraSegment), "camera")]
[JsonDerivedType(typeof(TextOverlaySegment), "textOverlay")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
public abstract record TimelineSegment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Video-track lane for full-frame segments: 0 is the contiguous base track; higher
    /// tracks are absolute-time overlays so inserts can cover instead of ripple-editing.
    /// </summary>
    public int TrackIndex { get; set; }

    /// <summary>True when this segment lives on an absolute overlay track rather than the reflowed base track.</summary>
    [JsonIgnore]
    public bool IsOverlayTrack => TrackIndex > 0;

    /// <summary>Position on the output timeline where this segment starts.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>Duration of this segment on the output timeline.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Position on the output timeline where this segment ends.</summary>
    [JsonIgnore]
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

    /// <summary>
    /// What this segment's recorded audio should do. <see cref="Muted"/> applies at any
    /// speed; the other two only differ once <see cref="SpeedFactor"/> is not 1.0.
    /// </summary>
    public SegmentAudioMode AudioMode { get; set; } = SegmentAudioMode.TimeStretch;
}

/// <summary>
/// What a <see cref="VideoSegment"/>'s recorded audio does.
/// </summary>
/// <remarks>
/// <para>
/// The only real choice is whether the segment's audio plays at all. When it does, it is
/// re-timed to match the picture (pitch preserved — see <c>WsolaTimeStretcher</c>), so it
/// stays in sync however the segment is sped up or slowed down. A user who wants the audio at
/// its original rate detaches it instead (<c>DetachSegmentAudioOperation</c>), which turns it
/// into a free-standing block they can move and trim — a strictly better answer than a mode
/// that silently truncated the audio at the segment boundary.
/// </para>
/// <para>
/// <b><see cref="TimeStretch"/> is the zero value</b> so projects saved before this property
/// existed open with picture and sound together.
/// </para>
/// <para>
/// These values are serialized <b>by name</b> (<c>MusioPackage.JsonOptions</c> registers a
/// <c>JsonStringEnumConverter</c>), so no member may be removed or renamed — a saved project
/// referencing it would fail to open. That is why <see cref="Native"/> is still here.
/// </para>
/// </remarks>
public enum SegmentAudioMode
{
    /// <summary>
    /// Re-time the audio to the segment's output duration with a WSOLA time-stretch
    /// (see <c>Musio.Core.Audio.WsolaTimeStretcher</c>), preserving pitch. The default, and
    /// what every audible segment does.
    /// </summary>
    TimeStretch = 0,

    /// <summary>
    /// <b>Legacy — behaves exactly like <see cref="TimeStretch"/> and is no longer offered.</b>
    /// Meant "play the audio at its native rate, cut at the segment boundary". It was removed
    /// as a user-facing choice once audio could be detached: detaching gives the same
    /// original-rate audio AND lets it be positioned, instead of silently discarding whatever
    /// did not fit inside the segment. Retained only because the on-disk format persists this
    /// enum by name.
    /// </summary>
    Native = 1,

    /// <summary>Drop this segment's recorded audio entirely — the segment plays silent.</summary>
    Muted = 2,
}

/// <summary>
/// A full-screen text/title card segment (not backed by a video file).
/// </summary>
public record TextSlideSegment : TimelineSegment
{
    private const double AutomaticTextRampSeconds = 0.6;
    private const double AutomaticTextRampDurationFraction = 0.45;

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

    /// <summary>
    /// Offset from the segment start at which text begins animating in; callers may set
    /// a rough drag result and let the resolve accessors clamp it defensively.
    /// </summary>
    public TimeSpan TextInStart { get; set; }

    /// <summary>
    /// Length of the text entrance ramp. Null preserves the legacy automatic 0.6s ramp,
    /// clamped to 45% of the segment duration.
    /// </summary>
    public TimeSpan? TextInDuration { get; set; }

    /// <summary>
    /// Offset from the segment start at which text has finished animating out. Null means
    /// the segment end, matching every saved project that predates editable windows.
    /// </summary>
    public TimeSpan? TextOutEnd { get; set; }

    /// <summary>
    /// Length of the text exit ramp. Null preserves the legacy automatic 0.6s ramp,
    /// clamped to 45% of the segment duration.
    /// </summary>
    public TimeSpan? TextOutDuration { get; set; }

    /// <summary>
    /// Resolves the clamped text-window start. Clamping is total and ordered: duration is
    /// first made non-negative, <see cref="TextInStart"/> is clamped into that span, then
    /// <see cref="TextOutEnd"/> is clamped no earlier than the resolved start.
    /// </summary>
    public TimeSpan ResolveTextInStart() => ResolveTextWindow().InStart;

    /// <summary>
    /// Resolves the entrance ramp after the window is clamped. Negative ramp requests become
    /// zero; if both ramps cannot fit, they are scaled down together so neither overlaps.
    /// </summary>
    public TimeSpan ResolveTextInDuration() => ResolveTextWindow().InDuration;

    /// <summary>
    /// Resolves the clamped text-window end. Null keeps the legacy whole-slide window, while
    /// inverted user input is clamped to the resolved start rather than throwing mid-render.
    /// </summary>
    public TimeSpan ResolveTextOutEnd() => ResolveTextWindow().OutEnd;

    /// <summary>
    /// Resolves the exit ramp after the window is clamped, using the same proportional
    /// shrink as the entrance ramp so short or inverted windows remain renderable.
    /// </summary>
    public TimeSpan ResolveTextOutDuration() => ResolveTextWindow().OutDuration;

    private (TimeSpan InStart, TimeSpan InDuration, TimeSpan OutEnd, TimeSpan OutDuration) ResolveTextWindow()
    {
        var duration = Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration;
        var inStart = Clamp(TextInStart, TimeSpan.Zero, duration);
        var outEnd = Clamp(TextOutEnd ?? duration, inStart, duration);
        var window = outEnd - inStart;

        var inDuration = ClampNonNegative(TextInDuration ?? AutomaticTextRamp(duration));
        var outDuration = ClampNonNegative(TextOutDuration ?? AutomaticTextRamp(duration));

        if (window <= TimeSpan.Zero)
            return (inStart, TimeSpan.Zero, outEnd, TimeSpan.Zero);

        if (inDuration > window) inDuration = window;
        if (outDuration > window) outDuration = window;

        double totalTicks = inDuration.Ticks + (double)outDuration.Ticks;
        if (totalTicks > window.Ticks)
        {
            double scale = window.Ticks / totalTicks;
            long inTicks = (long)(inDuration.Ticks * scale);
            inDuration = TimeSpan.FromTicks(inTicks);
            outDuration = TimeSpan.FromTicks(window.Ticks - inTicks);
        }

        return (inStart, inDuration, outEnd, outDuration);
    }

    private static TimeSpan AutomaticTextRamp(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Min(AutomaticTextRampSeconds, duration.TotalSeconds * AutomaticTextRampDurationFraction));
    }

    private static TimeSpan ClampNonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
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
    /// When true, the camera overlay animates between its normal position/size and
    /// covering the entire screen during this segment. See <see cref="FullscreenMode"/>
    /// for the specific animation.
    /// </summary>
    public bool FullscreenEnabled { get; set; }

    /// <summary>How the fullscreen animation behaves while <see cref="FullscreenEnabled"/>.</summary>
    public CameraFullscreenMode FullscreenMode { get; set; } = CameraFullscreenMode.Highlight;

    /// <summary>Eased ramp-up duration (in source time) from overlay to fullscreen.</summary>
    public static readonly TimeSpan FullscreenInDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>Eased ramp-down duration (in source time) from fullscreen back to overlay.</summary>
    public static readonly TimeSpan FullscreenOutDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Duration (in source time) of the <see cref="CameraFullscreenMode.Reveal"/>
    /// shrink from fullscreen down to the overlay at the end of the segment.
    /// </summary>
    public static readonly TimeSpan FullscreenRevealDuration = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// Duration (in source time) of the overlay fade-in at the segment start and
    /// fade-out at the segment end, so the camera doesn't pop in/out abruptly.
    /// </summary>
    public static readonly TimeSpan AppearDuration = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// Resolves the effective overlay style for this segment, layering any
    /// <see cref="StyleOverride"/> on top of a base style.
    /// </summary>
    public WebcamOverlayStyle ResolveStyle(WebcamOverlayStyle? baseStyle)
        => StyleOverride ?? baseStyle ?? new WebcamOverlayStyle();

    /// <summary>
    /// Computes the fullscreen interpolation factor in <c>[0,1]</c> for a given
    /// source-video time, where <c>0</c> = the normal overlay layout and <c>1</c> =
    /// covering the whole screen. The shape of the curve depends on
    /// <see cref="FullscreenMode"/>:
    /// <list type="bullet">
    /// <item><see cref="CameraFullscreenMode.Highlight"/>: ramps overlay→fullscreen over
    /// <see cref="FullscreenInDuration"/>, holds, then ramps back over
    /// <see cref="FullscreenOutDuration"/> (both ramps scale down for short segments).</item>
    /// <item><see cref="CameraFullscreenMode.Reveal"/>: holds fullscreen then eases down to
    /// the overlay over <see cref="FullscreenRevealDuration"/> at the end, revealing the video.</item>
    /// </list>
    /// Returns <c>0</c> when fullscreen is disabled or the time is outside the segment.
    /// </summary>
    public float ComputeFullscreenFactor(TimeSpan sourceTime)
    {
        if (!FullscreenEnabled) return 0f;

        double dur = Duration.TotalSeconds;
        if (dur <= 0) return 0f;

        double t = (sourceTime - Start).TotalSeconds;
        if (t < 0 || t >= dur) return 0f;

        if (FullscreenMode == CameraFullscreenMode.Reveal)
        {
            double revealR = Math.Min(FullscreenRevealDuration.TotalSeconds, dur);
            if (revealR <= 0) return 1f;
            double revealStart = dur - revealR;
            // Hold fullscreen for the body, then ease down to the overlay at the end,
            // revealing the video underneath.
            if (t <= revealStart) return 1f;
            return CubicBezierEasing.EaseInOutCinematic((float)((dur - t) / revealR));
        }

        // Highlight: overlay -> fullscreen -> overlay.
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

    /// <summary>
    /// Computes the overlay opacity in <c>[0,1]</c> for a given source-video time so
    /// the camera fades in at the segment start and fades out at the end instead of
    /// popping. The fades use <see cref="AppearDuration"/> (scaled down for very short
    /// segments) and ease out for a snappy but smooth appearance. The
    /// <paramref name="fadeIn"/>/<paramref name="fadeOut"/> flags let callers suppress a
    /// fade at a boundary shared with an adjacent camera segment (to avoid a dip/flash)
    /// or where the camera starts covering the whole screen (to avoid a fade from black).
    /// Returns <c>0</c> outside the segment.
    /// </summary>
    public float ComputeAppearOpacity(TimeSpan sourceTime, bool fadeIn = true, bool fadeOut = true)
    {
        double dur = Duration.TotalSeconds;
        if (dur <= 0) return 1f;

        double t = (sourceTime - Start).TotalSeconds;
        if (t < 0 || t >= dur) return 0f;

        double a = AppearDuration.TotalSeconds;
        if (a * 2 > dur) a = dur / 2.0;
        if (a <= 0) return 1f;

        if (fadeIn && t < a)
            return CubicBezierEasing.EaseOut((float)(t / a));
        if (fadeOut && t > dur - a)
            return CubicBezierEasing.EaseOut((float)((dur - t) / a));

        return 1f;
    }

    /// <summary>
    /// True when this segment begins covering the whole screen, so a fade-in would
    /// just read as a fade from black rather than an overlay appearing over the video.
    /// </summary>
    public bool StartsFullscreen => FullscreenEnabled && FullscreenMode == CameraFullscreenMode.Reveal;
}

/// <summary>
/// The fullscreen animation styles for a <see cref="CameraSegment"/>.
/// </summary>
public enum CameraFullscreenMode
{
    /// <summary>Overlay grows to fullscreen, holds, then shrinks back to the overlay.</summary>
    Highlight,

    /// <summary>Holds fullscreen then shrinks to the overlay at the end, revealing the video underneath.</summary>
    Reveal,
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
/// Where a <see cref="TextOverlaySegment"/>'s text box is anchored on the output canvas.
/// Every value except <see cref="Custom"/> hugs the named edge/corner (inset by
/// <see cref="TextOverlaySegment.MarginFraction"/>) regardless of output size, so overlays
/// stay put across different export resolutions. <see cref="Custom"/> is used once the
/// user drags the overlay to a specific spot on the preview.
/// </summary>
public enum TextOverlayAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,

    /// <summary>Freely positioned via X/Y (set when the user drags on the preview).</summary>
    Custom,
}

/// <summary>
/// How a <see cref="TextOverlaySegment"/>'s background is drawn behind its text.
/// Every mode is clipped to the text box itself (never the full frame) so the
/// recording underneath continues to show through everywhere else.
/// </summary>
public enum TextOverlayBackground
{
    /// <summary>No background; the text is drawn directly over the video.</summary>
    None,

    /// <summary>A flat, semi-transparent fill behind the text.</summary>
    Solid,

    /// <summary>A blurred sample of the video behind the text, plus a legibility tint.</summary>
    Blur,

    /// <summary>A directional gradient scrim fading from the text box edge.</summary>
    GradientScrim,

    /// <summary>An outline/drop-shadow around the text glyphs instead of a filled box.</summary>
    OutlineShadow,

    /// <summary>A thin accent bar along one side of the text box.</summary>
    AccentBar,
}

/// <summary>Direction a <see cref="TextOverlayBackground.GradientScrim"/> fades from.</summary>
public enum ScrimDirection { Bottom, Top, Left, Right }

/// <summary>Which side of the text box an <see cref="TextOverlayBackground.AccentBar"/> is drawn on.</summary>
public enum AccentSide { Left, Right, Top, Bottom }

/// <summary>
/// A time-ranged animated text overlay segment living on its own track, drawn on top of
/// the video rather than replacing it (unlike <see cref="TextSlideSegment"/>). Like
/// <see cref="CameraSegment"/>, its range is expressed in <b>source-video time</b>
/// (<see cref="TimelineSegment.Start"/>/<see cref="TimelineSegment.Duration"/> are reused
/// as the source in/out range) so it stays aligned with the recording and survives video
/// reorder/trim through the same source↔output mapping. The overlay is shown only while
/// the playhead's source time is inside an <see cref="Enabled"/> segment. Its background is
/// always clipped to the text box, so the recording continues to show through everywhere else.
/// </summary>
public record TextOverlaySegment : TimelineSegment
{
    public string Text { get; set; } = "Text";

    /// <summary>Whether the overlay is shown for this segment's range.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which recording's source-time space this overlay was authored against. Null means
    /// the primary recording. Mirrors <c>ZoomKeyframe.SourceVideoFilePath</c> so the same
    /// per-recording ownership rule (see <c>SegmentFrameComposer.BelongsToSource</c>)
    /// applies to overlays.
    /// </summary>
    public string? SourceVideoFilePath { get; init; }

    /// <summary>
    /// Reuses the existing <see cref="TextSlideAnimation"/> enum; one animation drives both
    /// the entrance and the matching exit (the renderer already works this way for slides).
    /// </summary>
    public TextSlideAnimation Animation { get; set; } = TextSlideAnimation.FadeIn;

    // ── Placement ──

    /// <summary>Which edge/corner (or <see cref="TextOverlayAnchor.Custom"/>) the text box hugs.</summary>
    public TextOverlayAnchor Anchor { get; set; } = TextOverlayAnchor.BottomCenter;

    /// <summary>Normalized horizontal centre (0..1). Authoritative only when <see cref="Anchor"/> is <see cref="TextOverlayAnchor.Custom"/>.</summary>
    public double X { get; set; } = 0.5;

    /// <summary>Normalized vertical centre (0..1). Authoritative only when <see cref="Anchor"/> is <see cref="TextOverlayAnchor.Custom"/>.</summary>
    public double Y { get; set; } = 0.85;

    /// <summary>
    /// Width of the overlay's box as a fraction of the output width. The box is an explicit
    /// rectangle rather than something that hugs the measured text: that makes wrapping and
    /// overflow predictable, lets the box be resized directly on the preview, and — because
    /// the geometry is then pure arithmetic — guarantees the editor's drag/resize region and
    /// the renderer's box agree exactly instead of drifting apart through two separate text
    /// measurements.
    /// </summary>
    public double WidthFraction { get; set; } = 0.6;

    /// <summary>
    /// Height of the overlay's box as a fraction of the output height. See
    /// <see cref="WidthFraction"/> for why the box is explicit. Text is wrapped to the box
    /// width and centred vertically within this height.
    /// </summary>
    public double HeightFraction { get; set; } = 0.14;

    /// <summary>Inset from the frame edge used by every non-<see cref="TextOverlayAnchor.Custom"/> anchor.</summary>
    public double MarginFraction { get; set; } = 0.06;

    // ── Typography ──
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 42;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string TextColor { get; set; } = "#FFFFFF";
    public SlideTextAlignment TextAlignment { get; set; } = SlideTextAlignment.Center;

    // ── Background (clipped to the text box so the video shows through) ──
    public TextOverlayBackground Background { get; set; } = TextOverlayBackground.Solid;
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>Opacity of the background fill/tint, in 0..1.</summary>
    public double BackgroundOpacity { get; set; } = 0.55;

    /// <summary>Corner radius of the text box, in output px at 1080p-ish scale.</summary>
    public double CornerRadius { get; set; } = 12;

    /// <summary>Padding around the text as a fraction of <see cref="FontSize"/>.</summary>
    public double PaddingScale { get; set; } = 0.35;

    // Blur background
    /// <summary>Gaussian blur sigma applied to the video sample behind the text.</summary>
    public double BlurAmount { get; set; } = 12;

    /// <summary>Legibility tint opacity drawn over the blur.</summary>
    public double BlurTintOpacity { get; set; } = 0.25;

    // Gradient scrim background
    public ScrimDirection ScrimDirection { get; set; } = ScrimDirection.Bottom;

    /// <summary>Strength of the gradient scrim, in 0..1.</summary>
    public double ScrimStrength { get; set; } = 0.7;

    // Outline / shadow background
    public double OutlineWidth { get; set; } = 2;
    public string OutlineColor { get; set; } = "#000000";

    /// <summary>Strength of the drop shadow, in 0..1.</summary>
    public double ShadowStrength { get; set; } = 0.6;

    // Accent bar background
    public string AccentColor { get; set; } = "#0078D4";
    public double AccentThickness { get; set; } = 5;
    public AccentSide AccentSide { get; set; } = AccentSide.Left;

    /// <summary>
    /// The overlay's box in output pixels: the single source of truth for its geometry,
    /// shared by the renderer (which draws the background and lays the text out inside it)
    /// and by the editor (which positions the drag/resize region over it). Both callers go
    /// through here so the on-screen box and the interactive handles cannot drift apart —
    /// they previously each measured the text themselves and disagreed.
    /// The box is clamped to stay inside the frame.
    /// </summary>
    public Rect ComputeBox(int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return default;

        double wf = Math.Clamp(WidthFraction, 0.02, 1.0);
        double hf = Math.Clamp(HeightFraction, 0.02, 1.0);

        double boxW = wf * frameWidth;
        double boxH = hf * frameHeight;

        var (nx, ny) = ResolveCenter(Anchor, X, Y, MarginFraction, wf, hf);

        double left = Math.Clamp(nx * frameWidth - boxW / 2, 0, Math.Max(0, frameWidth - boxW));
        double top = Math.Clamp(ny * frameHeight - boxH / 2, 0, Math.Max(0, frameHeight - boxH));

        return new Rect(left, top, boxW, boxH);
    }

    /// <summary>
    /// Resolves the normalized (0..1) centre of the overlay's text box for a given anchor.
    /// For <see cref="TextOverlayAnchor.Custom"/> the stored X/Y are returned unchanged (clamped
    /// into range); every other anchor is derived from <paramref name="margin"/> so the overlay
    /// hugs the requested edge/corner regardless of output size. <paramref name="boxWidthFraction"/>/
    /// <paramref name="boxHeightFraction"/> are the text box's normalized extents, needed so an
    /// edge-anchored box is inset by half its own size plus the margin (otherwise the box would
    /// hang off the frame). The result is always clamped into <c>[0,1]</c>, including when the box
    /// is larger than the frame (which would otherwise produce a nonsensical centre).
    /// </summary>
    public static (double X, double Y) ResolveCenter(
        TextOverlayAnchor anchor, double x, double y, double margin,
        double boxWidthFraction, double boxHeightFraction)
    {
        if (anchor == TextOverlayAnchor.Custom)
            return (Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));

        double halfW = boxWidthFraction / 2.0;
        double halfH = boxHeightFraction / 2.0;

        double resolvedX = anchor switch
        {
            TextOverlayAnchor.TopLeft or TextOverlayAnchor.MiddleLeft or TextOverlayAnchor.BottomLeft
                => margin + halfW,
            TextOverlayAnchor.TopRight or TextOverlayAnchor.MiddleRight or TextOverlayAnchor.BottomRight
                => 1.0 - margin - halfW,
            _ => 0.5,
        };

        double resolvedY = anchor switch
        {
            TextOverlayAnchor.TopLeft or TextOverlayAnchor.TopCenter or TextOverlayAnchor.TopRight
                => margin + halfH,
            TextOverlayAnchor.BottomLeft or TextOverlayAnchor.BottomCenter or TextOverlayAnchor.BottomRight
                => 1.0 - margin - halfH,
            _ => 0.5,
        };

        return (Math.Clamp(resolvedX, 0.0, 1.0), Math.Clamp(resolvedY, 0.0, 1.0));
    }
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

    /// <summary>
    /// Easing curve applied to the raw linear progress before it is handed to the renderer.
    /// Defaults to <see cref="TransitionEasing.EaseInOut"/> for new transitions. Note this has
    /// no bearing on the legacy <see cref="SlideTransitions"/> crossfade fallback, which is
    /// intentionally <see cref="TransitionEasing.Linear"/> to stay pixel-identical to the
    /// pre-existing hardcoded behaviour (see <see cref="TransitionResolver"/>).
    /// </summary>
    public TransitionEasing Easing { get; init; } = TransitionEasing.EaseInOut;
}

/// <summary>
/// Types of transition effects available between segments.
/// </summary>
/// <remarks>
/// These values are serialized <em>by name</em> (see <c>Musio.Core.Projects.MusioPackage.JsonOptions</c>,
/// which registers a <c>JsonStringEnumConverter</c>), so every value here is part of the on-disk
/// <c>.musio</c> format. Existing members must never be reordered or renamed — doing so silently
/// corrupts every saved project that references them. New members may only be appended at the end.
/// </remarks>
public enum TransitionType
{
    None,
    Fade,
    CrossFade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,

    /// <summary>Legacy name for a left-to-right wipe. Kept as-is (not renamed to "WipeLeft")
    /// because the enum value is part of the on-disk format.</summary>
    Wipe,

    // --- Appended members below. Keep appending; never reorder or rename anything above. ---

    /// <summary>Right-to-left wipe.</summary>
    WipeRight,
    /// <summary>Bottom-to-top wipe.</summary>
    WipeUp,
    /// <summary>Top-to-bottom wipe.</summary>
    WipeDown,
    /// <summary>Like <see cref="Fade"/> but dissolves through white instead of black.</summary>
    DipToWhite,
    /// <summary>Scale + blur push-through effect.</summary>
    ZoomBlur,
    WhipPanLeft,
    WhipPanRight,
    PushLeft,
    PushRight,
    PushUp,
    PushDown,
    /// <summary>RGB channel split with slice displacement.</summary>
    Glitch,
}

/// <summary>
/// Easing curves available for transition progress. Determines how
/// <see cref="TransitionResolver.RawProgress"/> (linear 0..1 through the transition window) maps
/// to <see cref="TransitionResolver.EasedProgress"/> (what renderers should actually use).
/// </summary>
/// <remarks>
/// Serialized by name for the same reason as <see cref="TransitionType"/> — append only, never
/// reorder or rename existing members.
/// </remarks>
public enum TransitionEasing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}
