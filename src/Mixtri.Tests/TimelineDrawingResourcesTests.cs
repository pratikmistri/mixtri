using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Mixtri_App.Controls;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Mixtri.Tests;

[TestClass]
public class TimelineDrawingResourcesTests
{
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color LightGray = Color.FromArgb(255, 211, 211, 211);
    private static readonly Color Orange = Color.FromArgb(255, 255, 165, 0);
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly FontWeight SemiBold = new() { Weight = 600 };

    [TestMethod]
    [DataRow(nameof(TimelineTextStyle.Plain11))]
    [DataRow(nameof(TimelineTextStyle.SemiBold10))]
    [DataRow(nameof(TimelineTextStyle.SemiBold11))]
    [DataRow(nameof(TimelineTextStyle.SemiBold12))]
    [DataRow(nameof(TimelineTextStyle.EmptyPlaceholder))]
    [DataRow(nameof(TimelineTextStyle.ZoomHint))]
    [DataRow(nameof(TimelineTextStyle.TransitionGlyph))]
    [DataRow(nameof(TimelineTextStyle.SpeedBadge))]
    [DataRow(nameof(TimelineTextStyle.AudioLabel))]
    [DataRow(nameof(TimelineTextStyle.OverlayLabel))]
    [DataRow(nameof(TimelineTextStyle.SlideLabel))]
    public void CachedFormatsMatchPreviousPixels(string styleName)
    {
        var style = Enum.Parse<TimelineTextStyle>(styleName);
        using var resources = new TimelineDrawingResources();
        using var previous = CreatePreviousFormat(style);
        var cached = resources.GetTextFormat(style);
        foreach (float dpi in new[] { 96f, 144f, 192f })
        {
            CollectionAssert.AreEqual(RenderText(previous, dpi), RenderText(cached, dpi), $"DPI {dpi}");
            Assert.AreSame(cached, resources.GetTextFormat(style));
        }
        Assert.AreEqual(1, resources.CreatedTextFormatCount);
    }

    [TestMethod]
    public void AnimatedSlideHeightsReuseOneFormatAndPreservePixels()
    {
        using var resources = new TimelineDrawingResources();
        var first = resources.GetSlideLabel(15);
        foreach (float size in new[] { 6.3f, 10.5f, 15f, 8.125f, 15f })
        {
            using var previous = CreatePreviousFormat(TimelineTextStyle.SlideLabel);
            previous.FontSize = size;
            var cached = resources.GetSlideLabel(size);
            Assert.AreSame(first, cached);
            CollectionAssert.AreEqual(RenderText(previous, 144), RenderText(cached, 144));
        }
        for (int i = 1; i <= 1000; i++) resources.GetSlideLabel(6 + i * .009f);
        Assert.AreEqual(1, resources.CachedTextFormatCount);
        Assert.AreEqual(1, resources.CreatedTextFormatCount);
    }

    [TestMethod]
    public void CachedStrokesMatchPreviousPixels()
    {
        using var resources = new TimelineDrawingResources();
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var rounded = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        foreach (float dpi in new[] { 96f, 144f, 192f })
            CollectionAssert.AreEqual(
                RenderStrokes(dashed, rounded, dpi),
                RenderStrokes(resources.DashedStroke, resources.RoundedStroke, dpi));
        Assert.AreEqual(2, resources.CreatedStrokeStyleCount);
    }

    [TestMethod]
    public void RepeatedRepaintsCreateOnlyTheFiniteStyleSet()
    {
        using var resources = new TimelineDrawingResources();
        Assert.AreEqual(0, resources.CreatedTextFormatCount);
        Assert.AreEqual(0, resources.CreatedStrokeStyleCount);
        var styles = Enum.GetValues<TimelineTextStyle>();
        for (int frame = 0; frame < 1000; frame++)
        {
            foreach (var style in styles) resources.GetTextFormat(style);
            for (int tick = 0; tick < 20; tick++) resources.GetTextFormat(TimelineTextStyle.Plain11);
            _ = resources.DashedStroke;
            _ = resources.RoundedStroke;
        }
        Assert.AreEqual(styles.Length, resources.CreatedTextFormatCount);
        Assert.AreEqual(styles.Length, resources.CachedTextFormatCount);
        Assert.AreEqual(2, resources.CreatedStrokeStyleCount);
    }

    [TestMethod]
    public void ClearReleasesResourcesAndAllowsReload()
    {
        using var resources = new TimelineDrawingResources();
        var oldFormat = resources.GetTextFormat(TimelineTextStyle.Plain11);
        var oldDashed = resources.DashedStroke;
        var oldRounded = resources.RoundedStroke;
        var pixels = RenderText(oldFormat, 96);
        resources.Clear();
        resources.Clear();
        Assert.AreEqual(0, resources.CachedTextFormatCount);
        AssertClosed(() => _ = oldFormat.FontSize);
        AssertClosed(() => _ = oldDashed.DashStyle);
        AssertClosed(() => _ = oldRounded.StartCap);
        var newFormat = resources.GetTextFormat(TimelineTextStyle.Plain11);
        Assert.AreNotSame(oldFormat, newFormat);
        Assert.AreNotSame(oldDashed, resources.DashedStroke);
        Assert.AreNotSame(oldRounded, resources.RoundedStroke);
        CollectionAssert.AreEqual(pixels, RenderText(newFormat, 96));
    }

    [TestMethod]
    public void PermanentDisposalRejectsNewResources()
    {
        var resources = new TimelineDrawingResources();
        var format = resources.GetTextFormat(TimelineTextStyle.OverlayLabel);
        resources.Dispose();
        resources.Dispose();
        resources.Clear();
        AssertClosed(() => _ = format.FontSize);
        Assert.ThrowsException<ObjectDisposedException>(() => resources.GetTextFormat(TimelineTextStyle.Plain11));
        Assert.ThrowsException<ObjectDisposedException>(() => resources.GetSlideLabel(10));
        Assert.ThrowsException<ObjectDisposedException>(() => _ = resources.DashedStroke);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = resources.RoundedStroke);
    }

    [TestMethod]
    public void InvalidStylesAndSizesDoNotAllocateResources()
    {
        using var resources = new TimelineDrawingResources();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => resources.GetTextFormat((TimelineTextStyle)int.MaxValue));
        foreach (float size in new[] { 0, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => resources.GetSlideLabel(size));
        Assert.AreEqual(0, resources.CreatedTextFormatCount);
    }

    private static byte[] RenderText(CanvasTextFormat format, float dpi)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 160, dpi);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 25, 30, 40));
            ds.DrawText("0:05.0  2.5x Camera", 3, 1, White, format);
            ds.DrawText("Record or import a video to start your timeline",
                new Rect(4, 35, 300, 28), LightGray, format);
            ds.DrawText("Long audio/text label (muted)", new Rect(8, 75, 90, 25), Orange, format);
            ds.DrawText("first line\nsecond line", new Rect(110, 75, 140, 70), White, format);
        }
        return target.GetPixelBytes();
    }

    private static byte[] RenderStrokes(CanvasStrokeStyle dashed, CanvasStrokeStyle rounded, float dpi)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 100, dpi);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Transparent);
            ds.DrawRoundedRectangle(5, 5, 230, 40, 6, 6, White, 1.2f, dashed);
            ds.DrawLine(10, 70, 200, 70, Orange, 3, rounded);
            using var geometry = CanvasGeometry.CreateRoundedRectangle(ds, 10, 80, 200, 12, 4, 4);
            ds.DrawGeometry(geometry, LightGray, 1, dashed);
        }
        return target.GetPixelBytes();
    }

    private static void AssertClosed(Action read)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return; }
        Assert.Fail("The native drawing resource was not closed.");
    }

    private static CanvasTextFormat CreatePreviousFormat(TimelineTextStyle style) => style switch
    {
        TimelineTextStyle.Plain11 => new() { FontSize = 11, FontFamily = "Segoe UI" },
        TimelineTextStyle.SemiBold10 => new() { FontSize = 10, FontFamily = "Segoe UI", FontWeight = SemiBold },
        TimelineTextStyle.SemiBold11 => new() { FontSize = 11, FontFamily = "Segoe UI", FontWeight = SemiBold },
        TimelineTextStyle.SemiBold12 => new() { FontSize = 12, FontFamily = "Segoe UI", FontWeight = SemiBold },
        TimelineTextStyle.EmptyPlaceholder => new()
        {
            FontSize = 12, FontFamily = "Segoe UI",
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        },
        TimelineTextStyle.ZoomHint => new()
        {
            FontSize = 10, FontFamily = "Segoe UI", FontStyle = FontStyle.Italic,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        },
        TimelineTextStyle.TransitionGlyph => new()
        {
            FontSize = 10, FontFamily = "Segoe UI", FontWeight = SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        },
        TimelineTextStyle.SpeedBadge => new()
        {
            FontSize = 10, FontFamily = "Segoe UI", FontWeight = SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        },
        TimelineTextStyle.AudioLabel => new()
        {
            FontSize = 10, FontFamily = "Segoe UI", FontWeight = SemiBold,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
        },
        TimelineTextStyle.OverlayLabel => new()
        {
            FontSize = 11, FontFamily = "Segoe UI", FontWeight = SemiBold,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
        },
        TimelineTextStyle.SlideLabel => new()
        {
            FontSize = 15, FontFamily = "Segoe UI", FontWeight = SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };
}
