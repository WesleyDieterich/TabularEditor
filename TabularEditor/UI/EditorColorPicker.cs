using System;
using System.Drawing;
using System.Windows.Forms;
using FastColoredTextBoxNS;
using TabularEditor.TextServices;
using FastColoredTextBox = TabularEditor.UI.Extensions.FCTB.FastColoredTextBox;

namespace TabularEditor.UI
{
    /// <summary>
    /// Ctrl+click on a hex color in a DAX string (drawn by <see cref="ColorChipStyle"/>) opens a
    /// <see cref="ColorPickerPopup"/>. The text follows the picker live; a whole picker session is a
    /// single undo step, and Esc puts the original color back.
    /// </summary>
    internal static class EditorColorPicker
    {
        public static void Attach(FastColoredTextBox editor)
        {
            if (editor == null) return;
            Point? pressedAt = null;

            editor.ToolTipNeeded += (sender, args) =>
            {
                if (!editor.Enabled || editor.ReadOnly) return;
                var point = editor.PointToClient(Control.MousePosition);
                if (editor.ClientRectangle.Contains(point) && TryGetColorAt(editor, point, out _))
                    args.ToolTipText = "Ctrl+click to open the color picker";
            };

            editor.MouseDown += (sender, args) =>
                pressedAt = args.Button == MouseButtons.Left && CtrlOnly ? args.Location : (Point?)null;

            editor.MouseUp += (sender, args) =>
            {
                var pressed = pressedAt;
                pressedAt = null;
                if (pressed == null || args.Button != MouseButtons.Left || !CtrlOnly) return;
                var drag = SystemInformation.DragSize;
                if (Math.Abs(args.X - pressed.Value.X) > drag.Width || Math.Abs(args.Y - pressed.Value.Y) > drag.Height) return;
                if (TryGetColorAt(editor, args.Location, out var range)) Open(editor, range);
            };

            // Show a hand over a color while Ctrl is held, like a link.
            var hand = false;
            var defaultCursor = editor.Cursor;
            editor.MouseMove += (sender, args) =>
            {
                var overColor = args.Button == MouseButtons.None && CtrlOnly && TryGetColorAt(editor, args.Location, out _);
                if (overColor == hand) return;
                hand = overColor;
                editor.Cursor = hand ? Cursors.Hand : defaultCursor;
            };
            editor.MouseLeave += (sender, args) =>
            {
                if (!hand) return;
                hand = false;
                editor.Cursor = defaultCursor;
            };
        }

        private static bool CtrlOnly => (Control.ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt)) == Keys.Control;

        /// <summary>Finds the hex color drawn as a chip under the given point, if any.</summary>
        internal static bool TryGetColorAt(FastColoredTextBox editor, Point point, out Range range)
        {
            range = null;
            var mask = editor.GetStyleIndexMask(new Style[] { ColorChipStyle.Instance });
            if (mask == StyleIndex.None) return false;

            var place = editor.PointToPlace(point);
            if (place.iLine < 0 || place.iLine >= editor.LinesCount) return false;
            var line = editor[place.iLine];
            // PointToPlace snaps to the nearest caret position: left of it means the previous character.
            if (place.iChar > 0 && editor.PlaceToPoint(place).X > point.X) place.iChar--;
            if (place.iChar >= line.Count || (line[place.iChar].style & mask) == 0) return false;
            var top = editor.PlaceToPoint(new Place(place.iChar, place.iLine)).Y;
            if (point.Y < top || point.Y >= top + editor.CharHeight) return false;

            var start = place.iChar;
            while (start > 0 && line[start].c != '#' && (line[start - 1].style & mask) != 0) start--;
            var end = start + 1;
            while (end < line.Count && line[end].c != '#' && (line[end].style & mask) != 0) end++;

            range = new Range(editor, start, place.iLine, end, place.iLine);
            return ColorLiterals.TryParse(range.Text, out _);
        }

        private static void Open(FastColoredTextBox editor, Range range)
        {
            if (editor.ReadOnly || !ColorLiterals.TryParse(range.Text, out var color)) return;

            var original = range.Text;
            var start = editor.PlaceToPosition(range.Start);
            var anchor = editor.PlaceToPoint(range.Start);
            var written = original;

            var popup = new ColorPickerPopup(color, original.Length == 9, c => ColorLiterals.Format(c, original), editor.DeviceDpi);

            void Write(string text)
            {
                // Undo the previous step first, so the history only ever holds one edit from this picker.
                if (written != original) editor.Undo();
                written = original;
                if (text != original)
                {
                    editor.Selection = editor.GetRange(start, start + original.Length);
                    editor.InsertText(text);
                    written = text;
                }
                // Tint the text now: edits drop its style, and the highlighter may only run after a delay.
                editor.GetRange(start, start + written.Length).SetStyle(ColorChipStyle.Instance);
            }

            popup.ColorChanged += (sender, args) => Write(ColorLiterals.Format(popup.Color, original));
            popup.FormClosed += (sender, args) =>
            {
                if (!popup.Committed) Write(original);
                popup.Dispose();
                // Clicking elsewhere already put the focus and caret where the user wanted them.
                if (editor.IsDisposed || popup.ClickedAway) return;
                var caret = editor.PositionToPlace(start + written.Length);
                editor.Selection = new Range(editor, caret, caret);
                editor.Focus();
            };

            var area = Screen.FromControl(editor).WorkingArea;
            var below = editor.PointToScreen(new Point(anchor.X, anchor.Y + editor.CharHeight + 2));
            var above = editor.PointToScreen(new Point(anchor.X, anchor.Y - popup.Height - 2));
            var y = below.Y + popup.Height <= area.Bottom ? below.Y : above.Y;
            popup.Location = new Point(
                Math.Max(area.Left, Math.Min(below.X, area.Right - popup.Width)),
                Math.Max(area.Top, Math.Min(y, area.Bottom - popup.Height)));
            popup.Show(editor.FindForm());
        }
    }
}
