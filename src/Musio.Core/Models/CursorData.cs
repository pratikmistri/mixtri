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
}
