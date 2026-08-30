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
    public static int TimeToFrameFloor(double timeSeconds, double fps) =>
        (int)(timeSeconds * fps);

    /// <summary>Converts a frame index at <paramref name="fps"/> back to a time in seconds.</summary>
    public static double FrameToTime(double frameIndex, double fps) => frameIndex / fps;
}
