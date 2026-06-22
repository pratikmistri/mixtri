using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Musio.Core.Models;

namespace Musio.Core.Processing;

/// <summary>
/// A cursor glyph: a filled vector silhouette plus the hotspot (in the glyph's own
/// coordinate space) that should align with the real on-screen pointer point.
/// </summary>
public readonly record struct CursorGlyph(CanvasGeometry Geometry, Vector2 Hotspot);

/// <summary>
/// Builds <see cref="CanvasGeometry"/> glyphs for the supported <see cref="CursorShape"/>
/// values from SVG path data. Each shape is authored as an SVG <c>d</c> string with a
/// known hotspot so the rendered cursor lines up with the recorded pointer position.
/// </summary>
public static class CursorGeometryLibrary
{
    // Each entry: SVG path data + hotspot in the path's coordinate space + a uniform
    // scale applied at build time. Shapes are authored as closed, filled silhouettes
    // (rendered as a white body with a contrasting outline, matching how Windows draws
    // its cursors). Scale normalizes art authored in different viewBoxes so every
    // cursor's main visual dimension is comparable (~26-30 units) regardless of source.
    private static readonly IReadOnlyDictionary<CursorShape, (string Path, Vector2 Hotspot, float Scale)> Definitions =
        new Dictionary<CursorShape, (string, Vector2, float)>
        {
            // Classic arrow; tip (hotspot) at the origin.
            [CursorShape.Arrow] =
                ("M0 0 L0 24 L5.5 18.5 L9.5 27 L13 25.5 L9 17 L15 15.5 Z", new Vector2(0f, 0f), 1f),

            // Pointing hand (link cursor) — fill silhouette from a user-provided SVG
            // (23x26 viewBox, Windows-style hand). Scaled up to match the other
            // cursors' visual size; hotspot at the index fingertip.
            [CursorShape.Hand] =
                ("M8.03536 13.2L7.14874 11.8453C6.54336 10.9203 5.25384 10.5829 4.26748 11.09" +
                 "L4.49731 10.9718C4.25253 11.0977 4.13515 11.4114 4.23593 11.6721" +
                 "L5.46321 14.8474C5.65776 15.3508 6.15466 16.0627 6.56642 16.4203" +
                 "C6.56642 16.4203 9.03536 18.4639 9.03536 19.23V20.2H13.0354H14.1292H15.0354H16.0354" +
                 "V19.23C16.0354 18.4639 17.5442 16.0507 17.5442 16.0507" +
                 "C17.8219 15.5816 18.0534 14.7552 18.0534 14.2066V10.1718" +
                 "C18.0354 9.2785 17.276 8.55454 16.3391 8.55454" +
                 "C15.8703 8.55454 15.4907 8.91652 15.4907 9.36341V9.68657" +
                 "C15.4907 8.79327 14.7313 8.06931 13.7944 8.06931" +
                 "C13.3257 8.06931 12.946 8.43129 12.946 8.87819V9.20135" +
                 "C12.946 8.30804 12.1867 7.58408 11.2497 7.58408" +
                 "C10.781 7.58408 10.4013 7.94606 10.4013 8.39296V8.71612" +
                 "C10.4013 8.57256 10.386 8.45843 10.3563 8.36799L10.0975 4.20121" +
                 "C10.0625 3.63776 9.58764 3.2 9.03536 3.2C8.47922 3.2 8.03536 3.64763 8.03536 4.19981" +
                 "V8.2V13.2Z",
                 new Vector2(9.03536f, 3.2f), 1.5f),

            // Text I-beam with serifs; hotspot at the center.
            [CursorShape.IBeam] =
                ("M0 0 L10 0 L10 2.5 L6.25 2.5 L6.25 23.5 L10 23.5 L10 26 L0 26 L0 23.5 " +
                 "L3.75 23.5 L3.75 2.5 L0 2.5 Z", new Vector2(5f, 13f), 1f),

            // Horizontal resize (double arrow); hotspot at the center.
            [CursorShape.ResizeWE] =
                ("M0 7 L7 0 L7 4 L23 4 L23 0 L30 7 L23 14 L23 10 L7 10 L7 14 Z",
                 new Vector2(15f, 7f), 1f),

            // Vertical resize (double arrow); hotspot at the center.
            [CursorShape.ResizeNS] =
                ("M7 0 L14 7 L10 7 L10 23 L14 23 L7 30 L0 23 L4 23 L4 7 L0 7 Z",
                 new Vector2(7f, 15f), 1f),

            // Diagonal resize NW-SE (symmetric about the main diagonal); hotspot center.
            [CursorShape.ResizeNWSE] =
                ("M0 0 L11 0 L6 5 L19 18 L24 13 L24 24 L13 24 L18 19 L5 6 L0 11 Z",
                 new Vector2(12f, 12f), 1f),

            // Diagonal resize NE-SW (mirror of NW-SE); hotspot center.
            [CursorShape.ResizeNESW] =
                ("M24 0 L13 0 L18 5 L5 18 L0 13 L0 24 L11 24 L6 19 L19 6 L24 11 Z",
                 new Vector2(12f, 12f), 1f),
        };

    /// <summary>
    /// Builds one <see cref="CursorGlyph"/> per supported shape. Callers own the returned
    /// geometries and must dispose them.
    /// </summary>
    public static Dictionary<CursorShape, CursorGlyph> Build(ICanvasResourceCreator creator)
    {
        ArgumentNullException.ThrowIfNull(creator);

        var result = new Dictionary<CursorShape, CursorGlyph>();
        foreach (var (shape, def) in Definitions)
        {
            var geometry = ParseSvgPath(creator, def.Path);
            var hotspot = def.Hotspot;
            if (def.Scale != 1f)
            {
                var scaled = geometry.Transform(Matrix3x2.CreateScale(def.Scale));
                geometry.Dispose();
                geometry = scaled;
                hotspot *= def.Scale;
            }
            result[shape] = new CursorGlyph(geometry, hotspot);
        }
        return result;
    }

    /// <summary>
    /// Parses a subset of SVG path data (commands M, L, H, V, C, Q, Z — absolute and
    /// relative) into a closed-or-open <see cref="CanvasGeometry"/>.
    /// </summary>
    public static CanvasGeometry ParseSvgPath(ICanvasResourceCreator creator, string pathData)
    {
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentException.ThrowIfNullOrEmpty(pathData);

        using var builder = new CanvasPathBuilder(creator);

        float curX = 0, curY = 0, startX = 0, startY = 0;
        bool figureOpen = false;
        int idx = 0;
        char cmd = '\0';

        while (idx < pathData.Length)
        {
            char c = pathData[idx];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                idx++;
                continue;
            }

            if (char.IsLetter(c))
            {
                cmd = c;
                idx++;

                if (cmd is 'Z' or 'z')
                {
                    if (figureOpen)
                    {
                        builder.EndFigure(CanvasFigureLoop.Closed);
                        figureOpen = false;
                    }
                    curX = startX;
                    curY = startY;
                }
                continue;
            }

            bool relative = char.IsLower(cmd);
            char op = char.ToUpperInvariant(cmd);

            switch (op)
            {
                case 'M':
                {
                    float x = ReadNumber(pathData, ref idx);
                    float y = ReadNumber(pathData, ref idx);
                    if (relative) { x += curX; y += curY; }

                    if (figureOpen)
                        builder.EndFigure(CanvasFigureLoop.Open);

                    builder.BeginFigure(x, y);
                    figureOpen = true;
                    curX = startX = x;
                    curY = startY = y;
                    // Subsequent implicit coordinate pairs after M are treated as L.
                    cmd = relative ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    float x = ReadNumber(pathData, ref idx);
                    float y = ReadNumber(pathData, ref idx);
                    if (relative) { x += curX; y += curY; }
                    builder.AddLine(x, y);
                    curX = x; curY = y;
                    break;
                }
                case 'H':
                {
                    float x = ReadNumber(pathData, ref idx);
                    if (relative) x += curX;
                    builder.AddLine(x, curY);
                    curX = x;
                    break;
                }
                case 'V':
                {
                    float y = ReadNumber(pathData, ref idx);
                    if (relative) y += curY;
                    builder.AddLine(curX, y);
                    curY = y;
                    break;
                }
                case 'C':
                {
                    float x1 = ReadNumber(pathData, ref idx);
                    float y1 = ReadNumber(pathData, ref idx);
                    float x2 = ReadNumber(pathData, ref idx);
                    float y2 = ReadNumber(pathData, ref idx);
                    float x = ReadNumber(pathData, ref idx);
                    float y = ReadNumber(pathData, ref idx);
                    if (relative) { x1 += curX; y1 += curY; x2 += curX; y2 += curY; x += curX; y += curY; }
                    builder.AddCubicBezier(new Vector2(x1, y1), new Vector2(x2, y2), new Vector2(x, y));
                    curX = x; curY = y;
                    break;
                }
                case 'Q':
                {
                    float x1 = ReadNumber(pathData, ref idx);
                    float y1 = ReadNumber(pathData, ref idx);
                    float x = ReadNumber(pathData, ref idx);
                    float y = ReadNumber(pathData, ref idx);
                    if (relative) { x1 += curX; y1 += curY; x += curX; y += curY; }
                    builder.AddQuadraticBezier(new Vector2(x1, y1), new Vector2(x, y));
                    curX = x; curY = y;
                    break;
                }
                default:
                    throw new FormatException($"Unsupported SVG path command '{cmd}'.");
            }
        }

        if (figureOpen)
            builder.EndFigure(CanvasFigureLoop.Open);

        return CanvasGeometry.CreatePath(builder);
    }

    private static float ReadNumber(string s, ref int idx)
    {
        // Skip separators.
        while (idx < s.Length && (char.IsWhiteSpace(s[idx]) || s[idx] == ','))
            idx++;

        int start = idx;
        if (idx < s.Length && (s[idx] == '-' || s[idx] == '+'))
            idx++;

        while (idx < s.Length && (char.IsDigit(s[idx]) || s[idx] == '.'))
            idx++;

        if (idx == start)
            throw new FormatException($"Expected a number at position {start} in path data.");

        return float.Parse(s.AsSpan(start, idx - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
