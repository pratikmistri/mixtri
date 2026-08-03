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
/// The shared cinematic / After-Effects-style text animation engine used by every
/// renderer that draws animated <see cref="TextSlideAnimation"/> text (full-screen
/// text slides via <see cref="TextSlideRenderer"/>, and animated text overlays via
/// a future <c>TextOverlayRenderer</c>). Every animation has a matching IN
/// (entrance) at the start and OUT (exit) at the end, driven by normalized
/// progress (0..1) over a constant wall-clock duration, with a static hold in the
/// middle for slides/overlays long enough to have one.
/// </summary>
public class AnimatedTextEngine : IDisposable
{
    private readonly CanvasDevice _device;
    private bool _disposed;

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

    // Reusable scratch target for DrawBlurredText (the ZoomBlurIn whole-text path).
    // A DrawAnimatedText call sequence for one overlay/slide frame can invoke this
    // several times in a row (e.g. TextOverlayRenderer's OutlineShadow background draws
    // a shadow pass, up to 8 outline passes, and a fill pass — all through this engine),
    // and each call used to allocate a brand-new full-frame CanvasRenderTarget. Caching
    // one target here (allocate-then-swap, keyed on device+size) turns that into a single
    // allocation that is reused both within a frame and across frames, which is the fix
    // for the render-target churn described in the "OutlineShadow x blurred animations"
    // review finding. Every call fully overwrites the target (Clear then Draw) before
    // reading it back, so reuse across passes/frames is safe.
    private CanvasRenderTarget? _blurScratch;
    private (int W, int H) _blurScratchKey;

    public AnimatedTextEngine(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
    }

    /// <summary>
    /// Maps normalized <paramref name="progress"/> (0..1 over the whole slide) to
    /// independent entrance/exit progresses (each 0..1) that advance over a constant
    /// number of seconds. The hold in the middle grows or shrinks with the slide's
    /// duration while the entrance and exit motion stay at a fixed, consistent speed.
    /// </summary>
    public static (double InP, double OutP) ComputeInOutProgress(double progress, double durationSeconds)
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

    /// <summary>
    /// The whole-text animation envelope — scale, translation and opacity — at a given
    /// progress, for callers that need to animate something *alongside* the text (such as a
    /// text overlay's background box, which must move and fade with its text rather than
    /// sitting statically behind it). Per-character animations have no single whole-text
    /// transform, so they report an identity transform with a plain fade envelope, which
    /// keeps a background box steady while its characters animate individually.
    /// </summary>
    public static (float Scale, float Tx, float Ty, double Opacity) ComputeEnvelope(
        TextSlideAnimation anim, double progress, double durationSeconds, int width, int height)
    {
        var (inP, outP) = ComputeInOutProgress(progress, durationSeconds);

        // Typewriter (with or without caret) draws the text fully opaque for its whole
        // duration (see DrawAnimatedText) — the background must not fade with it.
        if (anim is TextSlideAnimation.TypeWriter or TextSlideAnimation.TypewriterCaret)
            return (1f, 0f, 0f, 1.0);

        if (IsPerCharacter(anim))
        {
            double fade;
            if (anim == TextSlideAnimation.Wave)
            {
                // Wave is continuous (no discrete entrance) — mirror the fade envelope
                // ComputeCharParams uses: a quick fade-in over WaveFadeSeconds, then
                // tracking the exit progress out.
                double elapsedSeconds = Math.Clamp(progress, 0, 1) * Math.Max(0.001, durationSeconds);
                double fadeIn = Math.Clamp(elapsedSeconds / WaveFadeSeconds, 0, 1);
                fade = Math.Min(fadeIn, Math.Clamp(1 - outP, 0, 1));
            }
            else
            {
                fade = EaseOutCubic(inP) * (1 - EaseInCubic(outP));
            }

            return (1f, 0f, 0f, Math.Clamp(fade, 0, 1));
        }

        var (scale, tx, ty, _, opacity) = ComputeWholeState(anim, inP, outP, width, height);
        return (scale, tx, ty, opacity);
    }

    // ─────────────────────────── Core dispatch ───────────────────────────

    public void DrawAnimatedText(
        CanvasDrawingSession ds, string text, CanvasTextFormat format, Rect rect,
        Color baseColor, TextSlideAnimation anim, double progress,
        int canvasWidth, int canvasHeight, float fontSize, double durationSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
    public static (float Scale, float Tx, float Ty, float Blur, double Opacity) ComputeWholeState(
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

    public static (string Text, bool Caret) TypewriterText(string text, double elapsedSeconds, double durationSeconds)
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
        // Reuse the cached scratch target instead of allocating a fresh full-frame
        // CanvasRenderTarget on every call — see the field doc comment on
        // _blurScratch for why this matters (OutlineShadow can invoke this engine up
        // to 10 times per frame for a ZoomBlurIn-animated overlay/slide).
        var rt = EnsureBlurScratch(width, height);
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

    /// <summary>
    /// Returns the cached blur-scratch target, (re)allocating it via allocate-then-swap
    /// only when the size or device has changed (or it doesn't exist yet) — the normal
    /// case for a stable preview/export resolution is zero allocations after the first
    /// call. A failed allocation never leaves <see cref="_blurScratch"/> pointing at a
    /// disposed target, matching the pattern already used by
    /// <c>TextOverlayRenderer.EnsureBlurScratch</c>.
    /// </summary>
    private CanvasRenderTarget EnsureBlurScratch(int width, int height)
    {
        if (_blurScratch is null || _blurScratch.Device != _device || _blurScratchKey != (width, height))
        {
            var next = new CanvasRenderTarget(_device, width, height, 96);
            _blurScratch?.Dispose();
            _blurScratch = next;
            _blurScratchKey = (width, height);
        }
        return _blurScratch;
    }

    /// <summary>
    /// Disposes the cached blur-scratch target. <see cref="AnimatedTextEngine"/> is a
    /// shared helper used by both <see cref="TextOverlayRenderer"/> (which now disposes
    /// its instance) and <c>TextSlideRenderer</c> (which currently does not — see that
    /// type's own review follow-up). Disposal here is intentionally idempotent and the
    /// only disposable state is this one bounded, keyed cache entry, so even an owner
    /// that never calls <see cref="Dispose"/> only leaks a single render target sized to
    /// its last-used resolution rather than growing unboundedly.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blurScratch?.Dispose();
        GC.SuppressFinalize(this);
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

    public static bool IsPerCharacter(TextSlideAnimation a) => a is
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

    internal static CanvasTextFormat CreateFormat(
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

    internal static Color ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Color.FromArgb(255, 255, 255, 255);

        hex = hex.Trim().TrimStart('#');

        // Expand 3-digit (RGB) and 4-digit (ARGB) shorthand to full form.
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        else if (hex.Length == 4)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]);

        const System.Globalization.NumberStyles Style = System.Globalization.NumberStyles.HexNumber;
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), Style, ci, out var r) &&
            byte.TryParse(hex.AsSpan(2, 2), Style, ci, out var g) &&
            byte.TryParse(hex.AsSpan(4, 2), Style, ci, out var b))
            return Color.FromArgb(255, r, g, b);

        if (hex.Length == 8 &&
            byte.TryParse(hex.AsSpan(0, 2), Style, ci, out var a) &&
            byte.TryParse(hex.AsSpan(2, 2), Style, ci, out var r2) &&
            byte.TryParse(hex.AsSpan(4, 2), Style, ci, out var g2) &&
            byte.TryParse(hex.AsSpan(6, 2), Style, ci, out var b2))
            return Color.FromArgb(a, r2, g2, b2);

        return Color.FromArgb(255, 255, 255, 255);
    }
}
