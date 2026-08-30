using System.Runtime.InteropServices;

namespace Mixtri.Core.Models;

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

/// <summary>
/// The active system cursor shape at the time a sample was recorded. Unknown or
/// custom application cursors are recorded as <see cref="Arrow"/>.
/// </summary>
public enum CursorShape : byte
{
    Arrow = 0,
    Hand = 1,
    IBeam = 2,
    ResizeWE = 3,
    ResizeNS = 4,
    ResizeNWSE = 5,
    ResizeNESW = 6,
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
    public CursorShape Shape;
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
    /// Returns the recorded sample nearest <paramref name="secondsFromStart"/>, or <c>null</c>
    /// when there are no samples to choose from.
    /// <para>
    /// Binary search rather than a linear scan: a long recording holds tens of thousands of
    /// samples, and the callers run on the UI thread while the user is interacting. This is safe
    /// because <see cref="Mixtri.Core.Capture.MouseHookRecorder"/> keeps
    /// <see cref="MouseSample.TimestampTicks"/> non-decreasing by construction — two threads
    /// append samples (the hook and the shape poller), so its append path clamps any
    /// out-of-order timestamp up to its predecessor.
    /// </para>
    /// </summary>
    public MouseSample? FindSampleNearest(double secondsFromStart)
    {
        if (Samples.Count == 0) return null;
        if (!double.IsFinite(secondsFromStart) || TickFrequency <= 0) return Samples[0];

        double offsetTicks = secondsFromStart * TickFrequency;

        // Past either end of the recording the answer is simply the nearest end sample, and
        // short-circuiting here also keeps the conversion below away from long overflow.
        if (offsetTicks <= 0) return Samples[0];
        if (offsetTicks >= Samples[^1].TimestampTicks - StartTimestampTicks) return Samples[^1];

        long targetTicks = StartTimestampTicks + (long)offsetTicks;

        // Lower bound: the first sample at or after the target.
        int lo = 0;
        int hi = Samples.Count - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (Samples[mid].TimestampTicks < targetTicks)
                lo = mid + 1;
            else
                hi = mid;
        }

        var atOrAfter = Samples[lo];
        if (lo == 0) return atOrAfter;

        // The sample before it can be closer; ties go to the earlier one, matching the
        // "first closest wins" behaviour of the linear scans this replaced.
        var before = Samples[lo - 1];
        return Math.Abs(before.TimestampTicks - targetTicks) <= Math.Abs(atOrAfter.TimestampTicks - targetTicks)
            ? before
            : atOrAfter;
    }

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
