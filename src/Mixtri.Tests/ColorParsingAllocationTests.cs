using System.Globalization;
using System.Reflection;
using Mixtri.Core.Processing;
using Windows.UI;

namespace Mixtri.Tests;

[TestClass]
public class ColorParsingAllocationTests
{
    private static readonly string?[] Inputs =
    [
        null, "", " ", "\t\r\n", "#abc", "AbC", "#1234", "##aBcD", "#112233", "112233",
        "#80112233", "####00112233", " \t##aBcDeF \r\n", "#F FFFF", "#12 456", "#1\0FFFF",
        "# 12345", "#", "#####", "#ab", "#abcde", "#abcdefghi", "#0x1234", "#GGGGGG",
        "#12-456", "\u2000#123456\u2000", "#\uD800ab", "#ab\t", "#\t123",
    ];

    [TestMethod]
    public void TextParserPreservesWhitespaceMalformedAndFallbackBehavior()
    {
        foreach (var input in Inputs)
            Assert.AreEqual(PreviousTextColor(input), AnimatedTextEngine.ParseColor(input), $"Input {input}");
    }

    [TestMethod]
    public void EveryRgbAndArgbShorthandMatchesPreviousExpansion()
    {
        for (int value = 0; value <= 0xfff; value++)
        {
            string input = "#" + value.ToString("X3", CultureInfo.InvariantCulture);
            Assert.AreEqual(PreviousTextColor(input), AnimatedTextEngine.ParseColor(input));
        }
        for (int value = 0; value <= 0xffff; value++)
        {
            string input = "#" + value.ToString("X4", CultureInfo.InvariantCulture);
            Assert.AreEqual(PreviousTextColor(input), AnimatedTextEngine.ParseColor(input));
        }
    }

    [TestMethod]
    public void CursorParserPreservesItsDistinctPolicyAndOpacity()
    {
        foreach (var input in Inputs)
        {
            using var renderer = new CursorRenderer(new CursorStyle { Color = input! });
            var parse = typeof(CursorRenderer).GetMethod("ParseCursorColor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<float, Color>>(renderer);
            foreach (float opacity in new[] { 0f, .25f, .5f, .75f, 1f })
                Assert.AreEqual(PreviousCursorColor(input, opacity), parse(opacity), $"Input {input}, opacity {opacity}");
        }
    }

    [TestMethod]
    public void RepeatedTextAndCursorColorParsingAllocatesNothing()
    {
        string[] inputs = ["#abc", "#abcd", "#112233", "#80112233", " ##aBcDeF ", "#bad-value"];
        var renderers = inputs.Select(input => new CursorRenderer(new CursorStyle { Color = input })).ToArray();
        try
        {
            var method = typeof(CursorRenderer).GetMethod("ParseCursorColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var parsers = renderers.Select(renderer => method.CreateDelegate<Func<float, Color>>(renderer)).ToArray();
            for (int i = 0; i < 1000; i++)
            {
                AnimatedTextEngine.ParseColor(inputs[i % inputs.Length]);
                parsers[i % inputs.Length](.5f);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            int currentChecksum = 0;
            for (int i = 0; i < 50000; i++)
            {
                int index = i % inputs.Length;
                currentChecksum += AnimatedTextEngine.ParseColor(inputs[index]).R + parsers[index](.5f).A;
            }
            long currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            int previousChecksum = 0;
            for (int i = 0; i < 50000; i++)
            {
                var input = inputs[i % inputs.Length];
                previousChecksum += PreviousTextColor(input).R + PreviousCursorColor(input, .5f).A;
            }
            long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(previousChecksum, currentChecksum);
            Assert.AreEqual(0L, currentBytes);
            Assert.IsTrue(previousBytes > 1_000_000);
            Console.WriteLine($"50,000 text/cursor parse pairs: {previousBytes:N0} previous bytes, {currentBytes:N0} current bytes.");
        }
        finally { foreach (var renderer in renderers) renderer.Dispose(); }
    }

    private static Color PreviousTextColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(255, 255, 255, 255);
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        else if (hex.Length == 4)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]);
        const NumberStyles style = NumberStyles.HexNumber;
        var culture = CultureInfo.InvariantCulture;
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), style, culture, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), style, culture, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), style, culture, out var b))
            return Color.FromArgb(255, r, g, b);
        if (hex.Length == 8
            && byte.TryParse(hex.AsSpan(0, 2), style, culture, out var a)
            && byte.TryParse(hex.AsSpan(2, 2), style, culture, out var r2)
            && byte.TryParse(hex.AsSpan(4, 2), style, culture, out var g2)
            && byte.TryParse(hex.AsSpan(6, 2), style, culture, out var b2))
            return Color.FromArgb(a, r2, g2, b2);
        return Color.FromArgb(255, 255, 255, 255);
    }

    private static Color PreviousCursorColor(string? color, float opacity)
    {
        byte alpha = (byte)(opacity * 255);
        string hex = (color ?? "#FFFFFF").TrimStart('#');
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b))
            return Color.FromArgb(alpha, r, g, b);
        return Color.FromArgb(alpha, 255, 255, 255);
    }
}
