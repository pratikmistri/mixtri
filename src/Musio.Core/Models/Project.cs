using Musio.Core.Capture;

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
    /// Screen-absolute physical pixel position of the captured frame's top-left
    /// corner on the virtual desktop. Used to rebase mouse hook coordinates
    /// (which are screen-absolute) into the captured frame's coordinate space.
    /// For monitor captures this is the monitor origin, for window captures this
    /// is the window position, and for region captures this is the monitor origin
    /// plus the crop rect offset within the monitor.
    /// </summary>
    public int CropOffsetX { get; set; }
    public int CropOffsetY { get; set; }

    /// <summary>
    /// DPI scale factor of the monitor where the recording was made.
    /// Used to convert mouse hook coordinates (logical pixels) to physical pixels.
    /// Zero means auto-detect from source dimensions at compositor time.
    /// </summary>
    public float DpiScale { get; set; }

    /// <summary>
    /// Time offset in seconds between audio recording start and video frame 0.
    /// Positive: audio started before video (pre-roll to skip in WAV file).
    /// Negative: audio started after video (leading silence on timeline,
    /// e.g. mic permission dialog delayed audio capture).
    /// At video time T, the audio file position is T + this offset.
    /// </summary>
    public double AudioToVideoOffsetSeconds { get; set; }
    public CaptureTargetType CaptureType { get; set; } = CaptureTargetType.Monitor;
}
