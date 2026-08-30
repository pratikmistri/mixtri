using Mixtri.Core.Shell;

namespace Mixtri.Tests;

/// <summary>
/// The startup notice has to be wrong in only one direction: showing it to someone who does
/// not need it is confusing, and the worst case — telling a brand-new user that an app they
/// have never heard of has been renamed — is the one these pin hardest.
/// </summary>
[TestClass]
public class WhatsNewNoticeTests
{
    [TestMethod]
    public void ShouldShow_ForAnUpgradingUserWhoHasNotSeenIt()
    {
        Assert.IsTrue(WhatsNewNotice.ShouldShow(
            hasSeenNotice: false, isRelevant: true, isDocumentInstance: false));
    }

    [TestMethod]
    public void ShouldNotShow_OnceDismissed()
    {
        Assert.IsFalse(WhatsNewNotice.ShouldShow(
            hasSeenNotice: true, isRelevant: true, isDocumentInstance: false));
    }

    /// <summary>
    /// The case the whole <c>isRelevant</c> input exists for: a fresh install has no Musio to
    /// be renamed from, so "Musio is now Mixtri" would be meaningless.
    /// </summary>
    [TestMethod]
    public void ShouldNotShow_ToABrandNewUser()
    {
        Assert.IsFalse(WhatsNewNotice.ShouldShow(
            hasSeenNotice: false, isRelevant: false, isDocumentInstance: false));
    }

    /// <summary>
    /// Opening a project spawns one process per file, so a notice here would appear next to a
    /// document — and once per document — rather than once at the app's own startup.
    /// </summary>
    [TestMethod]
    public void ShouldNotShow_InADocumentInstance()
    {
        Assert.IsFalse(WhatsNewNotice.ShouldShow(
            hasSeenNotice: false, isRelevant: true, isDocumentInstance: true));
    }

    [TestMethod]
    public void ShouldNotShow_WhenEverySuppressingConditionApplies()
    {
        Assert.IsFalse(WhatsNewNotice.ShouldShow(
            hasSeenNotice: true, isRelevant: false, isDocumentInstance: true));
    }

    /// <summary>
    /// Each notice must carry its own key: sharing one would let whichever shipped first
    /// permanently suppress every later notice.
    /// </summary>
    [TestMethod]
    public void RebrandKey_IsStable()
    {
        // Pinned because changing it re-shows the notice to everyone who already dismissed it.
        Assert.AreEqual("HasSeenRebrandNotice", WhatsNewNotice.RebrandSeenSettingKey);
    }
}
