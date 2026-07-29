using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Timeline;

namespace Musio.Core.Projects;

/// <summary>
/// Result of opening a <c>.musio</c> package.
/// </summary>
/// <param name="Project">Project metadata with paths pointing into the extracted media cache.</param>
/// <param name="Composition">Saved composition/style state.</param>
/// <param name="Timeline">Saved timeline state.</param>
/// <param name="MediaFolder">Folder the package's media was extracted into.</param>
public sealed record MusioOpenResult(
    Project Project,
    CompositionConfig Composition,
    TimelineModel Timeline,
    string MediaFolder);

/// <summary>
/// Reads and writes <c>.musio</c> project packages.
/// </summary>
/// <remarks>
/// Media is extracted to a working folder on open rather than read in place, because the
/// decoding stack (<c>MediaPlayer</c>, <c>MediaClip</c>, NAudio) all want real file paths,
/// and <c>System.IO.Compression</c> does not expose seekable streams over archive entries.
/// The cache is keyed by project id and reused across opens, so the extra copy is paid
/// once per project rather than on every launch.
/// </remarks>
public static class MusioPackageService
{
    /// <summary>
    /// Writes <paramref name="project"/> and its edit state to a single
    /// <c>.musio</c> file at <paramref name="packagePath"/>.
    /// </summary>
    /// <remarks>
    /// The package is built into a temporary file and moved into place only once complete,
    /// so an interrupted save cannot leave a half-written project behind.
    /// </remarks>
    /// <param name="progress">Reports 0..1 packing progress.</param>
    public static async Task SaveAsync(
        string packagePath,
        Project project,
        CompositionConfig composition,
        TimelineModel? timeline,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(project);

        var directory = Path.GetDirectoryName(Path.GetFullPath(packagePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var mediaFiles = MusioPathRewriter.Enumerate(project, timeline, composition)
            .Where(File.Exists)
            .ToList();

        // Map each real file to a package entry, then rewrite a *copy* of the project so
        // the caller's live objects keep their working paths.
        var entryByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mediaFiles.Count; i++)
            entryByPath[mediaFiles[i]] = MusioPackage.BuildMediaEntryName(i, mediaFiles[i]);

        var manifest = new MusioManifest
        {
            SchemaVersion = MusioPackage.CurrentSchemaVersion,
            WrittenBy = typeof(MusioPackageService).Assembly.GetName().Version?.ToString(),
            SavedAt = DateTimeOffset.UtcNow,
            Project = Clone(project),
            Composition = Clone(composition),
            Timeline = timeline is null ? new TimelineModel() : Clone(timeline),
            MediaEntries = [.. entryByPath.Values],
        };

        manifest.Composition = MusioPathRewriter.Rewrite(
            manifest.Project,
            manifest.Timeline,
            manifest.Composition,
            path => entryByPath.TryGetValue(path, out var entry) ? entry : null);

        var tempPath = packagePath + ".saving";
        try
        {
            await Task.Run(() =>
            {
                using var file = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);

                var manifestEntry = archive.CreateEntry(
                    MusioPackage.ManifestEntryName, CompressionLevel.Optimal);
                using (var manifestStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(manifestStream, manifest, MusioPackage.JsonOptions);
                }

                int done = 0;
                foreach (var (sourcePath, entryName) in entryByPath)
                {
                    ct.ThrowIfCancellationRequested();

                    var entry = archive.CreateEntry(
                        entryName, MusioPackage.CompressionFor(sourcePath));

                    using (var entryStream = entry.Open())
                    using (var sourceStream = new FileStream(
                        sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        sourceStream.CopyTo(entryStream);
                    }

                    progress?.Report(++done / (double)Math.Max(1, entryByPath.Count));
                }
            }, ct).ConfigureAwait(false);

            File.Move(tempPath, packagePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Opens a <c>.musio</c> package, extracting its media into a working folder and
    /// returning project state whose paths point at the extracted files.
    /// </summary>
    /// <param name="workingRoot">
    /// Root folder for extracted media caches. Each project gets a subfolder keyed by id.
    /// </param>
    public static async Task<MusioOpenResult> OpenAsync(
        string packagePath,
        string workingRoot,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingRoot);

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Project package not found.", packagePath);

        return await Task.Run(() =>
        {
            using var file = new FileStream(
                packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            var manifestEntry = archive.GetEntry(MusioPackage.ManifestEntryName)
                ?? throw new InvalidDataException(
                    $"'{Path.GetFileName(packagePath)}' is not a Musio project (no manifest).");

            MusioManifest manifest;
            using (var manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<MusioManifest>(
                    manifestStream, MusioPackage.JsonOptions)
                    ?? throw new InvalidDataException("Project manifest is empty or unreadable.");
            }

            if (manifest.SchemaVersion > MusioPackage.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"This project was saved by a newer version of Musio " +
                    $"(format {manifest.SchemaVersion}, this build reads up to " +
                    $"{MusioPackage.CurrentSchemaVersion}).");
            }

            var mediaFolder = Path.Combine(workingRoot, manifest.Project.Id.ToString("N"));
            Directory.CreateDirectory(mediaFolder);

            var pathByEntry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mediaEntries = archive.Entries
                .Where(e => e.FullName.StartsWith(MusioPackage.MediaEntryPrefix, StringComparison.Ordinal))
                .ToList();

            int done = 0;
            foreach (var entry in mediaEntries)
            {
                ct.ThrowIfCancellationRequested();

                var destination = ResolveExtractionPath(mediaFolder, entry.FullName);

                // Reuse a previous extraction when it is already byte-for-byte complete.
                var existing = new FileInfo(destination);
                if (!existing.Exists || existing.Length != entry.Length)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }

                pathByEntry[entry.FullName] = destination;
                progress?.Report(++done / (double)Math.Max(1, mediaEntries.Count));
            }

            var composition = MusioPathRewriter.Rewrite(
                manifest.Project,
                manifest.Timeline,
                manifest.Composition,
                entryName => pathByEntry.TryGetValue(entryName, out var path) ? path : null);

            return new MusioOpenResult(
                manifest.Project, composition, manifest.Timeline, mediaFolder);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a package entry name to a path inside <paramref name="mediaFolder"/>,
    /// rejecting entries that would escape it.
    /// </summary>
    /// <remarks>
    /// A <c>.musio</c> file arrives from wherever the user got it, so entry names are
    /// untrusted input. Without this check a crafted archive containing
    /// <c>media/../../..</c> could overwrite arbitrary files on extraction.
    /// </remarks>
    private static string ResolveExtractionPath(string mediaFolder, string entryName)
    {
        var relative = entryName[MusioPackage.MediaEntryPrefix.Length..];
        var root = Path.GetFullPath(mediaFolder);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));

        if (!candidate.StartsWith(
                root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Project package contains an entry that escapes its folder: '{entryName}'.");
        }

        return candidate;
    }

    /// <summary>
    /// Deep-clones through JSON so the manifest can have its paths rewritten without
    /// disturbing the live objects the editor is still using.
    /// </summary>
    private static T Clone<T>(T value) where T : notnull
        => JsonSerializer.Deserialize<T>(
               JsonSerializer.Serialize(value, MusioPackage.JsonOptions),
               MusioPackage.JsonOptions)
           ?? throw new InvalidOperationException($"Failed to clone {typeof(T).Name}.");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MusioPackage] Could not remove temporary file '{path}': {ex.Message}");
        }
    }
}
