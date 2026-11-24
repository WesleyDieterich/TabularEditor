using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using FastColoredTextBoxNS;
using TabularEditor.TextServices;

namespace TabularEditor.UI
{
    /// <summary>
    /// Draws a hex color written in a DAX string on a chip filled with that color, with the text in black
    /// or white for contrast. FastColoredTextBox lays text out on a fixed character grid, so a separate
    /// swatch like VS Code's would cover a character; tinting the text itself takes no extra space.
    /// One style serves every color, as an editor only has 16 style slots.
    /// </summary>
    internal sealed class ColorChipStyle : Style
    {
        public static readonly ColorChipStyle Instance = new ColorChipStyle();

        private readonly TextStyle darkText = new TextStyle(Brushes.Black, null, FontStyle.Regular);
        private readonly TextStyle lightText = new TextStyle(Brushes.White, null, FontStyle.Regular);
        private static readonly Brush Checkers = new HatchBrush(HatchStyle.SmallCheckerBoard, Color.White, Color.FromArgb(204, 204, 204));

        private ColorChipStyle() { }

        public override void Draw(Graphics gr, Point position, Range range)
        {
            var tb = range.tb;
            var line = tb[range.Start.iLine];
            var first = range.Start.iChar;
            var last = Math.Min(range.End.iChar, line.Count);

            // Word wrap can split a color across rows, and each row is drawn on its own: read the whole color.
            var start = first;
            while (start > 0 && start > first - 9 && line[start].c != '#') start--;
            var end = start + 1;
            while (end < line.Count && end - start < 9 && Uri.IsHexDigit(line[end].c)) end++;
            var chars = new char[end - start];
            for (var i = start; i < end; i++) chars[i - start] = line[i].c;

            if (!ColorLiterals.TryParse(new string(chars), out var color))
            {
                ExpressionParser.LiteralStyle.Draw(gr, position, range);
                return;
            }

            var chip = new Rectangle(position.X, position.Y + tb.LineInterval / 2,
                (last - first) * tb.CharWidth, tb.CharHeight - tb.LineInterval);
            var wholeColor = first == start && last == end;
            var smoothing = gr.SmoothingMode;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var path = wholeColor ? GetRoundedRectangle(chip, Math.Max(2, tb.CharHeight / 6)) : null)
                using (var fill = new SolidBrush(color))
                {
                    if (color.A < 255) Fill(gr, Checkers, chip, path);
                    Fill(gr, fill, chip, path);
                }
            }
            finally
            {
                gr.SmoothingMode = smoothing;
            }

            (ColorLiterals.IsLight(color) ? darkText : lightText).Draw(gr, position, range);
        }

        private static void Fill(Graphics gr, Brush brush, Rectangle rect, GraphicsPath path)
        {
            if (path != null) gr.FillPath(brush, path);
            else gr.FillRectangle(brush, rect);
        }
    }
}
