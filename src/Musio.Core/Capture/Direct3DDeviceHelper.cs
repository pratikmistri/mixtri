using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Musio.Core.Capture;

/// <summary>
/// Creates an IDirect3DDevice from a D3D11 device via DXGI interop.
/// </summary>
public static class Direct3DDeviceHelper
{
    public static IDirect3DDevice CreateDevice()
    {
        object d3dDevice;

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

        // The D3D11 device implements IDXGIDevice; get its IUnknown for the DXGI interop call
        var dxgiDevicePtr = Marshal.GetIUnknownForObject(d3dDevice);
        try
        {
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var graphicsDevice);
            Marshal.ThrowExceptionForHR(hr);

            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            Marshal.Release(dxgiDevicePtr);
            Marshal.ReleaseComObject(d3dDevice);
        }
    }

    private static object CreateD3D11Device(D3D_DRIVER_TYPE driverType)
    {
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
            out _);
        Marshal.ThrowExceptionForHR(hr);
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
        [MarshalAs(UnmanagedType.IUnknown)] out object ppDevice,
        out int pFeatureLevel,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
