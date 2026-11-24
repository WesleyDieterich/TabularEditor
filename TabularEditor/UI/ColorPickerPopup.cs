using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TabularEditor.UI
{
    /// <summary>
    /// A small VS Code-style color picker: a header showing the new color (left) and the original color
    /// (right, click to go back to it), a saturation/brightness square, a hue bar and, for colors written
    /// with transparency, an opacity bar. Esc cancels, Enter or clicking elsewhere keeps the color.
    /// </summary>
    internal sealed class ColorPickerPopup : Form
    {
        private enum Part { None, Square, Hue, Alpha }

        private static readonly Brush Checkers = new HatchBrush(HatchStyle.SmallCheckerBoard, Color.White, Color.FromArgb(204, 204, 204));

        private readonly Color original;
        private readonly bool showAlpha;
        private readonly Func<Color, string> format;
        private readonly float scale;
        private float hue, saturation, brightness;
        private Rectangle header, current, originalArea, square, hueBar, alphaBar;
        private Part dragging;
        private bool closing;

        public event EventHandler ColorChanged;

        /// <summary>The picked color. Starts as the original exactly, so opening the picker changes nothing.</summary>
        public Color Color { get; private set; }

        /// <summary>True when the picker closed with Enter or by clicking elsewhere, false after Esc.</summary>
        public bool Committed { get; private set; }

        /// <summary>True when the picker closed because the user clicked elsewhere, which also placed focus there.</summary>
        public bool ClickedAway { get; private set; }

        public ColorPickerPopup(Color original, bool showAlpha, Func<Color, string> format, int dpi)
        {
            this.original = Color = original;
            this.showAlpha = showAlpha;
            this.format = format;
            scale = dpi / 96f;
            ToHsv(original, out hue, out saturation, out brightness);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Color picker";
            AccessibleName = "Color picker";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = AppTheme.IsDark ? AppTheme.Surface : SystemColors.Window;
            // Point sizes already follow the monitor DPI, unlike the pixel layout below.
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            LayoutParts();
        }

        private int S(float value) => (int)Math.Round(value * scale);

        private void LayoutParts()
        {
            int pad = S(8), bar = S(14), headerHeight = S(28);
            int squareWidth = S(170), squareHeight = S(130);
            var width = pad + squareWidth + pad + bar + (showAlpha ? pad + bar : 0) + pad;
            ClientSize = new Size(width + 2, 1 + headerHeight + pad + squareHeight + pad + 1);

            header = new Rectangle(1, 1, width, headerHeight);
            var originalWidth = S(52);
            current = new Rectangle(header.X, header.Y, header.Width - originalWidth, header.Height);
            originalArea = new Rectangle(current.Right, header.Y, originalWidth, header.Height);
            square = new Rectangle(1 + pad, header.Bottom + pad, squareWidth, squareHeight);
            hueBar = new Rectangle(square.Right + pad, square.Y, bar, squareHeight);
            alphaBar = new Rectangle(hueBar.Right + pad, square.Y, bar, squareHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var border = AppTheme.IsDark ? AppTheme.Border : SystemColors.ControlDark;

            // Header: new color with its hex code, original color on the right.
            if (Color.A < 255 || original.A < 255) g.FillRectangle(Checkers, header);
            using (var brush = new SolidBrush(Color)) g.FillRectangle(brush, current);
            using (var brush = new SolidBrush(original)) g.FillRectangle(brush, originalArea);
            TextRenderer.DrawText(g, format(Color), Font, Rectangle.Inflate(current, -S(8), 0),
                TextServices.ColorLiterals.IsLight(Color) ? Color.Black : Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Saturation (left to right) and brightness (top to bottom) for the current hue.
            using (var brush = new SolidBrush(FromHsv(hue, 1, 1, 255))) g.FillRectangle(brush, square);
            using (var brush = new LinearGradientBrush(Rectangle.Inflate(square, 1, 0), Color.White, Color.FromArgb(0, Color.White), LinearGradientMode.Horizontal))
                g.FillRectangle(brush, square);
            using (var brush = new LinearGradientBrush(Rectangle.Inflate(square, 0, 1), Color.FromArgb(0, Color.Black), Color.Black, LinearGradientMode.Vertical))
                g.FillRectangle(brush, square);

            using (var brush = new LinearGradientBrush(Rectangle.Inflate(hueBar, 0, 1), Color.Red, Color.Red, LinearGradientMode.Vertical))
            {
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red },
                    Positions = new[] { 0f, 1 / 6f, 2 / 6f, 3 / 6f, 4 / 6f, 5 / 6f, 1f }
                };
                g.FillRectangle(brush, hueBar);
            }

            if (showAlpha)
            {
                g.FillRectangle(Checkers, alphaBar);
                using (var brush = new LinearGradientBrush(Rectangle.Inflate(alphaBar, 0, 1),
                    Color.FromArgb(255, Color), Color.FromArgb(0, Color), LinearGradientMode.Vertical))
                    g.FillRectangle(brush, alphaBar);
            }

            using (var pen = new Pen(border))
            {
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                g.DrawLine(pen, header.Left, header.Bottom, header.Right, header.Bottom);
                g.DrawRectangle(pen, square.X - 1, square.Y - 1, square.Width + 1, square.Height + 1);
                g.DrawRectangle(pen, hueBar.X - 1, hueBar.Y - 1, hueBar.Width + 1, hueBar.Height + 1);
                if (showAlpha) g.DrawRectangle(pen, alphaBar.X - 1, alphaBar.Y - 1, alphaBar.Width + 1, alphaBar.Height + 1);
            }

            // Markers.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var radius = S(5);
            var x = square.X + saturation * (square.Width - 1);
            var y = square.Y + (1 - brightness) * (square.Height - 1);
            using (var outer = new Pen(Color.FromArgb(160, Color.Black), S(1)))
            using (var inner = new Pen(Color.White, Math.Max(1.5f, 1.5f * scale)))
            {
                g.DrawEllipse(outer, x - radius - 1, y - radius - 1, 2 * radius + 2, 2 * radius + 2);
                g.DrawEllipse(inner, x - radius, y - radius, 2 * radius, 2 * radius);
                DrawSliderMarker(g, hueBar, hue / 360f, outer, inner);
                if (showAlpha) DrawSliderMarker(g, alphaBar, 1 - Color.A / 255f, outer, inner);
            }
        }

        private void DrawSliderMarker(Graphics g, Rectangle bar, float fraction, Pen outer, Pen inner)
        {
            var y = bar.Y + fraction * (bar.Height - 1);
            var height = S(4);
            var marker = new RectangleF(bar.X - S(2), y - height / 2f, bar.Width + S(4) - 1, height);
            g.DrawRectangle(outer, marker.X - 1, marker.Y - 1, marker.Width + 2, marker.Height + 2);
            g.DrawRectangle(inner, marker.X, marker.Y, marker.Width, marker.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (originalArea.Contains(e.Location))
            {
                ToHsv(original, out hue, out saturation, out brightness);
                SetColor(original);
                return;
            }
            dragging = Rectangle.Inflate(square, S(4), S(4)).Contains(e.Location) ? Part.Square
                : Rectangle.Inflate(hueBar, S(4), S(4)).Contains(e.Location) ? Part.Hue
                : showAlpha && Rectangle.Inflate(alphaBar, S(4), S(4)).Contains(e.Location) ? Part.Alpha
                : Part.None;
            Track(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != Part.None) Track(e.Location);
            else Cursor = originalArea.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = Part.None;
        }

        private void Track(Point point)
        {
            float Fraction(int value, int start, int length) => Math.Max(0, Math.Min(1, (value - start) / (float)(length - 1)));
            switch (dragging)
            {
                case Part.Square:
                    saturation = Fraction(point.X, square.X, square.Width);
                    brightness = 1 - Fraction(point.Y, square.Y, square.Height);
                    break;
                case Part.Hue:
                    hue = 360 * Fraction(point.Y, hueBar.Y, hueBar.Height);
                    break;
                case Part.Alpha:
                    SetColor(Color.FromArgb((int)Math.Round(255 * (1 - Fraction(point.Y, alphaBar.Y, alphaBar.Height))), Color));
                    return;
                default:
                    return;
            }
            SetColor(FromHsv(hue, saturation, brightness, Color.A));
        }

        private void SetColor(Color color)
        {
            var changed = color.ToArgb() != Color.ToArgb();
            Color = color;
            Invalidate();
            if (changed) ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: Finish(false); return true;
                case Keys.Enter: Finish(true); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!closing) ClickedAway = true;
            Finish(true);
        }

        private void Finish(bool commit)
        {
            if (closing) return;
            closing = true;
            Committed = commit;
            // Closing from within Deactivate is not safe while activation is still moving elsewhere.
            BeginInvoke((Action)Close);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Font.Dispose();
            base.Dispose(disposing);
        }

        internal static void ToHsv(Color color, out float hue, out float saturation, out float brightness)
        {
            float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
            var max = Math.Max(r, Math.Max(g, b));
            var delta = max - Math.Min(r, Math.Min(g, b));
            hue = 0;
            if (delta > 0)
            {
                if (max == r) hue = 60 * ((g - b) / delta % 6);
                else if (max == g) hue = 60 * ((b - r) / delta + 2);
                else hue = 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
            }
            saturation = max == 0 ? 0 : delta / max;
            brightness = max;
        }

        internal static Color FromHsv(float hue, float saturation, float brightness, int alpha)
        {
            var h = (hue % 360 + 360) % 360 / 60;
            var chroma = brightness * saturation;
            var x = chroma * (1 - Math.Abs(h % 2 - 1));
            float r, g, b;
            switch ((int)h)
            {
                case 0: r = chroma; g = x; b = 0; break;
                case 1: r = x; g = chroma; b = 0; break;
                case 2: r = 0; g = chroma; b = x; break;
                case 3: r = 0; g = x; b = chroma; break;
                case 4: r = x; g = 0; b = chroma; break;
                default: r = chroma; g = 0; b = x; break;
            }
            var m = brightness - chroma;
            int Channel(float value) => Math.Max(0, Math.Min(255, (int)Math.Round((value + m) * 255)));
            return Color.FromArgb(alpha, Channel(r), Channel(g), Channel(b));
        }
    }
}
