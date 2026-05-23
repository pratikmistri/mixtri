using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
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

    public event EventHandler? ProjectChanged;

    public void SetProject(Project project)
    {
        CurrentProject = project;
        CurrentTimeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            Fps = project.Fps
        };

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
}
