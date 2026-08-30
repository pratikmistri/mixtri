namespace Mixtri.Core.Shell;

/// <summary>
/// Pure state machine describing how Mixtri moves between the Mini window,
/// the full app window, and the recording overlay.
/// </summary>
/// <remarks>
/// Deliberately free of UI types so the transition rules can be unit tested.
/// The shell coordinator in the app project owns the actual windows and simply
/// applies whatever state this class reports.
/// </remarks>
public sealed class AppShellStateMachine
{
    public AppShellStateMachine(AppShellState initialState = AppShellState.Mini)
    {
        CurrentState = initialState;
    }

    /// <summary>The surface that should currently be visible.</summary>
    public AppShellState CurrentState { get; private set; }

    /// <summary>
    /// Where the shell was when recording began, so a failed start or stop can
    /// put the user back where they were instead of stranding them.
    /// Null unless <see cref="CurrentState"/> is <see cref="AppShellState.Recording"/>.
    /// </summary>
    public AppShellState? RecordingOrigin { get; private set; }

    /// <summary>
    /// Applies <paramref name="trigger"/>. Returns true when the state actually
    /// changed, so callers can skip redundant window churn.
    /// </summary>
    public bool TryApply(AppShellTrigger trigger, out AppShellState newState)
    {
        var next = Resolve(CurrentState, trigger, RecordingOrigin);
        newState = next ?? CurrentState;

        if (next is null) return false;

        // The origin is captured on the way into Recording and consumed on the
        // way out, so a second recording can't inherit a stale origin.
        if (trigger == AppShellTrigger.RecordingStarted)
            RecordingOrigin = CurrentState;
        else if (CurrentState == AppShellState.Recording)
            RecordingOrigin = null;

        bool changed = next.Value != CurrentState;
        CurrentState = next.Value;
        return changed;
    }

    /// <summary>
    /// Resolves the target state, or null when the trigger is not valid in the
    /// current state and should be ignored.
    /// </summary>
    public static AppShellState? Resolve(
        AppShellState currentState,
        AppShellTrigger trigger,
        AppShellState? recordingOrigin = null)
    {
        return trigger switch
        {
            // Expand/Collapse are only meaningful between the two idle surfaces.
            // While recording, the pill overlay owns the screen.
            AppShellTrigger.Expand =>
                currentState == AppShellState.Mini ? AppShellState.Full : null,

            AppShellTrigger.Collapse =>
                currentState == AppShellState.Full ? AppShellState.Mini : null,

            AppShellTrigger.RecordingStarted =>
                currentState == AppShellState.Recording ? null : AppShellState.Recording,

            // A completed recording always hands off to the full app so the user
            // lands in the editor with their new take, regardless of where they
            // started from.
            AppShellTrigger.RecordingStopped =>
                currentState == AppShellState.Recording ? AppShellState.Full : null,

            AppShellTrigger.RecordingFailed =>
                currentState == AppShellState.Recording
                    ? recordingOrigin ?? AppShellState.Mini
                    : null,

            // The tray icon is the way back to Mini, but it must never yank a
            // recording off screen.
            AppShellTrigger.TrayActivated =>
                currentState == AppShellState.Recording ? null : AppShellState.Mini,

            _ => null,
        };
    }
}
