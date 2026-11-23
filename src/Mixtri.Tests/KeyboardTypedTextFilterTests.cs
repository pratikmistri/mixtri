using Mixtri.Core.Capture;

namespace Mixtri.Tests;

[TestClass]
public sealed class KeyboardTypedTextFilterTests
{
    [TestMethod]
    public void RecordTypedTextOn_RecordsEverything()
    {
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x41, false, false, false, true));
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x20, false, false, false, true));
    }

    [TestMethod]
    public void RecordTypedTextOff_DropsPlainCharacterKeys()
    {
        foreach (int vk in new[] { 0x41, 0x5A, 0x30, 0x39, 0x20, 0x60, 0xBA, 0xDE, 0xE2, 0xC1, 0xC2 })
            Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(vk, false, false, false, false), $"vk {vk:X}");
    }

    [TestMethod]
    public void RecordTypedTextOff_KeepsShortcuts()
    {
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x43, true, false, false, false));  // Ctrl+C
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x54, false, true, false, false));  // Alt+T
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x44, false, false, true, false));  // Win+D
    }

    [TestMethod]
    public void RecordTypedTextOff_KeepsModifiersAndNonTextKeys()
    {
        foreach (int vk in new[] { 0xA0, 0xA2, 0xA4, 0x5B, 0x70, 0x7B, 0x25, 0x24, 0x0D, 0x09, 0x1B, 0x08, 0x2E })
            Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(vk, false, false, false, false), $"vk {vk:X}");
    }

    [TestMethod]
    public void RecordTypedTextOff_DropsAltGrChordAndAltNumpadCodes()
    {
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0x51, true, true, false, false)); // AltGr+Q
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0x61, false, true, false, false)); // Alt+Numpad1
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0xC1, true, true, false, false)); // AltGr+ABNT_C1
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0xC2, true, true, false, false)); // AltGr+ABNT_C2
    }
}
