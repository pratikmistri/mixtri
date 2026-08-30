using System.Runtime.InteropServices;

namespace Mixtri.Core.Export;

/// <summary>
/// Detects available hardware video encoders (NVENC, QSV, AMF) via Media Foundation
/// and returns the best option with a CPU software fallback.
/// </summary>
public static class HardwareEncoderDetector
{
    // MFT_CATEGORY_VIDEO_ENCODER
    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER =
        new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // H.264 major type / subtype
    private static readonly Guid MFMediaType_Video =
        new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 =
        new("34363248-0000-0010-8000-00AA00389B71");

    // Well-known hardware encoder CLSIDs
    private static readonly Guid CLSID_NVIDIA_H264_ENCODER =
        new("63BE77C5-11B8-4174-BB22-7B40ADF5F4B2");
    private static readonly Guid CLSID_INTEL_QSV_H264_ENCODER =
        new("4BE8D3C0-0515-4A37-AD55-E4BAE19AF471");
    private static readonly Guid CLSID_AMD_AMF_H264_ENCODER =
        new("ADC9BC80-0F41-46C6-AB75-D693D793597D");

    // MFT_ENUM_FLAG
    private const uint MFT_ENUM_FLAG_ALL = 0x0000003F;

    /// <summary>
    /// Detects the best available hardware encoder, falling back to the built-in
    /// H.264 software encoder if no hardware option is found.
    /// </summary>
    public static EncoderInfo DetectBestEncoder()
    {
        var hwEncoders = EnumerateHardwareEncoders();

        // Prefer NVENC > QSV > AMF
        var nvidia = hwEncoders.FirstOrDefault(e =>
            e.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia is not null) return nvidia;

        var intel = hwEncoders.FirstOrDefault(e =>
            e.Vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase));
        if (intel is not null) return intel;

        var amd = hwEncoders.FirstOrDefault(e =>
            e.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase));
        if (amd is not null) return amd;

        // Any other hardware encoder
        var anyHw = hwEncoders.FirstOrDefault();
        if (anyHw is not null) return anyHw;

        // CPU fallback
        return new EncoderInfo(
            "H.264 Software Encoder",
            Guid.Empty,
            IsHardware: false,
            Vendor: "Microsoft");
    }

    private static List<EncoderInfo> EnumerateHardwareEncoders()
    {
        var results = new List<EncoderInfo>();

        try
        {
            var outputType = new MFT_REGISTER_TYPE_INFO
            {
                guidMajorType = MFMediaType_Video,
                guidSubtype = MFVideoFormat_H264,
            };

            int hr = MFTEnumEx(
                MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG_ALL,
                IntPtr.Zero,
                ref outputType,
                out IntPtr activateArrayPtr,
                out uint count);

            if (hr < 0 || count == 0 || activateArrayPtr == IntPtr.Zero)
                return results;

            try
            {
                for (uint i = 0; i < count; i++)
                {
                    IntPtr activatePtr = Marshal.ReadIntPtr(activateArrayPtr, (int)(i * IntPtr.Size));
                    if (activatePtr == IntPtr.Zero) continue;

                    var info = ClassifyEncoder(activatePtr);
                    if (info is not null && info.IsHardware)
                        results.Add(info);

                    Marshal.Release(activatePtr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activateArrayPtr);
            }
        }
        catch (Exception)
        {
            // MFT enumeration not available; return empty list so caller uses CPU fallback
        }

        return results;
    }

    private static EncoderInfo? ClassifyEncoder(IntPtr activatePtr)
    {
        try
        {
            // Read the friendly name via IMFAttributes::GetString (index 0 = MF_TRANSFORM_FRIENDLY_NAME_Attribute)
            Guid MF_TRANSFORM_FRIENDLY_NAME =
                new("314FFBAE-5B41-4C95-9C25-A20590EE3830");

            int hr = GetStringAttribute(activatePtr, ref MF_TRANSFORM_FRIENDLY_NAME, out string? name);
            if (hr < 0 || string.IsNullOrEmpty(name))
                return null;

            // Read CLSID
            Guid MFT_TRANSFORM_CLSID_Attribute =
                new("6821C42B-65A4-4e82-99BC-9A88205ECD0C");
            GetGuidAttribute(activatePtr, ref MFT_TRANSFORM_CLSID_Attribute, out Guid clsid);

            string vendor = ClassifyVendor(name, clsid);
            bool isHardware = vendor is not "Unknown";

            return new EncoderInfo(name, clsid, isHardware, vendor);
        }
        catch
        {
            return null;
        }
    }

    private static string ClassifyVendor(string name, Guid clsid)
    {
        string upper = name.ToUpperInvariant();

        if (upper.Contains("NVIDIA") || upper.Contains("NVENC") || clsid == CLSID_NVIDIA_H264_ENCODER)
            return "NVIDIA";
        if (upper.Contains("INTEL") || upper.Contains("QSV") || upper.Contains("QUICK SYNC") || clsid == CLSID_INTEL_QSV_H264_ENCODER)
            return "Intel";
        if (upper.Contains("AMD") || upper.Contains("AMF") || upper.Contains("ADVANCED MICRO") || clsid == CLSID_AMD_AMF_H264_ENCODER)
            return "AMD";

        // Check for generic hardware indicators
        if (upper.Contains("HARDWARE") || upper.Contains("HW"))
            return "Generic";

        return "Unknown";
    }

    #region P/Invoke helpers

    private static int GetStringAttribute(IntPtr obj, ref Guid key, out string? value)
    {
        value = null;
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 33 * IntPtr.Size);
            var getStr = Marshal.GetDelegateForFunctionPointer<GetAllocatedStringDelegate>(fnPtr);

            int hr = getStr(obj, ref key, out IntPtr strPtr, out uint length);
            if (hr >= 0 && strPtr != IntPtr.Zero)
            {
                value = Marshal.PtrToStringUni(strPtr, (int)length);
                Marshal.FreeCoTaskMem(strPtr);
            }

            return hr;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HardwareEncoderDetector] GetStringAttribute failed: {ex.Message}");
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    private static int GetGuidAttribute(IntPtr obj, ref Guid key, out Guid value)
    {
        value = Guid.Empty;
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 20 * IntPtr.Size);
            var getGuid = Marshal.GetDelegateForFunctionPointer<GetGUIDDelegate>(fnPtr);
            return getGuid(obj, ref key, out value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HardwareEncoderDetector] GetGuidAttribute failed: {ex.Message}");
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAllocatedStringDelegate(
        IntPtr pThis, ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGUIDDelegate(
        IntPtr pThis, ref Guid guidKey, out Guid pguidValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        uint flags,
        IntPtr pInputType,
        ref MFT_REGISTER_TYPE_INFO pOutputType,
        out IntPtr pppMFTActivate,
        out uint pnumMFTActivate);

    #endregion
}

/// <summary>
/// Describes a video encoder, either hardware-accelerated or software.
/// </summary>
public record EncoderInfo(string Name, Guid MftClsid, bool IsHardware, string Vendor);
