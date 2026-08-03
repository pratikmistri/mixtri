using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Musio.Core.Processing;

/// <summary>
/// Draws animated <see cref="TextOverlaySegment"/>s on top of an already-composed output
/// frame. Unlike <see cref="TextSlideRenderer"/> (which replaces the whole frame with a
/// slide), every overlay background here is clipped to its own text box, so the recording
/// underneath continues to show through everywhere else. The cinematic entrance/exit
/// animation math is shared with <see cref="TextSlideRenderer"/> via
/// <see cref="AnimatedTextEngine"/> rather than duplicated.
/// </summary>
public class TextOverlayRenderer : IDisposable
{
    /// <summary>
    /// Reference output height an overlay's pixel-valued properties (font size, corner
    /// radius, outline width, accent thickness, blur amount) are authored against.
    /// Every such value is multiplied by <c>height / ReferenceHeight</c> before use, so an
    /// overlay looks proportionally identical whether the preview renders at 720p or the
    /// export renders at 4K — without this, the same overlay would look tiny on a small
    /// canvas and enormous on a large one.
    /// </summary>
    private const double ReferenceHeight = 1080.0;

    /// <summary>
    /// Number of offset copies drawn for the <see cref="TextOverlayBackground.OutlineShadow"/>
    /// outline pass. Kept small (evenly spaced around a circle) since each pass re-measures
    /// and redraws the whole text via <see cref="AnimatedTextEngine.DrawAnimatedText"/>.
    /// </summary>
    private const int OutlinePassCount = 8;

    private readonly CanvasDevice _device;
    private readonly AnimatedTextEngine _textEngine;
    private bool _disposed;

    // Scratch render target used to sample the already-composed frame for the
    // frosted-glass (Blur) background. Allocated lazily — only the first time an active
    // overlay actually uses Blur — and resized via allocate-then-swap after that so a
    // failed allocation never leaves the field pointing at a disposed target, and normal
    // frames with no Blur overlay never touch the GPU for it at all.
    private CanvasRenderTarget? _blurScratch;
    private (int W, int H) _blurScratchKey;

    // Cached Blur effect graph (BorderEffect -> GaussianBlurEffect), rebuilt only when the
    // scratch target it reads from has been reallocated (a resize — see EnsureBlurScratch).
    // The common frame-to-frame case (same size, only BlurAmount potentially changing) just
    // updates the cheap BlurAmount property on the cached GaussianBlurEffect instead of
    // rebuilding the graph. Also fixes a real leak: the previous code constructed a fresh
    // BorderEffect/GaussianBlurEffect pair inline as a `using` local's initializer — if
    // CopyFrameIntoBlurScratch (GPU work) or a setter inside the initializer threw, the
    // partially-built effect was never captured by the `using` and never disposed. Because
    // these are now renderer-owned fields built in a separate step (not inline in a `using`
    // initializer) and disposed unconditionally in Dispose(), there is no such exception path.
    private BorderEffect? _blurBorderEffect;
    private GaussianBlurEffect? _blurGaussianEffect;

    /// <summary>
    /// Per-overlay cache of the Win2D text-measurement/draw resources that <see cref="Render"/>
    /// would otherwise allocate fresh on every single frame for every active overlay (this
    /// method runs once per preview/export frame). Keyed by <see cref="TimelineSegment.Id"/>
    /// and invalidated (allocate-then-swap, disposing the stale entry) whenever any property
    /// that affects measurement or drawing actually changes, so editing an overlay's text or
    /// font mid-session is still picked up immediately.
    /// </summary>
    private sealed class OverlayCache : IDisposable
    {
        public OverlayCacheKey Key;
        public required CanvasTextFormat DrawFormat;
        public double TextW;
        public double TextH;

        public void Dispose() => DrawFormat.Dispose();
    }

    /// <summary>Everything that affects the measured size and the draw format for an overlay.</summary>
    private readonly record struct OverlayCacheKey(
        string FontFamily, float FontSize, bool IsBold, bool IsItalic,
        SlideTextAlignment TextAlignment, string Text, double MaxWidth, int Width, int Height, CanvasDevice Device);

    private readonly Dictionary<string, OverlayCache> _overlayCache = new();

    public TextOverlayRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
        _textEngine = new AnimatedTextEngine(_device);
    }

    /// <summary>
    /// Draws every active text overlay on top of an already-composed frame.
    /// <paramref name="target"/> is the finished output frame; it is both read (for the
    /// frosted-blur background, which samples the video behind the text) and drawn into,
    /// which is why this takes the render target rather than an open drawing session.
    /// Overlays are drawn in list order, so later entries stack on top of earlier ones.
    /// Disabled overlays and ones whose source range does not contain
    /// <paramref name="sourceTime"/> are skipped. This method is on the hot path for both
    /// preview and export, so it does no drawing and no allocation when
    /// <paramref name="overlays"/> is empty or none are currently active — it only releases
    /// cache entries belonging to overlays that have since been deleted.
    /// </summary>
    public void Render(
        CanvasRenderTarget target,
        IReadOnlyList<TextOverlaySegment> overlays,
        TimeSpan sourceTime,
        int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overlays);

        if (width <= 0 || height <= 0)
            return;

        // Evict before the early-outs below, not after: an overlay deleted while the
        // playhead sits outside every overlay's range would otherwise never be seen by the
        // eviction pass again, keeping its cached CanvasTextFormat alive for the rest of the
        // session. EvictStaleCacheEntries returns immediately on an empty cache and
        // allocates nothing unless an entry actually needs removing, so the common
        // no-overlay path stays allocation-free.
        EvictStaleCacheEntries(overlays);

        if (overlays.Count == 0)
            return;

        bool anyActive = false;
        foreach (var overlay in overlays)
        {
            if (IsActive(overlay, sourceTime)) { anyActive = true; break; }
        }
        if (!anyActive)
            return;

        foreach (var overlay in overlays)
        {
            if (IsActive(overlay, sourceTime))
                RenderOverlay(target, overlay, sourceTime, width, height);
        }
    }

    /// <summary>
    /// Removes cache entries for overlay ids no longer present in <paramref name="overlays"/>
    /// (e.g. an overlay was deleted from the timeline) so a long editing session doesn't grow
    /// the cache unboundedly. Allocates nothing unless an entry actually needs removing.
    /// </summary>
    private void EvictStaleCacheEntries(IReadOnlyList<TextOverlaySegment> overlays)
    {
        if (_overlayCache.Count == 0)
            return;

        List<string>? staleKeys = null;
        foreach (var id in _overlayCache.Keys)
        {
            bool stillPresent = false;
            for (int i = 0; i < overlays.Count; i++)
            {
                if (overlays[i].Id == id) { stillPresent = true; break; }
            }
            if (!stillPresent)
                (staleKeys ??= new List<string>()).Add(id);
        }
        if (staleKeys is null)
            return;

        foreach (var id in staleKeys)
        {
            _overlayCache[id].Dispose();
            _overlayCache.Remove(id);
        }
    }

    /// <summary>
    /// Whether <paramref name="overlay"/> should be shown at <paramref name="sourceTime"/>:
    /// enabled, has text, and the instant falls inside its source range. Ownership by
    /// recording (<see cref="TextOverlaySegment.SourceVideoFilePath"/>) is the caller's
    /// concern (see <see cref="TimelineModel.GetActiveTextOverlays"/>) — this is a
    /// defensive re-check so <see cref="Render"/> is safe to call with an unfiltered list.
    /// </summary>
    private static bool IsActive(TextOverlaySegment overlay, TimeSpan sourceTime) =>
        overlay.Enabled
        && !string.IsNullOrEmpty(overlay.Text)
        && sourceTime >= overlay.Start && sourceTime < overlay.End;

    // ─────────────────────────── Geometry ───────────────────────────

    private void RenderOverlay(
        CanvasRenderTarget target, TextOverlaySegment overlay, TimeSpan sourceTime, int width, int height)
    {
        double progress = TimelineModel.GetTextOverlayProgress(overlay, sourceTime);
        double durationSeconds = overlay.Duration.TotalSeconds;

        // The whole-overlay animation envelope: the background (drawn manually below)
        // is transformed/faded in lockstep with this, while the text itself gets the
        // same envelope applied internally by AnimatedTextEngine.DrawAnimatedText.
        var (scale, tx, ty, envelopeOpacity) = AnimatedTextEngine.ComputeEnvelope(
            overlay.Animation, progress, durationSeconds, width, height);
        if (envelopeOpacity <= 0.001)
            return;

        float fontScale = (float)(height / ReferenceHeight);
        float scaledFontSize = (float)Math.Max(1.0, overlay.FontSize * fontScale);
        double scaledCornerRadius = Math.Max(0.0, overlay.CornerRadius * fontScale);
        double scaledOutlineWidth = Math.Max(0.0, overlay.OutlineWidth * fontScale);
        double scaledAccentThickness = Math.Max(1.0, overlay.AccentThickness * fontScale);
        double scaledBlurAmount = Math.Max(0.01, overlay.BlurAmount * fontScale);

        var hAlign = ToCanvasAlignment(overlay.TextAlignment);

        // The box is an explicit rectangle (see TextOverlaySegment.ComputeBox) rather than
        // something sized to the measured text: the user sets it directly with the preview's
        // resize handles, so wrapping and overflow are predictable — and because the geometry
        // is pure arithmetic, the editor's interactive region is guaranteed to line up with
        // what is drawn here instead of drifting apart through a second text measurement.
        var box = overlay.ComputeBox(width, height);
        if (box.Width <= 0 || box.Height <= 0)
            return;

        double padding = overlay.PaddingScale * scaledFontSize;
        // Never let padding eat the whole box when it is small relative to the font.
        padding = Math.Min(padding, Math.Min(box.Width, box.Height) / 3.0);

        var textRect = new Rect(
            box.X + padding, box.Y + padding,
            Math.Max(1.0, box.Width - padding * 2),
            Math.Max(1.0, box.Height - padding * 2));

        var cache = GetOrCreateOverlayCache(overlay, hAlign, scaledFontSize, textRect.Width, width, height);

        var boxCenter = new Vector2((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));

        DrawOverlay(
            target, overlay, box, textRect, boxCenter, cache.DrawFormat,
            scale, tx, ty, envelopeOpacity, progress, durationSeconds, scaledFontSize,
            scaledCornerRadius, scaledOutlineWidth, scaledAccentThickness, scaledBlurAmount,
            width, height);
    }

    /// <summary>
    /// Returns the cached measurement/draw resources for <paramref name="overlay"/>,
    /// rebuilding them (allocate-then-swap, disposing the stale entry) only when the
    /// computed <see cref="OverlayCacheKey"/> no longer matches what's cached — i.e. only
    /// when something that actually affects measurement or drawing changed since the last
    /// frame this overlay was rendered. This is what removes the per-frame
    /// <see cref="CanvasTextFormat"/>/<see cref="CanvasTextLayout"/> allocations from the
    /// hot path described in the "per-frame Win2D allocations" review finding.
    /// </summary>
    private OverlayCache GetOrCreateOverlayCache(
        TextOverlaySegment overlay, CanvasHorizontalAlignment hAlign, float scaledFontSize,
        double maxWidth, int width, int height)
    {
        var key = new OverlayCacheKey(
            overlay.FontFamily, scaledFontSize, overlay.IsBold, overlay.IsItalic,
            overlay.TextAlignment, overlay.Text, maxWidth, width, height, _device);

        if (_overlayCache.TryGetValue(overlay.Id, out var existing) && existing.Key.Equals(key))
            return existing;

        // Measure with a Top-aligned format so LayoutBounds reflects the text's true ink
        // extents (matches TextSlideRenderer.MeasureTextHeight's approach) — the actual
        // draw pass uses a separately-built Center-aligned format that is what gets cached
        // (the measure format itself is only ever needed transiently, right here).
        using var measureFormat = AnimatedTextEngine.CreateFormat(
            overlay.FontFamily, scaledFontSize, overlay.IsBold, overlay.IsItalic,
            hAlign, CanvasVerticalAlignment.Top, wrap: true);
        using var layout = new CanvasTextLayout(_device, overlay.Text, measureFormat, (float)maxWidth, (float)height);

        double textW = Math.Max(1.0, layout.LayoutBounds.Width);
        double textH = Math.Max(1.0, layout.LayoutBounds.Height);

        var drawFormat = AnimatedTextEngine.CreateFormat(
            overlay.FontFamily, scaledFontSize, overlay.IsBold, overlay.IsItalic,
            hAlign, CanvasVerticalAlignment.Center, wrap: true);

        var next = new OverlayCache { Key = key, DrawFormat = drawFormat, TextW = textW, TextH = textH };
        existing?.Dispose();
        _overlayCache[overlay.Id] = next;
        return next;
    }

    private static CanvasHorizontalAlignment ToCanvasAlignment(SlideTextAlignment a) => a switch
    {
        SlideTextAlignment.Left => CanvasHorizontalAlignment.Left,
        SlideTextAlignment.Right => CanvasHorizontalAlignment.Right,
        _ => CanvasHorizontalAlignment.Center,
    };

    // ─────────────────────────── Drawing ───────────────────────────

    private void DrawOverlay(
        CanvasRenderTarget target, TextOverlaySegment overlay, Rect box, Rect textRect, Vector2 boxCenter,
        CanvasTextFormat drawFormat, float scale, float tx, float ty, double envelopeOpacity,
        double progress, double durationSeconds, float scaledFontSize, double scaledCornerRadius,
        double scaledOutlineWidth, double scaledAccentThickness, double scaledBlurAmount,
        int width, int height)
    {
        // Blur must sample `target`'s current pixels *before* a drawing session is opened
        // on it — Win2D forbids reading from and drawing into the same render target in
        // one pass. Copy into the cached scratch target and (re)point the cached blur
        // effect graph at it first; every other background mode only ever draws into
        // `target`. Building the graph in a separate step (rather than inline as a
        // `using` local's object initializer) means a throw from CopyFrameIntoBlurScratch
        // (real GPU work) can never leave a partially-built effect ungoverned by Dispose().
        GaussianBlurEffect? blurEffect = null;
        if (overlay.Background == TextOverlayBackground.Blur)
        {
            var scratch = CopyFrameIntoBlurScratch(target, width, height);
            blurEffect = EnsureBlurEffectGraph(scratch, (float)scaledBlurAmount);
        }

        using var ds = target.CreateDrawingSession();

        if (overlay.Background == TextOverlayBackground.OutlineShadow)
        {
            // No box — the "background" is a shadow + outline drawn directly around the
            // glyphs, each pass already animated in lockstep with the fill via DrawAnimatedText.
            DrawOutlineShadow(
                ds, overlay, textRect, drawFormat, progress, durationSeconds,
                scaledOutlineWidth, scaledFontSize, width, height);
        }
        else if (overlay.Background != TextOverlayBackground.None)
        {
            var saved = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(scale, boxCenter) * Matrix3x2.CreateTranslation(tx, ty);
            try
            {
                switch (overlay.Background)
                {
                    case TextOverlayBackground.Solid:
                        DrawSolidBackground(ds, overlay, box, scaledCornerRadius, envelopeOpacity);
                        break;
                    case TextOverlayBackground.Blur:
                        // Blur needs the raw envelope components (not just the already-applied
                        // ds.Transform) so it can transform the clip geometry alone and draw the
                        // blurred frame with an identity transform — see the method doc comment.
                        DrawBlurBackground(ds, blurEffect!, overlay, box, scaledCornerRadius, envelopeOpacity, scale, boxCenter, tx, ty);
                        break;
                    case TextOverlayBackground.GradientScrim:
                        DrawGradientScrim(ds, overlay, box, envelopeOpacity, width, height);
                        break;
                    case TextOverlayBackground.AccentBar:
                        DrawAccentBarBackground(ds, overlay, box, scaledCornerRadius, scaledAccentThickness, envelopeOpacity);
                        break;
                }
            }
            finally
            {
                ds.Transform = saved;
            }
        }

        var textColor = AnimatedTextEngine.ParseColor(overlay.TextColor);
        _textEngine.DrawAnimatedText(
            ds, overlay.Text, drawFormat, textRect, textColor, overlay.Animation, progress,
            width, height, scaledFontSize, durationSeconds);
    }

    /// <summary>
    /// Copies the current contents of <paramref name="target"/> into the cached blur
    /// scratch target (resizing it via allocate-then-swap if needed) and returns it, ready
    /// to be wrapped in the blur effect graph. Must be called while no drawing session is
    /// open on <paramref name="target"/>.
    /// </summary>
    private CanvasRenderTarget CopyFrameIntoBlurScratch(CanvasRenderTarget target, int width, int height)
    {
        var scratch = EnsureBlurScratch(width, height);
        using (var scratchDs = scratch.CreateDrawingSession())
        {
            scratchDs.Clear(Color.FromArgb(0, 0, 0, 0));
            scratchDs.DrawImage(target);
        }
        return scratch;
    }

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
    /// Returns the cached Blur effect graph (<see cref="BorderEffect"/> feeding a
    /// <see cref="GaussianBlurEffect"/>), rebuilding it only when <paramref name="scratch"/>
    /// is a different instance than what the graph currently reads from — which only
    /// happens when <see cref="EnsureBlurScratch"/> reallocated it for a resize. The normal
    /// per-frame case reuses the same graph and just updates <see cref="GaussianBlurEffect.BlurAmount"/>,
    /// a cheap property set, instead of rebuilding two effect objects every frame.
    /// </summary>
    private GaussianBlurEffect EnsureBlurEffectGraph(CanvasRenderTarget scratch, float blurAmount)
    {
        if (_blurBorderEffect is null || _blurGaussianEffect is null || !ReferenceEquals(_blurBorderEffect.Source, scratch))
        {
            // Build both candidates before touching the fields, and dispose whichever one
            // was already constructed if the other throws — otherwise a failure part-way
            // through leaks the first effect, which is never reachable to dispose again.
            BorderEffect? nextBorder = null;
            GaussianBlurEffect? nextGaussian = null;
            try
            {
                nextBorder = new BorderEffect
                {
                    Source = scratch,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp,
                };
                nextGaussian = new GaussianBlurEffect { Source = nextBorder, BlurAmount = blurAmount };
            }
            catch
            {
                nextGaussian?.Dispose();
                nextBorder?.Dispose();
                throw;
            }

            _blurGaussianEffect?.Dispose();
            _blurBorderEffect?.Dispose();
            _blurBorderEffect = nextBorder;
            _blurGaussianEffect = nextGaussian;
        }
        else
        {
            _blurGaussianEffect.BlurAmount = blurAmount;
        }

        return _blurGaussianEffect;
    }

    // ─────────────────────────── Backgrounds ───────────────────────────

    private static void DrawSolidBackground(
        CanvasDrawingSession ds, TextOverlaySegment overlay, Rect box, double cornerRadius, double envelopeOpacity)
    {
        var color = AnimatedTextEngine.ParseColor(overlay.BackgroundColor);
        byte alpha = ToByteAlpha(overlay.BackgroundOpacity * envelopeOpacity);
        ds.FillRoundedRectangle(box, (float)cornerRadius, (float)cornerRadius, Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// Frosted-glass background: draws the pre-blurred sample of the frame behind the
    /// text, clipped to the rounded-rect box via a layer (so nothing bleeds outside it),
    /// then a flat legibility tint on top.
    /// </summary>
    /// <remarks>
    /// The caller has already set <c>ds.Transform</c> to the whole-overlay animation
    /// envelope (scale/translate around the box's own centre) so that every other
    /// background type slides/scales in exactly like the box does. That is correct for
    /// the clip shape and the tint — both are conceptually part of the box — but it must
    /// <em>not</em> apply to <paramref name="blurredFrame"/> itself: that image is a
    /// snapshot of the whole already-composited frame, and dragging/scaling it along with
    /// the envelope would sample pixels from where the box *used to be* instead of what is
    /// actually behind it right now (visible as a smeared, mis-registered "frosted glass"
    /// during a slide/scale-in animation). The fix is to bake the envelope into the clip
    /// geometry explicitly via <see cref="CanvasGeometry.Transform"/>, then switch to an
    /// identity transform just for the <see cref="CanvasDrawingSession.DrawImage(ICanvasImage)"/>
    /// call, restoring the envelope transform immediately after for the tint fill.
    /// </remarks>
    private static void DrawBlurBackground(
        CanvasDrawingSession ds, ICanvasImage blurredFrame,
        TextOverlaySegment overlay, Rect box, double cornerRadius, double envelopeOpacity,
        float scale, Vector2 boxCenter, float tx, float ty)
    {
        var envelopeTransform = Matrix3x2.CreateScale(scale, boxCenter) * Matrix3x2.CreateTranslation(tx, ty);

        using var rectGeometry = CanvasGeometry.CreateRoundedRectangle(ds, box, (float)cornerRadius, (float)cornerRadius);
        using var clip = rectGeometry.Transform(envelopeTransform);

        var envelopeDs = ds.Transform;
        ds.Transform = Matrix3x2.Identity;
        try
        {
            using (ds.CreateLayer((float)envelopeOpacity, clip))
            {
                ds.DrawImage(blurredFrame);
            }
        }
        finally
        {
            ds.Transform = envelopeDs;
        }

        var tint = AnimatedTextEngine.ParseColor(overlay.BackgroundColor);
        byte tintAlpha = ToByteAlpha(overlay.BlurTintOpacity * envelopeOpacity);
        ds.FillRoundedRectangle(box, (float)cornerRadius, (float)cornerRadius, Color.FromArgb(tintAlpha, tint.R, tint.G, tint.B));
    }

    /// <summary>
    /// A directional scrim: a linear gradient from an opaque edge at the frame boundary
    /// to fully transparent at the near edge of the text box, extended past the box
    /// itself so the fade — not a hard rectangle edge — is what reads as the background.
    /// </summary>
    private static void DrawGradientScrim(
        CanvasDrawingSession ds, TextOverlaySegment overlay, Rect box, double envelopeOpacity, int width, int height)
    {
        var baseColor = AnimatedTextEngine.ParseColor(overlay.BackgroundColor);
        byte alpha = ToByteAlpha(overlay.ScrimStrength * envelopeOpacity);
        var opaque = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        var transparent = Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B);

        Rect band;
        Vector2 start, end;
        switch (overlay.ScrimDirection)
        {
            case ScrimDirection.Top:
                band = new Rect(box.X, 0, box.Width, box.Bottom);
                start = new Vector2((float)box.X, 0f);
                end = new Vector2((float)box.X, (float)box.Bottom);
                break;
            case ScrimDirection.Left:
                band = new Rect(0, box.Y, box.Right, box.Height);
                start = new Vector2(0f, (float)box.Y);
                end = new Vector2((float)box.Right, (float)box.Y);
                break;
            case ScrimDirection.Right:
                band = new Rect(box.X, box.Y, width - box.X, box.Height);
                start = new Vector2((float)width, (float)box.Y);
                end = new Vector2((float)box.X, (float)box.Y);
                break;
            default: // Bottom
                band = new Rect(box.X, box.Y, box.Width, height - box.Y);
                start = new Vector2((float)box.X, (float)height);
                end = new Vector2((float)box.X, (float)box.Y);
                break;
        }

        using var brush = new CanvasLinearGradientBrush(ds, opaque, transparent) { StartPoint = start, EndPoint = end };
        ds.FillRectangle(band, brush);
    }

    /// <summary>A thin solid stripe along one edge of the box, on top of a normal solid backing.</summary>
    private static void DrawAccentBarBackground(
        CanvasDrawingSession ds, TextOverlaySegment overlay, Rect box, double cornerRadius,
        double thickness, double envelopeOpacity)
    {
        DrawSolidBackground(ds, overlay, box, cornerRadius, envelopeOpacity);

        var accent = AnimatedTextEngine.ParseColor(overlay.AccentColor);
        byte alpha = ToByteAlpha(envelopeOpacity);
        var color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);

        var stripe = overlay.AccentSide switch
        {
            AccentSide.Right => new Rect(box.Right - thickness, box.Y, thickness, box.Height),
            AccentSide.Top => new Rect(box.X, box.Y, box.Width, thickness),
            AccentSide.Bottom => new Rect(box.X, box.Bottom - thickness, box.Width, thickness),
            _ => new Rect(box.X, box.Y, thickness, box.Height), // Left
        };
        ds.FillRectangle(stripe, color);
    }

    /// <summary>
    /// No box: a soft drop shadow (a single downward-offset dark copy) plus a stroked
    /// outline (a ring of offset copies in <see cref="TextOverlaySegment.OutlineColor"/>)
    /// drawn behind where the fill pass will land. Every pass goes through
    /// <see cref="AnimatedTextEngine.DrawAnimatedText"/> so it animates identically to the
    /// fill (same envelope, computed independently from the same progress/duration).
    /// </summary>
    private void DrawOutlineShadow(
        CanvasDrawingSession ds, TextOverlaySegment overlay, Rect textRect, CanvasTextFormat format,
        double progress, double durationSeconds, double outlineWidth, float fontSize, int width, int height)
    {
        if (overlay.ShadowStrength > 0.001)
        {
            double shadowOffset = Math.Max(1.0, fontSize * 0.06);
            var shadowRect = new Rect(textRect.X, textRect.Y + shadowOffset, textRect.Width, textRect.Height);
            var shadowColor = Color.FromArgb(ToByteAlpha(overlay.ShadowStrength), 0, 0, 0);
            _textEngine.DrawAnimatedText(
                ds, overlay.Text, format, shadowRect, shadowColor, overlay.Animation, progress,
                width, height, fontSize, durationSeconds);
        }

        if (outlineWidth > 0.01)
        {
            var outlineColor = AnimatedTextEngine.ParseColor(overlay.OutlineColor);
            for (int i = 0; i < OutlinePassCount; i++)
            {
                double angle = i * (2.0 * Math.PI / OutlinePassCount);
                double dx = Math.Cos(angle) * outlineWidth;
                double dy = Math.Sin(angle) * outlineWidth;
                var rect = new Rect(textRect.X + dx, textRect.Y + dy, textRect.Width, textRect.Height);
                _textEngine.DrawAnimatedText(
                    ds, overlay.Text, format, rect, outlineColor, overlay.Animation, progress,
                    width, height, fontSize, durationSeconds);
            }
        }
    }

    private static byte ToByteAlpha(double normalized) => (byte)Math.Clamp(normalized * 255.0, 0, 255);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blurScratch?.Dispose();
        _blurGaussianEffect?.Dispose();
        _blurBorderEffect?.Dispose();
        foreach (var entry in _overlayCache.Values)
            entry.Dispose();
        _overlayCache.Clear();
        _textEngine.Dispose();
        GC.SuppressFinalize(this);
    }
}
