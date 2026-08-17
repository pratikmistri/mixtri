using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;

namespace Musio.Tests;

[TestClass]
public sealed class TypingZoomPlannerTests
{
    private const double Frequency = 1000;

    [TestMethod]
    public void Build_CreatesFixedCaretFocusedZoomWithReducedDrift()
    {
        var events = new[]
        {
            Key(1.0, new TextInputFocus(600, 400, 400, 300, 1000, 500)),
            Key(1.2, new TextInputFocus(700, 400, 400, 300, 1000, 500)),
            Key(1.4, new TextInputFocus(800, 400, 400, 300, 1000, 500)),
        };
        var ranges = new[]
        {
            new TypingActivityRange(TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.7)),
        };

        var keyframes = TypingZoomPlanner.Build(
            events,
            ranges,
            recordingStartTicks: 0,
            tickFrequency: Frequency,
            recordingToVideoOffsetSeconds: 0,
            sourceWidth: 1920,
            sourceHeight: 1080,
            cropOffsetX: 0,
            cropOffsetY: 0,
            sourceVideoFilePath: null);

        Assert.AreEqual(1, keyframes.Count);
        Assert.IsTrue(keyframes[0].UsesAuthoredCenter);
        Assert.AreEqual(TypingZoomPlanner.TypingDriftScale, keyframes[0].DriftScale, 0.001);
        Assert.IsTrue(keyframes[0].ZoomLevel > 1.0);
        Assert.AreEqual(ranges[0].Start, keyframes[0].Start);
        Assert.AreEqual(ranges[0].End, keyframes[0].End);
    }

    [TestMethod]
    public void Build_MapsPhysicalCaretThroughCaptureOffset()
    {
        var focus = new TextInputFocus(1500, 700, 1400, 600, 1600, 800);

        var keyframes = TypingZoomPlanner.Build(
            [Key(1.0, focus), Key(1.1, focus), Key(1.2, focus)],
            [new TypingActivityRange(TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.5))],
            recordingStartTicks: 0,
            tickFrequency: Frequency,
            recordingToVideoOffsetSeconds: 0,
            sourceWidth: 1000,
            sourceHeight: 600,
            cropOffsetX: 1000,
            cropOffsetY: 400,
            sourceVideoFilePath: "append.mp4");

        Assert.AreEqual(1, keyframes.Count);
        Assert.AreEqual("append.mp4", keyframes[0].SourceVideoFilePath);
        Assert.AreEqual(0.5, keyframes[0].CenterX, 0.08);
        Assert.AreEqual(0.5, keyframes[0].CenterY, 0.08);
    }

    [TestMethod]
    public void Build_SkipsCustomControlWithoutNativeCaret()
    {
        var events = new[]
        {
            new KeyPressEvent(1000, 0x41, "A", true, false, false, false, false),
            new KeyPressEvent(1100, 0x42, "B", true, false, false, false, false),
            new KeyPressEvent(1200, 0x43, "C", true, false, false, false, false),
        };

        var keyframes = TypingZoomPlanner.Build(
            events,
            [new TypingActivityRange(TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.5))],
            0,
            Frequency,
            0,
            1920,
            1080,
            0,
            0,
            null);

        Assert.AreEqual(0, keyframes.Count);
    }

    [TestMethod]
    public void Build_CustomControlUsesBroadRecentClickFallback()
    {
        var events = new[]
        {
            new KeyPressEvent(1000, 0x41, "A", true, false, false, false, false),
            new KeyPressEvent(1100, 0x42, "B", true, false, false, false, false),
            new KeyPressEvent(1200, 0x43, "C", true, false, false, false, false),
        };
        var mouse = new MouseRecordingData
        {
            StartTimestampTicks = 0,
            TickFrequency = Frequency,
        };
        mouse.Clicks.Add(new ClickEvent(
            TimestampTicks: 700,
            X: 500,
            Y: 400,
            Button: MouseButton.Left,
            IsDown: true));

        var keyframes = TypingZoomPlanner.Build(
            events,
            [new TypingActivityRange(TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.5))],
            0,
            Frequency,
            0,
            1920,
            1080,
            0,
            0,
            null,
            mouse);

        Assert.AreEqual(1, keyframes.Count);
        Assert.IsTrue(keyframes[0].ZoomLevel <= TypingZoomPlanner.PointerFallbackMaximumZoom);
        Assert.AreEqual(
            TypingZoomPlanner.PointerFallbackDriftScale,
            keyframes[0].DriftScale,
            0.001);
        Assert.IsTrue(keyframes[0].UsesAuthoredCenter);
        Assert.IsTrue(keyframes[0].CenterX > 500.0 / 1920.0,
            "Fallback framing should leave extra room to the right for typed text.");
    }

    [TestMethod]
    public void ResolveViewport_ContainsFocusWithSafetyMargin()
    {
        var focus = new TypingZoomPlanner.FocusBounds(300, 250, 900, 450);

        var result = TypingZoomPlanner.ResolveViewport(focus, 1920, 1080);

        double viewportWidth = 1920 / result.Zoom;
        double viewportHeight = 1080 / result.Zoom;
        double left = result.CenterX - viewportWidth / 2;
        double right = result.CenterX + viewportWidth / 2;
        double top = result.CenterY - viewportHeight / 2;
        double bottom = result.CenterY + viewportHeight / 2;
        Assert.IsTrue(focus.Left >= left + viewportWidth * TypingZoomPlanner.ViewportSafetyFraction - 0.001);
        Assert.IsTrue(focus.Right <= right - viewportWidth * TypingZoomPlanner.ViewportSafetyFraction + 0.001);
        Assert.IsTrue(focus.Top >= top + viewportHeight * TypingZoomPlanner.ViewportSafetyFraction - 0.001);
        Assert.IsTrue(focus.Bottom <= bottom - viewportHeight * TypingZoomPlanner.ViewportSafetyFraction + 0.001);
    }

    private static KeyPressEvent Key(double seconds, TextInputFocus focus) => new(
        TimestampTicks: (long)(seconds * Frequency),
        VirtualKeyCode: 0x41,
        KeyName: "A",
        IsDown: true,
        IsCtrl: false,
        IsAlt: false,
        IsShift: false,
        IsWin: false,
        TextFocus: focus);
}
