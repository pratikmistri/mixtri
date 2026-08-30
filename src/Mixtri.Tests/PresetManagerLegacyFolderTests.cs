using System.Text.Json;
using Mixtri.Core.Settings;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Presets in an UNPACKAGED run live under a name-scoped LocalAppData folder, so the
/// Musio → Mixtri rename moved them. (A packaged run stores them under
/// <c>ApplicationData.Current.LocalFolder</c>, which is keyed on the package identity and was
/// deliberately left unchanged, so it was never affected.) These pin the read path that keeps
/// pre-rename presets reachable.
/// </summary>
[TestClass]
public class PresetManagerLegacyFolderTests
{
    private TempDirectoryFixture? _tempDir;
    private string _current => Path.Combine(_tempDir!.Path, "current");
    private string _legacy => Path.Combine(_tempDir!.Path, "legacy");

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_presets_");
        Directory.CreateDirectory(_current);
        Directory.CreateDirectory(_legacy);
    }

    [TestCleanup]
    public void TearDown() => _tempDir?.Dispose();

    private static void WritePreset(string folder, string name)
    {
        var preset = new ExportPreset { Name = name };
        File.WriteAllText(
            Path.Combine(folder, $"{name}.json"),
            JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
    }

    [TestMethod]
    public void LoadPresets_FindsPresetsLeftInTheLegacyFolder()
    {
        WritePreset(_legacy, "Old preset");

        var presets = PresetManager.LoadPresets<ExportPreset>(_current, _legacy);

        Assert.AreEqual(1, presets.Count);
        Assert.AreEqual("Old preset", presets[0].Name);
    }

    [TestMethod]
    public void LoadPresets_ReturnsBothFolders()
    {
        WritePreset(_current, "New preset");
        WritePreset(_legacy, "Old preset");

        var names = PresetManager.LoadPresets<ExportPreset>(_current, _legacy)
            .Select(p => p.Name).ToList();

        CollectionAssert.AreEquivalent(new[] { "New preset", "Old preset" }, names);
    }

    /// <summary>
    /// Saving writes to the current folder, so an edited legacy preset exists in both. It
    /// must appear once, with the edited values.
    /// </summary>
    [TestMethod]
    public void LoadPresets_CurrentFolderShadowsTheLegacyCopy()
    {
        WritePreset(_legacy, "Shared");
        WritePreset(_current, "Shared");

        var presets = PresetManager.LoadPresets<ExportPreset>(_current, _legacy);

        Assert.AreEqual(1, presets.Count, "a preset present in both folders must not appear twice");
    }

    [TestMethod]
    public void LoadPresets_WithNoLegacyFolder_StillReadsTheCurrentOne()
    {
        WritePreset(_current, "Only preset");

        var presets = PresetManager.LoadPresets<ExportPreset>(_current, legacyFolder: null);

        Assert.AreEqual(1, presets.Count);
    }

    [TestMethod]
    public void LoadPresets_ToleratesAMissingLegacyFolder()
    {
        WritePreset(_current, "Only preset");

        var presets = PresetManager.LoadPresets<ExportPreset>(
            _current, Path.Combine(_tempDir!.Path, "does-not-exist"));

        Assert.AreEqual(1, presets.Count);
    }

    /// <summary>
    /// Without deleting the legacy copy too, a legacy-only preset would be re-read by the
    /// next load and reappear immediately after the user deleted it.
    /// </summary>
    [TestMethod]
    public void DeletePreset_RemovesALegacyOnlyPresetForGood()
    {
        WritePreset(_legacy, "Old preset");

        PresetManager.DeletePreset(_current, _legacy, "Old preset");

        Assert.AreEqual(0, PresetManager.LoadPresets<ExportPreset>(_current, _legacy).Count);
    }

    [TestMethod]
    public void DeletePreset_RemovesTheCopyInBothFolders()
    {
        WritePreset(_current, "Shared");
        WritePreset(_legacy, "Shared");

        PresetManager.DeletePreset(_current, _legacy, "Shared");

        Assert.AreEqual(0, PresetManager.LoadPresets<ExportPreset>(_current, _legacy).Count);
    }
}
