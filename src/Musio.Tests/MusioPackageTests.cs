using System.IO.Compression;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Settings;
using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public class MusioPackageTests
{
    private string _root = string.Empty;
    private string _sourceFolder = string.Empty;
    private string _workingRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_pkg_" + Guid.NewGuid().ToString("N"));
        _sourceFolder = Path.Combine(_root, "session");
        _workingRoot = Path.Combine(_root, "working");
        Directory.CreateDirectory(_sourceFolder);
        Directory.CreateDirectory(_workingRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string name, int sizeBytes, byte fill = 0xAB)
    {
        var path = Path.Combine(_sourceFolder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    private (Project Project, TimelineModel Timeline) BuildProject()
    {
        var video = WriteFile("video.mp4", 4096);
        var cursor = WriteFile("cursor.mcur", 512, 0x01);
        var keyboard = WriteFile("keyboard.mkbd", 256, 0x02);
        var audio = WriteFile("system.wav", 2048, 0x03);
        var webcam = WriteFile("webcam.mp4", 1024, 0x04);

        var project = new Project
        {
            Name = "Round trip",
            VideoFilePath = video,
            CursorDataFilePath = cursor,
            KeyboardDataFilePath = keyboard,
            WebcamFilePath = webcam,
            AudioFilePaths = [audio],
            Duration = TimeSpan.FromSeconds(10),
            Width = 1920,
            Height = 1080,
            Fps = 30,
            AspectRatio = AspectRatio.Landscape16x9,
            CropOffsetX = 12,
            CropOffsetY = 34,
        };

        var timeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            Fps = 30,
            PrimaryVideoFilePath = video,
        };

        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video,
            CursorDataFilePath = cursor,
            KeyboardDataFilePath = keyboard,
            WebcamFilePath = webcam,
            AudioFilePaths = [audio],
            SourceDuration = project.Duration,
            Duration = project.Duration,
            SourceWidth = 1920,
            SourceHeight = 1080,
            Fps = 30,
        });

        timeline.Segments.Add(new TextSlideSegment
        {
            Text = "Intro",
            Duration = TimeSpan.FromSeconds(3),
        });

        timeline.CameraSegments.Add(new CameraSegment
        {
            WebcamFilePath = webcam,
            Duration = TimeSpan.FromSeconds(2),
        });

        timeline.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(4),
            SourceVideoFilePath = video,
        });

        return (project, timeline);
    }

    [TestMethod]
    public async Task SaveThenOpen_RestoresProjectMetadata()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(project.Id, opened.Project.Id);
        Assert.AreEqual("Round trip", opened.Project.Name);
        Assert.AreEqual(1920, opened.Project.Width);
        Assert.AreEqual(AspectRatio.Landscape16x9, opened.Project.AspectRatio);
        Assert.AreEqual(12, opened.Project.CropOffsetX);
        Assert.AreEqual(TimeSpan.FromSeconds(10), opened.Project.Duration);
    }

    [TestMethod]
    public async Task SaveThenOpen_ExtractsMediaAndRepointsEveryReference()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.IsTrue(File.Exists(opened.Project.VideoFilePath), "video should be extracted");
        Assert.IsTrue(File.Exists(opened.Project.CursorDataFilePath), "cursor data should be extracted");
        Assert.IsTrue(File.Exists(opened.Project.KeyboardDataFilePath!), "keyboard data should be extracted");
        Assert.IsTrue(File.Exists(opened.Project.WebcamFilePath!), "webcam should be extracted");
        Assert.IsTrue(File.Exists(opened.Project.AudioFilePaths[0]), "audio should be extracted");

        var segment = opened.Timeline.Segments.OfType<VideoSegment>().Single();
        Assert.IsTrue(File.Exists(segment.VideoFilePath));
        Assert.IsTrue(File.Exists(segment.AudioFilePaths[0]));
        Assert.IsTrue(File.Exists(opened.Timeline.PrimaryVideoFilePath!));
        Assert.IsTrue(File.Exists(opened.Timeline.CameraSegments[0].WebcamFilePath!));
        Assert.IsTrue(File.Exists(opened.Timeline.ZoomKeyframes[0].SourceVideoFilePath!));

        // Nothing may still point at the original session folder.
        Assert.IsFalse(opened.Project.VideoFilePath.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(segment.VideoFilePath.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesMediaBytes()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");
        var originalVideo = File.ReadAllBytes(project.VideoFilePath);
        var originalAudio = File.ReadAllBytes(project.AudioFilePaths[0]);

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        CollectionAssert.AreEqual(originalVideo, File.ReadAllBytes(opened.Project.VideoFilePath));
        CollectionAssert.AreEqual(originalAudio, File.ReadAllBytes(opened.Project.AudioFilePaths[0]));
    }

    [TestMethod]
    public async Task Save_DoesNotMutateTheLiveProject()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        var originalVideoPath = project.VideoFilePath;
        var originalSegment = timeline.Segments[0];

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        Assert.AreEqual(originalVideoPath, project.VideoFilePath,
            "saving must not rewrite paths on the objects the editor is still using");
        Assert.AreSame(originalSegment, timeline.Segments[0],
            "saving must not replace live segment instances");
    }

    [TestMethod]
    public async Task SaveThenOpen_RestoresTimelineStructure()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(2, opened.Timeline.Segments.Count);
        Assert.IsInstanceOfType<VideoSegment>(opened.Timeline.Segments[0]);
        Assert.IsInstanceOfType<TextSlideSegment>(opened.Timeline.Segments[1]);
        Assert.AreEqual("Intro", ((TextSlideSegment)opened.Timeline.Segments[1]).Text);
        Assert.AreEqual(1, opened.Timeline.CameraSegments.Count);
        Assert.AreEqual(1, opened.Timeline.ZoomKeyframes.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(4), opened.Timeline.ZoomKeyframes[0].Timestamp);
    }

    [TestMethod]
    public async Task SaveThenOpen_RestoresCompositionSettings()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");
        var composition = new CompositionConfig
        {
            OutputFps = 60,
            AspectRatio = AspectRatio.Portrait9x16,
            FitMode = FitMode.Cover,
            CropAnchorX = 0.25,
            ZoomScope = ZoomScope.Source,
        };

        await MusioPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(60, opened.Composition.OutputFps);
        Assert.AreEqual(AspectRatio.Portrait9x16, opened.Composition.AspectRatio);
        Assert.AreEqual(FitMode.Cover, opened.Composition.FitMode);
        Assert.AreEqual(0.25, opened.Composition.CropAnchorX);
        Assert.AreEqual(ZoomScope.Source, opened.Composition.ZoomScope);
    }

    [TestMethod]
    public async Task SaveThenOpen_PacksTheCompositionBackgroundImage()
    {
        var (project, timeline) = BuildProject();
        var wallpaper = WriteFile("wallpaper.png", 777, 0x55);
        var packagePath = Path.Combine(_root, "project.musio");
        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { BackgroundImagePath = wallpaper },
        };

        await MusioPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        var restored = opened.Composition.Background.BackgroundImagePath;
        Assert.IsNotNull(restored);
        Assert.IsTrue(File.Exists(restored), "the background image must travel inside the package");
        Assert.IsFalse(restored.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase),
            "the restored path must not point back at the machine that saved it");
        Assert.AreEqual(777, new FileInfo(restored).Length);
    }

    [TestMethod]
    public async Task SaveThenOpen_PacksPerSegmentStyleOverrideImages()
    {
        var (project, timeline) = BuildProject();
        var overrideImage = WriteFile("segment_bg.png", 321, 0x66);
        var cursorImage = WriteFile("cursor.png", 123, 0x77);

        var video = timeline.Segments.OfType<VideoSegment>().Single();
        timeline.Segments[timeline.Segments.IndexOf(video)] = video with
        {
            FrameStyleOverride = new BackgroundStyle { BackgroundImagePath = overrideImage },
            CursorStyleOverride = new CursorStyle { CustomImagePath = cursorImage },
        };

        var packagePath = Path.Combine(_root, "project.musio");
        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        var restored = opened.Timeline.Segments.OfType<VideoSegment>().Single();
        Assert.IsTrue(File.Exists(restored.FrameStyleOverride!.BackgroundImagePath!));
        Assert.IsTrue(File.Exists(restored.CursorStyleOverride!.CustomImagePath!));
    }

    [TestMethod]
    public async Task Save_DoesNotMutateTheLiveComposition()
    {
        var (project, timeline) = BuildProject();
        var wallpaper = WriteFile("wallpaper.png", 64, 0x55);
        var packagePath = Path.Combine(_root, "project.musio");
        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { BackgroundImagePath = wallpaper },
        };

        await MusioPackageService.SaveAsync(packagePath, project, composition, timeline);

        Assert.AreEqual(wallpaper, composition.Background.BackgroundImagePath,
            "saving must not rewrite the composition the editor is still using");
    }

    [TestMethod]
    public async Task Save_ProducesASingleFileNotAFolder()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        Assert.IsTrue(File.Exists(packagePath));
        Assert.IsFalse(Directory.Exists(packagePath));
        Assert.IsFalse(File.Exists(packagePath + ".saving"), "temp file should be cleaned up");
    }

    [TestMethod]
    public async Task Save_StoresCompressedMediaWithoutRecompressing()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.musio");

        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        using var archive = ZipFile.OpenRead(packagePath);
        var mp4 = archive.Entries.Single(e => e.FullName.EndsWith("video.mp4", StringComparison.Ordinal));
        var wav = archive.Entries.Single(e => e.FullName.EndsWith("system.wav", StringComparison.Ordinal));

        Assert.AreEqual(mp4.Length, mp4.CompressedLength, "MP4 should be stored, not deflated");
        Assert.IsTrue(wav.CompressedLength < wav.Length, "PCM audio should be deflated");
    }

    [TestMethod]
    public async Task Open_RejectsAPackageFromANewerSchema()
    {
        var packagePath = Path.Combine(_root, "future.musio");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(MusioPackage.ManifestEntryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write($$"""{"SchemaVersion": {{MusioPackage.CurrentSchemaVersion + 1}}}""");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MusioPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_RejectsANonProjectArchive()
    {
        var packagePath = Path.Combine(_root, "bogus.musio");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MusioPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_RejectsEntriesThatEscapeTheMediaFolder()
    {
        var packagePath = Path.Combine(_root, "evil.musio");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry(MusioPackage.ManifestEntryName);
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write($$"""{"SchemaVersion": {{MusioPackage.CurrentSchemaVersion}}}""");

            var escape = archive.CreateEntry(MusioPackage.MediaEntryPrefix + "../../pwned.txt");
            using var escapeWriter = new StreamWriter(escape.Open());
            escapeWriter.Write("nope");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MusioPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_MissingFile_Throws()
    {
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => MusioPackageService.OpenAsync(
                Path.Combine(_root, "nope.musio"), _workingRoot));
    }

    [TestMethod]
    public void IsPackagePath_MatchesOnlyTheMusioExtension()
    {
        Assert.IsTrue(MusioPackage.IsPackagePath(@"C:\a\b.musio"));
        Assert.IsTrue(MusioPackage.IsPackagePath(@"C:\a\b.MUSIO"));
        Assert.IsFalse(MusioPackage.IsPackagePath(@"C:\a\b.mp4"));
        Assert.IsFalse(MusioPackage.IsPackagePath(null));
        Assert.IsFalse(MusioPackage.IsPackagePath("   "));
    }
}
