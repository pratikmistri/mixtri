using Windows.Foundation;

namespace Mixtri.Core.Capture;

public enum CaptureTargetType
{
    Monitor,
    Window,
    Region
}

/// <summary>
/// Describes what to capture. For <see cref="CaptureTargetType.Region"/> mode the full
/// monitor is captured and <see cref="CropRect"/> defines the sub-region to keep.
/// </summary>
public record CaptureTarget(
    CaptureTargetType Type,
    IntPtr Handle,
    string DisplayName,
    Rect? CropRect = null);
