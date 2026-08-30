namespace Mixtri.Core.Processing;

using System.Text.Json.Serialization;

/// <summary>
/// Camera motion blur settings. Blur here follows the photographic
/// <b>shutter angle</b> model rather than an arbitrary blur filter: the virtual
/// shutter stays open for <see cref="ShutterAngleDegrees"/> / 360 of a frame, and
/// the frame is the average of the camera positions swept during that interval.
/// <para>
/// This describes <b>camera</b> motion (zoom ramps and panning), not motion of the
/// recorded content itself — there is only one captured frame per output frame, so
/// content movement inside the screen recording cannot be blurred.
/// </para>
/// </summary>
/// <remarks>
/// Serialized into the <c>.mixtri</c> manifest as part of
/// <see cref="CompositionConfig"/>. Every property is <c>init</c>-defaulted, so
/// projects saved before this record existed load with these defaults and no
/// schema bump is required.
/// </remarks>
public record MotionBlurSettings
{
    /// <summary>Master switch for all motion blur (cursor, zoom, and pan).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Master strength in <c>[0,1]</c>, scaling the shutter interval. Tuned low: with
    /// the 180° shutter this lands well under a full film shutter, which is
    /// deliberate — screen recordings are mostly high-contrast text and UI, where
    /// smear becomes objectionable far sooner than it does on filmed footage.
    /// </summary>
    public float Strength { get; init; } = 0.3f;

    /// <summary>Per-channel multiplier for blur of the cursor's own movement.</summary>
    public float CursorStrength { get; init; } = 1.0f;

    /// <summary>Per-channel multiplier for blur while the camera zooms in or out.</summary>
    public float ZoomStrength { get; init; } = 1.0f;

    /// <summary>Per-channel multiplier for blur while the camera pans at a fixed zoom.</summary>
    public float PanStrength { get; init; } = 1.0f;

    /// <summary>
    /// Photographic shutter angle. 180° (shutter open half of each frame) is the
    /// cinema standard; 360° doubles the smear, smaller angles sharpen it.
    /// </summary>
    public float ShutterAngleDegrees { get; init; } = 180f;

    /// <summary>
    /// Upper bound on sub-frame samples per blurred frame. The actual count adapts
    /// to how far the camera travels (see <see cref="SampleSpacingPixels"/>), so
    /// this only caps the cost of the fastest movements. Each sample is a full-canvas
    /// draw, so this is the single biggest lever on blur cost; when the camera moves
    /// further than these samples can cover smoothly, callers shorten the shutter
    /// rather than spending more draws or letting the samples band apart.
    /// </summary>
    public int MaxSamples { get; init; } = 6;

    /// <summary>
    /// Target spacing between consecutive sub-frame samples, in output pixels.
    /// Below roughly 2px the samples merge into a continuous smear; above it the
    /// blur visibly bands into discrete ghosts. When the camera travels further than
    /// <see cref="MaxSamples"/> can cover at this spacing, callers shorten the
    /// shutter rather than spreading the samples out.
    /// </summary>
    public float SampleSpacingPixels { get; init; } = 2.0f;

    /// <summary>
    /// Camera travel (in output pixels) below which a frame is left completely
    /// unblurred. Keeps the common case — a still or barely drifting camera —
    /// free of any extra draw calls, and avoids paying for a smear too short to
    /// actually perceive.
    /// </summary>
    public float MinBlurPixels { get; init; } = 2.5f;

    /// <summary>
    /// Fraction of a frame interval the virtual shutter is open, after applying
    /// <see cref="Strength"/>. Blur length = camera velocity (px/frame) × this.
    /// </summary>
    [JsonIgnore]
    public float ShutterFraction =>
        Math.Clamp(ShutterAngleDegrees / 360f, 0f, 1f) * Math.Clamp(Strength, 0f, 1f);

    /// <summary>
    /// Resolves how many sub-frame samples to average for a camera that travels
    /// <paramref name="travelPixels"/> output pixels across the shutter interval.
    /// Returns <c>1</c> (meaning "draw once, no blur") whenever the travel is below
    /// <see cref="MinBlurPixels"/>, or whenever <see cref="MaxSamples"/> leaves no
    /// room for a blur at all.
    /// </summary>
    public int ResolveSampleCount(float travelPixels)
    {
        if (!Enabled || ShutterFraction <= 0f) return 1;
        if (!float.IsFinite(travelPixels) || travelPixels < MinBlurPixels) return 1;

        // A blur needs at least two samples, so anything less means "no blur". This
        // is also a hard guard, not just semantics: MaxSamples is deserialized
        // straight from the .mixtri manifest with no validation, and passing a cap
        // below the minimum to Math.Clamp throws rather than widening the range —
        // which would take down the whole render path on a hand-edited project file.
        int cap = Math.Min(MaxSamples, 64);
        if (cap < 2) return 1;

        float spacing = Math.Max(0.25f, SampleSpacingPixels);
        int samples = (int)Math.Ceiling(travelPixels / spacing) + 1;
        return Math.Clamp(samples, 2, cap);
    }
}

/// <summary>
/// Continuous "living camera" motion applied while a zoom segment is active, so a
/// zoomed scene keeps breathing instead of freezing on a static crop during the hold.
/// <para>
/// Two layers, both driven by <b>progress through the zoom segment</b> rather than by
/// absolute time. That distinction matters: a zoom segment only lasts a few seconds,
/// so a free-running oscillator can happen to sit on a stationary point of its cycle
/// for the whole hold and leave the camera visibly parked. Segment-relative motion
/// traverses the same arc every time, so every zoom drifts.
/// </para>
/// <list type="bullet">
/// <item><b>Breathe</b> — a monotonic push-in across the segment (the Ken Burns "push").</item>
/// <item><b>Float</b> — an arc the camera traverses once across the segment.</item>
/// </list>
/// <para>
/// Both layers are windowed by how far the camera is actually zoomed in (see
/// <see cref="CameraDrift"/>), which fades the motion to exactly zero at 1× — that
/// keeps the un-zoomed frame rock steady and guarantees a zoom segment returns to
/// its exact starting framing instead of leaving a residual offset behind.
/// </para>
/// </summary>
public record CameraDriftSettings
{
    /// <summary>Master switch for continuous drift during zoom segments.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Overall multiplier applied to both drift layers. <c>1</c> is the tuned
    /// default; the UI exposes up to <c>4</c> so a deliberately theatrical push is
    /// available without having to edit the project file. Values above roughly 2
    /// stop being subtle, which is the point of the upper half of the range.
    /// </summary>
    public float Strength { get; init; } = 1.0f;

    /// <summary>
    /// Peak extra scale from the breathing layer, as a fraction of the segment's
    /// zoom depth, reached at the end of the push. The Ken Burns convention is a
    /// 2–8% move across a whole shot.
    /// </summary>
    public float ZoomAmplitude { get; init; } = 0.07f;

    /// <summary>
    /// Peak pan offset from the float layer, as a fraction of the visible viewport.
    /// Kept well under the 10% that starts reading as a deliberate camera move.
    /// </summary>
    public float PanAmplitude { get; init; } = 0.035f;

    /// <summary>
    /// How much of the pan arc is traversed across one segment, in turns. Well under
    /// a full turn on purpose: a complete cycle would return the camera to where it
    /// started, which reads as a wobble rather than as a drift.
    /// </summary>
    public float PanSweepCycles { get; init; } = 0.3f;

    /// <summary>
    /// Fraction of the remaining room between the viewport and the source edge that
    /// the float is allowed to consume. Staying below 1 matters: if the pan reached
    /// the edge, viewport clamping would stall the motion dead, which reads as a bug
    /// rather than as a camera settling.
    /// </summary>
    public float MaxSlackFraction { get; init; } = 0.7f;

    /// <summary>The tuned defaults used by any zoom segment that carries no explicit settings.</summary>
    public static readonly CameraDriftSettings Default = new();
}
