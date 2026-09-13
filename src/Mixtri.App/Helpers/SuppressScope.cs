namespace Mixtri_App.Helpers;

/// <summary>
/// Sets a suppress-event <c>bool</c> flag to true for the scope's lifetime and unconditionally
/// restores its previous value when disposed, including when the scope's body throws.
/// Replaces the repeated
/// <c>_suppressXEvents = true; try { ... } finally { _suppressXEvents = false; }</c> pattern:
/// <code>
/// using var _ = SuppressScope.Enter(ref _suppressCursorEvents);
/// SyncCursorControlsToConfig(cursor);
/// </code>
/// Nested helpers leave an outer caller's suppression in effect.
/// </summary>
internal readonly ref struct SuppressScope
{
    private readonly ref bool _flag;
    private readonly bool _previousValue;

    private SuppressScope(ref bool flag)
    {
        _flag = ref flag;
        _previousValue = flag;
        _flag = true;
    }

    public static SuppressScope Enter(ref bool flag) => new(ref flag);

    public void Dispose() => _flag = _previousValue;
}
