using Mixtri.Core.Capture;

namespace Mixtri.Core.Processing;

/// <summary>A source-video range containing sustained text-entry activity.</summary>
public readonly record struct TypingActivityRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Converts low-level keyboard events into conservative text-entry bursts suitable for
/// automatic timeline acceleration.
/// </summary>
public static class TypingActivityDetector
{
    public const int MinimumKeyCount = 3;
    public const double MaximumInterKeyGapSeconds = 1.25;
    public const double LeadPaddingSeconds = 0.20;
    public const double TrailPaddingSeconds = 0.30;
    public const double MinimumRangeSeconds = 0.40;

    public static IReadOnlyList<TypingActivityRange> Detect(
        IReadOnlyList<KeyPressEvent> events,
        long recordingStartTicks,
        double tickFrequency,
        double recordingToVideoOffsetSeconds,
        TimeSpan videoDuration)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (tickFrequency <= 0 || videoDuration <= TimeSpan.Zero)
            return [];

        var keyTimes = events
            .Where(IsTextEditingKey)
            .Select(e => MapTimestampToVideoTime(
                e.TimestampTicks,
                recordingStartTicks,
                tickFrequency,
                recordingToVideoOffsetSeconds))
            .Where(t => t >= 0 && t <= videoDuration.TotalSeconds)
            .OrderBy(t => t)
            .ToList();

        if (keyTimes.Count < MinimumKeyCount)
            return [];

        var ranges = new List<TypingActivityRange>();
        int burstStart = 0;
        for (int i = 1; i <= keyTimes.Count; i++)
        {
            bool burstEnded = i == keyTimes.Count
                || keyTimes[i] - keyTimes[i - 1] > MaximumInterKeyGapSeconds;
            if (!burstEnded) continue;

            AddBurst(keyTimes, burstStart, i, videoDuration, ranges);
            burstStart = i;
        }

        return ranges;
    }

    public static double MapTimestampToVideoTime(
        long timestampTicks,
        long recordingStartTicks,
        double tickFrequency,
        double recordingToVideoOffsetSeconds)
    {
        if (tickFrequency <= 0) return double.NaN;
        return (timestampTicks - recordingStartTicks) / tickFrequency
            - recordingToVideoOffsetSeconds;
    }

    private static void AddBurst(
        IReadOnlyList<double> keyTimes,
        int startIndex,
        int endIndex,
        TimeSpan videoDuration,
        List<TypingActivityRange> ranges)
    {
        if (endIndex - startIndex < MinimumKeyCount)
            return;

        double start = Math.Max(0, keyTimes[startIndex] - LeadPaddingSeconds);
        double end = Math.Min(
            videoDuration.TotalSeconds,
            keyTimes[endIndex - 1] + TrailPaddingSeconds);
        if (end - start < MinimumRangeSeconds)
            return;

        var range = new TypingActivityRange(
            TimeSpan.FromSeconds(start),
            TimeSpan.FromSeconds(end));

        if (ranges.Count > 0 && range.Start <= ranges[^1].End)
        {
            ranges[^1] = ranges[^1] with { End = TimeSpan.FromTicks(
                Math.Max(ranges[^1].End.Ticks, range.End.Ticks)) };
        }
        else
        {
            ranges.Add(range);
        }
    }

    private static bool IsTextEditingKey(KeyPressEvent key)
    {
        if (!key.IsDown || key.IsCtrl || key.IsAlt || key.IsWin)
            return false;

        int vk = key.VirtualKeyCode;
        return vk is
            0x08 or // Backspace
            0x09 or // Tab / indentation
            0x0D or // Enter
            0x20 or // Space
            0x2E    // Delete
            || vk is >= 0x30 and <= 0x39  // Number row
            || vk is >= 0x41 and <= 0x5A  // Letters
            || vk is >= 0x60 and <= 0x6F  // Numpad and operators
            || vk is >= 0xBA and <= 0xC0  // OEM punctuation
            || vk is >= 0xDB and <= 0xDE; // OEM brackets/quotes
    }
}
