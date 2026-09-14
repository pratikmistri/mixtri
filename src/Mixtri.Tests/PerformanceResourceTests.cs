using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Mixtri.Core.Audio;
using Mixtri.Core.Capture;
using Mixtri.Core.Export;
using Mixtri.Core.Projects;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.Foundation;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class PerformanceResourceTests
{
    private static readonly Color Ink = Color.FromArgb(255, 120, 180, 220);

    private static CanvasRenderTarget Frame(int width = 320, int height = 180)
    {
        var frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using var ds = frame.CreateDrawingSession();
        ds.Clear(Ink);
        return frame;
    }

    private sealed class Source : IFrameSource
    {
        public int FrameCount => 100;
        public int Width => 320;
        public int Height => 180;
        public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;
        public int Loads { get; private set; }
        public bool Disposed { get; private set; }
        public Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
        {
            Loads++;
            return Task.FromResult<CanvasBitmap?>(Frame());
        }
        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public async Task FrameLease_SurvivesEvictionAndReaderDisposal_WithoutCopying()
    {
        var source = new Source();
        using var reader = new VideoFrameReader(source, 30, cacheCapacity: 2);
        using var first = await reader.AcquireFrameAsync(0);
        using var again = await reader.AcquireFrameAsync(0);
        Assert.IsNotNull(first);
        Assert.IsNotNull(again);
        Assert.IsTrue(first.Bitmap == again.Bitmap);
        Assert.AreEqual(1, source.Loads);
        using var second = await reader.AcquireFrameAsync(1);
        using var third = await reader.AcquireFrameAsync(2);
        reader.Dispose();
        Assert.IsTrue(source.Disposed);
        Assert.AreEqual(Ink, first.Bitmap.GetPixelColors()[0]);
        first.Dispose();
        first.Dispose();
        Assert.AreEqual(Ink, again.Bitmap.GetPixelColors()[0]);
    }

    [TestMethod]
    public async Task FrameLease_OversizeEntryIsStillUsable_AndLegacyCopiesStayIndependent()
    {
        var source = new Source();
        using var reader = new VideoFrameReader(source, 30, cacheBudgetBytes: 1);
        using var lease = await reader.AcquireFrameAsync(0);
        using var copy = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(lease);
        Assert.IsNotNull(copy);
        Assert.IsFalse(lease.Bitmap == copy);
        copy.Dispose();
        Assert.AreEqual(Ink, lease.Bitmap.GetPixelColors()[0]);
        Assert.AreEqual(1, source.Loads);
    }

    [TestMethod]
    public void Webcam_EqualStylesReuseCache_AndShadowDoesNotSpanCanvas()
    {
        var style = new WebcamOverlayStyle { Size = 100, Position = WebcamPosition.BottomRight };
        using var renderer = new WebcamCompositor(style);
        using var source = Frame();
        using var output = Frame(640, 360);
        byte[]? first = null;
        for (int i = 0; i < 20; i++)
        {
            renderer.UpdateStyle(style with { });
            using (var ds = output.CreateDrawingSession())
            {
                ds.Clear(Ink);
                renderer.RenderWebcam(ds, source, 640, 360);
            }
            first ??= output.GetPixelBytes();
        }
        Assert.AreEqual(1, renderer.CacheBuildCount);
        Assert.IsTrue(renderer.ShadowSize.Width <= 154);
        Assert.IsTrue(renderer.ShadowSize.Height <= 154);
        CollectionAssert.AreEqual(first, output.GetPixelBytes());
        renderer.UpdateStyle(style with { Size = 120 });
        using (var ds = output.CreateDrawingSession()) renderer.RenderWebcam(ds, source, 640, 360);
        Assert.AreEqual(2, renderer.CacheBuildCount);
    }

    [TestMethod]
    [DataRow(0f)]
    [DataRow(0.5f)]
    [DataRow(1f)]
    public void Webcam_LocalShadowMatchesOriginalCanvasOriginRendering(float fullscreen)
    {
        var style = new WebcamOverlayStyle { Size = 100, BorderWidth = 0 };
        using var source = Frame();
        using var actual = Frame(640, 360);
        using var expected = Frame(640, 360);
        using var renderer = new WebcamCompositor(style);
        renderer.SetFullscreenFactor(fullscreen);
        using (var ds = actual.CreateDrawingSession()) renderer.RenderWebcam(ds, source, 640, 360);

        var layout = WebcamLayoutCalculator.ComputeAnimatedLayout(style, 640, 360, 320, 180, fullscreen);
        var dest = layout.Destination;
        using var geometry = CanvasGeometry.CreateRoundedRectangle(
            expected.Device, dest, layout.CornerRadius, layout.CornerRadius);
        if (layout.ShadowAlpha > 0)
        {
            const float pad = 9;
            float w = Math.Max((float)dest.Right + pad + 16, (float)dest.Width + pad * 2);
            float h = Math.Max((float)dest.Bottom + pad + 20, (float)dest.Height + pad * 2 + 4);
            using var shadow = new CanvasRenderTarget(expected.Device, w, h, 96);
            using var mask = new CanvasCommandList(expected.Device);
            using (var ds = mask.CreateDrawingSession())
                ds.FillGeometry(geometry, Color.FromArgb(128, 0, 0, 0));
            using var effect = new ShadowEffect
            {
                Source = mask, BlurAmount = 8, ShadowColor = Color.FromArgb(100, 0, 0, 0)
            };
            using (var ds = shadow.CreateDrawingSession())
            {
                ds.Clear(default(Color));
                ds.DrawImage(effect);
            }
            using var output = expected.CreateDrawingSession();
            output.DrawImage(shadow, new Vector2(0, 4), shadow.Bounds, layout.ShadowAlpha);
        }
        using (var ds = expected.CreateDrawingSession())
        using (ds.CreateLayer(1f, geometry))
            ds.DrawImage(source, dest, layout.SourceCrop);
        AssertVisuallyIdentical(expected, actual, 640);
    }

    /// <summary>
    /// Compares two renderings that were composed through DIFFERENT intermediate surfaces.
    /// </summary>
    /// <remarks>
    /// Bit-exact equality is the wrong assertion here. The optimized path blurs the shadow in a
    /// small surface local to the overlay, the reference blurs it in a canvas-origin surface of
    /// a different size, and Direct2D does not promise identical rounding for the same blur
    /// evaluated over different extents. A hardware GPU happens to agree; the WARP software
    /// rasterizer used on CI rounds a handful of alpha-blended pixels differently by exactly 1.
    ///
    /// The invariant that actually matters is that the optimization is VISUALLY identical and,
    /// above all, that the shadow is never clipped by the smaller surface. Both are still
    /// enforced: a clipped shadow removes up to the full shadow alpha over a contiguous band,
    /// which blows past both bounds below by orders of magnitude.
    /// </remarks>
    private static void AssertVisuallyIdentical(
        CanvasRenderTarget expected, CanvasRenderTarget actual, int width)
    {
        var e = expected.GetPixelBytes();
        var a = actual.GetPixelBytes();
        Assert.AreEqual(e.Length, a.Length, "renderings differ in size");

        int differing = 0, maxDelta = 0, worstIndex = -1;
        for (int i = 0; i < e.Length; i++)
        {
            int delta = Math.Abs(e[i] - a[i]);
            if (delta == 0) continue;
            differing++;
            if (delta > maxDelta) { maxDelta = delta; worstIndex = i; }
        }

        if (maxDelta > 1)
        {
            int pixel = worstIndex / 4;
            Assert.Fail($"channel delta {maxDelta} at ({pixel % width},{pixel / width}) exceeds rounding "
                + "tolerance — the local shadow surface is clipping or displacing the blur, not just rounding it");
        }

        // Rounding touches a few edge pixels; a geometry or origin error touches a whole region.
        double fraction = differing / (double)e.Length;
        Assert.IsTrue(fraction < 0.005,
            $"{differing} of {e.Length} channels differ ({fraction:P3}); rounding alone cannot explain that");
    }

    [TestMethod]
    public void Background_WarmResourcesMatchColdRendering_AndInvalidateOnStyleChange()
    {
        using var source = Frame();
        using var output = Frame(400, 240);
        using var cached = new BackgroundCompositor();
        var style = new BackgroundStyle { Type = BackgroundType.Gradient };
        for (int i = 0; i < 4; i++)
        {
            if (i == 2) style = style with { ShadowOpacity = 0.8, CornerRadius = 18, Color = "#334455" };
            using (var ds = output.CreateDrawingSession())
                cached.CompositeFrame(ds, source, 400, 240, 40, 30, 320, 180, style);
            using var expected = Frame(400, 240);
            using var cold = new BackgroundCompositor();
            using (var ds = expected.CreateDrawingSession())
                cold.CompositeFrame(ds, source, 400, 240, 40, 30, 320, 180, style);
            CollectionAssert.AreEqual(expected.GetPixelBytes(), output.GetPixelBytes());
        }
        Assert.AreEqual(2, cached.DecorationBuildCount);
    }

    [TestMethod]
    public void CharacterLayout_IsReusedAcrossAnimationFrames_AndRespondsToFormatChanges()
    {
        using var renderer = new AnimatedTextEngine();
        using var format = AnimatedTextEngine.CreateFormat("Segoe UI", 24, false, false,
            CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center, true);
        using var output = Frame();
        for (int i = 0; i < 6; i++)
        {
            if (i == 3) format.FontSize = 28;
            double progress = 0.2 + i * 0.08;
            var rect = new Rect(0, 0, 320, 180);
            using (var ds = output.CreateDrawingSession())
            {
                ds.Clear(Ink);
                renderer.DrawAnimatedText(ds, "Same text", format, rect, Color.FromArgb(255, 255, 255, 255),
                    TextSlideAnimation.Wave, progress, 320, 180, format.FontSize, 3);
            }
            using var expected = Frame();
            using var cold = new AnimatedTextEngine();
            using (var ds = expected.CreateDrawingSession())
                cold.DrawAnimatedText(ds, "Same text", format, rect, Color.FromArgb(255, 255, 255, 255),
                    TextSlideAnimation.Wave, progress, 320, 180, format.FontSize, 3);
            CollectionAssert.AreEqual(expected.GetPixelBytes(), output.GetPixelBytes());
        }
        Assert.AreEqual(2, renderer.CharacterLayoutBuildCount);
    }

    [TestMethod]
    public async Task UnchangedEditState_DoesNotRebuildCursor_AndMutableInputsAreSnapshotted()
    {
        using var compositor = new FrameCompositor(new CompositionConfig());
        var mouse = TestMouseRecordingBuilder.WithPositions(100, 100, i => (100 + i, 80));
        await compositor.InitializeAsync(mouse, 320, 180, TimeSpan.FromSeconds(1));
        var anchors = new List<CursorAnchor>
        {
            new() { Timestamp = TimeSpan.FromSeconds(0.5), X = 0.2, Y = 0.4 }
        };
        compositor.SyncCursorAnchors(anchors);
        int builds = compositor.CursorPathBuildCount;
        for (int i = 0; i < 20; i++)
        {
            compositor.SyncCursorAnchors(anchors);
            compositor.SyncDisabledClickTicks([]);
            compositor.SyncSuppressedClickTicks([]);
        }
        Assert.AreEqual(builds, compositor.CursorPathBuildCount);
        anchors[0] = anchors[0] with { X = 0.8 };
        compositor.SyncCursorAnchors(anchors);
        Assert.AreEqual(builds + 1, compositor.CursorPathBuildCount);
    }

    [TestMethod]
    public void ZoomState_UnchangedInputsDoNotRebuild_ButMutatedSetsDo()
    {
        var engine = new AutoZoomEngine(new AutoZoomConfig());
        var mouse = TestMouseRecordingBuilder.WithPositions(100, 100, i => (100 + i, 80));
        engine.BuildZoomTimeline(mouse, 320, 180, mouse.TickFrequency);
        var keys = new List<ZoomKeyframe> { new() { Timestamp = TimeSpan.FromSeconds(0.5), IsManual = true } };
        engine.SetManualKeyframes(keys);
        var ticks = new HashSet<long> { 4 };
        engine.SetSuppressedClickTicks(ticks);
        int builds = engine.PathBuildCount;
        engine.SetManualKeyframes(keys);
        engine.SetSuppressedClickTicks(ticks);
        Assert.AreEqual(builds, engine.PathBuildCount);
        ticks.Add(5);
        engine.SetSuppressedClickTicks(ticks);
        Assert.AreEqual(builds + 1, engine.PathBuildCount);
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(1.7)]
    [DataRow(4.0)]
    public void Wsola_CompactsAtBlockBoundaries_NotEveryGrain(double speed)
    {
        const int blockSize = 16000;
        var input = Enumerable.Range(0, 64000).Select(i => (float)Math.Sin(i * 0.07)).ToArray();
        var expected = WsolaTimeStretcher.Stretch(input, 2, 16000, speed);
        var output = new List<float>();
        void Write(ReadOnlySpan<float> samples)
        {
            foreach (float sample in samples) output.Add(sample);
        }
        var stretcher = new WsolaTimeStretcher(16000, 2, speed);
        for (int i = 0; i < input.Length; i += blockSize)
            stretcher.Process(input.AsSpan(i, blockSize), Write);
        stretcher.Flush(Write);
        CollectionAssert.AreEqual(expected, output.ToArray());
        Assert.IsTrue(stretcher.CompactionCount <= input.Length / blockSize);
    }

    [TestMethod]
    public async Task ExportContexts_RetireAfterLastUse_AndReopenForBackwardRequests()
    {
        using var directory = new TempDirectoryFixture("mixtri_perf_");
        string video = Path.Combine(directory.Path, "video.mp4");
        using (var writer = new VideoWriter(video, 1920, 1080, 10))
        using (var frame = Frame(1920, 1080))
        {
            for (int i = 0; i < 20; i++)
                writer.WriteFrame(frame, TimeSpan.FromSeconds(i / 10.0));
            await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await writer.FinalizeAsync();
        }

        var project = new Project
        {
            Name = "Performance fixture", VideoFilePath = video, Width = 1920, Height = 1080,
            Fps = 10, Duration = TimeSpan.FromSeconds(2)
        };
        var timeline = new TimelineModel { PrimaryVideoFilePath = video, Fps = 10 };
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video, Duration = TimeSpan.FromSeconds(1), SourceDuration = TimeSpan.FromSeconds(1),
            SourceWidth = 1920, SourceHeight = 1080, Fps = 10
        });
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video, Duration = TimeSpan.FromSeconds(1), SourceDuration = TimeSpan.FromSeconds(1),
            SourceStart = TimeSpan.FromSeconds(1), SourceWidth = 1920, SourceHeight = 1080, Fps = 10,
            FrameStyleOverride = new BackgroundStyle { Padding = 24, Color = "#445566" }
        });
        timeline.Segments.Add(new TextSlideSegment { Text = "Performance", Duration = TimeSpan.FromSeconds(2) });
        timeline.RecalculateSegmentPositions();
        var config = new CompositionConfig { OutputFps = 10 };
        using var composer = await SegmentFrameComposer.CreateAsync(project, new MouseRecordingData(),
            config, timeline, new TimelineMapper(timeline, 10), 10);
        using (var first = await composer.ComposeFrameAsync(0)) Assert.AreEqual(1920u, first.SizeInPixels.Width);
        using (var second = await composer.ComposeFrameAsync(12)) Assert.IsNotNull(second);
        Assert.AreEqual(2, composer.ActiveContextCount);
        using (var slide = await composer.ComposeFrameAsync(32)) Assert.IsNotNull(slide);
        Assert.AreEqual(0, composer.ActiveContextCount);
        using (var repeated = await composer.ComposeFrameAsync(0)) Assert.AreEqual(1920u, repeated.SizeInPixels.Width);
        Assert.AreEqual(1, composer.ActiveContextCount);

        // Optional reusable fixture for exercising the real packaged editor's hide/restore lifecycle.
        if (Environment.GetEnvironmentVariable("MIXTRI_PERF_FIXTURE") is { Length: > 0 } fixture)
            await MixtriPackageService.SaveAsync(fixture, project, config, timeline);
    }
}
