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
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        string extension = Path.GetExtension(fullPath);
        return new ExportJobFiles(
            fullPath,
            jobId,
            Path.Combine(folder, $"{StagingPrefix}{stem}_{jobId}{extension}"),
            Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}{stem}_{jobId}.mp4"));
    }

    /// <summary>
    /// Replaces the destination with the finished staging file in one rename on the same
    /// volume, so the destination is either the old file or the complete new one.
    /// </summary>
    public void PublishStagedOutput() => File.Move(StagingOutputPath, OutputPath, overwrite: true);

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
