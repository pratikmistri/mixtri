using Musio.Core.Settings;
using Musio.Core.Shell;

namespace Musio.Tests;

[TestClass]
public class ShellSettingsTests
{
    [TestMethod]
    public void FirstLaunch_DefaultsToMini()
    {
        Assert.AreEqual(StartupMode.Mini, ShellSettings.ResolveStartupMode(null, hasBeenSet: false));
    }

    [TestMethod]
    public void FirstLaunch_IgnoresStrayPersistedValue()
    {
        // Nothing was ever explicitly chosen, so a leftover value must not win.
        Assert.AreEqual(StartupMode.Mini, ShellSettings.ResolveStartupMode("Full", hasBeenSet: false));
    }

    [TestMethod]
    public void ExplicitFull_IsHonoured()
    {
        Assert.AreEqual(StartupMode.Full, ShellSettings.ResolveStartupMode("Full", hasBeenSet: true));
    }

    [TestMethod]
    public void ExplicitMini_IsHonoured()
    {
        Assert.AreEqual(StartupMode.Mini, ShellSettings.ResolveStartupMode("Mini", hasBeenSet: true));
    }

    [TestMethod]
    public void ParsingIsCaseInsensitive()
    {
        Assert.AreEqual(StartupMode.Full, ShellSettings.ResolveStartupMode("full", hasBeenSet: true));
    }

    [TestMethod]
    public void CorruptValue_FallsBackToMini()
    {
        Assert.AreEqual(StartupMode.Mini, ShellSettings.ResolveStartupMode("Gigantic", hasBeenSet: true));
        Assert.AreEqual(StartupMode.Mini, ShellSettings.ResolveStartupMode("", hasBeenSet: true));
    }

    [TestMethod]
    public void StartupMode_RoundTripsThroughStore()
    {
        ShellSettings.Instance.StartupMode = StartupMode.Full;
        Assert.IsTrue(ShellSettings.Instance.HasChosenStartupMode);
        Assert.AreEqual(StartupMode.Full, ShellSettings.Instance.StartupMode);

        ShellSettings.Instance.StartupMode = StartupMode.Mini;
        Assert.AreEqual(StartupMode.Mini, ShellSettings.Instance.StartupMode);
    }

    [TestMethod]
    public void LastCaptureMode_RoundTripsAndNormalisesEmpty()
    {
        ShellSettings.Instance.LastCaptureMode = "CustomRegion";
        Assert.AreEqual("CustomRegion", ShellSettings.Instance.LastCaptureMode);

        ShellSettings.Instance.LastCaptureMode = null;
        Assert.IsNull(ShellSettings.Instance.LastCaptureMode);
    }
}
