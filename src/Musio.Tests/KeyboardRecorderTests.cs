using Musio.Core.Capture;

namespace Musio.Tests;

[TestClass]
public sealed class KeyboardRecorderTests
{
    #region KeyPressEvent Record

    [TestMethod]
    public void KeyPressEvent_Construction_HasCorrectValues()
    {
        var evt = new KeyPressEvent(
            TimestampTicks: 12345L,
            VirtualKeyCode: 0x41, // 'A'
            KeyName: "A",
            IsDown: true,
            IsCtrl: false,
            IsAlt: false,
            IsShift: true,
            IsWin: false);

        Assert.AreEqual(12345L, evt.TimestampTicks);
        Assert.AreEqual(0x41, evt.VirtualKeyCode);
        Assert.AreEqual("A", evt.KeyName);
        Assert.IsTrue(evt.IsDown);
        Assert.IsFalse(evt.IsCtrl);
        Assert.IsFalse(evt.IsAlt);
        Assert.IsTrue(evt.IsShift);
        Assert.IsFalse(evt.IsWin);
    }

    [TestMethod]
    public void KeyPressEvent_Equality_WorksForRecords()
    {
        var a = new KeyPressEvent(100L, 0x41, "A", true, false, false, false, false);
        var b = new KeyPressEvent(100L, 0x41, "A", true, false, false, false, false);
        var c = new KeyPressEvent(100L, 0x41, "A", false, false, false, false, false);

        Assert.AreEqual(a, b, "Same values should be equal");
        Assert.AreNotEqual(a, c, "Different IsDown should not be equal");
    }

    [TestMethod]
    public void KeyPressEvent_AllModifiers_Retained()
    {
        var evt = new KeyPressEvent(
            TimestampTicks: 999L,
            VirtualKeyCode: 0x09, // Tab
            KeyName: "Tab",
            IsDown: true,
            IsCtrl: true,
            IsAlt: true,
            IsShift: true,
            IsWin: true);

        Assert.IsTrue(evt.IsCtrl);
        Assert.IsTrue(evt.IsAlt);
        Assert.IsTrue(evt.IsShift);
        Assert.IsTrue(evt.IsWin);
    }

    [TestMethod]
    public void KeyPressEvent_KeyUp_HasIsDownFalse()
    {
        var evt = new KeyPressEvent(500L, 0x41, "A", false, false, false, false, false);
        Assert.IsFalse(evt.IsDown);
    }

    [TestMethod]
    public void PauseResume_PreservesEarlierEventsAndFiltersPausedEvents()
    {
        using var recorder = new KeyboardHookRecorder
        {
            IsRecording = true,
        };
        var beforePause = new KeyPressEvent(100, 0x41, "A", true, false, false, false, false);
        var duringPause = new KeyPressEvent(200, 0x42, "B", true, false, false, false, false);
        var afterResume = new KeyPressEvent(300, 0x43, "C", true, false, false, false, false);

        Assert.IsTrue(recorder.TryRecordEvent(beforePause));
        recorder.PauseRecording();
        Assert.IsFalse(recorder.TryRecordEvent(duringPause));
        recorder.ResumeRecording();
        Assert.IsTrue(recorder.TryRecordEvent(afterResume));

        CollectionAssert.AreEqual(
            new[] { beforePause, afterResume },
            recorder.GetRecordedEvents());

        recorder.IsRecording = false;
    }

    [TestMethod]
    public void KeyboardData_RoundTripsTextFocus()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mkbd");
        try
        {
            var focus = new TextInputFocus(500, 300, 100, 200, 900, 400);
            RecordingSession.SaveKeyboardData(
                path,
                [new KeyPressEvent(100, 0x41, "A", true, false, false, false, false, focus)]);

            var loaded = RecordingSession.LoadKeyboardData(path);

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(focus, loaded[0].TextFocus);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void KeyboardData_LoadsLegacyUnversionedFileWithoutTextFocus()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mkbd");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(1);
                writer.Write(100L);
                writer.Write(0x41);
                writer.Write("A");
                writer.Write(true);
                writer.Write(false);
                writer.Write(false);
                writer.Write(false);
                writer.Write(false);
            }

            var loaded = RecordingSession.LoadKeyboardData(path);

            Assert.AreEqual(1, loaded.Count);
            Assert.IsNull(loaded[0].TextFocus);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    #endregion
}
