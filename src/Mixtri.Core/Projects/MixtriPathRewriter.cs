using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Core.Projects;

/// <summary>
/// What a media reference is to the project.
/// </summary>
public enum MediaReferenceKind
{
    /// <summary>A decorative asset (background image, custom cursor image).</summary>
    StyleAsset,

    /// <summary>Captured content: video, audio, webcam, cursor or keyboard data.</summary>
    Recording,
}

/// <summary>A media file referenced by a project, and what role it plays.</summary>
public sealed record MediaReference(string Path, MediaReferenceKind Kind);

/// <summary>
/// Rewrites every media file reference held by a project and its timeline through a
/// caller-supplied mapping.
/// </summary>
/// <remarks>
/// Saving turns absolute paths into package-relative entry names; opening turns them back
/// into absolute paths inside the extracted media cache. Both directions are the same
/// traversal, so they share this one implementation — a reference missed here is a file
/// that silently fails to load after a round-trip.
/// </remarks>
public static class MixtriPathRewriter
{
    /// <summary>
    /// Applies <paramref name="map"/> to every media reference in <paramref name="project"/>,
    /// <paramref name="timeline"/> and <paramref name="composition"/>. A mapping that returns
    /// null leaves the value unchanged.
    /// </summary>
    /// <returns>
    /// The composition with its references rewritten. <see cref="CompositionConfig"/> and the
    /// style records it holds are immutable, so it cannot be updated in place like the other
    /// two.
    /// </returns>
    public static CompositionConfig Rewrite(
        Project project,
        TimelineModel? timeline,
        CompositionConfig composition,
        Func<string, string?> map)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(map);

        RewriteProject(project, map);

        foreach (var source in project.Sources)
            RewriteSource(source, map);

        if (timeline is not null)
            RewriteTimeline(timeline, map);

        return composition with
        {
            Background = RewriteBackground(composition.Background, map)!,
            Cursor = RewriteCursor(composition.Cursor, map)!,
        };
    }

    private static BackgroundStyle? RewriteBackground(BackgroundStyle? style, Func<string, string?> map)
        => style is null ? null : style with
        {
            BackgroundImagePath = ApplyOptional(style.BackgroundImagePath, map),
        };

    private static CursorStyle? RewriteCursor(CursorStyle? style, Func<string, string?> map)
        => style is null ? null : style with
        {
            CustomImagePath = ApplyOptional(style.CustomImagePath, map),
        };

    /// <summary>
    /// Enumerates every distinct media reference without modifying anything. Used to build
    /// the set of files that need packing.
    /// </summary>
    /// <remarks>
    /// This is a genuine read-only traversal rather than a <see cref="Rewrite"/> with an
    /// identity mapping: rewriting replaces record instances even when values are
    /// unchanged, which would invalidate references the editor holds to live segments.
    /// </remarks>
    public static IReadOnlyList<string> Enumerate(
        Project project, TimelineModel? timeline, CompositionConfig? composition = null)
        => [.. EnumerateReferences(project, timeline, composition).Select(r => r.Path)];

    /// <summary>
    /// Enumerates every distinct media reference along with what it is, so a caller can
    /// tell irreplaceable recording media apart from decorative assets.
    /// </summary>
    /// <remarks>
    /// The distinction matters when packing: a missing background image is cosmetic and
    /// can be dropped, while a missing video or cursor log is the recording itself and
    /// must never be dropped silently from a package that overwrites the previous one.
    /// </remarks>
    public static IReadOnlyList<MediaReference> EnumerateReferences(
        Project project, TimelineModel? timeline, CompositionConfig? composition = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var found = new List<MediaReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, MediaReferenceKind kind = MediaReferenceKind.StyleAsset)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                found.Add(new MediaReference(path, kind));
        }

        void AddRecording(
            string video, string cursor, string? webcam, string? keyboard, List<string> audio)
        {
            Add(video, MediaReferenceKind.Recording);
            Add(cursor, MediaReferenceKind.Recording);
            Add(webcam, MediaReferenceKind.Recording);
            Add(keyboard, MediaReferenceKind.Recording);
            foreach (var a in audio) Add(a, MediaReferenceKind.Recording);
        }

        AddRecording(
            project.VideoFilePath, project.CursorDataFilePath, project.WebcamFilePath,
            project.KeyboardDataFilePath, project.AudioFilePaths);

        foreach (var s in project.Sources)
            AddRecording(
                s.VideoFilePath, s.CursorDataFilePath, s.WebcamFilePath,
                s.KeyboardDataFilePath, s.AudioFilePaths);

        if (composition is not null)
        {
            Add(composition.Background?.BackgroundImagePath);
            Add(composition.Cursor?.CustomImagePath);
        }

        if (timeline is null)
            return found;

        Add(timeline.PrimaryVideoFilePath, MediaReferenceKind.Recording);

        foreach (var segment in timeline.Segments)
        {
            switch (segment)
            {
                case VideoSegment v:
                    AddRecording(
                        v.VideoFilePath, v.CursorDataFilePath ?? string.Empty, v.WebcamFilePath,
                        v.KeyboardDataFilePath, v.AudioFilePaths);
                    Add(v.FrameStyleOverride?.BackgroundImagePath);
                    Add(v.CursorStyleOverride?.CustomImagePath);
                    break;
                case TextSlideSegment slide:
                    Add(slide.BackgroundImagePath);
                    break;
                case CameraSegment camera:
                    // Captured webcam media, not a decorative asset: dropping it on save
                    // would take the only copy with it.
                    Add(camera.WebcamFilePath, MediaReferenceKind.Recording);
                    break;
            }
        }

        foreach (var camera in timeline.CameraSegments)
            Add(camera.WebcamFilePath, MediaReferenceKind.Recording);

        foreach (var zoom in timeline.ZoomKeyframes)
            Add(zoom.SourceVideoFilePath);

        // Mirrors zoom keyframes: a back-reference to a recording collected above, not a
        // distinct asset.
        foreach (var overlay in timeline.TextOverlays)
            Add(overlay.SourceVideoFilePath);

        foreach (var anchor in timeline.CursorAnchors)
            Add(anchor.SourceVideoFilePath);

        // Inserted voice-over/music is irreplaceable in exactly the way a style asset is
        // not: the normalised WAV lives in an app-owned import folder the orphan sweep can
        // reclaim, so dropping it from a package silently would lose the only copy.
        foreach (var track in timeline.AudioTracks)
            Add(track.FilePath, MediaReferenceKind.Recording);

        return found;
    }

    private static void RewriteProject(Project project, Func<string, string?> map)
    {
        project.VideoFilePath = Apply(project.VideoFilePath, map);
        project.CursorDataFilePath = Apply(project.CursorDataFilePath, map);
        project.WebcamFilePath = ApplyOptional(project.WebcamFilePath, map);
        project.KeyboardDataFilePath = ApplyOptional(project.KeyboardDataFilePath, map);
        RewriteList(project.AudioFilePaths, map);
    }

    private static void RewriteSource(RecordingSource source, Func<string, string?> map)
    {
        source.VideoFilePath = Apply(source.VideoFilePath, map);
        source.CursorDataFilePath = Apply(source.CursorDataFilePath, map);
        source.WebcamFilePath = ApplyOptional(source.WebcamFilePath, map);
        source.KeyboardDataFilePath = ApplyOptional(source.KeyboardDataFilePath, map);
        RewriteList(source.AudioFilePaths, map);
    }

    private static void RewriteTimeline(TimelineModel timeline, Func<string, string?> map)
    {
        timeline.PrimaryVideoFilePath = ApplyOptional(timeline.PrimaryVideoFilePath, map);

        for (int i = 0; i < timeline.Segments.Count; i++)
        {
            switch (timeline.Segments[i])
            {
                case VideoSegment video:
                {
                    var audio = new List<string>(video.AudioFilePaths);
                    RewriteList(audio, map);

                    timeline.Segments[i] = video with
                    {
                        VideoFilePath = Apply(video.VideoFilePath, map),
                        CursorDataFilePath = ApplyOptional(video.CursorDataFilePath, map),
                        WebcamFilePath = ApplyOptional(video.WebcamFilePath, map),
                        KeyboardDataFilePath = ApplyOptional(video.KeyboardDataFilePath, map),
                        AudioFilePaths = audio,
                        FrameStyleOverride = RewriteBackground(video.FrameStyleOverride, map),
                        CursorStyleOverride = RewriteCursor(video.CursorStyleOverride, map),
                    };
                    break;
                }

                case TextSlideSegment slide:
                    // BackgroundImagePath is settable, so the segment can be updated in place.
                    slide.BackgroundImagePath = ApplyOptional(slide.BackgroundImagePath, map);
                    break;

                case CameraSegment camera:
                    timeline.Segments[i] = camera with
                    {
                        WebcamFilePath = ApplyOptional(camera.WebcamFilePath, map),
                    };
                    break;
            }
        }

        for (int i = 0; i < timeline.CameraSegments.Count; i++)
        {
            timeline.CameraSegments[i] = timeline.CameraSegments[i] with
            {
                WebcamFilePath = ApplyOptional(timeline.CameraSegments[i].WebcamFilePath, map),
            };
        }

        for (int i = 0; i < timeline.ZoomKeyframes.Count; i++)
        {
            timeline.ZoomKeyframes[i] = timeline.ZoomKeyframes[i] with
            {
                SourceVideoFilePath = ApplyOptional(timeline.ZoomKeyframes[i].SourceVideoFilePath, map),
            };
        }

        // Mirrors zoom keyframes: SourceVideoFilePath names the recording whose source-time
        // space the overlay was authored against, not a distinct asset.
        for (int i = 0; i < timeline.TextOverlays.Count; i++)
        {
            timeline.TextOverlays[i] = timeline.TextOverlays[i] with
            {
                SourceVideoFilePath = ApplyOptional(timeline.TextOverlays[i].SourceVideoFilePath, map),
            };
        }

        // Mirrors zoom keyframes: SourceVideoFilePath names the recording whose source-time
        // space the anchor was authored against, not a distinct asset.
        for (int i = 0; i < timeline.CursorAnchors.Count; i++)
        {
            timeline.CursorAnchors[i] = timeline.CursorAnchors[i] with
            {
                SourceVideoFilePath = ApplyOptional(timeline.CursorAnchors[i].SourceVideoFilePath, map),
            };
        }

        // AudioTrack is a mutable class (not a record), so it updates in place.
        foreach (var track in timeline.AudioTracks)
            track.FilePath = Apply(track.FilePath, map);
    }

    private static void RewriteList(List<string> paths, Func<string, string?> map)
    {
        for (int i = 0; i < paths.Count; i++)
            paths[i] = Apply(paths[i], map);
    }

    private static string Apply(string path, Func<string, string?> map)
        => string.IsNullOrWhiteSpace(path) ? path : map(path) ?? path;

    private static string? ApplyOptional(string? path, Func<string, string?> map)
        => string.IsNullOrWhiteSpace(path) ? path : map(path) ?? path;
}
