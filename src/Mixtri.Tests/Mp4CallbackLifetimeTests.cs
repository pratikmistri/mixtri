using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;
using Windows.Media.Playback;

namespace Mixtri.Tests;

[TestClass]
public sealed class Mp4CallbackLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public async Task RepeatedFrameCopiesTheKnownSurfaceWithoutAnotherDecoderCommand()
    {
        using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
        using var source = (Mp4FrameSource)Activator.CreateInstance(
            typeof(Mp4FrameSource), Private, null,
            [player, CanvasDevice.GetSharedDevice(), 96, 64, 10, 4, false, FrameSourceOptions.FullResolution],
            null)!;
        var surface = Field<CanvasRenderTarget>(source, "_surface");
        using (var drawing = surface.CreateDrawingSession())
            drawing.Clear(Windows.UI.Color.FromArgb(255, 200, 30, 50));
        typeof(Mp4FrameSource).GetField("_lastDeliveredIndex", Private)!.SetValue(source, 0);

        // This player has no source; issuing a new seek cannot produce a frame.
        using var frame = await source.LoadFrameAsync(0);
        Assert.IsNotNull(frame);
        CollectionAssert.AreEqual(surface.GetPixelBytes(), frame.GetPixelBytes());
    }

    [TestMethod]
    public async Task DisposalRetainsTheSurfaceUntilAnAdmittedNativeCallbackExits()
    {
        using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
        using var source = (Mp4FrameSource)Activator.CreateInstance(
            typeof(Mp4FrameSource), Private, null,
            [player, CanvasDevice.GetSharedDevice(), 96, 64, 10, 4, false, FrameSourceOptions.FullResolution],
            null)!;
        var surface = Field<CanvasRenderTarget>(source, "_surface");
        var surfaceLock = Field<object>(source, "_surfaceLock");
        var callback = typeof(Mp4FrameSource).GetMethod("OnVideoFrameAvailable", Private)!;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(Mp4FrameSource).GetField("_framePending", Private)!.SetValue(source, pending);
        typeof(Mp4FrameSource).GetField("_activeFrameRequest", Private)!.SetValue(source, pending);
        Task? nativeCallback = null;
        try
        {
            await Task.Factory.StartNew(() =>
            {
                lock (surfaceLock)
                {
                    nativeCallback = Task.Factory.StartNew(
                        () => callback.Invoke(source, [player, new object()]),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Assert.IsTrue(SpinWait.SpinUntil(
                        () => Field<object?>(source, "_framePending") is null, TimeSpan.FromSeconds(5)));
                    source.Dispose();
                    Assert.IsTrue(surface.GetPixelBytes().Length > 0,
                        "A callback has already taken the request and still owns the surface.");
                    Assert.IsTrue(pending.Task.IsCompletedSuccessfully);
                    Assert.IsFalse(pending.Task.Result);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            if (nativeCallback is not null)
                await nativeCallback.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.IsTrue(IsClosed(surface), "The last callback must release the retired native resources.");
        callback.Invoke(source, [player, new object()]);
    }

    private static T Field<T>(object owner, string name) =>
        (T)typeof(Mp4FrameSource).GetField(name, Private)!.GetValue(owner)!;

    private static bool IsClosed(CanvasBitmap bitmap)
    {
        try { bitmap.GetPixelBytes(); return false; }
        catch (ObjectDisposedException) { return true; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013)) { return true; }
    }
}
