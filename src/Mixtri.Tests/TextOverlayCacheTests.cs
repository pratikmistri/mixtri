using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class TextOverlayCacheTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Color Background = Color.FromArgb(255, 30, 55, 80);

    [TestMethod]
    public void CacheEvictionScansTheLiveListLinearly()
    {
        var overlays = Enumerable.Range(0, 100).Select(i => Overlay($"overlay-{i}")).ToList();
        using var renderer = new TextOverlayRenderer();
        using var target = Target();
        renderer.Render(target, overlays, TimeSpan.FromSeconds(1), 320, 180);
        Assert.AreEqual(100, Cache(renderer).Count);
        var counted = new CountedOverlays(overlays);
        int previousReads = 0;
        foreach (string id in Cache(renderer).Keys)
        {
            for (int i = 0; i < counted.Count; i++)
            {
                previousReads++;
                if (counted[i].Id == id) break;
            }
        }
        counted.ResetCounts();
        renderer.Render(target, counted, TimeSpan.FromSeconds(10), 320, 180);
        Assert.AreEqual(5050, previousReads);
        Assert.AreEqual(200, counted.IndexReads);
        Assert.AreEqual(0, counted.Enumerations);
        Assert.AreEqual(100, Cache(renderer).Count);
        Console.WriteLine($"100 cached overlays: {previousReads:N0} previous cache lookups; " +
            $"{counted.IndexReads:N0} indexed reads including inactive-frame selection.");
    }

    [TestMethod]
    public void SmallCacheStopsScanningOnceItsEntriesAreFound()
    {
        var first = Overlay("first");
        using var renderer = new TextOverlayRenderer();
        using var target = Target();
        renderer.Render(target, new[] { first }, TimeSpan.FromSeconds(1), 320, 180);
        var overlays = new List<TextOverlaySegment> { first };
        overlays.AddRange(Enumerable.Range(0, 1000).Select(i => Overlay($"later-{i}")));
        var counted = new CountedOverlays(overlays);
        var evict = typeof(TextOverlayRenderer).GetMethod("EvictStaleCacheEntries", Fields)!
            .CreateDelegate<Action<IReadOnlyList<TextOverlaySegment>>>(renderer);
        evict(counted);
        Assert.AreEqual(1, counted.IndexReads);
        Assert.AreEqual(1, Cache(renderer).Count);
    }

    [TestMethod]
    public void InactiveFramesAllocateNothingAfterWarmup()
    {
        var overlays = Enumerable.Range(0, 20).Select(i => Overlay($"overlay-{i}")).ToList();
        using var renderer = new TextOverlayRenderer();
        using var target = Target();
        renderer.Render(target, overlays, TimeSpan.FromSeconds(1), 320, 180);
        var inactive = TimeSpan.FromSeconds(10);
        for (int i = 0; i < 10; i++) renderer.Render(target, overlays, inactive, 320, 180);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) renderer.Render(target, overlays, inactive, 320, 180);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void DuplicateLiveIdsDoNotProtectOtherDeletedCacheEntries()
    {
        var a = Overlay("a");
        var b = Overlay("b");
        var c = Overlay("c");
        using var renderer = new TextOverlayRenderer();
        using var target = Target();
        renderer.Render(target, new[] { a, b, c }, TimeSpan.FromSeconds(1), 320, 180);
        var formatA = Format(Cache(renderer)["a"]!);
        var formatB = Format(Cache(renderer)["b"]!);
        var formatC = Format(Cache(renderer)["c"]!);
        renderer.Render(target, new[] { a, a with { Text = "duplicate" } }, TimeSpan.FromSeconds(10), 320, 180);
        Assert.AreEqual(1, Cache(renderer).Count);
        Assert.AreSame(formatA, Format(Cache(renderer)["a"]!));
        AssertClosed(() => _ = formatB.FontSize);
        AssertClosed(() => _ = formatC.FontSize);
        renderer.Render(target, Array.Empty<TextOverlaySegment>(), TimeSpan.FromSeconds(10), 320, 180);
        Assert.AreEqual(0, Cache(renderer).Count);
        AssertClosed(() => _ = formatA.FontSize);
    }

    [TestMethod]
    [DataRow(TextOverlayBackground.None)]
    [DataRow(TextOverlayBackground.Solid)]
    [DataRow(TextOverlayBackground.Blur)]
    [DataRow(TextOverlayBackground.GradientScrim)]
    [DataRow(TextOverlayBackground.OutlineShadow)]
    [DataRow(TextOverlayBackground.AccentBar)]
    public void RenderingPreservesPreviousPixelsAcrossOrderAndContentEdits(TextOverlayBackground background)
    {
        var overlays = new List<TextOverlaySegment>
        {
            Overlay("first") with { Background = background, Animation = TextSlideAnimation.FadeIn, X = .45, TextColor = "#FFAA77" },
            Overlay("second") with { Background = background, X = .6, TextColor = "#77AAFF" },
        };
        using var previous = new TextOverlayRenderer();
        using var current = new TextOverlayRenderer();
        using var expected = Target();
        using var actual = Target();
        foreach (int step in Enumerable.Range(0, 7))
        {
            if (step == 2) overlays.Reverse();
            if (step == 3) overlays[0].Text = "Edited overlay";
            if (step == 4) overlays.RemoveAt(1);
            if (step == 5) overlays.Clear();
            if (step == 6) overlays.Add(Overlay("new") with { Background = background });
            var time = TimeSpan.FromSeconds(step == 0 ? .05 : step == 4 ? 10 : 1);
            Clear(expected);
            Clear(actual);
            PreviousRender(previous, expected, overlays, time);
            current.Render(actual, overlays, time, 320, 180);
            CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(), $"Step {step}");
            Assert.AreEqual(Cache(previous).Count, Cache(current).Count);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovingTheFinalOverlayReleasesBlurAndTextAndCanBeUndone(bool mutateLiveList)
    {
        using var compositor = new FrameCompositor(new CompositionConfig
        {
            Cursor = new CursorStyle { Type = CursorType.Hidden },
            Zoom = new AutoZoomConfig { Enabled = false },
            MotionBlur = new MotionBlurSettings { Enabled = false },
            Background = new BackgroundStyle { Padding = 0, CornerRadius = 0, ShadowEnabled = false },
        });
        await compositor.InitializeAsync(
            TestMouseRecordingBuilder.WithPositions(60, 30, _ => (50, 50)), 320, 180, TimeSpan.FromSeconds(2));
        using var source = Target();
        var overlay = Overlay("blur") with { Background = TextOverlayBackground.Blur };
        var list = new List<TextOverlaySegment> { overlay };
        compositor.SyncTextOverlays(list);
        using var first = compositor.ComposeFrame(source, 30);
        var renderer = Field<TextOverlayRenderer>(compositor, "_textOverlayRenderer");
        var blur = Field<GrowOnlyBuffer>(renderer, "_blurScratchHolder").Current;
        var format = Format(Cache(renderer)["blur"]!);
        Assert.IsNotNull(blur);

        if (mutateLiveList)
        {
            list.Clear();
            using var empty = compositor.ComposeFrame(source, 30);
        }
        else compositor.SyncTextOverlays([]);

        Assert.IsNull(typeof(FrameCompositor).GetField("_textOverlayRenderer", Fields)!.GetValue(compositor));
        AssertClosed(() => _ = blur.SizeInPixels);
        AssertClosed(() => _ = format.FontSize);
        if (mutateLiveList) list.Add(overlay);
        else compositor.SyncTextOverlays(list);
        using var restored = compositor.ComposeFrame(source, 30);
        CollectionAssert.AreEqual(first.GetPixelBytes(), restored.GetPixelBytes());
        Assert.AreNotSame(renderer, Field<TextOverlayRenderer>(compositor, "_textOverlayRenderer"));
    }

    private static TextOverlaySegment Overlay(string id) => new()
    {
        Id = id, Text = id, Start = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(4),
        Animation = TextSlideAnimation.None, Background = TextOverlayBackground.None,
        Anchor = TextOverlayAnchor.Custom, X = .5, Y = .5, WidthFraction = .7, HeightFraction = .4,
    };

    private static CanvasRenderTarget Target()
    {
        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96);
        Clear(target);
        return target;
    }

    private static void Clear(CanvasRenderTarget target)
    {
        using var ds = target.CreateDrawingSession();
        ds.Clear(Background);
        ds.FillRectangle(20, 20, 130, 110, Color.FromArgb(255, 80, 120, 170));
    }

    private static void PreviousRender(TextOverlayRenderer renderer, CanvasRenderTarget target,
        IReadOnlyList<TextOverlaySegment> overlays, TimeSpan time)
    {
        var cache = Cache(renderer);
        foreach (string id in cache.Keys.Cast<string>().ToArray())
        {
            bool present = false;
            for (int i = 0; i < overlays.Count; i++)
                if (overlays[i].Id == id) { present = true; break; }
            if (present) continue;
            ((IDisposable)cache[id]!).Dispose();
            cache.Remove(id);
        }
        var draw = typeof(TextOverlayRenderer).GetMethod("RenderOverlay", Fields)!
            .CreateDelegate<Action<CanvasRenderTarget, TextOverlaySegment, TimeSpan, int, int>>(renderer);
        foreach (var overlay in overlays)
        {
            if (overlay.Enabled && !string.IsNullOrEmpty(overlay.Text) && time >= overlay.Start && time < overlay.End)
                draw(target, overlay, time, 320, 180);
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Fields)!.GetValue(target)!;

    private static IDictionary Cache(TextOverlayRenderer renderer) => Field<IDictionary>(renderer, "_overlayCache");
    private static CanvasTextFormat Format(object entry) => Field<CanvasTextFormat>(entry, "DrawFormat");

    private static void AssertClosed(Action read)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return; }
        Assert.Fail("The native overlay resource was not closed.");
    }

    private sealed class CountedOverlays(List<TextOverlaySegment> items) : IReadOnlyList<TextOverlaySegment>
    {
        public int IndexReads { get; private set; }
        public int Enumerations { get; private set; }
        public int Count => items.Count;
        public TextOverlaySegment this[int index] { get { IndexReads++; return items[index]; } }
        public IEnumerator<TextOverlaySegment> GetEnumerator() { Enumerations++; return items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void ResetCounts() { IndexReads = 0; Enumerations = 0; }
    }
}
