namespace Musio.Core.Timeline;

public interface IEditOperation
{
    string Description { get; }
    void Execute(TimelineModel model);
    void Undo(TimelineModel model);
}

