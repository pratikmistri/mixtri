using System.Runtime.InteropServices;

namespace Mixtri.Core.Diagnostics;

/// <summary>Returns unused native heap caches to Windows after a bulk resource teardown.</summary>
public static class NativeHeapReclaimer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OptimizeInformation
    {
        public uint Version;
        public uint Flags;
    }

    public static bool OptimizeUnusedHeaps()
    {
        var information = new OptimizeInformation { Version = 1 };
        if (HeapSetInformation(IntPtr.Zero, 3, ref information, (nuint)Marshal.SizeOf<OptimizeInformation>()))
            return true;

        DiagLog.Write("Memory", $"Native heap reclamation failed: Win32 error {Marshal.GetLastWin32Error()}");
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSetInformation(
        IntPtr heap, int informationClass, ref OptimizeInformation information, nuint informationLength);
}
