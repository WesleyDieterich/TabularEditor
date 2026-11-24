using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TabularEditor.TextServices
{
    /// <summary>
    /// Finds, parses and formats hex colors (#RGB, #RRGGBB, #RRGGBBAA) inside DAX string literals,
    /// such as SWITCH branches returning a color or the fill of an SVG measure.
    /// </summary>
    internal static class ColorLiterals
    {
        // A preceding letter, digit, underscore or '&' (HTML entities like &#123;) means this '#' is not a color.
        private static readonly Regex Hex = new Regex(
            @"(?<![0-9A-Za-z_&])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Za-z_])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Returns (start, length) of every hex color within text[start .. start + length).</summary>
        public static IEnumerable<(int Start, int Length)> Find(string text, int start, int length)
        {
            if (text.IndexOf('#', start, length) < 0) yield break;
            for (var match = Hex.Match(text, start, length); match.Success; match = match.NextMatch())
                yield return (match.Index, match.Length);
        }

        public static bool TryParse(string hex, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
            var digits = hex.Substring(1);
            if (!digits.All(Uri.IsHexDigit)) return false;
            switch (digits.Length)
            {
                case 3:
                    color = Color.FromArgb(Nibble(digits[0]) * 17, Nibble(digits[1]) * 17, Nibble(digits[2]) * 17);
                    return true;
                case 6:
                    color = Color.FromArgb(Byte(digits, 0), Byte(digits, 2), Byte(digits, 4));
                    return true;
                case 8:
                    color = Color.FromArgb(Byte(digits, 6), Byte(digits, 0), Byte(digits, 2), Byte(digits, 4));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Formats the color the way <paramref name="original"/> was written: same letter case and digit
        /// count. A 3-digit color becomes 6 digits when it can no longer be written with 3.
        /// </summary>
        public static string Format(Color color, string original)
        {
            var digits = original?.Length - 1 ?? 6;
            string text;
            if (digits == 8)
                text = $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
            else if (digits == 3 && color.R % 17 == 0 && color.G % 17 == 0 && color.B % 17 == 0)
                text = $"#{color.R / 17:X}{color.G / 17:X}{color.B / 17:X}";
            else
                text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            var lower = original != null && original.Any(char.IsLower) && !original.Any(char.IsUpper);
            return lower ? text.ToLowerInvariant() : text;
        }

        /// <summary>Whether black text reads better than white on this color (shown over light checkers if translucent).</summary>
        public static bool IsLight(Color color)
        {
            double Channel(int value)
            {
                // Blend translucent colors over the light checkerboard behind them.
                var blended = (value * color.A + 230 * (255 - color.A)) / 255.0 / 255.0;
                return blended <= 0.03928 ? blended / 12.92 : Math.Pow((blended + 0.055) / 1.055, 2.4);
            }
            var luminance = 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
            // Above this luminance black text has more contrast than white (WCAG crossover point).
            return luminance > 0.179;
        }

        private static int Nibble(char c) => int.Parse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private static int Byte(string digits, int index) =>
            int.Parse(digits.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
