using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Mixtri.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Mixtri.Core.Processing;

/// <summary>
/// Renders <see cref="TextSlideSegment"/> full-screen text slide frames using
/// Win2D drawing APIs — backgrounds (solid, gradient, or organically animated image)
/// plus the slide's text. The cinematic / After-Effects-style text animation engine
/// itself (entrance/exit timing, per-character effects, typewriter, blur, etc.) has
/// been extracted to <see cref="AnimatedTextEngine"/> so it can be shared with other
/// renderers (such as animated text overlays) without duplicating the tuned logic.
/// </summary>
public class TextSlideRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly AnimatedTextEngine _textEngine;
    private bool _disposed;

    // Image background cache
    private CanvasBitmap? _bgImage;
    private string? _bgImagePath;
    private CanvasRenderTarget? _flowNoiseA;
    private CanvasRenderTarget? _flowNoiseB;

    // Cached, slide-invariant base gradient. Only the per-frame displacement field
    // depends on time, so the underlying gradient texture is built once per
    // (size, colours, angle) and reused across every frame of the slide.
    private CanvasRenderTarget? _gradientCache;
    private (int W, int H, string Start, string End, double Angle) _gradientKey;

    public TextSlideRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
        _textEngine = new AnimatedTextEngine(_device);
    }

    /// <summary>Renders a full-screen text slide frame.</summary>
    /// <param name="drawText">When false, only the background is drawn (used while
    /// the text is being edited in-place over the preview).</param>
    public CanvasRenderTarget RenderSlide(
        TextSlideSegment slide, double progress, int width, int height, bool drawText = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = Win2DUtils.CreateRenderTarget(_device, width, height, 96, "text slide frame");
        try
        {
            using var ds = target.CreateDrawingSession();

            DrawSlideBackground(ds, slide, progress, width, height);

            if (!drawText)
                return target;

            using var format = AnimatedTextEngine.CreateFormat(
                slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
                ToCanvasAlignment(slide.TextAlignment), CanvasVerticalAlignment.Center, wrap: true);

            var rect = ComputeTextRect(slide, width, height);

            _textEngine.DrawAnimatedText(ds, slide.DisplayText, format, rect, AnimatedTextEngine.ParseColor(slide.TextColor),
                slide.Animation, progress, width, height, (float)slide.FontSize, slide.Duration.TotalSeconds,
                TextAnimationWindow.FromSlide(slide));

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The text block rectangle for a slide, in output pixels, centered at the
    /// slide's normalized (TextX, TextY) position. The width is a fixed fraction
    /// of the slide (the wrapping width); the height hugs the actual measured
    /// text so the box doesn't fill the whole slide.
    /// </summary>
    public static Rect ComputeTextRect(TextSlideSegment slide, int width, int height)
    {
        double boxW = width * 0.84;
        double maxH = height * 0.8;
        double boxH = MeasureTextHeight(slide, boxW, maxH);
        double left = slide.TextX * width - boxW / 2;
        double top = slide.TextY * height - boxH / 2;
        return new Rect(left, top, boxW, boxH);
    }

    /// <summary>
    /// Measures the wrapped text height (in output pixels) for the slide, clamped
    /// between roughly one line and <paramref name="maxHeight"/>.
    /// </summary>
    private static double MeasureTextHeight(TextSlideSegment slide, double maxWidth, double maxHeight)
    {
        double minH = Math.Min(slide.FontSize * 1.3, maxHeight);
        if (string.IsNullOrEmpty(slide.Text) || maxWidth <= 0)
            return minH;

        using var format = AnimatedTextEngine.CreateFormat(
            slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
            ToCanvasAlignment(slide.TextAlignment), CanvasVerticalAlignment.Top, wrap: true);
        using var layout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(), slide.DisplayText, format, (float)maxWidth, (float)maxHeight);

        return Math.Clamp(layout.LayoutBounds.Height, minH, maxHeight);
    }

    private static CanvasHorizontalAlignment ToCanvasAlignment(SlideTextAlignment a) => a switch
    {
        SlideTextAlignment.Left => CanvasHorizontalAlignment.Left,
        SlideTextAlignment.Right => CanvasHorizontalAlignment.Right,
        _ => CanvasHorizontalAlignment.Center,
    };

    // ─────────────────────────── Backgrounds ─────────────────────────────

    private void DrawSlideBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, double progress, int width, int height)
    {
        switch (slide.BackgroundType)
        {
            case SlideBackgroundType.Gradient:
                DrawGradientBackground(ds, slide, progress, width, height);
                break;
            case SlideBackgroundType.Image:
                DrawImageBackground(ds, slide, progress, width, height);
                break;
            default:
                ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
                break;
        }
    }

    /// <summary>
    /// Draws the gradient background with a subtle turbulent "wave" animation so a
    /// playing slide reads like moving video rather than a flat gradient. The base
    /// gradient is kept stationary; the motion comes entirely from a GPU
    /// displacement map fed by <em>two</em> counter-rotating Perlin-noise fields.
    /// Because the two fields drift along opposing circular paths at different
    /// rates, their interference makes the displacement churn/boil in place
    /// instead of sliding in one direction — an organic wave, not a linear pan.
    /// <paramref name="progress"/> (0..1 over the slide's duration) is converted to
    /// elapsed seconds so the motion advances with the playhead (and is frozen when
    /// paused) and is identical in both the live preview and the exported video.
    /// </summary>
    private void DrawGradientBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, double progress, int width, int height)
    {
        // Elapsed seconds within the slide drives the motion (frozen on a static frame).
        float t = (float)(Math.Clamp(progress, 0, 1) * Math.Max(0.001, slide.Duration.TotalSeconds));

        if (slide.GradientMotion == SlideGradientMotion.SubtleDrift)
        {
            // Same gentle 18-second orbit as the chapter previews, driven by playhead time.
            // Draw a brush directly: no cached per-frame textures, turbulence or edge sampling.
            double phase = t * Math.PI * 2 / 18;
            double angle = (slide.GradientAngle + Math.Sin(phase) * 4) * Math.PI / 180;
            var center = new Vector2(
                (float)(width * (.5 + Math.Sin(phase) * .065)),
                (float)(height * (.5 + Math.Cos(phase) * .045)));
            float radius = MathF.Sqrt((float)width * width + (float)height * height) / 2;
            var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius;
            using var brush = new CanvasLinearGradientBrush(ds,
                AnimatedTextEngine.ParseColor(slide.BackgroundColor),
                AnimatedTextEngine.ParseColor(slide.GradientEndColor))
            { StartPoint = center - offset, EndPoint = center + offset };
            ds.FillRectangle(0, 0, width, height, brush);
            return;
        }

        // Stationary base gradient (no global drift — drift reads as a linear pan).
        double angleRad = slide.GradientAngle * Math.PI / 180.0;
        float cx = width / 2f, cy = height / 2f;
        float diag = MathF.Sqrt(width * width + height * height) / 2f;
        float dx = diag * MathF.Cos((float)angleRad);
        float dy = diag * MathF.Sin((float)angleRad);

        // Render the gradient into an intermediate target so it can be fed through
        // GPU effects for the wave distortion. The base gradient is slide-invariant,
        // so cache it and only rebuild when the size/colours/angle (or device) change
        // instead of allocating a full-resolution texture every frame.
        var key = (width, height, slide.BackgroundColor, slide.GradientEndColor, slide.GradientAngle);
        if (_gradientCache is null || _gradientCache.Device != _device || _gradientKey != key)
        {
            // Allocate-then-swap so a failed allocation never leaves _gradientCache
            // pointing at a disposed render target.
            var next = Win2DUtils.CreateRenderTarget(_device, width, height, 96, "text slide gradient cache");
            using (var gds = next.CreateDrawingSession())
            using (var brush = new CanvasLinearGradientBrush(
                gds, AnimatedTextEngine.ParseColor(slide.BackgroundColor), AnimatedTextEngine.ParseColor(slide.GradientEndColor))
            {
                StartPoint = new Vector2(cx - dx, cy - dy),
                EndPoint = new Vector2(cx + dx, cy + dy),
            })
            {
                gds.FillRectangle(0, 0, width, height, brush);
            }
            _gradientCache?.Dispose();
            _gradientCache = next;
            _gradientKey = key;
        }
        var gradientRt = _gradientCache;

        // Clamp the gradient edges so displacement sampling outside the image keeps
        // the edge color instead of revealing transparent/black borders.
        using var clamped = new BorderEffect
        {
            Source = gradientRt,
            ExtendX = CanvasEdgeBehavior.Clamp,
            ExtendY = CanvasEdgeBehavior.Clamp,
        };

        // Two fractal-noise fields at different frequencies/seeds.
        using var noiseA = new TurbulenceEffect
        {
            Frequency = new Vector2(0.006f, 0.009f),
            Octaves = 3,
            Size = new Vector2(width, height),
            Seed = 1,
            Noise = TurbulenceEffectNoise.FractalSum,
        };
        using var noiseB = new TurbulenceEffect
        {
            Frequency = new Vector2(0.010f, 0.007f),
            Octaves = 3,
            Size = new Vector2(width, height),
            Seed = 37,
            Noise = TurbulenceEffectNoise.FractalSum,
        };

        // Drift each field along an opposing circular path at a different rate. The
        // circular (sin/cos) paths never settle into a constant direction, so the
        // combined field swirls rather than panning linearly.
        using var driftA = new Transform2DEffect
        {
            Source = noiseA,
            TransformMatrix = Matrix3x2.CreateTranslation(
                MathF.Sin(t * 0.55f) * 110f, MathF.Cos(t * 0.42f) * 110f),
        };
        using var driftB = new Transform2DEffect
        {
            Source = noiseB,
            TransformMatrix = Matrix3x2.CreateTranslation(
                MathF.Cos(t * 0.37f) * -95f, MathF.Sin(t * 0.48f) * 95f),
        };

        // Average the two fields → an evolving, turbulent displacement map centered
        // around 0.5 (the no-displacement value for the R/G channels).
        using var field = new ArithmeticCompositeEffect
        {
            Source1 = driftA,
            Source2 = driftB,
            Source1Amount = 0.5f,
            Source2Amount = 0.5f,
            MultiplyAmount = 0f,
            Offset = 0f,
        };

        // Bend the gradient by the turbulent field. Amount ≈7.5% of the short edge —
        // visibly wavy but still subtle/ambient.
        using var displaced = new DisplacementMapEffect
        {
            Source = clamped,
            Displacement = field,
            Amount = MathF.Min(width, height) * 0.075f,
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
        };

        ds.DrawImage(displaced, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
    }

    private void DrawImageBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, double progress, int width, int height)
    {
        var path = TitleSlideImages.Resolve(slide.BackgroundImagePath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
            return;
        }

        if (_bgImage is null || _bgImagePath != path || _bgImage.Device != ds.Device)
        {
            _bgImage?.Dispose();
            _bgImage = null;
            _bgImagePath = null;
            try
            {
                // Synchronous fallback. The UI/preview path pre-warms this cache via
                // EnsureBackgroundLoadedAsync so this branch is only reached on the
                // off-UI export thread (where blocking is acceptable).
                _bgImage = CanvasBitmap.LoadAsync(ds.Device, path)
                    .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                _bgImagePath = path;
            }
            catch (Exception ex)
            {
                if (_device.IsDeviceLost(ex.HResult)) throw;
                ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
                return;
            }
        }

        DrawFlowingImage(ds, _bgImage!, slide, progress, width, height);
    }

    /// <summary>
    /// Asynchronously pre-loads and caches the slide's background image so the
    /// synchronous <see cref="RenderSlide"/> never has to block the calling thread
    /// on file I/O + GPU decode. Call this from the UI/preview path before rendering
    /// an image-backed slide. A no-op for non-image slides or when the image is
    /// already cached. File/decode failures fall back to the slide's solid background
    /// colour; device-loss failures propagate so the editor can rebuild its graphics.
    /// </summary>
    public async Task EnsureBackgroundLoadedAsync(TextSlideSegment slide)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (slide.BackgroundType != SlideBackgroundType.Image)
            return;

        var path = TitleSlideImages.Resolve(slide.BackgroundImagePath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        if (_bgImage is not null && _bgImagePath == path && _bgImage.Device == _device)
            return;

        try
        {
            var loaded = await CanvasBitmap.LoadAsync(_device, path).AsTask().ConfigureAwait(false);
            _bgImage?.Dispose();
            _bgImage = loaded;
            _bgImagePath = path;
        }
        catch (Exception ex)
        {
            if (_device.IsDeviceLost(ex.HResult)) throw;
            // Leave the cache empty; DrawImageBackground falls back to the solid colour.
        }
    }

    /// <summary>
    /// Broad, evolving displacement makes the image's surfaces flow in place.
    /// Gentle camera motion complements the flowing surfaces. Both noise fields use normalized
    /// frequencies and playhead time, keeping preview/export and different sizes consistent.
    /// </summary>
    private void DrawFlowingImage(
        CanvasDrawingSession ds, CanvasBitmap bitmap, TextSlideSegment slide,
        double progress, int width, int height)
    {
        float t = (float)(Math.Clamp(progress, 0, 1) * Math.Max(0.001, slide.Duration.TotalSeconds));
        var src = bitmap.Size;
        using var framed = new Transform2DEffect
        {
            Source = bitmap,
            TransformMatrix = ImageMotionTransform((float)src.Width, (float)src.Height, width, height, t),
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
        };
        using var clamped = new BorderEffect
        {
            Source = framed,
            ExtendX = CanvasEdgeBehavior.Clamp,
            ExtendY = CanvasEdgeBehavior.Clamp,
        };
        // Materialize the broad fields once at a bounded resolution. Evaluating two
        // oversized procedural fields at video resolution on every frame competes with
        // decode and every timeline canvas on the same graphics device.
        EnsureFlowNoise();
        var fieldScale = Matrix3x2.CreateScale(width / 256f, height / 256f);
        using var flowA = new Transform2DEffect
        {
            Source = _flowNoiseA,
            TransformMatrix = fieldScale * Matrix3x2.CreateTranslation(
                -width + MathF.Sin(t * .40f) * width * .22f,
                -height + MathF.Cos(t * .32f) * height * .22f),
        };
        using var flowB = new Transform2DEffect
        {
            Source = _flowNoiseB,
            TransformMatrix = fieldScale * Matrix3x2.CreateTranslation(
                -width - MathF.Cos(t * .29f) * width * .18f,
                -height + MathF.Sin(t * .37f) * height * .18f),
        };
        using var field = new ArithmeticCompositeEffect
        {
            Source1 = flowA, Source2 = flowB,
            Source1Amount = .5f, Source2Amount = .5f,
            MultiplyAmount = 0, Offset = 0,
        };
        using var flowing = new DisplacementMapEffect
        {
            Source = clamped, Displacement = field,
            Amount = Math.Min(width, height) * .085f,
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
        };
        // Cubic sampling can round opaque alpha down by one; composite over the
        // preset's base tone so the background remains fully opaque for transitions.
        ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
        ds.DrawImage(flowing, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
    }

    private void EnsureFlowNoise()
    {
        if (_flowNoiseA is not null && _flowNoiseB is not null) return;
        CanvasRenderTarget? nextA = null, nextB = null;
        try
        {
            nextA = CreateFlowNoise(new Vector2(2.2f / 256, 2.6f / 256), 1);
            nextB = CreateFlowNoise(new Vector2(3.1f / 256, 1.8f / 256), 37);
            _flowNoiseA = nextA; _flowNoiseB = nextB;
        }
        catch
        {
            nextA?.Dispose(); nextB?.Dispose();
            throw;
        }
    }

    private CanvasRenderTarget CreateFlowNoise(Vector2 frequency, int seed)
    {
        var target = Win2DUtils.CreateRenderTarget(_device, 768, 768, 96, "title flow field");
        try
        {
            using var ds = target.CreateDrawingSession();
            using var noise = new TurbulenceEffect
            {
                Frequency = frequency, Octaves = 2, Size = new Vector2(768, 768),
                Seed = seed, Noise = TurbulenceEffectNoise.FractalSum,
            };
            ds.DrawImage(noise);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    internal static Matrix3x2 ImageMotionTransform(float sourceWidth, float sourceHeight, int width, int height, float seconds)
    {
        float rotation = MathF.Sin(seconds * .18f) * (MathF.PI / 180 * .65f);
        float driftX = MathF.Sin(seconds * .16f) * width * .008f;
        float driftY = MathF.Sin(seconds * .13f) * height * .006f;
        // Cover the inverse-rotated viewport plus the maximum displacement sample
        // radius. This also works for portrait and ultrawide exports without corners showing.
        float margin = Math.Min(width, height) * .0425f + 2;
        float halfW = width / 2f + MathF.Abs(driftX) + margin;
        float halfH = height / 2f + MathF.Abs(driftY) + margin;
        float c = MathF.Abs(MathF.Cos(rotation)), s = MathF.Abs(MathF.Sin(rotation));
        float cover = Math.Max(2 * (c * halfW + s * halfH) / sourceWidth,
            2 * (s * halfW + c * halfH) / sourceHeight);
        float scale = cover * (1.02f + .015f * MathF.Sin(seconds * .22f));
        return Matrix3x2.CreateTranslation(-sourceWidth / 2, -sourceHeight / 2)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateRotation(rotation)
            * Matrix3x2.CreateTranslation(width / 2f + driftX, height / 2f + driftY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgImage?.Dispose();
        _flowNoiseA?.Dispose();
        _flowNoiseB?.Dispose();
        _gradientCache?.Dispose();
        // The engine owns a cached scratch render target for its blurred-text passes,
        // so it has to be released with the renderer that created it.
        _textEngine.Dispose();
        GC.SuppressFinalize(this);
    }
}
