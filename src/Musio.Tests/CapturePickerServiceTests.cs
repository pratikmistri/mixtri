using Musio.Core.Capture;
using Musio.Core.Settings;
using Musio_App.Controls;
using Musio_App.Services;
using Musio_App.ViewModels;

namespace Musio.Tests;

[TestClass]
public sealed class CapturePickerServiceTests
{
    [TestMethod]
    public async Task PickRegionAsync_WhenRegionSelected_ReturnsSelectedAndUpdatesViewModel()
    {
        var region = new CaptureRegion(1, 2, 3, 4, "DISPLAY1");
        var vm = new RecordingViewModel();
        var service = new CapturePickerService(_ => Task.FromResult<CaptureRegion?>(region), () => Task.FromResult<WindowInfo?>(null), vm);

        var result = await service.PickRegionAsync(owner: null);

        Assert.AreEqual(PickerResult.Selected, result);
        Assert.AreEqual(region, vm.SelectedRegion);
        Assert.IsTrue(vm.HasSelectedRegion);
    }

    [TestMethod]
    public async Task PickRegionAsync_WhenCancelled_ReturnsCancelledAndPreservesPriorSelection()
    {
        var prior = new CaptureRegion(10, 20, 30, 40, "DISPLAY1");
        var vm = new RecordingViewModel { SelectedRegion = prior, HasSelectedRegion = true };
        var service = new CapturePickerService(_ => Task.FromResult<CaptureRegion?>(null), () => Task.FromResult<WindowInfo?>(null), vm);

        var result = await service.PickRegionAsync(owner: null);

        Assert.AreEqual(PickerResult.Cancelled, result);
        Assert.AreEqual(prior, vm.SelectedRegion);
        Assert.IsTrue(vm.HasSelectedRegion);
    }

    [TestMethod]
    public async Task PickRegionAsync_WhilePickerOpen_ReturnsAlreadyOpenWithoutSecondLaunch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CaptureRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launches = 0;
        var service = new CapturePickerService(_ =>
        {
            launches++;
            entered.SetResult();
            return release.Task;
        }, () => Task.FromResult<WindowInfo?>(null), new RecordingViewModel());

        var first = service.PickRegionAsync(owner: null);
        await entered.Task;

        var second = await service.PickRegionAsync(owner: null);
        release.SetResult(null);
        await first;

        Assert.AreEqual(PickerResult.AlreadyOpen, second);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task PickRegionAsync_FiresPickerEventsWithRegionKind()
    {
        var service = new CapturePickerService(_ => Task.FromResult<CaptureRegion?>(null), () => Task.FromResult<WindowInfo?>(null), new RecordingViewModel());

        PickerKind? openingKind = null;
        var closedFired = false;
        service.PickerOpening += (_, args) => openingKind = args.Kind;
        service.PickerClosed += (_, _) => closedFired = true;

        await service.PickRegionAsync(owner: null);

        Assert.AreEqual(PickerKind.Region, openingKind);
        Assert.IsTrue(closedFired);
    }

    [TestMethod]
    public async Task PickWindowAsync_FiresPickerEventsWithWindowKind()
    {
        var service = new CapturePickerService(_ => Task.FromResult<CaptureRegion?>(null), () => Task.FromResult<WindowInfo?>(null), new RecordingViewModel());

        PickerKind? openingKind = null;
        var closedFired = false;
        service.PickerOpening += (_, args) => openingKind = args.Kind;
        service.PickerClosed += (_, _) => closedFired = true;

        await service.PickWindowAsync(owner: null);

        Assert.AreEqual(PickerKind.Window, openingKind);
        Assert.IsTrue(closedFired);
    }
}
