using System.Threading.Tasks;

namespace Musio_App.Controls;

/// <summary>
/// Surface that can be visually dimmed (e.g. opacity faded) while a picker /
/// overlay flow is taking place. Implementations should also restore
/// interactivity (hit-test) on <see cref="UndimAsync"/>.
/// </summary>
/// <remarks>
/// Phase A defines this interface and a default no-op so the picker service
/// can take a parameter of this type today. Concrete implementations on
/// <c>MiniSetupControl</c> land in Phase C (the dim-while-picking flow).
/// </remarks>
public interface IDimmable
{
    /// <summary>Begin the dim animation and disable hit-testing.</summary>
    Task DimAsync();

    /// <summary>Restore full opacity and re-enable hit-testing.</summary>
    Task UndimAsync();
}

/// <summary>
/// No-op <see cref="IDimmable"/> handy for "no toolbar to dim" call sites.
/// </summary>
public sealed class NoOpDimmable : IDimmable
{
    public static NoOpDimmable Instance { get; } = new();
    private NoOpDimmable() { }
    public Task DimAsync() => Task.CompletedTask;
    public Task UndimAsync() => Task.CompletedTask;
}
