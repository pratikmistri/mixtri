using Musio_App.ViewModels;

namespace Musio_App.Shell;

internal sealed class AppShellStateMachine
{
    public AppShellStateMachine(AppShellState initialState)
    {
        CurrentState = initialState;
    }

    public AppShellState CurrentState { get; private set; }

    public AppShellState? OriginStateBeforeRecording { get; private set; }

    public bool IsPickerOpen { get; set; }

    public bool WasRecentlySummoned { get; set; }

    public AppShellState Record(CaptureMode _)
    {
        var previous = CurrentState;
        CurrentState = NextState(previous, AppShellEvent.RecordPressed, new()) ?? previous;
        OriginStateBeforeRecording = previous;
        return CurrentState;
    }

    public AppShellState StopSucceeded()
    {
        CurrentState = NextState(CurrentState, AppShellEvent.StopSucceeded, new(OriginStateBeforeRecording)) ?? CurrentState;
        OriginStateBeforeRecording = null;
        return CurrentState;
    }

    public AppShellState StopFailed()
    {
        CurrentState = NextState(CurrentState, AppShellEvent.StopFailed, new(OriginStateBeforeRecording)) ?? CurrentState;
        OriginStateBeforeRecording = null;
        return CurrentState;
    }

    public bool TryDismissMiniSetup()
    {
        if (CurrentState != AppShellState.MiniSetup) return false;
        if (IsPickerOpen) return false;
        if (!WasRecentlySummoned) return false;
        return true;
    }

    internal static AppShellState? NextState(
        AppShellState currentState,
        AppShellEvent shellEvent,
        AppShellTransitionContext context)
    {
        return shellEvent switch
        {
            AppShellEvent.RecordPressed => currentState == AppShellState.Full
                ? AppShellState.FullRecording
                : AppShellState.MiniRecording,

            AppShellEvent.StopSucceeded => AppShellState.Full,

            AppShellEvent.StopFailed => context.OriginStateBeforeRecording
                ?? (currentState == AppShellState.FullRecording ? AppShellState.Full : AppShellState.MiniSetup),

            AppShellEvent.MiniSetupExpand => AppShellState.Full,
            AppShellEvent.RecordingExpand => AppShellState.FullRecording,

            AppShellEvent.FullCollapse => currentState == AppShellState.FullRecording
                ? AppShellState.MiniRecording
                : AppShellState.MiniSetup,

            AppShellEvent.DockedPillCollapse => AppShellState.MiniRecording,

            AppShellEvent.EscDismiss => currentState == AppShellState.MiniSetup
                                      && !context.IsRecording
                                      && !context.IsPickerOpen
                                      && context.WasRecentlySummoned
                ? AppShellState.MiniSetup
                : null,

            _ => null,
        };
    }
}

internal enum AppShellEvent
{
    RecordPressed,
    StopSucceeded,
    StopFailed,
    MiniSetupExpand,
    RecordingExpand,
    FullCollapse,
    DockedPillCollapse,
    EscDismiss,
}

internal readonly record struct AppShellTransitionContext(
    AppShellState? OriginStateBeforeRecording = null,
    bool IsPickerOpen = false,
    bool IsRecording = false,
    bool WasRecentlySummoned = false);
