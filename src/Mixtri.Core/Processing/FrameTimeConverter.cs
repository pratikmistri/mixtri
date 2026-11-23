namespace Mixtri.Core.Processing;

/// <summary>
/// Single named home for time↔frame-index conversions. Before this existed,
/// <c>(int)Math.Round(t * fps)</c> and <c>(int)(t * fps)</c> were inlined independently
/// across <c>FrameCompositor</c>, <c>SegmentFrameComposer</c>, <c>VideoEncoder</c>, and
/// <c>TimelineMapper</c>.
/// <para>
/// <b>The two policies are intentionally kept separate and must not be unified.</b> Each
/// call site chose Round or Floor/truncate for a reason specific to that site (e.g.
/// rounding a click timestamp onto the nearest cursor-path sample vs. flooring a duration
/// to the count of whole frames it fully covers). Converging one policy into the other
/// shifts a frame index by one at exact-boundary instants, which in this codebase means
/// preview and export can disagree by a frame — silently, since it is not exercised by
/// every test.
/// </para>
/// </summary>
public static class FrameTimeConverter
{
    /// <summary>
    /// Converts a time (in seconds) to the nearest frame index at <paramref name="fps"/>,
    /// via <see cref="Math.Round(double)"/>. Use where landing on the closest frame/sample
    /// boundary is what matters (e.g. mapping a click timestamp or a time offset onto a
    /// discrete sampled path).
    /// </summary>
    public static int TimeToFrameRounded(double timeSeconds, double fps) =>
        (int)Math.Round(timeSeconds * fps);

    /// <summary>
    /// Converts a time (in seconds) to a frame index by truncation (the same
    /// truncate-toward-zero behavior as a plain <c>(int)</c> cast — NOT
    /// <see cref="Math.Floor(double)"/>, which would differ for negative inputs). Use where
    /// the result must be the count of whole frames a duration fully covers, or the frame
    /// nominally still playing at that instant, without rounding up past what has actually
    /// elapsed.
    /// </summary>
    /// <remarks>
    /// Times reaching this method have usually been through <see cref="TimeSpan"/>, whose
    /// <c>FromSeconds(double)</c> truncates to 100 ns ticks: frame n at 30 fps becomes
    /// 0.0333333 s, and a bare cast of <c>0.0333333 * 30 = 0.999999</c> lands on frame n-1.
    /// That made about two thirds of export frames duplicate the previous one. A tolerance of
    /// <see cref="FrameBoundaryTolerance"/> frames (orders of magnitude above tick truncation,
    /// orders below any real sub-frame offset) snaps those back onto their boundary.
    /// </remarks>
    public static int TimeToFrameFloor(double timeSeconds, double fps) =>
        (int)(timeSeconds * fps + FrameBoundaryTolerance);

    /// <summary>
    /// Fraction of a frame by which a time may fall short of a frame boundary and still count
    /// as on it. One 100 ns tick at 120 fps is 1.2e-5 frames, so 1e-3 absorbs tick truncation
    /// with margin while staying far below a real offset (1 ms at 30 fps is 0.03 frames).
    /// </summary>
    public const double FrameBoundaryTolerance = 1e-3;

    /// <summary>
    /// How far before an EXCLUSIVE end time to sample for "the last frame before it" (a trim's
    /// out-point, the frozen outgoing side of a legacy transition). Must exceed
    /// <see cref="FrameBoundaryTolerance"/> at any frame rate — one tick does not, and would
    /// snap onto the boundary, i.e. the first frame past the end — yet stay under one frame:
    /// 1 ms is 0.03 frames at 30 fps and 0.24 at 240 fps.
    /// </summary>
    public static readonly TimeSpan InsideBoundaryInset = TimeSpan.FromMilliseconds(1);

    /// <summary>Converts a frame index at <paramref name="fps"/> back to a time in seconds.</summary>
    public static double FrameToTime(double frameIndex, double fps) => frameIndex / fps;
}
