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
/// Win2D drawing APIs — backgrounds (solid, gradient, or Ken-Burns-animated image)
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
        using var ds = target.CreateDrawingSession();

        DrawSlideBackground(ds, slide, progress, width, height);

        if (!drawText)
            return target;

        using var format = AnimatedTextEngine.CreateFormat(
            slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
            ToCanvasAlignment(slide.TextAlignment), CanvasVerticalAlignment.Center, wrap: true);

        var rect = ComputeTextRect(slide, width, height);

        _textEngine.DrawAnimatedText(ds, slide.Text, format, rect, AnimatedTextEngine.ParseColor(slide.TextColor),
            slide.Animation, progress, width, height, (float)slide.FontSize, slide.Duration.TotalSeconds,
            TextAnimationWindow.FromSlide(slide));

        return target;
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
            CanvasDevice.GetSharedDevice(), slide.Text, format, (float)maxWidth, (float)maxHeight);

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
        if (string.IsNullOrEmpty(slide.BackgroundImagePath) || !File.Exists(slide.BackgroundImagePath))
        {
            ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
            return;
        }

        if (_bgImage is null || _bgImagePath != slide.BackgroundImagePath || _bgImage.Device != ds.Device)
        {
            _bgImage?.Dispose();
            _bgImage = null;
            _bgImagePath = null;
            try
            {
                // Synchronous fallback. The UI/preview path pre-warms this cache via
                // EnsureBackgroundLoadedAsync so this branch is only reached on the
                // off-UI export thread (where blocking is acceptable).
                _bgImage = CanvasBitmap.LoadAsync(ds.Device, slide.BackgroundImagePath)
                    .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                _bgImagePath = slide.BackgroundImagePath;
            }
            catch
            {
                ds.Clear(AnimatedTextEngine.ParseColor(slide.BackgroundColor));
                return;
            }
        }

        DrawKenBurns(ds, _bgImage!, slide, progress, width, height);
    }

    /// <summary>
    /// Asynchronously pre-loads and caches the slide's background image so the
    /// synchronous <see cref="RenderSlide"/> never has to block the calling thread
    /// on file I/O + GPU decode. Call this from the UI/preview path before rendering
    /// an image-backed slide. A no-op for non-image slides or when the image is
    /// already cached. Failures are swallowed (the renderer falls back to the slide's
    /// solid background colour).
    /// </summary>
    public async Task EnsureBackgroundLoadedAsync(TextSlideSegment slide)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (slide.BackgroundType != SlideBackgroundType.Image)
            return;

        var path = slide.BackgroundImagePath;
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
        catch
        {
            // Leave the cache empty; DrawImageBackground falls back to the solid colour.
        }
    }

    /// <summary>
    /// Draws an image background with a slow "Ken Burns" motion (continuous gentle
    /// zoom + elliptical pan) so an image-backed slide reads like a living shot
    /// rather than a frozen still. The image is overscaled beyond a simple cover
    /// fit to leave headroom, then a breathing zoom and a slow elliptical pan —
    /// both driven by sine/cosine of elapsed time so they never settle into a
    /// stopped end state — push the framing around within that headroom. The pan
    /// stays inside the available slack so the image edges are never revealed.
    /// <paramref name="progress"/> (0..1 over the slide's duration) is converted to
    /// elapsed seconds so the motion advances with the playhead (frozen when paused)
    /// and is identical in the live preview and the exported video.
    /// </summary>
    private static void DrawKenBurns(
        CanvasDrawingSession ds, CanvasBitmap bitmap, TextSlideSegment slide,
        double progress, int width, int height)
    {
        // Elapsed seconds within the slide drives the motion (frozen on a static frame).
        float t = (float)(Math.Clamp(progress, 0, 1) * Math.Max(0.001, slide.Duration.TotalSeconds));

        var src = bitmap.SizeInPixels;
        float fill = Math.Max((float)width / src.Width, (float)height / src.Height);

        // Overscale beyond cover-fit gives headroom for the pan + breathing zoom
        // without ever exposing the image edges. The zoom slowly breathes and the
        // pan slowly drifts on an elliptical path — neither ever comes to rest.
        float zoom = 1.14f + 0.05f * MathF.Sin(t * 0.18f);

        float scale = fill * zoom;
        float drawW = src.Width * scale;
        float drawH = src.Height * scale;

        // Slack = how far we can shift the (over-sized) image while still covering
        // the frame. Keep the pan within ~70% of it so a corner is never revealed.
        float slackX = (drawW - width) * 0.5f;
        float slackY = (drawH - height) * 0.5f;
        float panX = MathF.Sin(t * 0.13f) * slackX * 0.7f;
        float panY = MathF.Cos(t * 0.11f) * slackY * 0.7f;

        float drawX = (width - drawW) * 0.5f + panX;
        float drawY = (height - drawH) * 0.5f + panY;

        ds.DrawImage(bitmap, new Rect(drawX, drawY, drawW, drawH));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgImage?.Dispose();
        _gradientCache?.Dispose();
        // The engine owns a cached scratch render target for its blurred-text passes,
        // so it has to be released with the renderer that created it.
        _textEngine.Dispose();
        GC.SuppressFinalize(this);
    }
}
