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

    [TestMethod]
    public void AltHexSequence_IsSuppressedUntilAltIsReleased()
    {
        var f = new TypedTextFilter();
        Assert.IsTrue(f.ShouldRecord(0xA4, true, false, true, false, false), "Alt press");
        Assert.IsFalse(f.ShouldRecord(0x6B, true, false, true, false, false), "Num+ starts hex input");
        Assert.IsFalse(f.ShouldRecord(0x6B, false, false, true, false, false));
        foreach (int vk in new[] { 0x30, 0x33, 0x41, 0x39 }) // "03A9" on the top row
        {
            Assert.IsFalse(f.ShouldRecord(vk, true, false, true, false, false), $"hex digit {vk:X} press");
            Assert.IsFalse(f.ShouldRecord(vk, false, false, true, false, false), $"hex digit {vk:X} release");
        }
        Assert.IsTrue(f.ShouldRecord(0xA4, false, false, false, false, false), "Alt release (its press was recorded)");

        // After Alt is released, shortcuts are recorded again.
        Assert.IsTrue(f.ShouldRecord(0x43, true, true, false, false, false), "Ctrl+C");
    }

    [TestMethod]
    public void PauseResumeReset_DoesNotLeakAPlainKeyRelease()
    {
        var f = new TypedTextFilter();
        Assert.IsTrue(f.ShouldRecord(0x43, true, true, false, false, false), "Ctrl+C press recorded");
        // Paused: the C release is never seen. Resume resets the filter.
        f.Reset();
        Assert.IsFalse(f.ShouldRecord(0x43, true, false, false, false, false), "plain c press dropped");
        Assert.IsFalse(f.ShouldRecord(0x43, false, false, false, false, false), "plain c release dropped");
    }

    [TestMethod]
    public void AltNavigation_OnDedicatedClusterIsKept_KeypadCodesAreDropped()
    {
        // Alt+Left / Alt+Home on the dedicated cluster (extended flag) are shortcuts.
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x25, false, true, false, false, isExtended: true));
        Assert.IsTrue(KeyboardHookRecorder.ShouldRecordKey(0x24, false, true, false, false, isExtended: true));
        // The same virtual keys from the keypad (NumLock off, not extended) enter Alt codes.
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0x25, false, true, false, false, isExtended: false));
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0x64, false, true, false, false, isExtended: false));
    }

    [TestMethod]
    public void AltHexAcrossPauseResume_StaysSuppressedUntilAltRelease()
    {
        var f = new TypedTextFilter();
        Assert.IsTrue(f.ShouldRecord(0xA4, true, false, true, false, false), "Alt press");
        Assert.IsFalse(f.ShouldRecord(0x6B, true, false, true, false, false), "Num+ starts hex input");
        // Pause, then resume with Alt still held.
        f.Reset(altHeld: true);
        Assert.IsFalse(f.ShouldRecord(0x41, true, false, true, false, false), "hex digit A after resume");
        Assert.IsFalse(f.ShouldRecord(0x39, true, false, true, false, false), "hex digit 9 after resume");
        Assert.IsFalse(f.ShouldRecord(0xA4, false, false, false, false, false), "Alt release (press predates the reset)");
        Assert.IsTrue(f.ShouldRecord(0x43, true, true, false, false, false), "Ctrl+C after Alt is released");
    }

    [TestMethod]
    public void ReleasingOneOfTwoHeldModifiers_KeepsTheModifierDown()
    {
        var m = new ModifierTracker();
        m.Update(0xA2, true);  // LCtrl
        m.Update(0xA3, true);  // RCtrl
        m.Update(0xA4, true);  // LAlt
        m.Update(0xA2, false); // release LCtrl only
        Assert.IsTrue(m.Ctrl, "RCtrl is still held");
        Assert.IsTrue(m.Alt);
        // RCtrl+Alt+Q is AltGr+Q (inserts '@' on German layouts): must be dropped.
        Assert.IsFalse(KeyboardHookRecorder.ShouldRecordKey(0x51, m.Ctrl, m.Alt, m.Win, false));
        m.Update(0xA3, false);
        Assert.IsFalse(m.Ctrl);
    }
}
