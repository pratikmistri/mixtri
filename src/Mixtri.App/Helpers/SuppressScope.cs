namespace Mixtri_App.Helpers;

/// <summary>
/// Sets a suppress-event <c>bool</c> flag to true for the scope's lifetime and unconditionally
/// resets it to false when the scope is disposed — including when the scope's body throws.
/// Replaces the repeated
/// <c>_suppressXEvents = true; try { ... } finally { _suppressXEvents = false; }</c> pattern:
/// <code>
/// using var _ = SuppressScope.Enter(ref _suppressCursorEvents);
/// SyncCursorControlsToConfig(cursor);
/// </code>
/// Deliberately resets to <c>false</c> rather than restoring the flag's prior value — matching
/// every existing call site exactly. Nested use of the same flag is not a scenario in this
/// codebase today, and save/restore semantics would change behavior if that ever changes.
/// </summary>
internal readonly ref struct SuppressScope
{
    private readonly ref bool _flag;

    private SuppressScope(ref bool flag)
    {
        _flag = ref flag;
        _flag = true;
    }

    public static SuppressScope Enter(ref bool flag) => new(ref flag);

    public void Dispose() => _flag = false;
}
