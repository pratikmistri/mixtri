namespace Mixtri.Core;

/// <summary>
/// The single source of truth for where Mixtri keeps per-user data under LocalAppData.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, six call sites each built their own
/// <c>Path.Combine(LocalApplicationData, "Musio", …)</c>. The Musio → Mixtri rename rewrote five
/// of them and deliberately held one back, which silently split the app's data across two
/// folders and stranded the recent-projects index. Asking "where is our data?" in one place is
/// what makes that whole class of bug unrepresentable.
/// </para>
/// <para>
/// <see cref="LegacyRoot"/> is the pre-rename folder. It is NEVER written to. Read paths that
/// must carry user data across the rename either consult it via
/// <see cref="ResolveExistingOrNew"/> or merge both roots — see
/// <c>RecentProjectsStore</c>, which merges so a user keeps one list rather than whichever half
/// happened to be found first.
/// </para>
/// <para>
/// This applies ONLY to data stored directly under LocalAppData. Anything under
/// <c>ApplicationData.Current.LocalFolder</c> (settings, and presets in a packaged build) is
/// package-scoped and was untouched by the rename, because the package identity deliberately
/// did not change.
/// </para>
/// </remarks>
public static class AppDataPaths
{
    /// <summary>Folder name used for all new data.</summary>
    public const string FolderName = "Mixtri";

    /// <summary>Pre-rename folder name. Read-only; never written to.</summary>
    public const string LegacyFolderName = "Musio";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string MyVideos =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>Root for all new data.</summary>
    public static string Root => Path.Combine(LocalAppData, FolderName);

    /// <summary>Pre-rename root, retained so upgrading installs keep their existing data.</summary>
    public static string LegacyRoot => Path.Combine(LocalAppData, LegacyFolderName);

    /// <summary>
    /// Default folder under the user's Videos library for saved projects and exports. Unlike
    /// <see cref="Root"/> this is user-facing: the user can see, move and save into it.
    /// </summary>
    public static string VideosFolder => Path.Combine(MyVideos, FolderName);

    /// <summary>
    /// Pre-rename Videos folder. Still scanned when listing projects — a user who saved into
    /// <c>Videos\Musio</c> must not find those projects missing after the rename — but never
    /// written to.
    /// </summary>
    public static string LegacyVideosFolder => Path.Combine(MyVideos, LegacyFolderName);

    /// <summary>Both Videos folders, current first. For scans that must cover either name.</summary>
    public static IReadOnlyList<string> AllVideosFolders => [VideosFolder, LegacyVideosFolder];

    /// <summary>
    /// Both roots, current first. For sweeps and scans that must cover data written on either
    /// side of the rename.
    /// </summary>
    public static IReadOnlyList<string> AllRoots => [Root, LegacyRoot];

    /// <summary>Builds a path under <see cref="Root"/>. This is where writes go.</summary>
    public static string Resolve(params string[] segments) => Combine(Root, segments);

    /// <summary>Builds a path under <see cref="LegacyRoot"/>.</summary>
    public static string ResolveLegacy(params string[] segments) => Combine(LegacyRoot, segments);

    /// <summary>
    /// The current-root path — unless nothing exists there and the pre-rename path does, in
    /// which case the legacy one, so an upgrading install keeps reading data it already has.
    /// </summary>
    /// <remarks>
    /// Only ever use this to READ. Writing through it would keep growing the legacy folder and
    /// the install would never migrate; write via <see cref="Resolve"/>.
    /// </remarks>
    public static string ResolveExistingOrNew(params string[] segments)
    {
        var current = Resolve(segments);
        if (File.Exists(current) || Directory.Exists(current))
            return current;

        var legacy = ResolveLegacy(segments);
        return File.Exists(legacy) || Directory.Exists(legacy) ? legacy : current;
    }

    private static string Combine(string root, string[] segments)
        => segments is { Length: > 0 } ? Path.Combine([root, .. segments]) : root;
}
