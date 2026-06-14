using Musio_App.Services;

namespace Musio.Tests;

[TestClass]
public sealed class SystemTrayServiceTests
{
    [TestMethod]
    public void BuildMenuItems_NotRecording_HasNewRecordingEntriesEnabled()
    {
        var items = SystemTrayService.BuildMenuItems(isRecording: false);

        CollectionAssert.AreEqual(new[]
        {
            "New recording",
            "New region recording",
            "New window recording",
            "New full-screen recording",
            "Open Musio",
            "Settings",
            "Quit Musio",
        }, items.Where(i => !i.IsSeparator).Select(i => i.Text).ToArray());
        Assert.IsTrue(items.First(i => i.Text == "New recording").IsDefault);
        Assert.IsTrue(items.Where(i => !i.IsSeparator).All(i => i.Enabled));
    }

    [TestMethod]
    public void BuildMenuItems_Recording_HasStopAndShowPillAndDisablesNewEntries()
    {
        var items = SystemTrayService.BuildMenuItems(isRecording: true);

        Assert.AreEqual("Stop recording", items[0].Text);
        Assert.AreEqual("Show recording pill", items[1].Text);
        Assert.IsFalse(items.First(i => i.Text == "New recording").Enabled);
        Assert.IsFalse(items.First(i => i.Text == "New region recording").Enabled);
        Assert.IsTrue(items.First(i => i.Text == "Quit Musio").Enabled);
    }

    [TestMethod]
    public void BuildMenuItems_RebuildsStableSpecsWithoutDuplicateCommandIds()
    {
        var first = SystemTrayService.BuildMenuItems(isRecording: false);
        var second = SystemTrayService.BuildMenuItems(isRecording: false);
        var firstIds = first.Where(i => !i.IsSeparator).Select(i => i.Id).ToArray();

        CollectionAssert.AreEqual(first.Select(i => i.Text).ToArray(), second.Select(i => i.Text).ToArray());
        Assert.AreEqual(firstIds.Length, firstIds.Distinct().Count());
    }
}
