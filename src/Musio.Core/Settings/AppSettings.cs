using Windows.Storage;

namespace Musio.Core.Settings;

/// <summary>
/// Singleton settings manager that persists user preferences via ApplicationData.LocalSettings.
/// </summary>
public sealed class AppSettings
{
    private static readonly Lazy<AppSettings> _instance = new(() => new AppSettings());
    public static AppSettings Instance => _instance.Value;

    private readonly ApplicationDataContainer? _settings;

    private AppSettings()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            // App may not have package identity — fall back to in-memory defaults
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

    public bool IsWebcamEnabled
    {
        get => Get(nameof(IsWebcamEnabled), false);
        set => Set(nameof(IsWebcamEnabled), value);
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (_settings is null) return defaultValue;
        if (_settings.Values.TryGetValue(key, out var value) && value is T typed)
            return typed;

        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        if (_settings is null) return;
        _settings.Values[key] = value;
    }
}
