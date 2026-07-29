using Musio.Core.Shell;

namespace Musio.Core.Settings;

/// <summary>
/// Typed accessors for the shell (Mini vs Full window) preferences, layered on
/// top of <see cref="AppSettings"/> so everything still lands in the one
/// settings store.
/// </summary>
public sealed class ShellSettings
{
    public static ShellSettings Instance { get; } = new();

    private const string StartupModeKey = "Shell.StartupMode";
    private const string StartupModeSetKey = "Shell.StartupMode.HasBeenSet";
    private const string LastCaptureModeKey = "Shell.LastCaptureMode";

    private ShellSettings() { }

    /// <summary>
    /// Which window the app opens on launch. Installs that have never chosen a
    /// value get <see cref="StartupMode.Mini"/>.
    /// </summary>
    public StartupMode StartupMode
    {
        get => ResolveStartupMode(
            AppSettings.Instance.Get<string>(StartupModeKey, null!),
            HasChosenStartupMode);
        set
        {
            AppSettings.Instance.Set(StartupModeKey, value.ToString());
            AppSettings.Instance.Set(StartupModeSetKey, true);
        }
    }

    /// <summary>
    /// True once a startup mode has been written explicitly. Distinguishes
    /// "user picked Full" from "nothing on disk yet", which a missing key alone
    /// cannot do.
    /// </summary>
    public bool HasChosenStartupMode => AppSettings.Instance.Get(StartupModeSetKey, false);

    /// <summary>
    /// Capture mode the user last recorded with, so Mini opens ready to go.
    /// Stored as the enum name; callers parse it against their own enum.
    /// </summary>
    public string? LastCaptureMode
    {
        get
        {
            var raw = AppSettings.Instance.Get<string>(LastCaptureModeKey, null!);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        set => AppSettings.Instance.Set(LastCaptureModeKey, value ?? string.Empty);
    }

    /// <summary>
    /// Pure resolution of the persisted startup-mode value, split out so the
    /// first-launch default is unit testable without touching the settings store.
    /// </summary>
    public static StartupMode ResolveStartupMode(string? persisted, bool hasBeenSet)
    {
        if (!hasBeenSet) return StartupMode.Mini;

        return Enum.TryParse<StartupMode>(persisted, ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : StartupMode.Mini;
    }
}
