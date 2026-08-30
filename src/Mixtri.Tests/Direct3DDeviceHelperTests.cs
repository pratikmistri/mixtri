using System.Runtime.InteropServices;
using Mixtri.Core.Capture;
using WinRT;

namespace Mixtri.Tests;

/// <summary>
/// Ownership guards for the raw COM/ABI pointers used by <see cref="Direct3DDeviceHelper"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateDirect3D11DeviceFromDXGIDevice</c> hands back an AddRef'd <c>IInspectable*</c>. CsWinRT's
/// <c>MarshalInterface&lt;T&gt;.FromAbi</c> is NON-owning — it QIs for <c>IUnknown</c> (releasing only
/// that temporary), then calls <c>ComWrappers.GetOrCreateObjectForComInstance</c>, which takes its own
/// reference and never consumes the caller's. Forgetting to release the raw pointer therefore leaks a
/// reference per device creation; releasing it twice tears down a live device.
/// </para>
/// <para>
/// These tests exercise the ownership seam (<c>Direct3DDeviceHelper.ProjectAbi</c>) and the real CsWinRT
/// marshaler against an ordinary WinRT object, so they need no GPU and no capture session.
/// </para>
/// </remarks>
[TestClass]
public class Direct3DDeviceHelperTests
{
    private static readonly IntPtr FakeAbi = new(0x1234);

    [TestMethod]
    public void ProjectAbi_ReleasesPointerExactlyOnce_OnSuccess()
    {
        var released = new List<IntPtr>();
        var projection = new object();

        var result = Direct3DDeviceHelper.ProjectAbi(FakeAbi, _ => projection, released.Add);

        Assert.AreSame(projection, result);
        CollectionAssert.AreEqual(new[] { FakeAbi }, released);
    }

    [TestMethod]
    public void ProjectAbi_ReleasesPointerExactlyOnce_WhenProjectionThrows()
    {
        var released = new List<IntPtr>();

        Assert.ThrowsException<InvalidOperationException>(() =>
            Direct3DDeviceHelper.ProjectAbi<object>(
                FakeAbi,
                _ => throw new InvalidOperationException("projection failed"),
                released.Add));

        CollectionAssert.AreEqual(new[] { FakeAbi }, released);
    }

    [TestMethod]
    public void ProjectAbi_DoesNotReleaseNullPointer()
    {
        var released = new List<IntPtr>();

        var result = Direct3DDeviceHelper.ProjectAbi<object?>(IntPtr.Zero, _ => null, released.Add);

        Assert.IsNull(result);
        Assert.AreEqual(0, released.Count, "releasing a null ABI pointer would fault");
    }

    [TestMethod]
    public void MarshalInterfaceFromAbi_DoesNotConsumeCallerReference()
    {
        // Any activatable WinRT object exercises the same CsWinRT marshaling path as IDirect3DDevice.
        var calendar = new Windows.Globalization.Calendar();
        var abi = MarshalInspectable<object>.FromManaged(calendar);
        Assert.AreNotEqual(IntPtr.Zero, abi);

        var countWithCallerReference = GetRefCount(abi);

        var projection = MarshalInterface<object>.FromAbi(abi);

        Assert.IsNotNull(projection);
        Assert.IsTrue(
            GetRefCount(abi) >= countWithCallerReference,
            "FromAbi must take its own reference and leave the caller's reference intact");

        var remaining = Marshal.Release(abi);

        Assert.IsTrue(remaining >= 1, "the projection must still hold a reference after the caller releases");
        calendar.SetToNow();
    }

    [TestMethod]
    public void ProjectAbi_WithRealMarshaler_ReleasesOnceAndKeepsProjectionUsable()
    {
        var calendar = new Windows.Globalization.Calendar();
        var abi = MarshalInspectable<object>.FromManaged(calendar);
        Assert.AreNotEqual(IntPtr.Zero, abi);

        var releases = 0;

        var projection = Direct3DDeviceHelper.ProjectAbi(
            abi,
            MarshalInterface<object>.FromAbi,
            ptr =>
            {
                releases++;
                MarshalInterface<object>.DisposeAbi(ptr);
            });

        Assert.AreEqual(1, releases);
        Assert.IsNotNull(projection);

        // Would fault if ProjectAbi had over-released the interop pointer.
        ((Windows.Globalization.Calendar)projection).SetToNow();
        GC.KeepAlive(calendar);
    }

    private static int GetRefCount(IntPtr unknown)
    {
        Marshal.AddRef(unknown);
        return Marshal.Release(unknown);
    }
}
