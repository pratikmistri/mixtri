using Musio_App.Shell;
using Musio_App.ViewModels;

namespace Musio.Tests;

[TestClass]
public sealed class CliFlagParserTests
{
    [TestMethod]
    public void ParseCliFlags_BareNewRecording_OpensMiniWithoutAutoPicker()
    {
        var flags = Musio_App.App.ParseCliFlags("--new-recording");

        Assert.AreEqual(AppShellState.MiniSetup, flags.InitialState);
        Assert.IsNull(flags.NewRecordingMode);
    }

    [TestMethod]
    public void ParseCliFlags_NewRecordingRegion_OpensMiniWithRegionPickerIntent()
    {
        var flags = Musio_App.App.ParseCliFlags("--new-recording=region");

        Assert.AreEqual(AppShellState.MiniSetup, flags.InitialState);
        Assert.AreEqual(CaptureMode.CustomRegion, flags.NewRecordingMode);
    }

    [TestMethod]
    public void ParseCliFlags_NewRecordingWindow_OpensMiniWithWindowPickerIntent()
    {
        var flags = Musio_App.App.ParseCliFlags("--new-recording=window");

        Assert.AreEqual(AppShellState.MiniSetup, flags.InitialState);
        Assert.AreEqual(CaptureMode.Window, flags.NewRecordingMode);
    }

    [TestMethod]
    public void ParseCliFlags_NewRecordingFullscreen_OpensMiniWithFullscreenIntent()
    {
        var flags = Musio_App.App.ParseCliFlags("--new-recording=fullscreen");

        Assert.AreEqual(AppShellState.MiniSetup, flags.InitialState);
        Assert.AreEqual(CaptureMode.FullScreen, flags.NewRecordingMode);
    }

    [TestMethod]
    public void ParseCliFlags_Mini_OpensMini()
    {
        var flags = Musio_App.App.ParseCliFlags("--mini");

        Assert.AreEqual(AppShellState.MiniSetup, flags.InitialState);
    }

    [TestMethod]
    public void ParseCliFlags_Full_OpensFull()
    {
        var flags = Musio_App.App.ParseCliFlags("--full");

        Assert.AreEqual(AppShellState.Full, flags.InitialState);
    }

    [TestMethod]
    public void ParseCliFlags_NoFlags_HasNoStateChangeIntent()
    {
        var flags = Musio_App.App.ParseCliFlags("");

        Assert.IsNull(flags.InitialState);
        Assert.IsNull(flags.NewRecordingMode);
        Assert.IsNull(flags.FullPage);
    }

    [TestMethod]
    public void ParseCliFlags_UnknownFlag_FallsBackGracefully()
    {
        var flags = Musio_App.App.ParseCliFlags("--unknown");

        Assert.IsNull(flags.InitialState);
        Assert.IsNull(flags.NewRecordingMode);
        Assert.IsNull(flags.FullPage);
    }
}
