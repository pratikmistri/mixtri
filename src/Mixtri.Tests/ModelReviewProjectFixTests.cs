using System.IO.Compression;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for the 2026-11 code-review fixes in project packaging, presets and
/// settings: optional sidecars no longer block a save, a null timeline round-trips with a
/// real duration, media compression levels, preset file-name disambiguation and atomic
/// writes, and the settings in-memory fallback.
/// </summary>
[TestClass]
public sealed class ModelReviewProjectFixTests
{
    private TempDirectoryFixture? _tempDir;
    private string Root => _tempDir!.Path;
    private string SourceFolder => Path.Combine(Root, "session");
    private string WorkingRoot => Path.Combine(Root, "working");

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_review_");
        Directory.CreateDirectory(SourceFolder);
        Directory.CreateDirectory(WorkingRoot);
    }

    [TestCleanup]
    public void TearDown() => _tempDir?.Dispose();

    private string WriteFile(string name, int sizeBytes, byte fill = 0xAB)
    {
        var path = Path.Combine(SourceFolder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    private Project BuildProject(bool writeSidecars)
    {
        var video = WriteFile("video.mp4", 4096);
        string cursor = Path.Combine(SourceFolder, "cursor.mcur");
        string keyboard = Path.Combine(SourceFolder, "keyboard.mkbd");
        string audio = Path.Combine(SourceFolder, "system.wav");
        string webcam = Path.Combine(SourceFolder, "webcam.mp4");
        if (writeSidecars)
        {
            WriteFile("cursor.mcur", 512, 0x01);
            WriteFile("keyboard.mkbd", 256, 0x02);
            WriteFile("system.wav", 2048, 0x03);
            WriteFile("webcam.mp4", 1024, 0x04);
        }

        return new Project
        {
            Name = "Review",
            VideoFilePath = video,
            CursorDataFilePath = cursor,
            KeyboardDataFilePath = keyboard,
            WebcamFilePath = webcam,
            AudioFilePaths = [audio],
            Duration = TimeSpan.FromSeconds(10),
            Width = 1920,
            Height = 1080,
            Fps = 30,
        };
    }

    // ── Optional sidecars ────────────────────────────────────────────

    [TestMethod]
    public async Task Save_WithMissingOptionalSidecars_SucceedsAndPacksTheVideo()
    {
        var project = BuildProject(writeSidecars: false);
        var packagePath = Path.Combine(Root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline: null);

        Assert.IsTrue(File.Exists(packagePath));
        var opened = await MixtriPackageService.OpenAsync(packagePath, WorkingRoot);
        Assert.IsTrue(File.Exists(opened.Project.VideoFilePath), "the primary video must still be packed");
    }

    [TestMethod]
    public async Task Save_WithMissingPrimaryVideo_StillRefuses()
    {
        var project = BuildProject(writeSidecars: true);
        File.Delete(project.VideoFilePath);
        var packagePath = Path.Combine(Root, "project.mixtri");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline: null));
        Assert.IsFalse(File.Exists(packagePath));
    }

    [TestMethod]
    public async Task Save_WithMissingInsertedAudioTrack_StillRefuses()
    {
        var project = BuildProject(writeSidecars: true);
        var timeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            PrimaryVideoFilePath = project.VideoFilePath,
        };
        timeline.AudioTracks.Add(new AudioTrack
        {
            FilePath = Path.Combine(SourceFolder, "voiceover.wav"),
            Name = "VO",
            Duration = TimeSpan.FromSeconds(2),
        });

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => MixtriPackageService.SaveAsync(
                Path.Combine(Root, "project.mixtri"), project, new CompositionConfig(), timeline));
    }

    [TestMethod]
    public void EnumerateReferences_ClassifiesSidecarsSeparatelyFromPrimaryMedia()
    {
        var project = BuildProject(writeSidecars: true);

        var refs = MixtriPathRewriter.EnumerateReferences(project, timeline: null);

        Assert.AreEqual(MediaReferenceKind.Recording,
            refs.Single(r => r.Path == project.VideoFilePath).Kind);
        Assert.AreEqual(MediaReferenceKind.RecordingSidecar,
            refs.Single(r => r.Path == project.CursorDataFilePath).Kind);
        Assert.AreEqual(MediaReferenceKind.RecordingSidecar,
            refs.Single(r => r.Path == project.WebcamFilePath).Kind);
        Assert.AreEqual(MediaReferenceKind.RecordingSidecar,
            refs.Single(r => r.Path == project.AudioFilePaths[0]).Kind);
    }

    // ── Null timeline ────────────────────────────────────────────────

    [TestMethod]
    public async Task Save_WithNullTimeline_ReopensWithProjectDuration()
    {
        var project = BuildProject(writeSidecars: true);
        var packagePath = Path.Combine(Root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline: null);
        var opened = await MixtriPackageService.OpenAsync(packagePath, WorkingRoot);

        Assert.AreEqual(TimeSpan.FromSeconds(10), opened.Timeline.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), opened.Timeline.TrimEnd);
    }

    [TestMethod]
    public void SeedAbsentTimeline_FillsOnlyAnEmptyZeroLengthTimeline()
    {
        var project = new Project { Duration = TimeSpan.FromSeconds(7) };

        var absent = new TimelineModel();
        MixtriPackageService.SeedAbsentTimeline(absent, project);
        Assert.AreEqual(TimeSpan.FromSeconds(7), absent.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(7), absent.TrimEnd);

        var withContent = new TimelineModel();
        withContent.Segments.Add(new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) });
        MixtriPackageService.SeedAbsentTimeline(withContent, project);
        Assert.AreEqual(TimeSpan.Zero, withContent.Duration, "a timeline with content is left alone");

        var withDuration = new TimelineModel { Duration = TimeSpan.FromSeconds(4) };
        MixtriPackageService.SeedAbsentTimeline(withDuration, project);
        Assert.AreEqual(TimeSpan.FromSeconds(4), withDuration.Duration);
    }

    // ── Compression ──────────────────────────────────────────────────

    [TestMethod]
    public void CompressionFor_SkipsCompressedMediaAndKeepsOptimalForData()
    {
        Assert.AreEqual(CompressionLevel.NoCompression, MixtriPackage.CompressionFor("video.mp4"));
        Assert.AreEqual(CompressionLevel.NoCompression, MixtriPackage.CompressionFor("poster.JPG"));
        Assert.AreEqual(CompressionLevel.NoCompression, MixtriPackage.CompressionFor("image.png"));
        Assert.AreEqual(CompressionLevel.Fastest, MixtriPackage.CompressionFor("system.wav"));
        Assert.AreEqual(CompressionLevel.Optimal, MixtriPackage.CompressionFor("manifest.json"));
        Assert.AreEqual(CompressionLevel.Optimal, MixtriPackage.CompressionFor("cursor.mcur"));
    }

    // ── Presets ──────────────────────────────────────────────────────

    [TestMethod]
    public void GetPresetPath_DisambiguatesNamesThatSanitizeAlike()
    {
        var folder = Path.Combine(Root, "presets");

        var slash = PresetManager.GetPresetPath(folder, "A/B");
        var question = PresetManager.GetPresetPath(folder, "A?B");

        Assert.AreNotEqual(slash, question);
        Assert.AreEqual(slash, PresetManager.GetPresetPath(folder, "A/B"), "the mapping must be stable");
        Assert.AreEqual(Path.Combine(folder, "Plain name.json"),
            PresetManager.GetPresetPath(folder, "Plain name"),
            "names that need no sanitising keep their historical file name");
    }

    [TestMethod]
    public void GetPresetPath_RejectsBlankNames()
    {
        var folder = Path.Combine(Root, "presets");
        Assert.ThrowsException<ArgumentException>(() => PresetManager.GetPresetPath(folder, ""));
        Assert.ThrowsException<ArgumentException>(() => PresetManager.GetPresetPath(folder, "   "));
    }

    [TestMethod]
    public void SavePreset_CollidingSanitizedNames_BothSurvive()
    {
        var folder = Path.Combine(Root, "presets");

        PresetManager.SavePreset(folder, "A/B", new ExportPreset { Name = "A/B", Fps = 24 });
        PresetManager.SavePreset(folder, "A?B", new ExportPreset { Name = "A?B", Fps = 60 });

        var loaded = PresetManager.LoadPresets<ExportPreset>(folder, legacyFolder: null);
        CollectionAssert.AreEquivalent(new[] { "A/B", "A?B" }, loaded.Select(p => p.Name).ToList());
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.tmp").Length, "no temp file is left behind");
    }

    [TestMethod]
    public void SavePreset_ReplacesTheUnsuffixedFileAnOlderBuildWrote()
    {
        var folder = Path.Combine(Root, "presets");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "A_B.json"),
            System.Text.Json.JsonSerializer.Serialize(new ExportPreset { Name = "A/B", Fps = 24 }));

        PresetManager.SavePreset(folder, "A/B", new ExportPreset { Name = "A/B", Fps = 60 });

        var loaded = PresetManager.LoadPresets<ExportPreset>(folder, legacyFolder: null);
        Assert.AreEqual(1, loaded.Count, "the old copy must not linger as a duplicate");
        Assert.AreEqual(60, loaded[0].Fps);
    }

    [TestMethod]
    public void DeletePreset_RemovesBothNewAndLegacyNamedFiles_ButNotAnotherPreset()
    {
        var folder = Path.Combine(Root, "presets");
        Directory.CreateDirectory(folder);
        // An old-format file for "A/B", and a genuinely different preset named "A_B".
        File.WriteAllText(Path.Combine(folder, "A_B.json"),
            System.Text.Json.JsonSerializer.Serialize(new ExportPreset { Name = "A/B" }));
        PresetManager.SavePreset(folder, "A?B", new ExportPreset { Name = "A?B" });

        PresetManager.DeletePreset(folder, legacyFolder: null, "A/B");

        var names = PresetManager.LoadPresets<ExportPreset>(folder, legacyFolder: null)
            .Select(p => p.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "A?B" }, names);

        // A blank name is a no-op, not an exception.
        PresetManager.DeletePreset(folder, legacyFolder: null, "  ");
    }

    // ── AppSettings ──────────────────────────────────────────────────

    [TestMethod]
    public void AppSettings_SetNull_ClearsTheInMemoryValue()
    {
        var key = "ReviewTest_" + Guid.NewGuid().ToString("N");
        var settings = AppSettings.Instance;

        settings.Set(key, "first");
        Assert.AreEqual("first", settings.Get(key, "default"));

        settings.Set<string?>(key, null);
        Assert.AreEqual("default", settings.Get(key, "default"));
    }
}
