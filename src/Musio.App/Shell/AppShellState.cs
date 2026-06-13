namespace Musio_App.Shell;

/// <summary>
/// The four visual states the unified <c>AppShellWindow</c> can be in.
/// Drives chrome, sizing/positioning, presenter flags, capture-exclusion,
/// and which inner control (MiniSetup / RecordingPill / FullShell) is shown.
/// See Mini Mode spec §5.2.
/// </summary>
public enum AppShellState
{
    /// <summary>Compact top-center toolbar (default launch state).</summary>
    MiniSetup,

    /// <summary>Compact top-center pill shown while recording.</summary>
    MiniRecording,

    /// <summary>Today's full app shell (Nav + Frame + title bar), centered.</summary>
    Full,

    /// <summary>Full app shell while recording (capture-excluded, docked pill in title bar).</summary>
    FullRecording,
}
