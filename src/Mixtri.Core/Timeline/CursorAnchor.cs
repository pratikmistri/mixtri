using System.Text.Json.Serialization;

namespace Mixtri.Core.Timeline;

/// <summary>
/// A user-authored override of where the recorded pointer is at one instant.
/// <para>
/// An anchor does not replace the recorded journey — it displaces it. The path is offset by
/// <c>target - recordedPosition</c> at <see cref="Timestamp"/>, and that offset falls away
/// smoothly towards the neighbouring anchors and clicks, so the real motion (speed, jitter,
/// dwell) is preserved and the rest of the recording is left untouched. See
/// <c>Mixtri.Core.Processing.CursorPathWarp</c> for the field this feeds.
/// </para>
/// </summary>
/// <remarks>
/// Deliberately shaped like <see cref="ZoomKeyframe"/>: a SOURCE-relative timestamp, a
/// normalized position, and a <see cref="SourceVideoFilePath"/> naming the recording it
/// belongs to. That is what keeps an anchor attached to the footage through trims, reorders
/// and appended recordings rather than to a moment on the output timeline.
/// </remarks>
public record CursorAnchor
{
    /// <summary>Stable identity for selection and undo/redo tracking.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// When, in the owning recording's SOURCE time, the pointer should be at
    /// <see cref="X"/>/<see cref="Y"/>.
    /// </summary>
    public TimeSpan Timestamp { get; init; }

    /// <summary>Target horizontal position, normalized 0-1 across the source frame.</summary>
    public double X { get; init; }

    /// <summary>Target vertical position, normalized 0-1 down the source frame.</summary>
    public double Y { get; init; }

    /// <summary>
    /// Source video file this anchor belongs to. Null means the primary recording.
    /// Mirrors <see cref="ZoomKeyframe.SourceVideoFilePath"/> so an appended recording can
    /// carry its own anchors without them warping the primary recording's cursor.
    /// </summary>
    public string? SourceVideoFilePath { get; init; }

    /// <summary>Position as a tuple, for the warp field's control-point construction.</summary>
    [JsonIgnore]
    public (double X, double Y) NormalizedPosition => (X, Y);
}
