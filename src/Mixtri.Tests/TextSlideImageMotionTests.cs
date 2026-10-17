using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

[TestClass]
public class TextSlideImageMotionTests
{
    [TestMethod]
    public async Task SustainedImageMotionSurvivesPresetSwitchesAndRendererRecreation()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        for (int pass = 0; pass < 2; pass++)
        {
            using var renderer = new TextSlideRenderer(device);
            var images = TextSlidePresets.All.Where(p => p.ImageName is not null).ToArray();
            for (int index = 0; index < images.Length; index++)
            {
                var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(30) };
                images[index].Apply(slide);
                await renderer.EnsureBackgroundLoadedAsync(slide);
                for (int frame = 0; frame < 30; frame++)
                {
                    int w = index % 2 == 0 ? 640 : 360, h = index % 2 == 0 ? 360 : 640;
                    using var result = renderer.RenderSlide(slide, frame / 30d, w, h, drawText: false);
                    var center = result.GetPixelColors(w / 2, h / 2, 1, 1)[0];
                    Assert.AreEqual((byte)255, center.A);
                }
            }
        }
    }

    [TestMethod]
    public void RotatingImageCoversViewportAndDisplacementAtEveryAspectRatio()
    {
        foreach (var (width, height) in new[] { (1920, 1080), (1080, 1920), (2560, 720), (1080, 1080) })
        for (int seconds = 0; seconds <= 120; seconds++)
        {
            var transform = TextSlideRenderer.ImageMotionTransform(1672, 941, width, height, seconds);
            Assert.IsTrue(System.Numerics.Matrix3x2.Invert(transform, out var inverse));
            float margin = Math.Min(width, height) * .0425f;
            foreach (float x in new[] { -margin, width + margin })
            foreach (float y in new[] { -margin, height + margin })
            {
                var source = System.Numerics.Vector2.Transform(new(x, y), inverse);
                Assert.IsTrue(source.X >= 0 && source.X <= 1672 && source.Y >= 0 && source.Y <= 941,
                    $"Exposed image edge at {width}x{height}, {seconds}s.");
            }
        }
    }

    [TestMethod]
    public async Task ImageFlowChangesWithPlayheadIsRepeatableAndCoversFrame()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var renderer = new TextSlideRenderer(device);
        foreach (var name in new[] { "image-eclipse", "image-celadon" })
        {
            var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(8) };
            TextSlidePresets.All.Single(p => p.Id == name).Apply(slide);
            await renderer.EnsureBackgroundLoadedAsync(slide);
            using var first = renderer.RenderSlide(slide, 0, 480, 270, drawText: false);
            using var later = renderer.RenderSlide(slide, .5, 480, 270, drawText: false);
            using var repeated = renderer.RenderSlide(slide, .5, 480, 270, drawText: false);
            var a = first.GetPixelBytes();
            var b = later.GetPixelBytes();
            CollectionAssert.AreEqual(b, repeated.GetPixelBytes(), "Paused/scrubbed frames must be deterministic.");
            Assert.IsTrue(a.Where((value, i) => value != b[i]).Count() > a.Length / 20,
                "The image must visibly evolve, not silently fall back to a solid background.");
            for (int i = 3; i < b.Length; i += 4)
                Assert.AreEqual((byte)255, b[i], "Displacement must not reveal transparent edges.");
        }
    }
}
