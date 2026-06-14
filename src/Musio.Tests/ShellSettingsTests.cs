using System.Reflection;
using Musio.Core.Settings;
using Musio_App.Services;
using Musio_App.Shell;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.Storage;

namespace Musio.Tests;

[TestClass]
public sealed class ShellSettingsTests
{
    private static readonly string[] Keys =
    [
        "Shell.StartupMode",
        "Shell.StartupMode.HasBeenSet",
        "Recording.LastCaptureMode",
        "Recording.LastRegion",
        "Recording.LastWindowSelection",
        "Recording.LastMicEnabled",
        "Recording.LastSystemAudioEnabled",
        "Recording.LastWebcamEnabled",
    ];

    [TestInitialize]
    public void ResetSettings() => ClearShellKeys();

    [TestCleanup]
    public void CleanupSettings() => ClearShellKeys();

    [TestMethod]
    public void RoundTrip_AllMiniModeProperties()
    {
        var settings = ShellSettings.Instance;
        var region = new Rect(1, 2, 300, 400);
        var window = (ProcessName: "proc", WindowTitle: "title", ClassName: "class");

        settings.StartupMode = StartupMode.Full;
        settings.LastCaptureMode = CaptureMode.Window;
        settings.LastRegion = region;
        settings.LastWindowSelection = window;
        settings.LastMicEnabled = true;
        settings.LastSystemAudioEnabled = true;
        settings.LastWebcamEnabled = true;

        Assert.AreEqual(StartupMode.Full, settings.StartupMode);
        Assert.AreEqual(CaptureMode.Window, settings.LastCaptureMode);
        Assert.AreEqual(region, settings.LastRegion);
        Assert.AreEqual(window, settings.LastWindowSelection);
        Assert.IsTrue(settings.LastMicEnabled);
        Assert.IsTrue(settings.LastSystemAudioEnabled);
        Assert.IsTrue(settings.LastWebcamEnabled);
    }

    [TestMethod]
    public void StartupModeWrite_SetsHasBeenSetSentinel()
    {
        Assert.IsFalse(ShellSettings.Instance.StartupModeHasBeenSet);

        ShellSettings.Instance.StartupMode = StartupMode.Mini;

        Assert.IsTrue(ShellSettings.Instance.StartupModeHasBeenSet);
    }

    [TestMethod]
    public void Defaults_ForUnsetKeys_AreExpected()
    {
        var settings = ShellSettings.Instance;

        Assert.AreEqual(StartupMode.Mini, settings.StartupMode);
        Assert.IsFalse(settings.StartupModeHasBeenSet);
        Assert.IsNull(settings.LastCaptureMode);
        Assert.IsNull(settings.LastRegion);
        Assert.IsNull(settings.LastWindowSelection);
        Assert.IsFalse(settings.LastMicEnabled);
        Assert.IsFalse(settings.LastSystemAudioEnabled);
        Assert.IsFalse(settings.LastWebcamEnabled);
    }

    private static void ClearShellKeys()
    {
        var appSettings = AppSettings.Instance;
        var settingsField = typeof(AppSettings).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var container = (ApplicationDataContainer?)settingsField?.GetValue(appSettings);

        var memoryField = typeof(AppSettings).GetField("_memoryStore", BindingFlags.Instance | BindingFlags.NonPublic);
        var memory = (Dictionary<string, object>?)memoryField?.GetValue(appSettings);

        foreach (var key in Keys)
        {
            container?.Values.Remove(key);
            lock (appSettings)
            {
                memory?.Remove(key);
            }
        }
    }
}
