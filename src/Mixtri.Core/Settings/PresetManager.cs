using System.Security.Cryptography;
using System.Text;
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
        SavePreset(_exportPresetsFolder, preset.Name, preset, _legacyExportPresetsFolder);
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
        SavePreset(_brandPresetsFolder, preset.Name, preset, _legacyBrandPresetsFolder);
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
    /// Writes <paramref name="preset"/> (which carries its original, unsanitised
    /// <paramref name="name"/> in its JSON) to its file in <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// Written to a temporary sibling and moved into place, so a crash or full disk mid-write
    /// cannot leave a truncated preset that then fails to load. If an older build stored the
    /// same preset under its un-suffixed name (see <see cref="GetPresetPath"/>), that copy is
    /// removed so the preset does not appear twice. Every copy of the same preset in
    /// <paramref name="legacyFolder"/> (matched by its stored <c>Name</c>, whatever its file
    /// name) is removed too: saving migrates it, and a leftover pre-rename copy under a
    /// different file name would otherwise load alongside it.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    internal static void SavePreset<T>(string folder, string name, T preset, string? legacyFolder = null)
    {
        var filePath = GetPresetPath(folder, name);
        var json = JsonSerializer.Serialize(preset, JsonOptions);

        Directory.CreateDirectory(folder);
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        RemoveLegacyNamedCopy(folder, name, filePath);

        if (!string.IsNullOrEmpty(legacyFolder)
            && !string.Equals(Path.GetFullPath(legacyFolder), Path.GetFullPath(folder),
                StringComparison.OrdinalIgnoreCase))
        {
            RemoveCopiesNamed(legacyFolder, name);
        }
    }

    /// <summary>
    /// Loads every preset in <paramref name="folder"/>, plus any in
    /// <paramref name="legacyFolder"/> that the current folder does not already provide.
    /// </summary>
    /// <remarks>
    /// Presets are identified by their stored <c>Name</c>, not their file name: the file a
    /// name maps to changed when disambiguation was added (<c>A_B.json</c> became
    /// <c>A_B_&lt;hash&gt;.json</c>), so the same preset can sit under two file names. The
    /// current folder wins over the legacy one, and within a folder the file
    /// <see cref="GetPresetPath"/> would write today wins over an older name. File names are
    /// still honoured as an identity too (a current file shadows a same-named legacy file),
    /// which covers files whose stored name cannot be read. Saving always writes to the
    /// current folder, so editing a legacy preset migrates it. <c>internal</c> rather than
    /// private purely so tests can drive it against temp folders — the constructor picks
    /// real machine paths.
    /// </remarks>
    internal static List<T> LoadPresets<T>(string folder, string? legacyFolder)
    {
        var presets = new List<T>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in new[] { folder, legacyFolder })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            var files = Directory.GetFiles(dir, "*.json");
            var candidates = new List<(string FilePath, T Preset, string? Name)>();

            foreach (var file in files)
            {
                if (seenFiles.Contains(Path.GetFileName(file)))
                    continue;

                try
                {
                    var json = File.ReadAllText(file);
                    var preset = JsonSerializer.Deserialize<T>(json, JsonOptions);
                    if (preset is not null)
                        candidates.Add((file, preset, ReadStoredName(json)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PresetManager] Failed to load preset '{file}': {ex.Message}");
                }
            }

            // Pick one file per stored name: the canonical (current-format) file if there is
            // one, otherwise the first encountered.
            var chosenByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (candidate.Name is null)
                    continue;

                if (!chosenByName.TryGetValue(candidate.Name, out var chosen)
                    || (!IsCanonicalFile(dir, chosen, candidate.Name)
                        && IsCanonicalFile(dir, candidate.FilePath, candidate.Name)))
                {
                    chosenByName[candidate.Name] = candidate.FilePath;
                }
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Name is not null)
                {
                    if (seenNames.Contains(candidate.Name)
                        || !string.Equals(chosenByName[candidate.Name], candidate.FilePath, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                presets.Add(candidate.Preset);
            }

            foreach (var name in chosenByName.Keys)
                seenNames.Add(name);

            // Every file in this folder shadows a same-named file in the next one, including
            // one that failed to load, as before.
            foreach (var file in files)
                seenFiles.Add(Path.GetFileName(file));
        }

        return presets;
    }

    /// <summary>
    /// The preset's stored <c>Name</c>, or null when it has none or it cannot be read.
    /// </summary>
    private static string? ReadStoredName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Name", out var stored)
                && stored.ValueKind == JsonValueKind.String)
            {
                var name = stored.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="file"/> is the file <see cref="GetPresetPath"/> maps
    /// <paramref name="name"/> to in <paramref name="dir"/>.
    /// </summary>
    private static bool IsCanonicalFile(string dir, string file, string name)
        => string.Equals(
            Path.GetFileName(GetPresetPath(dir, name)),
            Path.GetFileName(file),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes every preset file in <paramref name="folder"/> whose stored <c>Name</c> is
    /// exactly <paramref name="name"/>, whatever its file name.
    /// </summary>
    private static void RemoveCopiesNamed(string folder, string name)
    {
        if (!Directory.Exists(folder))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PresetManager] Could not list legacy presets in '{folder}': {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (string.Equals(ReadStoredName(File.ReadAllText(file)), name, StringComparison.Ordinal))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PresetManager] Could not remove legacy preset file '{file}': {ex.Message}");
            }
        }
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
        // A blank name can never have been saved, so there is nothing to delete.
        if (string.IsNullOrWhiteSpace(name))
            return;

        foreach (var dir in new[] { folder, legacyFolder })
        {
            if (string.IsNullOrEmpty(dir))
                continue;

            var filePath = GetPresetPath(dir, name);
            if (File.Exists(filePath))
                File.Delete(filePath);

            RemoveLegacyNamedCopy(dir, name, filePath);

            // Loading identifies presets by stored name, so any other file holding this
            // preset would bring it straight back.
            RemoveCopiesNamed(dir, name);
        }
    }

    /// <summary>
    /// Removes the file an older build would have written for <paramref name="name"/> — its
    /// sanitised name with no disambiguating suffix — but only when that file really holds
    /// this preset (its stored name matches), since a different preset may legitimately own
    /// that file name.
    /// </summary>
    private static void RemoveLegacyNamedCopy(string folder, string name, string currentPath)
    {
        var legacyPath = Path.Combine(folder, $"{SanitizeName(name)}.json");
        if (string.Equals(legacyPath, currentPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Name", out var stored)
                && stored.ValueKind == JsonValueKind.String
                && string.Equals(stored.GetString(), name, StringComparison.Ordinal))
            {
                File.Delete(legacyPath);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PresetManager] Could not inspect legacy preset file '{legacyPath}': {ex.Message}");
        }
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// The file a preset named <paramref name="name"/> is stored in.
    /// </summary>
    /// <remarks>
    /// Sanitising is lossy — "A/B", "A?B" and "A:B" all become "A_B" — so whenever the
    /// sanitised form differs from the original a short, stable hash of the ORIGINAL name is
    /// appended. Without it, saving one of those presets silently overwrote another. Names
    /// that need no sanitising keep their historical file name unchanged. The original name
    /// itself always lives in the preset's JSON (<c>Name</c>), which is what the UI shows.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is empty or whitespace.</exception>
    internal static string GetPresetPath(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A preset name cannot be empty.", nameof(name));

        var safeName = SanitizeName(name);
        if (!string.Equals(safeName, name, StringComparison.Ordinal))
            safeName = $"{safeName}_{ShortHash(name)}";

        return Path.Combine(folder, $"{safeName}.json");
    }

    /// <summary>The historical (pre-disambiguation) file-name stem for a preset name.</summary>
    private static string SanitizeName(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        // Prevent path traversal
        safeName = safeName.Replace("..", "_");
        // Truncate excessively long names
        if (safeName.Length > 100)
            safeName = safeName[..100];
        if (ReservedDeviceNames.Contains(safeName))
            safeName = "_" + safeName;
        return safeName;
    }

    /// <summary>
    /// Eight hex characters of SHA-256 over the name. Must be stable across processes and
    /// runs, which is why <see cref="string.GetHashCode()"/> (randomised per process) is not
    /// usable here.
    /// </summary>
    private static string ShortHash(string name)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
}
