using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;
using Mixtri.Core.Timeline;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public sealed class TextSlideBackgroundLoadingTests
{
    private static readonly Color Red = Color.FromArgb(255, 210, 30, 40);
    private static readonly Color Blue = Color.FromArgb(255, 20, 60, 220);

    private static TextSlideSegment ImageSlide(string path) => new()
    {
        BackgroundType = SlideBackgroundType.Image,
        BackgroundImagePath = path,
        BackgroundColor = "#224466",
        Duration = TimeSpan.FromSeconds(2),
    };

    private static string Placeholder(string root, string name)
    {
        string path = Path.Combine(root, name + ".png");
        File.WriteAllBytes(path, []);
        return path;
    }

    private static CanvasBitmap Bitmap(Color color)
        => CanvasBitmap.CreateFromColors(CanvasDevice.GetSharedDevice(), Enumerable.Repeat(color, 16).ToArray(), 4, 4);

    private static CanvasBitmap? CachedImage(TextSlideRenderer renderer)
        => (CanvasBitmap?)typeof(TextSlideRenderer)
            .GetField("_bgImage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer);

    private static void AssertDisposed(CanvasBitmap bitmap)
    {
        try
        {
            bitmap.GetPixelBytes();
            Assert.Fail("A discarded or replaced bitmap was not disposed.");
        }
        catch (ObjectDisposedException) { }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { }
    }

    private static void AssertBlueFrame(TextSlideRenderer renderer, TextSlideSegment slide)
    {
        using var frame = renderer.RenderSlide(slide, .25, 32, 24, drawText: false);
        Assert.AreEqual(Blue, frame.GetPixelColors()[12 * 32 + 16]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LatestRequestWinsRegardlessOfCompletionOrder(bool newestCompletesFirst)
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_loading_");
        var a = ImageSlide(Placeholder(directory.Path, "a"));
        var b = ImageSlide(Placeholder(directory.Path, "b"));
        using var red = Bitmap(Red);
        using var blue = Bitmap(Blue);
        var first = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        using var renderer = new TextSlideRenderer
        {
            ImageLoaderOverride = (_, path) =>
            {
                Interlocked.Increment(ref loads);
                return path == a.BackgroundImagePath ? first.Task : second.Task;
            },
        };
        var oldLoad = renderer.EnsureBackgroundLoadedAsync(a);
        var newLoad = renderer.EnsureBackgroundLoadedAsync(b);
        try
        {
            if (newestCompletesFirst)
            {
                second.SetResult(blue);
                await newLoad;
                first.SetResult(red);
                await oldLoad;
            }
            else
            {
                first.SetResult(red);
                await oldLoad;
                Assert.IsNull(CachedImage(renderer), "A superseded request must not publish while the latest load is pending.");
                second.SetResult(blue);
                await newLoad;
            }
            Assert.AreSame(blue, CachedImage(renderer));
            AssertDisposed(red);
            AssertBlueFrame(renderer, b);
            Assert.AreEqual(2, loads, "Rendering the latest background must reuse the correct cache.");
        }
        finally
        {
            first.TrySetResult(red);
            second.TrySetResult(blue);
            await Task.WhenAll(oldLoad, newLoad);
        }
    }

    [TestMethod]
    public async Task CachedSelectionSupersedesAnOlderDifferentPathLoad()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_cached_");
        var a = ImageSlide(Placeholder(directory.Path, "a"));
        var b = ImageSlide(Placeholder(directory.Path, "b"));
        using var red = Bitmap(Red);
        using var blue = Bitmap(Blue);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        using var renderer = new TextSlideRenderer
        {
            ImageLoaderOverride = (_, path) =>
            {
                Interlocked.Increment(ref loads);
                return path == a.BackgroundImagePath ? pending.Task : Task.FromResult(blue);
            },
        };
        await renderer.EnsureBackgroundLoadedAsync(b);
        var older = renderer.EnsureBackgroundLoadedAsync(a);
        await renderer.EnsureBackgroundLoadedAsync(b);
        pending.SetResult(red);
        await older;
        Assert.AreSame(blue, CachedImage(renderer));
        AssertDisposed(red);
        AssertBlueFrame(renderer, b);
        Assert.AreEqual(2, loads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonImageOrMissingSelectionRejectsAnOlderCompletion(bool missingImage)
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_cleared_");
        var a = ImageSlide(Placeholder(directory.Path, "a"));
        using var red = Bitmap(Red);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderer = new TextSlideRenderer { ImageLoaderOverride = (_, _) => pending.Task };
        var older = renderer.EnsureBackgroundLoadedAsync(a);
        await renderer.EnsureBackgroundLoadedAsync(missingImage
            ? ImageSlide(Path.Combine(directory.Path, "missing.png"))
            : new TextSlideSegment { BackgroundType = SlideBackgroundType.Gradient });
        pending.SetResult(red);
        await older;
        Assert.IsNull(CachedImage(renderer));
        AssertDisposed(red);
    }

    [TestMethod]
    public async Task SynchronousFallbackSupersedesAnAsyncLoad()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_sync_");
        var a = ImageSlide(Placeholder(directory.Path, "a"));
        var b = ImageSlide(Placeholder(directory.Path, "b"));
        using var red = Bitmap(Red);
        using var blue = Bitmap(Blue);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderer = new TextSlideRenderer
        {
            ImageLoaderOverride = (_, path) => path == a.BackgroundImagePath ? pending.Task : Task.FromResult(blue),
        };
        var older = renderer.EnsureBackgroundLoadedAsync(a);
        try { AssertBlueFrame(renderer, b); }
        finally
        {
            pending.TrySetResult(red);
            await older;
        }
        Assert.AreSame(blue, CachedImage(renderer));
        AssertDisposed(red);
    }

    [TestMethod]
    public async Task NewerLoadFailureDoesNotLetTheOlderRequestPublish()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_failed_");
        var a = ImageSlide(Placeholder(directory.Path, "a"));
        var b = ImageSlide(Placeholder(directory.Path, "b"));
        using var red = Bitmap(Red);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderer = new TextSlideRenderer
        {
            ImageLoaderOverride = (_, path) => path == a.BackgroundImagePath
                ? pending.Task : Task.FromException<CanvasBitmap>(new IOException("Image decode failed.")),
        };
        var older = renderer.EnsureBackgroundLoadedAsync(a);
        await renderer.EnsureBackgroundLoadedAsync(b);
        pending.SetResult(red);
        await older;
        Assert.IsNull(CachedImage(renderer));
        AssertDisposed(red);
    }

    [TestMethod]
    public async Task DisposeRejectsLatePublicationAndReleasesTheDecodedBitmap()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_dispose_");
        var slide = ImageSlide(Placeholder(directory.Path, "a"));
        using var red = Bitmap(Red);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderer = new TextSlideRenderer { ImageLoaderOverride = (_, _) => pending.Task };
        var load = renderer.EnsureBackgroundLoadedAsync(slide);
        renderer.Dispose();
        pending.SetResult(red);
        await load;
        Assert.IsNull(CachedImage(renderer));
        AssertDisposed(red);
        Assert.ThrowsException<ObjectDisposedException>(() => renderer.RenderSlide(slide, 0, 32, 24));
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => renderer.EnsureBackgroundLoadedAsync(slide));
    }

    [TestMethod]
    public async Task ChangedSlideObjectRejectsItsCapturedOldPath()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_changed_");
        var slide = ImageSlide(Placeholder(directory.Path, "a"));
        using var red = Bitmap(Red);
        var pending = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var renderer = new TextSlideRenderer { ImageLoaderOverride = (_, _) => pending.Task };
        var load = renderer.EnsureBackgroundLoadedAsync(slide);
        slide.BackgroundImagePath = Placeholder(directory.Path, "b");
        pending.SetResult(red);
        await load;
        Assert.IsNull(CachedImage(renderer));
        AssertDisposed(red);
    }

    [TestMethod]
    public async Task RealImagePreloadMatchesSynchronousRendering()
    {
        using var directory = new TempDirectoryFixture("mixtri_slide_real_");
        string path = Path.Combine(directory.Path, "image.png");
        using (var image = Bitmap(Blue)) await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
        var slide = ImageSlide(path);
        using var preloaded = new TextSlideRenderer();
        using var synchronous = new TextSlideRenderer();
        await preloaded.EnsureBackgroundLoadedAsync(slide);
        using var actual = preloaded.RenderSlide(slide, .4, 32, 24);
        using var expected = synchronous.RenderSlide(slide, .4, 32, 24);
        CollectionAssert.AreEqual(expected.GetPixelBytes(), actual.GetPixelBytes());
    }
}
