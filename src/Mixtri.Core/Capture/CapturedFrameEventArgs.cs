using Windows.Graphics.DirectX.Direct3D11;

namespace Mixtri.Core.Capture;

/// <summary>
/// A single accepted capture frame, delivered on the free-threaded frame-pool callback.
/// </summary>
/// <remarks>
/// <see cref="Surface"/> belongs to the capture frame pool and is recycled as soon as the
/// handler returns. Handlers must copy anything they need into their own resource before
/// returning, and must never retain, queue or reuse the surface afterwards.
/// </remarks>
public class CapturedFrameEventArgs : EventArgs
{
    /// <summary>
    /// Borrowed capture surface. Valid only for the duration of the event handler.
    /// </summary>
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
