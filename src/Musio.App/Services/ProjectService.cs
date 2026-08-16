using Musio.Core.Capture;
using Musio.Core.Media;
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

    /// <summary>
    /// Package whose open is currently in flight, or <c>null</c>. Published before the
    /// first await, because <see cref="CurrentPackagePath"/> is only assigned once the
    /// open finishes — seconds later for a real project — and callers need to tell
    /// "this project is loading" from "this project is loaded".
    /// </summary>
    /// <remarks>UI-thread affine, like the rest of this service.</remarks>
    public string? OpenInFlightPath { get; private set; }

    /// <summary>
    /// Whether a save is running. Same rationale as <see cref="OpenInFlightPath"/>:
    /// <see cref="SavePackageAsync"/> rebinds <see cref="CurrentPackagePath"/> only on
    /// completion, so anything that swaps the project mid-save would leave the new
    /// project bound to the old project's file.
    /// </summary>
    public bool IsSaveInFlight { get; private set; }
    public CompositionConfig CurrentComposition { get; set; } = new();
    public TimelineModel? CurrentTimeline { get; set; }

    /// <summary>
    /// Path of the <c>.musio</c> file this project was opened from or last saved to,
    /// or null when it has never been saved.
    /// </summary>
    public string? CurrentPackagePath { get; private set; }

    /// <summary>
    /// True when the current project's composition and timeline were restored from a
    /// saved package rather than produced by a fresh recording.
    /// </summary>
    /// <remarks>
    /// The editor applies opinionated defaults (cursor style, auto-zoom, smoothing) when
    /// it first opens a recording. Those defaults must not run against a restored project
    /// or they overwrite exactly the choices the user saved.
    /// </remarks>
    public bool IsRestoredFromPackage { get; private set; }

    private readonly HashSet<string> _restoredSourcePaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="videoFilePath"/> came from the restored package, and so
    /// already carries the user's saved zoom and style choices.
    /// </summary>
    /// <remarks>
    /// Tracked per source rather than per project: a recording appended *after* opening a
    /// package is brand new and must still get its first-open defaults and auto-zoom,
    /// even though the project as a whole was restored.
    /// </remarks>
    public bool IsRestoredSource(string? videoFilePath)
        => !string.IsNullOrEmpty(videoFilePath) && _restoredSourcePaths.Contains(videoFilePath);

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

        // Captured before the await: the project can be swapped underneath a running
        // save (a redirected file activation opens into this window), and reading
        // CurrentProject afterwards would stamp the wrong name and duration onto this
        // package's recents entry.
        var project = CurrentProject;

        // The file name the user chose is the project's identity from here on. Without
        // this the project keeps its auto-generated "Recording <timestamp>" name, which
        // then shows on the Projects card and in the export prefill instead of the name
        // they actually picked.
        var chosenName = Path.GetFileNameWithoutExtension(packagePath);
        if (!string.IsNullOrWhiteSpace(chosenName))
            project.Name = chosenName;

        IsSaveInFlight = true;
        try
        {
            await MusioPackageService.SaveAsync(
                packagePath, project, CurrentComposition, CurrentTimeline, progress, ct);

            // Only rebind the current package when the project we just wrote is still
            // the one on screen. A save can be started while an open is already in
            // flight (the redirected open is fire-and-forget, so Ctrl+S stays live),
            // and that open may finish first — publishing its own project and path.
            // Rebinding here regardless would leave the newly opened project pointing
            // at the file this save wrote, so the next save would silently overwrite
            // that file with the wrong project's content. The package itself was
            // written correctly either way, so the recents entry is still accurate.
            if (ReferenceEquals(CurrentProject, project))
                CurrentPackagePath = packagePath;

            RecentProjectsStore.Remember(packagePath, project.Name, project.Duration);
        }
        finally
        {
            IsSaveInFlight = false;
        }
    }

    /// <summary>
    /// Opens a <c>.musio</c> package and makes it the current project.
    /// </summary>
    public async Task OpenPackageAsync(
        string packagePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Marked here rather than at the call sites so every open path is covered —
        // file activation, the Open dialog and the recents list alike.
        OpenInFlightPath = packagePath;
        try
        {
            var result = await MusioPackageService.OpenAsync(
                packagePath, PackageCacheRoot, progress, ct);

            CurrentProject = result.Project;
            CurrentComposition = result.Composition;
            CurrentTimeline = result.Timeline;
            CurrentPackagePath = packagePath;
            IsRestoredFromPackage = true;

            _restoredSourcePaths.Clear();
            foreach (var path in MusioPathRewriter.Enumerate(
                         result.Project, result.Timeline, result.Composition))
            {
                _restoredSourcePaths.Add(path);
            }

            // A saved package carries its own timeline, so the primary segment must not be
            // synthesized the way SetProject does for a fresh recording.
            if (CurrentTimeline.Segments.Count == 0)
            {
                CurrentTimeline.Segments.Add(CreateVideoSegmentFromProject(result.Project));
                CurrentTimeline.RecalculateSegmentPositions();
            }

            RecentProjectsStore.Remember(packagePath, result.Project.Name, result.Project.Duration);

            ProjectChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            OpenInFlightPath = null;
        }
    }

    public void SetProject(Project project)
    {
        CurrentProject = project;
        CurrentPackagePath = null;
        IsRestoredFromPackage = false;
        _restoredSourcePaths.Clear();
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
    /// project's timeline as an additional <see cref="VideoSegment"/>, or inserts it as an
    /// overlay when the playhead is parked mid-edit so existing segments do not ripple.
    /// </summary>
    public VideoSegment? AppendRecording(Project newRecording)
    {
        if (CurrentProject is null || CurrentTimeline is null)
        {
            // No existing project — just set this as the primary
            SetProject(newRecording);
            return CurrentTimeline?.Segments
                .OfType<VideoSegment>()
                .FirstOrDefault(s => string.Equals(
                    s.VideoFilePath, newRecording.VideoFilePath, StringComparison.OrdinalIgnoreCase));
        }

        var segment = AddProjectSourceAndCreateSegment(newRecording);
        if (ShouldAppendRecordingToBaseTrack(CurrentTimeline))
        {
            new AppendVideoSegmentOperation(segment).Execute(CurrentTimeline);
        }
        else
        {
            new InsertSegmentOnOverlayTrackOperation(segment, CurrentTimeline.PlayheadPosition)
                .Execute(CurrentTimeline);
        }

        ProjectChanged?.Invoke(this, EventArgs.Empty);
        return segment;
    }

    /// <summary>
    /// Turns an external video file (already normalised by <see cref="VideoImportService"/>
    /// into a constant-frame-rate H.264 clip plus extracted audio) into a project source and
    /// inserts it on an overlay track at the requested output time.
    /// </summary>
    /// <remarks>
    /// An imported clip has none of the metadata a live capture produces: there is no cursor,
    /// click or keystroke log, no crop offset, no DPI scale and no audio/mouse alignment to
    /// correct. Those are therefore all zeroed here (<see cref="Project.CursorDataFilePath"/> is
    /// left empty and <see cref="Project.KeyboardDataFilePath"/> null), and every consumer keys
    /// off "no cursor data" to skip click-driven behaviour (auto-zoom, cursor overlay) rather
    /// than fabricate it. <see cref="Project.DpiScale"/> is 1 because the pixels are already the
    /// real frame pixels — there is no logical→physical mapping to undo.
    /// </remarks>
    public VideoSegment? ImportVideo(VideoImportResult result, TimeSpan? insertAt = null)
    {
        var project = new Project
        {
            Name = ProjectNameFromImport(result),
            VideoFilePath = result.VideoFilePath,
            CursorDataFilePath = string.Empty,
            KeyboardDataFilePath = null,
            AudioFilePaths = [.. result.AudioFilePaths],
            Duration = result.Duration,
            Width = result.Width,
            Height = result.Height,
            Fps = result.Fps,
            MouseToVideoOffsetSeconds = 0,
            AudioToVideoOffsetSeconds = 0,
            CropOffsetX = 0,
            CropOffsetY = 0,
            DpiScale = 1,
            CaptureType = CaptureTargetType.Monitor,
        };

        // Importing is also a valid way to START a project; in that case the imported clip is
        // the primary base-track segment because there is no existing edit to cover.
        if (CurrentProject is null || CurrentTimeline is null)
        {
            SetProject(project);
            return CurrentTimeline?.Segments
                .OfType<VideoSegment>()
                .FirstOrDefault(s => string.Equals(
                    s.VideoFilePath, project.VideoFilePath, StringComparison.OrdinalIgnoreCase));
        }

        var segment = AddProjectSourceAndCreateSegment(project);
        new InsertSegmentOnOverlayTrackOperation(segment, insertAt ?? CurrentTimeline.PlayheadPosition)
            .Execute(CurrentTimeline);

        ProjectChanged?.Invoke(this, EventArgs.Empty);
        return segment;
    }

    /// <summary>
    /// Registers a new recording/import as a project source and returns the segment that will
    /// be placed on either the base track or an overlay lane by the caller.
    /// </summary>
    private VideoSegment AddProjectSourceAndCreateSegment(Project newRecording)
    {
        CurrentProject!.Sources.Add(new RecordingSource
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
        });

        return CreateVideoSegmentFromProject(newRecording);
    }

    /// <summary>
    /// Treats a recording append as "continue recording" only when the playhead is already at
    /// the tail; mid-timeline appends are overlay inserts so existing edits do not ripple.
    /// </summary>
    private static bool ShouldAppendRecordingToBaseTrack(TimelineModel timeline)
    {
        var end = timeline.DisplayDuration;
        if (end <= TimeSpan.Zero) return true;

        var fps = timeline.Fps > 0 ? timeline.Fps : 30;
        var oneFrame = TimeSpan.FromSeconds(1.0 / fps);
        return timeline.PlayheadPosition >= end - oneFrame;
    }

    /// <summary>
    /// Derives a project/segment name from the imported file's original name, falling back to
    /// a generic label when the source name is unavailable.
    /// </summary>
    private static string ProjectNameFromImport(VideoImportResult result)
    {
        var name = Path.GetFileNameWithoutExtension(result.SourceFileName);
        return string.IsNullOrWhiteSpace(name) ? "Imported video" : name;
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
