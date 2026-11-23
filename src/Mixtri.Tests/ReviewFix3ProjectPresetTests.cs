using System.Text.Json;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for the third round of 2026-11 review fixes: a locked, stale cache file
/// is never reused on open, a file referenced in more than one role keeps its strongest
/// role, and presets are de-duplicated by their stored name across file-name schemes and
/// the legacy folder.
/// </summary>
[TestClass]
public sealed class ReviewFix3ProjectPresetTests
{
    private TempDirectoryFixture? _tempDir;
    private string Root => _tempDir!.Path;
    private string SourceFolder => Path.Combine(Root, "session");
    private string WorkingRoot => Path.Combine(Root, "working");

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_review3_");
        Directory.CreateDirectory(SourceFolder);
        Directory.CreateDirectory(WorkingRoot);
    }

    [TestCleanup]
    public void TearDown() => _tempDir?.Dispose();

    private string WriteFile(string name, int sizeBytes, byte fill)
    {
        var path = Path.Combine(SourceFolder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    private Project BuildProject()
    {
        var video = WriteFile("video.mp4", 4096, 0xAB);
        var cursor = WriteFile("cursor.mcur", 512, 0x01);
        var audio = WriteFile("system.wav", 2048, 0x03);

        return new Project
        {
            Name = "Review3",
            VideoFilePath = video,
            CursorDataFilePath = cursor,
            AudioFilePaths = [audio],
            Duration = TimeSpan.FromSeconds(10),
            Width = 1920,
            Height = 1080,
            Fps = 30,
        };
    }

    // ── Strongest role wins ──────────────────────────────────────────

    private static TimelineModel TimelineWithDetachedAudio(Project project)
    {
        var timeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            PrimaryVideoFilePath = project.VideoFilePath,
        };

        // A detached audio block reuses the recording's own audio file.
        timeline.AudioTracks.Add(new AudioTrack
        {
            FilePath = project.AudioFilePaths[0],
            Name = "Detached",
            Duration = TimeSpan.FromSeconds(10),
        });
        return timeline;
    }

    [TestMethod]
    public void EnumerateReferences_DetachedAudioPromotesTheRecordingAudioToRequired()
    {
        var project = BuildProject();
        var timeline = TimelineWithDetachedAudio(project);

        var refs = MixtriPathRewriter.EnumerateReferences(project, timeline);

        var audio = refs.Where(r => string.Equals(
            r.Path, project.AudioFilePaths[0], StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.AreEqual(1, audio.Count, "a file is still enumerated once");
        Assert.AreEqual(MediaReferenceKind.Recording, audio[0].Kind,
            "an explicit audio block makes the file required, even though it was first seen as a sidecar");
    }

    [TestMethod]
    public void EnumerateReferences_WeakerLaterRoleDoesNotDemote()
    {
        var project = BuildProject();
        var timeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            PrimaryVideoFilePath = project.VideoFilePath,
        };
        timeline.ZoomKeyframes.Add(new ZoomKeyframe { SourceVideoFilePath = project.VideoFilePath });

        var refs = MixtriPathRewriter.EnumerateReferences(project, timeline);

        Assert.AreEqual(MediaReferenceKind.Recording,
            refs.Single(r => r.Path == project.VideoFilePath).Kind);
    }

    [TestMethod]
    public async Task Save_WithMissingRecordingAudioUsedByDetachedAudioTrack_Refuses()
    {
        var project = BuildProject();
        var timeline = TimelineWithDetachedAudio(project);
        File.Delete(project.AudioFilePaths[0]);
        var packagePath = Path.Combine(Root, "project.mixtri");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline));
        Assert.IsFalse(File.Exists(packagePath), "nothing may be written over the package");
    }

    // ── Locked stale cache file ──────────────────────────────────────

    [TestMethod]
    public async Task Open_WhenStaleCachedMediaIsLocked_ExtractsCurrentMediaElsewhere()
    {
        var project = BuildProject();
        var packagePath = Path.Combine(Root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline: null);
        var first = await MixtriPackageService.OpenAsync(packagePath, WorkingRoot);
        var cachedVideo = first.Project.VideoFilePath;
        var oldBytes = File.ReadAllBytes(cachedVideo);

        // A different take of the same length, clearly newer.
        File.WriteAllBytes(project.VideoFilePath, Enumerable.Repeat((byte)0xCD, 4096).ToArray());
        File.SetLastWriteTimeUtc(project.VideoFilePath, DateTime.UtcNow.AddMinutes(10));
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline: null);

        MixtriOpenResult second;
        using (new FileStream(cachedVideo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            second = await MixtriPackageService.OpenAsync(packagePath, WorkingRoot);
        }

        var openedBytes = File.ReadAllBytes(second.Project.VideoFilePath);
        Assert.IsTrue(openedBytes.All(b => b == 0xCD),
            "the reopened project must point at the newly saved media, never the stale cached copy");
        Assert.AreEqual(4096, openedBytes.Length);
        Assert.AreEqual(Path.GetFileName(cachedVideo), Path.GetFileName(second.Project.VideoFilePath),
            "the file name is kept so a later save does not rename the entry");
        Assert.IsTrue(oldBytes.All(b => b == 0xAB), "sanity: the first open had the old take");
        Assert.AreEqual(0,
            Directory.GetFiles(second.MediaFolder, "*.part", SearchOption.AllDirectories).Length,
            "no partial extraction is left behind");
    }

    // ── Presets: identity by stored name ─────────────────────────────

    private static void WriteRaw(string folder, string fileName, ExportPreset preset)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), JsonSerializer.Serialize(preset));
    }

    [TestMethod]
    public void SavePreset_RemovesTheLegacyFolderCopyUnderItsOldFileName()
    {
        var current = Path.Combine(Root, "current");
        var legacy = Path.Combine(Root, "legacy");
        WriteRaw(legacy, "A_B.json", new ExportPreset { Name = "A/B", Fps = 24 });
        WriteRaw(legacy, "Other.json", new ExportPreset { Name = "Other", Fps = 30 });

        PresetManager.SavePreset(current, "A/B", new ExportPreset { Name = "A/B", Fps = 60 }, legacy);

        Assert.IsFalse(File.Exists(Path.Combine(legacy, "A_B.json")), "the legacy copy is migrated away");
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "Other.json")), "unrelated legacy presets are untouched");

        var loaded = PresetManager.LoadPresets<ExportPreset>(current, legacy);
        CollectionAssert.AreEquivalent(new[] { "A/B", "Other" }, loaded.Select(p => p.Name).ToList());
        Assert.AreEqual(60, loaded.Single(p => p.Name == "A/B").Fps);
    }

    [TestMethod]
    public void LoadPresets_DeduplicatesByStoredName_CurrentFolderWins()
    {
        var current = Path.Combine(Root, "current");
        var legacy = Path.Combine(Root, "legacy");
        PresetManager.SavePreset(current, "A/B", new ExportPreset { Name = "A/B", Fps = 60 });
        WriteRaw(legacy, "A_B.json", new ExportPreset { Name = "A/B", Fps = 24 });

        var loaded = PresetManager.LoadPresets<ExportPreset>(current, legacy);

        Assert.AreEqual(1, loaded.Count, "the same preset under two file names must appear once");
        Assert.AreEqual(60, loaded[0].Fps, "the current folder's copy wins");
    }

    [TestMethod]
    public void LoadPresets_WithinAFolder_PrefersTheCurrentFileName()
    {
        var current = Path.Combine(Root, "current");
        // Both files hold the same preset; enumeration order is not guaranteed, so the
        // choice must not depend on which one is encountered first.
        WriteRaw(current, "A_B.json", new ExportPreset { Name = "A/B", Fps = 24 });
        WriteRaw(current, Path.GetFileName(PresetManager.GetPresetPath(current, "A/B")),
            new ExportPreset { Name = "A/B", Fps = 60 });

        var loaded = PresetManager.LoadPresets<ExportPreset>(current, legacyFolder: null);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(60, loaded[0].Fps, "the hashed, current-format file wins");
    }

    [TestMethod]
    public void DeletePreset_RemovesEveryCopyOfThePresetSoItCannotReappear()
    {
        var current = Path.Combine(Root, "current");
        var legacy = Path.Combine(Root, "legacy");
        PresetManager.SavePreset(current, "A/B", new ExportPreset { Name = "A/B" });
        WriteRaw(legacy, "A_B.json", new ExportPreset { Name = "A/B" });
        WriteRaw(legacy, "renamed-by-hand.json", new ExportPreset { Name = "A/B" });

        PresetManager.DeletePreset(current, legacy, "A/B");

        Assert.AreEqual(0, PresetManager.LoadPresets<ExportPreset>(current, legacy).Count);
    }
}
