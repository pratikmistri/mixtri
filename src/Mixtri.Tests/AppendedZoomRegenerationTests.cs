using Mixtri.Core.Models;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

/// <summary>
/// Regression coverage for the appended-recording auto-zoom fix in
/// <c>EditorPage.GenerateAppendedZoomKeyframes</c>: generation for an appended source must
/// be idempotent (skip once keyframes exist for that source) and must honor
/// <see cref="TimelineModel.SuppressedClickTicks"/>, mirroring the primary recording's
/// already-fixed "generate once, then preserve" rule (see EditorPage.InitializePreviewAsync).
/// <c>EditorPage.xaml.cs</c> is a WinUI Page in the App project, which this test project does
/// not (and should not) reference, so <see cref="GenerateAppendedZoomKeyframes"/> below
/// mirrors the production algorithm exactly (same guard order, same suppression check, same
/// coordinate/cutoff math) to validate the TimelineModel-level contract the real fix relies on.
/// </summary>
[TestClass]
public sealed class AppendedZoomRegenerationTests
{
    /// <summary>
    /// Mirrors EditorPage.GenerateAppendedZoomKeyframes: only adds keyframes for
    /// <paramref name="sourceVideoFilePath"/> if no click-generated keyframes exist yet for
    /// that source, and skips any click tracked in <see cref="TimelineModel.SuppressedClickTicks"/>.
    /// </summary>
    private static void GenerateAppendedZoomKeyframes(
        TimelineModel model, string sourceVideoFilePath, MouseRecordingData? mouse,
        int width, int height, int cropOffsetX, int cropOffsetY,
        double mouseOffsetSeconds, double maxSourceSeconds)
    {
        bool existsForSource = model.ZoomKeyframes.Any(k =>
            k.SourceClickTicks.HasValue
            && string.Equals(k.SourceVideoFilePath, sourceVideoFilePath, StringComparison.OrdinalIgnoreCase));
        if (existsForSource) return;

        if (mouse is null || mouse.Clicks.Count == 0 || mouse.TickFrequency <= 0) return;

        foreach (var click in mouse.Clicks.Where(c => c.IsDown))
        {
            if (model.SuppressedClickTicks.Contains(click.TimestampTicks))
                continue;

            double t = (click.TimestampTicks - mouse.StartTimestampTicks) / mouse.TickFrequency - mouseOffsetSeconds;
            if (t < 0) continue;
            if (maxSourceSeconds > 0 && t > maxSourceSeconds) continue;

            model.ZoomKeyframes.Add(new ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(t),
                ZoomLevel = 2.0,
                CenterX = Math.Clamp((click.X - cropOffsetX) / (double)width, 0, 1),
                CenterY = Math.Clamp((click.Y - cropOffsetY) / (double)height, 0, 1),
                SourceClickTicks = click.TimestampTicks,
                SourceVideoFilePath = sourceVideoFilePath,
            });
        }
    }

    private static MouseRecordingData MakeMouse(long startTicks, double tickFrequency, params (long Ticks, int X, int Y)[] clicks)
        => TestMouseRecordingBuilder.WithClickTicks(startTicks, tickFrequency, clicks);

    [TestMethod]
    public void FirstGeneration_CreatesKeyframesTaggedWithSource()
    {
        var model = new TimelineModel();
        var mouse = MakeMouse(0, 1000, (1000, 100, 200), (2000, 300, 400));

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(2, model.ZoomKeyframes.Count);
        Assert.IsTrue(model.ZoomKeyframes.All(k => k.SourceVideoFilePath == "a.mp4"));
    }

    [TestMethod]
    public void TypingZoom_DoesNotSuppressClickZoomGeneration()
    {
        var model = new TimelineModel();
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(1),
            SourceVideoFilePath = "a.mp4",
            IsManual = true,
            HasAuthoredCenter = true,
        });
        var mouse = MakeMouse(0, 1000, (2000, 300, 400));

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(2, model.ZoomKeyframes.Count);
        Assert.AreEqual(1, model.ZoomKeyframes.Count(k => k.SourceClickTicks.HasValue));
    }

    [TestMethod]
    public void SecondRun_DoesNotDuplicate_AndPreservesManualEdit()
    {
        var model = new TimelineModel();
        var mouse = MakeMouse(0, 1000, (1000, 100, 200));

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);
        Assert.AreEqual(1, model.ZoomKeyframes.Count);

        // User manually adjusts the auto-generated keyframe (e.g. via a MoveZoomKeyframeOperation).
        var id = model.ZoomKeyframes[0].Id;
        model.ZoomKeyframes[0] = model.ZoomKeyframes[0] with { ZoomLevel = 3.5, IsManual = true };

        // Simulate a reload (e.g. "Record More" reconstructing the editor page) re-running
        // generation for the same appended source.
        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(1, model.ZoomKeyframes.Count,
            "Regeneration must not duplicate or replace existing keyframes for the source");
        Assert.AreEqual(id, model.ZoomKeyframes[0].Id);
        Assert.AreEqual(3.5, model.ZoomKeyframes[0].ZoomLevel, "Manual edit must survive regeneration");
    }

    [TestMethod]
    public void SuppressedClickTicks_AreNeverRegenerated_AcrossMultipleReloadCycles()
    {
        var model = new TimelineModel();
        var mouse = MakeMouse(0, 1000, (1000, 100, 200), (2000, 300, 400));

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);
        Assert.AreEqual(2, model.ZoomKeyframes.Count);

        // User deletes the auto-zoom for the first click (RemoveZoomKeyframeOperation tracks
        // the deletion by adding its SourceClickTicks to SuppressedClickTicks).
        var deleted = model.ZoomKeyframes.First(k => k.SourceClickTicks == 1000);
        model.SuppressedClickTicks.Add(1000);
        model.ZoomKeyframes.Remove(deleted);
        Assert.AreEqual(1, model.ZoomKeyframes.Count);

        // Simulate several editor reinitialization / "Record More" cycles.
        for (int i = 0; i < 3; i++)
            GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(1, model.ZoomKeyframes.Count, "Deleted auto-zoom must not return after re-init cycles");
        Assert.IsFalse(model.ZoomKeyframes.Any(k => k.SourceClickTicks == 1000));
    }

    [TestMethod]
    public void MultipleAppendedSources_AreIndependentlyGenerated()
    {
        var model = new TimelineModel();
        var mouseA = MakeMouse(0, 1000, (1000, 10, 10));
        var mouseB = MakeMouse(0, 1000, (5000, 50, 50));

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouseA, 1920, 1080, 0, 0, 0, 0);
        GenerateAppendedZoomKeyframes(model, "b.mp4", mouseB, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(1, model.ZoomKeyframes.Count(k => k.SourceVideoFilePath == "a.mp4"));
        Assert.AreEqual(1, model.ZoomKeyframes.Count(k => k.SourceVideoFilePath == "b.mp4"));

        // Re-running for "a.mp4" only must not touch "b.mp4"'s keyframe or duplicate its own.
        GenerateAppendedZoomKeyframes(model, "a.mp4", mouseA, 1920, 1080, 0, 0, 0, 0);
        Assert.AreEqual(2, model.ZoomKeyframes.Count);
    }

    [TestMethod]
    public void DuplicateSourcePath_SecondCallIsNoOp_CaseInsensitive()
    {
        var model = new TimelineModel();
        var mouse = MakeMouse(0, 1000, (1000, 10, 10), (2000, 20, 20));

        // Two VideoSegments referencing the same underlying file (e.g. a duplicated/reordered
        // segment) both attempt to generate for the same path, differing only by case.
        GenerateAppendedZoomKeyframes(model, "shared.mp4", mouse, 1920, 1080, 0, 0, 0, 0);
        GenerateAppendedZoomKeyframes(model, "SHARED.MP4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(2, model.ZoomKeyframes.Count, "Duplicate source path must not double-generate");
    }

    [TestMethod]
    public void EmptyCursorData_ProducesNoKeyframes_AndDoesNotThrow()
    {
        var model = new TimelineModel();
        var mouse = new MouseRecordingData { StartTimestampTicks = 0, TickFrequency = 1000 }; // no clicks recorded

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(0, model.ZoomKeyframes.Count);
    }

    [TestMethod]
    public void CorruptCursorData_ZeroTickFrequency_ProducesNoKeyframes_AndDoesNotThrow()
    {
        var model = new TimelineModel();
        var mouse = new MouseRecordingData
        {
            StartTimestampTicks = 0,
            TickFrequency = 0, // corrupt/degenerate recording data
            Clicks = [new ClickEvent(1000, 10, 10, MouseButton.Left, true)],
        };

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(0, model.ZoomKeyframes.Count);
    }

    [TestMethod]
    public void NullCursorData_ProducesNoKeyframes_AndDoesNotThrow()
    {
        var model = new TimelineModel();

        GenerateAppendedZoomKeyframes(model, "a.mp4", null, 1920, 1080, 0, 0, 0, 0);

        Assert.AreEqual(0, model.ZoomKeyframes.Count);
    }

    [TestMethod]
    public void SourceSpecificTimestampSpace_UsesSegmentOwnOffsetAndDuration()
    {
        var model = new TimelineModel();
        // An appended recording has its own clock (StartTimestampTicks), independent of the
        // primary recording's mouse-data timestamps, plus its own mouse->video offset and
        // source-duration cutoff.
        var mouse = MakeMouse(10_000, 1000,
            (10_000 + 1000, 10, 10),  // t = 1.0s - 0.5s offset = 0.5s -> kept
            (10_000 + 5000, 20, 20)); // t = 5.0s - 0.5s offset = 4.5s -> beyond 3s cutoff, dropped

        GenerateAppendedZoomKeyframes(model, "a.mp4", mouse, 1920, 1080, 0, 0,
            mouseOffsetSeconds: 0.5, maxSourceSeconds: 3.0);

        Assert.AreEqual(1, model.ZoomKeyframes.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), model.ZoomKeyframes[0].Timestamp);
    }
}
