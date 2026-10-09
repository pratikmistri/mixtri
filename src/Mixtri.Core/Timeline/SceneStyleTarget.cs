namespace Mixtri.Core.Timeline;

/// <summary>The clip whose scene controls are being edited.</summary>
public static class SceneStyleTarget
{
    public static VideoSegment? Resolve(TimelineModel model, string? selectedId, TimeSpan position)
    {
        if (selectedId is not null && model.Segments.OfType<VideoSegment>()
            .FirstOrDefault(segment => segment.Id == selectedId) is { } selected)
            return selected;

        return model.GetSegmentAtTime(position).Segment as VideoSegment;
    }
}
