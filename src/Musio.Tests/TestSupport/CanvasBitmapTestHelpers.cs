using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace Musio.Tests.TestSupport;

/// <summary>
/// The exact duplicate <c>MakeBitmap(CanvasDevice, Color, int, int)</c> helper from
/// <c>TransitionRendererTests.cs</c> and <c>TransitionRendererHardwareTests.cs</c>.
/// </summary>
internal static class CanvasBitmapTestHelpers
{
    public static CanvasBitmap MakeBitmap(CanvasDevice device, Color color, int w, int h)
    {
        var colors = new Color[w * h];
        Array.Fill(colors, color);
        return CanvasBitmap.CreateFromColors(device, colors, w, h);
    }
}
