using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class AnimatedBlurCacheTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Color ClearColor = Color.FromArgb(255, 20, 35, 50);

    [TestMethod]
    public void CachedEffectPreservesPixelsAcrossPassesParametersAndResizes()
    {
        using var engine = new AnimatedTextEngine();
        using var previous = new PreviousBlur();
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 24,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        var draw = typeof(AnimatedTextEngine).GetMethod("DrawBlurredText", Fields)!
            .CreateDelegate<Action<CanvasDrawingSession, string, CanvasTextFormat, Rect, Color,
                float, float, float, float, int, int>>(engine);
        foreach (var (width, height) in new[] { (320, 180), (480, 270), (160, 120), (320, 180) })
        {
            using var expected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            using var actual = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
            for (int frame = 0; frame < 4; frame++)
            {
                using (var oldSession = expected.CreateDrawingSession())
                using (var newSession = actual.CreateDrawingSession())
                {
                    oldSession.Clear(ClearColor);
                    newSession.Clear(ClearColor);
                    for (int pass = 0; pass < 10; pass++)
                    {
                        var rect = new Rect(10 + pass * .4, 15 + pass * .25, width - 20, height - 30);
                        var color = Color.FromArgb((byte)(70 + pass * 15), (byte)(240 - pass * 10), 150, 90);
                        float amount = .5f + frame + pass * .7f;
                        float scale = .85f + pass * .025f;
                        string text = pass % 2 == 0 ? "Blurred title" : "Second pass";
                        previous.Draw(oldSession, text, format, rect, color, amount, scale, width / 2f, height / 2f, width, height);
                        draw(newSession, text, format, rect, color, amount, scale, width / 2f, height / 2f, width, height);
                    }
                }
                CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(),
                    $"{width}x{height}, frame {frame}");
            }
        }
        Assert.AreEqual(4, engine.BlurEffectBuildCount);
    }

    [TestMethod]
    public void OutlinePassesAndRepeatedFramesReuseOneEffect()
    {
        using var renderer = new TextOverlayRenderer();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        List<TextOverlaySegment> overlays =
        [
            new()
            {
                Text = "Animated outline", Duration = TimeSpan.FromSeconds(4),
                Animation = TextSlideAnimation.ZoomBlurIn, Background = TextOverlayBackground.OutlineShadow,
            },
        ];
        for (int frame = 0; frame < 30; frame++)
        {
            using (var ds = target.CreateDrawingSession()) ds.Clear(ClearColor);
            renderer.Render(target, overlays, TimeSpan.FromSeconds(.05 + frame * .01), 320, 180);
        }
        var engine = (AnimatedTextEngine)typeof(TextOverlayRenderer).GetField("_textEngine", Fields)!.GetValue(renderer)!;
        Assert.AreEqual(1, engine.BlurEffectBuildCount);
    }

    [TestMethod]
    public void NonBlurredTextDoesNotCreateBlurResources()
    {
        using var engine = new AnimatedTextEngine();
        using var format = new CanvasTextFormat { FontSize = 24 };
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        using var ds = target.CreateDrawingSession();
        engine.DrawAnimatedText(ds, "Plain", format, new Rect(0, 0, 320, 180), ClearColor,
            TextSlideAnimation.None, .5, 320, 180, 24, 4);
        Assert.AreEqual(0, engine.BlurEffectBuildCount);
        Assert.IsNull(typeof(AnimatedTextEngine).GetField("_blurScratch", Fields)!.GetValue(engine));
    }

    [TestMethod]
    public void DisposalDropsTheEffectAndScratchReferences()
    {
        var engine = new AnimatedTextEngine();
        using var format = new CanvasTextFormat { FontSize = 24 };
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        using (var ds = target.CreateDrawingSession())
            engine.DrawAnimatedText(ds, "Blur", format, new Rect(0, 0, 320, 180), ClearColor,
                TextSlideAnimation.ZoomBlurIn, .05, 320, 180, 24, 4);
        Assert.AreEqual(1, engine.BlurEffectBuildCount);
        var scratch = (CanvasRenderTarget)typeof(AnimatedTextEngine).GetField("_blurScratch", Fields)!.GetValue(engine)!;
        engine.Dispose();
        engine.Dispose();
        AssertClosed(() => _ = scratch.SizeInPixels);
        Assert.IsNull(typeof(AnimatedTextEngine).GetField("_blurEffect", Fields)!.GetValue(engine));
        Assert.IsNull(typeof(AnimatedTextEngine).GetField("_blurScratch", Fields)!.GetValue(engine));
        using var drawing = target.CreateDrawingSession();
        Assert.ThrowsException<ObjectDisposedException>(() =>
            engine.DrawAnimatedText(drawing, "Blur", format, new Rect(0, 0, 320, 180), ClearColor,
                TextSlideAnimation.ZoomBlurIn, .05, 320, 180, 24, 4));
    }

    private static void AssertClosed(Action read)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return; }
        Assert.Fail("The native blur resource was not closed.");
    }

    private sealed class PreviousBlur : IDisposable
    {
        private CanvasRenderTarget? _scratch;
        private (int Width, int Height) _size;

        public void Draw(CanvasDrawingSession session, string text, CanvasTextFormat format, Rect rect,
            Color color, float blurAmount, float scale, float cx, float cy, int width, int height)
        {
            if (_scratch is null || _size != (width, height))
            {
                var next = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
                _scratch?.Dispose();
                _scratch = next;
                _size = (width, height);
            }
            using (var drawing = _scratch.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.DrawText(text, rect, color, format);
            }
            using var blur = new GaussianBlurEffect
            {
                Source = _scratch, BlurAmount = blurAmount, BorderMode = EffectBorderMode.Soft,
            };
            var saved = session.Transform;
            session.Transform = Matrix3x2.CreateScale(scale, new Vector2(cx, cy));
            session.DrawImage(blur);
            session.Transform = saved;
        }

        public void Dispose() => _scratch?.Dispose();
    }
}
