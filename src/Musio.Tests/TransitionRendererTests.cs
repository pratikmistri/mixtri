namespace Musio.Tests;

using Microsoft.Graphics.Canvas;
using Musio.Core.Processing;
using Musio.Core.Timeline;
using Windows.UI;

/// <summary>
/// T4: covers every <see cref="TransitionType"/> member end-to-end through
/// <see cref="TransitionRenderer.Render"/> (GPU smoke tests, following the
/// <c>new CanvasDevice(forceSoftwareRenderer: true)</c> + <c>Assert.Inconclusive</c> fallback
/// pattern already established by <c>BackgroundCompositorPreloadTests</c>/<c>CursorShapeTests</c>),
/// plus plain unit tests for the pure geometry/intensity arithmetic extracted onto
/// <see cref="TransitionRenderer"/> as <c>internal static</c> helpers (no GPU device needed).
/// </summary>
[TestClass]
public sealed class TransitionRendererTests
{
    private static CanvasDevice? TryCreateDevice()
    {
        try
        {
            return new CanvasDevice(forceSoftwareRenderer: true);
        }
        catch
        {
            return null;
        }
    }

    private static CanvasBitmap MakeBitmap(CanvasDevice device, Color color, int w = 8, int h = 8)
    {
        var colors = new Color[w * h];
        Array.Fill(colors, color);
        return CanvasBitmap.CreateFromColors(device, colors, w, h);
    }

    // ── GPU smoke tests: every TransitionType, both outgoing null/non-null, three progress points ──

    public static IEnumerable<object[]> AllTransitionTypes()
    {
        foreach (var value in Enum.GetValues<TransitionType>())
        {
            yield return new object[] { value };
        }
    }

    /// <summary>
    /// Every <see cref="TransitionType"/> except <see cref="TransitionType.None"/>, which is
    /// exempt from the "outgoing=null renders opaque black" fade-from-black contract: it is a
    /// hard cut straight to incoming (see the <c>default:</c> case doc comment in
    /// <see cref="TransitionRenderer"/>), so there is no "faded from black" state to assert.
    /// </summary>
    public static IEnumerable<object[]> AllTransitionTypesExceptNone()
    {
        foreach (var value in Enum.GetValues<TransitionType>())
        {
            if (value == TransitionType.None) continue;
            yield return new object[] { value };
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllTransitionTypes), DynamicDataSourceType.Method)]
    public void Render_EveryTransitionType_ProducesCorrectlySizedTarget_WithAndWithoutOutgoing(
        TransitionType type)
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var incoming = MakeBitmap(device, Color.FromArgb(255, 0, 200, 0)))
        using (var outgoing = MakeBitmap(device, Color.FromArgb(255, 200, 0, 0)))
        {
            const int Width = 64;
            const int Height = 48;

            foreach (double progress in new[] { 0.0, 0.5, 1.0 })
            {
                using var withOutgoing = renderer.Render(outgoing, incoming, type, progress, Width, Height);
                Assert.AreEqual((uint)Width, withOutgoing.SizeInPixels.Width,
                    $"{type} @ {progress} (outgoing present) width mismatch.");
                Assert.AreEqual((uint)Height, withOutgoing.SizeInPixels.Height,
                    $"{type} @ {progress} (outgoing present) height mismatch.");

                using var withoutOutgoing = renderer.Render(null, incoming, type, progress, Width, Height);
                Assert.AreEqual((uint)Width, withoutOutgoing.SizeInPixels.Width,
                    $"{type} @ {progress} (outgoing null) width mismatch.");
                Assert.AreEqual((uint)Height, withoutOutgoing.SizeInPixels.Height,
                    $"{type} @ {progress} (outgoing null) height mismatch.");
            }
        }
    }

    [TestMethod]
    public void Render_DifferentInputResolutions_ScalesBothIntoOutputSize()
    {
        // Slides/text render at project resolution while video frames render at source
        // resolution — Render must scale both into the requested output size regardless.
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var incoming = MakeBitmap(device, Color.FromArgb(255, 0, 200, 0), 1920, 1080))
        using (var outgoing = MakeBitmap(device, Color.FromArgb(255, 200, 0, 0), 320, 240))
        {
            foreach (var type in Enum.GetValues<TransitionType>())
            {
                using var target = renderer.Render(outgoing, incoming, type, 0.5, 100, 60);
                Assert.AreEqual(100u, target.SizeInPixels.Width, $"{type} width mismatch.");
                Assert.AreEqual(60u, target.SizeInPixels.Height, $"{type} height mismatch.");
            }
        }
    }

    [TestMethod]
    public void Render_Disposed_Throws()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var incoming = MakeBitmap(device, Color.FromArgb(255, 0, 200, 0)))
        {
            var renderer = new TransitionRenderer(device);
            renderer.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(
                () => renderer.Render(null, incoming, TransitionType.CrossFade, 0.5, 16, 16));
        }
    }

    // ── Pixel-level assertions: these actually inspect drawn content, not just target size ──

    [TestMethod]
    [DynamicData(nameof(AllTransitionTypes), DynamicDataSourceType.Method)]
    public void Render_EveryTransitionType_CoversEntireOutput_NoTransparentGaps(TransitionType type)
    {
        // Regression coverage for a small input scaled UP into a large output, and a large input
        // scaled DOWN into a small output, both with and without an outgoing frame. Every sampled
        // pixel — corners, edge midpoints, centre — must be fully opaque; a gap here would mean
        // the render left part of the output at its native (transparent) background instead of
        // covering it, which a dimension-only assertion can never detect.
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var smallBitmap = MakeBitmap(device, Color.FromArgb(255, 200, 0, 0), 16, 12))
        using (var largeBitmap = MakeBitmap(device, Color.FromArgb(255, 0, 200, 0), 400, 300))
        {
            const int UpW = 200, UpH = 150;
            const int DownW = 50, DownH = 40;

            foreach (double progress in new[] { 0.0, 0.5, 1.0 })
            {
                foreach (var outgoing in new[] { smallBitmap, null })
                {
                    using var upTarget = renderer.Render(outgoing, smallBitmap, type, progress, UpW, UpH);
                    AssertFullyOpaque(upTarget, UpW, UpH,
                        $"{type} @ {progress}, small input scaled into a larger output " +
                        $"(outgoing={(outgoing is null ? "null" : "present")})");
                }

                foreach (var outgoing in new[] { largeBitmap, null })
                {
                    using var downTarget = renderer.Render(outgoing, largeBitmap, type, progress, DownW, DownH);
                    AssertFullyOpaque(downTarget, DownW, DownH,
                        $"{type} @ {progress}, large input scaled into a smaller output " +
                        $"(outgoing={(outgoing is null ? "null" : "present")})");
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllTransitionTypes), DynamicDataSourceType.Method)]
    public void Render_LargeInputIntoSmallOutput_ScalesEntireImage_RatherThanCropping(TransitionType type)
    {
        // Regression test for bug 4: pre-fix, TransitionType.None's default case drew the
        // incoming bitmap at its own native pixel size with no dest/src Rect scaling, so a small
        // output only ever sampled the source's far-left region — a marker placed near the
        // source's right edge would never appear, no matter how the output was sized. Every
        // other type already used dest/src scaling and should show the marker regardless.
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var incoming = MakeRightEdgeMarkerBitmap(device, 400, 400))
        {
            // progress=1.0 resolves to pure incoming for every transition type (verified in
            // Render_AtProgress0_MatchesOutgoing_AndAtProgress1_MatchesIncoming below), so this
            // isolates the scaling behaviour from any transition-specific blending.
            using var target = renderer.Render(null, incoming, type, 1.0, 50, 50);

            var rightEdgePixel = target.GetPixelColors(48, 25, 1, 1)[0];
            Assert.IsTrue(rightEdgePixel.G > 100,
                $"{type}: expected the incoming bitmap's right-edge marker to reappear (scaled) " +
                $"near the output's right edge, but found G={rightEdgePixel.G} — looks cropped to " +
                "the source's native top-left region rather than scaled into the output.");
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllTransitionTypes), DynamicDataSourceType.Method)]
    public void Render_AtProgress0_MatchesOutgoing_AndAtProgress1_MatchesIncoming(TransitionType type)
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        var outgoingColor = Color.FromArgb(255, 200, 40, 40);
        var incomingColor = Color.FromArgb(255, 40, 200, 40);

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var outgoing = MakeBitmap(device, outgoingColor, 64, 64))
        using (var incoming = MakeBitmap(device, incomingColor, 64, 64))
        {
            using var at0 = renderer.Render(outgoing, incoming, type, 0.0, 64, 64);
            var pixelAt0 = at0.GetPixelColors(32, 32, 1, 1)[0];

            using var at1 = renderer.Render(outgoing, incoming, type, 1.0, 64, 64);
            var pixelAt1 = at1.GetPixelColors(32, 32, 1, 1)[0];

            if (type == TransitionType.None)
            {
                // Hard-cut fallback: incoming shows immediately regardless of progress (this is
                // pre-existing, intentional "no transition" behaviour, unrelated to the bug-4
                // scaling fix) — so both endpoints show incoming, not outgoing.
                AssertColorApprox(incomingColor, pixelAt0, 6, $"{type} @ progress=0 (hard cut).");
            }
            else
            {
                AssertColorApprox(outgoingColor, pixelAt0, 6, $"{type} @ progress=0.");
            }

            AssertColorApprox(incomingColor, pixelAt1, 6, $"{type} @ progress=1.");
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllTransitionTypesExceptNone), DynamicDataSourceType.Method)]
    public void Render_NullOutgoing_AtProgress0_IsOpaqueBlack(TransitionType type)
    {
        // Regression test for bug 3 (RenderGlitch/RenderCrossFade not clearing to black when
        // outgoing is null) — and, by construction, also covers the RenderFade/RenderSlide
        // null-outgoing gaps found during the same audit.
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var incoming = MakeBitmap(device, Color.FromArgb(255, 40, 200, 40), 64, 64))
        {
            using var target = renderer.Render(null, incoming, type, 0.0, 64, 64);
            var pixel = target.GetPixelColors(32, 32, 1, 1)[0];

            Assert.AreEqual(255, pixel.A, $"{type}: expected fully opaque output.");
            Assert.IsTrue(pixel.R <= 5 && pixel.G <= 5 && pixel.B <= 5,
                $"{type} @ progress=0 with outgoing=null should render solid black " +
                $"(fade-from-black contract), but got RGB=({pixel.R},{pixel.G},{pixel.B}).");
        }
    }

    [TestMethod]
    public void RenderZoomBlur_BlurWidthInOutputSpace_IsComparableAcrossDifferentSourceResolutions()
    {
        // Regression test for bug 2: blur was applied in each input's own source-pixel space and
        // only afterwards scaled into output space via the dest/src Rect, so the *effective*
        // output-space blur radius was divided by that input's own source→output scale factor.
        // A 64px source scaled up ~3x into this test's output should show roughly the same
        // output-space blur width as a 640px source scaled down ~0.3x into the same output —
        // pre-fix, the disparity was on the order of the resolution ratio itself (~10x here).
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        const int OutputSize = 200;

        using (device)
        using (var renderer = new TransitionRenderer(device))
        using (var smallEdge = MakeHardEdgeBitmap(device, 64))
        using (var largeEdge = MakeHardEdgeBitmap(device, 640))
        {
            int smallSourceWidth = MeasureZoomBlurEdgeWidth(renderer, smallEdge, OutputSize);
            int largeSourceWidth = MeasureZoomBlurEdgeWidth(renderer, largeEdge, OutputSize);

            Assert.IsTrue(smallSourceWidth > 0 && largeSourceWidth > 0,
                $"Expected a measurable blurred edge in both cases " +
                $"(64px-source width={smallSourceWidth}, 640px-source width={largeSourceWidth}).");

            double ratio = (double)Math.Max(smallSourceWidth, largeSourceWidth)
                / Math.Min(smallSourceWidth, largeSourceWidth);
            Assert.IsTrue(ratio < 2.0,
                "Output-space blur width should be comparable regardless of source resolution " +
                $"(64px-source width={smallSourceWidth}px, 640px-source width={largeSourceWidth}px, " +
                $"ratio={ratio:F2}). A large ratio indicates the blur amount is not being " +
                "compensated for the source→output scale factor.");
        }
    }

    private static int MeasureZoomBlurEdgeWidth(
        TransitionRenderer renderer, CanvasBitmap edgeBitmap, int outputSize)
    {
        // outgoing=null, progress=0.5 (PeakEnvelope's maximum) isolates the incoming side's
        // zoom+blur from any outgoing-side confound.
        using var target = renderer.Render(
            null, edgeBitmap, TransitionType.ZoomBlur, 0.5, outputSize, outputSize);
        var scanline = target.GetPixelColors(0, outputSize / 2, outputSize, 1);
        return MeasureEdgeWidth(scanline);
    }

    /// <summary>
    /// Counts pixels along a scanline whose grayscale intensity falls strictly between the 10th
    /// and 90th percentile of the scanline's own min/max range — a relative (amplitude-invariant)
    /// proxy for gradient/blur width, so it stays comparable even though this test's two cases
    /// render at different opacity/contrast levels.
    /// </summary>
    private static int MeasureEdgeWidth(Color[] scanline)
    {
        var intensities = scanline.Select(c => (c.R + c.G + c.B) / 3.0).ToArray();
        double min = intensities.Min();
        double max = intensities.Max();
        if (max - min < 1e-6) return 0;

        double lowThreshold = min + 0.1 * (max - min);
        double highThreshold = min + 0.9 * (max - min);
        return intensities.Count(v => v > lowThreshold && v < highThreshold);
    }

    private static CanvasBitmap MakeHardEdgeBitmap(CanvasDevice device, int size)
    {
        var colors = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                colors[y * size + x] = x < size / 2
                    ? Color.FromArgb(255, 0, 0, 0)
                    : Color.FromArgb(255, 255, 255, 255);
            }
        }
        return CanvasBitmap.CreateFromColors(device, colors, size, size);
    }

    private static CanvasBitmap MakeRightEdgeMarkerBitmap(CanvasDevice device, int w, int h)
    {
        var colors = new Color[w * h];
        int markerStart = (int)(w * 0.85);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                colors[y * w + x] = x >= markerStart
                    ? Color.FromArgb(255, 0, 255, 0)
                    : Color.FromArgb(255, 0, 0, 0);
            }
        }
        return CanvasBitmap.CreateFromColors(device, colors, w, h);
    }

    private static void AssertFullyOpaque(CanvasRenderTarget target, int width, int height, string context)
    {
        var samplePoints = new (int x, int y)[]
        {
            (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1),
            (width / 2, height / 2),
            (width / 2, 0), (width / 2, height - 1),
            (0, height / 2), (width - 1, height / 2),
        };

        foreach (var (x, y) in samplePoints)
        {
            var pixel = target.GetPixelColors(x, y, 1, 1)[0];
            Assert.AreEqual(255, pixel.A,
                $"{context}: pixel at ({x},{y}) is not fully opaque (alpha={pixel.A}) — " +
                "uncovered/transparent region.");
        }
    }

    private static void AssertColorApprox(Color expected, Color actual, byte tolerance, string context)
    {
        Assert.IsTrue(Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance,
            $"{context}: expected ~RGB({expected.R},{expected.G},{expected.B}), " +
            $"got RGB({actual.R},{actual.G},{actual.B}).");
    }

    // ── Pure arithmetic: no GPU device required ──

    [TestMethod]
    [DataRow(0.0, 1, 0)]
    [DataRow(0.25, 1, 0)]
    [DataRow(0.5, 1, 0)]
    [DataRow(0.75, 1, 0)]
    [DataRow(1.0, 1, 0)]
    [DataRow(0.0, -1, 0)]
    [DataRow(0.5, -1, 0)]
    [DataRow(1.0, -1, 0)]
    [DataRow(0.0, 0, 1)]
    [DataRow(0.5, 0, 1)]
    [DataRow(1.0, 0, 1)]
    [DataRow(0.0, 0, -1)]
    [DataRow(0.5, 0, -1)]
    [DataRow(1.0, 0, -1)]
    public void PushPairOffsets_StaysEdgeToEdge_NoGapOrOverlap(double t, int dirX, int dirY)
    {
        const double Size = 200;
        int dir = dirX != 0 ? dirX : dirY;

        var (outgoingOffset, incomingOffset) = TransitionRenderer.PushPairOffsets(t, dir, Size);

        // Outgoing spans [outgoingOffset, outgoingOffset + Size]; incoming spans
        // [incomingOffset, incomingOffset + Size]. Regardless of direction sign, the two frames
        // must share exactly one edge (whichever is the leading edge of incoming / trailing edge
        // of outgoing), with no gap and no overlap, at every t.
        double outgoingLeadingEdge = dir >= 0 ? outgoingOffset + Size : outgoingOffset;
        double incomingTrailingEdge = dir >= 0 ? incomingOffset : incomingOffset + Size;

        Assert.AreEqual(outgoingLeadingEdge, incomingTrailingEdge, 1e-9,
            $"t={t}, dir={dir}: outgoing/incoming frames must be exactly edge-to-edge.");
    }

    [TestMethod]
    public void PushPairOffsets_AtStartAndEnd_FramesAreFullyInOrOutOfView()
    {
        const double Size = 100;

        var (outAt0, inAt0) = TransitionRenderer.PushPairOffsets(0.0, dir: 1, Size);
        Assert.AreEqual(0.0, outAt0, 1e-9, "Outgoing must be fully in view at t=0.");
        Assert.AreEqual(Size, inAt0, 1e-9, "Incoming must be exactly one frame-size away at t=0.");

        var (outAt1, inAt1) = TransitionRenderer.PushPairOffsets(1.0, dir: 1, Size);
        Assert.AreEqual(-Size, outAt1, 1e-9, "Outgoing must be exactly one frame-size away at t=1.");
        Assert.AreEqual(0.0, inAt1, 1e-9, "Incoming must be fully in view at t=1.");
    }

    [TestMethod]
    public void PeakEnvelope_IsZeroAtBothEnds_AndMaximalAtMidpoint()
    {
        Assert.AreEqual(0.0, TransitionRenderer.PeakEnvelope(0.0), 1e-9);
        Assert.AreEqual(0.0, TransitionRenderer.PeakEnvelope(1.0), 1e-9);
        Assert.AreEqual(1.0, TransitionRenderer.PeakEnvelope(0.5), 1e-9);

        // Strictly increasing on [0, 0.5] and strictly decreasing on [0.5, 1].
        Assert.IsTrue(TransitionRenderer.PeakEnvelope(0.25) < TransitionRenderer.PeakEnvelope(0.5));
        Assert.IsTrue(TransitionRenderer.PeakEnvelope(0.75) < TransitionRenderer.PeakEnvelope(0.5));
        Assert.IsTrue(TransitionRenderer.PeakEnvelope(0.1) < TransitionRenderer.PeakEnvelope(0.25));
    }

    [TestMethod]
    public void PeakEnvelope_ClampsOutOfRangeInput()
    {
        Assert.AreEqual(0.0, TransitionRenderer.PeakEnvelope(-1.0), 1e-9);
        Assert.AreEqual(0.0, TransitionRenderer.PeakEnvelope(2.0), 1e-9);
    }

    [TestMethod]
    public void GlitchSliceOffset_IsDeterministic_ForSameInputs()
    {
        for (int slice = 0; slice < 10; slice++)
        {
            foreach (double progress in new[] { 0.0, 0.13, 0.5, 0.87, 1.0 })
            {
                double first = TransitionRenderer.GlitchSliceOffset(slice, progress);
                double second = TransitionRenderer.GlitchSliceOffset(slice, progress);
                Assert.AreEqual(first, second,
                    $"slice={slice}, progress={progress}: must be perfectly deterministic.");
            }
        }
    }

    [TestMethod]
    public void GlitchSliceOffset_MatchesKnownLiteralValues()
    {
        // Pins the deterministic formula (Knuth multiplicative hash of sliceIndex mod 997 for a
        // per-slice phase, alternating sign, sin-based envelope) against pre-computed literal
        // values, rather than only checking "two calls agree" — the latter would still pass even
        // if the function secretly depended on hidden mutable state that just didn't happen to
        // change between the two calls within the same test run.
        AssertOffset(0, 0.0, 0.0);
        AssertOffset(0, 0.5, 3.6739403974420594E-16);
        AssertOffset(0, 1.0, -7.347880794884119E-16);
        AssertOffset(1, 0.0, -0.1879384261945807);
        AssertOffset(1, 0.25, 0.9821808122537846);
        AssertOffset(1, 0.5, 0.1879384261945808);
        AssertOffset(2, 0.13, 0.11738875894320883);
        AssertOffset(2, 0.87, -0.9534992942351256);
        AssertOffset(3, 0.5, 0.6907697631754542);
        AssertOffset(6, 0.5, -0.9888196415611219);

        static void AssertOffset(int slice, double progress, double expected)
        {
            double actual = TransitionRenderer.GlitchSliceOffset(slice, progress);
            Assert.AreEqual(expected, actual, 1e-9,
                $"slice={slice}, progress={progress}: expected {expected:R}, got {actual:R}.");
        }
    }

    [TestMethod]
    public void GlitchSliceOffset_DiffersAcrossSlices_ForTheSameProgress()
    {
        // Not every pair needs to differ, but the function must not collapse to a single
        // constant value across all slices (that would make the effect look uniform/fake).
        var offsets = Enumerable.Range(0, 8)
            .Select(i => TransitionRenderer.GlitchSliceOffset(i, 0.5))
            .Distinct()
            .Count();

        Assert.IsTrue(offsets > 1, "Different slices should not all produce the same offset.");
    }

    [TestMethod]
    public void GlitchSliceOffset_StaysWithinExpectedRange()
    {
        for (int slice = 0; slice < 20; slice++)
        {
            for (double progress = 0; progress <= 1.0; progress += 0.1)
            {
                double offset = TransitionRenderer.GlitchSliceOffset(slice, progress);
                Assert.IsTrue(offset is >= -1.0 and <= 1.0,
                    $"slice={slice}, progress={progress}: offset {offset} out of [-1, 1].");
            }
        }
    }

    [TestMethod]
    [DataRow(0.0, 1, 0)]
    [DataRow(0.5, 1, 0)]
    [DataRow(1.0, 1, 0)]
    public void WipeRevealRect_LegacyWipe_GrowsFromLeftEdgeRightward(double t, int dirX, int dirY)
    {
        var rect = TransitionRenderer.WipeRevealRect(t, 100, 50, dirX, dirY);

        Assert.AreEqual(0.0, rect.X, 1e-9);
        Assert.AreEqual(100.0 * t, rect.Width, 1e-9);
        Assert.AreEqual(50.0, rect.Height, 1e-9);
    }

    [TestMethod]
    public void WipeRevealRect_WipeRight_GrowsFromRightEdgeLeftward()
    {
        var rect = TransitionRenderer.WipeRevealRect(0.25, 100, 50, -1, 0);

        Assert.AreEqual(75.0, rect.X, 1e-9);
        Assert.AreEqual(25.0, rect.Width, 1e-9);
    }

    [TestMethod]
    public void WipeRevealRect_WipeUp_GrowsFromBottomEdgeUpward()
    {
        var rect = TransitionRenderer.WipeRevealRect(0.25, 100, 80, 0, -1);

        Assert.AreEqual(60.0, rect.Y, 1e-9);
        Assert.AreEqual(20.0, rect.Height, 1e-9);
    }

    [TestMethod]
    public void WipeRevealRect_WipeDown_GrowsFromTopEdgeDownward()
    {
        var rect = TransitionRenderer.WipeRevealRect(0.25, 100, 80, 0, 1);

        Assert.AreEqual(0.0, rect.Y, 1e-9);
        Assert.AreEqual(20.0, rect.Height, 1e-9);
    }

    [TestMethod]
    public void WipeRevealRect_AtT0IsEmpty_AtT1CoversWholeFrame()
    {
        foreach (var (dirX, dirY) in new (int, int)[] { (1, 0), (-1, 0), (0, -1), (0, 1) })
        {
            var atStart = TransitionRenderer.WipeRevealRect(0.0, 100, 60, dirX, dirY);
            Assert.AreEqual(0.0, atStart.Width * atStart.Height, 1e-9,
                $"dir=({dirX},{dirY}): reveal must be empty at t=0.");

            var atEnd = TransitionRenderer.WipeRevealRect(1.0, 100, 60, dirX, dirY);
            Assert.AreEqual(100.0 * 60.0, atEnd.Width * atEnd.Height, 1e-6,
                $"dir=({dirX},{dirY}): reveal must cover the whole frame at t=1.");
        }
    }
}
