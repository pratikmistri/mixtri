using Microsoft.Graphics.Canvas;
using Mixtri.Core.Diagnostics;
using Windows.Media.Editing;
using Windows.Storage;

namespace Mixtri.Core.Processing;

/// <summary>Evenly spaced source-time thumbnails. The caller owns the returned bitmaps.</summary>
public sealed record ThumbnailStrip(
    CanvasBitmap?[] Thumbnails,
    double IntervalSeconds,
    double AspectRatio,
    TimeSpan Duration);

/// <summary>
/// Uses a dedicated MediaComposition for sparse frame access, never the preview decoder.
/// Progressive consumers receive a four-frame overview, then bounded refinement batches.
/// </summary>
public static class VideoThumbnailExtractor
{
    public static Task<ThumbnailStrip?> ExtractAsync(
        string videoFilePath, int targetHeight, CanvasDevice device, int maxCount = 300,
        double minIntervalSeconds = 0.5, CancellationToken ct = default)
        => CollectAsync(publish => ExtractVideoBatchesAsync(videoFilePath, targetHeight, device,
            publish, progressive: false, maxCount, minIntervalSeconds, ct));

    /// <summary>
    /// The callback runs in the calling context. TakeFrames transfers ownership; untaken
    /// frames are disposed after the callback, including when it throws or work is cancelled.
    /// </summary>
    public static Task<bool> ExtractProgressivelyAsync(
        string videoFilePath, int targetHeight, CanvasDevice device, Action<ThumbnailBatch> publish,
        int maxCount = 300, double minIntervalSeconds = 0.5, CancellationToken ct = default)
        => ExtractVideoBatchesAsync(videoFilePath, targetHeight, device,
            AsAsync(publish), progressive: true, maxCount, minIntervalSeconds, ct);

    public static Task<bool> ExtractProgressivelyAsync(
        string videoFilePath, int targetHeight, CanvasDevice device, Func<ThumbnailBatch, Task> publish,
        int maxCount = 300, double minIntervalSeconds = 0.5, CancellationToken ct = default,
        IReadOnlyList<TimeSpan>? priorityTimes = null)
        => ExtractVideoBatchesAsync(videoFilePath, targetHeight, device,
            publish, progressive: true, maxCount, minIntervalSeconds, ct, priorityTimes);

    public static Task<ThumbnailStrip?> ExtractFromCapturedFramesAsync(
        string videoFilePath, int fps, int targetHeight, CanvasDevice device, int maxCount = 300,
        double minIntervalSeconds = 0.5, CancellationToken ct = default)
        => CollectAsync(publish => ExtractJpegBatchesAsync(videoFilePath, fps, targetHeight, device,
            publish, progressive: false, maxCount, minIntervalSeconds, ct));

    public static Task<bool> ExtractCapturedFramesProgressivelyAsync(
        string videoFilePath, int fps, int targetHeight, CanvasDevice device, Action<ThumbnailBatch> publish,
        int maxCount = 300, double minIntervalSeconds = 0.5, CancellationToken ct = default)
        => ExtractJpegBatchesAsync(videoFilePath, fps, targetHeight, device,
            AsAsync(publish), progressive: true, maxCount, minIntervalSeconds, ct);

    public static Task<bool> ExtractCapturedFramesProgressivelyAsync(
        string videoFilePath, int fps, int targetHeight, CanvasDevice device, Func<ThumbnailBatch, Task> publish,
        int maxCount = 300, double minIntervalSeconds = 0.5, CancellationToken ct = default)
        => ExtractJpegBatchesAsync(videoFilePath, fps, targetHeight, device,
            publish, progressive: true, maxCount, minIntervalSeconds, ct);

    private static Func<ThumbnailBatch, Task> AsAsync(Action<ThumbnailBatch> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        return batch => { publish(batch); return Task.CompletedTask; };
    }

    private static async Task<ThumbnailStrip?> CollectAsync(Func<Func<ThumbnailBatch, Task>, Task<bool>> extract)
    {
        ThumbnailStrip? strip = null;
        bool transferred = false;
        try
        {
            bool success = await extract(batch =>
            {
                strip ??= new(new CanvasBitmap?[batch.TotalCount],
                    batch.IntervalSeconds, batch.AspectRatio, batch.Duration);
                batch.TakeFrames().CopyTo(strip.Thumbnails, batch.StartIndex);
                return Task.CompletedTask;
            });
            if (!success) return null;
            transferred = true;
            return strip;
        }
        finally
        {
            if (!transferred && strip is not null)
                foreach (var frame in strip.Thumbnails) frame?.Dispose();
        }
    }

    private static async Task<bool> ExtractVideoBatchesAsync(
        string videoFilePath, int targetHeight, CanvasDevice device, Func<ThumbnailBatch, Task> publish,
        bool progressive, int maxCount, double minIntervalSeconds, CancellationToken ct,
        IReadOnlyList<TimeSpan>? priorityTimes = null)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(videoFilePath) || targetHeight <= 0) return false;
        MediaComposition? composition = null;
        bool anyFrame = false;
        try
        {
            var info = new FileInfo(videoFilePath);
            if (!info.Exists || info.Length == 0) return false;
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoFilePath));
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct);
            composition = new MediaComposition();
            composition.Clips.Add(clip);
            var duration = clip.OriginalDuration;
            if (duration <= TimeSpan.Zero) return false;
            var props = clip.GetVideoEncodingProperties();
            double aspect = props.Height > 0 ? props.Width / (double)props.Height : 16.0 / 9.0;
            var plan = ThumbnailSamplingPlan.Create(duration, maxCount, minIntervalSeconds);
            bool flip = Capture.RecordingMarker.NeedsVerticalFlip(videoFilePath);

            bool overviewPending = progressive && plan.Count > 4;
            for (int start = 0; start < plan.Count;)
            {
                ct.ThrowIfCancellationRequested();
                int[]? overview = overviewPending ? plan.OverviewIndices(priorityTimes) : null;
                int count = overview?.Length ?? plan.BatchCountAt(start, progressive);
                var indices = overview ?? Enumerable.Range(start, count).ToArray();
                var times = indices.Select(plan.TimeAt).ToList();
                // MediaComposition can corrupt a cold nonzero seek. Warm each batch at zero;
                // the extra image is discarded, so all published source times stay unchanged.
                int warmup = indices[0] > 0 ? 1 : 0;
                if (warmup != 0) times.Insert(0, TimeSpan.Zero);
                var streams = await composition.GetThumbnailsAsync(
                    times, 0, targetHeight, VideoFramePrecision.NearestFrame).AsTask(ct);
                var frames = new CanvasBitmap?[count];
                using var batch = new ThumbnailBatch(frames, start, plan.Count, plan.IntervalSeconds, aspect, duration, overview);
                try
                {
                    for (int i = 0; i < count && i + warmup < streams.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var loaded = await CanvasBitmap.LoadAsync(device, streams[i + warmup]).AsTask(ct);
                            frames[i] = flip ? FlipVertically(loaded, device) : loaded;
                            anyFrame = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            DiagLog.Write("Filmstrip", $"thumbnail {indices[i]} failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    foreach (var stream in streams) stream.Dispose();
                }
                ct.ThrowIfCancellationRequested();
                await publish(batch).ConfigureAwait(false);
                if (overviewPending) overviewPending = false;
                else start += count;
                if (progressive) await Task.Yield();
            }
            return anyFrame;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagLog.Write("Filmstrip", $"video extraction failed for '{videoFilePath}': {ex.Message}");
            return false;
        }
        finally
        {
            composition?.Clips.Clear();
        }
    }

    private static async Task<bool> ExtractJpegBatchesAsync(
        string videoFilePath, int fps, int targetHeight, CanvasDevice device, Func<ThumbnailBatch, Task> publish,
        bool progressive, int maxCount, double minIntervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(videoFilePath) || targetHeight <= 0 || fps <= 0) return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoFilePath));
        if (string.IsNullOrEmpty(folder)) return false;
        using var source = JpegFrameSource.Open(folder, device);
        if (source is null || source.FrameCount == 0) return false;
        var duration = TimeSpan.FromSeconds(source.FrameCount / (double)fps);
        var plan = ThumbnailSamplingPlan.Create(duration, maxCount, minIntervalSeconds);
        double aspect = 16.0 / 9.0;
        bool anyFrame = false;
        bool overviewPending = progressive && plan.Count > 4;
        for (int start = 0; start < plan.Count;)
        {
            ct.ThrowIfCancellationRequested();
            int[]? overview = overviewPending ? plan.OverviewIndices() : null;
            int count = overview?.Length ?? plan.BatchCountAt(start, progressive);
            var frames = new CanvasBitmap?[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int sample = overview is null ? start + i : overview[i];
                    int index = Math.Clamp((int)(sample * plan.IntervalSeconds * fps), 0, source.FrameCount - 1);
                    using var frame = await source.LoadFrameAsync(index);
                    if (frame is null) continue;
                    double frameAspect = frame.Size.Height > 0 ? frame.Size.Width / frame.Size.Height : aspect;
                    if (start + i == 0) aspect = frameAspect;
                    CanvasRenderTarget? scaled = null;
                    try
                    {
                        int width = Math.Max(1, (int)Math.Round(targetHeight * frameAspect));
                        scaled = Win2DUtils.CreateRenderTarget(device, width, targetHeight, 96, "video thumbnail");
                        using (var ds = scaled.CreateDrawingSession())
                            ds.DrawImage(frame, new Windows.Foundation.Rect(0, 0, width, targetHeight));
                        frames[i] = scaled;
                        scaled = null;
                        anyFrame = true;
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Write("Filmstrip", $"captured thumbnail {start + i} failed: {ex.Message}");
                    }
                    finally { scaled?.Dispose(); }
                }
                ct.ThrowIfCancellationRequested();
                using var batch = new ThumbnailBatch(frames, start, plan.Count, plan.IntervalSeconds, aspect, duration, overview);
                frames = []; // batch owns these until the callback explicitly takes them
                await publish(batch).ConfigureAwait(false);
            }
            finally
            {
                foreach (var frame in frames) frame?.Dispose();
            }
            if (overviewPending) overviewPending = false;
            else start += count;
            if (progressive) await Task.Yield();
        }
        return anyFrame;
    }

    private static CanvasBitmap FlipVertically(CanvasBitmap source, CanvasDevice device)
    {
        CanvasRenderTarget? flipped = null;
        try
        {
            float height = (float)source.Size.Height;
            flipped = Win2DUtils.CreateRenderTarget(
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
            flipped?.Dispose();
            DiagLog.Write("Filmstrip", $"legacy thumbnail flip failed: {ex.Message}");
            return source;
        }
    }
}
