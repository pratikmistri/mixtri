using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Musio.Core.Capture;

/// <summary>
/// Creates an IDirect3DDevice from a D3D11 device via DXGI interop.
/// </summary>
/// <remarks>
/// Every COM pointer obtained here is owned by this class and released exactly once, on both the
/// success and the failure paths. In particular, CsWinRT's <c>MarshalInterface&lt;T&gt;.FromAbi</c>
/// is NON-owning: it forwards to <c>ComWrappers.GetOrCreateObjectForComInstance</c>, which takes
/// its own reference on the ABI pointer and never consumes the caller's. The caller must therefore
/// release the raw pointer itself (that is what <c>MarshalInterface&lt;T&gt;.DisposeAbi</c> exists
/// for), otherwise every device creation leaks a reference and pins GPU resources.
/// </remarks>
public static class Direct3DDeviceHelper
{
    public static IDirect3DDevice CreateDevice()
    {
        IntPtr d3dDevice;

        try
        {
            d3dDevice = CreateD3D11Device(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE);
        }
        catch (Exception hardwareException)
        {
            Debug.WriteLine($"[Direct3DDeviceHelper] Hardware D3D11 device creation failed; falling back to WARP: {hardwareException.Message}");

            try
            {
                d3dDevice = CreateD3D11Device(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP);
            }
            catch
            {
                ExceptionDispatchInfo.Capture(hardwareException).Throw();
                throw;
            }
        }

        try
        {
            // The D3D11 device implements IDXGIDevice; the interop entry point takes its IUnknown.
            var hr = CreateDirect3D11DeviceFromDXGIDevice(d3dDevice, out var graphicsDevice);
            Marshal.ThrowExceptionForHR(hr);

            if (graphicsDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice reported success but returned a null device.");
            }

            return ProjectAbi(
                graphicsDevice,
                MarshalInterface<IDirect3DDevice>.FromAbi,
                MarshalInterface<IDirect3DDevice>.DisposeAbi);
        }
        finally
        {
            Marshal.Release(d3dDevice);
        }
    }

    /// <summary>
    /// Projects a WinRT ABI pointer into its managed projection and then releases the caller's
    /// reference on that pointer exactly once — including when the projection throws.
    /// </summary>
    /// <remarks>
    /// The projection keeps its own reference, so releasing here neither leaks nor over-releases.
    /// The delegates are parameters purely so this ownership contract can be regression-tested
    /// without a real GPU device.
    /// </remarks>
    internal static T ProjectAbi<T>(IntPtr abi, Func<IntPtr, T> fromAbi, Action<IntPtr> disposeAbi)
    {
        try
        {
            return fromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero)
            {
                disposeAbi(abi);
            }
        }
    }

    /// <summary>
    /// Creates a D3D11 device and returns its raw ID3D11Device pointer; the caller owns that single
    /// reference and must release it.
    /// </summary>
    private static IntPtr CreateD3D11Device(D3D_DRIVER_TYPE driverType)
    {
        // A null ppImmediateContext keeps D3D from handing back a context reference that this
        // helper would only have to release again.
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null,
            0,
            D3D11_SDK_VERSION,
            out var d3dDevice,
            out _,
            IntPtr.Zero);

        if (hr < 0)
        {
            if (d3dDevice != IntPtr.Zero)
            {
                Marshal.Release(d3dDevice);
            }

            Marshal.ThrowExceptionForHR(hr);
        }

        if (d3dDevice == IntPtr.Zero)
        {
            throw new InvalidOperationException("D3D11CreateDevice reported success but returned a null device.");
        }

        return d3dDevice;
    }

    private const uint D3D11_SDK_VERSION = 7;

    [Flags]
    private enum D3D11_CREATE_DEVICE_FLAG : uint
    {
        D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20
    }

    private enum D3D_DRIVER_TYPE
    {
        D3D_DRIVER_TYPE_HARDWARE = 1,
        D3D_DRIVER_TYPE_WARP = 5
    }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", SetLastError = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        D3D_DRIVER_TYPE driverType,
        IntPtr software,
        D3D11_CREATE_DEVICE_FLAG flags,
        [MarshalAs(UnmanagedType.LPArray)] int[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
