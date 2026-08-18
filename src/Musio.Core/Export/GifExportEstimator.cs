using Musio.Core.Processing;
using Musio.Core.Settings;

namespace Musio.Core.Export;

/// <summary>
/// Estimates how long an animated GIF export will take.
///
/// <para>GIF encoding cost is dominated by the WIC encoder's palette quantization, which
/// scales with pixel count and swamps everything else in the pipeline. Measured on the
/// shipping encoder path: ~14 ms/frame at 480x270, ~46 ms/frame at 960x540 and
/// ~390 ms/frame at 1920x1080 — i.e. roughly 90-190 ms per megapixel per frame. Setting
/// the per-frame GIF delay property costs nothing measurable, so there is no cheaper
/// encoder configuration to switch to; the only levers are resolution and frame count.</para>
///
/// <para>This exists so the export UI can warn about a long GIF up front rather than
/// leaving the user staring at a progress ring for several minutes.</para>
/// </summary>
public static class GifExportEstimator
{
    /// <summary>
    /// Conservative middle of the measured 90-190 ms/megapixel/frame range, which also
    /// absorbs the per-frame composition cost shared with the MP4 pipeline.
    /// </summary>
    private const double MillisecondsPerMegapixelPerFrame = 150.0;

    /// <summary>
    /// Estimates the wall-clock duration of a GIF export at the given output size,
    /// frame rate and clip length. Returns <see cref="TimeSpan.Zero"/> for degenerate
    /// inputs rather than throwing — a missing estimate simply suppresses the warning.
    /// </summary>
    public static TimeSpan Estimate(int width, int height, int fps, TimeSpan clipDuration)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || clipDuration <= TimeSpan.Zero)
            return TimeSpan.Zero;

        double megapixels = (double)width * height / 1_000_000.0;
        double frames = clipDuration.TotalSeconds * fps;
        double milliseconds = frames * megapixels * MillisecondsPerMegapixelPerFrame;

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// Resolves the GIF's output dimensions for the given source size and selected
    /// resolution, using the same sizing rules the exporter itself applies.
    /// </summary>
    public static (int Width, int Height) ResolveOutputSize(
        int sourceWidth, int sourceHeight, VideoResolution resolution) =>
        AspectRatioHelper.ComputeExportDimensions(sourceWidth, sourceHeight, resolution);

    /// <summary>
    /// Formats an estimate for display, e.g. "about 3 min" or "under a minute".
    /// Returns an empty string when there is no usable estimate.
    /// </summary>
    public static string FormatEstimate(TimeSpan estimate)
    {
        if (estimate <= TimeSpan.Zero)
            return string.Empty;

        if (estimate.TotalSeconds < 60)
            return "under a minute";

        if (estimate.TotalMinutes < 60)
            return $"about {(int)Math.Round(estimate.TotalMinutes)} min";

        int hours = (int)estimate.TotalHours;
        int minutes = estimate.Minutes;
        return $"about {hours} h {minutes} min";
    }
}
