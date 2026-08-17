using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Covers the three-way choice a speed-adjusted segment offers for its recorded audio
/// (<see cref="SegmentAudioMode"/>): re-time it, leave it at its native rate, or drop it.
/// </summary>
/// <remarks>
/// The plan stays I/O-free, so the time-stretch shows up as a
/// <see cref="AudioPlacement.Stretch"/> request riding alongside an unchanged native-rate
/// placement — that shape is what makes a failed render degrade to
/// <see cref="SegmentAudioMode.Native"/> instead of failing the export, and it is asserted
/// here rather than left implicit.
/// </remarks>
[TestClass]
public sealed class SegmentAudioModeTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";
    private const string PrimaryMic = @"C:\rec\primary\mic_0.wav";

    private static Project NewProject() => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(20),
        AudioFilePaths = [PrimaryMic],
    };

    private static VideoSegment Video(
        double sourceStartSec, double sourceDurationSec, double speed,
        SegmentAudioMode mode = SegmentAudioMode.TimeStretch) => new()
        {
            VideoFilePath = Primary,
            SourceStart = TimeSpan.FromSeconds(sourceStartSec),
            SourceDuration = TimeSpan.FromSeconds(sourceDurationSec),
            Duration = TimeSpan.FromSeconds(sourceDurationSec / speed),
            SpeedFactor = speed,
            AudioMode = mode,
        };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    private static AudioPlacement Mic(IReadOnlyList<AudioPlacement> plan)
        => plan.Single(p => p.SourcePath == PrimaryMic && p.Kind == AudioSourceKind.AudioFile);

    private static void AssertSeconds(double expected, TimeSpan actual, string message)
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    [TestMethod]
    public void NewSegments_DefaultToTimeStretch()
    {
        Assert.AreEqual(
            SegmentAudioMode.TimeStretch, new VideoSegment().AudioMode,
            "Keeping picture and sound together is the default; it is also the zero value, so " +
            "projects saved before this property existed open into it");
    }

    [TestMethod]
    public void SpedUpSegment_IsAlwaysReTimed_AndKeepsTheNativeFallback()
    {
        // 10s of source at 2x occupies 5s of output.
        var plan = ExportAudioPlan.Build(NewProject(), ModelWith(Video(0, 10, speed: 2.0)), null);
        var mic = Mic(plan);

        Assert.IsNotNull(mic.Stretch, "An audible speed-adjusted segment must request a re-time");
        var stretch = mic.Stretch!.Value;

        Assert.AreEqual(2.0, stretch.Speed, 0.001);
        AssertSeconds(0, stretch.SourceStart, "The stretch reads from the segment's source in point");
        AssertSeconds(10, stretch.SourceDuration, "All 10s of source audio under the segment is consumed");
        AssertSeconds(5, stretch.OutputDuration, "...and re-timed to exactly the segment's 5s of output");

        AssertSeconds(
            5, mic.TakeDuration!.Value,
            "The placement itself stays the native-rate one, so a failed render degrades to " +
            "muxing at native rate rather than losing the audio");
        Assert.IsTrue(mic.PlaysAtNativeRateOnSpeedAdjustedSegment,
            "Until the render is substituted in, this placement really is native-rate");
    }

    [TestMethod]
    public void SlowedSegment_FillsTheWholeSegmentInsteadOfEndingEarly()
    {
        // 5s of source at 0.5x occupies 10s of output; natively the back half is silence.
        var plan = ExportAudioPlan.Build(NewProject(), ModelWith(Video(0, 5, speed: 0.5)), null);
        var stretch = Mic(plan).Stretch!.Value;

        AssertSeconds(5, stretch.SourceDuration, "Only 5s of source exists");
        AssertSeconds(10, stretch.OutputDuration, "...and it must be stretched across the full 10s");
    }

    [TestMethod]
    public void LegacyNativeMode_BehavesAsTimeStretch()
    {
        // "Keep original speed" was removed as a choice once audio could be detached, but the
        // value survives in projects saved while it existed — and the format persists this
        // enum BY NAME, so the member cannot be deleted. It must simply re-time like anything
        // else rather than resurrecting the old drifting behaviour.
        var plan = ExportAudioPlan.Build(
            NewProject(), ModelWith(Video(0, 10, speed: 2.0, SegmentAudioMode.Native)), null);
        var mic = Mic(plan);

        Assert.IsNotNull(mic.Stretch, "A legacy Native segment is re-timed like any other");
        AssertSeconds(5, mic.Stretch!.Value.OutputDuration, "...to exactly the segment's output length");
    }

    [TestMethod]
    public void SpedUpSegment_Muted_ProducesNoPlacementsAtAll()
    {
        var plan = ExportAudioPlan.Build(
            NewProject(), ModelWith(Video(0, 10, speed: 2.0, SegmentAudioMode.Muted)), null);

        Assert.AreEqual(0, plan.Count, "A muted segment must cost nothing — not even a volume-0 track");
    }

    [TestMethod]
    public void MutedMode_OnlySilencesTheSegmentThatCarriesIt()
    {
        var model = ModelWith(
            Video(0, 4, speed: 1.0),
            Video(4, 10, speed: 2.0, SegmentAudioMode.Muted));

        var plan = ExportAudioPlan.Build(NewProject(), model, null);

        Assert.IsTrue(plan.Count > 0, "The untouched segment keeps its audio");
        Assert.IsTrue(
            plan.All(p => p.Delay == TimeSpan.Zero),
            "Only the muted segment's own placements are dropped");
    }

    [TestMethod]
    [DataRow(SegmentAudioMode.TimeStretch)]
    [DataRow(SegmentAudioMode.Native)]
    public void NativeSpeedSegments_AreUnaffectedByTheAudibleModes(SegmentAudioMode mode)
    {
        var plan = ExportAudioPlan.Build(
            NewProject(), ModelWith(Video(0, 10, speed: 1.0, mode)), null);
        var mic = Mic(plan);

        Assert.IsNull(mic.Stretch, "Nothing to re-time at 1.0");
        AssertSeconds(10, mic.TakeDuration!.Value, "A 1.0 segment is byte-for-byte unaffected");
        Assert.IsFalse(mic.PlaysAtNativeRateOnSpeedAdjustedSegment);
    }

    [TestMethod]
    public void MutedSegment_IsSilentAtNativeSpeedToo()
    {
        // Mute is an edit in its own right, not a way of coping with a re-time: a user who
        // silences one segment of a normal-speed recording must get silence there.
        var plan = ExportAudioPlan.Build(
            NewProject(), ModelWith(Video(0, 10, speed: 1.0, SegmentAudioMode.Muted)), null);

        Assert.AreEqual(0, plan.Count, "A muted segment contributes no audio at any speed");
    }

    [TestMethod]
    public void MutedSegment_AtNativeSpeed_LeavesItsNeighboursAlone()
    {
        var model = ModelWith(
            Video(0, 4, speed: 1.0),
            Video(4, 6, speed: 1.0, SegmentAudioMode.Muted));

        var plan = ExportAudioPlan.Build(NewProject(), model, null);

        Assert.IsTrue(plan.Count > 0, "The untouched segment keeps its audio");
        Assert.IsTrue(
            plan.All(p => p.Delay == TimeSpan.Zero),
            "Only the muted segment's own placements are dropped");
    }

    [TestMethod]
    public void StretchRequest_NeverOverrunsTheSegmentOnTheOutputTimeline()
    {
        // Audio started 1s of source time late, so the segment's first half-second of output
        // has no audio behind it and the placement is delayed into the segment.
        var project = NewProject();
        project.AudioToVideoOffsetSeconds = -1.0;
        var plan = ExportAudioPlan.Build(project, ModelWith(Video(0, 10, speed: 2.0)), null);
        var mic = Mic(plan);
        var stretch = mic.Stretch!.Value;

        AssertSeconds(0, stretch.SourceStart, "Cannot read before the start of the file");
        AssertSeconds(0.5, mic.Delay, "1s of source lead is 0.5s of output at 2x");
        AssertSeconds(4.5, stretch.OutputDuration, "The stretch fills only the room that is left");
        Assert.AreEqual(
            TimeSpan.FromSeconds(5), mic.Delay + stretch.OutputDuration,
            "Stretched audio must end exactly with its segment, never bleed into the next");
    }

    [TestMethod]
    public void SubstituteStretchedSource_RepointsThePlacementAtTheRenderedFile()
    {
        var placement = Mic(ExportAudioPlan.Build(NewProject(), ModelWith(Video(0, 10, speed: 2.0)), null));
        var stretch = placement.Stretch!.Value;

        var substituted = VideoEncoder.SubstituteStretchedSource(
            placement, @"C:\rec\primary\.stretched\stretch_abc.wav", stretch);

        Assert.AreEqual(@"C:\rec\primary\.stretched\stretch_abc.wav", substituted.SourcePath);
        Assert.AreEqual(AudioSourceKind.AudioFile, substituted.Kind);
        AssertSeconds(0, substituted.TrimFromStart, "The rendered file contains only this segment's audio");
        AssertSeconds(5, substituted.TakeDuration!.Value, "...and all of it is played");
        AssertSeconds(0, substituted.Delay, "Its position on the output timeline does not change");
        Assert.IsFalse(substituted.PlaysAtNativeRateOnSpeedAdjustedSegment,
            "Re-timed audio is no longer a native-rate compromise to warn about");
        Assert.IsNull(substituted.Stretch, "The request is consumed, not carried into the mux");
    }

    [TestMethod]
    public void SubstituteStretchedSource_FailedRender_FallsBackToTheNativePlacement()
    {
        var placement = Mic(ExportAudioPlan.Build(NewProject(), ModelWith(Video(0, 10, speed: 2.0)), null));
        var stretch = placement.Stretch!.Value;

        var fallback = VideoEncoder.SubstituteStretchedSource(placement, renderedPath: null, stretch);

        Assert.AreEqual(placement with { Stretch = null }, fallback,
            "An unrenderable source must degrade to exactly the pre-feature behaviour");
        Assert.IsTrue(fallback.PlaysAtNativeRateOnSpeedAdjustedSegment,
            "...and must still be reported as native-rate");
    }

    [TestMethod]
    public void ChangeSegmentAudioModeOperation_AppliesAndUndoes()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        string id = model.Segments[0].Id;

        var operation = new ChangeSegmentAudioModeOperation(id, SegmentAudioMode.Muted);
        operation.Execute(model);

        Assert.IsTrue(operation.ChangedModel);
        Assert.AreEqual(SegmentAudioMode.Muted, ((VideoSegment)model.Segments[0]).AudioMode);

        operation.Undo(model);
        Assert.AreEqual(
            SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode,
            "Undo must restore the previous mode, not the type default");
    }

    [TestMethod]
    public void ChangeSegmentAudioModeOperation_ChoosingTheCurrentMode_IsANoOp()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0, SegmentAudioMode.Native));
        var operation = new ChangeSegmentAudioModeOperation(model.Segments[0].Id, SegmentAudioMode.Native);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel, "Re-picking the active mode must not push an undo entry");
    }

    [TestMethod]
    public void ChangeSegmentAudioModeOperation_SurvivesAFurtherSpeedChange()
    {
        // The mode is a preference about this segment, not about one particular speed.
        var model = ModelWith(Video(0, 10, speed: 2.0));
        string id = model.Segments[0].Id;

        new ChangeSegmentAudioModeOperation(id, SegmentAudioMode.Muted).Execute(model);
        new ChangeSegmentSpeedOperation(id, 4.0).Execute(model);

        Assert.AreEqual(SegmentAudioMode.Muted, ((VideoSegment)model.Segments[0]).AudioMode);
    }

    [TestMethod]
    public void ChangeSegmentAudioModeOperation_UnknownSegment_IsANoOp()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        var operation = new ChangeSegmentAudioModeOperation("no-such-segment", SegmentAudioMode.Muted);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel);
        Assert.AreEqual(SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode);
    }

    #region Detaching a segment's audio

    private static IReadOnlyList<DetachedAudioSource> MicSource(
        double offsetSeconds = 0, double sourceDurationSec = 60)
        => [new DetachedAudioSource(PrimaryMic, "Microphone", offsetSeconds, TimeSpan.FromSeconds(sourceDurationSec))];

    [TestMethod]
    public void Detach_KeepsTheWholeSourceRange_NotJustTheAudiblePart()
    {
        // 10s of source at 2x: only 5s is audible while bound, and repositioning the 5s the
        // cut discards is the entire reason to detach.
        var model = ModelWith(Video(0, 10, speed: 2.0));
        var operation = new DetachSegmentAudioOperation(model.Segments[0].Id, MicSource());

        operation.Execute(model);

        Assert.AreEqual(1, model.AudioTracks.Count);
        var track = model.AudioTracks[0];
        AssertSeconds(0, track.StartTime, "The block starts where its segment starts");
        AssertSeconds(0, track.TrimStart, "...reading from the segment's own source in point");
        AssertSeconds(10, track.Duration!.Value, "...and keeps all 10s of source audio");
        Assert.AreEqual(1.0, track.Volume, "Recorded audio is detached at its captured level");
    }

    [TestMethod]
    public void Detach_SilencesTheSegmentSoNothingIsHeardTwice()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        new DetachSegmentAudioOperation(model.Segments[0].Id, MicSource()).Execute(model);

        Assert.AreEqual(SegmentAudioMode.Muted, ((VideoSegment)model.Segments[0]).AudioMode);

        // And the exporter agrees: the only placements left are the detached block's.
        var plan = ExportAudioPlan.Build(NewProject(), model, null);
        Assert.IsTrue(
            plan.All(p => p.Delay == TimeSpan.Zero && p.TakeDuration == TimeSpan.FromSeconds(10)),
            "The bound copy must be gone, leaving only the detached block");
    }

    [TestMethod]
    public void Detach_IsUndoable()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        var operation = new DetachSegmentAudioOperation(model.Segments[0].Id, MicSource());

        operation.Execute(model);
        operation.Undo(model);

        Assert.AreEqual(0, model.AudioTracks.Count, "Undo removes the blocks it created");
        Assert.AreEqual(
            SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode,
            "...and restores the mode the segment had before");
    }

    [TestMethod]
    public void Detach_AlignsThroughTheAudioOffset()
    {
        // Audio started 1s of source time late: the block cannot read before the file's start,
        // so it begins 0.5s into the segment (1s of source lead at 2x).
        var model = ModelWith(Video(0, 10, speed: 2.0));
        new DetachSegmentAudioOperation(model.Segments[0].Id, MicSource(offsetSeconds: -1.0)).Execute(model);

        var track = model.AudioTracks[0];
        AssertSeconds(0, track.TrimStart, "Cannot seek before the start of the file");
        AssertSeconds(0.5, track.StartTime, "1s of source lead is 0.5s of output at 2x");
        AssertSeconds(9, track.Duration!.Value, "Only the audio that exists is placed");
    }

    [TestMethod]
    public void Detach_PositionsABlockPerCapture()
    {
        var model = ModelWith(Video(4, 6, speed: 1.0));
        model.Segments[0].Start = TimeSpan.FromSeconds(4);

        var sources = new List<DetachedAudioSource>
        {
            new(PrimaryMic, "Microphone", 0, TimeSpan.FromSeconds(60)),
            new(@"C:\rec\primary\system_0.wav", "System audio", 0, TimeSpan.FromSeconds(60)),
        };

        new DetachSegmentAudioOperation(model.Segments[0].Id, sources).Execute(model);

        Assert.AreEqual(2, model.AudioTracks.Count, "Every capture becomes its own block");
        Assert.IsTrue(
            model.AudioTracks.All(t => t.StartTime == TimeSpan.FromSeconds(4)),
            "Both start where the segment does, so they stay in sync with each other");
        Assert.IsTrue(model.AudioTracks.All(t => t.TrimStart == TimeSpan.FromSeconds(4)));
    }

    [TestMethod]
    public void Detach_WithNoCaptures_IsANoOp()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        var operation = new DetachSegmentAudioOperation(model.Segments[0].Id, []);

        operation.Execute(model);

        Assert.IsFalse(operation.ChangedModel);
        Assert.AreEqual(0, model.AudioTracks.Count);
        Assert.AreEqual(
            SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode,
            "A segment with nothing to detach must not be silenced");
    }

    [TestMethod]
    public void DetachedBlock_MayRunPastItsSegment()
    {
        // The L-cut this makes possible: 10s of audio under a 5s segment keeps playing over
        // whatever follows, which is legal for an inserted track and impossible for a bound one.
        var model = ModelWith(
            Video(0, 10, speed: 2.0),
            Video(10, 5, speed: 1.0));
        new DetachSegmentAudioOperation(model.Segments[0].Id, MicSource()).Execute(model);

        var track = model.AudioTracks[0];
        Assert.IsTrue(
            track.StartTime + track.Duration!.Value > model.Segments[1].Start,
            "Detached audio is not cut at the segment boundary the way bound audio is");
    }

    [TestMethod]
    public void DetachedBlock_RemembersWhichSegmentItCameFrom()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        string id = model.Segments[0].Id;

        new DetachSegmentAudioOperation(id, MicSource()).Execute(model);

        Assert.AreEqual(id, model.AudioTracks[0].DetachedFromSegmentId);
        Assert.IsTrue(ReattachSegmentAudioOperation.HasDetachedAudio(model, id));
    }

    [TestMethod]
    public void Reattach_RemovesTheBlocksAndUnmutesTheSegment()
    {
        // Unmuting a detached segment while its blocks remain would sum the same recording
        // twice, so re-attaching is the only way back — and it must do both halves.
        var model = ModelWith(Video(0, 10, speed: 2.0));
        string id = model.Segments[0].Id;
        new DetachSegmentAudioOperation(id, MicSource()).Execute(model);

        var reattach = new ReattachSegmentAudioOperation(id);
        reattach.Execute(model);

        Assert.AreEqual(0, model.AudioTracks.Count, "The detached blocks are gone");
        Assert.AreEqual(
            SegmentAudioMode.TimeStretch, ((VideoSegment)model.Segments[0]).AudioMode,
            "...and the segment is audible again");

        var plan = ExportAudioPlan.Build(NewProject(), model, null);
        Assert.IsTrue(plan.Any(p => p.SourcePath == PrimaryMic),
            "The bound copy is back in the export, exactly once");
    }

    [TestMethod]
    public void Reattach_IsUndoable()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0));
        string id = model.Segments[0].Id;
        new DetachSegmentAudioOperation(id, MicSource()).Execute(model);

        var reattach = new ReattachSegmentAudioOperation(id);
        reattach.Execute(model);
        reattach.Undo(model);

        Assert.AreEqual(1, model.AudioTracks.Count, "Undo puts the detached block back");
        Assert.AreEqual(id, model.AudioTracks[0].DetachedFromSegmentId);
        Assert.AreEqual(
            SegmentAudioMode.Muted, ((VideoSegment)model.Segments[0]).AudioMode,
            "...and re-mutes the segment, or the audio would play twice");
    }

    [TestMethod]
    public void Reattach_WithNothingDetached_IsANoOp()
    {
        var model = ModelWith(Video(0, 10, speed: 2.0, SegmentAudioMode.Muted));
        var reattach = new ReattachSegmentAudioOperation(model.Segments[0].Id);

        reattach.Execute(model);

        Assert.IsFalse(reattach.ChangedModel);
        Assert.AreEqual(
            SegmentAudioMode.Muted, ((VideoSegment)model.Segments[0]).AudioMode,
            "A hand-muted segment must not be unmuted by a re-attach that found nothing");
    }

    [TestMethod]
    public void Reattach_OnlyTouchesItsOwnSegmentsBlocks()
    {
        var model = ModelWith(
            Video(0, 10, speed: 2.0),
            Video(10, 10, speed: 2.0));
        string first = model.Segments[0].Id;
        string second = model.Segments[1].Id;

        new DetachSegmentAudioOperation(first, MicSource()).Execute(model);
        new DetachSegmentAudioOperation(second, MicSource()).Execute(model);

        new ReattachSegmentAudioOperation(first).Execute(model);

        Assert.AreEqual(1, model.AudioTracks.Count);
        Assert.AreEqual(second, model.AudioTracks[0].DetachedFromSegmentId);
        Assert.AreEqual(SegmentAudioMode.Muted, ((VideoSegment)model.Segments[1]).AudioMode);
    }

    #endregion
}
