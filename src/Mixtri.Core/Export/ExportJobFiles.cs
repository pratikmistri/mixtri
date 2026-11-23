namespace Mixtri.Core.Export;

/// <summary>
/// The files one export job owns. An export never writes the destination the user chose
/// until it has succeeded: the encoder writes <see cref="StagingOutputPath"/> (and, when
/// audio is muxed, <see cref="TempVideoPath"/>) beside it, and the finished staging file
/// then atomically replaces <see cref="OutputPath"/>. Every owned name embeds the job's
/// GUID, so cleanup after a cancelled, failed or killed export deletes exactly this job's
/// files — never the destination (which may be a pre-existing file the user chose to
/// overwrite) and never another export's temp files, even one for a similarly named output.
/// </summary>
public sealed class ExportJobFiles
{
    /// <summary>Prefix of the hidden staging file an export encodes into before publishing.</summary>
    public const string StagingPrefix = ".mixtri_out_";

    private ExportJobFiles(string outputPath, string jobId, string stagingOutputPath, string tempVideoPath)
    {
        OutputPath = outputPath;
        JobId = jobId;
        StagingOutputPath = stagingOutputPath;
        TempVideoPath = tempVideoPath;
    }

    /// <summary>The destination; only written by <see cref="PublishStagedOutput"/>.</summary>
    public string OutputPath { get; }

    /// <summary>The job's GUID in <c>"N"</c> format (32 lowercase hex digits).</summary>
    public string JobId { get; }

    /// <summary>Where the encoder writes the complete output before it is published.</summary>
    public string StagingOutputPath { get; }

    /// <summary>Where the video-only first pass is written when audio is muxed afterwards.</summary>
    public string TempVideoPath { get; }

    /// <summary>Every path this job may create. The destination is deliberately not one of them.</summary>
    public IReadOnlyList<string> OwnedTempPaths => new[] { StagingOutputPath, TempVideoPath };

    /// <summary>A fresh job id for <see cref="For"/>.</summary>
    public static string NewJobId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// True when <paramref name="jobId"/> is exactly 32 lowercase hex digits. The id is
    /// embedded in file names and may arrive over the worker protocol, so anything else
    /// (separators, <c>..</c>, a wildcard) is rejected rather than trusted.
    /// </summary>
    public static bool IsValidJobId(string? jobId)
    {
        if (jobId is null || jobId.Length != 32) return false;
        foreach (char c in jobId)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>The files job <paramref name="jobId"/> owns when exporting to <paramref name="outputPath"/>.</summary>
    public static ExportJobFiles For(string outputPath, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!IsValidJobId(jobId))
            throw new ArgumentException("An export job id must be 32 lowercase hex digits.", nameof(jobId));

        string fullPath = Path.GetFullPath(outputPath);
        string folder = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The export output has no folder.", nameof(outputPath));
        string extension = Path.GetExtension(fullPath);
        string stem = BoundStem(Path.GetFileNameWithoutExtension(fullPath), extension);
        return new ExportJobFiles(
            fullPath,
            jobId,
            Path.Combine(folder, $"{StagingPrefix}{stem}_{jobId}{extension}"),
            Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}{stem}_{jobId}.mp4"));
    }

    /// <summary>
    /// Longest file-name component most Windows file systems accept. A prefix, the 32-digit
    /// job id and an extension are added around the stem, so a valid destination name close
    /// to this limit would otherwise yield staging/temp names that cannot be created.
    /// </summary>
    internal const int MaxFileNameComponent = 255;

    /// <summary>
    /// Truncates <paramref name="stem"/> so every name built from it stays within
    /// <see cref="MaxFileNameComponent"/>. Deterministic, so the worker and the client's
    /// cleanup always derive identical paths.
    /// </summary>
    internal static string BoundStem(string stem, string extension)
    {
        int longestPrefix = Math.Max(StagingPrefix.Length, VideoEncoder.TempVideoPrefix.Length);
        int longestExtension = Math.Max(extension.Length, ".mp4".Length);
        // prefix + stem + "_" + 32-digit id + extension
        int budget = MaxFileNameComponent - longestPrefix - 1 - 32 - longestExtension;
        if (budget < 1) budget = 1;
        return stem.Length <= budget ? stem : stem[..budget];
    }

    /// <summary>Attempts made to move the staged file onto the destination before falling back.</summary>
    internal const int PublishAttempts = 4;

    /// <summary>Delay between publish attempts.</summary>
    internal static readonly TimeSpan PublishRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Replaces the destination with the finished staging file in one rename on the same
    /// volume, so the destination is either the old file or the complete new one. If the
    /// destination is locked (open in a player or sync client) the move is retried briefly,
    /// then the finished file is moved to a unique sibling name instead of being discarded.
    /// Returns the path the output actually lives at (<see cref="OutputPath"/> unless the
    /// fallback was used). Throws only if even the fallback move fails.
    /// </summary>
    public string PublishStagedOutput()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(StagingOutputPath, OutputPath, overwrite: true);
                return OutputPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(StagingOutputPath)) throw;
                if (attempt >= PublishAttempts)
                {
                    Mixtri.Core.Diagnostics.DiagLog.Write("Export",
                        $"destination locked, publishing to a sibling name: {ex.Message}");
                    break;
                }
                Thread.Sleep(PublishRetryDelay);
            }
        }

        string folder = Path.GetDirectoryName(OutputPath)!;
        string extension = Path.GetExtension(OutputPath);
        // Leave room for " (NNNN)" so a valid name near the 255-character component limit
        // still yields a creatable fallback name.
        string stem = BoundFallbackStem(Path.GetFileNameWithoutExtension(OutputPath), extension);
        for (int counter = 2; ; counter++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({counter}){extension}");
            if (File.Exists(candidate)) continue;
            try
            {
                // No overwrite: never replace a file that appeared since the check.
                File.Move(StagingOutputPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate)) { /* raced; try the next name */ }
            if (counter > 1000) throw new IOException("Could not find a free file name to publish the export.");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex GeneratedTempName = new(
        @"^\.mixtri_(out|video)_.*_[0-9a-f]{32}\.[A-Za-z0-9]{1,8}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True for a staging/temp file name in the exact shape <see cref="For"/> and
    /// <see cref="VideoEncoder"/> generate.</summary>
    internal static bool IsGeneratedTempName(string fileName)
        => (fileName.StartsWith(StagingPrefix, StringComparison.Ordinal)
            || fileName.StartsWith(VideoEncoder.TempVideoPrefix, StringComparison.Ordinal))
           && GeneratedTempName.IsMatch(fileName);

    /// <summary>
    /// Trims <paramref name="stem"/> so <c>stem + " (NNNN)" + extension</c> fits in one file-name
    /// component (the counter is capped well below 10 000 attempts).
    /// </summary>
    internal static string BoundFallbackStem(string stem, string extension)
    {
        const int counterSuffix = 7; // " (1000)"
        int budget = MaxFileNameComponent - counterSuffix - extension.Length;
        if (budget < 1) budget = 1;
        return stem.Length <= budget ? stem : stem[..budget];
    }

    /// <summary>
    /// Best-effort removal of orphaned staging/temp files in <paramref name="folder"/>
    /// left by an app and worker that died together. Only files with this class's
    /// hidden prefixes whose last write is older than <paramref name="olderThan"/> are
    /// deleted, so a running job's files are never touched. Never throws.
    /// Returns how many files were removed.
    /// </summary>
    public static int SweepStale(string folder, TimeSpan olderThan, string? keepPath = null)
    {
        int removed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (string prefix in new[] { StagingPrefix, VideoEncoder.TempVideoPrefix })
            {
                foreach (string path in Directory.EnumerateFiles(folder, prefix + "*"))
                {
                    try
                    {
                        // Only names this class actually generates (prefix, stem, "_", a
                        // 32-hex job id, extension) — a user's own file that merely starts
                        // with the prefix is never swept — and never the requested output.
                        if (!IsGeneratedTempName(Path.GetFileName(path))) continue;
                        if (keepPath is not null && string.Equals(
                                Path.GetFullPath(path), Path.GetFullPath(keepPath),
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                        File.Delete(path);
                        removed++;
                    }
                    catch (Exception) { /* locked or vanished: best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("Export", $"stale staging sweep failed: {ex.Message}");
        }
        return removed;
    }

    /// <summary>
    /// Best-effort deletion of this job's temp/staging files. Never touches
    /// <see cref="OutputPath"/>. Returns how many files were removed.
    /// </summary>
    public int DeleteOwnedTempFiles()
    {
        int removed = 0;
        foreach (var path in OwnedTempPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Mixtri.Core.Diagnostics.DiagLog.Write("Export", $"could not remove export temp file: {ex.Message}");
            }
        }
        return removed;
    }
}
