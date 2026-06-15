using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace Musio.Core.Settings;

/// <summary>
/// Singleton settings manager that persists user preferences via ApplicationData.LocalSettings.
/// Falls back to an in-memory dictionary when running without package identity.
/// </summary>
public sealed class AppSettings
{
    private static readonly Lazy<AppSettings> _instance = new(() => new AppSettings());
    public static AppSettings Instance => _instance.Value;

    private readonly ApplicationDataContainer? _settings;
    private readonly Dictionary<string, object> _memoryStore = new();
    private readonly string? _jsonFallbackPath;

    private AppSettings()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            // App may not have package identity — fall back to a JSON file
            // under %LOCALAPPDATA%\Musio\settings.json so dev builds and
            // unpackaged runs still persist across restarts.
            _settings = null;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Musio");
                Directory.CreateDirectory(dir);
                _jsonFallbackPath = Path.Combine(dir, "settings.json");
                LoadJsonFallback();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] JSON fallback init failed: {ex.Message}");
                _jsonFallbackPath = null;
            }
        }
    }

    private void LoadJsonFallback()
    {
        if (_jsonFallbackPath is null || !File.Exists(_jsonFallbackPath)) return;
        try
        {
            var json = File.ReadAllText(_jsonFallbackPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                object? val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? (object)i
                                          : prop.Value.TryGetInt64(out var l) ? l
                                          : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
                if (val is not null) _memoryStore[prop.Name] = val;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] LoadJsonFallback failed: {ex.Message}");
        }
    }

    private void SaveJsonFallback()
    {
        if (_jsonFallbackPath is null) return;
        try
        {
            var json = JsonSerializer.Serialize(_memoryStore, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_jsonFallbackPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] SaveJsonFallback failed: {ex.Message}");
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

    private T GetEnum<T>(string key, T defaultValue) where T : struct, Enum
    {
        var raw = Get(key, defaultValue.ToString());
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : defaultValue;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (_settings is not null)
        {
            if (_settings.Values.TryGetValue(key, out var value) && value is T typed)
                return typed;

            // Check in-memory fallback (populated when persistence failed)
            if (_memoryStore.TryGetValue(key, out var fallback) && fallback is T fbTyped)
                return fbTyped;

            return defaultValue;
        }

        if (_memoryStore.TryGetValue(key, out var memValue) && memValue is T memTyped)
            return memTyped;

        return defaultValue;
    }

    /// <summary>
    /// True when <paramref name="key"/> has a persisted value in the backing
    /// store (LocalSettings or in-memory fallback). Used by version-to-version
    /// migrations that need to distinguish "never written" from "explicitly
    /// set to the default value".
    /// </summary>
    public bool HasKey(string key)
    {
        if (_settings is not null && _settings.Values.ContainsKey(key))
            return true;
        return _memoryStore.ContainsKey(key);
    }

    public void Set<T>(string key, T value)
    {
        if (_settings is not null)
        {
            try
            {
                _settings.Values[key] = value;
            }
            catch (Exception ex)
            {
                // Persist failed — fall back to in-memory so the value is
                // at least available for the current session.
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to persist '{key}': {ex.Message}");
                if (value is not null) _memoryStore[key] = value;
            }
            return;
        }

        if (value is not null)
        {
            _memoryStore[key] = value;
            SaveJsonFallback();
        }
    }
}
