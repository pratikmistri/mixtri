namespace Musio.Tests;

using Microsoft.Graphics.Canvas;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Windows.UI;

/// <summary>
/// Renders every <see cref="TransitionType"/> on the <b>hardware</b> shared device.
/// </summary>
/// <remarks>
/// <see cref="TransitionRendererTests"/> deliberately builds its device with
/// <c>forceSoftwareRenderer: true</c> so its pixel assertions are deterministic across
/// machines. The app, however, renders on <see cref="CanvasDevice.GetSharedDevice"/> — the
/// same device this fixture uses — so a failure that only reproduces on a real GPU (an
/// effect a driver refuses, a composite mode the WARP rasteriser tolerates but hardware does
/// not) would otherwise be invisible to the entire suite. This fixture exists purely to close
/// that gap; it asserts only that rendering succeeds, leaving pixel-level correctness to the
/// deterministic software-renderer tests.
/// <para>
/// Sizes deliberately mismatch the output and each other, mirroring the editor: text slides
/// compose at project resolution while video frames compose at their source resolution.
/// </para>
/// </remarks>
[TestClass]
public sealed class TransitionRendererHardwareTests
{
    private static CanvasBitmap MakeBitmap(CanvasDevice device, Color color, int w, int h)
    {
        var colors = new Color[w * h];
        Array.Fill(colors, color);
        return CanvasBitmap.CreateFromColors(device, colors, w, h);
    }

    [TestMethod]
    public void Render_EveryTransitionType_SucceedsOnSharedHardwareDevice()
    {
        CanvasDevice device;
        try
        {
            device = CanvasDevice.GetSharedDevice();
        }
        catch (Exception ex)
        {
            // Headless CI agents legitimately have no shared device; the software-renderer
            // fixture still covers behaviour there.
            Assert.Inconclusive($"Shared Win2D hardware device unavailable: {ex.Message}");
            return;
        }

        var failures = new List<string>();

        using var renderer = new TransitionRenderer(device);
        using var incoming = MakeBitmap(device, Color.FromArgb(255, 0, 200, 0), 1920, 1080);
        using var outgoing = MakeBitmap(device, Color.FromArgb(255, 200, 0, 0), 1280, 720);

        foreach (var type in Enum.GetValues<TransitionType>())
        {
            foreach (double progress in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                foreach (var outgoingFrame in new[] { outgoing, null })
                {
                    try
                    {
                        using var target = renderer.Render(
                            outgoingFrame, incoming, type, progress, 1280, 720);

                        if (target.SizeInPixels.Width != 1280 || target.SizeInPixels.Height != 720)
                        {
                            failures.Add($"{type} @ {progress}: unexpected target size " +
                                $"{target.SizeInPixels.Width}x{target.SizeInPixels.Height}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(
                            $"{type} @ {progress} " +
                            $"(outgoing={(outgoingFrame is null ? "null" : "present")}): " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        Assert.AreEqual(0, failures.Count,
            "Hardware-device render failures:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Distinct()));
    }
}
