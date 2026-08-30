using System.Text.Json;
using Windows.Storage;

namespace Mixtri.Core.Settings;

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
    private readonly string? _legacyExportPresetsFolder;
    private readonly string? _legacyBrandPresetsFolder;

    public PresetManager()
    {
        string localFolder;
        string? legacyLocalFolder = null;

        try
        {
            // Packaged: this is package-scoped storage, keyed on an identity that did NOT
            // change with the Musio → Mixtri rename, so there is no legacy location here.
            localFolder = ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            // Unpackaged: this IS name-scoped, so the rename moved it. Presets saved before
            // the rename sit under the old root and must still be found.
            localFolder = AppDataPaths.Root;
            legacyLocalFolder = AppDataPaths.LegacyRoot;
        }

        _exportPresetsFolder = Path.Combine(localFolder, ExportPresetsFolderName);
        _brandPresetsFolder = Path.Combine(localFolder, BrandPresetsFolderName);

        if (legacyLocalFolder is not null)
        {
            _legacyExportPresetsFolder = Path.Combine(legacyLocalFolder, ExportPresetsFolderName);
            _legacyBrandPresetsFolder = Path.Combine(legacyLocalFolder, BrandPresetsFolderName);
        }

        Directory.CreateDirectory(_exportPresetsFolder);
        Directory.CreateDirectory(_brandPresetsFolder);
    }

    private const string ExportPresetsFolderName = "ExportPresets";
    private const string BrandPresetsFolderName = "BrandPresets";

    // --- Export Presets ---

    public void SaveExportPreset(ExportPreset preset)
    {
        var filePath = GetPresetPath(_exportPresetsFolder, preset.Name);
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public List<ExportPreset> LoadExportPresets()
    {
        return LoadPresets<ExportPreset>(_exportPresetsFolder, _legacyExportPresetsFolder);
    }

    public void DeleteExportPreset(string name)
    {
        DeletePreset(_exportPresetsFolder, _legacyExportPresetsFolder, name);
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
        return LoadPresets<BrandPreset>(_brandPresetsFolder, _legacyBrandPresetsFolder);
    }

    public void DeleteBrandPreset(string name)
    {
        DeletePreset(_brandPresetsFolder, _legacyBrandPresetsFolder, name);
    }

    // --- Helpers ---

    /// <summary>
    /// Loads every preset in <paramref name="folder"/>, plus any in
    /// <paramref name="legacyFolder"/> that the current folder does not already provide.
    /// </summary>
    /// <remarks>
    /// Current folder first, so a preset re-saved after the rename shadows its pre-rename
    /// copy instead of appearing twice. Saving always writes to the current folder, so
    /// editing a legacy preset migrates it. <c>internal</c> rather than private purely so
    /// tests can drive it against temp folders — the constructor picks real machine paths.
    /// </remarks>
    internal static List<T> LoadPresets<T>(string folder, string? legacyFolder)
    {
        var presets = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in new[] { folder, legacyFolder })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                // File names come from GetPresetPath, so they are a stable identity for
                // the preset and are what makes "current wins" work.
                if (!seen.Add(Path.GetFileName(file)))
                    continue;

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
        }

        return presets;
    }

    /// <summary>
    /// Deletes a preset from the current folder and from the pre-rename one.
    /// </summary>
    /// <remarks>
    /// Deleting from the legacy folder is the one place this code writes there. It has to:
    /// a preset that exists ONLY in the legacy folder would otherwise be reloaded by
    /// <see cref="LoadPresets{T}"/> and reappear immediately after the user deleted it.
    /// </remarks>
    internal static void DeletePreset(string folder, string? legacyFolder, string name)
    {
        foreach (var dir in new[] { folder, legacyFolder })
        {
            if (string.IsNullOrEmpty(dir))
                continue;

            var filePath = GetPresetPath(dir, name);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static string GetPresetPath(string folder, string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        // Prevent path traversal
        safeName = safeName.Replace("..", "_");
        // Truncate excessively long names
        if (safeName.Length > 100)
            safeName = safeName[..100];
        if (ReservedDeviceNames.Contains(safeName))
            safeName = "_" + safeName;
        return Path.Combine(folder, $"{safeName}.json");
    }
}
