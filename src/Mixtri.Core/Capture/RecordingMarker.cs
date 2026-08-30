using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Mixtri.Core.Capture;

/// <summary>
/// Reads and writes the finalization marker that records which encoder produced a
/// recording's MP4.
/// </summary>
/// <remarks>
/// <para>
/// The marker is what makes a decoded MP4 trustworthy. Builds before
/// <see cref="VideoWriter.EncoderVersion"/> 2 wrote a vertically flipped file (see the
/// <c>BitmapFlip.Vertical</c> comment in <c>VideoWriter.ProduceFrameSampleAsync</c>), and
/// nothing displayed those files at the time, so the defect is invisible until such a
/// recording is opened for editing — where it would silently preview *and export* upside
/// down. Every read path therefore asks this class whether the file needs compensating.
/// </para>
/// <para>
/// Two layouts carry the marker:
/// <list type="bullet">
/// <item>a capture session folder, where it sits beside <c>video.mp4</c> as
/// <c>finalized.marker</c>; and</item>
/// <item>a <c>.mixtri</c> package's extracted media cache, where every video is packed with
/// a per-file companion (<c>video.mp4.finalized.marker</c>) because one flat folder holds
/// the media of many recordings.</item>
/// </list>
/// </para>
/// <para>
/// A missing marker means "legacy", not "unknown". That inference is only sound because
/// frames are never released without one: <see cref="VideoWriter.DeleteCapturedFrames"/>
/// and <c>SessionCleanupService.CanReleaseFrames</c> both require it, so a marker-less
/// session still has its JPEGs and never reaches the MP4 decode path at all. Reaching it
/// means the frames were deleted by a build that predates the marker.
/// </para>
/// </remarks>
public static class RecordingMarker
{
    /// <summary>Marker file name used inside a capture session folder.</summary>
    public const string SessionMarkerName = VideoWriter.FinalizedMarkerName;

    /// <summary>
    /// Suffix appended to a video's file name for the per-file marker used inside a
    /// package's media folder.
    /// </summary>
    public const string CompanionSuffix = "." + VideoWriter.FinalizedMarkerName;

    /// <summary>Encoder version reported for a recording with no marker.</summary>
    public const int LegacyEncoderVersion = 1;

    private static readonly Regex VersionPattern = new(
        "\"encoderVersion\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Path of the per-file companion marker for <paramref name="videoFilePath"/>.
    /// </summary>
    public static string CompanionMarkerPath(string videoFilePath)
        => videoFilePath + CompanionSuffix;

    /// <summary>
    /// Serialized marker contents for <paramref name="encoderVersion"/>.
    /// </summary>
    public static string BuildContent(int encoderVersion)
        => $"{{\"encoderVersion\":{encoderVersion},\"finalizedUtc\":\"{DateTimeOffset.UtcNow:O}\"}}";

    /// <summary>
    /// Returns true when a marker exists for <paramref name="videoFilePath"/> in either
    /// the session or the package layout.
    /// </summary>
    public static bool Exists(string? videoFilePath)
        => TryFindMarker(videoFilePath) is not null;

    /// <summary>
    /// Reads the encoder version that produced <paramref name="videoFilePath"/>, falling
    /// back to <see cref="LegacyEncoderVersion"/> when no marker is present.
    /// </summary>
    public static int ReadEncoderVersion(string? videoFilePath)
    {
        var markerPath = TryFindMarker(videoFilePath);
        if (markerPath is null)
            return LegacyEncoderVersion;

        try
        {
            var match = VersionPattern.Match(File.ReadAllText(markerPath));
            if (match.Success && int.TryParse(match.Groups[1].Value, out int version))
                return version;

            // A marker with no parsable version still proves the file came from a build
            // that wrote markers at all, i.e. the orientation-correct encoder.
            return VideoWriter.EncoderVersion;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingMarker] Could not read '{markerPath}': {ex.Message}");
            return VideoWriter.EncoderVersion;
        }
    }

    /// <summary>
    /// True when frames decoded from <paramref name="videoFilePath"/> must be flipped
    /// vertically to display the right way up.
    /// </summary>
    public static bool NeedsVerticalFlip(string? videoFilePath)
        => ReadEncoderVersion(videoFilePath) < 2;

    /// <summary>
    /// Writes a companion marker beside <paramref name="videoFilePath"/>. Best effort:
    /// failure only costs the orientation hint, so it never fails a save or an open.
    /// </summary>
    public static void TryWriteCompanion(string videoFilePath, int encoderVersion)
    {
        try
        {
            File.WriteAllText(CompanionMarkerPath(videoFilePath), BuildContent(encoderVersion));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingMarker] Could not write companion marker: {ex.Message}");
        }
    }

    private static string? TryFindMarker(string? videoFilePath)
    {
        if (string.IsNullOrWhiteSpace(videoFilePath))
            return null;

        try
        {
            var companion = CompanionMarkerPath(videoFilePath);
            if (File.Exists(companion))
                return companion;

            var directory = Path.GetDirectoryName(videoFilePath);
            if (string.IsNullOrEmpty(directory))
                return null;

            var sessionMarker = Path.Combine(directory, SessionMarkerName);
            return File.Exists(sessionMarker) ? sessionMarker : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingMarker] Could not locate marker: {ex.Message}");
            return null;
        }
    }
}
