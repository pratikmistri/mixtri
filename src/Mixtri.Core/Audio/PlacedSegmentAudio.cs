using System.Globalization;
using System.Text;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Audio;

/// <summary>
/// Chooses which recorded-audio placements the editor preview plays through its
/// OUTPUT-time placement engine, and converts them into what that engine loads.
/// </summary>
/// <remarks>
/// <para>
/// Preview has two ways to play recorded audio. The primary recording's own system/mic files
/// at 1x go through a continuous engine seeked through the segment mapping
/// (<c>EditorPage.AudioPositionForVideo</c>). Everything else that is cut from a recording is
/// placed on the output timeline exactly as <see cref="ExportAudioPlan"/> places it:
/// speed-adjusted segments (whose placements carry a <see cref="AudioPlacement.Stretch"/>
/// request) and — the case this type was introduced for — every native-rate segment of an
/// APPENDED or imported recording.
/// </para>
/// <para>
/// Appended recordings used to have no preview path at all: the continuous engine only ever
/// loads <see cref="Project.AudioFilePaths"/>, so an audio-bearing recording appended to a
/// project whose first recording had no audio was silent in the editor while export placed
/// it at the segment. Reading the placements from <see cref="ExportAudioPlan.BuildRecorded"/>
/// means preview inherits export's per-recording audio offset, per-segment and per-channel
/// mutes, overlay visibility and mix instead of re-deriving any of them.
/// </para>
/// </remarks>
public static class PlacedSegmentAudio
{
    /// <summary>
    /// The recorded placements the placement engine must play: every placement with a
    /// time-stretch request, plus every native-rate audio-file placement that does NOT read
    /// one of the primary recording's own files (those stay on the continuous engine).
    /// </summary>
    public static IReadOnlyList<AudioPlacement> Select(Project project, TimelineModel timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(timeline);

        var selected = new List<AudioPlacement>();
        if (timeline.Segments.Count == 0) return selected;

        var primaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in project.AudioFilePaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path)) primaryFiles.Add(path);
        }

        foreach (var placement in ExportAudioPlan.BuildRecorded(project, timeline, null))
        {
            if (placement.Stretch is not null)
            {
                selected.Add(placement);
                continue;
            }

            // Embedded tracks are not opened by the preview engine (recordings and imports
            // both keep their audio in separate WAVs), and the primary's own files are
            // already played by the continuous engine.
            if (placement.Kind != AudioSourceKind.AudioFile) continue;
            if (primaryFiles.Contains(placement.SourcePath)) continue;

            selected.Add(placement);
        }

        return selected;
    }

    /// <summary>
    /// Converts a native-rate placement into the engine's form, or <c>null</c> when it
    /// would never sound.
    /// </summary>
    /// <remarks>
    /// Uses the hard-cut take export actually muxes: neither pipeline ramps a transition
    /// crossfade, so the fade extension <see cref="ExportAudioPlan"/> records is subtracted
    /// back out here exactly as <c>VideoEncoder.ExportTakeDuration</c> does — otherwise the
    /// outgoing segment's audio would keep playing under the next segment in preview only.
    /// </remarks>
    public static AudioTimelinePlacement? ToTimelinePlacement(AudioPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(placement.SourcePath)) return null;
        if (placement.TakeDuration is not { } take) return null;

        if (placement.FadeOutDuration > TimeSpan.Zero && !placement.PlaysAtNativeRateOnSpeedAdjustedSegment)
            take -= placement.FadeOutDuration;
        if (take <= TimeSpan.Zero) return null;

        float volume = (float)Math.Clamp(placement.Volume, 0.0, 1.0);
        if (volume <= 0f) return null;

        var trim = placement.TrimFromStart < TimeSpan.Zero ? TimeSpan.Zero : placement.TrimFromStart;
        var delay = placement.Delay < TimeSpan.Zero ? TimeSpan.Zero : placement.Delay;

        return new AudioTimelinePlacement(placement.SourcePath, delay, trim, take, volume);
    }

    /// <summary>
    /// A string that changes whenever anything the placement engine plays would change, so
    /// the editor rebuilds (and re-renders stretches) only when it must.
    /// </summary>
    /// <param name="placements">The placements, in the order they are loaded.</param>
    /// <param name="includeVolume">
    /// <c>false</c> leaves every gain out, so a caller can tell "only levels moved" (re-level
    /// the loaded engine in place) from "the layout changed" (rebuild it).
    /// </param>
    public static string Signature(IEnumerable<AudioPlacement> placements, bool includeVolume = true)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var sb = new StringBuilder();
        foreach (var p in placements)
        {
            sb.Append(p.SourcePath).Append('|')
              .Append(p.Delay.Ticks).Append('|')
              .Append(p.TrimFromStart.Ticks).Append('|')
              .Append(p.TakeDuration?.Ticks ?? -1).Append('|')
              .Append(p.FadeOutDuration.Ticks);

            if (includeVolume)
                sb.Append('|').Append(p.Volume.ToString("R", CultureInfo.InvariantCulture));

            if (p.Stretch is { } s)
            {
                sb.Append("|s:")
                  .Append(s.Speed.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(s.SourceStart.Ticks).Append('|')
                  .Append(s.SourceDuration.Ticks).Append('|')
                  .Append(s.OutputDuration.Ticks);
            }

            sb.Append(';');
        }

        return sb.ToString();
    }
}
