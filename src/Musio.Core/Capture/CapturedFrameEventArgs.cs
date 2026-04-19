using Windows.Graphics.DirectX.Direct3D11;

namespace Musio.Core.Capture;

public class CapturedFrameEventArgs : EventArgs
{
    public IDirect3DSurface Surface { get; }
    public TimeSpan Timestamp { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Number of frame slots that were missed between the previous emitted frame
    /// and this one. Zero means no gap. Used to fill CFR gaps with duplicate frames.
    /// </summary>
    public int SkippedSlots { get; }

    public CapturedFrameEventArgs(IDirect3DSurface surface, TimeSpan timestamp, int width, int height, int skippedSlots = 0)
    {
        Surface = surface;
        Timestamp = timestamp;
        Width = width;
        Height = height;
        SkippedSlots = skippedSlots;
    }
}
