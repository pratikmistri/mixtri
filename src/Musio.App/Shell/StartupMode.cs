namespace Musio_App.Shell;

/// <summary>
/// How the app launches by default — see §3.1 of the Mini Mode spec.
/// Phase A only defines the enum; it is not yet read by activation.
/// </summary>
public enum StartupMode
{
    /// <summary>Open at top-center as the compact Mini Setup toolbar.</summary>
    Mini,

    /// <summary>Open centered as today's full app shell (default for existing installs).</summary>
    Full,
}
