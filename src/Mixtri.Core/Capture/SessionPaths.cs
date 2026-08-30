namespace Mixtri.Core.Capture;

/// <summary>
/// Filesystem locations used by recording.
/// </summary>
/// <remarks>
/// A recording session is working state, not a deliverable. It is a folder of raw media
/// that only the app understands, so it lives under LocalAppData rather than in the
/// user's Videos folder — which should only ever receive things the user asked for: a
/// saved <c>.mixtri</c> project or an exported video.
/// </remarks>
public static class SessionPaths
{
    /// <summary>Folder name holding all recording session working folders.</summary>
    public const string SessionsFolderName = "Sessions";

    /// <summary>Prefix of the per-import working folder created under <see cref="SessionsRoot"/>.</summary>
    public const string ImportFolderPrefix = "import_";

    /// <summary>
    /// Root under which each recording creates its own <c>session_*</c> working folder.
    /// </summary>
    /// <remarks>
    /// New sessions are written under the current <see cref="AppDataPaths.Root"/>. Sessions
    /// recorded before the Musio → Mixtri rename are NOT moved and do not need to be: a project
    /// stores absolute paths to its media, so they keep resolving wherever they sit. What must
    /// span both roots is the cleanup sweep — see <see cref="AllSessionsRoots"/>, without which
    /// pre-rename sessions would never be reclaimed and would leak disk forever.
    /// </remarks>
    public static string SessionsRoot => AppDataPaths.Resolve(SessionsFolderName);

    /// <summary>Pre-rename sessions root. Swept for cleanup; never written to.</summary>
    public static string LegacySessionsRoot => AppDataPaths.ResolveLegacy(SessionsFolderName);

    /// <summary>
    /// Every root that may hold session or import working folders, current first. Cleanup
    /// sweeps must cover all of these.
    /// </summary>
    public static IReadOnlyList<string> AllSessionsRoots => [SessionsRoot, LegacySessionsRoot];

    /// <summary>
    /// Root under which each external-video import creates its own <c>import_*</c> working
    /// folder.
    /// </summary>
    /// <remarks>
    /// Imports share <see cref="SessionsRoot"/> deliberately: an imported video is normalised
    /// into the very same <c>video.mp4</c> + <c>finalized.marker</c> layout a recording
    /// produces, so the decode, orientation and cleanup code already understands it without a
    /// second concept of "working media". The <see cref="ImportFolderPrefix"/> (rather than
    /// <c>session_</c>) keeps imports outside <c>SessionCleanupService</c>'s <c>session_*</c>
    /// sweep — an import has no captured JPEGs to reclaim and no recording lifecycle to end.
    /// </remarks>
    public static string ImportsRoot => SessionsRoot;

    /// <summary>
    /// Every root that may hold import working folders, current first. Mirrors
    /// <see cref="AllSessionsRoots"/> for the same reason <see cref="ImportsRoot"/> mirrors
    /// <see cref="SessionsRoot"/>, and is kept as its own member so a caller sweeping imports
    /// reads as sweeping imports.
    /// </summary>
    public static IReadOnlyList<string> AllImportsRoots => AllSessionsRoots;

    /// <summary>
    /// Creates and returns a fresh, empty <c>import_&lt;guid&gt;</c> folder under
    /// <see cref="ImportsRoot"/> for a single video import.
    /// </summary>
    public static string CreateImportFolder(string? root = null)
    {
        var baseRoot = string.IsNullOrWhiteSpace(root) ? ImportsRoot : root!;
        Directory.CreateDirectory(baseRoot);
        var folder = Path.Combine(baseRoot, ImportFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Ensures <see cref="SessionsRoot"/> exists and returns it, falling back to the
    /// supplied path if it cannot be created.
    /// </summary>
    public static string EnsureSessionsRoot(string fallback)
    {
        try
        {
            var root = SessionsRoot;
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SessionPaths] Could not create sessions root: {ex.Message}");
            return fallback;
        }
    }
}
