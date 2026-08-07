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
        var model = ModelWith(Video(sourceStartSec: 0, sourceDurationSec: 10));
        model.AudioTracks.Add(Track(startSec: 4, sourceDurationSec: 3));

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
        var model = ModelWith(Video(sourceStartSec: 2, sourceDurationSec: 3));
        model.AudioTracks.Add(Track(startSec: 1, sourceDurationSec: 2));

        var plan = ExportAudioPlan.Build(project, model, null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        AssertSeconds(1, placement.Delay, "an inserted track must not follow the segment's trim");
        AssertSeconds(0, placement.TrimFromStart, "nor be trimmed by it");
    }

    [TestMethod]
    public void InsertedTrack_CarriesItsTrimAndVolume()
    {
        var project = NewProject();
        var model = ModelWith(Video(0, 10));
        var music = Track(MusicPath, startSec: 2, sourceDurationSec: 30, kind: AudioTrackKind.Music);
        music.TrimStart = TimeSpan.FromSeconds(5);
        music.Duration = TimeSpan.FromSeconds(6);
        music.Volume = 0.25;
        model.AudioTracks.Add(music);

        var plan = ExportAudioPlan.Build(project, model, null);
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
        var model = ModelWith(Video(0, 10));
        var muted = Track(VoicePath);
        muted.IsMuted = true;
        var silent = Track(MusicPath, kind: AudioTrackKind.Music);
        silent.Volume = 0;
        model.AudioTracks.Add(muted);
        model.AudioTracks.Add(silent);

        var plan = ExportAudioPlan.Build(project, model, null);

        Assert.IsFalse(plan.Any(p => p.SourcePath == VoicePath), "a muted track must not be muxed");
        Assert.IsFalse(plan.Any(p => p.SourcePath == MusicPath), "nor a silenced one");
    }

    [TestMethod]
    public void InsertedTracks_ArePlanned_OnLegacyTimelinesToo()
    {
        // A timeline with no video segments takes ExportAudioPlan's legacy path; an inserted
        // track is identical there, because it never depended on segments in the first place.
        var project = NewProject(PrimaryMic);
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.AudioTracks.Add(Track(startSec: 3, sourceDurationSec: 2));

        var plan = ExportAudioPlan.Build(project, model, null);
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
        var model = ModelWith(Video(0, 10));
        model.AudioTracks.Add(Track(startSec: -5, sourceDurationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var placement = plan.Single(p => p.SourcePath == VoicePath);

        AssertSeconds(0, placement.Delay, "BackgroundAudioTrack.Delay cannot be negative");
    }

    #endregion

    #region Edit operations

    private static (TimelineModel Model, AudioTrack Track) ModelWithTrack(
        double startSec = 2, double sourceDurationSec = 10, double? durationSec = null)
    {
        var model = ModelWith(Video(0, 20));
        var track = Track(startSec: startSec, sourceDurationSec: sourceDurationSec);
        if (durationSec is { } d) track.Duration = TimeSpan.FromSeconds(d);
        model.AudioTracks.Add(track);
        return (model, track);
    }

    [TestMethod]
    public void Move_ShiftsOnlyTheStart_AndUndoesExactly()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 6);
        var op = new MoveAudioTrackOperation(track.Id, TimeSpan.FromSeconds(9));

        op.Execute(model);
        AssertSeconds(9, track.StartTime, "the block moves to where it was dropped");
        AssertSeconds(0, track.TrimStart, "a move must not change which audio plays");
        AssertSeconds(6, track.EffectiveDuration, "nor how much of it plays");

        op.Undo(model);
        AssertSeconds(2, track.StartTime, "undo restores the original position");
    }

    [TestMethod]
    public void Move_ClampsToTheStartOfTheTimeline()
    {
        var (model, track) = ModelWithTrack(startSec: 2);
        new MoveAudioTrackOperation(track.Id, TimeSpan.FromSeconds(-5)).Execute(model);

        AssertSeconds(0, track.StartTime, "a block cannot start before the output does");
    }

    [TestMethod]
    public void TrimLeftEdge_AdvancesTheTrimByTheSameAmount_SoTheAudioDoesNotShift()
    {
        // The defining behaviour of a left trim: dragging the edge right must reveal LATER
        // audio, leaving every surviving sample at the timeline instant it was already at.
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);

        new TrimAudioTrackOperation(track.Id, fromStart: true, TimeSpan.FromSeconds(5)).Execute(model);

        AssertSeconds(5, track.StartTime, "the block now starts at the dragged edge");
        AssertSeconds(3, track.TrimStart, "and skips exactly the audio that was cut away");
        AssertSeconds(7, track.EffectiveDuration, "the tail is untouched");
        AssertSeconds(12, track.End, "so the right edge has not moved");
    }

    [TestMethod]
    public void TrimLeftEdge_CannotUncoverAudioThatWasNeverThere()
    {
        var (model, track) = ModelWithTrack(startSec: 4, sourceDurationSec: 10);
        track.TrimStart = TimeSpan.FromSeconds(1);

        // Dragging left can only give back the 1s already trimmed off; there is no earlier
        // audio in the file to reveal, so the edge stops at output 3s.
        new TrimAudioTrackOperation(track.Id, fromStart: true, TimeSpan.FromSeconds(0)).Execute(model);

        AssertSeconds(3, track.StartTime, "the edge stops where the file itself starts");
        AssertSeconds(0, track.TrimStart, "with nothing trimmed away any more");
    }

    [TestMethod]
    public void TrimRightEdge_ShortensTheDuration_AndCannotExceedTheFile()
    {
        var (model, track) = ModelWithTrack(startSec: 0, sourceDurationSec: 8);

        new TrimAudioTrackOperation(track.Id, fromStart: false, TimeSpan.FromSeconds(3)).Execute(model);
        AssertSeconds(3, track.EffectiveDuration, "the right edge sets the duration");

        new TrimAudioTrackOperation(track.Id, fromStart: false, TimeSpan.FromSeconds(50)).Execute(model);
        AssertSeconds(8, track.EffectiveDuration, "there is no more file to extend into");
    }

    [TestMethod]
    public void Trim_NeverProducesABlockTooSmallToGrabAgain()
    {
        var (model, track) = ModelWithTrack(startSec: 0, sourceDurationSec: 8);

        new TrimAudioTrackOperation(track.Id, fromStart: false, TimeSpan.FromSeconds(-10)).Execute(model);
        Assert.IsTrue(track.EffectiveDuration >= AudioTrackEditing.MinDuration,
            "a block trimmed to nothing could never be grabbed to undo the trim");
    }

    [TestMethod]
    public void Trim_UndoRestoresStartTrimAndDurationTogether()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);
        var op = new TrimAudioTrackOperation(track.Id, fromStart: true, TimeSpan.FromSeconds(6));

        op.Execute(model);
        op.Undo(model);

        AssertSeconds(2, track.StartTime);
        AssertSeconds(0, track.TrimStart, "undoing a left trim must restore the trim, not just the start");
        AssertSeconds(10, track.EffectiveDuration);
    }

    [TestMethod]
    public void Split_ProducesTwoHalvesThatTogetherPlayTheOriginalAudio()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);
        var op = new SplitAudioTrackOperation(track.Id, TimeSpan.FromSeconds(6));

        op.Execute(model);

        Assert.AreEqual(2, model.AudioTracks.Count, "a split produces exactly two blocks");
        var left = model.AudioTracks.Single(t => t.Id == track.Id);
        var right = model.AudioTracks.Single(t => t.Id == op.CreatedId);

        AssertSeconds(2, left.StartTime);
        AssertSeconds(4, left.EffectiveDuration, "the left half ends at the split point");
        AssertSeconds(6, right.StartTime, "and the right half starts there");
        AssertSeconds(4, right.TrimStart, "skipping exactly the audio the left half played");
        AssertSeconds(6, right.EffectiveDuration, "the two halves together are the original");
        Assert.AreEqual(left.FilePath, right.FilePath, "both halves read the same file");
        Assert.AreEqual(left.Kind, right.Kind, "and stay on the same lane");
    }

    [TestMethod]
    public void Split_IsRejectedOutsideTheBlock_RatherThanCuttingSomewhereElse()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);

        Assert.IsFalse(SplitAudioTrackOperation.CanSplit(track, TimeSpan.FromSeconds(20)));
        Assert.IsFalse(SplitAudioTrackOperation.CanSplit(track, TimeSpan.FromSeconds(0)));
        Assert.IsFalse(SplitAudioTrackOperation.CanSplit(track, track.StartTime),
            "splitting exactly at the edge would leave a zero-length half");

        var op = new SplitAudioTrackOperation(track.Id, TimeSpan.FromSeconds(20));
        op.Execute(model);

        Assert.AreEqual(1, model.AudioTracks.Count, "an out-of-range split must do nothing");
        Assert.IsNull(op.CreatedId);
    }

    [TestMethod]
    public void Split_UndoRestoresTheSingleOriginalBlock()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);
        var op = new SplitAudioTrackOperation(track.Id, TimeSpan.FromSeconds(6));

        op.Execute(model);
        op.Undo(model);

        var restored = model.AudioTracks.Single();
        Assert.AreEqual(track.Id, restored.Id);
        AssertSeconds(2, restored.StartTime);
        AssertSeconds(0, restored.TrimStart);
        AssertSeconds(10, restored.EffectiveDuration, "the original length comes back");
    }

    [TestMethod]
    public void Remove_TakesTheBlockOut_AndUndoBringsItBackIntact()
    {
        var (model, track) = ModelWithTrack(startSec: 3, sourceDurationSec: 5);
        track.Volume = 0.6;
        var op = new RemoveAudioTrackOperation(track.Id);

        op.Execute(model);
        Assert.AreEqual(0, model.AudioTracks.Count);

        op.Undo(model);
        var restored = model.AudioTracks.Single();
        Assert.AreEqual(track.Id, restored.Id);
        AssertSeconds(3, restored.StartTime);
        Assert.AreEqual(0.6, restored.Volume, "undo must restore the whole block, not a fresh one");
    }

    [TestMethod]
    public void UpdateProperties_TogglesMuteAndVolume_Reversibly()
    {
        var (model, track) = ModelWithTrack();
        var op = new UpdateAudioTrackPropertiesOperation(track.Id, isMuted: true, volume: 0.2);

        op.Execute(model);
        Assert.IsTrue(track.IsMuted);
        Assert.AreEqual(0.2, track.Volume);

        op.Undo(model);
        Assert.IsFalse(track.IsMuted);
        Assert.AreEqual(AudioTrack.DefaultVolumeFor(AudioTrackKind.VoiceOver), track.Volume);
    }

    [TestMethod]
    public void Add_InsertsInStartOrder_SoLanesDrawAndHitTestLeftToRight()
    {
        var model = ModelWith(Video(0, 20));
        new AddAudioTrackOperation(Track(VoicePath, startSec: 8)).Execute(model);
        var early = Track(MusicPath, startSec: 1);
        new AddAudioTrackOperation(early).Execute(model);

        Assert.AreEqual(early.Id, model.AudioTracks[0].Id, "tracks must stay ordered by start time");
    }

    [TestMethod]
    public void TrimLeftEdge_ToAnInstantInsideTheBlock_MatchesTheTrimToPlayheadMenu()
    {
        // The context menu's "Trim start to playhead" passes the playhead straight through as
        // the new edge, so its guard (playhead strictly inside the block, leaving at least
        // MinDuration) has to agree with what the operation will actually do.
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);
        var playhead = TimeSpan.FromSeconds(7);

        Assert.IsTrue(playhead > track.StartTime && playhead <= track.End - AudioTrackEditing.MinDuration,
            "the menu would enable the item here");

        new TrimAudioTrackOperation(track.Id, fromStart: true, playhead).Execute(model);

        AssertSeconds(7, track.StartTime, "the start lands exactly on the playhead");
        AssertSeconds(5, track.TrimStart, "skipping the audio the playhead passed");
        AssertSeconds(12, track.End, "and the tail is untouched");
    }

    [TestMethod]
    public void TrimRightEdge_ToAnInstantInsideTheBlock_LeavesTheStartAlone()
    {
        var (model, track) = ModelWithTrack(startSec: 2, sourceDurationSec: 10);

        new TrimAudioTrackOperation(track.Id, fromStart: false, TimeSpan.FromSeconds(7)).Execute(model);

        AssertSeconds(2, track.StartTime, "trimming the end must not move the block");
        AssertSeconds(0, track.TrimStart, "nor change which audio it starts from");
        AssertSeconds(7, track.End, "the end lands exactly on the requested instant");
    }

    [TestMethod]
    public void TrimRightEdge_OnAnUntrimmedImport_MaterialisesADuration()
    {
        // A freshly imported track has Duration == null ("play to the end of the file"),
        // which is the state a first right-edge trim has to convert into a real length —
        // the case a long music bed always starts in.
        var (model, track) = ModelWithTrack(startSec: 0, sourceDurationSec: 180);
        Assert.IsNull(track.Duration, "a fresh import plays to the end of the file");

        new TrimAudioTrackOperation(track.Id, fromStart: false, TimeSpan.FromSeconds(25)).Execute(model);

        Assert.IsNotNull(track.Duration);
        AssertSeconds(25, track.EffectiveDuration, "a 3-minute bed can be cut down to the video");
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
