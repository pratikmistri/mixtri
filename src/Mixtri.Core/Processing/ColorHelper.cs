using Windows.UI;

namespace Mixtri.Core.Processing;

/// <summary>
/// Utility methods for parsing and manipulating colors.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Parses a hex color string (#RGB, #RRGGBB, or #AARRGGBB) into a <see cref="Color"/>.
    /// </summary>
    public static Color ParseColor(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        return span.Length switch
        {
            // #RGB → expand each nibble
            3 => Color.FromArgb(
                0xFF,
                ParseDuplicatedNibble(span[0]),
                ParseDuplicatedNibble(span[1]),
                ParseDuplicatedNibble(span[2])),

            // #RRGGBB
            6 => Color.FromArgb(
                0xFF,
                ParseByte(span[0..2]),
                ParseByte(span[2..4]),
                ParseByte(span[4..6])),

            // #AARRGGBB
            8 => Color.FromArgb(
                ParseByte(span[0..2]),
                ParseByte(span[2..4]),
                ParseByte(span[4..6]),
                ParseByte(span[6..8])),

            _ => throw new FormatException($"Invalid hex color format: {hex}")
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="color"/> with the specified opacity (0.0–1.0).
    /// </summary>
    public static Color WithOpacity(Color color, double opacity)
    {
        byte a = (byte)Math.Clamp(opacity * 255, 0, 255);
        return Color.FromArgb(a, color.R, color.G, color.B);
    }

    private static byte ParseByte(ReadOnlySpan<char> hex) =>
        byte.Parse(hex, System.Globalization.NumberStyles.HexNumber);

    private static byte ParseDuplicatedNibble(char c)
    {
        byte nibble = byte.Parse([c], System.Globalization.NumberStyles.HexNumber);
        return (byte)(nibble << 4 | nibble);
    }
}
