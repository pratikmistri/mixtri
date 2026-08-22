using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="CursorType.Hidden"/>: the option means "draw nothing at all", which is a
/// stronger statement than the auto-hide fade it sits next to.
/// <para>
/// The reason this needs a rendering test rather than a property test is that
/// <see cref="CursorRenderer.RenderFrame"/> has four independent ways to put marks on a frame —
/// the pointer glyph, the click scale animation, the two motion-blur paths, and the touch
/// indicators — and each one returns early from a different branch. A guard placed anywhere
/// except the very top of the method would suppress some of them and silently leave the rest
/// drawing, which is exactly the sort of gap that only shows up in an exported video.
/// </para>
/// </summary>
/// <remarks>
/// Win2D needs a graphics device; without one every test here reports Inconclusive rather than
/// failing, matching <see cref="ZoomRegionPickerGeometryTests"/>'s environment gate. The canvas
/// is deliberately small — the crash-hardening playbook records that large render targets on the
/// SHARED Win2D device can leave later, unrelated tests composing blank frames.
/// </remarks>
[TestClass]
public sealed class HiddenCursorTests
{
    private const int CanvasW = 320;
    private const int CanvasH = 180;
    private const double TickFrequency = TimeSpan.TicksPerSecond;

    private static readonly Color Background = Color.FromArgb(255, 0, 0, 0);

    private static bool HasDevice()
    {
        try { _ = CanvasDevice.GetSharedDevice(); return true; }
        catch { return false; }
    }

    private static SmoothedPosition Position() => new()
    {
        X = CanvasW / 2.0,
        Y = CanvasH / 2.0,
        TimestampSeconds = 1.0,
        // A non-zero velocity so the tilt and motion-blur paths are live rather than
        // trivially skipped: a hidden cursor must suppress those too.
        VelocityX = 900,
        VelocityY = 450,
        Shape = CursorShape.Arrow,
    };

    /// <summary>A click straddling the rendered instant, so the click animation is active.</summary>
    private static List<ClickEvent> ClickAtOneSecond() =>
    [
        new((long)TickFrequency, CanvasW / 2, CanvasH / 2, MouseButton.Left, IsDown: true),
    ];

    /// <summary>
    /// Renders one frame with <paramref name="style"/> and returns how many pixels differ from
    /// the cleared background.
    /// </summary>
    private static async Task<int> CountDrawnPixelsAsync(CursorStyle style, bool shutterBlur = false)
    {
        var device = CanvasDevice.GetSharedDevice();

        using var renderer = new CursorRenderer(style)
        {
            StartTimestampTicks = 0,
            TickFrequency = TickFrequency,
            OutputFps = 30,
        };
        await renderer.LoadCursorAsync(device);

        using var target = new CanvasRenderTarget(device, CanvasW, CanvasH, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Background);
            renderer.RenderFrame(
                ds,
                Position(),
                ClickAtOneSecond(),
                currentTimeSeconds: 1.0,
                lastMoveTimeSeconds: 1.0,
                motionBlur: shutterBlur
                    ? new MotionBlurSettings { Enabled = true, CursorStrength = 1f }
                    : null);
        }

        int drawn = 0;
        foreach (var pixel in target.GetPixelColors())
        {
            if (pixel.R != Background.R || pixel.G != Background.G || pixel.B != Background.B)
                drawn++;
        }
        return drawn;
    }

    [TestMethod]
    public async Task Hidden_DrawsNothing()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        int drawn = await CountDrawnPixelsAsync(new CursorStyle { Type = CursorType.Hidden });

        Assert.AreEqual(0, drawn,
            "a hidden cursor must leave the frame untouched — no glyph and no click animation");
    }

    [TestMethod]
    public async Task Hidden_DrawsNothing_EvenWithShutterMotionBlur()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        int drawn = await CountDrawnPixelsAsync(
            new CursorStyle { Type = CursorType.Hidden }, shutterBlur: true);

        Assert.AreEqual(0, drawn,
            "the shutter blur path must not run for a hidden cursor");
    }

    [TestMethod]
    public async Task Hidden_DrawsNothing_EvenWithLegacyGhostMotionBlur()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        int drawn = await CountDrawnPixelsAsync(new CursorStyle
        {
            Type = CursorType.Hidden,
            MotionBlurEnabled = true,
            MotionBlurStrength = 1f,
        });

        Assert.AreEqual(0, drawn, "the legacy ghost-trail path must not run for a hidden cursor");
    }

    /// <summary>
    /// The control for the three tests above: without it, a bug that made the renderer draw
    /// nothing in ALL modes would leave them passing. Touch is chosen because it reaches the
    /// frame through the click list rather than the cursor position, so it also proves the
    /// click-driven branch is one the Hidden guard has to cover.
    /// </summary>
    [TestMethod]
    public async Task Touch_DrawsSomething_SoTheHiddenAssertionsAreMeaningful()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        int drawn = await CountDrawnPixelsAsync(new CursorStyle { Type = CursorType.Touch });

        Assert.IsTrue(drawn > 0, "the touch indicator should have marked the frame");
    }

    [TestMethod]
    public async Task Default_DrawsSomething_SoTheHiddenAssertionsAreMeaningful()
    {
        if (!HasDevice()) { Assert.Inconclusive("No Win2D device available."); return; }

        int drawn = await CountDrawnPixelsAsync(new CursorStyle { Type = CursorType.Default });

        Assert.IsTrue(drawn > 0, "the pointer glyph should have marked the frame");
    }
}
