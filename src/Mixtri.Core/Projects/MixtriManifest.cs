using System.Text.Json.Serialization;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Projects;

/// <summary>
/// The JSON document stored as <c>manifest.json</c> inside a <c>.mixtri</c> package.
/// </summary>
/// <remarks>
/// Every media path inside <see cref="Project"/> and <see cref="Timeline"/> is stored as a
/// package-relative entry name (for example <c>media/0_video.mp4</c>) rather than an
/// absolute path, so a package can be copied between machines and still open.
/// </remarks>
public sealed class MixtriManifest
{
    /// <summary>
    /// Format version. Bump on breaking layout changes; readers refuse anything newer
    /// than they understand rather than silently mangling it.
    /// </summary>
    public int SchemaVersion { get; set; } = MixtriPackage.CurrentSchemaVersion;

    /// <summary>Version of the app that wrote the package, for diagnostics.</summary>
    public string? WrittenBy { get; set; }

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    public Project Project { get; set; } = new();

    public CompositionConfig Composition { get; set; } = new();

    public TimelineModel Timeline { get; set; } = new();

    /// <summary>
    /// Package entry names for every media file the project references, in the order they
    /// were added. Purely informational — the authoritative references are the rewritten
    /// paths inside <see cref="Project"/> and <see cref="Timeline"/>.
    /// </summary>
    [JsonPropertyName("media")]
    public List<string> MediaEntries { get; set; } = [];
}
