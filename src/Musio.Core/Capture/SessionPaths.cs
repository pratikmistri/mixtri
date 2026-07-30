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

    /// <summary>
    /// Root under which each recording creates its own <c>session_*</c> working folder.
    /// </summary>
    public static string SessionsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musio", SessionsFolderName);

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
