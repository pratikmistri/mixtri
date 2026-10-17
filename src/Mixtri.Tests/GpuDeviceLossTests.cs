using System.Runtime.InteropServices;
using Mixtri.Core.Processing;

namespace Mixtri.Tests;

[TestClass]
public class GpuDeviceLossTests
{
    [TestMethod]
    [DataRow(unchecked((int)0x887A0005))]
    [DataRow(unchecked((int)0x887A0006))]
    [DataRow(unchecked((int)0x887A0007))]
    [DataRow(unchecked((int)0x887A0020))]
    [DataRow(unchecked((int)0x8899000C))]
    public void WrappedAllocationFailure_StillIdentifiesDeviceLoss(int hresult)
    {
        var exception = new InvalidOperationException(
            "Failed to allocate preview render target.", new COMException("device lost", hresult));
        Assert.IsTrue(GpuContext.IsDeviceLoss(exception));
    }

    [TestMethod]
    public void RecoverableGuardException_IdentifiesDeviceLoss()
    {
        Assert.IsTrue(GpuContext.IsDeviceLoss(new RecoverableDeviceLostException("lost")));
    }

    [TestMethod]
    public void AggregateDeviceLoss_IdentifiesWrappedCause()
    {
        var exception = new AggregateException(
            new InvalidOperationException("unrelated"),
            new InvalidOperationException("wrapped", new COMException("reset", unchecked((int)0x887A0007))));
        Assert.IsTrue(GpuContext.IsDeviceLoss(exception));
    }

    [TestMethod]
    public void UnrelatedRenderFailure_DoesNotTriggerDeviceReset()
    {
        Assert.IsFalse(GpuContext.IsDeviceLoss(new InvalidOperationException("invalid style")));
        Assert.IsFalse(GpuContext.IsDeviceLoss(new OutOfMemoryException()));
        Assert.IsFalse(GpuContext.IsDeviceLoss(new COMException("invalid argument", unchecked((int)0x80070057))));
    }
}
