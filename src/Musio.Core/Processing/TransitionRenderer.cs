using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Musio.Core.Timeline;
using Windows.Foundation;
using Windows.UI;

namespace Musio.Core.Processing;

/// <summary>
/// Renders transition effects between two frames (outgoing → incoming)
/// using Win2D compositing operations.
/// </summary>
public class TransitionRenderer : IDisposable
{
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    private readonly CanvasDevice _device;
    private bool _disposed;

    public TransitionRenderer(CanvasDevice? device = null)
    {
        _device = device ?? CanvasDevice.GetSharedDevice();
    }

    /// <summary>
    /// Blends an outgoing frame and incoming frame based on the transition type and progress.
    /// </summary>
    /// <param name="outgoing">The frame being transitioned away from (can be null for fade-from-black).</param>
    /// <param name="incoming">The frame being transitioned to.</param>
    /// <param name="type">The transition effect type.</param>
    /// <param name="progress">
    /// Normalized progress of the transition (0 = fully outgoing, 1 = fully incoming). This is
    /// expected to already be <see cref="TransitionResolver.EasedProgress"/> — i.e. the caller
    /// has already run the raw linear progress through the segment's configured
    /// <see cref="TransitionEasing"/> curve (<c>CubicBezierEasing</c>). Every helper below
    /// therefore treats <paramref name="progress"/> as final motion/blend progress and must NOT
    /// apply any further easing/smoothing curve to it — doing so would double-ease the value and
    /// distort the user's configured curve. (Effects may still apply a curve to their own
    /// *intensity* envelope — e.g. a blur radius that peaks mid-transition — since that shapes
    /// the effect's identity, not overall progress; see <see cref="PeakEnvelope"/>.)
    /// </param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    public CanvasRenderTarget Render(
        CanvasBitmap? outgoing,
        CanvasBitmap incoming,
        TransitionType type,
        double progress,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        progress = Math.Clamp(progress, 0, 1);

        // `target` is only handed to the caller (who owns disposal from then on) once the whole
        // body below completes successfully. If drawing-session creation or any helper/effect
        // throws, `target` must be disposed here — otherwise a failed render leaks a native GPU
        // resource, and the preview path retries a failing render every tick.
        var target = new CanvasRenderTarget(_device, width, height, 96);
        try
        {
            RenderInto(target, outgoing, incoming, type, progress, width, height);
        }
        catch
        {
            target.Dispose();
            throw;
        }

        return target;
    }

    private static void RenderInto(
        CanvasRenderTarget target,
        CanvasBitmap? outgoing,
        CanvasBitmap incoming,
        TransitionType type,
        double progress,
        int width,
        int height)
    {
        using var ds = target.CreateDrawingSession();

        switch (type)
        {
            case TransitionType.Fade:
                RenderFade(ds, outgoing, incoming, progress, width, height, Black);
                break;
            case TransitionType.CrossFade:
                RenderCrossFade(ds, outgoing, incoming, progress, width, height);
                break;
            case TransitionType.SlideLeft:
                RenderSlide(ds, outgoing, incoming, progress, width, height, -1, 0);
                break;
            case TransitionType.SlideRight:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 1, 0);
                break;
            case TransitionType.SlideUp:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 0, -1);
                break;
            case TransitionType.SlideDown:
                RenderSlide(ds, outgoing, incoming, progress, width, height, 0, 1);
                break;
            case TransitionType.Wipe:
                // Legacy left-to-right wipe: reveal originates at the left edge, growing right.
                RenderWipe(ds, outgoing, incoming, progress, width, height, 1, 0);
                break;
            case TransitionType.WipeRight:
                // Right-to-left wipe: reveal originates at the right edge, growing left.
                RenderWipe(ds, outgoing, incoming, progress, width, height, -1, 0);
                break;
            case TransitionType.WipeUp:
                // Bottom-to-top wipe: reveal originates at the bottom edge, growing upward.
                RenderWipe(ds, outgoing, incoming, progress, width, height, 0, -1);
                break;
            case TransitionType.WipeDown:
                // Top-to-bottom wipe: reveal originates at the top edge, growing downward.
                RenderWipe(ds, outgoing, incoming, progress, width, height, 0, 1);
                break;
            case TransitionType.DipToWhite:
                RenderFade(ds, outgoing, incoming, progress, width, height, White);
                break;
            case TransitionType.ZoomBlur:
                RenderZoomBlur(ds, outgoing, incoming, progress, width, height);
                break;
            case TransitionType.WhipPanLeft:
                RenderWhipPan(ds, outgoing, incoming, progress, width, height, -1);
                break;
            case TransitionType.WhipPanRight:
                RenderWhipPan(ds, outgoing, incoming, progress, width, height, 1);
                break;
            case TransitionType.PushLeft:
                RenderPush(ds, outgoing, incoming, progress, width, height, -1, 0);
                break;
            case TransitionType.PushRight:
                RenderPush(ds, outgoing, incoming, progress, width, height, 1, 0);
                break;
            case TransitionType.PushUp:
                RenderPush(ds, outgoing, incoming, progress, width, height, 0, -1);
                break;
            case TransitionType.PushDown:
                RenderPush(ds, outgoing, incoming, progress, width, height, 0, 1);
                break;
            case TransitionType.Glitch:
                RenderGlitch(ds, outgoing, incoming, progress, width, height);
                break;
            default:
                // None (and any future unhandled member) — no transition, hard cut to incoming.
                // Scaled into the output rect like every other helper: incoming may render at a
                // different resolution than the requested output (a text slide renders at
                // project resolution, a video frame at its own source resolution), and drawing
                // it at native size would crop a larger bitmap or leave the frame partially
                // uncovered (transparent) for a smaller one. `None` is user-selectable and is
                // also the default for any unconfigured boundary, so this is genuinely reachable
                // with mismatched sizes — not just a defensive fallback.
                ds.DrawImage(incoming, new Rect(0, 0, width, height),
                    new Rect(0, 0, incoming.SizeInPixels.Width, incoming.SizeInPixels.Height));
                break;
        }
    }

    /// <summary>
    /// Fade through a solid colour: outgoing fades out over the first half of the transition,
    /// then incoming fades in over the second half. <see cref="TransitionType.Fade"/> dips
    /// through black; <see cref="TransitionType.DipToWhite"/> is identical except it dips
    /// through white — both share this implementation, parameterised on <paramref name="through"/>.
    /// </summary>
    private static void RenderFade(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, Color through)
    {
        ds.Clear(through);

        if (progress < 0.5)
        {
            // First half: outgoing fades out
            float opacity = (float)(1.0 - progress * 2);
            if (outgoing is not null)
            {
                ds.DrawImage(outgoing, new Rect(0, 0, width, height),
                    new Rect(0, 0, outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height),
                    opacity);
            }
            else
            {
                // No outgoing frame — the fade-from-black contract on Render's `outgoing`
                // parameter applies regardless of which colour this fade dips *through*: without
                // this, DipToWhite (through = white) with a null outgoing would start at solid
                // white instead of black, since skipping the draw left the cleared `through`
                // colour fully exposed at progress 0. Filling black at the same opacity the real
                // outgoing frame would have used reproduces the same fade-from-black start for
                // every colour this method is parameterised with.
                ds.FillRectangle(new Rect(0, 0, width, height),
                    Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0));
            }
        }
        else
        {
            // Second half: incoming fades in
            float opacity = (float)((progress - 0.5) * 2);
            ds.DrawImage(incoming, new Rect(0, 0, width, height),
                new Rect(0, 0, incoming.SizeInPixels.Width, incoming.SizeInPixels.Height),
                opacity);
        }
    }

    /// <summary>Direct crossfade: outgoing and incoming blend simultaneously.</summary>
    private static void RenderCrossFade(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        var destRect = new Rect(0, 0, width, height);
        var inSrcRect = new Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);

        if (outgoing is null)
        {
            // Fade-from-black contract (see Render's `outgoing` doc): with nothing to dissolve
            // from, black IS the outgoing side, so clearing to it and fading the incoming in over
            // the top is the intended result rather than a dip.
            ds.Clear(Black);
            ds.DrawImage(incoming, destRect, inSrcRect, (float)progress);
            return;
        }

        // Draw the outgoing frame FULLY OPAQUE and dissolve the incoming over it, rather than
        // layering both at partial opacity. Source-over of two partial layers does not sum to an
        // even blend: over an opaque black clear, progress 0.5 composites to
        // 0.25*outgoing + 0.5*incoming + 0.25*black — the picture visibly dips dark through the
        // middle of every dissolve. Drawing outgoing opaque makes the single "over" blend below
        // resolve to exactly (1-progress)*outgoing + progress*incoming, and the destination is
        // already fully opaque so the result stays alpha 1 without needing a clear at all.
        // RenderGlitch composites its base layer through this method, so it inherits the fix.
        var outSrcRect = new Rect(0, 0,
            outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
        ds.DrawImage(outgoing, destRect, outSrcRect, 1f);
        ds.DrawImage(incoming, destRect, inSrcRect, (float)progress);
    }

    /// <summary>
    /// Slide transition: incoming slides in from a direction while outgoing slides away in the
    /// same direction, at the same rate — the two stay edge-to-edge (see <see cref="RenderPush"/>
    /// for the identical edge-to-edge arithmetic, kept as a separate named helper so the two
    /// effects can diverge independently later, e.g. if Slide grows an overshoot/spring while
    /// Push stays perfectly rigid).
    /// </summary>
    private static void RenderSlide(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, int dirX, int dirY)
    {
        // `progress` arrives ALREADY EASED from TransitionResolver.EasedProgress (see the doc
        // comment on Render), so it is the final motion progress and is used as-is. This helper
        // previously ran it through an internal SmoothStep as well, which applied a second curve
        // on top of the user's configured easing and distorted the motion.
        //
        // (An earlier revision of this comment justified the removal by claiming Slide/Wipe were
        // unreachable because every call site passed CrossFade. That was true only before the
        // export and preview paths were wired to pass resolution.Type — they now pass whatever
        // the user configured, so these effects are very much reachable. Avoiding the
        // double-easing is the reason on its own; no reachability argument is needed or valid.)
        double t = progress;

        double offsetX = dirX * width * (1.0 - t);
        double offsetY = dirY * height * (1.0 - t);

        // Draw outgoing shifted away
        if (outgoing is not null)
        {
            double outOffsetX = -dirX * width * t;
            double outOffsetY = -dirY * height * t;
            var outDest = new Rect(outOffsetX, outOffsetY, width, height);
            var outSrc = new Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
            ds.DrawImage(outgoing, outDest, outSrc);
        }
        else
        {
            // Fade-from-black contract: at any t < 1 the incoming frame doesn't yet cover the
            // whole output (it slides in from off-screen), so without this the uncovered region
            // left the render target's native transparent background instead of solid black.
            ds.Clear(Black);
        }

        // Draw incoming sliding in
        var inDest = new Rect(offsetX, offsetY, width, height);
        var inSrc = new Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        ds.DrawImage(incoming, inDest, inSrc);
    }

    /// <summary>
    /// Push transition: outgoing and incoming translate together as a single rigid pair, so the
    /// incoming frame visually shoves the outgoing one off-screen. Outgoing sits at offset
    /// <c>-dir * size * t</c> and incoming at offset <c>dir * size * (1 - t)</c> using the exact
    /// same <c>t</c> for both — at every instant the outgoing frame's trailing edge and the
    /// incoming frame's leading edge land on the same coordinate (<c>size * (1 - t)</c> along the
    /// push axis), so there is never a gap or an overlap between them.
    /// </summary>
    private static void RenderPush(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, int dirX, int dirY)
    {
        (double outOffsetX, double inOffsetX) = PushPairOffsets(progress, dirX, width);
        (double outOffsetY, double inOffsetY) = PushPairOffsets(progress, dirY, height);

        if (outgoing is not null)
        {
            var outDest = new Rect(outOffsetX, outOffsetY, width, height);
            var outSrc = new Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
            ds.DrawImage(outgoing, outDest, outSrc);
        }
        else
        {
            ds.Clear(Black);
        }

        var inDest = new Rect(inOffsetX, inOffsetY, width, height);
        var inSrc = new Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        ds.DrawImage(incoming, inDest, inSrc);
    }

    /// <summary>
    /// Pure arithmetic for <see cref="RenderPush"/>'s rigid-pair translation along one axis:
    /// outgoing offset is <c>-dir * size * t</c>, incoming offset is <c>dir * size * (1 - t)</c>.
    /// Extracted so the edge-to-edge contact claim (outgoing's trailing edge always meets
    /// incoming's leading edge, for every t and every direction) can be verified with a plain
    /// unit test, with no GPU/Win2D device involved.
    /// </summary>
    internal static (double OutgoingOffset, double IncomingOffset) PushPairOffsets(
        double t, int dir, double size)
    {
        t = Math.Clamp(t, 0, 1);
        double outgoingOffset = -dir * size * t;
        double incomingOffset = dir * size * (1.0 - t);
        return (outgoingOffset, incomingOffset);
    }

    /// <summary>Directional wipe: incoming is revealed through a rectangle that grows from one
    /// edge of the frame. See <see cref="WipeRevealRect"/> for the direction convention.</summary>
    private static void RenderWipe(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, int dirX, int dirY)
    {
        double t = progress; // already eased by the caller — see the no-double-easing note on Render

        // Draw outgoing (full)
        if (outgoing is not null)
        {
            ds.DrawImage(outgoing, new Rect(0, 0, width, height),
                new Rect(0, 0,
                    outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height));
        }
        else
        {
            ds.Clear(Black);
        }

        // Draw incoming clipped to the revealed area
        Rect clip = WipeRevealRect(t, width, height, dirX, dirY);
        if (clip.Width > 0 && clip.Height > 0)
        {
            using var layer = ds.CreateLayer(1.0f, clip);
            ds.DrawImage(incoming, new Rect(0, 0, width, height),
                new Rect(0, 0,
                    incoming.SizeInPixels.Width, incoming.SizeInPixels.Height));
        }
    }

    /// <summary>
    /// Computes the axis-aligned reveal rectangle for a directional wipe at progress
    /// <paramref name="t"/>. <paramref name="dirX"/>/<paramref name="dirY"/> indicate which edge
    /// the reveal originates from and grows away from:
    /// <list type="bullet">
    /// <item>(1, 0) — legacy <see cref="TransitionType.Wipe"/>: originates at the left edge, growing right.</item>
    /// <item>(-1, 0) — <see cref="TransitionType.WipeRight"/>: originates at the right edge, growing left.</item>
    /// <item>(0, -1) — <see cref="TransitionType.WipeUp"/>: originates at the bottom edge, growing upward.</item>
    /// <item>(0, 1) — <see cref="TransitionType.WipeDown"/>: originates at the top edge, growing downward.</item>
    /// </list>
    /// Extracted as a pure function so the geometry itself can be unit-tested without a GPU device.
    /// </summary>
    internal static Rect WipeRevealRect(double t, int width, int height, int dirX, int dirY)
    {
        t = Math.Clamp(t, 0, 1);
        double revealW = width * t;
        double revealH = height * t;

        if (dirX > 0) return new Rect(0, 0, revealW, height);
        if (dirX < 0) return new Rect(width - revealW, 0, revealW, height);
        if (dirY < 0) return new Rect(0, height - revealH, width, revealH);
        return new Rect(0, 0, width, revealH); // dirY > 0 (and the dirX==dirY==0 fallback)
    }

    /// <summary>
    /// Whip-zoom / dolly-punch transition: the outgoing frame scales up and blurs away while the
    /// incoming frame scales down from an over-zoomed start into its resting size and sharpens.
    /// The blur radius is not monotonic with progress — it ramps up and back down, peaking at the
    /// transition's midpoint (<see cref="PeakEnvelope"/>), so both frames are sharp at the very
    /// start/end and only the middle of the whip is blurred.
    /// </summary>
    private static void RenderZoomBlur(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        const float MaxBlur = 24f;
        const float MaxZoom = 0.35f;

        double t = progress;
        double envelope = PeakEnvelope(t);
        var destRect = new Rect(0, 0, width, height);

        ds.Clear(Black);

        if (outgoing is not null)
        {
            var center = new Vector2(
                outgoing.SizeInPixels.Width / 2f, outgoing.SizeInPixels.Height / 2f);
            float scale = 1f + MaxZoom * (float)t;
            // See CompensateBlurForScale: blurring in outgoing's own source-pixel space, then
            // scaling that into `destRect`, would otherwise make the blur radius depend on
            // outgoing's resolution relative to the requested output.
            float blurAmount = CompensateBlurForScale(
                MaxBlur * (float)envelope,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height,
                width, height);

            using var transform = new Transform2DEffect
            {
                Source = outgoing,
                TransformMatrix = Matrix3x2.CreateScale(scale, center),
            };
            using var blur = new GaussianBlurEffect
            {
                Source = transform,
                BlurAmount = blurAmount,
                BorderMode = EffectBorderMode.Soft,
            };

            var srcRect = new Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);
            ds.DrawImage(blur, destRect, srcRect, (float)(1.0 - t));
        }

        {
            var center = new Vector2(
                incoming.SizeInPixels.Width / 2f, incoming.SizeInPixels.Height / 2f);
            float scale = 1f + MaxZoom * (float)(1.0 - t);
            float blurAmount = CompensateBlurForScale(
                MaxBlur * (float)envelope,
                incoming.SizeInPixels.Width, incoming.SizeInPixels.Height,
                width, height);

            using var transform = new Transform2DEffect
            {
                Source = incoming,
                TransformMatrix = Matrix3x2.CreateScale(scale, center),
            };
            using var blur = new GaussianBlurEffect
            {
                Source = transform,
                BlurAmount = blurAmount,
                BorderMode = EffectBorderMode.Soft,
            };

            var srcRect = new Rect(0, 0,
                incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
            ds.DrawImage(blur, destRect, srcRect, (float)t);
        }
    }

    /// <summary>
    /// Horizontal whip-pan: a <see cref="RenderPush"/>-style rigid-pair push with directional
    /// motion blur layered on top. The blur strength peaks at the transition's midpoint
    /// (<see cref="PeakEnvelope"/>) and is zero at both ends, mimicking a fast camera whip-pan
    /// that is sharp just before and after the snap and blurred only mid-motion.
    /// </summary>
    private static void RenderWhipPan(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height, int dirX)
    {
        const float MaxBlur = 40f;

        double t = progress;
        double envelope = PeakEnvelope(t);
        float desiredOutputBlur = MaxBlur * (float)envelope;

        // Clear to opaque black unconditionally first (not only when outgoing is null): both
        // frames here are drawn via a DirectionalBlurEffect wrapping the source bitmap directly,
        // and a soft-bordered blur effect can produce partially-transparent edge pixels even at
        // full nominal opacity (most visible when a small bitmap is scaled up steeply, so a
        // single source-edge pixel maps to many output pixels). Compositing those draws onto an
        // already-opaque background — same reasoning as RenderCrossFade/RenderZoomBlur — keeps
        // the final output frame fully opaque regardless of any such effect-internal edge
        // softening, rather than only patching the outgoing-is-null case.
        ds.Clear(Black);

        if (outgoing is not null)
        {
            double outOffsetX = -dirX * width * t;
            var outDest = new Rect(outOffsetX, 0, width, height);
            var outSrc = new Rect(0, 0,
                outgoing.SizeInPixels.Width, outgoing.SizeInPixels.Height);

            // See CompensateDirectionalBlurForScale: the blur is applied in outgoing's own
            // source-pixel space (Source = outgoing directly, no intermediate), so it must be
            // compensated for outgoing's own horizontal scale factor into the output — otherwise
            // a video frame at source resolution and a text slide at project resolution end up
            // blurred by very different amounts for the same nominal blur strength.
            float outBlurAmount = CompensateDirectionalBlurForScale(
                desiredOutputBlur, outgoing.SizeInPixels.Width, width);

            using var blur = new DirectionalBlurEffect
            {
                Source = outgoing,
                Angle = 0f, // horizontal — matches the horizontal push direction
                BlurAmount = outBlurAmount,
            };
            ds.DrawImage(blur, outDest, outSrc);
        }

        double inOffsetX = dirX * width * (1.0 - t);
        var inDest = new Rect(inOffsetX, 0, width, height);
        var inSrc = new Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        float inBlurAmount = CompensateDirectionalBlurForScale(
            desiredOutputBlur, incoming.SizeInPixels.Width, width);

        using var inBlur = new DirectionalBlurEffect
        {
            Source = incoming,
            Angle = 0f,
            BlurAmount = inBlurAmount,
        };
        ds.DrawImage(inBlur, inDest, inSrc);
    }

    /// <summary>
    /// RGB channel-split glitch: a crossfade underneath, with red/blue channel copies of the
    /// incoming frame separated horizontally in opposite directions, plus a handful of horizontal
    /// slices displaced sideways. Both the channel separation and the slice displacement peak at
    /// the transition's midpoint (<see cref="PeakEnvelope"/>) and settle to zero at both ends.
    /// Slice displacement is driven by <see cref="GlitchSliceOffset"/>, a deterministic function
    /// of (slice index, progress) rather than <see cref="Random"/> — export renders every frame
    /// independently (and potentially out of order/on a different pass than the live preview), so
    /// a wall-clock- or instance-seeded RNG would make the glitch look different between preview
    /// and the exported file for the same instant, and could flicker inconsistently across export
    /// re-runs of the same project.
    /// </summary>
    private static void RenderGlitch(
        CanvasDrawingSession ds, CanvasBitmap? outgoing, CanvasBitmap incoming,
        double progress, int width, int height)
    {
        double t = progress;
        double envelope = PeakEnvelope(t);

        // Base crossfade underneath the glitch artifacts.
        RenderCrossFade(ds, outgoing, incoming, t, width, height);

        if (envelope <= 0.0) return;

        var srcRect = new Rect(0, 0,
            incoming.SizeInPixels.Width, incoming.SizeInPixels.Height);
        float channelOffset = (float)(width * 0.03 * envelope);

        using (var redOnly = new ColorMatrixEffect
        {
            Source = incoming,
            ColorMatrix = new Matrix5x4 { M11 = 1, M22 = 0, M33 = 0, M44 = 1 },
        })
        using (var blueOnly = new ColorMatrixEffect
        {
            Source = incoming,
            ColorMatrix = new Matrix5x4 { M11 = 0, M22 = 0, M33 = 1, M44 = 1 },
        })
        {
            ds.DrawImage(redOnly,
                new Rect(channelOffset, 0, width, height), srcRect,
                (float)envelope, CanvasImageInterpolation.Linear, CanvasComposite.Add);
            ds.DrawImage(blueOnly,
                new Rect(-channelOffset, 0, width, height), srcRect,
                (float)envelope, CanvasImageInterpolation.Linear, CanvasComposite.Add);
        }

        // Horizontal slice displacement.
        const int SliceCount = 7;
        double sliceHeight = height / (double)SliceCount;
        double srcSliceHeight = incoming.SizeInPixels.Height / (double)SliceCount;

        for (int i = 0; i < SliceCount; i++)
        {
            double offsetFrac = GlitchSliceOffset(i, t);
            float offsetX = (float)(offsetFrac * width * 0.06 * envelope);
            if (Math.Abs(offsetX) < 0.5f) continue;

            var sliceSrc = new Rect(0, i * srcSliceHeight,
                incoming.SizeInPixels.Width, srcSliceHeight);
            double destY = i * sliceHeight;
            var sliceDest = new Rect(offsetX, destY, width, sliceHeight);

            // Clip vertically to this slice's band only — the horizontal offset is the whole
            // point of the effect, but it must not smear into neighbouring slices' rows.
            using var layer = ds.CreateLayer(1.0f, new Rect(0, destY, width, sliceHeight));
            ds.DrawImage(incoming, sliceDest, sliceSrc, (float)envelope);
        }
    }

    /// <summary>
    /// Deterministic pseudo-random horizontal displacement fraction (range roughly -1..1) for a
    /// glitch slice, driven only by <paramref name="sliceIndex"/> and <paramref name="progress"/>
    /// — no <see cref="Random"/>, no wall-clock seed. The same (slice, progress) pair always
    /// produces the same result, which <see cref="RenderGlitch"/>'s doc comment explains is
    /// required so preview and export render identically for the same instant.
    /// </summary>
    internal static double GlitchSliceOffset(int sliceIndex, double progress)
    {
        unchecked
        {
            // Knuth multiplicative hash of the slice index gives a stable per-slice phase.
            uint hash = (uint)sliceIndex * 2654435761u;
            double phase = (hash % 997) / 997.0;
            double direction = (sliceIndex % 2 == 0) ? 1.0 : -1.0;
            return direction * Math.Sin((progress * 3.0 + phase) * Math.PI * 2.0);
        }
    }

    /// <summary>
    /// Computes the isotropic blur "amount" to apply in <paramref name="bitmapWidth"/> x
    /// <paramref name="bitmapHeight"/> source-pixel space so that, once scaled into an
    /// <paramref name="outputWidth"/> x <paramref name="outputHeight"/> destination via
    /// <see cref="CanvasDrawingSession.DrawImage(Microsoft.Graphics.Canvas.Effects.ICanvasImage,Rect,Rect,float)"/>'s
    /// dest/src <see cref="Rect"/> overload, the blur radius measures approximately
    /// <paramref name="desiredOutputBlur"/> pixels in OUTPUT space — regardless of the bitmap's
    /// own resolution. A text slide renders at project resolution while a video frame renders at
    /// its own source resolution; blurring each in its own native pixel space and then relying on
    /// the dest/src scale to bring it into the shared output frame (as every effect here must,
    /// since the two inputs can be different sizes) otherwise divides the *effective* output blur
    /// by that input's own source→output scale factor, so the same nominal "blur amount" looks
    /// wildly different on each side of the transition. Clamped to 250 (Direct2D's accepted
    /// maximum for both Gaussian and directional blur radii) so an extreme downscale (a large
    /// source into a tiny output) can't overflow the effect's valid range.
    /// </summary>
    internal static float CompensateBlurForScale(
        float desiredOutputBlur, double bitmapWidth, double bitmapHeight,
        int outputWidth, int outputHeight)
    {
        if (bitmapWidth <= 0 || bitmapHeight <= 0) return desiredOutputBlur;

        double scaleX = outputWidth / bitmapWidth;
        double scaleY = outputHeight / bitmapHeight;
        double scale = Math.Sqrt(scaleX * scaleY);
        if (scale <= 0) return desiredOutputBlur;

        return Math.Clamp((float)(desiredOutputBlur / scale), 0f, 250f);
    }

    /// <summary>
    /// Same source→output normalisation as <see cref="CompensateBlurForScale"/>, but for a 1-D
    /// directional blur along a single axis (<see cref="RenderWhipPan"/>'s horizontal motion
    /// blur) — only that axis's scale factor matters, not the geometric mean of both.
    /// </summary>
    internal static float CompensateDirectionalBlurForScale(
        float desiredOutputBlur, double bitmapWidth, int outputWidth)
    {
        if (bitmapWidth <= 0) return desiredOutputBlur;

        double scaleX = outputWidth / bitmapWidth;
        if (scaleX <= 0) return desiredOutputBlur;

        return Math.Clamp((float)(desiredOutputBlur / scaleX), 0f, 250f);
    }

    /// <summary>
    /// Unimodal shaping curve for effect-intensity envelopes (blur radius, glitch strength): 0 at
    /// t=0 and t=1, 1 at t=0.5. This shapes an effect's own identity, not overall
    /// crossfade/motion progress — <paramref name="t"/> itself must still be used unshaped for
    /// any positional/opacity blend (see the no-double-easing note on <see cref="Render"/>).
    /// </summary>
    internal static double PeakEnvelope(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 4.0 * t * (1.0 - t);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
