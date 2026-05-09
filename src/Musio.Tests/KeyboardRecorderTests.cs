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

    #endregion
}
