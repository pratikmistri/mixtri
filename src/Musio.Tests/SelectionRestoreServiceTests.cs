using Musio.Core.Capture;
using Musio.Core.Settings;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio.Tests;

[TestClass]
public sealed class SelectionRestoreServiceTests
{
    [TestMethod]
    public void Restore_CustomRegion_WithValidStoredBounds_AppliesRegionWithoutPicker()
    {
        var vm = new RecordingViewModel();
        var region = new CaptureRegion(10, 20, 300, 200, "DISPLAY1");

        var outcome = Restore(vm, new(CaptureMode.CustomRegion, region, null, true, false, true));

        Assert.AreEqual(CaptureMode.CustomRegion, outcome.AppliedMode);
        Assert.IsFalse(outcome.AutoLaunchPicker);
        Assert.AreEqual(region, vm.SelectedRegion);
        Assert.IsTrue(vm.HasSelectedRegion);
        Assert.IsTrue(vm.IsMicEnabled);
        Assert.IsTrue(vm.IsWebcamEnabled);
    }

    [TestMethod]
    public void Restore_Window_WithMatchingStoredWindow_AppliesWindowWithoutPicker()
    {
        var vm = new RecordingViewModel();
        var selection = (ProcessName: "notepad", WindowTitle: "Notes", ClassName: "Notepad");

        var outcome = Restore(vm, new(CaptureMode.Window, null, selection, false, false, false),
            findWindow: (_, _) => new IntPtr(1234));

        Assert.AreEqual(CaptureMode.Window, outcome.AppliedMode);
        Assert.IsFalse(outcome.AutoLaunchPicker);
        Assert.IsNotNull(vm.SelectedWindow);
        Assert.AreEqual(new IntPtr(1234), vm.SelectedWindow!.Handle);
        Assert.AreEqual("Notes", vm.SelectedWindow.Title);
    }

    [TestMethod]
    public void Restore_Window_WithNoMatch_FallsBackToWindowModeAndPicker()
    {
        var vm = new RecordingViewModel();
        var selection = (ProcessName: "notepad", WindowTitle: "Missing", ClassName: "Notepad");

        var outcome = Restore(vm, new(CaptureMode.Window, null, selection, false, false, false),
            findWindow: (_, _) => null);

        Assert.AreEqual(CaptureMode.Window, outcome.AppliedMode);
        Assert.IsTrue(outcome.AutoLaunchPicker);
        Assert.IsNull(vm.SelectedWindow);
    }

    [TestMethod]
    public void Restore_FullScreen_AppliesFullScreenWithoutPicker()
    {
        var vm = new RecordingViewModel();

        var outcome = Restore(vm, new(CaptureMode.FullScreen, null, null, false, true, false));

        Assert.AreEqual(CaptureMode.FullScreen, outcome.AppliedMode);
        Assert.IsFalse(outcome.AutoLaunchPicker);
        Assert.AreEqual(CaptureMode.FullScreen, vm.CaptureMode);
        Assert.IsTrue(vm.IsSystemAudioEnabled);
    }

    [TestMethod]
    public void Restore_NoPersistedSelection_DefaultsToFullScreenWithoutPicker()
    {
        var vm = new RecordingViewModel();

        var outcome = Restore(vm, new(null, null, null, false, false, false));

        Assert.AreEqual(CaptureMode.FullScreen, outcome.AppliedMode);
        Assert.IsFalse(outcome.AutoLaunchPicker);
        Assert.AreEqual(CaptureMode.FullScreen, vm.CaptureMode);
    }

    private static SelectionRestoreOutcome Restore(
        RecordingViewModel vm,
        PersistedSelectionState state,
        Func<string, string, IntPtr?>? findWindow = null)
        => SelectionRestoreService.RestoreOnLaunch(
            vm,
            state,
            findWindow ?? ((_, _) => null),
            _ => true);
}
