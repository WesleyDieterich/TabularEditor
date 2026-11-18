using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabularEditor.UI
{
    internal sealed class DarkSelectorChrome : NativeWindow, IDisposable
    {
        private const int Paint = 0x000F, EraseBackground = 0x0014, NonClientPaint = 0x0085, Print = 0x0317, PrintClient = 0x0318;
        private static readonly Pen FocusBorder = new Pen(Color.FromArgb(105, 133, 162));
        private static readonly Pen Arrow = new Pen(AppTheme.Muted);
        private static readonly Brush Field = new SolidBrush(AppTheme.Window);
        private readonly ComboBox selector;

        internal DarkSelectorChrome(ComboBox selector)
        {
            this.selector = selector;
            selector.HandleCreated += Attach;
            selector.SelectedIndexChanged += Redraw;
            selector.EnabledChanged += Redraw;
            selector.GotFocus += Redraw;
            selector.LostFocus += Redraw;
            selector.DropDown += Redraw;
            selector.DropDownClosed += Redraw;
            if (selector.IsHandleCreated) Attach(selector, EventArgs.Empty);
        }

        private void Attach(object sender, EventArgs e)
        {
            AssignHandle(selector.Handle);
            selector.Invalidate();
        }

        public void Dispose()
        {
            selector.HandleCreated -= Attach;
            selector.SelectedIndexChanged -= Redraw;
            selector.EnabledChanged -= Redraw;
            selector.GotFocus -= Redraw;
            selector.LostFocus -= Redraw;
            selector.DropDown -= Redraw;
            selector.DropDownClosed -= Redraw;
            ReleaseHandle();
        }

        private void Redraw(object sender, EventArgs e) => selector.Invalidate();

        protected override void WndProc(ref Message message)
        {
            // List-only selectors have no edit child, so paint the complete frame in one pass.
            if (selector.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                switch (message.Msg)
                {
                    case EraseBackground:
                        message.Result = new IntPtr(1);
                        return;
                    case NonClientPaint:
                        return;
                    case Paint:
                        DarkControlPaint.Paint(Handle, selector.ClientRectangle, Draw);
                        return;
                    case Print:
                    case PrintClient:
                        if (message.WParam != IntPtr.Zero)
                            using (var graphics = Graphics.FromHdc(message.WParam)) Draw(graphics);
                        return;
                }
            }

            base.WndProc(ref message);
            if (Handle == IntPtr.Zero || !selector.Visible) return;

            if ((message.Msg == Print || message.Msg == PrintClient) && message.WParam != IntPtr.Zero)
            {
                using (var graphics = Graphics.FromHdc(message.WParam)) Draw(graphics);
            }
            else if (message.Msg == Paint || message.Msg == NonClientPaint)
            {
                var hdc = GetWindowDC(Handle);
                if (hdc == IntPtr.Zero) return;
                try
                {
                    using (var graphics = Graphics.FromHdc(hdc)) Draw(graphics);
                }
                finally
                {
                    ReleaseDC(Handle, hdc);
                }
            }
        }

        private void Draw(Graphics graphics)
        {
            var width = selector.Width;
            var height = selector.Height;
            if (width < 10 || height < 10) return;

            var listOnly = selector.DropDownStyle == ComboBoxStyle.DropDownList;
            if (listOnly) graphics.FillRectangle(Field, 0, 0, width, height);
            var info = new ComboBoxInfo { Size = Marshal.SizeOf(typeof(ComboBoxInfo)) };
            if (GetComboBoxInfo(Handle, ref info) && info.ButtonRight > info.ButtonLeft)
            {
                var left = Math.Max(1, Math.Min(info.ButtonLeft, width - 2));
                var button = new Rectangle(left, 1, width - left - 1, height - 2);
                if (listOnly)
                {
                    var textArea = new Rectangle(4, 1, Math.Max(0, left - 6), height - 2);
                    TextRenderer.DrawText(graphics, selector.GetItemText(selector.SelectedItem), selector.Font, textArea,
                        selector.Enabled ? AppTheme.Text : AppTheme.Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                graphics.FillRectangle(selector.DroppedDown ? AppTheme.SelectionBrush : AppTheme.RaisedBrush, button);
                var x = button.Left + button.Width / 2;
                var y = button.Top + button.Height / 2;
                graphics.DrawLines(selector.Enabled ? Arrow : AppTheme.BorderPen,
                    new[] { new Point(x - 3, y - 1), new Point(x, y + 2), new Point(x + 3, y - 1) });
            }

            graphics.DrawRectangle(selector.ContainsFocus ? FocusBorder : AppTheme.BorderPen, 0, 0, width - 1, height - 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ComboBoxInfo
        {
            internal int Size, ItemLeft, ItemTop, ItemRight, ItemBottom;
            internal int ButtonLeft, ButtonTop, ButtonRight, ButtonBottom, ButtonState;
            internal IntPtr ComboHandle, EditHandle, ListHandle;
        }

        [DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboBoxInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    }
}
