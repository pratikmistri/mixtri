namespace Musio.Core.Capture;

/// <summary>
/// Filesystem locations used by recording.
/// </summary>
/// <remarks>
/// A recording session is working state, not a deliverable. It is a folder of raw media
/// that only the app understands, so it lives under LocalAppData rather than in the
/// user's Videos folder — which should only ever receive things the user asked for: a
/// saved <c>.musio</c> project or an exported video.
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
    public static string SessionsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musio", SessionsFolderName);

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
