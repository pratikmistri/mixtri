using Musio.Core.Processing;
using Musio.Core.Timeline;

namespace Musio.Tests;

/// <summary>
/// Tests for the independent camera (webcam) track: add / move / trim / remove /
/// update-properties operations and the active-segment lookup. Camera segments are
/// source-time ranged so they stay aligned with the recording.
/// </summary>
[TestClass]
public sealed class CameraSegmentOperationsTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    [TestMethod]
    public void Add_InsertsSegmentSortedBySourceStart()
    {
        var model = new TimelineModel();
        new AddCameraSegmentOperation(S(5), S(3)).Execute(model);
        new AddCameraSegmentOperation(S(1), S(2)).Execute(model);

        Assert.AreEqual(2, model.CameraSegments.Count);
        Assert.AreEqual(S(1), model.CameraSegments[0].Start);
        Assert.AreEqual(S(5), model.CameraSegments[1].Start);
    }

    [TestMethod]
    public void Add_Undo_RemovesSegment()
    {
        var model = new TimelineModel();
        var op = new AddCameraSegmentOperation(S(0), S(4));
        op.Execute(model);
        op.Undo(model);
        Assert.AreEqual(0, model.CameraSegments.Count);
    }

    [TestMethod]
    public void GetCameraSegmentAtSourceTime_ReturnsActiveEnabledSegment()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(2), S(3)); // covers [2,5)
        add.Execute(model);

        Assert.IsNotNull(model.GetCameraSegmentAtSourceTime(S(3)));
        Assert.IsNull(model.GetCameraSegmentAtSourceTime(S(6)));
    }

    [TestMethod]
    public void GetCameraSegmentAtSourceTime_DisabledSegment_NotReturned()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(0), S(5));
        add.Execute(model);
        new UpdateCameraSegmentPropertiesOperation(add.CreatedId, enabled: false).Execute(model);

        Assert.IsNull(model.GetCameraSegmentAtSourceTime(S(2)));
    }

    [TestMethod]
    public void Move_ChangesStartPreservingDuration()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(2), S(3));
        add.Execute(model);

        new MoveCameraSegmentOperation(add.CreatedId, S(6)).Execute(model);

        var seg = model.CameraSegments[0];
        Assert.AreEqual(S(6), seg.Start);
        Assert.AreEqual(S(3), seg.Duration);
    }

    [TestMethod]
    public void TrimRightEdge_AdjustsDuration()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        new TrimCameraSegmentOperation(add.CreatedId, fromStart: false, S(5)).Execute(model);

        Assert.AreEqual(S(2), model.CameraSegments[0].Start);
        Assert.AreEqual(S(3), model.CameraSegments[0].Duration);
    }

    [TestMethod]
    public void TrimLeftEdge_AdjustsStartAndDuration()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(2), S(5)); // [2,7)
        add.Execute(model);

        new TrimCameraSegmentOperation(add.CreatedId, fromStart: true, S(4)).Execute(model);

        Assert.AreEqual(S(4), model.CameraSegments[0].Start);
        Assert.AreEqual(S(3), model.CameraSegments[0].Duration); // end stays at 7
    }

    [TestMethod]
    public void Trim_Undo_RestoresRange()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(2), S(5));
        add.Execute(model);

        var op = new TrimCameraSegmentOperation(add.CreatedId, fromStart: false, S(4));
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(S(2), model.CameraSegments[0].Start);
        Assert.AreEqual(S(5), model.CameraSegments[0].Duration);
    }

    [TestMethod]
    public void Remove_AndUndo_Roundtrips()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(0), S(4));
        add.Execute(model);

        var remove = new RemoveCameraSegmentOperation(add.CreatedId);
        remove.Execute(model);
        Assert.AreEqual(0, model.CameraSegments.Count);

        remove.Undo(model);
        Assert.AreEqual(1, model.CameraSegments.Count);
        Assert.AreEqual(add.CreatedId, model.CameraSegments[0].Id);
    }

    [TestMethod]
    public void UpdateProperties_SetsStyleAndUndoRestores()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(0), S(4));
        add.Execute(model);

        var style = new WebcamOverlayStyle { Shape = WebcamShape.Rectangle, Size = 200f };
        var op = new UpdateCameraSegmentPropertiesOperation(add.CreatedId, styleOverride: style, setStyle: true);
        op.Execute(model);
        Assert.AreEqual(WebcamShape.Rectangle, model.CameraSegments[0].StyleOverride!.Shape);

        op.Undo(model);
        Assert.IsNull(model.CameraSegments[0].StyleOverride);
    }

    [TestMethod]
    public void ResolveStyle_PrefersOverrideThenBase()
    {
        var seg = new CameraSegment { Start = S(0), Duration = S(4) };
        var baseStyle = new WebcamOverlayStyle { Size = 300f };
        Assert.AreEqual(300f, seg.ResolveStyle(baseStyle).Size);

        seg.StyleOverride = new WebcamOverlayStyle { Size = 150f };
        Assert.AreEqual(150f, seg.ResolveStyle(baseStyle).Size);
    }

    // ── Fullscreen animation ──────────────────────────────────────────────

    [TestMethod]
    public void Fullscreen_Disabled_FactorAlwaysZero()
    {
        var seg = new CameraSegment { Start = S(0), Duration = S(10), FullscreenEnabled = false };
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(0)));
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(5)));
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(9.99)));
    }

    [TestMethod]
    public void Fullscreen_RampsInHoldsAndRampsOut()
    {
        var seg = new CameraSegment { Start = S(0), Duration = S(10), FullscreenEnabled = true };

        // Edges and outside are 0.
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(0)));
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(10)));

        // Mid-in-ramp is strictly between 0 and 1.
        float inMid = seg.ComputeFullscreenFactor(S(0.25));
        Assert.IsTrue(inMid > 0f && inMid < 1f, $"in-ramp factor was {inMid}");

        // Body holds at fullscreen.
        Assert.AreEqual(1f, seg.ComputeFullscreenFactor(S(5)));

        // Mid-out-ramp is strictly between 0 and 1.
        float outMid = seg.ComputeFullscreenFactor(S(9.75));
        Assert.IsTrue(outMid > 0f && outMid < 1f, $"out-ramp factor was {outMid}");
    }

    [TestMethod]
    public void Fullscreen_ShortSegment_RampsClampSoTheyDoNotOverlap()
    {
        // Duration 0.4s < in(0.5)+out(0.5); ramps scale to 0.2s each, no hold.
        var seg = new CameraSegment { Start = S(0), Duration = S(0.4), FullscreenEnabled = true };

        float start = seg.ComputeFullscreenFactor(S(0.1)); // inside scaled in-ramp
        Assert.IsTrue(start > 0f && start <= 1f, $"start factor was {start}");

        // The midpoint sits at the peak of the scaled ramps.
        Assert.AreEqual(1f, seg.ComputeFullscreenFactor(S(0.2)));
    }

    [TestMethod]
    public void UpdateProperties_SetsFullscreenAndUndoRestores()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(0), S(4));
        add.Execute(model);
        Assert.IsFalse(model.CameraSegments[0].FullscreenEnabled);

        var op = new UpdateCameraSegmentPropertiesOperation(add.CreatedId, fullscreenEnabled: true);
        op.Execute(model);
        Assert.IsTrue(model.CameraSegments[0].FullscreenEnabled);

        op.Undo(model);
        Assert.IsFalse(model.CameraSegments[0].FullscreenEnabled);
    }

    [TestMethod]
    public void Reveal_HoldsFullscreenThenRevealsAtEnd()
    {
        var seg = new CameraSegment
        {
            Start = S(0),
            Duration = S(10),
            FullscreenEnabled = true,
            FullscreenMode = CameraFullscreenMode.Reveal,
        };

        // Fullscreen at the start and held through the body.
        Assert.AreEqual(1f, seg.ComputeFullscreenFactor(S(0)), 0.001f);
        Assert.AreEqual(1f, seg.ComputeFullscreenFactor(S(5)), 0.001f);
        Assert.AreEqual(1f, seg.ComputeFullscreenFactor(S(9.2)), 0.001f); // start of the end reveal

        // Eases down to the overlay during the final reveal window.
        float revealing = seg.ComputeFullscreenFactor(S(9.6));
        Assert.IsTrue(revealing > 0f && revealing < 1f, $"reveal factor was {revealing}");

        // Overlay (0) by the segment end.
        Assert.AreEqual(0f, seg.ComputeFullscreenFactor(S(10)));
    }

    [TestMethod]
    public void AppearOpacity_FadesInAtStartAndOutAtEnd()
    {
        var seg = new CameraSegment { Start = S(0), Duration = S(10) };

        // Outside the segment the overlay is hidden.
        Assert.AreEqual(0f, seg.ComputeAppearOpacity(S(-1)));
        Assert.AreEqual(0f, seg.ComputeAppearOpacity(S(11)));

        // Fades in from transparent at the start.
        Assert.AreEqual(0f, seg.ComputeAppearOpacity(S(0)), 0.001f);
        float fadingIn = seg.ComputeAppearOpacity(S(0.1));
        Assert.IsTrue(fadingIn > 0f && fadingIn < 1f, $"fade-in opacity was {fadingIn}");

        // Fully opaque through the body.
        Assert.AreEqual(1f, seg.ComputeAppearOpacity(S(5)), 0.001f);

        // Fades out near the end.
        float fadingOut = seg.ComputeAppearOpacity(S(9.9));
        Assert.IsTrue(fadingOut > 0f && fadingOut < 1f, $"fade-out opacity was {fadingOut}");
    }

    [TestMethod]
    public void GetCameraOverlayOpacity_SuppressesFadeAtSharedBoundaries()
    {
        var model = new TimelineModel();
        // Two back-to-back segments: A [0,5), B [5,10).
        var a = new CameraSegment { Start = S(0), Duration = S(5) };
        var b = new CameraSegment { Start = S(5), Duration = S(5) };
        model.CameraSegments.Add(a);
        model.CameraSegments.Add(b);

        // A still fades IN at the very start (no predecessor) ...
        Assert.AreEqual(0f, model.GetCameraOverlayOpacity(a, S(0)), 0.001f);
        // ... but does NOT fade out near its end because B continues (no flash).
        Assert.AreEqual(1f, model.GetCameraOverlayOpacity(a, S(4.95)), 0.001f);

        // B does NOT fade in at its start because A precedes it ...
        Assert.AreEqual(1f, model.GetCameraOverlayOpacity(b, S(5)), 0.001f);
        // ... but fades out at the very end (no successor).
        float bEnd = model.GetCameraOverlayOpacity(b, S(9.95));
        Assert.IsTrue(bEnd < 1f, $"B end opacity was {bEnd}");
    }

    [TestMethod]
    public void GetCameraOverlayOpacity_NoFadeInWhenStartingFullscreen()
    {
        var model = new TimelineModel();
        var seg = new CameraSegment
        {
            Start = S(0),
            Duration = S(10),
            FullscreenEnabled = true,
            FullscreenMode = CameraFullscreenMode.Reveal,
        };
        model.CameraSegments.Add(seg);

        // Starts fullscreen → fully opaque immediately (no fade from black).
        Assert.AreEqual(1f, model.GetCameraOverlayOpacity(seg, S(0)), 0.001f);
        Assert.AreEqual(1f, model.GetCameraOverlayOpacity(seg, S(0.1)), 0.001f);
    }

    [TestMethod]
    public void UpdateProperties_SetsFullscreenModeAndUndoRestores()
    {
        var model = new TimelineModel();
        var add = new AddCameraSegmentOperation(S(0), S(4));
        add.Execute(model);
        Assert.AreEqual(CameraFullscreenMode.Highlight, model.CameraSegments[0].FullscreenMode);

        var op = new UpdateCameraSegmentPropertiesOperation(
            add.CreatedId, fullscreenMode: CameraFullscreenMode.Reveal);
        op.Execute(model);
        Assert.AreEqual(CameraFullscreenMode.Reveal, model.CameraSegments[0].FullscreenMode);

        op.Undo(model);
        Assert.AreEqual(CameraFullscreenMode.Highlight, model.CameraSegments[0].FullscreenMode);
    }
}
