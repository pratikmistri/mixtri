using Microsoft.UI.Xaml;

namespace Musio_App.Helpers;

/// <summary>
/// Wraps a <see cref="DispatcherTimer"/> to provide trailing-edge debounce, replacing the
/// hand-written "lazy-create timer → wire Tick to stop-then-callback → stop → start" blocks
/// scattered across <c>EditorPage</c>. One <see cref="Schedule"/> call restarts the full
/// interval; the timer always stops itself before invoking the callback, so a re-entrant
/// <see cref="Schedule"/> call from inside the callback behaves exactly like the pattern it
/// replaces. Must be created on and used from the UI thread only, as today.
/// </summary>
internal sealed class Debouncer
{
    private readonly DispatcherTimer _timer;

    public Debouncer(Action callback, int intervalMilliseconds = 200)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMilliseconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            callback();
        };
    }

    /// <summary>Restarts the debounce interval, deferring the callback until it next elapses.</summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Cancels any pending callback without invoking it.</summary>
    public void Stop() => _timer.Stop();
}
