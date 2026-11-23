using Mixtri.Core.Audio;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for appending an audio-bearing recording to a project whose first
/// recording (or imported video) had no audio, and for muting one segment's sound.
/// </summary>
/// <remarks>
/// The preview used to have no path for an appended recording's audio at all — only the
/// primary recording's files were ever loaded — so the appended clip's sound was not where
/// its picture was. <see cref="PlacedSegmentAudio"/> now hands preview the SAME placements
/// <see cref="ExportAudioPlan"/> muxes; these tests pin that both agree on the appended
/// segment's timeline position, its recording's own audio offset, and every kind of mute.
/// </remarks>
[TestClass]
public sealed class AppendedAudioAlignmentTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";
    private const string PrimaryMic = @"C:\rec\primary\mic_0.wav";
    private const string Appended = @"C:\rec\appended\video.mp4";
    private const string AppendedMic = @"C:\rec\appended\mic_0.wav";
    private const string AppendedSystem = @"C:\rec\appended\system_0.wav";

    /// <summary>A primary recording captured WITHOUT audio.</summary>
    private static Project SilentPrimaryProject() => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(5),
        AudioFilePaths = [],
    };

    private static VideoSegment PrimarySegment(double durationSec = 5) => new()
    {
        VideoFilePath = Primary,
        SourceStart = TimeSpan.Zero,
        SourceDuration = TimeSpan.FromSeconds(durationSec),
        Duration = TimeSpan.FromSeconds(durationSec),
    };

    private static VideoSegment AppendedSegment(
        double offsetSeconds = 0, double durationSec = 4, params string[] audio) => new()
        {
            VideoFilePath = Appended,
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromSeconds(durationSec),
            Duration = TimeSpan.FromSeconds(durationSec),
            AudioFilePaths = audio.Length > 0 ? [.. audio] : [AppendedMic],
            AudioToVideoOffsetSeconds = offsetSeconds,
        };

    /// <summary>Builds the timeline the way ProjectService.AppendRecording does.</summary>
    private static TimelineModel AppendToSilentPrimary(VideoSegment appended)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.Add(PrimarySegment());
        model.RecalculateSegmentPositions();
        new AppendVideoSegmentOperation(appended).Execute(model);
        return model;
    }

    private static VideoSegment SegmentFor(TimelineModel model, string path)
        => model.Segments.OfType<VideoSegment>().Single(s => s.VideoFilePath == path);

    private static void AssertSeconds(double expected, TimeSpan actual, string message)
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    #region Alignment: export and preview agree

    [TestMethod]
    public void Export_AppendedAudioAfterSilentPrimary_StartsAtTheAppendedSegment()
    {
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment());
        var appended = SegmentFor(model, Appended);

        var mic = ExportAudioPlan.Build(project, model, null)
            .Single(p => p.SourcePath == AppendedMic);

        AssertSeconds(5, appended.Start, "Sanity: the appended clip follows the 5s primary");
        AssertSeconds(appended.Start.TotalSeconds, mic.Delay,
            "The appended sound must start where its picture starts, not at 0");
        AssertSeconds(0, mic.TrimFromStart, "No offset, so the file plays from its start");
        AssertSeconds(4, mic.TakeDuration!.Value, "It spans exactly its own segment");
    }

    [TestMethod]
    public void Preview_AppendedAudioAfterSilentPrimary_IsPlacedAtTheAppendedSegment()
    {
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment());

        var selected = PlacedSegmentAudio.Select(project, model);

        Assert.AreEqual(1, selected.Count,
            "The appended recording's audio must reach the preview — it used to be silent there");
        var placed = PlacedSegmentAudio.ToTimelinePlacement(selected[0]);
        Assert.IsNotNull(placed);
        Assert.AreEqual(AppendedMic, placed.Value.FilePath);
        AssertSeconds(5, placed.Value.OutputStart, "Preview places it where export does");
        AssertSeconds(0, placed.Value.TrimStart, "Preview trims it where export does");
        AssertSeconds(4, placed.Value.Duration, "Preview plays as much as export muxes");
    }

    [TestMethod]
    public void AppendedRecordingsOwnAudioOffset_IsAppliedPerSource_InBothPipelines()
    {
        // The primary has no audio, so its offset is meaningless (and can be huge when no
        // audio engine ever started) — it must never leak into the appended recording.
        var project = SilentPrimaryProject();
        project.AudioToVideoOffsetSeconds = 12345;
        var model = AppendToSilentPrimary(AppendedSegment(offsetSeconds: 0.25));

        var export = ExportAudioPlan.Build(project, model, null).Single(p => p.SourcePath == AppendedMic);
        var preview = PlacedSegmentAudio.ToTimelinePlacement(PlacedSegmentAudio.Select(project, model).Single());

        AssertSeconds(0.25, export.TrimFromStart, "Export skips the appended recording's own pre-roll");
        AssertSeconds(5, export.Delay, "...and still starts at the appended segment");
        Assert.IsNotNull(preview);
        AssertSeconds(export.TrimFromStart.TotalSeconds, preview.Value.TrimStart, "Preview applies the same offset");
        AssertSeconds(export.Delay.TotalSeconds, preview.Value.OutputStart, "Preview starts at the same instant");
    }

    [TestMethod]
    public void LateStartingAppendedAudio_LeadsInSilence_InBothPipelines()
    {
        // Negative offset: the appended recording's audio started 0.5s after its first frame.
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment(offsetSeconds: -0.5));

        var export = ExportAudioPlan.Build(project, model, null).Single(p => p.SourcePath == AppendedMic);
        var preview = PlacedSegmentAudio.ToTimelinePlacement(PlacedSegmentAudio.Select(project, model).Single());

        AssertSeconds(5.5, export.Delay, "Audio that began late is delayed by that much into the segment");
        AssertSeconds(0, export.TrimFromStart, "Nothing to skip");
        Assert.IsNotNull(preview);
        AssertSeconds(5.5, preview.Value.OutputStart, "Preview leads in with the same silence");
    }

    [TestMethod]
    public void Preview_LeavesThePrimaryRecordingsOwnFilesToTheContinuousEngine()
    {
        var project = SilentPrimaryProject();
        project.AudioFilePaths = [PrimaryMic];
        var model = AppendToSilentPrimary(AppendedSegment());

        var selected = PlacedSegmentAudio.Select(project, model);

        Assert.IsFalse(selected.Any(p => p.SourcePath == PrimaryMic),
            "Primary 1x audio is already played by the continuous engine; placing it too would double it");
        Assert.IsTrue(selected.Any(p => p.SourcePath == AppendedMic));
    }

    [TestMethod]
    public void Preview_DoesNotReplayDetachedBlocks_WhichTheInsertedEngineOwns()
    {
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment());
        var appended = SegmentFor(model, Appended);
        appended.AudioMode = SegmentAudioMode.Muted;
        model.AudioTracks.Add(new AudioTrack
        {
            FilePath = AppendedMic,
            StartTime = TimeSpan.FromSeconds(1),
            SourceDuration = TimeSpan.FromSeconds(4),
        });

        Assert.AreEqual(0, PlacedSegmentAudio.Select(project, model).Count,
            "A detached block reads the same file but is an inserted track, played elsewhere");
    }

    [TestMethod]
    public void Preview_UsesTheHardCutTake_NotTheTransitionExtension()
    {
        var placement = new AudioPlacement(
            AppendedMic, AudioSourceKind.AudioFile,
            TrimFromStart: TimeSpan.FromSeconds(1),
            TakeDuration: TimeSpan.FromSeconds(5),
            Delay: TimeSpan.FromSeconds(2),
            FadeOutDuration: TimeSpan.FromSeconds(1));

        var placed = PlacedSegmentAudio.ToTimelinePlacement(placement);

        Assert.IsNotNull(placed);
        AssertSeconds(4, placed.Value.Duration,
            "Neither pipeline crossfades, so preview must cut where export does");
    }

    [TestMethod]
    public void Signature_WithoutVolume_IgnoresOnlyGains()
    {
        var a = new AudioPlacement(AppendedMic, AudioSourceKind.AudioFile,
            TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), Volume: 1.0);
        var quieter = a with { Volume = 0.5 };
        var moved = a with { Delay = TimeSpan.FromSeconds(6) };

        Assert.AreNotEqual(PlacedSegmentAudio.Signature([a]), PlacedSegmentAudio.Signature([quieter]));
        Assert.AreEqual(
            PlacedSegmentAudio.Signature([a], includeVolume: false),
            PlacedSegmentAudio.Signature([quieter], includeVolume: false),
            "A fader move must be recognisable as a re-level, not a rebuild");
        Assert.AreNotEqual(
            PlacedSegmentAudio.Signature([a], includeVolume: false),
            PlacedSegmentAudio.Signature([moved], includeVolume: false));
    }

    #endregion

    #region Muting one segment

    [TestMethod]
    public void MutedAppendedSegment_ContributesNoAudio_ToExportOrPreview()
    {
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment());
        string id = SegmentFor(model, Appended).Id;

        new ChangeSegmentAudioModeOperation(id, SegmentAudioMode.Muted).Execute(model);

        Assert.IsFalse(ExportAudioPlan.Build(project, model, null).Any(p => p.SourcePath == AppendedMic));
        Assert.AreEqual(0, PlacedSegmentAudio.Select(project, model).Count);
    }

    [TestMethod]
    public void ChannelMute_SilencesOnlyThatChannelOfThatSegment()
    {
        var project = SilentPrimaryProject();
        var model = AppendToSilentPrimary(AppendedSegment(audio: [AppendedSystem, AppendedMic]));
        string id = SegmentFor(model, Appended).Id;

        var op = new SetSegmentAudioChannelMuteOperation(id, SegmentAudioChannels.Mic, muted: true);
        op.Execute(model);

        Assert.IsTrue(op.ChangedModel);
        var export = ExportAudioPlan.Build(project, model, null);
        Assert.IsFalse(export.Any(p => p.SourcePath == AppendedMic), "The muted mic block is dropped from export");
        Assert.IsTrue(export.Any(p => p.SourcePath == AppendedSystem), "The segment's system audio keeps playing");

        var preview = PlacedSegmentAudio.Select(project, model);
        Assert.IsFalse(preview.Any(p => p.SourcePath == AppendedMic), "...and from preview");
        Assert.IsTrue(preview.Any(p => p.SourcePath == AppendedSystem));
    }

    [TestMethod]
    public void ChannelMute_OnlyAffectsTheSegmentThatCarriesIt()
    {
        var project = new Project
        {
            VideoFilePath = Primary,
            Duration = TimeSpan.FromSeconds(10),
            AudioFilePaths = [PrimaryMic],
        };
        var first = PrimarySegment(4);
        var second = PrimarySegment(4) with { SourceStart = TimeSpan.FromSeconds(4) };
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.Add(first);
        model.Segments.Add(second);
        model.RecalculateSegmentPositions();

        new SetSegmentAudioChannelMuteOperation(first.Id, SegmentAudioChannels.Mic, muted: true).Execute(model);

        var mic = ExportAudioPlan.Build(project, model, null).Where(p => p.SourcePath == PrimaryMic).ToList();
        Assert.AreEqual(1, mic.Count, "Only the unmuted segment's mic survives");
        AssertSeconds(4, mic[0].Delay, "...and it is the second segment's");
    }

    [TestMethod]
    public void ChannelMuteOperation_AppliesAndUndoes()
    {
        var model = AppendToSilentPrimary(AppendedSegment());
        var segment = SegmentFor(model, Appended);

        var op = new SetSegmentAudioChannelMuteOperation(segment.Id, SegmentAudioChannels.System, muted: true);
        op.Execute(model);
        Assert.AreEqual(SegmentAudioChannels.System, segment.MutedAudioChannels);

        op.Undo(model);
        Assert.AreEqual(SegmentAudioChannels.None, segment.MutedAudioChannels,
            "Undo restores the previous channel state");

        op.Execute(model);
        Assert.AreEqual(SegmentAudioChannels.System, segment.MutedAudioChannels, "Redo re-applies it");
    }

    [TestMethod]
    public void ChannelMuteOperation_Unmute_KeepsTheOtherChannelMuted()
    {
        var model = AppendToSilentPrimary(AppendedSegment());
        var segment = SegmentFor(model, Appended);
        segment.MutedAudioChannels = SegmentAudioChannels.System | SegmentAudioChannels.Mic;

        var op = new SetSegmentAudioChannelMuteOperation(segment.Id, SegmentAudioChannels.Mic, muted: false);
        op.Execute(model);

        Assert.AreEqual(SegmentAudioChannels.System, segment.MutedAudioChannels);
        op.Undo(model);
        Assert.AreEqual(SegmentAudioChannels.System | SegmentAudioChannels.Mic, segment.MutedAudioChannels);
    }

    [TestMethod]
    public void ChannelMuteOperation_AlreadyInThatState_IsANoOp()
    {
        var model = AppendToSilentPrimary(AppendedSegment());
        var segment = SegmentFor(model, Appended);
        segment.MutedAudioChannels = SegmentAudioChannels.Mic;

        var op = new SetSegmentAudioChannelMuteOperation(segment.Id, SegmentAudioChannels.Mic, muted: true);
        op.Execute(model);

        Assert.IsFalse(op.ChangedModel, "Re-muting a muted block must not push an undo entry");
        op.Undo(model);
        Assert.AreEqual(SegmentAudioChannels.Mic, segment.MutedAudioChannels,
            "Undoing a no-op must not clear state it never set");
    }

    [TestMethod]
    public void ChannelMuteOperation_UnknownOrNonVideoSegment_IsANoOp()
    {
        var model = AppendToSilentPrimary(AppendedSegment());
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(2) };
        model.Segments.Add(slide);
        model.RecalculateSegmentPositions();

        var unknown = new SetSegmentAudioChannelMuteOperation("nope", SegmentAudioChannels.Mic, muted: true);
        unknown.Execute(model);
        Assert.IsFalse(unknown.ChangedModel);

        var onSlide = new SetSegmentAudioChannelMuteOperation(slide.Id, SegmentAudioChannels.Mic, muted: true);
        onSlide.Execute(model);
        Assert.IsFalse(onSlide.ChangedModel);

        var nothing = new SetSegmentAudioChannelMuteOperation(
            SegmentFor(model, Appended).Id, SegmentAudioChannels.None, muted: true);
        nothing.Execute(model);
        Assert.IsFalse(nothing.ChangedModel);
    }

    [TestMethod]
    public void ChannelMute_SurvivesASplit()
    {
        var model = AppendToSilentPrimary(AppendedSegment());
        SegmentFor(model, Appended).MutedAudioChannels = SegmentAudioChannels.Mic;

        // The appended clip covers 5s..9s; cut it in the middle.
        new SplitSegmentAtTimeOperation(TimeSpan.FromSeconds(7)).Execute(model);

        var halves = model.Segments.OfType<VideoSegment>().Where(s => s.VideoFilePath == Appended).ToList();
        Assert.AreEqual(2, halves.Count, "Sanity: the appended clip was split");
        Assert.IsTrue(halves.All(h => h.MutedAudioChannels == SegmentAudioChannels.Mic),
            "Both halves of a split keep the block's mute");
    }

    [TestMethod]
    public void RecordedAudio_IsMutedOnSegment_ClassifiesPackagedFileNames()
    {
        var segment = new VideoSegment { MutedAudioChannels = SegmentAudioChannels.Mic };

        Assert.IsTrue(RecordedAudio.IsMutedOnSegment(segment, @"C:\pkg\3_mic_0.wav"),
            "A packaged index prefix must not reclassify the mic as system audio");
        Assert.IsFalse(RecordedAudio.IsMutedOnSegment(segment, @"C:\pkg\3_system_0.wav"));

        segment.AudioMode = SegmentAudioMode.Muted;
        Assert.IsTrue(RecordedAudio.IsMutedOnSegment(segment, @"C:\pkg\3_system_0.wav"),
            "A whole-segment mute silences every channel");
    }

    #endregion
}
