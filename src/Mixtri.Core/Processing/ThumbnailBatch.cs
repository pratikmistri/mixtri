using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;

namespace Mixtri.Core.Processing;

/// <summary>Owns one contiguous filmstrip batch until the receiver takes its frames.</summary>
public sealed class ThumbnailBatch : IDisposable
{
    private CanvasBitmap?[]? _frames;
    private readonly int[]? _indices;
    public int StartIndex { get; }
    public int Count { get; }
    public int TotalCount { get; }
    public double IntervalSeconds { get; }
    public double AspectRatio { get; }
    public TimeSpan Duration { get; }
    public bool IsOverview => _indices is not null;
    public bool IsComplete => !IsOverview && StartIndex + Count == TotalCount;
    public int FrameIndexAt(int offset) => _indices is null ? StartIndex + offset : _indices[offset];

    internal ThumbnailBatch(CanvasBitmap?[] frames, int startIndex, int totalCount,
        double intervalSeconds, double aspectRatio, TimeSpan duration, int[]? indices = null)
    {
        _frames = frames;
        Count = frames.Length;
        StartIndex = startIndex;
        TotalCount = totalCount;
        IntervalSeconds = intervalSeconds;
        AspectRatio = aspectRatio;
        Duration = duration;
        _indices = indices;
    }

    public CanvasBitmap?[] TakeFrames()
        => Interlocked.Exchange(ref _frames, null)
            ?? throw new InvalidOperationException("Thumbnail batch ownership was already released.");

    public void Dispose()
    {
        var frames = Interlocked.Exchange(ref _frames, null);
        if (frames is not null)
            foreach (var frame in frames) frame?.Dispose();
    }
}

/// <summary>Source-time sampling stays identical regardless of how extraction is batched.</summary>
public readonly record struct ThumbnailSamplingPlan(int Count, double IntervalSeconds, double DurationSeconds)
{
    public static ThumbnailSamplingPlan Create(TimeSpan duration, int maxCount, double minIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (!double.IsFinite(minIntervalSeconds) || minIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(minIntervalSeconds));
        double seconds = duration.TotalSeconds;
        double interval = Math.Max(minIntervalSeconds, seconds / 200);
        return new(Math.Clamp((int)(seconds / interval) + 1, 1, maxCount), interval, seconds);
    }

    public TimeSpan TimeAt(int index)
        => TimeSpan.FromSeconds(Math.Min(index * IntervalSeconds, Math.Max(0, DurationSeconds - 0.001)));

    public int BatchCountAt(int startIndex, bool progressive)
        => Math.Min(Count - startIndex, progressive ? startIndex == 0 ? 4 : 16 : Count);

    public int[] OverviewIndices(IReadOnlyList<TimeSpan>? priorityTimes = null)
    {
        if (priorityTimes is { Count: > 0 })
        {
            int totalCount = Count;
            double interval = IntervalSeconds;
            var indices = priorityTimes.Select(time =>
                    Math.Clamp((int)(time.TotalSeconds / interval), 0, totalCount - 1))
                .Distinct().Order().ToArray();
            if (indices.Length <= 24) return indices;
            return Enumerable.Range(0, 24)
                .Select(i => indices[(int)Math.Round(i * (indices.Length - 1.0) / 23)]).ToArray();
        }
        int count = Math.Min(4, Count);
        int total = Count;
        return Enumerable.Range(0, count)
            .Select(i => count <= 1 ? 0 : (int)Math.Round(i * (total - 1.0) / (count - 1))).ToArray();
    }

    public static double LoadedSourceEnd(int completedCount, double intervalSeconds, double sourceDuration)
        => Math.Clamp(completedCount * intervalSeconds, 0, sourceDuration);
}

public static class ThumbnailReveal
{
    public const double DurationSeconds = 0.35;
    public static float Progress(double elapsedSeconds)
    {
        double t = Math.Clamp(elapsedSeconds / DurationSeconds, 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>GPU-only overview-to-detail blend; completed frames use the original bitmap directly.</summary>
    public static void Draw(CanvasDrawingSession session, CanvasBitmap frame, CanvasBitmap? previous,
        Rect destination, Rect source, float progress)
    {
        if (previous is not null && progress < 1)
        {
            using var blend = new ArithmeticCompositeEffect
            {
                Source1 = previous, Source2 = frame,
                Source1Amount = 1 - progress, Source2Amount = progress,
                MultiplyAmount = 0, Offset = 0
            };
            session.DrawImage(blend, destination, source);
        }
        else
        {
            session.DrawImage(frame, destination, source, progress);
        }
    }
}
