using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Verifies that the source↔output time mapping (used to keep zoom, click, and
/// cursor visualizations aligned with the video) stays correct after the primary
/// video segments are reordered, moved, trimmed, or split by inserted text slides.
/// </summary>
[TestClass]
public sealed class TimelineSyncMappingTests
{
    private const string PrimaryPath = "primary.mp4";

    private static VideoSegment Video(double srcStartSec, double srcDurSec, double speed = 1.0)
        => TestTimelineBuilder.Video(PrimaryPath, srcStartSec, srcDurSec, speed);

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
        => TestTimelineBuilder.ModelWithPrimaryPath(PrimaryPath, segments);

    [TestMethod]
    public void NoSegments_IsIdentity()
    {
        var model = new TimelineModel { PrimaryVideoFilePath = PrimaryPath };
        Assert.AreEqual(TimeSpan.FromSeconds(7), model.SourceToOutputTime(TimeSpan.FromSeconds(7)));
    }

    [TestMethod]
    public void ContiguousSegments_MapToRecordedOrder()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        // Source 12s lives in segment B, which sits at output 10..20.
        Assert.AreEqual(TimeSpan.FromSeconds(12), model.SourceToOutputTime(TimeSpan.FromSeconds(12)));
        Assert.AreEqual(TimeSpan.FromSeconds(3), model.SourceToOutputTime(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public void ReorderedSegments_RemapByTimelineOrderNotSourceOrder()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(a, b);

        // Move B before A (FCP-style reorder).
        model.Segments.Clear();
        model.Segments.AddRange(new[] { b, a });
        model.RecalculateSegmentPositions();

        // B now occupies output 0..10, so its source time 12s maps to output 2s.
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.SourceToOutputTime(TimeSpan.FromSeconds(12)));
        // A now occupies output 10..20, so its source time 3s maps to output 13s.
        Assert.AreEqual(TimeSpan.FromSeconds(13), model.SourceToOutputTime(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public void InsertedTextSlide_ShiftsLaterSourceContent()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) };
        var video = Video(0, 10);
        var model = ModelWith(slide, video);

        // Video now starts at output 3s; its source time 4s maps to output 7s.
        Assert.AreEqual(TimeSpan.FromSeconds(7), model.SourceToOutputTime(TimeSpan.FromSeconds(4)));
    }

    [TestMethod]
    public void TrimmedInPoint_SourceBeforeKeptRange_IsNotMapped()
    {
        // Source [5..10] kept, rendered at output 0..5.
        var model = ModelWith(Video(5, 5));

        Assert.IsFalse(model.TrySourceToOutputTime(TimeSpan.FromSeconds(2), out _),
            "Source time trimmed out should report no output position");

        Assert.IsTrue(model.TrySourceToOutputTime(TimeSpan.FromSeconds(7), out var output));
        Assert.AreEqual(TimeSpan.FromSeconds(2), output);
    }

    [TestMethod]
    public void TrimmedOut_FallbackClampsToNearestBoundary()
    {
        var model = ModelWith(Video(5, 5)); // output 0..5

        // Before the kept range clamps to the segment start.
        Assert.AreEqual(TimeSpan.Zero, model.SourceToOutputTime(TimeSpan.FromSeconds(2)));
        // After the kept range clamps to the segment end.
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.SourceToOutputTime(TimeSpan.FromSeconds(99)));
    }

    [TestMethod]
    public void SpeedFactor_CompressesOutputMapping()
    {
        // 10s of source at 2x => 5s of output.
        var model = ModelWith(Video(0, 10, speed: 2.0));

        // Source 4s => local source 4s / 2 = 2s output.
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.SourceToOutputTime(TimeSpan.FromSeconds(4)));
    }

    [TestMethod]
    public void OutputToSource_RoundTripsForVideo()
    {
        var a = Video(0, 10);
        var b = Video(10, 10);
        var model = ModelWith(b, a); // reordered

        // Output 2s is inside B (source 12s); round-trips back.
        var src = model.OutputToSourceTime(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(src);
        Assert.AreEqual(TimeSpan.FromSeconds(12), src!.Value);
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.SourceToOutputTime(src.Value));
    }

    [TestMethod]
    public void OutputToSource_OnTextSlide_ReturnsNull()
    {
        var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(3) };
        var model = ModelWith(slide, Video(0, 10));

        Assert.IsNull(model.OutputToSourceTime(TimeSpan.FromSeconds(1)));
    }
}
