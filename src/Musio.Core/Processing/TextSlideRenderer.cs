using System.Numerics;
using Microsoft.Graphics.Canvas;
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
/// After-Effects-style text animations driven by normalized progress (0..1)
/// within the segment or overlay duration.
/// </summary>
public class TextSlideRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private bool _disposed;

    public TextSlideRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
    }

    /// <summary>
    /// Renders a full-screen text slide frame.
    /// </summary>
    public CanvasRenderTarget RenderSlide(TextSlideSegment slide, double progress, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = new CanvasRenderTarget(_device, width, height, 96);
        using var ds = target.CreateDrawingSession();

        ds.Clear(ParseColor(slide.BackgroundColor));

        using var format = CreateFormat(
            slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
            CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center, wrap: true);

        var rect = new Rect(width * 0.08, height * 0.1, width * 0.84, height * 0.8);

        DrawAnimatedText(ds, slide.Text, format, rect, ParseColor(slide.TextColor),
            slide.Animation, progress, width, height, (float)slide.FontSize);

        return target;
    }

    /// <summary>
    /// Renders a text overlay onto an existing drawing session.
    /// </summary>
    public void RenderOverlay(CanvasDrawingSession ds, TextOverlay overlay, double progress, int width, int height)
    {
        using var format = CreateFormat(
            overlay.FontFamily, overlay.FontSize, overlay.IsBold, overlay.IsItalic,
            CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center, wrap: false);

        // Measure to center the overlay around its normalized position
        using var layout = new CanvasTextLayout(_device,
            string.IsNullOrEmpty(overlay.Text) ? " " : overlay.Text,
            format, (float)(width * 0.8), (float)(height * 0.5));
        double textWidth = layout.LayoutBounds.Width;
        double textHeight = layout.LayoutBounds.Height;

        double x = overlay.X * width - textWidth / 2;
        double y = overlay.Y * height - textHeight / 2;

        // Optional background box
        var bgColor = ParseColor(overlay.BackgroundColor);
        if (bgColor.A > 0)
        {
            double padding = 12;
            byte boxAlpha = (byte)(bgColor.A * ComputeWholeOpacity(overlay.Animation, progress));
            var box = Color.FromArgb(boxAlpha, bgColor.R, bgColor.G, bgColor.B);
            ds.FillRoundedRectangle(
                (float)(x - padding), (float)(y - padding),
                (float)(textWidth + padding * 2), (float)(textHeight + padding * 2),
                8, 8, box);
        }

        var rect = new Rect(x, y, textWidth, textHeight);
        DrawAnimatedText(ds, overlay.Text, format, rect, ParseColor(overlay.TextColor),
            overlay.Animation, progress, width, height, (float)overlay.FontSize);
    }

    // ─────────────────────────── Core dispatch ───────────────────────────

    private void DrawAnimatedText(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color baseColor, TextSlideAnimation anim, double progress,
        int canvasWidth, int canvasHeight, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (IsPerCharacter(anim))
        {
            DrawPerCharacter(ds, text, format, rect, baseColor, anim, progress, fontSize);
            return;
        }

        // ── Whole-text animations ──
        string draw = text;
        bool caret = false;
        if (anim is TextSlideAnimation.TypeWriter or TextSlideAnimation.TypewriterCaret)
        {
            double typed = Math.Clamp(progress / 0.8, 0, 1);
            int count = (int)Math.Round(text.Length * typed);
            draw = text[..Math.Min(count, text.Length)];
            caret = anim == TextSlideAnimation.TypewriterCaret;
        }

        double opacity = ComputeWholeOpacity(anim, progress);
        var col = WithAlpha(baseColor, opacity);

        float cx = (float)(rect.X + rect.Width / 2);
        float cy = (float)(rect.Y + rect.Height / 2);

        // Reveal: left-to-right mask wipe
        if (anim == TextSlideAnimation.Reveal)
        {
            double rp = EaseInOutCubic(Math.Clamp(progress / 0.45, 0, 1));
            var clip = new Rect(rect.X, rect.Y, rect.Width * rp, rect.Height);
            using (ds.CreateLayer(1f, clip))
                ds.DrawText(draw, rect, col, format);
            return;
        }

        var (scale, tx, ty, blur) = ComputeWholeTransform(anim, progress, canvasWidth, canvasHeight);

        // Blur path (cinematic zoom-blur title)
        if (blur > 0.25f)
        {
            DrawBlurredText(ds, draw, format, rect, col, blur, scale, cx, cy, canvasWidth, canvasHeight);
            return;
        }

        var saved = ds.Transform;
        ds.Transform =
            Matrix3x2.CreateScale(scale, new Vector2(cx, cy)) *
            Matrix3x2.CreateTranslation(tx, ty);

        ds.DrawText(draw, rect, col, format);

        if (caret)
            DrawCaret(ds, draw, format, rect, col, fontSize, progress);

        ds.Transform = saved;
    }

    // ─────────────────────── Per-character engine ────────────────────────

    private void DrawPerCharacter(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color baseColor, TextSlideAnimation anim, double progress, float fontSize)
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
            if (char.IsWhiteSpace(ch)) { continue; }

            CanvasTextLayoutRegion[] regions;
            try { regions = layout.GetCharacterRegions(i, 1); }
            catch { continue; }
            if (regions.Length == 0) continue;

            var rb = regions[0].LayoutBounds;
            float bx = (float)(rect.X + rb.X);
            float by = (float)(rect.Y + rb.Y);
            float ccx = bx + (float)rb.Width / 2;
            float ccy = by + (float)rb.Height / 2;

            // Stagger across the (visible) characters
            double frac = visibleCount <= 1 ? 0 : (double)visibleIndex / (visibleCount - 1);

            var p = ComputeCharParams(anim, progress, frac, visibleIndex, fontSize);
            visibleIndex++;

            if (p.Opacity <= 0.003) continue;

            var m =
                Matrix3x2.CreateRotation(p.Angle, new Vector2(ccx, ccy)) *
                Matrix3x2.CreateScale(p.Scale, new Vector2(ccx, ccy)) *
                Matrix3x2.CreateTranslation(p.Dx, p.Dy);
            ds.Transform = m;

            ds.DrawText(ch.ToString(), bx, by, WithAlpha(baseColor, p.Opacity), charFormat);
        }

        ds.Transform = saved;
    }

    private readonly record struct CharParams(float Dx, float Dy, float Scale, float Angle, double Opacity);

    private static CharParams ComputeCharParams(
        TextSlideAnimation anim, double progress, double frac, int index, float fontSize)
    {
        // Continuous animation (not intro-staggered)
        if (anim == TextSlideAnimation.Wave)
        {
            double amp = fontSize * 0.18;
            double dy = Math.Sin(progress * Math.PI * 4 + index * 0.55) * amp;
            double op = Math.Clamp(progress / 0.08, 0, 1);
            return new CharParams(0, (float)dy, 1f, 0f, op);
        }

        // Intro-staggered: char `frac` starts later; all finish by progress≈1
        const double spread = 0.6;
        const double introEnd = 0.6;
        double pIntro = Math.Clamp(progress / introEnd, 0, 1);
        double cp = Math.Clamp((pIntro - frac * spread) / (1 - spread), 0, 1);

        switch (anim)
        {
            case TextSlideAnimation.CascadeFadeUp:
            {
                double e = EaseOutCubic(cp);
                float dy = (float)((1 - e) * fontSize * 0.6);
                return new CharParams(0, dy, 1f, 0f, cp);
            }
            case TextSlideAnimation.CascadePop:
            {
                float s = (float)Math.Max(0, EaseOutBack(cp));
                double op = Math.Clamp(cp * 1.6, 0, 1);
                return new CharParams(0, 0, s, 0f, op);
            }
            case TextSlideAnimation.TrackingIn:
            {
                // Letters start spread out and condense to normal spacing + fade
                double e = EaseOutCubic(Math.Clamp(progress / 0.55, 0, 1));
                float track = fontSize * 0.9f;
                float dx = (float)((frac - 0.5) * (1 - e) * track * 2);
                double op = Math.Clamp(progress / 0.4, 0, 1);
                return new CharParams(dx, 0, 1f, 0f, op);
            }
            case TextSlideAnimation.RotateIn:
            {
                double e = EaseOutBack(cp);
                float angle = (float)((1 - e) * (-Math.PI / 2));
                float s = (float)(0.6 + 0.4 * Math.Clamp(cp, 0, 1));
                return new CharParams(0, 0, s, angle, cp);
            }
            case TextSlideAnimation.BounceIn:
            {
                double e = EaseOutBounce(cp);
                float dy = (float)((1 - e) * (-fontSize * 0.9));
                return new CharParams(0, dy, 1f, 0f, Math.Clamp(cp * 2, 0, 1));
            }
            default:
                return new CharParams(0, 0, 1f, 0f, 1);
        }
    }

    // ───────────────────────── Whole-text helpers ────────────────────────

    private static double ComputeWholeOpacity(TextSlideAnimation anim, double progress)
    {
        const double fade = 0.2;
        return anim switch
        {
            TextSlideAnimation.None => 1,
            TextSlideAnimation.FadeIn => EaseOutCubic(Math.Clamp(progress / fade, 0, 1)),
            TextSlideAnimation.FadeOut => EaseOutCubic(Math.Clamp((1 - progress) / fade, 0, 1)),
            TextSlideAnimation.FadeInOut => progress < 0.5
                ? EaseOutCubic(Math.Clamp(progress / fade, 0, 1))
                : EaseOutCubic(Math.Clamp((1 - progress) / fade, 0, 1)),
            TextSlideAnimation.SlideUp or TextSlideAnimation.SlideDown
                or TextSlideAnimation.SlideLeft or TextSlideAnimation.SlideRight
                => EaseOutCubic(Math.Clamp(progress / 0.25, 0, 1)),
            TextSlideAnimation.ScalePop => Math.Clamp(progress / 0.2, 0, 1),
            TextSlideAnimation.ZoomBlurIn => Math.Clamp(progress / 0.25, 0, 1),
            _ => 1,
        };
    }

    private static (float Scale, float Tx, float Ty, float Blur) ComputeWholeTransform(
        TextSlideAnimation anim, double progress, int width, int height)
    {
        switch (anim)
        {
            case TextSlideAnimation.SlideUp:
            {
                double e = EaseOutCubic(Math.Clamp(progress / 0.3, 0, 1));
                return (1f, 0f, (float)((1 - e) * height * 0.12), 0f);
            }
            case TextSlideAnimation.SlideDown:
            {
                double e = EaseOutCubic(Math.Clamp(progress / 0.3, 0, 1));
                return (1f, 0f, (float)((1 - e) * -height * 0.12), 0f);
            }
            case TextSlideAnimation.SlideLeft:
            {
                double e = EaseOutCubic(Math.Clamp(progress / 0.3, 0, 1));
                return (1f, (float)((1 - e) * width * 0.12), 0f, 0f);
            }
            case TextSlideAnimation.SlideRight:
            {
                double e = EaseOutCubic(Math.Clamp(progress / 0.3, 0, 1));
                return (1f, (float)((1 - e) * -width * 0.12), 0f, 0f);
            }
            case TextSlideAnimation.ScalePop:
            {
                float s = (float)(0.6 + 0.4 * EaseOutBack(Math.Clamp(progress / 0.4, 0, 1)));
                return (s, 0f, 0f, 0f);
            }
            case TextSlideAnimation.ZoomBlurIn:
            {
                double e = EaseOutCubic(Math.Clamp(progress / 0.4, 0, 1));
                float s = (float)(1.6 - 0.6 * e);
                float blur = (float)((1 - e) * 28);
                return (s, 0f, 0f, blur);
            }
            default:
                return (1f, 0f, 0f, 0f);
        }
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
        // Blink roughly twice per second of progress-normalized time
        bool visible = ((int)(progress / 0.04)) % 2 == 0;
        if (!visible) return;

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
        GC.SuppressFinalize(this);
    }
}
