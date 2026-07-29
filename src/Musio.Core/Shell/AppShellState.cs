namespace Musio.Core.Shell;

/// <summary>
/// Which shell surface currently owns the screen. Exactly one of these is
/// visible at a time, which is what stops the Mini window, the full app
/// window, and the recording pill from ever being shown together.
/// </summary>
public enum AppShellState
{
    /// <summary>The compact Mini window is visible.</summary>
    Mini,

    /// <summary>The full app window is visible.</summary>
    Full,

    /// <summary>A recording is in flight: both windows are hidden and only the pill overlay shows.</summary>
    Recording,
}

/// <summary>
/// Inputs that can move the shell between <see cref="AppShellState"/> values.
/// </summary>
public enum AppShellTrigger
{
    /// <summary>User pressed Expand on the Mini window.</summary>
    Expand,

    /// <summary>User pressed Collapse on the full app window.</summary>
    Collapse,

    /// <summary>A recording started successfully.</summary>
    RecordingStarted,

    /// <summary>A recording stopped and produced a project to hand off to the editor.</summary>
    RecordingStopped,

    /// <summary>Starting or stopping a recording failed, so the shell must go back where it came from.</summary>
    RecordingFailed,

    /// <summary>User clicked the system tray icon.</summary>
    TrayActivated,
}
