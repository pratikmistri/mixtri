using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Mixtri.Core.AI;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class SubtitleBurnerTests
{
    private static readonly Color ClearColor = Color.FromArgb(255, 25, 30, 40);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    [DataRow(SubtitlePosition.Top, 96f)]
    [DataRow(SubtitlePosition.Center, 144f)]
    [DataRow(SubtitlePosition.Bottom, 192f)]
    public void CachedSubtitlesMatchPreviousPixels(SubtitlePosition position, float dpi)
    {
        var style = new SubtitleStyle
        {
            FontFamily = "Segoe UI", FontSize = 19,
            TextColor = "#DDEECCAA", BackgroundColor = "#70123456",
            Position = position, PaddingHorizontal = 9, PaddingVertical = 5, MarginBottom = 13,
        };
        List<SubtitleSegment> segments =
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "A caption that wraps across several lines in a narrow output."),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "Second line\n\u05e9\u05dc\u05d5\u05dd  \u4f60\u597d"),
        ];
        using var burner = new SubtitleBurner(segments, style);
        foreach (var (width, height) in new[] { (260, 150), (340, 180), (260, 150) })
        {
            using var expected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, dpi);
            using var actual = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, dpi);
            foreach (double time in new[] { -.1, 0, .5, 1.999, 2, 2.5, 3, 4.9, 5, .5 })
            {
                DrawPrevious(expected, segments, style, time, width, height);
                Draw(actual, burner, time, width, height);
                CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(),
                    $"Time {time}, size {width}x{height}");
            }
        }
        Assert.AreEqual(1, burner.TextFormatCreationCount);
    }

    [TestMethod]
    public void UnchangedSubtitlesCreateOneFormatAndLayout()
    {
        List<SubtitleSegment> segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Unchanged caption")];
        using var burner = new SubtitleBurner(segments, new SubtitleStyle());
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        Assert.AreEqual(0, burner.TextFormatCreationCount);
        Assert.AreEqual(0, burner.TextLayoutCreationCount);
        using (var ds = target.CreateDrawingSession())
        {
            for (int frame = 0; frame < 300; frame++)
            {
                ds.Clear(ClearColor);
                burner.RenderSubtitle(ds, frame / 30.0, 320, 180);
            }
        }
        Assert.AreEqual(1, burner.TextFormatCreationCount);
        Assert.AreEqual(1, burner.TextLayoutCreationCount);
    }

    [TestMethod]
    public void SameLayoutPreservesPixelsAcrossDisplayDpiChanges()
    {
        List<SubtitleSegment> segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Same caption at a different display scale")];
        var style = new SubtitleStyle { FontSize = 16, MarginBottom = 8 };
        using var burner = new SubtitleBurner(segments, style);
        foreach (float dpi in new[] { 96f, 144f, 192f, 96f })
        {
            using var expected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 120, dpi);
            using var actual = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 120, dpi);
            DrawPrevious(expected, segments, style, 1, 240, 120);
            Draw(actual, burner, 1, 240, 120);
            CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(), $"DPI {dpi}");
        }
        Assert.AreEqual(1, burner.TextLayoutCreationCount);
    }

    [TestMethod]
    public void EmptyAndInactiveTracksCreateNoNativeTextResources()
    {
        List<SubtitleSegment> segments = [];
        using var burner = new SubtitleBurner(segments, new SubtitleStyle());
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        Draw(target, burner, 1, 320, 180);
        segments.Add(new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "Later"));
        Draw(target, burner, 1, 320, 180);
        Draw(target, burner, 3, 320, 180);
        Assert.AreEqual(0, burner.TextFormatCreationCount);
        Assert.AreEqual(0, burner.TextLayoutCreationCount);
        Assert.IsTrue(target.GetPixelColors().All(color => color == ClearColor));
    }

    [TestMethod]
    public void LiveEditsAndOverlapsKeepFirstListMatchAndExclusiveEnd()
    {
        List<SubtitleSegment> segments =
        [
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), "First in list, later in time"),
            new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Earlier in time, second in list"),
        ];
        var style = new SubtitleStyle();
        using var burner = new SubtitleBurner(segments, style);
        using var expected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        using var actual = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        foreach (double time in new[] { 1d, 2, 5, 6 })
        {
            DrawPrevious(expected, segments, style, time, 320, 180);
            Draw(actual, burner, time, 320, 180);
            CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes());
        }
        segments.Insert(0, new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Inserted at the front"));
        DrawPrevious(expected, segments, style, 2, 320, 180);
        Draw(actual, burner, 2, 320, 180);
        CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes());
        segments[0] = segments[0] with { Text = "Edited text" };
        DrawPrevious(expected, segments, style, 2, 320, 180);
        Draw(actual, burner, 2, 320, 180);
        CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes());
        segments.Clear();
        Draw(actual, burner, 2, 320, 180);
        Assert.IsTrue(actual.GetPixelColors().All(color => color == ClearColor));
    }

    [TestMethod]
    public void LayoutIsReplacedAndClosedWhenTextOrDimensionsChange()
    {
        List<SubtitleSegment> segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "First")];
        using var burner = new SubtitleBurner(segments, new SubtitleStyle());
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        Draw(target, burner, 1, 320, 180);
        var old = Layout(burner);
        segments[0] = segments[0] with { Text = "Changed" };
        Draw(target, burner, 1, 320, 180);
        AssertClosed(() => _ = old.LayoutBounds);
        old = Layout(burner);
        Draw(target, burner, 1, 300, 180);
        AssertClosed(() => _ = old.LayoutBounds);
        old = Layout(burner);
        Draw(target, burner, 1, 300, 160);
        AssertClosed(() => _ = old.LayoutBounds);
        Assert.AreEqual(4, burner.TextLayoutCreationCount);
        Assert.AreEqual(1, burner.TextFormatCreationCount);
    }

    [TestMethod]
    public void InactiveCueReleasesItsLayoutAndCanBeScrubbedBackInto()
    {
        using var burner = new SubtitleBurner(
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Caption")], new SubtitleStyle());
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        Draw(target, burner, 1, 320, 180);
        var old = Layout(burner);
        var pixels = target.GetPixelBytes();
        Draw(target, burner, 2, 320, 180);
        AssertClosed(() => _ = old.LayoutBounds);
        Assert.IsNull(typeof(SubtitleBurner).GetField("_textLayout", Fields)!.GetValue(burner));
        Draw(target, burner, 1, 320, 180);
        CollectionAssert.AreEqual(pixels, target.GetPixelBytes());
        Assert.AreEqual(2, burner.TextLayoutCreationCount);
        Assert.AreEqual(1, burner.TextFormatCreationCount);
    }

    [TestMethod]
    public void DeviceChangesRebuildOnlyTheLayoutAndDoNotDisposeCallerDevices()
    {
        using var firstDevice = new CanvasDevice();
        using var secondDevice = new CanvasDevice();
        using var firstTarget = new CanvasRenderTarget(firstDevice, 240, 120, 96);
        using var secondTarget = new CanvasRenderTarget(secondDevice, 240, 120, 96);
        using var burner = new SubtitleBurner(
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Device change")], new SubtitleStyle { FontSize = 14 });
        Draw(firstTarget, burner, 1, 240, 120);
        var old = Layout(burner);
        Draw(secondTarget, burner, 1, 240, 120);
        AssertClosed(() => _ = old.LayoutBounds);
        CollectionAssert.AreEqual(firstTarget.GetPixelBytes(), secondTarget.GetPixelBytes());
        Assert.AreEqual(2, burner.TextLayoutCreationCount);
        Assert.AreEqual(1, burner.TextFormatCreationCount);
        burner.Dispose();
        using var ds = firstTarget.CreateDrawingSession();
        ds.Clear(ClearColor);
    }

    [TestMethod]
    public async Task CompositorUsesAndDisposesTheSubtitleCache()
    {
        using var compositor = new FrameCompositor(new CompositionConfig
        {
            OutputFps = 30, Cursor = new CursorStyle { Type = CursorType.Hidden },
            SubtitleStyle = new SubtitleStyle { FontSize = 14, MarginBottom = 10 },
            Subtitles = [new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Composed subtitle")],
        });
        await compositor.InitializeAsync(
            TestMouseRecordingBuilder.WithPositions(60, 30, _ => (50, 50)), 240, 120, TimeSpan.FromSeconds(2));
        var burner = (SubtitleBurner)typeof(FrameCompositor).GetField("_subtitleBurner", Fields)!.GetValue(compositor)!;
        using var source = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 120, 96);
        using (var ds = source.CreateDrawingSession()) ds.Clear(ClearColor);
        for (int frame = 0; frame < 30; frame++)
        {
            using var result = compositor.ComposeFrame(source, frame);
        }
        Assert.AreEqual(1, burner.TextLayoutCreationCount);
        var layout = Layout(burner);
        var format = (CanvasTextFormat)typeof(SubtitleBurner).GetField("_textFormat", Fields)!.GetValue(burner)!;
        compositor.Dispose();
        AssertClosed(() => _ = layout.LayoutBounds);
        AssertClosed(() => _ = format.FontSize);
        using var session = source.CreateDrawingSession();
        Assert.ThrowsException<ObjectDisposedException>(() => burner.RenderSubtitle(session, 0, 240, 120));
        burner.Dispose();
    }

    private static CanvasTextLayout Layout(SubtitleBurner burner) =>
        (CanvasTextLayout)typeof(SubtitleBurner).GetField("_textLayout", Fields)!.GetValue(burner)!;

    private static void Draw(CanvasRenderTarget target, SubtitleBurner burner, double time, int width, int height)
    {
        using var ds = target.CreateDrawingSession();
        ds.Clear(ClearColor);
        burner.RenderSubtitle(ds, time, width, height);
    }

    private static void DrawPrevious(CanvasRenderTarget target, List<SubtitleSegment> segments,
        SubtitleStyle style, double time, int width, int height)
    {
        using var ds = target.CreateDrawingSession();
        ds.Clear(ClearColor);
        var instant = TimeSpan.FromSeconds(time);
        var current = segments.FirstOrDefault(segment => instant >= segment.Start && instant < segment.End);
        if (current is null) return;
        using var format = new CanvasTextFormat
        {
            FontFamily = style.FontFamily, FontSize = style.FontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.Wrap,
        };
        using var layout = new CanvasTextLayout(ds, current.Text, format, width - style.PaddingHorizontal * 4, height);
        float backgroundWidth = (float)layout.LayoutBounds.Width + style.PaddingHorizontal * 2;
        float backgroundHeight = (float)layout.LayoutBounds.Height + style.PaddingVertical * 2;
        float x = (width - backgroundWidth) / 2f;
        float y = style.Position switch
        {
            SubtitlePosition.Top => style.MarginBottom,
            SubtitlePosition.Center => (height - backgroundHeight) / 2f,
            _ => height - backgroundHeight - style.MarginBottom,
        };
        ds.FillRoundedRectangle(x, y, backgroundWidth, backgroundHeight, 8, 8, Parse(style.BackgroundColor));
        ds.DrawTextLayout(layout, new Vector2(x + style.PaddingHorizontal, y + style.PaddingVertical), Parse(style.TextColor));
    }

    private static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        uint value = Convert.ToUInt32(hex, 16);
        return Color.FromArgb(hex.Length == 8 ? (byte)(value >> 24) : (byte)255,
            (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static void AssertClosed(Action read)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return; }
        Assert.Fail("The native subtitle resource was not closed.");
    }
}
