using Musio_App.Services;

namespace Musio.Tests;

[TestClass]
public sealed class WindowMatcherTests
{
    [TestMethod]
    public void FindWindow_ExactProcessNameAndTitleMatch_ReturnsHandle()
    {
        var expected = new IntPtr(42);
        var windows = new[]
        {
            new WindowSnapshot(expected, "notepad", "Notes", IsVisible: true, IsCloaked: false),
        };

        var actual = WindowMatcher.FindWindow(windows, "notepad", "Notes");

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void FindWindow_Mismatch_ReturnsNull()
    {
        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(42), "notepad", "Notes", IsVisible: true, IsCloaked: false),
        };

        var actual = WindowMatcher.FindWindow(windows, "notepad", "Other");

        Assert.IsNull(actual);
    }

    [TestMethod]
    public void FindWindow_CloakedWindow_IsFilteredOut()
    {
        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(42), "notepad", "Notes", IsVisible: true, IsCloaked: true),
        };

        var actual = WindowMatcher.FindWindow(windows, "notepad", "Notes");

        Assert.IsNull(actual);
    }

    [TestMethod]
    public void FindWindow_SameProcessDifferentTitles_RequiresExactTitle()
    {
        var expected = new IntPtr(2);
        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(1), "notepad", "Draft", IsVisible: true, IsCloaked: false),
            new WindowSnapshot(expected, "notepad", "Final", IsVisible: true, IsCloaked: false),
        };

        var actual = WindowMatcher.FindWindow(windows, "notepad.exe", "Final");

        Assert.AreEqual(expected, actual);
    }
}
