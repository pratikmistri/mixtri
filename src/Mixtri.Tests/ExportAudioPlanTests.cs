using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

/// <summary>
/// Regression tests for <see cref="ExportAudioPlan"/>: the pure mapping that decides
/// which audio the exporter muxes and where it lands on the output timeline.
/// The rule under test is that <b>any</b> active segment timeline drives audio placement
/// (not only timelines containing a text slide), so trims, splits, deletes, reorders and
/// appended recordings can never mux the full, unedited source audio.
/// </summary>
[TestClass]
public sealed class ExportAudioPlanTests
{
    private const string Primary = @"C:\rec\primary\video.mp4";
    private const string Appended = @"C:\rec\appended\video.mp4";
    private const string PrimaryMic = @"C:\rec\primary\mic_0.wav";
    private const string AppendedMic = @"C:\rec\appended\mic_0.wav";

    private static Project NewProject(params string[] audioPaths) => new()
    {
        VideoFilePath = Primary,
        Duration = TimeSpan.FromSeconds(10),
        AudioFilePaths = [.. audioPaths],
    };

    private static VideoSegment Video(
        string path, double sourceStartSec, double sourceDurationSec,
        double speed = 1.0, string[]? audioPaths = null) => new()
        {
            VideoFilePath = path,
            SourceStart = TimeSpan.FromSeconds(sourceStartSec),
            SourceDuration = TimeSpan.FromSeconds(sourceDurationSec),
            Duration = TimeSpan.FromSeconds(sourceDurationSec / speed),
            SpeedFactor = speed,
            AudioFilePaths = audioPaths is null ? [] : [.. audioPaths],
        };

    private static TextSlideSegment Slide(double durationSec) => new()
    {
        Duration = TimeSpan.FromSeconds(durationSec),
    };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = Primary };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    private static AudioPlacement Single(IReadOnlyList<AudioPlacement> plan, string path, AudioSourceKind kind)
        => plan.Single(p => p.SourcePath == path && p.Kind == kind);

    private static void AssertSeconds(double expected, TimeSpan actual, string message)
        => Assert.AreEqual(expected, actual.TotalSeconds, 0.001, message);

    #region Segment-driven placement

    [TestMethod]
    public void TrimmedSegment_WithoutTextSlide_StillTrimsAudio()
    {
        var project = NewProject(PrimaryMic);
        // User trimmed the recording down to source 2s..5s. No text slide involved.
        var model = ModelWith(Video(Primary, sourceStartSec: 2, sourceDurationSec: 3));

        var plan = ExportAudioPlan.Build(project, model, null);

        var embedded = Single(plan, Primary, AudioSourceKind.EmbeddedVideoTrack);
        AssertSeconds(2, embedded.TrimFromStart, "Audio must start at the segment's source in point");
        AssertSeconds(3, embedded.TakeDuration!.Value, "Only the kept range may be muxed");
        AssertSeconds(0, embedded.Delay, "First segment starts at output zero");

        var mic = Single(plan, PrimaryMic, AudioSourceKind.AudioFile);
        AssertSeconds(2, mic.TrimFromStart, "Separate audio tracks follow the same trim");
        AssertSeconds(3, mic.TakeDuration!.Value, "Separate audio tracks follow the same trim");
    }

    [TestMethod]
    public void ReorderedSegments_PlaceAudioInOutputOrder()
    {
        var project = NewProject();
        // Second half of the recording was moved in front of the first half.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5),
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(5, embedded[0].TrimFromStart, "First output segment plays the later source range");
        AssertSeconds(0, embedded[0].Delay, "First output segment starts at zero");
        AssertSeconds(0, embedded[1].TrimFromStart, "Second output segment plays the earlier source range");
        AssertSeconds(5, embedded[1].Delay, "Second output segment starts after the first");
    }

    [TestMethod]
    public void DeletedMiddle_SkipsTheDeletedSourceRange()
    {
        var project = NewProject();
        // Source 4s..6s was deleted; the two survivors are contiguous on the output.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 4),
            Video(Primary, sourceStartSec: 6, sourceDurationSec: 4));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(4, embedded[0].TakeDuration!.Value, "Audio stops where the cut starts");
        AssertSeconds(6, embedded[1].TrimFromStart, "Audio resumes after the deleted range");
        AssertSeconds(4, embedded[1].Delay, "Deleted range leaves no gap on the output");
    }

    [TestMethod]
    public void TextSlideBetweenSegments_DelaysFollowingAudio()
    {
        var project = NewProject();
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Slide(3),
            Video(Primary, sourceStartSec: 5, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        Assert.AreEqual(2, embedded.Count);
        AssertSeconds(0, embedded[0].Delay, "Audio before the slide is unshifted");
        AssertSeconds(8, embedded[1].Delay, "Audio after the slide is delayed by the slide duration");
    }

    [TestMethod]
    public void SpedUpSegment_ClampsTakeToTheOutputDuration()
    {
        var project = NewProject();
        // 10s of source played at 2x occupies 5s of output. Audio is muxed at native
        // rate, so only 5s of it may play or it would bleed into the next segment.
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 10, speed: 2.0),
            Video(Primary, sourceStartSec: 10, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = plan.Where(p => p.Kind == AudioSourceKind.EmbeddedVideoTrack).ToList();

        AssertSeconds(5, embedded[0].TakeDuration!.Value, "Sped-up audio must not overlap the next segment");
        AssertSeconds(5, embedded[1].Delay, "The next segment still starts at its output position");
    }

    [TestMethod]
    public void SlowedSegment_TakesOnlyTheAvailableSource()
    {
        var project = NewProject();
        // 5s of source at 0.5x occupies 10s of output; audio simply ends early.
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 5, speed: 0.5));

        var plan = ExportAudioPlan.Build(project, model, null);
        var embedded = Single(plan, Primary, AudioSourceKind.EmbeddedVideoTrack);

        AssertSeconds(5, embedded.TakeDuration!.Value, "Only the existing source audio can play");
        AssertSeconds(0, embedded.Delay, "Slow motion does not shift the segment start");
    }

    [TestMethod]
    public void AppendedSegment_UsesItsOwnSourcesNotThePrimaryOnes()
    {
        var project = NewProject(PrimaryMic);
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 5),
            Video(Appended, sourceStartSec: 0, sourceDurationSec: 4, audioPaths: [AppendedMic]));

        var plan = ExportAudioPlan.Build(project, model, null);

        var appendedEmbedded = Single(plan, Appended, AudioSourceKind.EmbeddedVideoTrack);
        AssertSeconds(5, appendedEmbedded.Delay, "Appended audio starts where its clip starts");
        AssertSeconds(4, appendedEmbedded.TakeDuration!.Value, "Appended audio spans its own clip");

        var appendedMic = Single(plan, AppendedMic, AudioSourceKind.AudioFile);
        AssertSeconds(5, appendedMic.Delay, "The appended recording's own audio file is used");

        // The primary recording's separate audio must not be replayed under the
        // appended clip.
        Assert.AreEqual(1, plan.Count(p => p.SourcePath == PrimaryMic),
            "Primary audio belongs to the primary clip only");
        AssertSeconds(0, Single(plan, PrimaryMic, AudioSourceKind.AudioFile).Delay,
            "Primary audio stays on the primary clip");
    }

    [TestMethod]
    public void PositiveAudioOffset_SkipsPreRollOnAudioFilesOnly()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = 0.5; // audio recording started 0.5s early
        var model = ModelWith(Video(Primary, sourceStartSec: 2, sourceDurationSec: 3));

        var plan = ExportAudioPlan.Build(project, model, null);

        AssertSeconds(2.5, Single(plan, PrimaryMic, AudioSourceKind.AudioFile).TrimFromStart,
            "The WAV pre-roll is skipped so audio lines up with the video");
        AssertSeconds(2, Single(plan, Primary, AudioSourceKind.EmbeddedVideoTrack).TrimFromStart,
            "Embedded audio is already aligned with its own video frames");
    }

    [TestMethod]
    public void PositiveAudioOffset_TakeStillEndsAtTheSegmentsSourceEnd()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = 1.5;
        // Segment keeps source 2s..5s, so the aligned audio interval is 3.5s..6.5s.
        var model = ModelWith(Video(Primary, sourceStartSec: 2, sourceDurationSec: 3));

        var mic = Single(
            ExportAudioPlan.Build(project, model, null), PrimaryMic, AudioSourceKind.AudioFile);

        AssertSeconds(3.5, mic.TrimFromStart, "Playback starts at the aligned in point");
        AssertSeconds(3, mic.TakeDuration!.Value,
            "The take may never be longer than the segment's own source range");
        AssertSeconds(6.5, mic.TrimFromStart + mic.TakeDuration!.Value,
            "The audio out point maps exactly to the segment's source out point");
        AssertSeconds(0, mic.Delay, "A pre-roll offset does not shift the output position");
    }

    [TestMethod]
    public void PositiveAudioOffset_OnSpedUpSegment_StaysInsideTheSegment()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = 1.0;
        // 8s of source at 2x → 4s of output.
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 8, speed: 2.0));

        var mic = Single(
            ExportAudioPlan.Build(project, model, null), PrimaryMic, AudioSourceKind.AudioFile);

        AssertSeconds(1, mic.TrimFromStart, "Aligned in point still honours the offset");
        AssertSeconds(4, mic.TakeDuration!.Value, "Capped to the segment's output duration");
        AssertSeconds(0, mic.Delay, "Segment starts at output zero");
    }

    [TestMethod]
    public void NegativeAudioOffset_OnSpedUpSegment_ScalesTheLeadIntoOutputTime()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = -1.0; // audio started 1s of source time late
        // 10s of source at 2x → 5s of output.
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 10, speed: 2.0));

        var mic = Single(
            ExportAudioPlan.Build(project, model, null), PrimaryMic, AudioSourceKind.AudioFile);

        AssertSeconds(0, mic.TrimFromStart, "Cannot seek before the start of the file");
        AssertSeconds(0.5, mic.Delay, "1s of source lead is 0.5s of output at 2x");
        AssertSeconds(4.5, mic.TakeDuration!.Value, "Playback still ends with the segment");
        AssertSeconds(5, mic.Delay + mic.TakeDuration!.Value,
            "Audio never extends past the segment's output end");
    }

    [TestMethod]
    public void AudioStartingAfterTheSegmentEnds_IsDropped()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = -20; // audio began long after this segment
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 5));

        var plan = ExportAudioPlan.Build(project, model, null);

        Assert.AreEqual(0, plan.Count(p => p.Kind == AudioSourceKind.AudioFile),
            "A track with no audible range inside the segment must not be placed");
    }

    [TestMethod]
    public void SpeedAdjustedSegments_AreFlaggedAsPlayingAtNativeRate()
    {
        var project = NewProject(PrimaryMic);
        var model = ModelWith(
            Video(Primary, sourceStartSec: 0, sourceDurationSec: 4),
            Video(Primary, sourceStartSec: 4, sourceDurationSec: 6, speed: 1.5));

        var plan = ExportAudioPlan.Build(project, model, null);

        var normal = plan.Where(p => p.Delay == TimeSpan.Zero).ToList();
        Assert.IsTrue(normal.All(p => !p.PlaysAtNativeRateOnSpeedAdjustedSegment),
            "Unmodified segments are fully synchronized");

        var sped = plan.Where(p => p.Delay > TimeSpan.Zero).ToList();
        Assert.AreEqual(2, sped.Count);
        Assert.IsTrue(sped.All(p => p.PlaysAtNativeRateOnSpeedAdjustedSegment),
            "Speed-adjusted segments must advertise that their audio is not time-scaled");
    }

    [TestMethod]
    public void LegacyPlacements_AreNeverFlaggedAsSpeedAdjusted()
    {
        var project = NewProject(PrimaryMic);

        Assert.IsTrue(ExportAudioPlan.Build(project, null, null)
            .All(p => !p.PlaysAtNativeRateOnSpeedAdjustedSegment));
    }

    [TestMethod]
    public void NegativeAudioOffset_DelaysAudioInsteadOfSeekingBeforeTheFile()
    {
        var project = NewProject(PrimaryMic);
        project.AudioToVideoOffsetSeconds = -0.5; // audio capture started 0.5s late
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 10));

        var mic = Single(
            ExportAudioPlan.Build(project, model, null), PrimaryMic, AudioSourceKind.AudioFile);

        AssertSeconds(0, mic.TrimFromStart, "Cannot seek before the start of the file");
        AssertSeconds(0.5, mic.Delay, "The missing head becomes leading silence");
        AssertSeconds(9.5, mic.TakeDuration!.Value, "Playback still ends with the segment");
    }

    [TestMethod]
    public void DegenerateSegment_ProducesNoPlacements()
    {
        var project = NewProject(PrimaryMic);
        var model = ModelWith(Video(Primary, sourceStartSec: 0, sourceDurationSec: 0));

        Assert.AreEqual(0, ExportAudioPlan.Build(project, model, null).Count);
    }

    #endregion

    #region Legacy (no segments)

    [TestMethod]
    public void LegacyTimeline_UsesTrimRange()
    {
        var project = NewProject(PrimaryMic);
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(10),
            TrimStart = TimeSpan.FromSeconds(2),
            TrimEnd = TimeSpan.FromSeconds(8),
        };
        var mapper = new TimelineMapper(model, 30);

        var plan = ExportAudioPlan.Build(project, model, mapper);

        Assert.AreEqual(2, plan.Count);
        foreach (var placement in plan)
        {
            AssertSeconds(2, placement.TrimFromStart, "Legacy placement starts at TrimStart");
            AssertSeconds(6, placement.TakeDuration!.Value, "Legacy placement spans the trim range");
            AssertSeconds(0, placement.Delay, "Legacy placement is never delayed");
        }
    }

    [TestMethod]
    public void LegacyTimeline_WithoutMapper_KeepsFullSources()
    {
        var project = NewProject(PrimaryMic);

        var plan = ExportAudioPlan.Build(project, null, null);

        Assert.AreEqual(2, plan.Count);
        Assert.IsTrue(plan.All(p => p.TakeDuration is null), "Untrimmed sources play in full");
        Assert.IsTrue(plan.All(p => p.TrimFromStart == TimeSpan.Zero));
    }

    #endregion
}
