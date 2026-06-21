using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Musio.Core.Processing;

/// <summary>
/// Renders <see cref="TextSlideSegment"/> frames and <see cref="TextOverlay"/>
/// elements using Win2D text/drawing APIs. Supports a rich set of cinematic /
/// After-Effects-style text animations driven by normalized progress (0..1).
/// Every animation has a matching IN (entrance) at the start and OUT (exit)
/// at the end, with a static hold in the middle.
/// </summary>
public class TextSlideRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private bool _disposed;

    // Image background cache
    private CanvasBitmap? _bgImage;
    private string? _bgImagePath;

    // Entrance and exit run for a CONSTANT wall-clock duration (in seconds), so the
    // motion always feels the same regardless of how long the slide is held or how
    // much text it shows. The middle is a static hold that absorbs any extra time.
    // For very short slides the phases shrink proportionally so they always fit.
    private const double EntranceSeconds = 0.6;
    private const double ExitSeconds = 0.6;

    // Continuous-motion rates, expressed per second so they don't stretch with duration.
    private const double TypewriterCharsPerSecond = 28.0;
    private const double TypewriterEraseCharsPerSecond = 38.0;
    private const double WaveHz = 0.8;            // vertical bob cycles per second
    private const double WaveFadeSeconds = 0.18;  // quick fade at each end

    /// <summary>
    /// Maps normalized <paramref name="progress"/> (0..1 over the whole slide) to
    /// independent entrance/exit progresses (each 0..1) that advance over a constant
    /// number of seconds. The hold in the middle grows or shrinks with the slide's
    /// duration while the entrance and exit motion stay at a fixed, consistent speed.
    /// </summary>
    private static (double InP, double OutP) ComputeInOutProgress(double progress, double durationSeconds)
    {
        double dur = Math.Max(0.001, durationSeconds);
        double elapsed = Math.Clamp(progress, 0, 1) * dur;

        // Never let entrance + exit exceed the slide; keep a hold when there's room.
        double inSec = Math.Min(EntranceSeconds, dur * 0.45);
        double outSec = Math.Min(ExitSeconds, dur * 0.45);

        double inP = inSec > 1e-6 ? Math.Clamp(elapsed / inSec, 0, 1) : 1;
        double outP = outSec > 1e-6 ? Math.Clamp((elapsed - (dur - outSec)) / outSec, 0, 1) : 0;
        return (inP, outP);
    }

    public TextSlideRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
    }

    /// <summary>Renders a full-screen text slide frame.</summary>
    /// <param name="drawText">When false, only the background is drawn (used while
    /// the text is being edited in-place over the preview).</param>
    public CanvasRenderTarget RenderSlide(
        TextSlideSegment slide, double progress, int width, int height, bool drawText = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = new CanvasRenderTarget(_device, width, height, 96);
        using var ds = target.CreateDrawingSession();

        DrawSlideBackground(ds, slide, progress, width, height);

        if (!drawText)
            return target;

        using var format = CreateFormat(
            slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
            ToCanvasAlignment(slide.TextAlignment), CanvasVerticalAlignment.Center, wrap: true);

        var rect = ComputeTextRect(slide, width, height);

        DrawAnimatedText(ds, slide.Text, format, rect, ParseColor(slide.TextColor),
            slide.Animation, progress, width, height, (float)slide.FontSize, slide.Duration.TotalSeconds);

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

        using var format = CreateFormat(
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
                ds.Clear(ParseColor(slide.BackgroundColor));
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
        // GPU effects for the wave distortion.
        using var gradientRt = new CanvasRenderTarget(_device, width, height, 96);
        using (var gds = gradientRt.CreateDrawingSession())
        using (var brush = new CanvasLinearGradientBrush(
            gds, ParseColor(slide.BackgroundColor), ParseColor(slide.GradientEndColor))
        {
            StartPoint = new Vector2(cx - dx, cy - dy),
            EndPoint = new Vector2(cx + dx, cy + dy),
        })
        {
            gds.FillRectangle(0, 0, width, height, brush);
        }

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
            ds.Clear(ParseColor(slide.BackgroundColor));
            return;
        }

        if (_bgImage is null || _bgImagePath != slide.BackgroundImagePath || _bgImage.Device != ds.Device)
        {
            _bgImage?.Dispose();
            _bgImage = null;
            _bgImagePath = null;
            try
            {
                _bgImage = CanvasBitmap.LoadAsync(ds.Device, slide.BackgroundImagePath)
                    .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                _bgImagePath = slide.BackgroundImagePath;
            }
            catch
            {
                ds.Clear(ParseColor(slide.BackgroundColor));
                return;
            }
        }

        DrawKenBurns(ds, _bgImage!, slide, progress, width, height);
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

    /// <summary>Renders a text overlay onto an existing drawing session.</summary>
    public void RenderOverlay(CanvasDrawingSession ds, TextOverlay overlay, double progress, int width, int height)
    {
        using var format = CreateFormat(
            overlay.FontFamily, overlay.FontSize, overlay.IsBold, overlay.IsItalic,
            CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center, wrap: false);

        using var layout = new CanvasTextLayout(_device,
            string.IsNullOrEmpty(overlay.Text) ? " " : overlay.Text,
            format, (float)(width * 0.8), (float)(height * 0.5));
        double textWidth = layout.LayoutBounds.Width;
        double textHeight = layout.LayoutBounds.Height;

        double x = overlay.X * width - textWidth / 2;
        double y = overlay.Y * height - textHeight / 2;

        var bgColor = ParseColor(overlay.BackgroundColor);
        if (bgColor.A > 0)
        {
            double padding = 12;
            var (boxInP, boxOutP) = ComputeInOutProgress(progress, overlay.Duration.TotalSeconds);
            var (_, _, _, _, op) = ComputeWholeState(overlay.Animation, boxInP, boxOutP, width, height);
            byte boxAlpha = (byte)(bgColor.A * op);
            var box = Color.FromArgb(boxAlpha, bgColor.R, bgColor.G, bgColor.B);
            ds.FillRoundedRectangle(
                (float)(x - padding), (float)(y - padding),
                (float)(textWidth + padding * 2), (float)(textHeight + padding * 2),
                8, 8, box);
        }

        var rect = new Rect(x, y, textWidth, textHeight);
        DrawAnimatedText(ds, overlay.Text, format, rect, ParseColor(overlay.TextColor),
            overlay.Animation, progress, width, height, (float)overlay.FontSize, overlay.Duration.TotalSeconds);
    }

    // ─────────────────────────── Core dispatch ───────────────────────────

    private void DrawAnimatedText(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color baseColor, TextSlideAnimation anim, double progress,
        int canvasWidth, int canvasHeight, float fontSize, double durationSeconds)
    {
        if (string.IsNullOrEmpty(text)) return;

        double elapsedSeconds = Math.Clamp(progress, 0, 1) * Math.Max(0.001, durationSeconds);
        var (inP, outP) = ComputeInOutProgress(progress, durationSeconds);

        if (IsPerCharacter(anim))
        {
            DrawPerCharacter(ds, text, format, rect, baseColor, anim, inP, outP, elapsedSeconds, fontSize);
            return;
        }

        float cx = (float)(rect.X + rect.Width / 2);
        float cy = (float)(rect.Y + rect.Height / 2);

        // Typewriter (with optional caret): type in, then erase out — at a constant rate.
        if (anim is TextSlideAnimation.TypeWriter or TextSlideAnimation.TypewriterCaret)
        {
            var (typed, showCaret) = TypewriterText(text, elapsedSeconds, durationSeconds);
            var col0 = WithAlpha(baseColor, 1.0);
            ds.DrawText(typed, rect, col0, format);
            if (anim == TextSlideAnimation.TypewriterCaret && showCaret)
                DrawCaret(ds, typed, format, rect, col0, fontSize, progress);
            return;
        }

        var (scale, tx, ty, blur, opacity) = ComputeWholeState(anim, inP, outP, canvasWidth, canvasHeight);
        var col = WithAlpha(baseColor, opacity);

        // Reveal: wipe in from the left, then wipe out to the left.
        if (anim == TextSlideAnimation.Reveal)
        {
            double inFrac = EaseInOutCubic(inP);
            double outFrac = EaseInOutCubic(outP);
            double visible = Math.Clamp(Math.Min(inFrac, 1 - outFrac), 0, 1);
            if (visible <= 0.001) return;
            var clip = new Rect(rect.X, rect.Y, rect.Width * visible, rect.Height);
            using (ds.CreateLayer(1f, clip))
                ds.DrawText(text, rect, WithAlpha(baseColor, 1.0), format);
            return;
        }

        if (blur > 0.25f)
        {
            DrawBlurredText(ds, text, format, rect, col, blur, scale, cx, cy, canvasWidth, canvasHeight);
            return;
        }

        var saved = ds.Transform;
        ds.Transform =
            Matrix3x2.CreateScale(scale, new Vector2(cx, cy)) *
            Matrix3x2.CreateTranslation(tx, ty);
        ds.DrawText(text, rect, col, format);
        ds.Transform = saved;
    }

    // ─────────────────────── Per-character engine ────────────────────────

    private void DrawPerCharacter(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color baseColor, TextSlideAnimation anim, double inP, double outP,
        double elapsedSeconds, float fontSize)
    {
        using var layout = new CanvasTextLayout(_device, text, format,
            (float)rect.Width, (float)rect.Height);

        using var charFormat = CreateFormat(
            format.FontFamily, format.FontSize,
            format.FontWeight.Weight >= 600, format.FontStyle == FontStyle.Italic,
            CanvasHorizontalAlignment.Left, CanvasVerticalAlignment.Top, wrap: false);

        int n = text.Length;
        int visibleCount = CountNonWhitespace(text);
        int visibleIndex = 0;
        var saved = ds.Transform;

        for (int i = 0; i < n; i++)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) continue;

            CanvasTextLayoutRegion[] regions;
            try { regions = layout.GetCharacterRegions(i, 1); }
            catch { continue; }
            if (regions.Length == 0) continue;

            var rb = regions[0].LayoutBounds;
            float bx = (float)(rect.X + rb.X);
            float by = (float)(rect.Y + rb.Y);
            float ccx = bx + (float)rb.Width / 2;
            float ccy = by + (float)rb.Height / 2;

            double frac = visibleCount <= 1 ? 0 : (double)visibleIndex / (visibleCount - 1);
            var p = ComputeCharParams(anim, inP, outP, elapsedSeconds, frac, visibleIndex, fontSize);
            visibleIndex++;

            if (p.Opacity <= 0.003) continue;

            ds.Transform =
                Matrix3x2.CreateRotation(p.Angle, new Vector2(ccx, ccy)) *
                Matrix3x2.CreateScale(p.Scale, new Vector2(ccx, ccy)) *
                Matrix3x2.CreateTranslation(p.Dx, p.Dy);

            ds.DrawText(ch.ToString(), bx, by, WithAlpha(baseColor, p.Opacity), charFormat);
        }

        ds.Transform = saved;
    }

    private readonly record struct CharParams(float Dx, float Dy, float Scale, float Angle, double Opacity);

    private static CharParams ComputeCharParams(
        TextSlideAnimation anim, double inP, double outP, double elapsedSeconds,
        double frac, int index, float fontSize)
    {
        // Continuous wave: bob at a constant frequency the whole duration, with a
        // quick constant-time fade at the very ends.
        if (anim == TextSlideAnimation.Wave)
        {
            double amp = fontSize * 0.18;
            double dy = Math.Sin(elapsedSeconds * (2 * Math.PI * WaveHz) + index * 0.55) * amp;
            double fadeIn = Math.Clamp(elapsedSeconds / WaveFadeSeconds, 0, 1);
            double op = Math.Min(fadeIn, Math.Clamp(1 - outP, 0, 1));
            return new CharParams(0, (float)dy, 1f, 0f, op);
        }

        const double spread = 0.6;

        // Entrance and exit are staggered across the (constant-time) in/out windows.
        double inCp = Math.Clamp((inP - frac * spread) / (1 - spread), 0, 1);
        double outCp = Math.Clamp((outP - frac * spread) / (1 - spread), 0, 1);

        double opacity = Math.Clamp(inCp, 0, 1) * (1 - Math.Clamp(outCp, 0, 1));

        switch (anim)
        {
            case TextSlideAnimation.CascadeFadeUp:
            {
                double inE = EaseOutCubic(inCp);
                double outE = EaseInCubic(outCp);
                float dy = (float)((1 - inE) * fontSize * 0.6 + outE * (-fontSize * 0.6));
                return new CharParams(0, dy, 1f, 0f, opacity);
            }
            case TextSlideAnimation.CascadePop:
            {
                float sIn = (float)Math.Max(0, EaseOutBack(inCp));
                float sOut = (float)(1 - EaseInCubic(outCp));
                float s = Math.Max(0, sIn * sOut);
                return new CharParams(0, 0, s, 0f, opacity);
            }
            case TextSlideAnimation.TrackingIn:
            {
                double inE = EaseOutCubic(inCp);
                double outE = EaseInCubic(outCp);
                float track = fontSize * 0.9f;
                float dx = (float)((frac - 0.5) * ((1 - inE) + outE) * track * 2);
                return new CharParams(dx, 0, 1f, 0f, opacity);
            }
            case TextSlideAnimation.RotateIn:
            {
                double inE = EaseOutBack(inCp);
                double outE = EaseInCubic(outCp);
                float angle = (float)((1 - inE) * (-Math.PI / 2) + outE * (Math.PI / 2));
                float s = (float)(0.6 + 0.4 * Math.Clamp(inCp, 0, 1)) * (float)(1 - 0.4 * outE);
                return new CharParams(0, 0, s, angle, opacity);
            }
            case TextSlideAnimation.BounceIn:
            {
                double inE = EaseOutBounce(inCp);
                double outE = EaseInCubic(outCp);
                float dy = (float)((1 - inE) * (-fontSize * 0.9) + outE * (-fontSize * 0.9));
                return new CharParams(0, dy, 1f, 0f, opacity);
            }
            default:
                return new CharParams(0, 0, 1f, 0f, opacity);
        }
    }

    // ───────────────────────── Whole-text helpers ────────────────────────

    /// <summary>
    /// Computes the combined entrance + exit state for a whole-text animation.
    /// Returns scale, translation, blur amount, and opacity for the given progress.
    /// </summary>
    private static (float Scale, float Tx, float Ty, float Blur, double Opacity) ComputeWholeState(
        TextSlideAnimation anim, double inP, double outP, int width, int height)
    {
        // Default opacity: fade in over IN, fully visible, fade out over OUT.
        double fadeOpacity = EaseOutCubic(inP) * (1 - EaseInCubic(outP));

        float scale = 1f, tx = 0f, ty = 0f, blur = 0f;
        double opacity = fadeOpacity;

        switch (anim)
        {
            case TextSlideAnimation.None:
                opacity = 1;
                break;

            case TextSlideAnimation.FadeIn:
                // opacity already = fade in + fade out
                break;

            case TextSlideAnimation.SlideUp:
                ty = (float)((1 - EaseOutCubic(inP)) * height * 0.12
                           + EaseInCubic(outP) * (-height * 0.12));
                break;
            case TextSlideAnimation.SlideDown:
                ty = (float)((1 - EaseOutCubic(inP)) * (-height * 0.12)
                           + EaseInCubic(outP) * (height * 0.12));
                break;
            case TextSlideAnimation.SlideLeft:
                tx = (float)((1 - EaseOutCubic(inP)) * width * 0.12
                           + EaseInCubic(outP) * (-width * 0.12));
                break;
            case TextSlideAnimation.SlideRight:
                tx = (float)((1 - EaseOutCubic(inP)) * (-width * 0.12)
                           + EaseInCubic(outP) * (width * 0.12));
                break;

            case TextSlideAnimation.ScalePop:
            {
                float sIn = (float)(0.6 + 0.4 * EaseOutBack(inP));
                scale = sIn - (float)(0.4 * EaseInCubic(outP)); // shrink out toward 0.6
                break;
            }

            case TextSlideAnimation.ZoomBlurIn:
            {
                float sIn = (float)(1.6 - 0.6 * EaseOutCubic(inP)); // 1.6 → 1.0
                scale = sIn + (float)(0.4 * EaseInCubic(outP));     // zoom back out on exit
                blur = (float)((1 - EaseOutCubic(inP)) * 28 + EaseInCubic(outP) * 22);
                break;
            }

            default:
                break;
        }

        return (scale, tx, ty, blur, Math.Clamp(opacity, 0, 1));
    }

    private static (string Text, bool Caret) TypewriterText(string text, double elapsedSeconds, double durationSeconds)
    {
        // Type in at a constant rate, hold, then erase at a constant rate near the end,
        // so the typing speed is identical regardless of slide length or text amount.
        double dur = Math.Max(0.001, durationSeconds);
        double eraseSeconds = Math.Min(text.Length / TypewriterEraseCharsPerSecond, dur * 0.3);
        double eraseStart = dur - eraseSeconds;

        int typed = (int)Math.Round(elapsedSeconds * TypewriterCharsPerSecond);
        int erased = eraseSeconds > 1e-6
            ? (int)Math.Round(Math.Max(0, elapsedSeconds - eraseStart) * TypewriterEraseCharsPerSecond)
            : 0;
        int shown = Math.Clamp(typed - erased, 0, text.Length);

        bool caret = ((int)(elapsedSeconds / 0.5)) % 2 == 0;
        return (text[..shown], caret);
    }

    private void DrawBlurredText(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color color, float blurAmount, float scale, float cx, float cy,
        int width, int height)
    {
        using var rt = new CanvasRenderTarget(_device, width, height, 96);
        using (var rds = rt.CreateDrawingSession())
        {
            rds.Clear(Color.FromArgb(0, 0, 0, 0));
            rds.DrawText(text, rect, color, format);
        }

        using var blur = new GaussianBlurEffect
        {
            Source = rt,
            BlurAmount = blurAmount,
            BorderMode = EffectBorderMode.Soft,
        };

        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(scale, new Vector2(cx, cy));
        ds.DrawImage(blur);
        ds.Transform = saved;
    }

    private void DrawCaret(
        CanvasDrawingSession ds, string drawn, CanvasTextFormat format, Rect rect,
        Color color, float fontSize, double progress)
    {
        using var layout = new CanvasTextLayout(_device,
            drawn.Length == 0 ? " " : drawn, format, (float)rect.Width, (float)rect.Height);
        double textWidth = layout.LayoutBounds.Width;

        float caretX = (float)(rect.X + rect.Width / 2 + textWidth / 2 + fontSize * 0.08);
        float caretY = (float)(rect.Y + rect.Height / 2 - fontSize / 2);
        float caretW = Math.Max(2f, fontSize * 0.08f);

        ds.FillRectangle(caretX, caretY, caretW, fontSize, color);
    }

    // ───────────────────────────── Utilities ─────────────────────────────

    private static bool IsPerCharacter(TextSlideAnimation a) => a is
        TextSlideAnimation.CascadeFadeUp or
        TextSlideAnimation.CascadePop or
        TextSlideAnimation.Wave or
        TextSlideAnimation.TrackingIn or
        TextSlideAnimation.RotateIn or
        TextSlideAnimation.BounceIn;

    private static int CountNonWhitespace(string s)
    {
        int c = 0;
        foreach (var ch in s) if (!char.IsWhiteSpace(ch)) c++;
        return c;
    }

    private static CanvasTextFormat CreateFormat(
        string family, double size, bool bold, bool italic,
        CanvasHorizontalAlignment h, CanvasVerticalAlignment v, bool wrap) => new()
    {
        FontFamily = family,
        FontSize = (float)size,
        FontWeight = bold ? new FontWeight { Weight = 700 } : new FontWeight { Weight = 400 },
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        HorizontalAlignment = h,
        VerticalAlignment = v,
        WordWrapping = wrap ? CanvasWordWrapping.WholeWord : CanvasWordWrapping.NoWrap,
    };

    private static Color WithAlpha(Color c, double opacity) =>
        Color.FromArgb((byte)(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);

    // ── Easing functions ──
    private static double EaseInCubic(double t) { t = Math.Clamp(t, 0, 1); return t * t * t; }
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static double EaseInOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    private static double EaseOutBack(double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseOutBounce(double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) { t -= 1.5 / d1; return n1 * t * t + 0.75; }
        if (t < 2.5 / d1) { t -= 2.25 / d1; return n1 * t * t + 0.9375; }
        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Color.FromArgb(255,
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber)),
            8 => Color.FromArgb(
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber)),
            _ => Color.FromArgb(255, 255, 255, 255),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgImage?.Dispose();
        GC.SuppressFinalize(this);
    }
}
