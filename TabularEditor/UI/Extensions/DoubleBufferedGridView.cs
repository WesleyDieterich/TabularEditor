using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TabularEditor.UI.Extensions
{
    public class DoubleBufferedGridView: DataGridView
    {
        public DoubleBufferedGridView() { DoubleBuffered = true; }
    }

    internal class QueryResultGrid : DoubleBufferedGridView
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (AppTheme.IsDark && HorizontalScrollBar.Visible && VerticalScrollBar.Visible)
                e.Graphics.FillRectangle(AppTheme.RaisedBrush, VerticalScrollBar.Left, HorizontalScrollBar.Top,
                    VerticalScrollBar.Width, HorizontalScrollBar.Height);
        }
    }
}
