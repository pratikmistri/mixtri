using Mixtri.Core.Capture;
using Mixtri.Core.Processing;

namespace Mixtri.Tests;

[TestClass]
public sealed class TypingActivityDetectorTests
{
    private const double Frequency = 1000;

    [TestMethod]
    public void Detect_GroupsTextKeysAndAddsPadding()
    {
        var ranges = Detect(
            Key(1.0, 0x41),
            Key(1.2, 0x42),
            Key(1.4, 0x43));

        Assert.AreEqual(1, ranges.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(0.8), ranges[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1.7), ranges[0].End);
    }

    [TestMethod]
    public void Detect_ExcludesShortcutsAndKeyUpEvents()
    {
        var ranges = Detect(
            Key(1.0, 0x43, ctrl: true),
            Key(1.1, 0x41, isDown: false),
            Key(1.2, 0x42),
            Key(1.3, 0x43));

        Assert.AreEqual(0, ranges.Count);
    }

    [TestMethod]
    public void Detect_SplitsBurstsAcrossLongPause()
    {
        var ranges = Detect(
            Key(1.0, 0x41),
            Key(1.1, 0x42),
            Key(1.2, 0x43),
            Key(4.0, 0x44),
            Key(4.1, 0x45),
            Key(4.2, 0x46));

        Assert.AreEqual(2, ranges.Count);
    }

    [TestMethod]
    public void Detect_MapsRecorderTicksIntoVideoTime()
    {
        var ranges = TypingActivityDetector.Detect(
            [
                Key(2.0, 0x41),
                Key(2.2, 0x42),
                Key(2.4, 0x43),
            ],
            recordingStartTicks: 1000,
            tickFrequency: Frequency,
            recordingToVideoOffsetSeconds: 0.5,
            videoDuration: TimeSpan.FromSeconds(10));

        Assert.AreEqual(TimeSpan.FromSeconds(0.3), ranges[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1.2), ranges[0].End);
    }

    [TestMethod]
    public void Detect_ClampsPaddingToVideoBounds()
    {
        var ranges = Detect(
            Key(0.05, 0x41),
            Key(0.10, 0x42),
            Key(0.15, 0x43),
            durationSeconds: 0.4);

        Assert.AreEqual(1, ranges.Count);
        Assert.AreEqual(TimeSpan.Zero, ranges[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(0.4), ranges[0].End);
    }

    private static IReadOnlyList<TypingActivityRange> Detect(
        KeyPressEvent a,
        KeyPressEvent b,
        KeyPressEvent c,
        KeyPressEvent? d = null,
        KeyPressEvent? e = null,
        KeyPressEvent? f = null,
        double durationSeconds = 10)
    {
        var events = new[] { a, b, c, d, e, f }.OfType<KeyPressEvent>().ToList();
        return TypingActivityDetector.Detect(
            events,
            recordingStartTicks: 0,
            tickFrequency: Frequency,
            recordingToVideoOffsetSeconds: 0,
            videoDuration: TimeSpan.FromSeconds(durationSeconds));
    }

    private static KeyPressEvent Key(
        double seconds,
        int virtualKey,
        bool ctrl = false,
        bool isDown = true) =>
        new(
            TimestampTicks: (long)(seconds * Frequency),
            VirtualKeyCode: virtualKey,
            KeyName: "Key",
            IsDown: isDown,
            IsCtrl: ctrl,
            IsAlt: false,
            IsShift: false,
            IsWin: false);
}
