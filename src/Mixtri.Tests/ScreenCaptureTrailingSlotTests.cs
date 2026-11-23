using Mixtri.Core.Capture;

namespace Mixtri.Tests;

/// <summary>
/// A static screen delivers no capture frames, so the slots between the last emitted frame
/// and the stop instant must be computed at stop and handed to the writer as owed slots.
/// </summary>
[TestClass]
public class ScreenCaptureTrailingSlotTests
{
    [TestMethod]
    public void NoFrameYet_OwesNothing()
        => Assert.AreEqual(0, ScreenCaptureEngine.ComputeTrailingOwedSlots(-1, 500));

    [TestMethod]
    public void StopInLastEmittedSlot_OwesNothing()
        => Assert.AreEqual(0, ScreenCaptureEngine.ComputeTrailingOwedSlots(10, 10));

    [TestMethod]
    public void StopInNextSlot_OwesNothing()
        => Assert.AreEqual(0, ScreenCaptureEngine.ComputeTrailingOwedSlots(10, 11));

    [TestMethod]
    public void StaticTail_OwesEverySlotBetweenLastFrameAndStop()
        // Last frame in slot 10, stop lands in slot 310 (10 s at 30 fps later): 299 owed,
        // exactly what a frame arriving at that instant would have reported as skipped.
        => Assert.AreEqual(299, ScreenCaptureEngine.ComputeTrailingOwedSlots(10, 310));

    [TestMethod]
    public void EndBeforeLastEmitted_OwesNothing()
        => Assert.AreEqual(0, ScreenCaptureEngine.ComputeTrailingOwedSlots(50, 20));

    [TestMethod]
    public void HugeGap_IsClampedToInt()
        => Assert.AreEqual(int.MaxValue,
            ScreenCaptureEngine.ComputeTrailingOwedSlots(0, long.MaxValue));
}
