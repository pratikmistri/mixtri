using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Windows.Media.Editing;
using Windows.Storage;

namespace Mixtri.Core.Processing;

/// <summary>
/// A generated filmstrip: evenly spaced thumbnails covering a video's full duration.
/// </summary>
/// <param name="Thumbnails">
/// Thumbnails in time order. An entry is null when that instant could not be decoded.
/// The caller owns and must dispose them.
/// </param>
/// <param name="IntervalSeconds">Spacing between consecutive thumbnails.</param>
/// <param name="AspectRatio">Width / height of the source video.</param>
/// <param name="Duration">Total duration of the source video.</param>
public sealed record ThumbnailStrip(
    CanvasBitmap?[] Thumbnails,
    double IntervalSeconds,
    double AspectRatio,
    TimeSpan Duration);

/// <summary>
/// Extracts filmstrip thumbnails from a video file.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does NOT go through <see cref="VideoFrameReader"/>. That reader owns a
/// <c>MediaPlayer</c> positioned at a single point, so pulling sparse thumbnails from it
/// means a seek per thumbnail — measured at ~334 ms each with roughly a quarter returning
/// no frame at all, and competing with the preview's decoder for the same file.
/// </para>
/// <para>
/// <c>MediaComposition.GetThumbnailsAsync</c> is built for exactly this access pattern:
/// the same strip measured ~15 ms per thumbnail at 100 thumbnails with no failures, and it
/// needs no <c>MediaPlayer</c>, so the preview is unaffected.
/// </para>
/// </remarks>
public static class VideoThumbnailExtractor
{
    /// <summary>
    /// Generates an evenly spaced strip of thumbnails spanning <paramref name="videoFilePath"/>.
    /// </summary>
    /// <param name="targetHeight">Thumbnail height in pixels; width follows the aspect ratio.</param>
    /// <param name="maxCount">Upper bound on the number of thumbnails.</param>
    /// <param name="minIntervalSeconds">Smallest spacing between thumbnails.</param>
    /// <returns>The strip, or null when the video could not be read.</returns>
    public static async Task<ThumbnailStrip?> ExtractAsync(
        string videoFilePath,
        int targetHeight,
        CanvasDevice device,
        int maxCount = 300,
        double minIntervalSeconds = 0.5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoFilePath) || targetHeight <= 0)
            return null;

        try
        {
            var fileInfo = new FileInfo(videoFilePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                return null;

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoFilePath));

            // Guarded because a recording's MP4 can be absent or unfinalized — finalization
            // is intentionally non-fatal — and MediaClip throws rather than reporting it.
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var duration = clip.OriginalDuration;
            if (duration <= TimeSpan.Zero)
                return null;

            var props = clip.GetVideoEncodingProperties();
            double aspectRatio = props.Height > 0
                ? props.Width / (double)props.Height
                : 16.0 / 9.0;

            double totalSeconds = duration.TotalSeconds;
            double interval = Math.Max(minIntervalSeconds, totalSeconds / 200);
            int count = Math.Clamp((int)(totalSeconds / interval) + 1, 1, maxCount);

            var times = new List<TimeSpan>(count);
            for (int i = 0; i < count; i++)
            {
                // Clamp inside the clip: a timestamp at or past the end yields no frame.
                double t = Math.Min(i * interval, Math.Max(0, totalSeconds - 0.001));
                times.Add(TimeSpan.FromSeconds(t));
            }

            ct.ThrowIfCancellationRequested();

            var streams = await composition.GetThumbnailsAsync(
                times, 0, targetHeight, VideoFramePrecision.NearestFrame).AsTask(ct);

            var thumbnails = new CanvasBitmap?[count];
            bool flip = Mixtri.Core.Capture.RecordingMarker.NeedsVerticalFlip(videoFilePath);
            for (int i = 0; i < count && i < streams.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var loaded = await CanvasBitmap.LoadAsync(device, streams[i]);
                    thumbnails[i] = flip ? FlipVertically(loaded, device) : loaded;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VideoThumbnailExtractor] Thumbnail {i} failed to load: {ex.Message}");
                }
            }

            return new ThumbnailStrip(thumbnails, interval, aspectRatio, duration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoThumbnailExtractor] Failed for '{videoFilePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds a filmstrip from a session's captured JPEGs instead of its MP4.
    /// </summary>
    /// <remarks>
    /// The JPEGs survive exactly when MP4 finalization failed — which is the case the
    /// whole write-ahead design exists for, and precisely when
    /// <see cref="ExtractAsync"/> can return nothing. Without this the preview would play
    /// happily from the frames while the timeline showed a flat colour block.
    /// </remarks>
    /// <param name="fps">Recording FPS, used to turn frame indices into timestamps.</param>
    public static async Task<ThumbnailStrip?> ExtractFromCapturedFramesAsync(
        string videoFilePath,
        int fps,
        int targetHeight,
        CanvasDevice device,
        int maxCount = 300,
        double minIntervalSeconds = 0.5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoFilePath) || targetHeight <= 0 || fps <= 0)
            return null;

        var sessionFolder = Path.GetDirectoryName(Path.GetFullPath(videoFilePath));
        if (string.IsNullOrEmpty(sessionFolder))
            return null;

        using var source = JpegFrameSource.Open(sessionFolder, device);
        if (source is null || source.FrameCount == 0)
            return null;

        double totalSeconds = source.FrameCount / (double)fps;
        double interval = Math.Max(minIntervalSeconds, totalSeconds / 200);
        int count = Math.Clamp((int)(totalSeconds / interval) + 1, 1, maxCount);

        var thumbnails = new CanvasBitmap?[count];
        double aspectRatio = 16.0 / 9.0;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            int frameIndex = Math.Clamp(
                (int)(i * interval * fps), 0, source.FrameCount - 1);

            using var frame = await source.LoadFrameAsync(frameIndex).ConfigureAwait(false);
            if (frame is null)
                continue;

            double frameAspect = frame.Size.Height > 0
                ? frame.Size.Width / frame.Size.Height
                : aspectRatio;
            if (i == 0)
                aspectRatio = frameAspect;

            try
            {
                int width = Math.Max(1, (int)Math.Round(targetHeight * frameAspect));
                var scaled = Win2DUtils.CreateRenderTarget(device, width, targetHeight, 96, "video thumbnail");
                using (var ds = scaled.CreateDrawingSession())
                    ds.DrawImage(frame, new Windows.Foundation.Rect(0, 0, width, targetHeight));

                thumbnails[i] = scaled;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoThumbnailExtractor] JPEG thumbnail {i} failed: {ex.Message}");
            }
        }

        return new ThumbnailStrip(
            thumbnails, interval, aspectRatio, TimeSpan.FromSeconds(totalSeconds));
    }

    /// <summary>
    /// Returns a vertically mirrored copy of <paramref name="source"/> and disposes it.
    /// </summary>
    /// <remarks>
    /// Needed for recordings written by the pre-orientation-fix encoder. The preview
    /// applies the same correction when decoding, so without it the filmstrip and the
    /// package poster would be the only upside-down surfaces in the app.
    /// </remarks>
    private static CanvasBitmap FlipVertically(CanvasBitmap source, CanvasDevice device)
    {
        try
        {
            float height = (float)source.Size.Height;
            var flipped = Win2DUtils.CreateRenderTarget(
                device, (float)source.Size.Width, height, source.Dpi, "flipped video thumbnail");

            using (var ds = flipped.CreateDrawingSession())
            {
                ds.Transform =
                    System.Numerics.Matrix3x2.CreateScale(1, -1) *
                    System.Numerics.Matrix3x2.CreateTranslation(0, height);
                ds.DrawImage(source);
            }

            source.Dispose();
            return flipped;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoThumbnailExtractor] Flip failed: {ex.Message}");
            return source;
        }
    }
}
