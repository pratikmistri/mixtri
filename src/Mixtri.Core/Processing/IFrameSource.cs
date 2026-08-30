using Microsoft.Graphics.Canvas;

namespace Mixtri.Core.Processing;

/// <summary>
/// Where a <see cref="VideoFrameReader"/> is pulling its frames from.
/// </summary>
public enum FrameSourceKind
{
    /// <summary>Loose JPEGs in a session's <c>.frames/</c> scratch directory.</summary>
    CapturedJpeg,

    /// <summary>Decoded on demand from the session's finalized <c>video.mp4</c>.</summary>
    EncodedVideo,
}

/// <summary>
/// Supplies decoded frames by index for a single recording source.
/// </summary>
/// <remarks>
/// Two implementations exist. <see cref="JpegFrameSource"/> reads the loose JPEGs that
/// <c>VideoWriter</c> writes during capture; it is the fastest path but those files are
/// transient and get deleted once the MP4 is finalized. <see cref="Mp4FrameSource"/>
/// decodes the finalized MP4 and is what keeps a project editable for its whole life.
/// </remarks>
public interface IFrameSource : IDisposable
{
    /// <summary>Total number of frames addressable by <see cref="LoadFrameAsync"/>.</summary>
    int FrameCount { get; }

    /// <summary>Frame width in pixels, or 0 when unknown.</summary>
    int Width { get; }

    /// <summary>Frame height in pixels, or 0 when unknown.</summary>
    int Height { get; }

    /// <summary>Which backing store this source reads from.</summary>
    FrameSourceKind Kind { get; }

    /// <summary>
    /// Decodes the frame at <paramref name="frameIndex"/>, or returns null when the
    /// index is out of range or the frame could not be decoded. The caller owns the
    /// returned bitmap and must dispose it.
    /// </summary>
    Task<CanvasBitmap?> LoadFrameAsync(int frameIndex);
}

/// <summary>
/// Controls how an encoded frame source is opened.
/// </summary>
public sealed record FrameSourceOptions
{
    public static FrameSourceOptions FullResolution { get; } = new();

    public static FrameSourceOptions CreatePreview(int maxWidth, int maxHeight) => new()
    {
        MaxWidth = maxWidth,
        MaxHeight = maxHeight,
        SeekAttempts = 1,
        SeekAttemptTimeout = TimeSpan.FromMilliseconds(400),
        EnableSeekRecovery = false,
    };

    public int MaxWidth { get; init; }
    public int MaxHeight { get; init; }
    public int SeekAttempts { get; init; } = 3;
    public TimeSpan SeekAttemptTimeout { get; init; } = TimeSpan.FromMilliseconds(600);
    public bool EnableSeekRecovery { get; init; } = true;
}
