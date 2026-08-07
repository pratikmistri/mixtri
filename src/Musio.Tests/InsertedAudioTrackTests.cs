using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Regression tests for inserted audio (<see cref="AudioTrack"/>): the voice-over/music
/// tracks the Insert menu adds, which — unlike the recording's own audio — are anchored to
/// the OUTPUT timeline and must never be re-cut when the footage under them is edited.
/// </summary>
[TestClass]
public sealed class InsertedAudioTrackTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";
    private const string PrimaryMic = @"C:\rec\primary\mic_0.wav";
    private const string VoicePath = @"C:\imports\import_a\audio.wav";
    private const string MusicPath = @"C:\imports\import_b\audio.wav";

    private static Project NewProject(params string[] audioPaths) => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(10),
        AudioFilePaths = [.. audioPaths],
    };

    private static VideoSegment Video(double sourceStartSec, double sourceDurationSec) => new()
    {
        VideoFilePath = Primary,
        SourceStart = TimeSpan.FromSeconds(sourceStartSec),
        SourceDuration = TimeSpan.FromSeconds(sourceDurationSec),
        Duration = TimeSpan.FromSeconds(sourceDurationSec),
        AudioFilePaths = [PrimaryMic],
    };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    private static AudioTrack Track(
        string path = VoicePath,
        double startSec = 0,
        double sourceDurationSec = 8,
        AudioTrackKind kind = AudioTrackKind.VoiceOver) => new()
        {
            FilePath = path,
            Name = "Take 1",
            Kind = kind,
            StartTime = TimeSpan.FromSeconds(startSec),
            SourceDuration = TimeSpan.FromSeconds(sourceDurationSec),
            Volume = AudioTrack.DefaultVolumeFor(kind),
        };

    private static void AssertSeconds(double expected, TimeSpan actual, string message = "")
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    #region Model

    [TestMethod]
    public void EffectiveDuration_DefaultsToTheRestOfTheFileAfterTheTrim()
    {
        var track = Track(sourceDurationSec: 8);
        track.TrimStart = TimeSpan.FromSeconds(3);

        AssertSeconds(5, track.EffectiveDuration, "trim must come off the available length");
        AssertSeconds(track.StartTime.TotalSeconds + 5, track.End, "End follows EffectiveDuration");
    }

    [TestMethod]
    public void EffectiveDuration_IsClampedToWhatTheFileActuallyHas()
    {
        var track = Track(sourceDurationSec: 8);
        track.TrimStart = TimeSpan.FromSeconds(6);
        track.Duration = TimeSpan.FromSeconds(30);

        AssertSeconds(2, track.EffectiveDuration,
            "a requested duration longer than the file must clamp, or export plans a take past EOF");
    }

    [TestMethod]
    public void EffectiveDuration_FallsBackToTheRequestedLength_WhenTheSourceWasNeverMeasured()
    {
        // SourceDuration == 0 means "never probed" (hand-built project / failed import),
        // NOT "empty file" — clamping to it would silence the track outright.
        var track = Track(sourceDurationSec: 0);
        track.Duration = TimeSpan.FromSeconds(4);

        AssertSeconds(4, track.EffectiveDuration, "an unmeasured source must not silence the track");
    }

    [TestMethod]
    public void MutedTrack_IsSilentAndNotAudible()
    {
        var track = Track();
        track.IsMuted = true;

        Assert.AreEqual(0.0, track.EffectiveVolume, "mute must fold into the effective volume");
        Assert.IsFalse(track.IsAudible, "a muted track must not be muxed");
    }

    [TestMethod]
    public void Volume_IsClampedToTheRangeBackgroundAudioTrackAccepts()
    {
        var loud = Track();
        loud.Volume = 4.0;
        Assert.AreEqual(1.0, loud.EffectiveVolume, "above-unity gain must clamp to 1");

        var negative = Track();
        negative.Volume = -1.0;
        Assert.AreEqual(0.0, negative.EffectiveVolume, "negative gain must clamp to 0");
        Assert.IsFalse(negative.IsAudible);
    }

    [TestMethod]
    public void MusicDefaultsQuieterThanVoiceOver()
    {
        Assert.IsTrue(
            AudioTrack.DefaultVolumeFor(AudioTrackKind.Music)
                < AudioTrack.DefaultVolumeFor(AudioTrackKind.VoiceOver),
            "a freshly inserted music bed must sit under narration, not over it");
    }

    #endregion

    #region Export plan

    [TestMethod]
    public void InsertedTrack_IsPlacedAtItsOwnOutputTime()
    {
        var project = NewProject(PrimaryMic);
        project.AudioTracks.Add(Track(startSec: 4, sourceDurationSec: 3));
        var model = ModelWith(Video(sourceStartSec: 0, sourceDurationSec: 10));

        var plan = ExportAudioPlan.Build(project, model, null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        Assert.AreEqual(AudioSourceKind.AudioFile, placement.Kind);
        AssertSeconds(4, placement.Delay, "delay is the track's own output-timeline start");
        AssertSeconds(0, placement.TrimFromStart, "an untrimmed track starts at the top of the file");
        AssertSeconds(3, placement.TakeDuration!.Value, "the whole file plays");
        Assert.AreEqual(1.0, placement.Volume, "voice-over defaults to full volume");
    }

    [TestMethod]
    public void InsertedTrack_KeepsItsPosition_WhenTheFootageUnderItIsTrimmed()
    {
        // The whole point of the type: the user trimmed the recording to source 2s..5s, and
        // the voice-over they placed at output 1s must still be at output 1s.
        var project = NewProject(PrimaryMic);
        project.AudioTracks.Add(Track(startSec: 1, sourceDurationSec: 2));
        var model = ModelWith(Video(sourceStartSec: 2, sourceDurationSec: 3));

        var plan = ExportAudioPlan.Build(project, model, null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        AssertSeconds(1, placement.Delay, "an inserted track must not follow the segment's trim");
        AssertSeconds(0, placement.TrimFromStart, "nor be trimmed by it");
    }

    [TestMethod]
    public void InsertedTrack_CarriesItsTrimAndVolume()
    {
        var project = NewProject();
        var music = Track(MusicPath, startSec: 2, sourceDurationSec: 30, kind: AudioTrackKind.Music);
        music.TrimStart = TimeSpan.FromSeconds(5);
        music.Duration = TimeSpan.FromSeconds(6);
        music.Volume = 0.25;
        project.AudioTracks.Add(music);

        var plan = ExportAudioPlan.Build(project, ModelWith(Video(0, 10)), null);
        var placement = plan.Single(p => p.SourcePath == MusicPath);

        AssertSeconds(5, placement.TrimFromStart, "playback starts inside the file");
        AssertSeconds(6, placement.TakeDuration!.Value, "only the requested span plays");
        AssertSeconds(2, placement.Delay);
        Assert.AreEqual(0.25, placement.Volume, "a music bed's gain must reach the mux");
    }

    [TestMethod]
    public void MutedOrSilentTracks_AreNotPlanned()
    {
        var project = NewProject();
        var muted = Track(VoicePath);
        muted.IsMuted = true;
        var silent = Track(MusicPath, kind: AudioTrackKind.Music);
        silent.Volume = 0;
        project.AudioTracks.Add(muted);
        project.AudioTracks.Add(silent);

        var plan = ExportAudioPlan.Build(project, ModelWith(Video(0, 10)), null);

        Assert.IsFalse(plan.Any(p => p.SourcePath == VoicePath), "a muted track must not be muxed");
        Assert.IsFalse(plan.Any(p => p.SourcePath == MusicPath), "nor a silenced one");
    }

    [TestMethod]
    public void InsertedTracks_ArePlanned_OnLegacyTimelinesToo()
    {
        // A project with no segments takes ExportAudioPlan's legacy path; an inserted track
        // is identical in both, because it never depended on segments in the first place.
        var project = NewProject(PrimaryMic);
        project.AudioTracks.Add(Track(startSec: 3, sourceDurationSec: 2));

        var plan = ExportAudioPlan.Build(project, timeline: null, mapper: null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        AssertSeconds(3, placement.Delay);
        AssertSeconds(2, placement.TakeDuration!.Value);
    }

    [TestMethod]
    public void RecordedAudio_StillPlansAtFullVolume()
    {
        // Guards the default on the new AudioPlacement.Volume field: every placement cut
        // from a recording must keep muxing at the level it was captured.
        var project = NewProject(PrimaryMic);
        var plan = ExportAudioPlan.Build(project, ModelWith(Video(0, 10)), null);

        Assert.IsTrue(plan.Count > 0, "the recording itself must still be planned");
        foreach (var placement in plan)
            Assert.AreEqual(1.0, placement.Volume, $"'{placement.SourcePath}' must mux unattenuated");
    }

    [TestMethod]
    public void NegativeStartTime_IsPlacedAtTheStartOfTheOutput()
    {
        var project = NewProject();
        project.AudioTracks.Add(Track(startSec: -5, sourceDurationSec: 4));

        var plan = ExportAudioPlan.Build(project, ModelWith(Video(0, 10)), null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        AssertSeconds(0, placement.Delay, "BackgroundAudioTrack.Delay cannot be negative");
    }

    #endregion

    #region Preview placement mapping

    [TestMethod]
    public void FilePositionFor_MapsOutputTimeThroughTheTrackOffsetAndTrim()
    {
        var placement = new Musio.Core.Audio.AudioTimelinePlacement(
            VoicePath,
            OutputStart: TimeSpan.FromSeconds(4),
            TrimStart: TimeSpan.FromSeconds(2),
            Duration: TimeSpan.FromSeconds(3),
            Volume: 1f);

        AssertSeconds(2, placement.FilePositionFor(TimeSpan.FromSeconds(4))!.Value,
            "at the track's start the file plays from its trim point");
        AssertSeconds(3.5, placement.FilePositionFor(TimeSpan.FromSeconds(5.5))!.Value,
            "output time advances the file position one-for-one");
        AssertSeconds(7, placement.OutputEnd, "the span ends at start + duration");
    }

    [TestMethod]
    public void FilePositionFor_IsSilentOutsideTheTrackSpan()
    {
        var placement = new Musio.Core.Audio.AudioTimelinePlacement(
            VoicePath,
            OutputStart: TimeSpan.FromSeconds(4),
            TrimStart: TimeSpan.Zero,
            Duration: TimeSpan.FromSeconds(3),
            Volume: 1f);

        Assert.IsNull(placement.FilePositionFor(TimeSpan.FromSeconds(3.99)),
            "a track must be silent before it starts, not play from its first sample");
        Assert.IsNull(placement.FilePositionFor(TimeSpan.FromSeconds(7)),
            "the end is exclusive, so it is silent the instant it finishes");
        Assert.IsNull(placement.FilePositionFor(TimeSpan.FromSeconds(20)));
    }

    #endregion
}
