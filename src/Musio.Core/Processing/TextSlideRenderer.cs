using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Musio.Core.Timeline;
using Windows.UI;
using Windows.UI.Text;

namespace Musio.Core.Processing;

/// <summary>
/// Renders <see cref="TextSlideSegment"/> frames and <see cref="TextOverlay"/>
/// elements using Win2D text/drawing APIs. Handles text animation based on
/// normalized progress within the segment or overlay duration.
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
    /// <param name="slide">The text slide segment configuration.</param>
    /// <param name="progress">Normalized progress within the slide (0..1).</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    public CanvasRenderTarget RenderSlide(TextSlideSegment slide, double progress, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = new CanvasRenderTarget(_device, width, height, 96);
        using var ds = target.CreateDrawingSession();

        // Background
        var bgColor = ParseColor(slide.BackgroundColor);
        ds.Clear(bgColor);

        // Text
        var textColor = ParseColor(slide.TextColor);
        var opacity = ComputeAnimationOpacity(slide.Animation, progress);
        textColor.A = (byte)(textColor.A * opacity);

        var text = ApplyTypewriterEffect(slide.Text, slide.Animation, progress);

        using var format = new CanvasTextFormat
        {
            FontFamily = slide.FontFamily,
            FontSize = (float)slide.FontSize,
            FontWeight = slide.IsBold
                ? new FontWeight { Weight = 700 }
                : new FontWeight { Weight = 400 },
            FontStyle = slide.IsItalic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.WholeWord,
        };

        var verticalOffset = ComputeSlideOffset(slide.Animation, progress, height);

        var textRect = new Windows.Foundation.Rect(
            width * 0.1,
            height * 0.1 + verticalOffset,
            width * 0.8,
            height * 0.8);

        ds.DrawText(text, textRect, textColor, format);

        return target;
    }

    /// <summary>
    /// Renders a text overlay onto an existing drawing session.
    /// </summary>
    /// <param name="ds">The drawing session to render into.</param>
    /// <param name="overlay">The text overlay configuration.</param>
    /// <param name="progress">Normalized progress within the overlay duration (0..1).</param>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    public void RenderOverlay(CanvasDrawingSession ds, TextOverlay overlay, double progress, int width, int height)
    {
        var textColor = ParseColor(overlay.TextColor);
        var opacity = ComputeAnimationOpacity(overlay.Animation, progress);
        textColor.A = (byte)(textColor.A * opacity);

        var text = ApplyTypewriterEffect(overlay.Text, overlay.Animation, progress);

        using var format = new CanvasTextFormat
        {
            FontFamily = overlay.FontFamily,
            FontSize = (float)overlay.FontSize,
            FontWeight = overlay.IsBold
                ? new FontWeight { Weight = 700 }
                : new FontWeight { Weight = 400 },
            FontStyle = overlay.IsItalic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // Measure text to center around the overlay position
        using var layout = new CanvasTextLayout(_device, text, format, (float)(width * 0.8), (float)(height * 0.5));
        var textWidth = layout.LayoutBounds.Width;
        var textHeight = layout.LayoutBounds.Height;

        var x = overlay.X * width - textWidth / 2;
        var y = overlay.Y * height - textHeight / 2;

        // Draw background if not fully transparent
        var bgColor = ParseColor(overlay.BackgroundColor);
        if (bgColor.A > 0)
        {
            var padding = 12.0;
            ds.FillRoundedRectangle(
                (float)(x - padding), (float)(y - padding),
                (float)(textWidth + padding * 2), (float)(textHeight + padding * 2),
                8, 8, bgColor);
        }

        var verticalOffset = ComputeSlideOffset(overlay.Animation, progress, height);
        var textRect = new Windows.Foundation.Rect(x, y + verticalOffset, textWidth, textHeight);
        ds.DrawText(text, textRect, textColor, format);
    }

    private static double ComputeAnimationOpacity(TextSlideAnimation animation, double progress)
    {
        const double fadeRegion = 0.2; // 20% of duration for fade-in/out

        return animation switch
        {
            TextSlideAnimation.None => 1.0,
            TextSlideAnimation.FadeIn => Math.Clamp(progress / fadeRegion, 0, 1),
            TextSlideAnimation.FadeOut => Math.Clamp((1.0 - progress) / fadeRegion, 0, 1),
            TextSlideAnimation.FadeInOut => progress < 0.5
                ? Math.Clamp(progress / fadeRegion, 0, 1)
                : Math.Clamp((1.0 - progress) / fadeRegion, 0, 1),
            TextSlideAnimation.TypeWriter => 1.0,
            TextSlideAnimation.SlideUp => Math.Clamp(progress / fadeRegion, 0, 1),
            TextSlideAnimation.SlideDown => Math.Clamp(progress / fadeRegion, 0, 1),
            _ => 1.0,
        };
    }

    private static double ComputeSlideOffset(TextSlideAnimation animation, double progress, double height)
    {
        const double slideDistance = 0.15; // 15% of height
        const double animDuration = 0.2;  // 20% of total duration

        return animation switch
        {
            TextSlideAnimation.SlideUp => progress < animDuration
                ? height * slideDistance * (1.0 - progress / animDuration)
                : 0,
            TextSlideAnimation.SlideDown => progress < animDuration
                ? -height * slideDistance * (1.0 - progress / animDuration)
                : 0,
            _ => 0,
        };
    }

    private static string ApplyTypewriterEffect(string text, TextSlideAnimation animation, double progress)
    {
        if (animation != TextSlideAnimation.TypeWriter || string.IsNullOrEmpty(text))
            return text;

        // Reserve last 20% for the full text to be visible
        var typingProgress = Math.Clamp(progress / 0.8, 0, 1);
        var charCount = (int)(text.Length * typingProgress);
        return text[..Math.Min(charCount, text.Length)];
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
