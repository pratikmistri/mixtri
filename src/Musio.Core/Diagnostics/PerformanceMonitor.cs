using System.Diagnostics;

namespace Musio.Core.Diagnostics;

/// <summary>
/// Snapshot of current performance metrics.
/// </summary>
public record PerformanceReport
{
    public long TotalFramesCaptured { get; init; }
    public long DroppedFrames { get; init; }
    public double DropRate { get; init; }
    public double AvgCaptureTimeMs { get; init; }
    public double AvgCompositionTimeMs { get; init; }
    public double AvgEncodingTimeMs { get; init; }
    public double MaxCaptureTimeMs { get; init; }
    public long CurrentMemoryMB { get; init; }
    public long PeakMemoryMB { get; init; }
}

/// <summary>
/// Thread-safe performance monitor that tracks frame capture, composition,
/// encoding metrics and memory usage.
/// </summary>
public class PerformanceMonitor
{
    private long _totalFramesCaptured;
    private long _droppedFrames;

    private long _totalCaptureTimeTicks;
    private long _maxCaptureTimeTicks;
    private long _totalCompositionTimeTicks;
    private long _totalEncodingTimeTicks;
    private long _compositionCount;
    private long _encodingCount;

    private long _peakMemoryBytes;

    // --- Frame capture tracking ---

    /// <summary>Records a successful frame capture with its elapsed time.</summary>
    public void RecordFrameCapture(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _totalFramesCaptured);
        Interlocked.Add(ref _totalCaptureTimeTicks, elapsed.Ticks);

        // Lock-free max update
        long ticks = elapsed.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _maxCaptureTimeTicks);
            if (ticks <= current) break;
        } while (Interlocked.CompareExchange(ref _maxCaptureTimeTicks, ticks, current) != current);
    }

    /// <summary>Records a dropped frame.</summary>
    public void RecordFrameDrop()
    {
        Interlocked.Increment(ref _droppedFrames);
    }

    // --- Export performance tracking ---

    /// <summary>Records a frame composition elapsed time.</summary>
    public void RecordFrameComposition(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _compositionCount);
        Interlocked.Add(ref _totalCompositionTimeTicks, elapsed.Ticks);
    }

    /// <summary>Records a frame encoding elapsed time.</summary>
    public void RecordFrameEncoding(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _encodingCount);
        Interlocked.Add(ref _totalEncodingTimeTicks, elapsed.Ticks);
    }

    // --- Memory tracking ---

    /// <summary>Gets current managed + unmanaged working set in MB.</summary>
    public long GetCurrentMemoryUsageMB()
    {
        var bytes = Process.GetCurrentProcess().WorkingSet64;
        UpdatePeakMemory(bytes);
        return bytes / (1024 * 1024);
    }

    /// <summary>Gets the peak memory usage observed in MB.</summary>
    public long GetPeakMemoryUsageMB()
    {
        // Refresh peak from the OS value as well
        var osPeak = Process.GetCurrentProcess().PeakWorkingSet64;
        UpdatePeakMemory(osPeak);
        return Interlocked.Read(ref _peakMemoryBytes) / (1024 * 1024);
    }

    private void UpdatePeakMemory(long currentBytes)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _peakMemoryBytes);
            if (currentBytes <= current) break;
        } while (Interlocked.CompareExchange(ref _peakMemoryBytes, currentBytes, current) != current);
    }

    // --- Reporting ---

    /// <summary>Returns a snapshot of all collected metrics.</summary>
    public PerformanceReport GetReport()
    {
        var captured = Interlocked.Read(ref _totalFramesCaptured);
        var dropped = Interlocked.Read(ref _droppedFrames);
        var totalCaptureTicks = Interlocked.Read(ref _totalCaptureTimeTicks);
        var maxCaptureTicks = Interlocked.Read(ref _maxCaptureTimeTicks);
        var compCount = Interlocked.Read(ref _compositionCount);
        var compTicks = Interlocked.Read(ref _totalCompositionTimeTicks);
        var encCount = Interlocked.Read(ref _encodingCount);
        var encTicks = Interlocked.Read(ref _totalEncodingTimeTicks);

        return new PerformanceReport
        {
            TotalFramesCaptured = captured,
            DroppedFrames = dropped,
            DropRate = captured + dropped > 0
                ? (double)dropped / (captured + dropped)
                : 0.0,
            AvgCaptureTimeMs = captured > 0
                ? TimeSpan.FromTicks(totalCaptureTicks / captured).TotalMilliseconds
                : 0.0,
            AvgCompositionTimeMs = compCount > 0
                ? TimeSpan.FromTicks(compTicks / compCount).TotalMilliseconds
                : 0.0,
            AvgEncodingTimeMs = encCount > 0
                ? TimeSpan.FromTicks(encTicks / encCount).TotalMilliseconds
                : 0.0,
            MaxCaptureTimeMs = TimeSpan.FromTicks(maxCaptureTicks).TotalMilliseconds,
            CurrentMemoryMB = GetCurrentMemoryUsageMB(),
            PeakMemoryMB = GetPeakMemoryUsageMB()
        };
    }

    /// <summary>Resets all counters to zero.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalFramesCaptured, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _totalCaptureTimeTicks, 0);
        Interlocked.Exchange(ref _maxCaptureTimeTicks, 0);
        Interlocked.Exchange(ref _totalCompositionTimeTicks, 0);
        Interlocked.Exchange(ref _totalEncodingTimeTicks, 0);
        Interlocked.Exchange(ref _compositionCount, 0);
        Interlocked.Exchange(ref _encodingCount, 0);
        Interlocked.Exchange(ref _peakMemoryBytes, 0);
    }
}
