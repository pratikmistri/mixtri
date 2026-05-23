using System.Runtime.InteropServices;

namespace Musio.Core.Models;

public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

public enum MouseEventKind : byte
{
    Move = 0,
    ButtonDown = 1,
    ButtonUp = 2,
    Scroll = 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MouseSample
{
    public long TimestampTicks;
    public int X;
    public int Y;
    public MouseEventKind EventKind;
    public MouseButton Button;
    public short ScrollDelta;
}

public record ClickEvent(long TimestampTicks, int X, int Y, MouseButton Button, bool IsDown);

public class MouseRecordingData
{
    public List<MouseSample> Samples { get; init; } = [];
    public List<ClickEvent> Clicks { get; init; } = [];
    public long StartTimestampTicks { get; init; }
    public long EndTimestampTicks { get; init; }
    public double TickFrequency { get; init; }

    public TimeSpan Duration => TickFrequency > 0
        ? TimeSpan.FromTicks(
            (long)((EndTimestampTicks - StartTimestampTicks) / TickFrequency * TimeSpan.TicksPerSecond))
        : TimeSpan.Zero;

    /// <summary>
    /// Returns a copy of <paramref name="data"/> with the stop-trigger click removed.
    /// The stop-trigger click is the last left-button-down event that occurred
    /// within 200 ms before <paramref name="stopRequestedTicks"/>. Its matching
    /// button-up event and corresponding button samples are also removed.
    /// Move and scroll samples are preserved.
    /// </summary>
    public static MouseRecordingData TrimStopClick(MouseRecordingData data, long stopRequestedTicks)
    {
        var samples = new List<MouseSample>(data.Samples);
        var clicks = new List<ClickEvent>(data.Clicks);
        long endTicks = stopRequestedTicks;

        // Find the last left-button-down click (the one that triggered stop).
        int lastDownIdx = -1;
        for (int i = clicks.Count - 1; i >= 0; i--)
        {
            if (clicks[i].IsDown && clicks[i].Button == MouseButton.Left)
            {
                lastDownIdx = i;
                break;
            }
        }

        if (lastDownIdx >= 0)
        {
            var stopClick = clicks[lastDownIdx];
            long threshold = (long)(data.TickFrequency * 0.2); // 200 ms

            if (stopClick.TimestampTicks >= stopRequestedTicks - threshold)
            {
                long cutoffTicks = stopClick.TimestampTicks;

                // Remove all click events at or after the stop click
                clicks.RemoveAll(c => c.TimestampTicks >= cutoffTicks);

                // Remove button-down / button-up samples at or after the stop click
                samples.RemoveAll(s => s.TimestampTicks >= cutoffTicks
                    && (s.EventKind == MouseEventKind.ButtonDown
                        || s.EventKind == MouseEventKind.ButtonUp));
            }
        }

        return new MouseRecordingData
        {
            Samples = samples,
            Clicks = clicks,
            StartTimestampTicks = data.StartTimestampTicks,
            EndTimestampTicks = endTicks,
            TickFrequency = data.TickFrequency,
        };
    }
}
