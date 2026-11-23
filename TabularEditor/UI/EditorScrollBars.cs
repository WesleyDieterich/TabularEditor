using System;
using System.Windows.Forms;
using FastColoredTextBox = TabularEditor.UI.Extensions.FCTB.FastColoredTextBox;

namespace TabularEditor.UI
{
    /// <summary>
    /// Replaces a code editor's native window scrollbars, which the dark theme cannot paint, with
    /// ScrollBar controls docked beside it. AppTheme themes those like every other ScrollBar.
    /// The editor stays where it is in its parent, so code that relies on editor.Parent keeps working.
    /// </summary>
    internal static class EditorScrollBars
    {
        public static void Attach(FastColoredTextBox editor)
        {
            var parent = editor.Parent ?? throw new InvalidOperationException("Add the editor to its parent first.");
            var vertical = new VScrollBar { Dock = DockStyle.Right, Visible = false };
            var horizontal = new HScrollBar { Dock = DockStyle.Bottom, Visible = false };
            editor.ShowScrollBars = false;

            // Docking runs from the back of the z-order to the front. Placing the bars directly behind
            // the editor keeps toolbars docked in the same parent outside them, lets the vertical bar
            // take the full height, and leaves the rest for the editor.
            parent.Controls.Add(vertical);
            parent.Controls.Add(horizontal);
            var index = parent.Controls.GetChildIndex(editor);
            parent.Controls.SetChildIndex(vertical, index);
            parent.Controls.SetChildIndex(horizontal, index);
            parent.Controls.SetChildIndex(editor, index);

            var relayoutPending = false;
            void Relayout()
            {
                relayoutPending = false;
                if (!parent.IsDisposed) parent.PerformLayout();
            }

            void Sync(object sender, EventArgs e)
            {
                if (editor.IsDisposed) return;
                var changed = Update(vertical, editor.VerticalScroll, editor.ClientSize.Height, editor.CharHeight, editor);
                changed |= Update(horizontal, editor.HorizontalScroll, editor.ClientSize.Width, editor.CharWidth, editor);

                // Sync often runs while the parent is laying out (the editor's SizeChanged, e.g. after a
                // zoom). WinForms drops the layout request a bar's Visible change makes at that point, so
                // the editor kept covering a newly shown horizontal bar. Lay out again once that pass ends.
                if (changed && !relayoutPending && parent.IsHandleCreated)
                {
                    relayoutPending = true;
                    parent.BeginInvoke((Action)Relayout);
                }
            }

            void Scroll(object sender, ScrollEventArgs e)
            {
                var dragging = e.Type == ScrollEventType.ThumbTrack || e.Type == ScrollEventType.ThumbPosition;
                editor.OnScroll(e, !dragging);
            }

            vertical.Scroll += Scroll;
            horizontal.Scroll += Scroll;
            editor.ScrollbarsUpdated += Sync;
            editor.SizeChanged += Sync;
            editor.VisibleChanged += Sync;
            editor.EnabledChanged += Sync;
            Sync(editor, EventArgs.Empty);
        }

        /// <returns>True when the bar was shown or hidden.</returns>
        private static bool Update(ScrollBar bar, ScrollProperties scroll, int page, int step, Control editor)
        {
            var wasVisible = bar.Visible;
            // With native scrollbars hidden the editor still reports its range: Maximum is the
            // largest scroll offset, 0 when everything fits.
            var range = Math.Max(0, scroll.Maximum);
            var large = Math.Max(1, page);
            bar.Minimum = 0;
            bar.LargeChange = large;
            bar.SmallChange = Math.Max(1, step);
            bar.Maximum = range + large - 1;
            bar.Value = Math.Max(0, Math.Min(scroll.Value, range));
            bar.Enabled = editor.Enabled;
            bar.Visible = range > 0 && editor.Visible;
            return bar.Visible != wasVisible;
        }
    }
}
