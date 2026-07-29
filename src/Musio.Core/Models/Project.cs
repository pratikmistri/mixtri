using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio.Core.Models;

/// <summary>
/// Represents a single recording's media files and metadata.
/// Used when a project contains multiple recordings (appended recordings).
/// </summary>
public class RecordingSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VideoFilePath { get; set; } = string.Empty;
    public string CursorDataFilePath { get; set; } = string.Empty;
    public string? WebcamFilePath { get; set; }
    public string? KeyboardDataFilePath { get; set; }
    public List<string> AudioFilePaths { get; set; } = [];
    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; } = 30;
    public double MouseToVideoOffsetSeconds { get; set; }
    public double AudioToVideoOffsetSeconds { get; set; }
    public int CropOffsetX { get; set; }
    public int CropOffsetY { get; set; }
    public float DpiScale { get; set; }
    public CaptureTargetType CaptureType { get; set; } = CaptureTargetType.Monitor;
}

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
    /// Additional recordings appended to this project.
    /// The primary recording is represented by the top-level properties.
    /// </summary>
    public List<RecordingSource> Sources { get; set; } = [];

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

    /// <summary>
    /// Canvas aspect ratio for preview and export. <see cref="AspectRatio.Auto"/>
    /// matches the captured source. Drives both editor preview and exported video dimensions.
    /// </summary>
    public AspectRatio AspectRatio { get; set; } = AspectRatio.Auto;

    /// <summary>
    /// How the captured source frame fits into the canvas when <see cref="AspectRatio"/>
    /// differs from the source aspect ratio. Ignored when <see cref="AspectRatio"/> is Auto.
    /// </summary>
    public FitMode FitMode { get; set; } = FitMode.Contain;

    /// <summary>
    /// Horizontal crop anchor in 0..1 when <see cref="FitMode"/> is Cover. 0 = left edge,
    /// 0.5 = center, 1 = right edge. Selects which portion of the source frame is visible
    /// when the source is wider than the canvas.
    /// </summary>
    public double CropAnchorX { get; set; } = 0.5;

    /// <summary>
    /// Vertical crop anchor in 0..1 when <see cref="FitMode"/> is Cover. 0 = top edge,
    /// 0.5 = center, 1 = bottom edge.
    /// </summary>
    public double CropAnchorY { get; set; } = 0.5;

    /// <summary>
    /// Whether zoom operates on the entire composed canvas (Frame) or only the
    /// captured source frame within it (Source). Only matters when the source
    /// area is smaller than the canvas (Contain mode or non-zero padding).
    /// </summary>
    public ZoomScope ZoomScope { get; set; } = ZoomScope.Frame;
}
