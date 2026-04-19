namespace Musio.Core.Settings;

public enum VideoResolution
{
    HD720,
    HD1080,
    QHD,
    UHD4K
}

public enum VideoFormat
{
    MP4,
    GIF,
    WebM
}

public enum AspectRatio
{
    Auto,
    Landscape16x9,
    Portrait9x16,
    Square1x1,
    Classic4x3,
    Tall3x4
}

public enum VideoQuality
{
    Draft,
    Standard,
    High,
    Ultra
}

/// <summary>
/// Defines export settings for a recording.
/// </summary>
public class ExportPreset
{
    public string Name { get; set; } = string.Empty;
    public VideoResolution Resolution { get; set; } = VideoResolution.HD1080;
    public int Fps { get; set; } = 30;
    public VideoFormat Format { get; set; } = VideoFormat.MP4;
    public AspectRatio AspectRatio { get; set; } = AspectRatio.Auto;
    public VideoQuality Quality { get; set; } = VideoQuality.High;
}
