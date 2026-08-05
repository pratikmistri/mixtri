using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;

namespace Musio.Tests;

/// <summary>
/// Investigation harness for the "preview degrades into garbage rectangles after some load"
/// report. Drives <see cref="FrameCompositor"/> the way sustained preview playback does — many
/// sequential frames, then rebuilt at a different source size, which is what adaptive preview
/// quality does and is the moment the reported corruption appeared.
/// Set MUSIO_PROBE_VIDEO to a real .mp4 to run; skips otherwise.
/// </summary>
[TestClass]
public class PreviewSustainedRenderProbeTests
{
    private static string? ProbeVideo =>
        Environment.GetEnvironmentVariable("MUSIO_PROBE_VIDEO") is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static CompositionConfig Config() => new()
    {
        OutputFps = 30,
        Background = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = "#1a1a2e" },
    };

    /// <summary>
    /// Fraction of near-pure-white pixels. The corrupted frames in the report were dominated by
    /// flat white with hard-edged blocks, so this is a cheap degeneracy signal.
    /// </summary>
    private static double WhiteFraction(CanvasRenderTarget rt)
    {
        var px = rt.GetPixelColors();
        int white = 0;
        foreach (var c in px)
            if (c.R > 250 && c.G > 250 && c.B > 250) white++;
        return (double)white / px.Length;
    }

    [TestMethod]
    public async Task SustainedPlayback_AcrossCompositorRebuilds_DoesNotDegrade()
    {
        var video = ProbeVideo;
        if (video is null) { Assert.Inconclusive("Set MUSIO_PROBE_VIDEO to a real .mp4 to run."); return; }

        try { _ = CanvasDevice.GetSharedDevice(); }
        catch (Exception ex) { Assert.Inconclusive($"No Win2D device: {ex.Message}"); return; }

        var problems = new List<string>();

        // Three compositor generations at different source scales, mimicking adaptive preview
        // quality stepping down under load and back up, rebuilding a compositor each time
        // exactly as the editor does.
        foreach (var (scale, label) in new[] { (1.0, "full"), (0.66, "reduced"), (1.0, "full-again") })
        {
            var reader = await VideoFrameReader.OpenFromVideoPathAsync(video, 30);
            if (reader is null) { Assert.Inconclusive($"Could not open {video}"); return; }

            using (reader)
            {
                using var probe = await reader.LoadFrameAsync(0);
                if (probe is null) { Assert.Inconclusive("Could not decode frame 0"); return; }

                int srcW = Math.Max(2, (int)(probe.SizeInPixels.Width * scale));
                int srcH = Math.Max(2, (int)(probe.SizeInPixels.Height * scale));

                var mouse = new MouseRecordingData { TickFrequency = TimeSpan.TicksPerSecond };
                using var compositor = new FrameCompositor(Config());
                await compositor.InitializeAsync(mouse, srcW, srcH, duration: reader.Duration);

                int frames = Math.Min(60, reader.FrameCount);
                for (int i = 0; i < frames; i++)
                {
                    using var bmp = await reader.LoadFrameAsync(i);
                    if (bmp is null) continue;

                    CanvasRenderTarget outFrame;
                    try { outFrame = compositor.ComposeFrame(bmp, i); }
                    catch (Exception ex)
                    {
                        problems.Add($"{label} frame {i}: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }

                    using (outFrame)
                    {
                        if (i % 20 != 0) continue;
                        double white = WhiteFraction(outFrame);
                        Console.WriteLine(
                            $"{label,-11} frame {i,3}: " +
                            $"{outFrame.SizeInPixels.Width}x{outFrame.SizeInPixels.Height} white={white:P1}");
                        if (white > 0.80)
                            problems.Add($"{label} frame {i}: {white:P1} pure white — degenerate output");
                    }
                }
            }
        }

        Assert.AreEqual(0, problems.Count,
            "Sustained render degraded:\n  " + string.Join("\n  ", problems));
    }
}
