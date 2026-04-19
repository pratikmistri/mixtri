using Windows.Graphics.DirectX.Direct3D11;

namespace Musio.Core.Capture;

public class CapturedFrameEventArgs : EventArgs
{
    public IDirect3DSurface Surface { get; }
    public TimeSpan Timestamp { get; }
    public int Width { get; }
    public int Height { get; }

    public CapturedFrameEventArgs(IDirect3DSurface surface, TimeSpan timestamp, int width, int height)
    {
        Surface = surface;
        Timestamp = timestamp;
        Width = width;
        Height = height;
    }
}
