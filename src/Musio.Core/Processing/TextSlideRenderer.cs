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

    // Entrance occupies the first In fraction; exit occupies the last Out fraction.
    private const double InDur = 0.25;
    private const double OutDur = 0.25;

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

        DrawSlideBackground(ds, slide, width, height);

        if (!drawText)
            return target;

        using var format = CreateFormat(
            slide.FontFamily, slide.FontSize, slide.IsBold, slide.IsItalic,
            ToCanvasAlignment(slide.TextAlignment), CanvasVerticalAlignment.Center, wrap: true);

        var rect = ComputeTextRect(slide, width, height);

        DrawAnimatedText(ds, slide.Text, format, rect, ParseColor(slide.TextColor),
            slide.Animation, progress, width, height, (float)slide.FontSize);

        return target;
    }

    /// <summary>
    /// The text block rectangle for a slide, in output pixels, centered at the
    /// slide's normalized (TextX, TextY) position.
    /// </summary>
    public static Rect ComputeTextRect(TextSlideSegment slide, int width, int height)
    {
        double boxW = width * 0.84;
        double boxH = height * 0.8;
        double left = slide.TextX * width - boxW / 2;
        double top = slide.TextY * height - boxH / 2;
        return new Rect(left, top, boxW, boxH);
    }

    private static CanvasHorizontalAlignment ToCanvasAlignment(SlideTextAlignment a) => a switch
    {
        SlideTextAlignment.Left => CanvasHorizontalAlignment.Left,
        SlideTextAlignment.Right => CanvasHorizontalAlignment.Right,
        _ => CanvasHorizontalAlignment.Center,
    };

    // ─────────────────────────── Backgrounds ─────────────────────────────

    private void DrawSlideBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, int width, int height)
    {
        switch (slide.BackgroundType)
        {
            case SlideBackgroundType.Gradient:
                DrawGradientBackground(ds, slide, width, height);
                break;
            case SlideBackgroundType.Image:
                DrawImageBackground(ds, slide, width, height);
                break;
            default:
                ds.Clear(ParseColor(slide.BackgroundColor));
                break;
        }
    }

    private void DrawGradientBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, int width, int height)
    {
        double angleRad = slide.GradientAngle * Math.PI / 180.0;
        float cx = width / 2f, cy = height / 2f;
        float diag = MathF.Sqrt(width * width + height * height) / 2f;
        float dx = diag * MathF.Cos((float)angleRad);
        float dy = diag * MathF.Sin((float)angleRad);

        using var brush = new CanvasLinearGradientBrush(
            ds, ParseColor(slide.BackgroundColor), ParseColor(slide.GradientEndColor))
        {
            StartPoint = new Vector2(cx - dx, cy - dy),
            EndPoint = new Vector2(cx + dx, cy + dy),
        };
        ds.FillRectangle(0, 0, width, height, brush);
    }

    private void DrawImageBackground(
        CanvasDrawingSession ds, TextSlideSegment slide, int width, int height)
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

        DrawScaledToFill(ds, _bgImage!, width, height);
    }

    private static void DrawScaledToFill(CanvasDrawingSession ds, CanvasBitmap bitmap, int width, int height)
    {
        var src = bitmap.SizeInPixels;
        float scale = Math.Max((float)width / src.Width, (float)height / src.Height);
        float drawW = src.Width * scale;
        float drawH = src.Height * scale;
        float drawX = (width - drawW) / 2f;
        float drawY = (height - drawH) / 2f;
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
            var (_, _, _, _, op) = ComputeWholeState(overlay.Animation, progress, width, height);
            byte boxAlpha = (byte)(bgColor.A * op);
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

        float cx = (float)(rect.X + rect.Width / 2);
        float cy = (float)(rect.Y + rect.Height / 2);

        // Typewriter (with optional caret): type in, then erase out.
        if (anim is TextSlideAnimation.TypeWriter or TextSlideAnimation.TypewriterCaret)
        {
            var (typed, showCaret) = TypewriterText(text, progress);
            var col0 = WithAlpha(baseColor, 1.0);
            ds.DrawText(typed, rect, col0, format);
            if (anim == TextSlideAnimation.TypewriterCaret && showCaret)
                DrawCaret(ds, typed, format, rect, col0, fontSize, progress);
            return;
        }

        var (scale, tx, ty, blur, opacity) = ComputeWholeState(anim, progress, canvasWidth, canvasHeight);
        var col = WithAlpha(baseColor, opacity);

        // Reveal: wipe in from the left, then wipe out to the left.
        if (anim == TextSlideAnimation.Reveal)
        {
            double inFrac = EaseInOutCubic(Math.Clamp(progress / InDur, 0, 1));
            double outFrac = EaseInOutCubic(Math.Clamp((progress - (1 - OutDur)) / OutDur, 0, 1));
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
            var p = ComputeCharParams(anim, progress, frac, visibleIndex, fontSize);
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
        TextSlideAnimation anim, double progress, double frac, int index, float fontSize)
    {
        // Continuous wave: bob the whole duration, fade in/out at the very ends.
        if (anim == TextSlideAnimation.Wave)
        {
            double amp = fontSize * 0.18;
            double dy = Math.Sin(progress * Math.PI * 4 + index * 0.55) * amp;
            double op = Math.Min(Math.Clamp(progress / 0.08, 0, 1),
                                 Math.Clamp((1 - progress) / 0.08, 0, 1));
            return new CharParams(0, (float)dy, 1f, 0f, op);
        }

        const double spread = 0.6;

        // Entrance: staggered across first part of the slide.
        double pIn = Math.Clamp(progress / 0.5, 0, 1);
        double inCp = Math.Clamp((pIn - frac * spread) / (1 - spread), 0, 1);

        // Exit: staggered across the last part of the slide.
        double pOut = Math.Clamp((progress - 0.6) / 0.4, 0, 1);
        double outCp = Math.Clamp((pOut - frac * spread) / (1 - spread), 0, 1);

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
        TextSlideAnimation anim, double progress, int width, int height)
    {
        double inP = Math.Clamp(progress / InDur, 0, 1);
        double outP = Math.Clamp((progress - (1 - OutDur)) / OutDur, 0, 1);

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
            case TextSlideAnimation.FadeInOut:
                // opacity already = fade in + fade out
                break;

            case TextSlideAnimation.FadeOut:
                // Visible from the start, fades only at the end.
                opacity = 1 - EaseInCubic(outP);
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

    private static (string Text, bool Caret) TypewriterText(string text, double progress)
    {
        // Type in over the first ~45%, hold, then erase over the last ~30%.
        double typeIn = Math.Clamp(progress / 0.45, 0, 1);
        double eraseOut = Math.Clamp((progress - 0.7) / 0.3, 0, 1);

        int typed = (int)Math.Round(text.Length * typeIn);
        int erased = (int)Math.Round(text.Length * eraseOut);
        int shown = Math.Clamp(typed - erased, 0, text.Length);

        bool caret = ((int)(progress / 0.04)) % 2 == 0;
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
