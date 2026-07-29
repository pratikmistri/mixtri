using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Timeline;

namespace Musio_App.Services;

/// <summary>
/// Singleton service that holds the shared project state flowing
/// between Recording → Editor → Export pages.
/// </summary>
public class ProjectService
{
    private static ProjectService? _instance;
    public static ProjectService Instance => _instance ??= new();

    public Project? CurrentProject { get; set; }
    public CompositionConfig CurrentComposition { get; set; } = new();
    public TimelineModel? CurrentTimeline { get; set; }

    /// <summary>
    /// Path of the <c>.musio</c> file this project was opened from or last saved to,
    /// or null when it has never been saved.
    /// </summary>
    public string? CurrentPackagePath { get; private set; }

    public event EventHandler? ProjectChanged;

    /// <summary>
    /// Folder that <c>.musio</c> packages unpack their media into.
    /// </summary>
    /// <remarks>
    /// Lives under LocalAppData rather than beside the package so opening a project from
    /// a read-only location (a network share, a downloads folder) still works.
    /// </remarks>
    public static string PackageCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musio", "OpenProjects");

    /// <summary>
    /// Saves the current project, composition and timeline into a single
    /// <c>.musio</c> package.
    /// </summary>
    public async Task SavePackageAsync(
        string packagePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (CurrentProject is null)
            throw new InvalidOperationException("There is no project to save.");

        await MusioPackageService.SaveAsync(
            packagePath, CurrentProject, CurrentComposition, CurrentTimeline, progress, ct);

        CurrentPackagePath = packagePath;
    }

    /// <summary>
    /// Opens a <c>.musio</c> package and makes it the current project.
    /// </summary>
    public async Task OpenPackageAsync(
        string packagePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var result = await MusioPackageService.OpenAsync(
            packagePath, PackageCacheRoot, progress, ct);

        CurrentProject = result.Project;
        CurrentComposition = result.Composition;
        CurrentTimeline = result.Timeline;
        CurrentPackagePath = packagePath;

        // A saved package carries its own timeline, so the primary segment must not be
        // synthesized the way SetProject does for a fresh recording.
        if (CurrentTimeline.Segments.Count == 0)
        {
            CurrentTimeline.Segments.Add(CreateVideoSegmentFromProject(result.Project));
            CurrentTimeline.RecalculateSegmentPositions();
        }

        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetProject(Project project)
    {
        CurrentProject = project;
        CurrentPackagePath = null;
        CurrentTimeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            Fps = project.Fps,
            PrimaryVideoFilePath = project.VideoFilePath,
        };

        // Create an initial VideoSegment from the primary recording
        var primarySegment = CreateVideoSegmentFromProject(project);
        CurrentTimeline.Segments.Add(primarySegment);
        CurrentTimeline.RecalculateSegmentPositions();

        // Apply capture-type-specific style defaults at project load time
        // so they're guaranteed to be in place before the editor reads
        // composition state, regardless of which initialization path runs.
        // Full-screen (Monitor) captures default to zeroed padding/shadow/
        // corner radius/border; users can still override via the Style menu
        // and their edits persist for the lifetime of this project.
        if (project.CaptureType is CaptureTargetType.Monitor)
        {
            CurrentComposition = CurrentComposition with
            {
                Background = CurrentComposition.Background with
                {
                    Padding = 0,
                    ShadowEnabled = false,
                    CornerRadius = 0,
                    BorderEnabled = false,
                },
            };
        }

        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Appends a new recording (captured as a separate Project) to the current
    /// project's timeline as an additional <see cref="VideoSegment"/>.
    /// </summary>
    public void AppendRecording(Project newRecording)
    {
        if (CurrentProject is null || CurrentTimeline is null)
        {
            // No existing project — just set this as the primary
            SetProject(newRecording);
            return;
        }

        // Add to sources list
        var source = new RecordingSource
        {
            Id = newRecording.Id,
            VideoFilePath = newRecording.VideoFilePath,
            CursorDataFilePath = newRecording.CursorDataFilePath,
            WebcamFilePath = newRecording.WebcamFilePath,
            KeyboardDataFilePath = newRecording.KeyboardDataFilePath,
            AudioFilePaths = newRecording.AudioFilePaths,
            Duration = newRecording.Duration,
            Width = newRecording.Width,
            Height = newRecording.Height,
            Fps = newRecording.Fps,
            MouseToVideoOffsetSeconds = newRecording.MouseToVideoOffsetSeconds,
            AudioToVideoOffsetSeconds = newRecording.AudioToVideoOffsetSeconds,
            CropOffsetX = newRecording.CropOffsetX,
            CropOffsetY = newRecording.CropOffsetY,
            DpiScale = newRecording.DpiScale,
            CaptureType = newRecording.CaptureType,
        };
        CurrentProject.Sources.Add(source);

        // Create and append a VideoSegment
        var segment = CreateVideoSegmentFromProject(newRecording);
        CurrentTimeline.Segments.Add(segment);
        CurrentTimeline.RecalculateSegmentPositions();

        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private static VideoSegment CreateVideoSegmentFromProject(Project project) => new()
    {
        VideoFilePath = project.VideoFilePath,
        CursorDataFilePath = project.CursorDataFilePath,
        WebcamFilePath = project.WebcamFilePath,
        KeyboardDataFilePath = project.KeyboardDataFilePath,
        AudioFilePaths = [.. project.AudioFilePaths],
        SourceStart = TimeSpan.Zero,
        SourceDuration = project.Duration,
        Duration = project.Duration,
        SourceWidth = project.Width,
        SourceHeight = project.Height,
        Fps = project.Fps,
        MouseToVideoOffsetSeconds = project.MouseToVideoOffsetSeconds,
        AudioToVideoOffsetSeconds = project.AudioToVideoOffsetSeconds,
        DpiScale = project.DpiScale,
        CropOffsetX = project.CropOffsetX,
        CropOffsetY = project.CropOffsetY,
    };
}
