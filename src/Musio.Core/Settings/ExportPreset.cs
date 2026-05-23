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
    Tall3x4,
    Cinematic21x9,
    Instagram4x5
}

public enum FitMode
{
    /// <summary>Fill the canvas; crop overflow according to <see cref="Project.CropAnchorX"/>/<see cref="Project.CropAnchorY"/>.</summary>
    Cover,
    /// <summary>Show entire source frame; gap filled with the project's background style (letterbox/pillarbox).</summary>
    Contain
}

/// <summary>
/// Controls what the zoom region operates on when zoom is active and the source
/// frame is smaller than the output canvas (Contain mode and/or non-zero padding).
/// </summary>
public enum ZoomScope
{
    /// <summary>Zoom region spans the entire composed canvas (background + source + letterbox/padding zoom together as one unit).</summary>
    Frame,
    /// <summary>Zoom region is constrained to the source frame; letterbox bars and padding remain at a fixed size.</summary>
    Source
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
