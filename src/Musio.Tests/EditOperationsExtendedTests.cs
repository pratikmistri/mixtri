using Musio.Core.Timeline;

namespace Musio.Tests;

[TestClass]
public sealed class EditOperationsExtendedTests
{
    private static TimelineModel CreateModelWithClips()
    {
        var model = new TimelineModel
        {
            Duration = TimeSpan.FromSeconds(30),
            TrimStart = TimeSpan.Zero,
            TrimEnd = TimeSpan.FromSeconds(30),
        };
        model.Clips.Add(new TimelineClip(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Part 1"));
        model.Clips.Add(new TimelineClip(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Part 2"));
        model.Clips.Add(new TimelineClip(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), "Part 3"));
        return model;
    }

    #region MoveZoomKeyframeOperation

    [TestMethod]
    public void MoveZoomKeyframe_MovesTimestamp()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            IsManual = true,
        };
        model.ZoomKeyframes.Add(kf);

        var op = new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5));
        op.Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(5), model.ZoomKeyframes[0].Timestamp);
    }

    [TestMethod]
    public void MoveZoomKeyframe_Undo_RestoresOriginal()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(3),
            ZoomLevel = 2.0,
            IsManual = true,
        };
        model.ZoomKeyframes.Add(kf);

        var op = new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(7));
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(TimeSpan.FromSeconds(3), model.ZoomKeyframes[0].Timestamp);
    }

    [TestMethod]
    public void MoveZoomKeyframe_AutoToManual_SuppressesClick()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        long clickTicks = 20_000_000;
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            IsManual = false,
            SourceClickTicks = clickTicks,
        };
        model.ZoomKeyframes.Add(kf);

        var op = new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5));
        op.Execute(model);

        Assert.IsTrue(model.ZoomKeyframes[0].IsManual, "Should become manual");
        Assert.IsTrue(model.SuppressedClickTicks.Contains(clickTicks), "Should suppress auto click");

        op.Undo(model);
        Assert.IsFalse(model.SuppressedClickTicks.Contains(clickTicks), "Undo should unsuppress");
    }

    // ── Occupancy pinning ──
    //
    // SourceVideoFilePath is not a sufficient key for WHICH copy of a recording a zoom belongs
    // to: the same file can sit on several tracks, and when PrimaryVideoFilePath is null a null
    // path matches every segment there is. Resolution then fell through to "first match in list
    // order", so a chip rendered in another clip's band and hopped clips as it was dragged.
    // Editing pins the occurrence, exactly as it promotes IsManual.

    [TestMethod]
    public void MoveZoomKeyframe_PinsTheOwningSegmentItWasDraggedWithin()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), ZoomLevel = 2.0, IsManual = true };
        model.ZoomKeyframes.Add(kf);

        new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5), "segment-a").Execute(model);

        Assert.AreEqual("segment-a", model.ZoomKeyframes[0].OwningSegmentId);
    }

    /// <summary>
    /// A keyframe that already names its occurrence keeps it. Re-pinning on every move would
    /// let a drag that momentarily resolved elsewhere rewrite ownership permanently.
    /// </summary>
    [TestMethod]
    public void MoveZoomKeyframe_DoesNotRepinAnAlreadyPinnedKeyframe()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            IsManual = true,
            OwningSegmentId = "original-segment",
        };
        model.ZoomKeyframes.Add(kf);

        new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5), "some-other-segment").Execute(model);

        Assert.AreEqual("original-segment", model.ZoomKeyframes[0].OwningSegmentId);
    }

    [TestMethod]
    public void MoveZoomKeyframe_Undo_RestoresTheUnpinnedState()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), ZoomLevel = 2.0, IsManual = true };
        model.ZoomKeyframes.Add(kf);

        var op = new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5), "segment-a");
        op.Execute(model);
        op.Undo(model);

        Assert.IsNull(model.ZoomKeyframes[0].OwningSegmentId,
            "Undo must leave the keyframe unpinned, not pinned to where the undone drag put it");
        Assert.AreEqual(TimeSpan.FromSeconds(2), model.ZoomKeyframes[0].Timestamp);
    }

    /// <summary>
    /// Callers that have no occurrence to offer (keyboard nudges, auto-generated edits) must
    /// leave the keyframe exactly as unpinned as they found it.
    /// </summary>
    [TestMethod]
    public void MoveZoomKeyframe_WithNoSegmentOffered_LeavesTheKeyframeUnpinned()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), ZoomLevel = 2.0, IsManual = true };
        model.ZoomKeyframes.Add(kf);

        new MoveZoomKeyframeOperation(kf.Id, TimeSpan.FromSeconds(5)).Execute(model);

        Assert.IsNull(model.ZoomKeyframes[0].OwningSegmentId);
    }

    #endregion

    #region AddZoomSegmentOperation

    [TestMethod]
    public void AddZoomSegment_FromRange_AddsKeyframe()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };

        var op = new AddZoomSegmentOperation(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), 3.0);
        op.Execute(model);

        Assert.AreEqual(1, model.ZoomKeyframes.Count);
        Assert.AreEqual(3.0, model.ZoomKeyframes[0].ZoomLevel);
    }

    [TestMethod]
    public void AddZoomSegment_Undo_RemovesKeyframe()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };

        var op = new AddZoomSegmentOperation(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 2.0);
        op.Execute(model);
        Assert.AreEqual(1, model.ZoomKeyframes.Count);

        op.Undo(model);
        Assert.AreEqual(0, model.ZoomKeyframes.Count);
    }

    [TestMethod]
    public void AddZoomSegment_Multiple_SortedByTimestamp()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(20) };

        new AddZoomSegmentOperation(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), 2.0).Execute(model);
        new AddZoomSegmentOperation(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 2.0).Execute(model);

        Assert.IsTrue(model.ZoomKeyframes[0].Timestamp < model.ZoomKeyframes[1].Timestamp,
            "Keyframes should be sorted by timestamp");
    }

    [TestMethod]
    public void AddZoomSegment_CreatedId_IsAvailable()
    {
        var op = new AddZoomSegmentOperation(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 2.0);

        Assert.IsFalse(string.IsNullOrEmpty(op.CreatedId));
    }

    #endregion

    #region UpdateZoomSegmentPropertiesOperation

    [TestMethod]
    public void UpdateZoomProperties_ChangesZoomLevel()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), ZoomLevel = 2.0, IsManual = true };
        model.ZoomKeyframes.Add(kf);

        var op = new UpdateZoomSegmentPropertiesOperation(kf.Id, zoomLevel: 3.5);
        op.Execute(model);

        Assert.AreEqual(3.5, model.ZoomKeyframes[0].ZoomLevel);
    }

    [TestMethod]
    public void UpdateZoomProperties_ClampsCenterValues()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe { Timestamp = TimeSpan.FromSeconds(2), ZoomLevel = 2.0, IsManual = true };
        model.ZoomKeyframes.Add(kf);

        var op = new UpdateZoomSegmentPropertiesOperation(kf.Id, centerX: 1.5, centerY: -0.5);
        op.Execute(model);

        Assert.AreEqual(1.0, model.ZoomKeyframes[0].CenterX, "CenterX should be clamped to 1.0");
        Assert.AreEqual(0.0, model.ZoomKeyframes[0].CenterY, "CenterY should be clamped to 0.0");
    }

    [TestMethod]
    public void UpdateZoomProperties_Undo_RestoresAll()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(10) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(2),
            ZoomLevel = 2.0,
            CenterX = 0.3,
            CenterY = 0.7,
            IsManual = true,
        };
        model.ZoomKeyframes.Add(kf);

        var op = new UpdateZoomSegmentPropertiesOperation(kf.Id, zoomLevel: 4.0, centerX: 0.9, centerY: 0.1);
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(2.0, model.ZoomKeyframes[0].ZoomLevel);
        Assert.AreEqual(0.3, model.ZoomKeyframes[0].CenterX);
        Assert.AreEqual(0.7, model.ZoomKeyframes[0].CenterY);
    }

    #endregion

    #region ApplyClipSpeedOperation

    [TestMethod]
    public void ApplyClipSpeed_ChangesClipDuration()
    {
        var model = CreateModelWithClips();

        // Double speed on first clip: [0-10] → source 10s at 2x = 5s output
        var op = new ApplyClipSpeedOperation(0, 2.0);
        op.Execute(model);

        Assert.AreEqual(2.0, model.Clips[0].SpeedFactor);
        Assert.AreEqual(TimeSpan.FromSeconds(5), model.Clips[0].End - model.Clips[0].Start);
    }

    [TestMethod]
    public void ApplyClipSpeed_ShiftsSubsequentClips()
    {
        var model = CreateModelWithClips();
        var originalClip2Start = model.Clips[1].Start;

        var op = new ApplyClipSpeedOperation(0, 2.0);
        op.Execute(model);

        Assert.IsTrue(model.Clips[1].Start < originalClip2Start,
            "Subsequent clips should shift left when clip shrinks");
    }

    [TestMethod]
    public void ApplyClipSpeed_Undo_RestoresEverything()
    {
        var model = CreateModelWithClips();
        var originalDuration = model.Duration;
        var originalClipCount = model.Clips.Count;

        var op = new ApplyClipSpeedOperation(0, 2.0);
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(originalDuration, model.Duration);
        Assert.AreEqual(originalClipCount, model.Clips.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Clips[0].End - model.Clips[0].Start);
    }

    [TestMethod]
    public void ApplyClipSpeed_InvalidIndex_DoesNotCrash()
    {
        var model = CreateModelWithClips();

        var op = new ApplyClipSpeedOperation(99, 2.0);
        op.Execute(model); // Should not throw

        Assert.AreEqual(3, model.Clips.Count, "Model should remain unchanged");
    }

    #endregion

    #region RippleDeleteOperation

    [TestMethod]
    public void RippleDelete_RemovesClipAndShifts()
    {
        var model = CreateModelWithClips();

        var op = new RippleDeleteOperation(1); // Remove "Part 2" [10-20]
        op.Execute(model);

        Assert.AreEqual(2, model.Clips.Count);
        // Part 3 should shift left by 10s
        Assert.AreEqual(TimeSpan.FromSeconds(10), model.Clips[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Clips[1].End);
    }

    [TestMethod]
    public void RippleDelete_ReducesDuration()
    {
        var model = CreateModelWithClips();

        var op = new RippleDeleteOperation(0);
        op.Execute(model);

        Assert.AreEqual(TimeSpan.FromSeconds(20), model.Duration);
    }

    [TestMethod]
    public void RippleDelete_RemovesContainedZoomKeyframes()
    {
        var model = CreateModelWithClips();
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(15), // Within Part 2 [10-20]
            ZoomLevel = 2.0,
        });
        model.ZoomKeyframes.Add(new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(25), // Within Part 3 [20-30]
            ZoomLevel = 2.0,
        });

        var op = new RippleDeleteOperation(1);
        op.Execute(model);

        Assert.AreEqual(1, model.ZoomKeyframes.Count, "Keyframe in deleted clip should be removed");
        Assert.IsTrue(model.ZoomKeyframes[0].Timestamp < TimeSpan.FromSeconds(20),
            "Remaining keyframe should be shifted left");
    }

    [TestMethod]
    public void RippleDelete_Undo_RestoresEverything()
    {
        var model = CreateModelWithClips();
        var originalDuration = model.Duration;

        var op = new RippleDeleteOperation(1);
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(3, model.Clips.Count);
        Assert.AreEqual(originalDuration, model.Duration);
    }

    [TestMethod]
    public void RippleDelete_InvalidIndex_DoesNotCrash()
    {
        var model = CreateModelWithClips();

        var op = new RippleDeleteOperation(-1);
        op.Execute(model); // Should not throw

        Assert.AreEqual(3, model.Clips.Count);
    }

    #endregion

    #region ResizeZoomSegmentOperation

    [TestMethod]
    public void ResizeZoomSegment_RightEdge_ExtendsEnd()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(20) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            ZoomLevel = 2.0,
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
            IsManual = true,
        };
        model.ZoomKeyframes.Add(kf);

        var originalEnd = kf.End;
        var op = new ResizeZoomSegmentOperation(kf.Id, false, TimeSpan.FromSeconds(10));
        op.Execute(model);

        var newEnd = model.ZoomKeyframes[0].End;
        Assert.IsTrue(newEnd > originalEnd, "End should have moved later");
    }

    [TestMethod]
    public void ResizeZoomSegment_Undo_RestoresOriginal()
    {
        var model = new TimelineModel { Duration = TimeSpan.FromSeconds(20) };
        var kf = new ZoomKeyframe
        {
            Timestamp = TimeSpan.FromSeconds(5),
            ZoomLevel = 2.0,
            PreDuration = TimeSpan.FromMilliseconds(300),
            HoldDuration = TimeSpan.FromMilliseconds(500),
            PostDuration = TimeSpan.FromMilliseconds(500),
            IsManual = true,
        };
        model.ZoomKeyframes.Add(kf);

        var originalKf = model.ZoomKeyframes[0];
        var op = new ResizeZoomSegmentOperation(kf.Id, false, TimeSpan.FromSeconds(10));
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(originalKf.HoldDuration, model.ZoomKeyframes[0].HoldDuration);
        Assert.AreEqual(originalKf.PostDuration, model.ZoomKeyframes[0].PostDuration);
    }

    #endregion
}
