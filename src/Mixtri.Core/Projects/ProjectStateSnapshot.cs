using System.Text.Json;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Projects;

/// <summary>Detached project state for work that must not observe subsequent editor mutations.</summary>
public sealed class ProjectStateSnapshot
{
    public Project Project { get; }
    public CompositionConfig Composition { get; }
    public TimelineModel? Timeline { get; }

    private ProjectStateSnapshot(Project project, CompositionConfig composition, TimelineModel? timeline)
    {
        Project = project;
        Composition = composition;
        Timeline = timeline;
    }

    /// <summary>Captures synchronously on the thread that owns the editable state.</summary>
    public static ProjectStateSnapshot Capture(
        Project project, CompositionConfig composition, TimelineModel? timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(composition);
        return new(Clone(project), Clone(composition), timeline is null ? null : Clone(timeline));
    }

    private static T Clone<T>(T value) where T : notnull =>
        JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value, MixtriPackage.JsonOptions),
            MixtriPackage.JsonOptions)
        ?? throw new InvalidOperationException($"Failed to snapshot {typeof(T).Name}.");
}
