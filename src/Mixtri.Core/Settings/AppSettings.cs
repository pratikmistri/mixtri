using System.Collections.Concurrent;
using Windows.Storage;

namespace Mixtri.Core.Settings;

/// <summary>
/// Singleton settings manager that persists user preferences via ApplicationData.LocalSettings.
/// Falls back to an in-memory dictionary when running without package identity.
/// </summary>
public sealed class AppSettings
{
    private static readonly Lazy<AppSettings> _instance = new(() => new AppSettings());
    public static AppSettings Instance => _instance.Value;

    private readonly ApplicationDataContainer? _settings;
    // Concurrent: settings are read from render/worker threads as well as the UI thread.
    private readonly ConcurrentDictionary<string, object> _memoryStore = new();

    private AppSettings()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            // App may not have package identity — use in-memory store only
            _settings = null;
        }
    }

    public string Theme
    {
        get => Get(nameof(Theme), "System");
        set => Set(nameof(Theme), value);
    }

    public int DefaultFps
    {
        get => Get(nameof(DefaultFps), 30);
        set => Set(nameof(DefaultFps), value);
    }

    public string DefaultCaptureMode
    {
        get => Get(nameof(DefaultCaptureMode), "FullScreen");
        set => Set(nameof(DefaultCaptureMode), value);
    }

    public string DefaultSavePath
    {
        get => Get(nameof(DefaultSavePath),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        set => Set(nameof(DefaultSavePath), value);
    }

    public bool IsSystemAudioEnabled
    {
        get => Get(nameof(IsSystemAudioEnabled), true);
        set => Set(nameof(IsSystemAudioEnabled), value);
    }

    public bool IsMicEnabled
    {
        get => Get(nameof(IsMicEnabled), false);
        set => Set(nameof(IsMicEnabled), value);
    }

    /// <summary>
    /// Whether typed characters are written to the recording's keystroke log. Defaults to true
    /// (an absent stored value, as for existing installs, keeps the old behaviour). When false
    /// only modifier keys and shortcut combos are recorded.
    /// </summary>
    public bool RecordTypedText
    {
        get => Get(nameof(RecordTypedText), true);
        set => Set(nameof(RecordTypedText), value);
    }

    public bool IsWebcamEnabled
    {
        get => Get(nameof(IsWebcamEnabled), false);
        set => Set(nameof(IsWebcamEnabled), value);
    }

    public string WebcamDeviceId
    {
        get => Get(nameof(WebcamDeviceId), "");
        set => Set(nameof(WebcamDeviceId), value);
    }

    public VideoResolution DefaultExportResolution
    {
        get => GetEnum(nameof(DefaultExportResolution), VideoResolution.UHD4K);
        set => Set(nameof(DefaultExportResolution), value.ToString());
    }

    public VideoQuality DefaultExportQuality
    {
        get => GetEnum(nameof(DefaultExportQuality), VideoQuality.High);
        set => Set(nameof(DefaultExportQuality), value.ToString());
    }

    /// <summary>
    /// Bitrate of the intermediate MP4 written while recording. That file is the durable
    /// master a project stays editable from, so this trades disk against how much quality
    /// survives into later re-edits and exports.
    /// </summary>
    public CaptureQuality CaptureQuality
    {
        get => GetEnum(nameof(CaptureQuality), CaptureQuality.HighFidelity);
        set => Set(nameof(CaptureQuality), value.ToString());
    }

    /// <summary>
    /// True once the user has dismissed the one-time "Musio is now Mixtri" notice.
    /// </summary>
    /// <remarks>
    /// Written on DISMISSAL rather than on display, so a launch that is killed before the user
    /// reads it shows the notice again next time rather than burning it silently.
    /// </remarks>
    public bool HasSeenRebrandNotice
    {
        get => Get(Shell.WhatsNewNotice.RebrandSeenSettingKey, false);
        set => Set(Shell.WhatsNewNotice.RebrandSeenSettingKey, value);
    }

    private T GetEnum<T>(string key, T defaultValue) where T : struct, Enum
    {
        var raw = Get(key, defaultValue.ToString());
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : defaultValue;
    }

    public T Get<T>(string key, T defaultValue)
    {
        // The in-memory store is consulted FIRST. With persistence available it only ever
        // holds values whose persist FAILED, and those are newer than whatever the settings
        // container still has — reading the container first returned the stale value the
        // failed Set was meant to replace.
        if (_memoryStore.TryGetValue(key, out var memValue) && memValue is T memTyped)
            return memTyped;

        if (_settings is not null
            && _settings.Values.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        if (_settings is not null)
        {
            try
            {
                _settings.Values[key] = value;

                // Persisted: drop any fallback left by an earlier failed Set, or it would
                // shadow this newer value in Get.
                _memoryStore.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                // Persist failed — fall back to in-memory so the value is
                // at least available for the current session.
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to persist '{key}': {ex.Message}");
                SetMemory(key, value);
            }
            return;
        }

        SetMemory(key, value);
    }

    /// <summary>
    /// Records <paramref name="value"/> in the in-memory store. A null clears the key, so an
    /// older value can never resurface through <see cref="Get{T}"/>.
    /// </summary>
    private void SetMemory<T>(string key, T value)
    {
        if (value is not null)
            _memoryStore[key] = value;
        else
            _memoryStore.TryRemove(key, out _);
    }
}
