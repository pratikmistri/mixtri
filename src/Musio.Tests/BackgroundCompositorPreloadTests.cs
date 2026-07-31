namespace Musio.Tests;

using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Models;
using Musio.Core.Processing;
using Color = Windows.UI.Color;

/// <summary>
/// PERF-002: the frame-composition path must never block on file I/O or GPU decode for
/// image backgrounds. The wallpaper is warmed by <see cref="BackgroundCompositor.PreloadAsync"/>
/// (keyed by path + <see cref="CanvasDevice"/>); rendering falls back to the solid colour
/// whenever the bitmap is absent, not yet ready, or failed to load.
/// </summary>
[TestClass]
public sealed class BackgroundCompositorPreloadTests
{
    private const string SolidHex = "#0000FF"; // blue fallback
    private static readonly Color ImageColor = Color.FromArgb(255, 255, 0, 0); // red wallpaper
    private static readonly Color ReplacementColor = Color.FromArgb(255, 0, 255, 0);

    private static string TempDir =>
        Path.Combine(AppContext.BaseDirectory, "BackgroundCompositorPreloadTests");

    [ClassCleanup]
    public static void CleanupTempFiles()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static CanvasDevice? TryCreateDevice()
    {
        try
        {
            return new CanvasDevice(forceSoftwareRenderer: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> CreateWallpaperFileAsync(CanvasDevice device)
    {
        Directory.CreateDirectory(TempDir);
        string path = Path.Combine(TempDir, $"wallpaper_{Guid.NewGuid():N}.png");

        using var rt = new CanvasRenderTarget(device, 32, 32, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(ImageColor);
        }

        await rt.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
        return path;
    }

    private static Color RenderCenterPixel(
        CanvasDevice device, BackgroundCompositor compositor, BackgroundStyle style,
        out TimeSpan elapsed)
    {
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        var sw = Stopwatch.StartNew();
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 0, 0, 0));
            compositor.FillBackgroundRect(ds, null, 0, 0, 64, 64, style);
        }
        sw.Stop();
        elapsed = sw.Elapsed;

        var pixels = target.GetPixelColors(32, 32, 1, 1);
        return pixels[0];
    }

    private static BackgroundStyle ImageStyle(string? path) => new()
    {
        Type = BackgroundType.Image,
        BackgroundImagePath = path,
        Color = SolidHex,
    };

    [TestMethod]
    public async Task Render_WithoutPreload_UsesSolidFallback_AndDoesNotBlock()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            string path = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();
            var style = ImageStyle(path);

            var color = RenderCenterPixel(device, compositor, style, out var elapsed);

            Assert.AreEqual(ColorHelper.ParseColor(SolidHex), color,
                "A cold cache must render the solid-colour fallback, not the wallpaper.");
            Assert.IsTrue(elapsed < TimeSpan.FromMilliseconds(250),
                $"Rendering must not block on image load (took {elapsed.TotalMilliseconds:F1} ms).");
        }
    }

    [TestMethod]
    public async Task PreloadAsync_ThenRender_DrawsTheWallpaper()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            string path = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();
            var style = ImageStyle(path);

            await compositor.PreloadAsync(device, style);

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            var color = RenderCenterPixel(device, compositor, style, out _);
            Assert.AreEqual(ImageColor, color, "The preloaded wallpaper should be drawn.");
        }
    }

    [TestMethod]
    public async Task PreloadAsync_MissingFile_DoesNotThrow_AndRenderFallsBack()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            using var compositor = new BackgroundCompositor();
            var style = ImageStyle(Path.Combine(TempDir, "does_not_exist.png"));

            await compositor.PreloadAsync(device, style);

            Assert.IsFalse(compositor.IsBackgroundImageReady(device, style));
            var color = RenderCenterPixel(device, compositor, style, out _);
            Assert.AreEqual(ColorHelper.ParseColor(SolidHex), color);
        }
    }

    [TestMethod]
    public async Task PreloadAsync_NonImageStyle_IsReady_AndReleasesCachedImage()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            string path = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();
            var imageStyle = ImageStyle(path);

            await compositor.PreloadAsync(device, imageStyle);
            Assert.IsTrue(compositor.IsBackgroundImageReady(device, imageStyle));

            // Switching to a non-image style deterministically drops the cached bitmap.
            var solidStyle = new BackgroundStyle { Type = BackgroundType.SolidColor, Color = SolidHex };
            await compositor.PreloadAsync(device, solidStyle);

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, solidStyle));
            Assert.IsFalse(compositor.IsBackgroundImageReady(device, imageStyle),
                "The wallpaper cache should be invalidated when the style stops using an image.");
        }
    }

    [TestMethod]
    public async Task PreloadAsync_PathChange_InvalidatesPreviousEntry()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            string first = await CreateWallpaperFileAsync(device);
            string second = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();

            await compositor.PreloadAsync(device, ImageStyle(first));
            await compositor.PreloadAsync(device, ImageStyle(second));

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, ImageStyle(second)));
            Assert.IsFalse(compositor.IsBackgroundImageReady(device, ImageStyle(first)),
                "Only the most recently preloaded path stays cached.");
        }
    }

    [TestMethod]
    public async Task PreloadAsync_CacheIsDeviceScoped()
    {
        var device = TryCreateDevice();
        var otherDevice = TryCreateDevice();
        if (device is null || otherDevice is null)
        {
            device?.Dispose();
            otherDevice?.Dispose();
            Assert.Inconclusive("Win2D CanvasDevice unavailable.");
            return;
        }

        using (device)
        using (otherDevice)
        {
            string path = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();
            var style = ImageStyle(path);

            await compositor.PreloadAsync(device, style);

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            Assert.IsFalse(compositor.IsBackgroundImageReady(otherDevice, style),
                "A bitmap loaded on one CanvasDevice must not be reused on another.");
        }
    }

    [TestMethod]
    public async Task InvalidateImageCache_DropsWarmedImage()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            string path = await CreateWallpaperFileAsync(device);
            using var compositor = new BackgroundCompositor();
            var style = ImageStyle(path);

            await compositor.PreloadAsync(device, style);
            compositor.InvalidateImageCache();

            Assert.IsFalse(compositor.IsBackgroundImageReady(device, style));
            var color = RenderCenterPixel(device, compositor, style, out _);
            Assert.AreEqual(ColorHelper.ParseColor(SolidHex), color);
        }
    }

    [TestMethod]
    public async Task InvalidateImageCache_DiscardsInflightLoadWhenItCompletes()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var compositor = new BackgroundCompositor();
            compositor.ImageLoaderOverride = async (dev, _, _) =>
            {
                started.SetResult();
                await release.Task.ConfigureAwait(false);
                return RedPixel(dev);
            };

            var style = ImageStyle(FakePath);
            var preload = compositor.PreloadAsync(device, style);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            compositor.InvalidateImageCache();
            release.SetResult();
            await preload;

            Assert.IsFalse(compositor.IsBackgroundImageReady(device, style),
                "A load completing after invalidation must not repopulate the cache.");
        }
    }

    [TestMethod]
    public async Task InvalidateImageCache_OldSameKeyLoadCannotReplaceNewLoad()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            int calls = 0;
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var compositor = new BackgroundCompositor();
            compositor.ImageLoaderOverride = async (dev, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.ConfigureAwait(false);
                    return RedPixel(dev);
                }

                secondStarted.SetResult();
                await releaseSecond.Task.ConfigureAwait(false);
                return CanvasBitmap.CreateFromColors(dev, [ReplacementColor], 1, 1);
            };

            var style = ImageStyle(FakePath);
            var firstPreload = compositor.PreloadAsync(device, style);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            compositor.InvalidateImageCache();
            var secondPreload = compositor.PreloadAsync(device, style);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFirst.SetResult();
            await firstPreload;
            releaseSecond.SetResult();
            await secondPreload;

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            Assert.AreEqual(
                ReplacementColor,
                RenderCenterPixel(device, compositor, style, out _),
                "The completion orphaned by invalidation must not publish over the replacement load.");
        }
    }

    [TestMethod]
    public async Task PreloadAsync_AfterDispose_Throws()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            var compositor = new BackgroundCompositor();
            compositor.Dispose();

            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
                () => compositor.PreloadAsync(device, ImageStyle("ignored.png")));
        }
    }

    // ---------------------------------------------------------------------------------
    // Cancellation and failure recovery.
    //
    // These use the internal loader seam so the transient/cancelled/failing outcomes are
    // deterministic instead of depending on file-system or GPU timing. The retry backoff
    // is compressed for the same reason.
    // ---------------------------------------------------------------------------------

    private const string FakePath = @"C:\wallpapers\fake.png";

    private static CanvasBitmap RedPixel(CanvasDevice device) =>
        CanvasBitmap.CreateFromColors(device, [ImageColor], 1, 1);

    private static BackgroundCompositor CreateFastRetryCompositor(
        double baseMs = 40, double maxMs = 80) =>
        new()
        {
            RetryBaseDelay = TimeSpan.FromMilliseconds(baseMs),
            RetryMaxDelay = TimeSpan.FromMilliseconds(maxMs),
        };

    /// <summary>
    /// Drives the render path (which is what asks for a warm) until the wallpaper shows
    /// up, or the timeout elapses. Returns the last rendered centre pixel.
    /// </summary>
    private static Color RenderUntilReady(
        CanvasDevice device, BackgroundCompositor compositor, BackgroundStyle style,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Color color;
        do
        {
            color = RenderCenterPixel(device, compositor, style, out _);
            if (color == ImageColor) return color;
            Thread.Sleep(15);
        }
        while (DateTime.UtcNow < deadline);

        return color;
    }

    [TestMethod]
    public async Task Cancellation_DoesNotLatchFailure_AndTheKeyStaysLoadable()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            int calls = 0;
            using var compositor = CreateFastRetryCompositor();
            compositor.ImageLoaderOverride = (dev, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new OperationCanceledException();
                return Task.FromResult<CanvasBitmap?>(RedPixel(dev));
            };

            var style = ImageStyle(FakePath);
            await compositor.PreloadAsync(device, style);

            Assert.IsFalse(compositor.IsBackgroundImageReady(device, style));
            Assert.IsNull(compositor.LastImageLoadFailure,
                "A cancelled load must not be recorded as a failure.");

            // No backoff applies, so the very next frame is allowed to warm again.
            var color = RenderUntilReady(device, compositor, style, TimeSpan.FromSeconds(5));

            Assert.AreEqual(ImageColor, color,
                "Cancellation must not poison the key — a later load has to succeed.");
            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            Assert.IsNull(compositor.LastImageLoadFailure);
        }
    }

    [TestMethod]
    public async Task Cancellation_OfOneCaller_DoesNotAbortTheSharedLoad()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var compositor = CreateFastRetryCompositor();
            compositor.ImageLoaderOverride = async (dev, _, _) =>
            {
                await gate.Task.ConfigureAwait(false);
                return RedPixel(dev);
            };

            var style = ImageStyle(FakePath);
            using var cts = new CancellationTokenSource();
            var canceledCaller = compositor.PreloadAsync(device, style, cts.Token);
            var patientCaller = compositor.PreloadAsync(device, style);

            cts.Cancel();
            try
            {
                await canceledCaller;
                Assert.Fail("The cancelled caller should stop waiting on the shared load.");
            }
            catch (OperationCanceledException)
            {
                // Expected: only this caller's wait is cancelled.
            }

            gate.SetResult();
            await patientCaller;

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style),
                "One caller cancelling must not abort the shared load for the others.");
            Assert.IsNull(compositor.LastImageLoadFailure);
        }
    }

    [TestMethod]
    public void RenderPath_RetriesAfterTransientFailure_ButNotEveryFrame()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            int calls = 0;
            var failNext = true;
            using var compositor = CreateFastRetryCompositor(baseMs: 1000, maxMs: 1000);
            compositor.ImageLoaderOverride = (dev, _, _) =>
            {
                Interlocked.Increment(ref calls);
                if (Volatile.Read(ref failNext))
                    throw new IOException("file is locked by another process");
                return Task.FromResult<CanvasBitmap?>(RedPixel(dev));
            };

            var style = ImageStyle(FakePath);

            // First frame warms, fails, and latches a backoff.
            var color = RenderCenterPixel(device, compositor, style, out _);
            Assert.AreEqual(ColorHelper.ParseColor(SolidHex), color);
            SpinWait.SpinUntil(() => compositor.LastImageLoadFailure is not null, 2000);

            var failure = compositor.LastImageLoadFailure;
            Assert.IsNotNull(failure, "A transient failure must be surfaced, not swallowed.");
            Assert.AreEqual(1, failure.Attempts);
            Assert.IsTrue(failure.WillRetry);
            Assert.IsTrue(failure.Reason.Contains("IOException", StringComparison.Ordinal));

            // Frames drawn inside the backoff window must not re-request the load.
            for (int i = 0; i < 30; i++)
                RenderCenterPixel(device, compositor, style, out _);

            Assert.AreEqual(1, Volatile.Read(ref calls),
                "The render path must not retry a failed load on every frame.");

            // Once the transient condition clears and the backoff elapses, a later frame
            // recovers on its own.
            Volatile.Write(ref failNext, false);
            Thread.Sleep(1100);

            var recovered = RenderUntilReady(device, compositor, style, TimeSpan.FromSeconds(5));

            Assert.AreEqual(ImageColor, recovered,
                "A transient failure must be retryable — the wallpaper should recover.");
            Assert.IsNull(compositor.LastImageLoadFailure,
                "A successful load clears the recorded failure.");
            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
        }
    }

    [TestMethod]
    public async Task RenderPathRetries_AreBounded_AndPreloadResumesThem()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            int calls = 0;
            var failNext = true;
            using var compositor = CreateFastRetryCompositor();
            compositor.ImageLoaderOverride = (dev, _, _) =>
            {
                Interlocked.Increment(ref calls);
                if (Volatile.Read(ref failNext))
                    throw new IOException("still unreadable");
                return Task.FromResult<CanvasBitmap?>(RedPixel(dev));
            };

            var style = ImageStyle(FakePath);

            // Render for well over the whole backoff schedule.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                RenderCenterPixel(device, compositor, style, out _);
                Thread.Sleep(10);
            }

            Assert.AreEqual(BackgroundCompositor.MaxLoadAttempts, Volatile.Read(ref calls),
                "The render path must stop retrying once the attempt budget is spent.");

            var failure = compositor.LastImageLoadFailure;
            Assert.IsNotNull(failure);
            Assert.IsFalse(failure.WillRetry, "The exhausted key must report that it gave up.");

            // An explicit preload is an explicit retry, and a later success recovers the
            // same path/device key.
            Volatile.Write(ref failNext, false);
            await compositor.PreloadAsync(device, style);

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            Assert.IsNull(compositor.LastImageLoadFailure);
            Assert.AreEqual(ImageColor, RenderCenterPixel(device, compositor, style, out _));
        }
    }

    [TestMethod]
    public async Task PreloadAsync_FailThenSucceed_RecoversForTheSameKey()
    {
        var device = TryCreateDevice();
        if (device is null) { Assert.Inconclusive("Win2D CanvasDevice unavailable."); return; }

        using (device)
        {
            using var compositor = new BackgroundCompositor();
            string path = Path.Combine(TempDir, $"late_{Guid.NewGuid():N}.png");
            var style = ImageStyle(path);

            // The file does not exist yet: preload fails and the render path falls back.
            await compositor.PreloadAsync(device, style);
            Assert.IsFalse(compositor.IsBackgroundImageReady(device, style));
            Assert.IsNotNull(compositor.LastImageLoadFailure);
            Assert.AreEqual(ColorHelper.ParseColor(SolidHex),
                RenderCenterPixel(device, compositor, style, out _));

            // Same path, same device, now readable — a later preload must recover it.
            Directory.CreateDirectory(TempDir);
            using (var rt = new CanvasRenderTarget(device, 32, 32, 96))
            {
                using (var ds = rt.CreateDrawingSession()) ds.Clear(ImageColor);
                await rt.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
            }

            await compositor.PreloadAsync(device, style);

            Assert.IsTrue(compositor.IsBackgroundImageReady(device, style));
            Assert.IsNull(compositor.LastImageLoadFailure);
            Assert.AreEqual(ImageColor, RenderCenterPixel(device, compositor, style, out _));
        }
    }
}
