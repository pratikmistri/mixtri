namespace Mixtri.Core.Timeline;

/// <summary>Stable project references to bundled backgrounds, independent of installation location.</summary>
public static class TitleSlideImages
{
    public const string Prefix = "mixtri-title:";

    public static bool IsBundled(string? path) => path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string? Resolve(string? path)
    {
        if (!IsBundled(path)) return path;
        var name = path![Prefix.Length..];
        // Only known assets may resolve; project files cannot escape the assets directory.
        if (name is not ("eclipse" or "porcelain" or "celadon" or "mist-blue" or "lilac" or "clay" or "champagne"))
            return null;
        // WinUI places referenced-library content under the library name in MSIX;
        // regular test hosts copy Content directly beneath the executable.
        var packaged = Path.Combine(AppContext.BaseDirectory, "Mixtri.Core", "Assets", "TitleBackgrounds", name + ".png");
        return File.Exists(packaged) ? packaged
            : Path.Combine(AppContext.BaseDirectory, "Assets", "TitleBackgrounds", name + ".png");
    }
}
