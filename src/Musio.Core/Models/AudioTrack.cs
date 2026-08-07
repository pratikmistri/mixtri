namespace Musio.Core.Models;

/// <summary>
/// What an <see cref="AudioTrack"/> was inserted as. Purely descriptive — it drives the
/// track's default volume, its label and its colour, never how the audio is decoded or
/// muxed (both kinds take exactly the same path through the exporter and the preview
/// engine).
/// </summary>
public enum AudioTrackKind
{
    /// <summary>Narration inserted over the recording; defaults to full volume.</summary>
    VoiceOver,

    /// <summary>Background music; defaults to a lower volume so it sits under narration.</summary>
    Music,
}

/// <summary>
/// An externally supplied audio file placed at a chosen position on the OUTPUT timeline.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT another entry in <see cref="Project.AudioFilePaths"/>. Those are the
/// recording's own system/mic captures: every consumer assumes they start at the recording's
/// frame 0 (offset only by the single project-wide
/// <see cref="Project.AudioToVideoOffsetSeconds"/>) and that they follow the segments when
/// the timeline is cut, trimmed or reordered. A voice-over or a music bed is the opposite —
/// it is anchored to a point on the finished timeline the user picked, and must NOT be
/// re-cut when the footage under it changes.
/// </para>
/// <para>
/// <b>Times are output-timeline times.</b> <see cref="StartTime"/> is measured on the
/// exported/previewed timeline, not in any source recording's own clock, which is what makes
/// this type independent of segment mapping.
/// </para>
/// <para>
/// <b>Why there are no fade fields.</b> Export muxes through
/// <c>MediaComposition</c>/<c>BackgroundAudioTrack</c>, which exposes a constant
/// <c>Volume</c> but no gain envelope — see <c>VideoEncoder.ExportTakeDuration</c>'s remarks
/// for the full writeup of why a time-varying ramp cannot be applied in that pipeline. A
/// per-track fade would therefore be silently ignored on export while working in preview, so
/// this model exposes only what both pipelines can honour: a constant
/// <see cref="Volume"/> and <see cref="IsMuted"/>.
/// </para>
/// </remarks>
public class AudioTrack
{
    /// <summary>Stable identity, used by the editor to select and update a track.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Absolute path of the normalised WAV produced by <c>AudioImportService</c>.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Display name, derived from the imported file's original name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this was inserted as narration or as a music bed.</summary>
    public AudioTrackKind Kind { get; set; } = AudioTrackKind.VoiceOver;

    /// <summary>Where this track starts on the OUTPUT timeline.</summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>How far into the source file playback begins.</summary>
    public TimeSpan TrimStart { get; set; }

    /// <summary>Full duration of the source file, measured at import.</summary>
    public TimeSpan SourceDuration { get; set; }

    /// <summary>
    /// How much of the source to play, or <c>null</c> for "everything after
    /// <see cref="TrimStart"/>". Prefer <see cref="EffectiveDuration"/>, which resolves the
    /// null case and clamps against what the file actually contains.
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Constant playback gain, 0..1. Muxed as <c>BackgroundAudioTrack.Volume</c>.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>When true the track is skipped by preview and export alike, but kept in the project.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Default gain for a newly inserted track of <paramref name="kind"/>.</summary>
    /// <remarks>
    /// Music is inserted quieter than narration so a freshly added bed does not drown the
    /// recording's own audio — the common reason a first music insert sounds "broken".
    /// </remarks>
    public static double DefaultVolumeFor(AudioTrackKind kind)
        => kind == AudioTrackKind.Music ? 0.35 : 1.0;

    /// <summary>
    /// How long this track actually sounds for: <see cref="Duration"/> when set, otherwise
    /// the rest of the file, in both cases clamped to what remains after
    /// <see cref="TrimStart"/>. <see cref="TimeSpan.Zero"/> when nothing is playable.
    /// </summary>
    public TimeSpan EffectiveDuration
    {
        get
        {
            var trim = TrimStart < TimeSpan.Zero ? TimeSpan.Zero : TrimStart;
            var available = SourceDuration > trim ? SourceDuration - trim : TimeSpan.Zero;

            // A source duration of zero means "never measured" (a hand-built project, or a
            // probe that failed at import), not "empty file" — clamping to it would silence
            // the track. Fall back to the requested duration and let the decoder bound it.
            if (SourceDuration <= TimeSpan.Zero)
                return Duration is { } unmeasured && unmeasured > TimeSpan.Zero ? unmeasured : TimeSpan.Zero;

            if (Duration is not { } requested || requested <= TimeSpan.Zero)
                return available;

            return requested < available ? requested : available;
        }
    }

    /// <summary>Where this track stops sounding on the OUTPUT timeline.</summary>
    public TimeSpan End => StartTime + EffectiveDuration;

    /// <summary>
    /// The gain to actually apply, with mute folded in and the stored value clamped to the
    /// 0..1 range <c>BackgroundAudioTrack.Volume</c> accepts.
    /// </summary>
    public double EffectiveVolume => IsMuted ? 0.0 : Math.Clamp(Volume, 0.0, 1.0);

    /// <summary>
    /// Whether this track contributes any audio at all — false for a muted, silenced,
    /// pathless or zero-length track, all of which callers skip rather than mux.
    /// </summary>
    public bool IsAudible =>
        !string.IsNullOrWhiteSpace(FilePath)
        && EffectiveVolume > 0
        && EffectiveDuration > TimeSpan.Zero;
}
