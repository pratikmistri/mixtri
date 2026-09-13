using Microsoft.Graphics.Canvas;
using Mixtri.Core.Diagnostics;
using System.Runtime.InteropServices;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
[DoNotParallelize]
public sealed class IdleGraphicsDeviceTests
{
    [TestMethod]
    public void SharedDevice_CanBeReleasedBetweenEditorSessions()
    {
        var oldDevice = CanvasDevice.GetSharedDevice();
        using (var frame = new CanvasRenderTarget(oldDevice, 32, 32, 96))
        using (var ds = frame.CreateDrawingSession())
            ds.Clear(Color.FromArgb(255, 20, 40, 60));

        oldDevice.Trim();
        oldDevice.Dispose();
        var replacement = CanvasDevice.GetSharedDevice();
        Assert.IsFalse(oldDevice == replacement);
        using var restored = new CanvasRenderTarget(replacement, 32, 32, 96);
        using (var ds = restored.CreateDrawingSession())
            ds.Clear(Color.FromArgb(255, 20, 40, 60));
        Assert.AreEqual(Color.FromArgb(255, 20, 40, 60), restored.GetPixelColors()[0]);
    }

    [TestMethod]
    public void Trim_PreservesAnInUseSurface()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var frame = new CanvasRenderTarget(device, 32, 32, 96);
        var color = Color.FromArgb(255, 20, 40, 60);
        using (var ds = frame.CreateDrawingSession()) ds.Clear(color);
        device.Trim();
        Assert.AreEqual(color, frame.GetPixelColors()[0]);
    }

    [TestMethod]
    public void NativeHeapReclamation_PreservesLiveAllocations()
    {
        var expected = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
        var allocation = Marshal.AllocHGlobal(expected.Length);
        try
        {
            Marshal.Copy(expected, 0, allocation, expected.Length);
            Assert.IsTrue(NativeHeapReclaimer.OptimizeUnusedHeaps());
            var actual = new byte[expected.Length];
            Marshal.Copy(allocation, actual, 0, actual.Length);
            CollectionAssert.AreEqual(expected, actual);
        }
        finally
        {
            Marshal.FreeHGlobal(allocation);
        }
    }
}
