using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musio.Core.Projects;

/// <summary>
/// Shared constants and helpers for the <c>.musio</c> single-file project format.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.musio</c> file is an ordinary ZIP archive. Windows Explorer has no concept of a
/// macOS-style bundle, so presenting a project as one item genuinely requires one file;
/// ZIP gives that while staying inspectable (rename to <c>.zip</c>) and needing no custom
/// parser.
/// </para>
/// <para>
/// Layout:
/// <code>
/// manifest.json          project, composition and timeline state (deflated)
/// media/&lt;n&gt;_&lt;name&gt;   video / audio / webcam / cursor / keyboard files
/// media/&lt;n&gt;_&lt;name&gt;.mp4.finalized.marker
///                        encoder version of the video it sits beside
/// </code>
/// </para>
/// <para>
/// Compression is chosen per entry. Already-compressed media (MP4) is stored verbatim —
/// deflating it wastes minutes of CPU to save nothing. PCM audio and the cursor/keyboard
/// event logs are deflated, which is where the real reduction comes from.
/// </para>
/// </remarks>
public static class MusioPackage
{
    /// <summary>Current on-disk format version.</summary>
    /// <remarks>
    /// Version 2 packs a per-video <c>.finalized.marker</c> companion entry. Without it an
    /// extracted MP4 is indistinguishable from one written by the pre-orientation-fix
    /// encoder, which the decode path compensates for by flipping — so a version 1 package
    /// is read back with markers synthesized for the current encoder instead.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    /// <summary>File extension, including the leading dot.</summary>
    public const string FileExtension = ".musio";

    /// <summary>Entry name of the project manifest.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>Prefix for packed media entries.</summary>
    public const string MediaEntryPrefix = "media/";

    /// <summary>
    /// Entry name of the poster image: a single frame used to represent the project in
    /// the Open page, so listing projects never has to decode their video.
    /// </summary>
    public const string PosterEntryName = "poster.jpg";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Chooses a compression level for a packed media file.
    /// </summary>
    internal static CompressionLevel CompressionFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        // Container formats whose payload is already entropy-coded. Re-compressing them
        // costs significant time for a fraction of a percent.
        return extension.ToLowerInvariant() switch
        {
            ".mp4" or ".mov" or ".mkv" or ".m4a" or ".mp3" or ".jpg" or ".jpeg" or ".png" or ".gif"
                => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal,
        };
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> names a Musio project package.
    /// </summary>
    public static bool IsPackagePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && string.Equals(Path.GetExtension(path), FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a unique, filesystem-safe package entry name for a media file.
    /// </summary>
    internal static string BuildMediaEntryName(int index, string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
            name = "file";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return $"{MediaEntryPrefix}{index}_{name}";
    }

    /// <summary>
    /// Removes the single <c>&lt;index&gt;_</c> prefix that
    /// <see cref="BuildMediaEntryName"/> adds, recovering the original file name.
    /// </summary>
    /// <remarks>
    /// Exactly one group is removed, which is what makes this unambiguous: a file the user
    /// genuinely named <c>2024_recap.mp4</c> is stored as <c>0_2024_recap.mp4</c> and comes
    /// back intact. Without this, extracting and re-saving would stack prefixes on every
    /// round trip — <c>0_video.mp4</c>, <c>0_0_video.mp4</c>, and so on.
    /// <para>
    /// Only ever call this on a name produced by <see cref="BuildMediaEntryName"/>, which
    /// always carries the prefix. Applied to an arbitrary name it would strip a leading
    /// digit group the user meant to keep.
    /// </para>
    /// </remarks>
    internal static string StripEntryIndexPrefix(string entryFileName)
    {
        int i = 0;
        while (i < entryFileName.Length && char.IsAsciiDigit(entryFileName[i]))
            i++;

        return i > 0 && i < entryFileName.Length - 1 && entryFileName[i] == '_'
            ? entryFileName[(i + 1)..]
            : entryFileName;
    }
}
