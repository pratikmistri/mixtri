using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Windows.Storage.Streams;

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
    /// Candidate poster heights, tried in order.
    /// </summary>
    /// <remarks>
    /// More than one is needed because <c>GetThumbnailsAsync</c> returns a blank frame at
    /// certain output sizes — reproducibly, and not by any threshold I could pin down: for
    /// a 3:2 source, 270x180 and 480x320 decode fine while 330x220 comes back flat grey.
    /// Rather than pretend to know the rule, the result is checked and the next size tried.
    /// </remarks>
    private static readonly int[] PosterHeightCandidates = [180, 320, 240, 120];

    /// <summary>
    /// How many candidate frames the poster is chosen from. A handful is enough to skip
    /// a blank opening frame without decoding the whole recording.
    /// </summary>
    private const int PosterSampleCount = 4;

    /// <summary>
    /// Distinct-colour count below which a decoded frame is treated as blank. Real screen
    /// content runs into the hundreds; a failed decode sits around twenty.
    /// </summary>
    private const int BlankFrameColorThreshold = 64;

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

        // A save writes over the file it came from, so a reference that silently vanishes
        // here takes the only copy of that media with it. Cosmetic assets may be dropped;
        // captured content may not.
        var references = MusioPathRewriter.EnumerateReferences(project, timeline, composition);

        var missing = references
            .Where(r => r.Kind == MediaReferenceKind.Recording && !File.Exists(r.Path))
            .Select(r => r.Path)
            .ToList();

        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Take(3).Select(Path.GetFileName));
            var more = missing.Count > 3 ? $" (+{missing.Count - 3} more)" : string.Empty;
            throw new FileNotFoundException(
                $"{missing.Count} recording file(s) this project needs are missing, so saving " +
                $"would drop them permanently: {names}{more}.",
                missing[0]);
        }

        var mediaFiles = references
            .Where(r => File.Exists(r.Path))
            .Select(r => r.Path)
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
            // Rendered before the archive is opened so a poster failure cannot abort the
            // save; the poster is presentation only.
            var poster = await TryRenderPosterAsync(project, ct).ConfigureAwait(false);

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

                if (poster is { Length: > 0 })
                {
                    var posterEntry = archive.CreateEntry(
                        MusioPackage.PosterEntryName, CompressionLevel.NoCompression);
                    using var posterStream = posterEntry.Open();
                    posterStream.Write(poster, 0, poster.Length);
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

                    // Carry the recording's encoder version with it. A package's media all
                    // lands in one flat folder, so the session-folder marker cannot come
                    // along as-is; a per-file companion can. Losing it would make an
                    // orientation-correct video look legacy on the next open and get
                    // flipped.
                    if (string.Equals(Path.GetExtension(sourcePath), ".mp4",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var markerEntry = archive.CreateEntry(
                            entryName + RecordingMarker.CompanionSuffix, CompressionLevel.Optimal);
                        using var markerStream = new StreamWriter(markerEntry.Open());
                        markerStream.Write(RecordingMarker.BuildContent(
                            RecordingMarker.ReadEncoderVersion(sourcePath)));
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
    /// Renders a representative frame for the Open page, or null when one cannot be
    /// produced.
    /// </summary>
    /// <remarks>
    /// Taken a little way into the recording rather than at frame zero, which is often a
    /// blank desktop or a half-drawn window.
    /// </remarks>
    private static async Task<byte[]?> TryRenderPosterAsync(Project project, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(project.VideoFilePath) || !File.Exists(project.VideoFilePath))
                return null;

            var device = CanvasDevice.GetSharedDevice();

            foreach (var height in PosterHeightCandidates)
            {
                ct.ThrowIfCancellationRequested();

                var strip = await VideoThumbnailExtractor.ExtractAsync(
                    project.VideoFilePath,
                    height,
                    device,
                    maxCount: PosterSampleCount,
                    ct: ct).ConfigureAwait(false);

                if (strip is null)
                    continue;

                try
                {
                    // Prefer a later sample: the opening frame is often a blank desktop or
                    // a half-drawn window.
                    var usable = strip.Thumbnails.Where(t => t is not null && !IsEffectivelyBlank(t)).ToList();
                    var frame = usable.LastOrDefault();
                    if (frame is null)
                        continue;

                    return await EncodeJpegAsync(frame).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var t in strip.Thumbnails) t?.Dispose();
                }
            }

            Debug.WriteLine($"[MusioPackage] No usable poster frame for '{project.VideoFilePath}'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MusioPackage] Could not render poster: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when a decoded frame carries essentially no detail, which is how a failed
    /// thumbnail decode presents.
    /// </summary>
    private static bool IsEffectivelyBlank(CanvasBitmap frame)
    {
        try
        {
            var seen = new HashSet<int>();
            foreach (var c in frame.GetPixelColors())
            {
                if (seen.Add((c.R << 16) | (c.G << 8) | c.B) && seen.Count >= BlankFrameColorThreshold)
                    return false;
            }
            return true;
        }
        catch
        {
            // If it cannot be inspected, let it through rather than discard a good frame.
            return false;
        }
    }

    private static async Task<byte[]> EncodeJpegAsync(CanvasBitmap frame)
    {
        using var stream = new InMemoryRandomAccessStream();
        await frame.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.85f);

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Reads a package's poster image without extracting any media, or null when the
    /// package has none.
    /// </summary>
    public static byte[]? ReadPoster(string packagePath)
    {
        try
        {
            if (!File.Exists(packagePath))
                return null;

            using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            var entry = archive.GetEntry(MusioPackage.PosterEntryName);
            if (entry is null)
                return null;

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MusioPackage] Could not read poster from '{packagePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads a package's manifest only — no media extraction — for listing projects.
    /// </summary>
    public static MusioManifest? ReadManifest(string packagePath)
    {
        try
        {
            if (!File.Exists(packagePath))
                return null;

            using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            var entry = archive.GetEntry(MusioPackage.ManifestEntryName);
            if (entry is null)
                return null;

            using var entryStream = entry.Open();
            return JsonSerializer.Deserialize<MusioManifest>(entryStream, MusioPackage.JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MusioPackage] Could not read manifest from '{packagePath}': {ex.Message}");
            return null;
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
            var allEntries = archive.Entries
                .Where(e => e.FullName.StartsWith(MusioPackage.MediaEntryPrefix, StringComparison.Ordinal))
                .ToList();

            // Markers are resolved from their video's destination rather than through the
            // name heuristic, so a package holding two recordings both called video.mp4
            // cannot pair a marker with the wrong file.
            var mediaEntries = allEntries
                .Where(e => !e.FullName.EndsWith(RecordingMarker.CompanionSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var markerEntries = allEntries
                .Where(e => e.FullName.EndsWith(RecordingMarker.CompanionSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int done = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in mediaEntries)
            {
                ct.ThrowIfCancellationRequested();

                var destination = ResolveExtractionPath(mediaFolder, entry.FullName, usedNames);

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

            ExtractOrientationMarkers(
                markerEntries, pathByEntry, manifest.SchemaVersion, manifest.SavedAt, ct);

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
    /// Restores the per-video encoder-version markers beside the extracted media.
    /// </summary>
    /// <remarks>
    /// Version 1 packages predate the companion marker, so their videos read back as "no
    /// marker", which the decode path treats as the pre-orientation-fix encoder and
    /// compensates for by flipping. That default is right for a v1 package only when the
    /// package was actually written before the encoder was fixed — packaging shipped
    /// first, so v1 packages exist on both sides of that change and their videos really
    /// can be mirrored. The manifest's <see cref="MusioManifest.SavedAt"/> is the only
    /// evidence available, so it decides; guessing "current encoder" for every v1 package
    /// would persist a wrong marker and leave the recording permanently upside down in the
    /// preview, the filmstrip and every export, with no way to recover.
    /// </remarks>
    private static void ExtractOrientationMarkers(
        List<ZipArchiveEntry> markerEntries,
        Dictionary<string, string> pathByEntry,
        int schemaVersion,
        DateTimeOffset savedAt,
        CancellationToken ct)
    {
        foreach (var entry in markerEntries)
        {
            ct.ThrowIfCancellationRequested();

            var videoEntryName = entry.FullName[..^RecordingMarker.CompanionSuffix.Length];
            if (!pathByEntry.TryGetValue(videoEntryName, out var videoPath))
                continue;

            try
            {
                entry.ExtractToFile(RecordingMarker.CompanionMarkerPath(videoPath), overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MusioPackage] Could not restore marker for '{videoPath}': {ex.Message}");
            }
        }

        if (schemaVersion >= 2)
            return;

        // Saved before the encoder was fixed: leave the videos unmarked so the decode
        // path's legacy default flips them, exactly as it does for an unmarked session.
        if (savedAt < OrientationFixedUtc)
            return;

        foreach (var videoPath in pathByEntry.Values)
        {
            if (!string.Equals(Path.GetExtension(videoPath), ".mp4", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!RecordingMarker.Exists(videoPath))
                RecordingMarker.TryWriteCompanion(videoPath, VideoWriter.EncoderVersion);
        }
    }

    /// <summary>
    /// When the vertically-mirrored-MP4 encoder bug was fixed. A version 1 package saved
    /// before this carries mirrored video; one saved after does not.
    /// </summary>
    private static readonly DateTimeOffset OrientationFixedUtc =
        new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Resolves a package entry name to a path inside <paramref name="mediaFolder"/>,
    /// rejecting entries that would escape it and restoring the original file name.
    /// </summary>
    /// <remarks>
    /// A <c>.musio</c> file arrives from wherever the user got it, so entry names are
    /// untrusted input. Without this check a crafted archive containing
    /// <c>media/../../..</c> could overwrite arbitrary files on extraction.
    /// </remarks>
    private static string ResolveExtractionPath(
        string mediaFolder, string entryName, HashSet<string> usedNames)
    {
        var relative = entryName[MusioPackage.MediaEntryPrefix.Length..];
        var root = Path.GetFullPath(mediaFolder);

        // Validate the raw entry first: stripping happens only after the path is known safe.
        var rawCandidate = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!rawCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Project package contains an entry that escapes its folder: '{entryName}'.");
        }

        // Extract under the original name so a later save does not add another index
        // prefix on top of this one. Fall back to the prefixed name on a collision.
        var stripped = MusioPackage.StripEntryIndexPrefix(Path.GetFileName(relative));
        var chosen = usedNames.Add(stripped) ? stripped : relative;

        var candidate = Path.GetFullPath(Path.Combine(root, chosen));
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : rawCandidate;
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
