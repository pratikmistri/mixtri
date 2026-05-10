using System.Text.Json;
using Windows.Storage;

namespace Musio.Core.Settings;

/// <summary>
/// Manages saving, loading, and deleting export and brand presets as JSON files.
/// </summary>
public class PresetManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _exportPresetsFolder;
    private readonly string _brandPresetsFolder;

    public PresetManager()
    {
        string localFolder;
        try
        {
            localFolder = ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            // Unpackaged app — use AppData\Local\Musio instead
            localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musio");
        }

        _exportPresetsFolder = Path.Combine(localFolder, "ExportPresets");
        _brandPresetsFolder = Path.Combine(localFolder, "BrandPresets");

        Directory.CreateDirectory(_exportPresetsFolder);
        Directory.CreateDirectory(_brandPresetsFolder);
    }

    // --- Export Presets ---

    public void SaveExportPreset(ExportPreset preset)
    {
        var filePath = GetPresetPath(_exportPresetsFolder, preset.Name);
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public List<ExportPreset> LoadExportPresets()
    {
        return LoadPresets<ExportPreset>(_exportPresetsFolder);
    }

    public void DeleteExportPreset(string name)
    {
        DeletePreset(_exportPresetsFolder, name);
    }

    // --- Brand Presets ---

    public void SaveBrandPreset(BrandPreset preset)
    {
        var filePath = GetPresetPath(_brandPresetsFolder, preset.Name);
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public List<BrandPreset> LoadBrandPresets()
    {
        return LoadPresets<BrandPreset>(_brandPresetsFolder);
    }

    public void DeleteBrandPreset(string name)
    {
        DeletePreset(_brandPresetsFolder, name);
    }

    // --- Helpers ---

    private static List<T> LoadPresets<T>(string folder)
    {
        var presets = new List<T>();

        if (!Directory.Exists(folder))
            return presets;

        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (preset is not null)
                    presets.Add(preset);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PresetManager] Failed to load preset '{file}': {ex.Message}");
            }
        }

        return presets;
    }

    private static void DeletePreset(string folder, string name)
    {
        var filePath = GetPresetPath(folder, name);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    private static string GetPresetPath(string folder, string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        // Prevent path traversal
        safeName = safeName.Replace("..", "_");
        // Truncate excessively long names
        if (safeName.Length > 100)
            safeName = safeName[..100];
        // Guard against Windows reserved device names
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        if (reserved.Contains(safeName))
            safeName = "_" + safeName;
        return Path.Combine(folder, $"{safeName}.json");
    }
}
