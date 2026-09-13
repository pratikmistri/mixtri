using System.Reflection;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.Foundation;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class CursorClickBufferTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Rect Viewport = new(7.25, 11.5, 640, 360);
    private const float ScaleX = .75f, ScaleY = 1.125f;
    private const double MouseOffset = .13791;
    private static readonly Color ClearColor = Color.FromArgb(255, 20, 30, 40);

    [TestMethod]
    public void WindowAndTransformMatchPreviousEventsWithoutChangingRecording()
    {
        var data = Recording();
        var originals = data.Clicks.ToArray();
        using var compositor = Build(data, out var select);
        List<CursorClick>? previousBuffer = null;
        foreach (double time in new[] { 1.5, 2.4, -.5, 4.0, .13791, 1.5 })
        {
            var expected = PreviousSelection(data, time);
            var actual = select(time, Viewport, ScaleX, ScaleY);
            CollectionAssert.AreEqual(expected.Select(ToValue).ToArray(), actual.ToArray());
            if (previousBuffer is not null) Assert.AreSame(previousBuffer, actual);
            previousBuffer = actual;
        }
        for (int i = 0; i < originals.Length; i++) Assert.AreSame(originals[i], data.Clicks[i]);
    }

    [TestMethod]
    public void EmptySelectionClearsReusedValuesAndDisposalReleasesCapacity()
    {
        var data = Recording();
        var compositor = Build(data, out var select);
        try
        {
            var buffer = select(1.5, Viewport, ScaleX, ScaleY);
            Assert.IsTrue(buffer.Count > 0);
            data.Clicks.Clear();
            Assert.AreSame(buffer, select(1.5, Viewport, ScaleX, ScaleY));
            Assert.AreEqual(0, buffer.Count);
            Set(compositor, "_mouseData", null);
            Assert.AreSame(buffer, select(1.5, Viewport, ScaleX, ScaleY));
            compositor.Dispose();
            Assert.AreEqual(0, buffer.Capacity);
        }
        finally { compositor.Dispose(); }
    }

    [TestMethod]
    public void WarmClickSelectionAllocatesNoManagedMemory()
    {
        var data = Recording();
        using var compositor = Build(data, out var select);
        for (int i = 0; i < 1000; i++) select(1.5, Viewport, ScaleX, ScaleY);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        for (int i = 0; i < 3000; i++) count += select(1.5, Viewport, ScaleX, ScaleY).Count;
        long currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        for (int i = 0; i < 100; i++) PreviousSelection(data, 1.5);
        before = GC.GetAllocatedBytesForCurrentThread();
        int previousCount = 0;
        for (int i = 0; i < 3000; i++) previousCount += PreviousSelection(data, 1.5).Count;
        long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(previousCount, count);
        Assert.AreEqual(0L, currentBytes);
        Assert.IsTrue(previousBytes > 1_000_000, $"Reference allocation was only {previousBytes} bytes.");
        Console.WriteLine($"3,000 frame selections: {previousBytes:N0} previous bytes, {currentBytes:N0} reused-buffer bytes.");
    }

    [TestMethod]
    [DataRow(CursorType.Default, false)]
    [DataRow(CursorType.Default, true)]
    [DataRow(CursorType.System, false)]
    [DataRow(CursorType.Touch, false)]
    [DataRow(CursorType.Hidden, true)]
    public async Task TransformedValuesPreserveRenderedPixels(CursorType type, bool shutter)
    {
        var device = CanvasDevice.GetSharedDevice();
        var style = new CursorStyle
        {
            Type = type, Scale = 1, AutoHideEnabled = false,
            MotionBlurEnabled = !shutter, ClickAnimationEnabled = true,
        };
        using var recordedRenderer = new CursorRenderer(style) { TickFrequency = 1000 };
        using var valueRenderer = new CursorRenderer(style) { TickFrequency = 1000 };
        await recordedRenderer.LoadCursorAsync(device);
        await valueRenderer.LoadCursorAsync(device);
        List<ClickEvent> events =
        [
            new(1000, 80, 80, MouseButton.Left, true),
            new(1060, 80, 80, MouseButton.Left, false),
            new(1300, 150, 95, MouseButton.Right, true),
            new(1360, 150, 95, MouseButton.Right, false),
            new(3000, 120, 100, MouseButton.Left, true),
        ];
        var values = events.Select(ToValue).ToList();
        var position = new SmoothedPosition { X = 110, Y = 90, VelocityX = 800, VelocityY = 300, Shape = CursorShape.Hand };
        var motion = shutter ? new MotionBlurSettings { Enabled = true, CursorStrength = 1 } : null;
        using var expected = new CanvasRenderTarget(device, 240, 180, 96);
        using var actual = new CanvasRenderTarget(device, 240, 180, 96);

        foreach (double time in new[] { .3, .8, 1.0, 1.02, 1.08, 1.15, 1.3, 1.55, 2.4, 3.0, 4.0, .8 })
        {
            using (var ds = expected.CreateDrawingSession())
            {
                ds.Clear(ClearColor);
                recordedRenderer.RenderFrame(ds, position, events, time, time, motion);
            }
            using (var ds = actual.CreateDrawingSession())
            {
                ds.Clear(ClearColor);
                valueRenderer.RenderTransformedFrame(ds, position, values, time, time, motion);
            }
            CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes(), $"Time {time}");
        }
    }

    [TestMethod]
    public async Task TouchChainScratchIsReusedAndCleared()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var renderer = new CursorRenderer(new CursorStyle { Type = CursorType.Touch }) { TickFrequency = 1000 };
        await renderer.LoadCursorAsync(device);
        using var target = new CanvasRenderTarget(device, 240, 180, 96);
        List<CursorClick> clicks = [new(1000, 80, 80, MouseButton.Left, true), new(1250, 120, 90, MouseButton.Left, true)];
        var field = typeof(CursorRenderer).GetField("_touchClicks", Fields)!;
        var scratch = (List<(CursorClick, double)>)field.GetValue(renderer)!;
        for (int frame = 0; frame < 30; frame++)
        {
            using var ds = target.CreateDrawingSession();
            ds.Clear(ClearColor);
            renderer.RenderTransformedFrame(ds, default, clicks, 1.0 + frame / 60.0, 1.0);
            Assert.AreSame(scratch, field.GetValue(renderer));
            Assert.AreEqual(2, scratch.Count);
        }
        clicks.Clear();
        using (var ds = target.CreateDrawingSession())
            renderer.RenderTransformedFrame(ds, default, clicks, 2, 2);
        Assert.AreEqual(0, scratch.Count);
        renderer.Dispose();
        Assert.AreEqual(0, scratch.Capacity);
    }

    [TestMethod]
    public async Task ComposedFramesUseTheSameClickBufferForLiveTouchRendering()
    {
        var data = TestMouseRecordingBuilder.WithPositions(180, 60, i => (80 + i * .2, 80));
        var down = new ClickEvent(data.StartTimestampTicks + (long)data.TickFrequency, 100, 90, MouseButton.Left, true);
        data.Clicks.Add(down);
        data.Clicks.Add(new(data.StartTimestampTicks + (long)(1.2 * data.TickFrequency), 100, 90, MouseButton.Left, false));
        using var compositor = new FrameCompositor(new CompositionConfig
        {
            OutputFps = 30,
            Cursor = new CursorStyle { Type = CursorType.Touch, Scale = 1 },
            Zoom = new AutoZoomConfig { Enabled = false },
            MotionBlur = new MotionBlurSettings { Enabled = false },
            Background = new BackgroundStyle
            {
                Type = BackgroundType.SolidColor, Color = "#141e28",
                Padding = 0, CornerRadius = 0, ShadowEnabled = false,
            },
        });
        await compositor.InitializeAsync(data, 240, 180, TimeSpan.FromSeconds(3));
        var field = typeof(FrameCompositor).GetField("_activeClicks", Fields)!;
        var buffer = (List<CursorClick>)field.GetValue(compositor)!;
        using var source = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 180, 96);
        using (var ds = source.CreateDrawingSession()) ds.Clear(ClearColor);
        var sourcePixels = source.GetPixelBytes();
        bool drewTouch = false;
        for (int frame = 15; frame < 45; frame++)
        {
            using var output = compositor.ComposeFrame(source, frame);
            Assert.AreSame(buffer, field.GetValue(compositor));
            Assert.AreEqual(2, buffer.Count);
            drewTouch |= !sourcePixels.SequenceEqual(output.GetPixelBytes());
        }
        Assert.IsTrue(drewTouch, "The integration check must draw the touch animation, not just clear a frame.");
        Assert.AreSame(down, data.Clicks[0]);
        compositor.Dispose();
        Assert.AreEqual(0, buffer.Capacity);
    }

    private static FrameCompositor Build(MouseRecordingData data,
        out Func<double, Rect, float, float, List<CursorClick>> select)
    {
        var compositor = new FrameCompositor(new CompositionConfig());
        Set(compositor, "_mouseData", data);
        Set(compositor, "_mouseTimeOffset", MouseOffset);
        Set(compositor, "_coordScaleX", 1.25f);
        Set(compositor, "_coordScaleY", 1.5f);
        Set(compositor, "_cropOffsetX", 100.5f);
        Set(compositor, "_cropOffsetY", 60.25f);
        Set(compositor, "_sourceAreaOffsetX", 48);
        Set(compositor, "_sourceAreaOffsetY", 72);
        Set(compositor, "_clickDisplacements", Enumerable.Range(0, data.Clicks.Count)
            .Select(i => (X: i * .5, Y: -i * .25)).ToArray());
        Set(compositor, "_disabledClickTicks", new HashSet<long> { data.Clicks[4].TimestampTicks });
        select = typeof(FrameCompositor).GetMethod("GetActiveClicks", Fields)!
            .CreateDelegate<Func<double, Rect, float, float, List<CursorClick>>>(compositor);
        return compositor;
    }

    private static MouseRecordingData Recording() => new()
    {
        StartTimestampTicks = 50_000, TickFrequency = 10_000,
        Clicks = Enumerable.Range(0, 60)
            .Select(i => new ClickEvent(50_000 + i * 500, 130 + i, 90 + i, MouseButton.Left, i % 2 == 0)).ToList(),
    };

    private static List<ClickEvent> PreviousSelection(MouseRecordingData data, double time)
    {
        var result = new List<ClickEvent>();
        long firstTick = data.StartTimestampTicks + (long)((time - 1.5 + MouseOffset) * data.TickFrequency);
        long lastTick = data.StartTimestampTicks + (long)((time + 1.5 + MouseOffset) * data.TickFrequency);
        int lo = 0, hi = data.Clicks.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (data.Clicks[mid].TimestampTicks < firstTick) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < data.Clicks.Count; i++)
        {
            var click = data.Clicks[i];
            if (click.TimestampTicks > lastTick) break;
            if (click.TimestampTicks == data.Clicks[4].TimestampTicks) continue;
            int x = (int)((click.X * 1.25f - 100.5f + i * .5 - Viewport.X) * ScaleX + 48);
            int y = (int)((click.Y * 1.5f - 60.25f - i * .25 - Viewport.Y) * ScaleY + 72);
            result.Add(new(click.TimestampTicks - (long)(MouseOffset * data.TickFrequency), x, y, click.Button, click.IsDown));
        }
        return result;
    }

    private static CursorClick ToValue(ClickEvent click) =>
        new(click.TimestampTicks, click.X, click.Y, click.Button, click.IsDown);

    private static void Set(object target, string name, object? value) =>
        typeof(FrameCompositor).GetField(name, Fields)!.SetValue(target, value);
}
