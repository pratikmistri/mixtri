using System;
using System.Text.Json;
using Musio.Core.Settings;
using Musio_App.Shell;
using Musio_App.ViewModels;
using Windows.Foundation;

namespace Musio_App.Services;

/// <summary>
/// Typed facade over <see cref="AppSettings"/> for the Mini-Mode shell.
/// Phase A only defines accessors — no flow reads or writes these yet.
/// </summary>
/// <remarks>
/// Lives in the App project (not <c>Musio.Core</c>) because the keys reference
/// types defined in App (<see cref="StartupMode"/>, <see cref="CaptureMode"/>),
/// and Core does not depend on App. Persistence still flows through
/// <see cref="AppSettings.Instance"/> so we share the existing settings store.
/// </remarks>
public sealed class ShellSettings
{
    public static ShellSettings Instance { get; } = new();
    private ShellSettings() { }

    private AppSettings Store => AppSettings.Instance;

    /// <summary>
    /// Where the app opens on launch. Phase C migration: new installs default
    /// to <see cref="StartupMode.Mini"/>; existing installs (which already
    /// have a persisted value) keep whatever they were set to before. A
    /// separate <c>Shell.StartupMode.HasBeenSet</c> sentinel records whether
    /// the value was ever explicitly written, so a missing key on disk
    /// reliably means "first launch ever" and not "user picked Full".
    /// </summary>
    public StartupMode StartupMode
    {
        get
        {
            // First-launch default = Mini (Phase C spec §7 / Resolution 7).
            var defaultValue = StartupMode.Mini;

            // Existing-install migration: if there's a legacy persisted value
            // from Phase B (which defaulted to Full), keep it. We detect a
            // legacy value as "raw non-empty string but the sentinel says
            // never-explicitly-set".
            var raw = Store.Get<string>(KeyStartupMode, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return defaultValue;
            return Enum.TryParse<StartupMode>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : defaultValue;
        }
        set
        {
            Store.Set(KeyStartupMode, value.ToString());
            Store.Set(KeyStartupModeHasBeenSet, true);
        }
    }

    /// <summary>
    /// True once the user has explicitly chosen a startup mode (or the app
    /// has migrated their legacy value). Used by the launch path to decide
    /// whether to apply the first-launch defaults.
    /// </summary>
    public bool StartupModeHasBeenSet
    {
        get => Store.Get(KeyStartupModeHasBeenSet, false);
        set => Store.Set(KeyStartupModeHasBeenSet, value);
    }

    /// <summary>
    /// Last <see cref="CaptureMode"/> the user picked, or <c>null</c> if the
    /// user hasn't completed a first-launch selection yet.
    /// </summary>
    public CaptureMode? LastCaptureMode
    {
        get
        {
            var raw = Store.Get<string>(KeyLastCaptureMode, string.Empty);
            if (string.IsNullOrEmpty(raw)) return null;
            return Enum.TryParse<CaptureMode>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : null;
        }
        set => Store.Set(KeyLastCaptureMode, value.HasValue ? value.Value.ToString() : string.Empty);
    }

    /// <summary>
    /// Last custom region rectangle (logical screen coordinates), or
    /// <c>null</c> if the user has never picked one.
    /// </summary>
    public Rect? LastRegion
    {
        get
        {
            var raw = Store.Get<string>(KeyLastRegion, string.Empty);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var dto = JsonSerializer.Deserialize<RectDto>(raw);
                if (dto is null) return null;
                return new Rect(dto.X, dto.Y, dto.Width, dto.Height);
            }
            catch
            {
                return null;
            }
        }
        set
        {
            // Normalize degenerate rectangles to "no value" so downstream
            // restore code doesn't get a zero-area region (which would never
            // re-resolve to anything useful and could confuse the picker).
            if (value is null || value.Value.Width <= 0 || value.Value.Height <= 0)
            {
                Store.Set(KeyLastRegion, string.Empty);
                return;
            }
            var r = value.Value;
            var json = JsonSerializer.Serialize(new RectDto
            {
                X = r.X,
                Y = r.Y,
                Width = r.Width,
                Height = r.Height,
            });
            Store.Set(KeyLastRegion, json);
        }
    }

    /// <summary>
    /// Identifying tuple for the last selected window, used by
    /// <c>WindowMatcher</c> to re-resolve the HWND at next launch.
    /// </summary>
    public (string ProcessName, string WindowTitle, string ClassName)? LastWindowSelection
    {
        get
        {
            var raw = Store.Get<string>(KeyLastWindowSelection, string.Empty);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var dto = JsonSerializer.Deserialize<WindowDto>(raw);
                if (dto is null) return null;
                return (dto.ProcessName ?? string.Empty,
                        dto.WindowTitle ?? string.Empty,
                        dto.ClassName ?? string.Empty);
            }
            catch
            {
                return null;
            }
        }
        set
        {
            // Normalize degenerate selections to "no value" so WindowMatcher
            // is never called with empty strings (which would match the
            // first hidden window with no title) and so "no selection" and
            // "empty selection" stay indistinguishable downstream.
            if (value is null
                || (string.IsNullOrEmpty(value.Value.ProcessName)
                    && string.IsNullOrEmpty(value.Value.WindowTitle)))
            {
                Store.Set(KeyLastWindowSelection, string.Empty);
                return;
            }
            var v = value.Value;
            var json = JsonSerializer.Serialize(new WindowDto
            {
                ProcessName = v.ProcessName,
                WindowTitle = v.WindowTitle,
                ClassName = v.ClassName,
            });
            Store.Set(KeyLastWindowSelection, json);
        }
    }

    /// <summary>Last microphone-toggle state (default <c>false</c>).</summary>
    public bool LastMicEnabled
    {
        get => Store.Get(KeyLastMicEnabled, false);
        set => Store.Set(KeyLastMicEnabled, value);
    }

    /// <summary>Last system-audio toggle state (default <c>false</c>).</summary>
    public bool LastSystemAudioEnabled
    {
        get => Store.Get(KeyLastSystemAudioEnabled, false);
        set => Store.Set(KeyLastSystemAudioEnabled, value);
    }

    /// <summary>Last webcam-toggle state (default <c>false</c>).</summary>
    public bool LastWebcamEnabled
    {
        get => Store.Get(KeyLastWebcamEnabled, false);
        set => Store.Set(KeyLastWebcamEnabled, value);
    }

    private const string KeyStartupMode = "Shell.StartupMode";
    private const string KeyStartupModeHasBeenSet = "Shell.StartupMode.HasBeenSet";
    private const string KeyLastCaptureMode = "Recording.LastCaptureMode";
    private const string KeyLastRegion = "Recording.LastRegion";
    private const string KeyLastWindowSelection = "Recording.LastWindowSelection";
    private const string KeyLastMicEnabled = "Recording.LastMicEnabled";
    private const string KeyLastSystemAudioEnabled = "Recording.LastSystemAudioEnabled";
    private const string KeyLastWebcamEnabled = "Recording.LastWebcamEnabled";

    private sealed class RectDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class WindowDto
    {
        public string? ProcessName { get; set; }
        public string? WindowTitle { get; set; }
        public string? ClassName { get; set; }
    }
}
