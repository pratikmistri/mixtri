using System.Text.Json;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Projects;

/// <summary>Detached export inputs, captured on the editor's owner thread before any await.</summary>
public sealed record ProjectStateSnapshot(
    Project Project, CompositionConfig Composition, TimelineModel? Timeline)
{
    public static ProjectStateSnapshot Capture(
        Project project, CompositionConfig composition, TimelineModel? timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(composition);
        var state = new ProjectStateSnapshot(project, composition, timeline);
        return JsonSerializer.Deserialize<ProjectStateSnapshot>(
            JsonSerializer.SerializeToUtf8Bytes(state, MixtriPackage.JsonOptions),
            MixtriPackage.JsonOptions)
            ?? throw new InvalidOperationException("Could not capture the export project.");
    }
}
