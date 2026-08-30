namespace Mixtri.Core.Shell;

/// <summary>
/// Decides whether a one-time "what's new" notice should be shown at startup.
/// </summary>
/// <remarks>
/// <para>
/// The decision is a pure function so it can be tested without a window. Every input is
/// supplied by the caller rather than read here, because the interesting cases — a brand-new
/// user, a document instance — are exactly the ones that are awkward to stage against real
/// settings and a real LocalAppData folder.
/// </para>
/// <para>
/// Each notice owns its own "seen" key. Reusing one key across notices would mean whichever
/// shipped first permanently suppressed every later one.
/// </para>
/// </remarks>
public static class WhatsNewNotice
{
    /// <summary>Settings key for the one-time Musio → Mixtri rename notice.</summary>
    public const string RebrandSeenSettingKey = "HasSeenRebrandNotice";

    /// <summary>
    /// Whether to show a notice on this launch.
    /// </summary>
    /// <param name="hasSeenNotice">The user has already dismissed this particular notice.</param>
    /// <param name="isRelevant">
    /// This notice applies to this user at all. For the rename that means they actually used
    /// Musio: a fresh install must never be told that an app it has never heard of has been
    /// renamed. A notice that applies to everyone passes <c>true</c>.
    /// </param>
    /// <param name="isDocumentInstance">
    /// This process was launched to open a project file. Those spawn one process per file, so
    /// showing a notice here would pop it up alongside a document — repeatedly, whenever
    /// several are opened — instead of once at the app's own startup.
    /// </param>
    public static bool ShouldShow(bool hasSeenNotice, bool isRelevant, bool isDocumentInstance)
        => !hasSeenNotice
           && isRelevant
           && !isDocumentInstance;
}
