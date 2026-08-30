using System.IO.Compression;
using Mixtri.Core.Audio;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Settings;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public class MixtriPackageTests
{
    private TempDirectoryFixture? _tempDir;
    private string _root => _tempDir!.Path;
    private string _sourceFolder => Path.Combine(_root, "session");
    private string _workingRoot => Path.Combine(_root, "working");

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = new TempDirectoryFixture("mixtri_pkg_");
        Directory.CreateDirectory(_sourceFolder);
        Directory.CreateDirectory(_workingRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        _tempDir?.Dispose();
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
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

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
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

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
        var packagePath = Path.Combine(_root, "project.mixtri");
        var originalVideo = File.ReadAllBytes(project.VideoFilePath);
        var originalAudio = File.ReadAllBytes(project.AudioFilePaths[0]);

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        CollectionAssert.AreEqual(originalVideo, File.ReadAllBytes(opened.Project.VideoFilePath));
        CollectionAssert.AreEqual(originalAudio, File.ReadAllBytes(opened.Project.AudioFilePaths[0]));
    }

    [TestMethod]
    public async Task Save_DoesNotMutateTheLiveProject()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        var originalVideoPath = project.VideoFilePath;
        var originalSegment = timeline.Segments[0];

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        Assert.AreEqual(originalVideoPath, project.VideoFilePath,
            "saving must not rewrite paths on the objects the editor is still using");
        Assert.AreSame(originalSegment, timeline.Segments[0],
            "saving must not replace live segment instances");
    }

    [TestMethod]
    public async Task SaveThenOpen_RestoresTimelineStructure()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

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
        var packagePath = Path.Combine(_root, "project.mixtri");
        var composition = new CompositionConfig
        {
            OutputFps = 60,
            AspectRatio = AspectRatio.Portrait9x16,
            FitMode = FitMode.Cover,
            CropAnchorX = 0.25,
            ZoomScope = ZoomScope.Source,
        };

        await MixtriPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

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
        var packagePath = Path.Combine(_root, "project.mixtri");
        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { BackgroundImagePath = wallpaper },
        };

        await MixtriPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

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

        var packagePath = Path.Combine(_root, "project.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        var restored = opened.Timeline.Segments.OfType<VideoSegment>().Single();
        Assert.IsTrue(File.Exists(restored.FrameStyleOverride!.BackgroundImagePath!));
        Assert.IsTrue(File.Exists(restored.CursorStyleOverride!.CustomImagePath!));
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesCursorAnchorsAndRewritesTheirSourcePaths()
    {
        // An anchor's SourceVideoFilePath is a back-reference to a recording that MOVES when a
        // project is packaged and reopened elsewhere. Leaving it pointing at the saving
        // machine's path would silently orphan the anchor: the cursor would snap back to where
        // it was recorded, with nothing to explain why. Mirrors the zoom-keyframe rule.
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        string savedVideoPath = project.VideoFilePath!;
        timeline.CursorAnchors.Add(new CursorAnchor
        {
            Timestamp = TimeSpan.FromSeconds(4),
            X = 0.25,
            Y = 0.75,
        });
        timeline.CursorAnchors.Add(new CursorAnchor
        {
            Timestamp = TimeSpan.FromSeconds(6),
            X = 0.5,
            Y = 0.5,
            SourceVideoFilePath = savedVideoPath,
        });

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(2, opened.Timeline.CursorAnchors.Count);

        var primaryAnchor = opened.Timeline.CursorAnchors.Single(a => a.SourceVideoFilePath is null);
        Assert.AreEqual(TimeSpan.FromSeconds(4), primaryAnchor.Timestamp);
        Assert.AreEqual(0.25, primaryAnchor.X);
        Assert.AreEqual(0.75, primaryAnchor.Y);

        var sourced = opened.Timeline.CursorAnchors.Single(a => a.SourceVideoFilePath is not null);
        Assert.AreEqual(opened.Project.VideoFilePath, sourced.SourceVideoFilePath,
            "the anchor must follow the recording to its restored location");
        Assert.IsFalse(
            sourced.SourceVideoFilePath!.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase),
            "the restored path must not point back at the machine that saved it");
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesHiddenCursorType_GloballyAndPerSegment()
    {
        // CursorType is serialized BY NAME (MixtriPackage.JsonOptions registers a
        // JsonStringEnumConverter), so a newly appended member is only safe if it actually
        // round-trips under that name — both on the global composition and on the per-segment
        // override, which is the surface that lets one clip hide its cursor.
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        var video = timeline.Segments.OfType<VideoSegment>().Single();
        timeline.Segments[timeline.Segments.IndexOf(video)] = video with
        {
            CursorStyleOverride = new CursorStyle { Type = CursorType.Hidden },
        };

        var composition = new CompositionConfig
        {
            Cursor = new CursorStyle { Type = CursorType.Hidden },
        };

        await MixtriPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(CursorType.Hidden, opened.Composition.Cursor.Type);
        Assert.AreEqual(
            CursorType.Hidden,
            opened.Timeline.Segments.OfType<VideoSegment>().Single().CursorStyleOverride!.Type);
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesEveryEditableSetting()
    {
        // Guards the whole "reopening loses my work" class of bug: text slides, cursor,
        // camera and scene settings must all survive a round trip.
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { Padding = 42, CornerRadius = 17, ShadowEnabled = false },
            Cursor = new CursorStyle { Scale = 1.75f, ClickAnimationEnabled = false, AutoHideEnabled = false },
            Zoom = new AutoZoomConfig { Enabled = false },
            SmoothingAlgorithm = SmoothingAlgorithm.None,
            SmoothingStrength = SmoothingStrength.Subtle,
            WebcamStyle = new WebcamOverlayStyle { Size = 275f },
        };

        var slide = (TextSlideSegment)timeline.Segments[1];
        slide.Text = "Chapter Two";
        slide.FontFamily = "Consolas";
        slide.FontSize = 96;
        slide.IsBold = true;
        slide.TextColor = "#FF00AA";
        slide.BackgroundType = SlideBackgroundType.Gradient;
        slide.GradientAngle = 42;
        slide.Animation = TextSlideAnimation.FadeIn;

        timeline.CameraSegments[0].Enabled = false;
        timeline.CameraSegments[0].FullscreenEnabled = true;
        timeline.IsMicAudioMuted = true;
        timeline.SuppressedClickTicks.Add(123456789L);
        timeline.DisabledClickTicks.Add(987654321L);

        await MixtriPackageService.SaveAsync(packagePath, project, composition, timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        // Scene / frame style
        Assert.AreEqual(42, opened.Composition.Background.Padding);
        Assert.AreEqual(17, opened.Composition.Background.CornerRadius);
        Assert.IsFalse(opened.Composition.Background.ShadowEnabled);

        // Cursor
        Assert.AreEqual(1.75f, opened.Composition.Cursor.Scale);
        Assert.IsFalse(opened.Composition.Cursor.ClickAnimationEnabled);
        Assert.IsFalse(opened.Composition.Cursor.AutoHideEnabled);
        Assert.AreEqual(SmoothingAlgorithm.None, opened.Composition.SmoothingAlgorithm);
        Assert.AreEqual(SmoothingStrength.Subtle, opened.Composition.SmoothingStrength);

        // Zoom
        Assert.IsFalse(opened.Composition.Zoom.Enabled);
        Assert.AreEqual(1, opened.Timeline.ZoomKeyframes.Count);

        // Camera
        Assert.AreEqual(275f, opened.Composition.WebcamStyle!.Size);
        Assert.IsFalse(opened.Timeline.CameraSegments[0].Enabled);
        Assert.IsTrue(opened.Timeline.CameraSegments[0].FullscreenEnabled);

        // Text slide
        var restoredSlide = opened.Timeline.Segments.OfType<TextSlideSegment>().Single();
        Assert.AreEqual("Chapter Two", restoredSlide.Text);
        Assert.AreEqual("Consolas", restoredSlide.FontFamily);
        Assert.AreEqual(96, restoredSlide.FontSize);
        Assert.IsTrue(restoredSlide.IsBold);
        Assert.AreEqual("#FF00AA", restoredSlide.TextColor);
        Assert.AreEqual(SlideBackgroundType.Gradient, restoredSlide.BackgroundType);
        Assert.AreEqual(42, restoredSlide.GradientAngle);
        Assert.AreEqual(TextSlideAnimation.FadeIn, restoredSlide.Animation);

        // Track state
        Assert.IsTrue(opened.Timeline.IsMicAudioMuted);
        CollectionAssert.Contains(opened.Timeline.SuppressedClickTicks.ToList(), 123456789L);

        // Disabled clicks are a SEPARATE persisted set from the auto-zoom suppressions; a
        // project must round-trip both without either leaking into the other.
        CollectionAssert.Contains(opened.Timeline.DisabledClickTicks.ToList(), 987654321L);
        CollectionAssert.DoesNotContain(opened.Timeline.DisabledClickTicks.ToList(), 123456789L);
        CollectionAssert.DoesNotContain(opened.Timeline.SuppressedClickTicks.ToList(), 987654321L);
    }

    [TestMethod]
    public async Task Save_DoesNotMutateTheLiveComposition()
    {
        var (project, timeline) = BuildProject();
        var wallpaper = WriteFile("wallpaper.png", 64, 0x55);
        var packagePath = Path.Combine(_root, "project.mixtri");
        var composition = new CompositionConfig
        {
            Background = new BackgroundStyle { BackgroundImagePath = wallpaper },
        };

        await MixtriPackageService.SaveAsync(packagePath, project, composition, timeline);

        Assert.AreEqual(wallpaper, composition.Background.BackgroundImagePath,
            "saving must not rewrite the composition the editor is still using");
    }

    [TestMethod]
    public async Task Save_ProducesASingleFileNotAFolder()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        Assert.IsTrue(File.Exists(packagePath));
        Assert.IsFalse(Directory.Exists(packagePath));
        Assert.IsFalse(File.Exists(packagePath + ".saving"), "temp file should be cleaned up");
    }

    [TestMethod]
    public async Task Save_StoresCompressedMediaWithoutRecompressing()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        using var archive = ZipFile.OpenRead(packagePath);
        var mp4 = archive.Entries.Single(e => e.FullName.EndsWith("video.mp4", StringComparison.Ordinal));
        var wav = archive.Entries.Single(e => e.FullName.EndsWith("system.wav", StringComparison.Ordinal));

        Assert.AreEqual(mp4.Length, mp4.CompressedLength, "MP4 should be stored, not deflated");
        Assert.IsTrue(wav.CompressedLength < wav.Length, "PCM audio should be deflated");
    }

    [TestMethod]
    public async Task Open_RejectsAPackageFromANewerSchema()
    {
        var packagePath = Path.Combine(_root, "future.mixtri");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(MixtriPackage.ManifestEntryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write($$"""{"SchemaVersion": {{MixtriPackage.CurrentSchemaVersion + 1}}}""");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MixtriPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_RejectsANonProjectArchive()
    {
        var packagePath = Path.Combine(_root, "bogus.mixtri");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MixtriPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_RejectsEntriesThatEscapeTheMediaFolder()
    {
        var packagePath = Path.Combine(_root, "evil.mixtri");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry(MixtriPackage.ManifestEntryName);
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write($$"""{"SchemaVersion": {{MixtriPackage.CurrentSchemaVersion}}}""");

            var escape = archive.CreateEntry(MixtriPackage.MediaEntryPrefix + "../../pwned.txt");
            using var escapeWriter = new StreamWriter(escape.Open());
            escapeWriter.Write("nope");
        }

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => MixtriPackageService.OpenAsync(packagePath, _workingRoot));
    }

    [TestMethod]
    public async Task Open_MissingFile_Throws()
    {
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => MixtriPackageService.OpenAsync(
                Path.Combine(_root, "nope.mixtri"), _workingRoot));
    }

    [TestMethod]
    public async Task SaveThenReadManifest_ReturnsMetadataWithoutExtracting()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        var manifest = MixtriPackageService.ReadManifest(packagePath);

        Assert.IsNotNull(manifest);
        Assert.AreEqual("Round trip", manifest.Project.Name);
        Assert.AreEqual(TimeSpan.FromSeconds(10), manifest.Project.Duration);
        Assert.AreEqual(MixtriPackage.CurrentSchemaVersion, manifest.SchemaVersion);

        // Listing projects must not spill extracted media anywhere.
        Assert.AreEqual(0, Directory.GetFileSystemEntries(_workingRoot).Length);
    }

    [TestMethod]
    public void ReadManifest_NonPackage_ReturnsNull()
    {
        var bogus = Path.Combine(_root, "not-a-package.mixtri");
        File.WriteAllText(bogus, "definitely not a zip");

        Assert.IsNull(MixtriPackageService.ReadManifest(bogus));
        Assert.IsNull(MixtriPackageService.ReadPoster(bogus));
    }

    [TestMethod]
    public void ReadManifest_MissingFile_ReturnsNull()
    {
        Assert.IsNull(MixtriPackageService.ReadManifest(Path.Combine(_root, "nope.mixtri")));
        Assert.IsNull(MixtriPackageService.ReadPoster(Path.Combine(_root, "nope.mixtri")));
    }

    [TestMethod]
    public async Task SaveThenOpen_SucceedsWhenNoPosterCanBeRendered()
    {
        // The stub video is not decodable, so no poster is produced. That must not stop
        // the project from saving or reopening — the poster is presentation only.
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.IsNull(MixtriPackageService.ReadPoster(packagePath));
        Assert.AreEqual("Round trip", opened.Project.Name);
    }

    [TestMethod]
    public async Task SaveOpenResave_DoesNotStackEntryPrefixes()
    {
        // Extraction restores the original file name, so re-saving an opened project keeps
        // entry names stable instead of growing 0_video.mp4 -> 0_0_video.mp4 -> ...
        var (project, timeline) = BuildProject();
        var first = Path.Combine(_root, "first.mixtri");
        var second = Path.Combine(_root, "second.mixtri");

        await MixtriPackageService.SaveAsync(first, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(first, _workingRoot);

        await MixtriPackageService.SaveAsync(
            second, opened.Project, opened.Composition, opened.Timeline);

        using var archive = ZipFile.OpenRead(second);
        var mediaNames = archive.Entries
            .Where(e => e.FullName.StartsWith("media/", StringComparison.Ordinal))
            .Select(e => e.FullName)
            .ToList();

        CollectionAssert.Contains(mediaNames, "media/0_video.mp4");
        Assert.IsFalse(mediaNames.Any(n => n.Contains("0_0_", StringComparison.Ordinal)),
            $"entry names stacked prefixes: {string.Join(", ", mediaNames)}");
    }

    [TestMethod]
    public async Task Open_ReExtractsMediaThatChangedWithoutChangingSize()
    {
        // Extraction reuses whatever is already in the media folder. Size alone is not
        // identity: re-saving a project can replace media with a different take of exactly
        // the same byte length (a re-recorded WAV, a re-encoded MP4), and reusing the old
        // bytes would silently open the project with the previous audio or video.
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var first = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        long originalLength = new FileInfo(first.Project.VideoFilePath).Length;

        // Replace the source recording with a different take of identical length, then
        // re-save over the same package — the path a user actually takes.
        var replacement = new byte[originalLength];
        for (int i = 0; i < replacement.Length; i++)
            replacement[i] = 0x5C;

        File.WriteAllBytes(project.VideoFilePath, replacement);
        File.SetLastWriteTimeUtc(project.VideoFilePath, DateTime.UtcNow.AddMinutes(5));

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var second = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        var reopened = File.ReadAllBytes(second.Project.VideoFilePath);

        Assert.AreEqual(originalLength, reopened.Length, "the replacement must be the same size");
        CollectionAssert.AreEqual(
            replacement, reopened,
            "the changed media should have been re-extracted, not reused from the previous open");
    }

    [TestMethod]
    public void StripEntryIndexPrefix_RemovesExactlyOneGroup()
    {
        Assert.AreEqual("video.mp4", MixtriPackage.StripEntryIndexPrefix("0_video.mp4"));
        Assert.AreEqual("video.mp4", MixtriPackage.StripEntryIndexPrefix("12_video.mp4"));

        // A user file that genuinely starts with digits keeps its own name, because only
        // the single prefix this format adds is removed.
        Assert.AreEqual("2024_recap.mp4", MixtriPackage.StripEntryIndexPrefix("0_2024_recap.mp4"));

        // Names without a prefix are left alone.
        Assert.AreEqual("video.mp4", MixtriPackage.StripEntryIndexPrefix("video.mp4"));
        Assert.AreEqual("_video.mp4", MixtriPackage.StripEntryIndexPrefix("_video.mp4"));
    }

    [TestMethod]
    public async Task Save_AdoptsTheChosenFileNameAsTheProjectName()
    {
        // The name in the manifest must match the file the user chose, so the Projects
        // page and export prefill show that rather than the auto-generated capture name.
        var (project, timeline) = BuildProject();
        project.Name = "Recording 2026-07-29 20:37";

        var packagePath = Path.Combine(_root, "Sample5.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        // MixtriPackageService itself does not rename; ProjectService does before calling it.
        // Verify the manifest faithfully carries whatever name it was handed.
        var manifest = MixtriPackageService.ReadManifest(packagePath);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("Recording 2026-07-29 20:37", manifest.Project.Name);

        project.Name = "Sample5";
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        manifest = MixtriPackageService.ReadManifest(packagePath);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("Sample5", manifest.Project.Name);
    }

    [TestMethod]
    public void IsPackagePath_MatchesProjectExtensionsOnly()
    {
        Assert.IsTrue(MixtriPackage.IsPackagePath(@"C:\a\b.mixtri"));
        Assert.IsTrue(MixtriPackage.IsPackagePath(@"C:\a\b.MIXTRI"));
        Assert.IsFalse(MixtriPackage.IsPackagePath(@"C:\a\b.mp4"));
        Assert.IsFalse(MixtriPackage.IsPackagePath(null));
        Assert.IsFalse(MixtriPackage.IsPackagePath("   "));
    }

    /// <summary>
    /// Projects saved before the Musio → Mixtri rename must keep opening. The format did not
    /// change, so <c>.musio</c> stays readable indefinitely.
    /// </summary>
    [TestMethod]
    public void IsPackagePath_AcceptsLegacyMusioExtension()
    {
        Assert.IsTrue(MixtriPackage.IsPackagePath(@"C:\a\b.musio"));
        Assert.IsTrue(MixtriPackage.IsPackagePath(@"C:\a\b.MUSIO"));
        CollectionAssert.Contains(MixtriPackage.LegacyFileExtensions, ".musio");
    }

    /// <summary>
    /// The app must never WRITE a legacy extension: <see cref="MixtriPackage.FileExtension"/> is
    /// what the save picker offers, so re-saving an old project migrates it to the new name.
    /// </summary>
    [TestMethod]
    public void AllExtensions_LeadsWithTheWrittenExtensionAndIncludesLegacy()
    {
        Assert.AreEqual(".mixtri", MixtriPackage.FileExtension);
        Assert.AreEqual(MixtriPackage.FileExtension, MixtriPackage.AllExtensions[0]);
        CollectionAssert.IsSubsetOf(MixtriPackage.LegacyFileExtensions, MixtriPackage.AllExtensions);
        CollectionAssert.DoesNotContain(MixtriPackage.LegacyFileExtensions, MixtriPackage.FileExtension);
    }

    /// <summary>
    /// A package written under the current extension and one renamed to <c>.musio</c> are the
    /// same bytes — the rename introduced an alias, not a second format.
    /// </summary>
    [TestMethod]
    public async Task ReadManifest_ReadsAPackageRenamedToTheLegacyExtension()
    {
        var (project, timeline) = BuildProject();
        project.Name = "Pre-rename project";

        var packagePath = Path.Combine(_root, "legacy" + MixtriPackage.FileExtension);
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        var legacyPath = Path.ChangeExtension(packagePath, ".musio");
        File.Move(packagePath, legacyPath);

        Assert.IsTrue(MixtriPackage.IsPackagePath(legacyPath));

        var manifest = MixtriPackageService.ReadManifest(legacyPath);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("Pre-rename project", manifest.Project.Name);
    }

    // ── Text overlays ────────────────────────────────────────────────────

    /// <summary>
    /// Extends <see cref="BuildProject"/> with an appended (secondary) recording and two
    /// text overlays: one authored against the primary recording (<c>SourceVideoFilePath
    /// == null</c>) and one authored against the appended recording, exercising the same
    /// per-recording source-time ownership as <c>ZoomKeyframe.SourceVideoFilePath</c>.
    /// </summary>
    private (Project Project, TimelineModel Timeline, string AppendedVideoPath) BuildProjectWithTextOverlays()
    {
        var (project, timeline) = BuildProject();
        var appendedVideo = WriteFile("appended.mp4", 2048, 0x09);

        project.Sources.Add(new RecordingSource
        {
            VideoFilePath = appendedVideo,
            CursorDataFilePath = WriteFile("appended_cursor.mcur", 128, 0x0A),
            Duration = TimeSpan.FromSeconds(6),
            Width = 1920,
            Height = 1080,
            Fps = 30,
        });

        timeline.TextOverlays.Add(new TextOverlaySegment
        {
            Text = "Primary Caption",
            Start = TimeSpan.FromSeconds(1),
            Duration = TimeSpan.FromSeconds(3),
            Animation = TextSlideAnimation.CascadePop,
            Anchor = TextOverlayAnchor.TopLeft,
            Background = TextOverlayBackground.GradientScrim,
            ScrimDirection = ScrimDirection.Left,
            ScrimStrength = 0.42,
            FontFamily = "Consolas",
            FontSize = 51,
            IsBold = true,
            TextColor = "#00FF00",
        });

        timeline.TextOverlays.Add(new TextOverlaySegment
        {
            Text = "Secondary Callout",
            Start = TimeSpan.FromSeconds(2),
            Duration = TimeSpan.FromSeconds(2),
            SourceVideoFilePath = appendedVideo,
            Animation = TextSlideAnimation.BounceIn,
            Anchor = TextOverlayAnchor.Custom,
            X = 0.2,
            Y = 0.3,
            Background = TextOverlayBackground.AccentBar,
            AccentColor = "#FF00AA",
            AccentThickness = 9,
            AccentSide = AccentSide.Bottom,
        });

        return (project, timeline, appendedVideo);
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesTextOverlayProperties()
    {
        var (project, timeline, appendedVideo) = BuildProjectWithTextOverlays();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(2, opened.Timeline.TextOverlays.Count);

        var primary = opened.Timeline.TextOverlays.Single(o => o.Text == "Primary Caption");
        Assert.AreEqual(TimeSpan.FromSeconds(1), primary.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3), primary.Duration);
        Assert.AreEqual(TextSlideAnimation.CascadePop, primary.Animation);
        Assert.AreEqual(TextOverlayAnchor.TopLeft, primary.Anchor);
        Assert.AreEqual(TextOverlayBackground.GradientScrim, primary.Background);
        Assert.AreEqual(ScrimDirection.Left, primary.ScrimDirection);
        Assert.AreEqual(0.42, primary.ScrimStrength);
        Assert.AreEqual("Consolas", primary.FontFamily);
        Assert.AreEqual(51, primary.FontSize);
        Assert.IsTrue(primary.IsBold);
        Assert.AreEqual("#00FF00", primary.TextColor);
        Assert.IsNull(primary.SourceVideoFilePath);

        var secondary = opened.Timeline.TextOverlays.Single(o => o.Text == "Secondary Callout");
        Assert.AreEqual(TimeSpan.FromSeconds(2), secondary.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2), secondary.Duration);
        Assert.AreEqual(TextSlideAnimation.BounceIn, secondary.Animation);
        Assert.AreEqual(TextOverlayAnchor.Custom, secondary.Anchor);
        Assert.AreEqual(0.2, secondary.X);
        Assert.AreEqual(0.3, secondary.Y);
        Assert.AreEqual(TextOverlayBackground.AccentBar, secondary.Background);
        Assert.AreEqual("#FF00AA", secondary.AccentColor);
        Assert.AreEqual(9, secondary.AccentThickness);
        Assert.AreEqual(AccentSide.Bottom, secondary.AccentSide);
    }

    [TestMethod]
    public async Task SaveThenOpen_RewritesTextOverlaySourceVideoFilePath()
    {
        var (project, timeline, appendedVideo) = BuildProjectWithTextOverlays();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        var secondary = opened.Timeline.TextOverlays.Single(o => o.Text == "Secondary Callout");
        Assert.IsNotNull(secondary.SourceVideoFilePath);
        Assert.IsTrue(File.Exists(secondary.SourceVideoFilePath), "the appended recording must travel with the package");
        Assert.IsFalse(
            secondary.SourceVideoFilePath!.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase),
            "the restored path must not point back at the machine that saved it");
        Assert.AreNotEqual(appendedVideo, secondary.SourceVideoFilePath);

        // It resolves to the same appended recording that was extracted for the source itself.
        var restoredSource = opened.Project.Sources.Single();
        Assert.AreEqual(restoredSource.VideoFilePath, secondary.SourceVideoFilePath);

        // The primary-authored overlay stays null through the round trip.
        var primary = opened.Timeline.TextOverlays.Single(o => o.Text == "Primary Caption");
        Assert.IsNull(primary.SourceVideoFilePath);
    }

    [TestMethod]
    public async Task SaveThenOpen_TextOverlay_KindDiscriminatorRoundTrips()
    {
        var (project, timeline, _) = BuildProjectWithTextOverlays();
        var packagePath = Path.Combine(_root, "project.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        foreach (var overlay in opened.Timeline.TextOverlays)
            Assert.IsInstanceOfType<TextOverlaySegment>(overlay);
    }

    /// <summary>
    /// A pre-existing <c>.mixtri</c> written before the per-segment text overlay model was
    /// removed still opens. Nothing in the app ever populated <c>VideoSegment.TextOverlays</c>
    /// (the type and property were dead code with no producer and no renderer call site), so
    /// no real project can carry overlay data there — but old files DO contain the serialized
    /// empty <c>"TextOverlays": []</c> property, and a future switch to strict member handling
    /// on <see cref="MixtriPackage.JsonOptions"/> would start throwing on every one of them.
    /// This pins the "old files keep opening" guarantee, injecting a populated legacy array to
    /// prove even the strongest form of the old shape is tolerated.
    /// </summary>
    [TestMethod]
    public async Task Open_ToleratesLegacyPerSegmentTextOverlays()
    {
        var (project, timeline) = BuildProject();
        var packagePath = Path.Combine(_root, "legacy-overlays.mixtri");

        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        // Rewrite the packaged manifest so its video segment carries the retired
        // per-segment overlay shape, exactly as a build from before this change would.
        RewriteManifest(packagePath, json =>
            json.Replace("\"$kind\": \"video\"",
                "\"$kind\": \"video\",\n      \"TextOverlays\": [ { \"Id\": \"legacy1\", \"Text\": \"Old overlay\", \"X\": 0.5, \"Y\": 0.5 } ]"));

        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        // Opens cleanly, and everything that IS still modelled survives untouched.
        Assert.AreEqual(project.Id, opened.Project.Id);
        var segment = opened.Timeline.Segments.OfType<VideoSegment>().Single();
        Assert.IsTrue(File.Exists(segment.VideoFilePath), "the segment's media should still resolve");
        Assert.AreEqual(0, opened.Timeline.TextOverlays.Count, "the retired per-segment shape carries no overlays forward");
    }

    [TestMethod]
    public async Task SaveThenOpen_PacksAndRepointsInsertedAudioTracks()
    {
        // An inserted voice-over lives in an app-owned import folder the orphan sweep can
        // reclaim, so a package that dropped it would lose the only copy — and a path left
        // pointing at that folder would break the moment it was swept.
        var (project, timeline) = BuildProject();
        var voicePath = WriteFile("voiceover.wav", 3072, 0x5A);

        timeline.AudioTracks.Add(new AudioTrack
        {
            FilePath = voicePath,
            Name = "Take 1",
            Kind = AudioTrackKind.VoiceOver,
            StartTime = TimeSpan.FromSeconds(2),
            TrimStart = TimeSpan.FromSeconds(1),
            SourceDuration = TimeSpan.FromSeconds(9),
            Duration = TimeSpan.FromSeconds(5),
            Volume = 0.4,
            IsMuted = true,
        });

        var packagePath = Path.Combine(_root, "voiceover.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        var track = opened.Timeline.AudioTracks.Single();
        Assert.IsTrue(File.Exists(track.FilePath), "the inserted audio should be packed and extracted");
        Assert.IsFalse(
            track.FilePath.StartsWith(_sourceFolder, StringComparison.OrdinalIgnoreCase),
            "the restored track must not still point at the import folder");
        CollectionAssert.AreEqual(
            File.ReadAllBytes(voicePath), File.ReadAllBytes(track.FilePath),
            "the audio bytes must survive the round trip");

        Assert.AreEqual("Take 1", track.Name);
        Assert.AreEqual(AudioTrackKind.VoiceOver, track.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(2), track.StartTime);
        Assert.AreEqual(TimeSpan.FromSeconds(1), track.TrimStart);
        Assert.AreEqual(TimeSpan.FromSeconds(9), track.SourceDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(5), track.Duration);
        Assert.AreEqual(0.4, track.Volume);
        Assert.IsTrue(track.IsMuted, "mute state must survive so a disabled track stays disabled");
    }

    [TestMethod]
    public async Task SaveThenOpen_RemembersRecordedAudioMuteState()
    {
        // The mute flags and per-channel gain are the project's whole audio mix; losing
        // either reopens the project at the wrong level.
        var (project, timeline) = BuildProject();
        timeline.IsSystemAudioMuted = true;
        timeline.IsMicAudioMuted = true;
        timeline.SystemAudioVolume = 0.35;
        timeline.MicAudioVolume = 0.8;
        timeline.IsMusicMuted = true;
        timeline.MusicVolume = 0.25;
        timeline.VoiceOverVolume = 0.6;

        var packagePath = Path.Combine(_root, "muted.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.IsTrue(opened.Timeline.IsSystemAudioMuted, "system audio mute must survive a round trip");
        Assert.IsTrue(opened.Timeline.IsMicAudioMuted, "mic mute must survive a round trip");
        Assert.AreEqual(0.35, opened.Timeline.SystemAudioVolume, 0.0001);
        Assert.AreEqual(0.8, opened.Timeline.MicAudioVolume, 0.0001);
        Assert.IsTrue(opened.Timeline.IsMusicMuted, "a muted lane must stay muted");
        Assert.AreEqual(0.25, opened.Timeline.MusicVolume, 0.0001);
        Assert.AreEqual(0.6, opened.Timeline.VoiceOverVolume, 0.0001);
    }

    [TestMethod]
    public async Task Open_LegacyProjectWithoutAMix_DefaultsEveryChannelToFullVolume()
    {
        // Projects saved before the mix existed carry no volume fields. They must come back
        // at full volume, not silent — a gain that defaulted to zero would mute every old
        // project on open.
        //
        // Saved with DELIBERATELY non-default levels, then stripped from the manifest: if the
        // stripping ever stopped matching, the assertions below would see 0.35/0.2 and fail,
        // rather than passing vacuously because the defaults happen to be 1.
        var (project, timeline) = BuildProject();
        timeline.SystemAudioVolume = 0.35;
        timeline.MicAudioVolume = 0.2;
        timeline.VoiceOverVolume = 0.4;
        timeline.MusicVolume = 0.15;

        var packagePath = Path.Combine(_root, "legacy-mix.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        int stripped = 0;
        RewriteManifest(packagePath, json =>
        {
            // Edited as JSON rather than by regex: the last property in an object has no
            // trailing comma, so a text pattern silently misses one of the four (and a
            // pattern loose enough to catch it would leave the document malformed).
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var timelineNode = root["Timeline"]!.AsObject();

            foreach (var name in new[]
                     { "SystemAudioVolume", "MicAudioVolume", "VoiceOverVolume", "MusicVolume" })
            {
                if (timelineNode.Remove(name)) stripped++;
            }

            return root.ToJsonString();
        });

        Assert.AreEqual(4, stripped, "the manifest must actually have carried all four levels");

        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(1.0, opened.Timeline.EffectiveVolume(AudioMixChannel.System), 0.0001);
        Assert.AreEqual(1.0, opened.Timeline.EffectiveVolume(AudioMixChannel.Mic), 0.0001);
        Assert.AreEqual(1.0, opened.Timeline.EffectiveVolume(AudioMixChannel.VoiceOver), 0.0001);
        Assert.AreEqual(1.0, opened.Timeline.EffectiveVolume(AudioMixChannel.Music), 0.0001);
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesVideoTrackIndexAndTextSlideWindow()
    {
        var (project, timeline) = BuildProject();
        var slide = timeline.Segments.OfType<TextSlideSegment>().Single();
        slide.TrackIndex = 1;
        slide.Start = TimeSpan.FromSeconds(2);
        slide.TextInStart = TimeSpan.FromSeconds(0.5);
        slide.TextInDuration = TimeSpan.FromMilliseconds(250);
        slide.TextOutEnd = TimeSpan.FromSeconds(2.5);
        slide.TextOutDuration = TimeSpan.FromMilliseconds(300);

        var packagePath = Path.Combine(_root, "tracks-and-text-window.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        var restored = opened.Timeline.Segments.OfType<TextSlideSegment>().Single();
        Assert.AreEqual(1, restored.TrackIndex);
        Assert.AreEqual(TimeSpan.FromSeconds(2), restored.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), restored.TextInStart);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), restored.TextInDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), restored.TextOutEnd);
        Assert.AreEqual(TimeSpan.FromMilliseconds(300), restored.TextOutDuration);
    }

    /// <summary>
    /// Legacy manifests are edited as JSON, not text: nullable window properties can be the
    /// last member in a segment object, where regex comma stripping silently misses them.
    /// </summary>
    [TestMethod]
    public async Task Open_LegacyProjectWithoutTrackAndTextWindowFields_UsesDefaults()
    {
        var (project, timeline) = BuildProject();
        var slide = timeline.Segments.OfType<TextSlideSegment>().Single();
        slide.TrackIndex = 2;
        slide.Start = TimeSpan.FromSeconds(2);
        slide.TextInStart = TimeSpan.FromSeconds(0.4);
        slide.TextInDuration = TimeSpan.FromMilliseconds(250);
        slide.TextOutEnd = TimeSpan.FromSeconds(2.4);
        slide.TextOutDuration = TimeSpan.FromMilliseconds(350);

        var packagePath = Path.Combine(_root, "legacy-tracks-and-text-window.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);

        int strippedTrackIndices = 0;
        int strippedWindowFields = 0;
        RewriteManifest(packagePath, json =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var segments = root["Timeline"]!["Segments"]!.AsArray();

            foreach (var node in segments)
            {
                var segment = node!.AsObject();
                if (segment.Remove("TrackIndex"))
                    strippedTrackIndices++;

                if ((string?)segment["$kind"] == "textSlide")
                {
                    foreach (var name in new[] { "TextInStart", "TextInDuration", "TextOutEnd", "TextOutDuration" })
                    {
                        if (segment.Remove(name))
                            strippedWindowFields++;
                    }
                }
            }

            return root.ToJsonString();
        });

        Assert.IsTrue(strippedTrackIndices > 0, "the manifest must actually have carried track indices");
        Assert.AreEqual(4, strippedWindowFields, "the manifest must actually have carried all text-window fields");

        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.IsTrue(opened.Timeline.Segments.All(s => s.TrackIndex == 0));
        var restored = opened.Timeline.Segments.OfType<TextSlideSegment>().Single();
        Assert.AreEqual(TimeSpan.Zero, restored.TextInStart);
        Assert.IsNull(restored.TextInDuration);
        Assert.IsNull(restored.TextOutEnd);
        Assert.IsNull(restored.TextOutDuration);
        Assert.AreEqual(TimeSpan.Zero, restored.ResolveTextInStart());
        Assert.AreEqual(TimeSpan.FromMilliseconds(600), restored.ResolveTextInDuration());
        Assert.AreEqual(restored.Duration, restored.ResolveTextOutEnd());
        Assert.AreEqual(TimeSpan.FromMilliseconds(600), restored.ResolveTextOutDuration());
    }

    [TestMethod]
    public async Task SaveThenOpen_DoesNotPersistRegenerableWaveformSamples()
    {
        // Waveforms are a render cache rebuilt from the WAVs on load; persisting them would
        // add thousands of floats to every manifest. This pins that they are excluded — and
        // therefore that the load path MUST regenerate them.
        var (project, timeline) = BuildProject();
        timeline.SystemAudioWaveformSamples = [0.1f, 0.9f, 0.4f];
        timeline.MicAudioWaveformSamples = [0.2f, 0.7f];

        var packagePath = Path.Combine(_root, "waveforms.mixtri");
        await MixtriPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MixtriPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.IsNull(opened.Timeline.SystemAudioWaveformSamples);
        Assert.IsNull(opened.Timeline.MicAudioWaveformSamples);
    }

    /// <summary>Rewrites <c>manifest.json</c> inside a saved package.</summary>
    private static void RewriteManifest(string packagePath, Func<string, string> transform)
    {
        string json;
        using (var read = ZipFile.OpenRead(packagePath))
        {
            var entry = read.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("manifest.json missing from package");
            using var reader = new StreamReader(entry.Open());
            json = reader.ReadToEnd();
        }

        json = transform(json);

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry("manifest.json")!.Delete();
        var replacement = archive.CreateEntry("manifest.json");
        using var writer = new StreamWriter(replacement.Open());
        writer.Write(json);
    }
}
