using Mixtri.Core.Models;

namespace Mixtri.Core.Audio;

/// <summary>
/// One mixer channel the editor exposes a volume and mute control for.
/// </summary>
/// <remarks>
/// Deliberately spans both kinds of audio. The first two are the recording's own captures,
/// identified by file name (see <see cref="RecordedAudio.Classify"/>); the last two are the
/// lanes that inserted <see cref="AudioTrack"/>s live on. They share one enum because every
/// consumer — the label flyouts, the preview engine and the export plan — wants "give me the
/// gain for this channel" without caring which sort it is.
/// </remarks>
public enum AudioMixChannel
{
    /// <summary>Captured system/desktop audio.</summary>
    System,

    /// <summary>Captured microphone audio.</summary>
    Mic,

    /// <summary>The inserted voice-over lane.</summary>
    VoiceOver,

    /// <summary>The inserted music lane.</summary>
    Music,
}

/// <summary>
/// Classifies a recorded audio file as system or microphone capture.
/// </summary>
public static class RecordedAudio
{
    /// <summary>
    /// Whether <paramref name="path"/> is a microphone capture. Anything that is not
    /// recognisably a mic file counts as system audio, matching how the editor has always
    /// built its waveforms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorder names its files <c>mic_*</c> and <c>system_*</c>, and that convention is
    /// the only thing identifying them — the paths carry no other metadata.
    /// </para>
    /// <para>
    /// A leading <c>&lt;digits&gt;_</c> group is skipped before matching. Saving a project
    /// prefixes every packed file with its entry index (<c>3_mic_0.wav</c>), and although
    /// opening normally strips that again, it deliberately keeps the prefix when two
    /// recordings contribute files with the same name. Without this, the surviving prefix
    /// would silently reclassify an appended recording's microphone track as system audio —
    /// so it would ignore the mic's mute and volume.
    /// </para>
    /// </remarks>
    public static bool IsMic(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        int i = 0;
        while (i < name.Length && char.IsAsciiDigit(name[i])) i++;
        if (i > 0 && i < name.Length - 1 && name[i] == '_')
            name = name[(i + 1)..];

        return name.StartsWith("mic_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Which recorded channel <paramref name="path"/> belongs to.</summary>
    public static AudioMixChannel Classify(string? path)
        => IsMic(path) ? AudioMixChannel.Mic : AudioMixChannel.System;

    /// <summary>The mixer channel an inserted track's lane corresponds to.</summary>
    public static AudioMixChannel ChannelFor(AudioTrackKind kind)
        => kind == AudioTrackKind.Music ? AudioMixChannel.Music : AudioMixChannel.VoiceOver;
}
