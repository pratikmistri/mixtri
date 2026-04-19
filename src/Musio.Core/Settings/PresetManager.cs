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
            var json = File.ReadAllText(file);
            var preset = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (preset is not null)
                presets.Add(preset);
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
        return Path.Combine(folder, $"{safeName}.json");
    }
}
