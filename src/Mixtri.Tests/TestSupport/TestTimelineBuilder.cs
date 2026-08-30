using Mixtri.Core.Timeline;

namespace Mixtri.Tests.TestSupport;

/// <summary>
/// Shared builders for the <c>ModelWith(params TimelineSegment[])</c> and
/// <c>Video(...)</c> factories that were copy-pasted (and had silently diverged) across
/// several test files. Preserves both existing shapes exactly:
/// <list type="bullet">
/// <item><see cref="ModelWith"/> — a <see cref="TimelineModel"/> with NO
/// <see cref="TimelineModel.PrimaryVideoFilePath"/> set (the "variant B" call sites:
/// SlideTransitionsTests, TransitionCrossCuttingTests, TransitionEditOperationsTests,
/// TransitionResolverTests).</item>
/// <item><see cref="ModelWithPrimaryPath"/> — a <see cref="TimelineModel"/> WITH
/// <see cref="TimelineModel.PrimaryVideoFilePath"/> set (the "variant A" call sites:
/// SegmentEditOperationsTests, SegmentStyleOverrideTests, SplitSegmentOperationTests,
/// TimelineSyncMappingTests). Segment code compares file paths with
/// <see cref="StringComparison.OrdinalIgnoreCase"/> to decide whether two segments share a
/// source, and a model with no primary path can take a different branch — do NOT collapse
/// these two variants into one.</item>
/// <item><see cref="Video(string, double, double, double)"/> — the source-start/duration/speed
/// shaped factory (SegmentEditOperationsTests, SplitSegmentOperationTests,
/// TimelineSyncMappingTests all pass "primary.mp4" unchanged).</item>
/// <item><see cref="TransitionVideo"/> — the duration-only shaped factory used by the
/// transition-boundary tests. <paramref name="videoFilePath"/> is required (not defaulted)
/// because call sites disagree on the literal ("C:\clip.mp4" in
/// TransitionEditOperationsTests vs "C:\primary.mp4" elsewhere) and that difference must be
/// preserved, not normalized.</item>
/// </list>
/// </summary>
internal static class TestTimelineBuilder
{
    public static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel();
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    public static TimelineModel ModelWithPrimaryPath(string primaryVideoFilePath, params TimelineSegment[] segments)
    {
        var model = new TimelineModel { PrimaryVideoFilePath = primaryVideoFilePath };
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    public static VideoSegment Video(string videoFilePath, double srcStartSec, double srcDurSec, double speed = 1.0)
    {
        return new VideoSegment
        {
            VideoFilePath = videoFilePath,
            SourceStart = TimeSpan.FromSeconds(srcStartSec),
            SourceDuration = TimeSpan.FromSeconds(srcDurSec),
            Duration = TimeSpan.FromSeconds(srcDurSec / speed),
            SpeedFactor = speed,
        };
    }

    public static VideoSegment TransitionVideo(
        string videoFilePath, double durSec, TransitionConfig? inTransition = null, double speedFactor = 1.0)
    {
        return new VideoSegment
        {
            VideoFilePath = videoFilePath,
            SourceStart = TimeSpan.Zero,
            SourceDuration = TimeSpan.FromSeconds(durSec * speedFactor),
            Duration = TimeSpan.FromSeconds(durSec),
            SpeedFactor = speedFactor,
            InTransition = inTransition,
        };
    }
}
