using System.Runtime.InteropServices;

namespace Musio.Core.Interop;

// Shared Win32 struct definitions used across capture, DPI/monitor, and overlay
// interop code. Consolidated from what were previously multiple private, per-file
// copies (see learnings/playbooks.md - DPI & capture coordinate transforms).
//
// Field order and every marshalling attribute here are load-bearing - do not
// "clean up" a struct without checking every call site. In particular,
// MONITORINFOEX requires CharSet.Unicode on both the struct and the P/Invoke
// declaration that fills it, or szDevice silently corrupts.

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
public struct CURSORINFO
{
    public int cbSize;
    public int flags;
    public nint hCursor;
    public POINT ptScreenPos;
}

// GDI bitmap header used with GetDIBits. Not part of the work item's core struct
// table, but consolidated alongside it because it is duplicated verbatim in the
// same two files (RegionSelectorOverlay/WindowSelectorOverlay) whose GetDIBits
// declaration is also being consolidated.
[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public short biPlanes;
    public short biBitCount;
    public int biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public int biClrUsed;
    public int biClrImportant;
}
