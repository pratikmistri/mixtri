using Microsoft.Graphics.Canvas;
using Mixtri.Core.Processing;

namespace Mixtri.Tests;

/// <summary>
/// Pins the replacement-ordering contract of <see cref="GrowOnlyBuffer"/>.
/// </summary>
/// <remarks>
/// The call sites this type replaced did not agree on whether to free the old render target
/// before or after allocating its replacement, and that difference decides whether two
/// full-resolution buffers are alive simultaneously. Unifying them on allocate-then-dispose
/// doubled peak GPU memory for the compositor's composite/crop buffers — which resize
/// whenever preview resolution, zoom or aspect ratio changes, i.e. constantly while
/// scrubbing — and corrupted preview rendering under sustained load.
/// These tests exist because that failure mode is invisible to output-correctness testing:
/// the frames a leaking-peak buffer produces are perfectly correct right up until the GPU
/// runs out of memory.
/// </remarks>
[TestClass]
public class GrowOnlyBufferReplacePolicyTests
{
    private static CanvasDevice? TryGetDevice()
    {
        try { return CanvasDevice.GetSharedDevice(); }
        catch { return null; }
    }

    [TestMethod]
    public void DefaultPolicy_FreesTheOldTargetBeforeAllocatingTheReplacement()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using var buffer = new GrowOnlyBuffer();

        var first = buffer.Ensure(device, 256, 256, "test");
        Assert.IsNotNull(first);

        // Resizing must retire the previous target. Reaching into it after the swap is the
        // only way to observe that it was disposed rather than left alive alongside the new one.
        var second = buffer.Ensure(device, 512, 512, "test");
        Assert.IsFalse(ReferenceEquals(first, second), "resize should have allocated a new target");

        Assert.ThrowsException<ObjectDisposedException>(
            () => _ = first.SizeInPixels,
            "the previous target must be disposed during the swap, not kept alive alongside " +
            "the replacement — holding both doubles peak GPU memory on every resize.");
    }

    [TestMethod]
    public void AllocateThenDisposePolicy_KeepsTheOldTargetUntilTheReplacementExists()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using var buffer = new GrowOnlyBuffer(BufferReplacePolicy.AllocateThenDispose);

        var first = buffer.Ensure(device, 64, 64, "test");
        var second = buffer.Ensure(device, 128, 128, "test");

        Assert.IsFalse(ReferenceEquals(first, second), "resize should have allocated a new target");
        // The old target is still disposed once the swap completes; the policy only governs
        // ordering during the swap, not whether the old target is eventually freed.
        Assert.ThrowsException<ObjectDisposedException>(() => _ = first.SizeInPixels);
    }

    [TestMethod]
    public void SameSizeRequest_ReusesTheTarget_UnderBothPolicies()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        foreach (var policy in new[]
                 { BufferReplacePolicy.DisposeThenAllocate, BufferReplacePolicy.AllocateThenDispose })
        {
            using var buffer = new GrowOnlyBuffer(policy);
            var a = buffer.Ensure(device, 320, 240, "test");
            var b = buffer.Ensure(device, 320, 240, "test");
            Assert.IsTrue(ReferenceEquals(a, b), $"{policy}: an unchanged size must not reallocate");
        }
    }

    [TestMethod]
    public void GrowOnly_ReusesALargerBuffer_AndFreesTheOldOneWhenItMustGrow()
    {
        var device = TryGetDevice();
        if (device is null) { Assert.Inconclusive("No Win2D device available."); return; }

        using var buffer = new GrowOnlyBuffer();

        Assert.IsTrue(buffer.TryEnsureAtLeast(device, 256, 256, "test", out var big));
        Assert.IsNotNull(big);

        // Shrinking must reuse the existing, larger buffer — that is the whole point of the
        // grow-only policy CursorRenderer's shutter scratch relies on.
        Assert.IsTrue(buffer.TryEnsureAtLeast(device, 128, 128, "test", out var reused));
        Assert.IsTrue(ReferenceEquals(big, reused), "a smaller request must reuse the larger buffer");

        // Growing past it must retire the old one rather than hold both.
        Assert.IsTrue(buffer.TryEnsureAtLeast(device, 512, 512, "test", out var grown));
        Assert.IsFalse(ReferenceEquals(big, grown), "growing should have allocated a new target");
        Assert.ThrowsException<ObjectDisposedException>(() => _ = big!.SizeInPixels);
    }
}
