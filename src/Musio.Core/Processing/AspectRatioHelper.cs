using Musio.Core.Settings;

namespace Musio.Core.Processing;

/// <summary>
/// Utility methods for aspect ratio calculations: ratio values, output dimensions,
/// crop rectangles, and display names.
/// </summary>
public static class AspectRatioHelper
{
    /// <summary>
    /// Returns the integer ratio components for the given <see cref="AspectRatio"/>.
    /// For <see cref="AspectRatio.Auto"/>, returns (0, 0) indicating no constraint.
    /// </summary>
    public static (int W, int H) GetRatio(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Landscape16x9 => (16, 9),
        AspectRatio.Portrait9x16 => (9, 16),
        AspectRatio.Square1x1 => (1, 1),
        AspectRatio.Classic4x3 => (4, 3),
        AspectRatio.Tall3x4 => (3, 4),
        _ => (0, 0),
    };

    /// <summary>
    /// Calculates the output pixel dimensions for a given source size, target aspect ratio,
    /// and target resolution. When the aspect ratio is <see cref="AspectRatio.Auto"/>,
    /// the output is sized to fit the resolution while preserving the source aspect ratio.
    /// </summary>
    public static (int Width, int Height) CalculateOutputDimensions(
        int sourceWidth, int sourceHeight, AspectRatio ratio, VideoResolution resolution)
    {
        var (maxW, maxH) = GetResolutionBounds(resolution);
        var (ratioW, ratioH) = GetRatio(ratio);

        if (ratioW == 0 || ratioH == 0)
        {
            // Auto: fit source aspect ratio within the resolution bounds
            return FitWithinBounds(sourceWidth, sourceHeight, maxW, maxH);
        }

        // Target aspect ratio
        double targetAr = (double)ratioW / ratioH;

        // Fit the target aspect ratio within resolution bounds
        int outW = maxW;
        int outH = (int)Math.Round(maxW / targetAr);

        if (outH > maxH)
        {
            outH = maxH;
            outW = (int)Math.Round(maxH * targetAr);
        }

        // Ensure even dimensions for video encoding
        outW = EnsureEven(outW);
        outH = EnsureEven(outH);

        return (outW, outH);
    }

    /// <summary>
    /// Calculates a center-crop rectangle to convert the source dimensions to the
    /// target aspect ratio. Returns (X, Y, Width, Height) in source pixel coordinates.
    /// For <see cref="AspectRatio.Auto"/>, the crop covers the full source.
    /// </summary>
    public static (int X, int Y, int Width, int Height) CalculateCropRect(
        int sourceWidth, int sourceHeight, AspectRatio targetRatio)
    {
        var (ratioW, ratioH) = GetRatio(targetRatio);

        if (ratioW == 0 || ratioH == 0)
            return (0, 0, sourceWidth, sourceHeight);

        double targetAr = (double)ratioW / ratioH;
        double sourceAr = (double)sourceWidth / sourceHeight;

        int cropW, cropH, offsetX, offsetY;

        if (sourceAr > targetAr)
        {
            // Source is wider — crop horizontally
            cropH = sourceHeight;
            cropW = (int)Math.Round(sourceHeight * targetAr);
            cropW = Math.Min(cropW, sourceWidth);
            offsetX = (sourceWidth - cropW) / 2;
            offsetY = 0;
        }
        else
        {
            // Source is taller — crop vertically
            cropW = sourceWidth;
            cropH = (int)Math.Round(sourceWidth / targetAr);
            cropH = Math.Min(cropH, sourceHeight);
            offsetX = 0;
            offsetY = (sourceHeight - cropH) / 2;
        }

        return (offsetX, offsetY, cropW, cropH);
    }

    /// <summary>
    /// Returns a human-readable display name for the given aspect ratio.
    /// </summary>
    public static string GetDisplayName(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Auto => "Auto",
        AspectRatio.Landscape16x9 => "16:9 Landscape",
        AspectRatio.Portrait9x16 => "9:16 Portrait",
        AspectRatio.Square1x1 => "1:1 Square",
        AspectRatio.Classic4x3 => "4:3 Classic",
        AspectRatio.Tall3x4 => "3:4 Tall",
        _ => ratio.ToString(),
    };

    private static (int MaxWidth, int MaxHeight) GetResolutionBounds(VideoResolution resolution) =>
        resolution switch
        {
            VideoResolution.HD720 => (1280, 720),
            VideoResolution.HD1080 => (1920, 1080),
            VideoResolution.QHD => (2560, 1440),
            VideoResolution.UHD4K => (3840, 2160),
            _ => (1920, 1080),
        };

    private static (int Width, int Height) FitWithinBounds(
        int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return (maxWidth, maxHeight);

        double sourceAr = (double)sourceWidth / sourceHeight;

        int outW = maxWidth;
        int outH = (int)Math.Round(maxWidth / sourceAr);

        if (outH > maxHeight)
        {
            outH = maxHeight;
            outW = (int)Math.Round(maxHeight * sourceAr);
        }

        return (EnsureEven(outW), EnsureEven(outH));
    }

    private static int EnsureEven(int value) => value % 2 == 0 ? value : value + 1;
}
