using Microsoft.Graphics.Canvas;
using Musio.Core.Processing;

namespace Musio.Tests;

/// <summary>
/// Guards <see cref="GrowOnlyBuffer"/> against spurious reallocation.
/// </summary>
/// <remarks>
/// The buffer decides whether to reuse its cached render target partly by comparing the
/// <see cref="CanvasDevice"/> it was last built against. An earlier revision compared with
/// <see cref="object.ReferenceEquals"/>, which is wrong: reading the device back — e.g. via
/// <c>CanvasDrawingSession.Device</c>, which is how <c>CursorRenderer</c> obtains it — can
/// hand out a different projection wrapper around the same underlying device. That makes the
/// check report a change on every call and reallocates a full-size render target every frame:
/// output stays pixel-perfect, so no correctness test notices, while GPU memory churns until
/// rendering degrades under sustained load.
/// These tests therefore assert reuse behaviour, not object identity.
/// </remarks>
[TestClass]
public class GrowOnlyBufferDeviceIdentityTests
{
    private static CanvasDevice? TryGetDevice()
    {
        try { return CanvasDevice.GetSharedDevice(); }
        catch { return null; }
    }

    [TestMethod]
    public void RepeatedFrames_AtAConstantSize_AllocateOnlyOnce()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using var rt = new CanvasRenderTarget(device, 64, 64, 96);
        using var buffer = new GrowOnlyBuffer();

        CanvasRenderTarget? first = null;
        int reallocations = 0;

        // Mirrors CursorRenderer's shutter-blur path, which takes the device off a freshly
        // created drawing session on every frame.
        for (int i = 0; i < 20; i++)
        {
            using var ds = rt.CreateDrawingSession();
            Assert.IsTrue(
                buffer.TryEnsureAtLeast(ds.Device, 128, 128, "test scratch", out var target),
                "scratch allocation failed");

            if (first is null) first = target;
            else if (!ReferenceEquals(first, target)) reallocations++;
        }

        Assert.AreEqual(0, reallocations,
            $"GrowOnlyBuffer reallocated {reallocations} times over 20 constant-size frames. " +
            "It must reuse the cached target — a per-frame reallocation of a full-size render " +
            "target still renders correctly but exhausts GPU memory under load.");
    }

    [TestMethod]
    public void DeviceReadBackFromADrawingSession_IsTreatedAsTheSameDevice()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using var rt = new CanvasRenderTarget(device, 64, 64, 96);
        using var buffer = new GrowOnlyBuffer();

        // Seed using the device object directly...
        var seeded = buffer.Ensure(device, 200, 200, "test");

        // ...then request the same size using the device as read back off a drawing session.
        CanvasRenderTarget viaSession;
        using (var ds = rt.CreateDrawingSession())
            viaSession = buffer.Ensure(ds.Device, 200, 200, "test");

        Assert.IsTrue(ReferenceEquals(seeded, viaSession),
            "the device read back from a drawing session must compare equal to the device it " +
            "was created from, otherwise every frame reallocates.");
    }
}
