namespace Musio.Core.Models;

/// <summary>
/// Represents a single recording session and its associated files.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string VideoFilePath { get; set; } = string.Empty;
    public string CursorDataFilePath { get; set; } = string.Empty;
    public string? WebcamFilePath { get; set; }
    public string? KeyboardDataFilePath { get; set; }
    public List<string> AudioFilePaths { get; set; } = [];
    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; } = 30;

    /// <summary>
    /// Time offset in seconds between mouse recording start and video frame 0.
    /// Positive means mouse started before video. Subtract from mouse timestamps
    /// to align with video frames.
    /// </summary>
    public double MouseToVideoOffsetSeconds { get; set; }

    /// <summary>
    /// Offset of the crop region's top-left corner within the full monitor capture,
    /// in physical pixels. Used to rebase mouse coordinates for region recordings.
    /// Both are zero when the recording is not a region capture.
    /// </summary>
    public int CropOffsetX { get; set; }
    public int CropOffsetY { get; set; }
}
