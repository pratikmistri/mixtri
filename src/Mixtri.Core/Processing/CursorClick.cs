using Mixtri.Core.Models;

namespace Mixtri.Core.Processing;

internal readonly record struct CursorClick(long TimestampTicks, int X, int Y, MouseButton Button, bool IsDown);

/// <summary>A borrowed, synchronous view of recorded events or reusable transformed click values.</summary>
internal readonly struct CursorClickSource
{
    private readonly List<ClickEvent>? _recorded;
    private readonly List<CursorClick>? _transformed;

    internal CursorClickSource(List<ClickEvent>? recorded) => _recorded = recorded;
    internal CursorClickSource(List<CursorClick> transformed) => _transformed = transformed;

    internal int Count => _transformed?.Count ?? _recorded?.Count ?? 0;

    internal CursorClick this[int index]
    {
        get
        {
            if (_transformed is not null) return _transformed[index];
            var click = _recorded![index];
            return new(click.TimestampTicks, click.X, click.Y, click.Button, click.IsDown);
        }
    }
}
