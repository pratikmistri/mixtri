using System.Runtime.InteropServices;
using Musio.Core.Interop;
using Musio.Core.Models;

namespace Musio.Core.Capture;

/// <summary>
/// Resolves the currently displayed system cursor into a <see cref="CursorShape"/> by
/// comparing the live cursor handle (from <c>GetCursorInfo</c>) against the standard
/// system cursor handles (loaded via <c>LoadCursor</c> with the <c>IDC_*</c> ids).
///
/// Handles are cached once on construction. Any cursor that does not match a known
/// shape — including custom application cursors — resolves to <see cref="CursorShape.Arrow"/>.
/// </summary>
public sealed class CursorShapeResolver
{
    #region Win32 interop

    private const int CURSOR_SHOWING = 0x00000001;

    // Standard cursor resource ids (winuser.h IDC_*)
    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;
    private const int IDC_SIZENWSE = 32642;
    private const int IDC_SIZENESW = 32643;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_SIZENS = 32645;
    private const int IDC_HAND = 32649;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadCursor(nint hInstance, int lpCursorName);

    #endregion

    private readonly Dictionary<nint, CursorShape> _handleToShape = new();
    private static readonly int CursorInfoSize = Marshal.SizeOf<CURSORINFO>();

    public CursorShapeResolver()
    {
        // Cache the standard system cursor handles. LoadCursor on the standard
        // IDC_* ids returns shared handles that stay valid for the process lifetime.
        Register(IDC_ARROW, CursorShape.Arrow);
        Register(IDC_HAND, CursorShape.Hand);
        Register(IDC_IBEAM, CursorShape.IBeam);
        Register(IDC_SIZEWE, CursorShape.ResizeWE);
        Register(IDC_SIZENS, CursorShape.ResizeNS);
        Register(IDC_SIZENWSE, CursorShape.ResizeNWSE);
        Register(IDC_SIZENESW, CursorShape.ResizeNESW);
    }

    private void Register(int idc, CursorShape shape)
    {
        nint handle = LoadCursor(nint.Zero, idc);
        if (handle != nint.Zero)
            _handleToShape[handle] = shape;
    }

    /// <summary>
    /// Returns the shape of the cursor currently displayed on screen. Unknown or
    /// custom cursors (and any failure) resolve to <see cref="CursorShape.Arrow"/>.
    /// </summary>
    public CursorShape Resolve() => Resolve(out POINT _, out bool _);

    /// <summary>
    /// Same as <see cref="Resolve()"/> but also reports the cursor's screen position as
    /// observed in the same <c>GetCursorInfo</c> call, so a caller that records a shape
    /// change can stamp it with a matching position. On failure the position is (0,0)
    /// and <paramref name="positionValid"/> is false.
    /// </summary>
    public CursorShape Resolve(out int screenX, out int screenY, out bool positionValid)
    {
        var shape = Resolve(out var point, out positionValid);
        screenX = point.X;
        screenY = point.Y;
        return shape;
    }

    private CursorShape Resolve(out POINT position, out bool positionValid)
    {
        var info = new CURSORINFO { cbSize = CursorInfoSize };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0)
        {
            position = default;
            positionValid = false;
            return CursorShape.Arrow;
        }

        position = info.ptScreenPos;
        positionValid = true;

        return _handleToShape.TryGetValue(info.hCursor, out var shape)
            ? shape
            : CursorShape.Arrow;
    }
}
