using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.UI;

namespace TabularEditor
{
    internal static class AppMessageBox
    {
        public static DialogResult Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon) =>
            Show(null, text, title, buttons, icon);

        public static DialogResult Show(IWin32Window owner, string text, string title, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            owner = owner ?? Form.ActiveForm;
            if (SystemInformation.HighContrast)
                return MessageBox.Show(owner, text, title, buttons, icon, defaultButton);

            var dark = AppTheme.IsDark;
            var surface = dark ? AppTheme.Surface : Color.FromArgb(244, 246, 248);
            var foreground = dark ? AppTheme.Text : SystemColors.ControlText;
            var buttonSurface = dark ? AppTheme.Raised : SystemColors.Window;
            var border = dark ? AppTheme.Border : Color.FromArgb(196, 204, 214);
            var screen = owner == null ? Screen.FromPoint(Cursor.Position) : Screen.FromHandle(owner.Handle);
            var width = Math.Min(520, screen.WorkingArea.Width - 32);
            var messageWidth = width - 80;
            var font = SystemFonts.MessageBoxFont;
            var messageHeight = TextRenderer.MeasureText(text, font, new Size(messageWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
            var bodyHeight = Math.Min(Math.Max(44, messageHeight + 8), Math.Max(44, screen.WorkingArea.Height / 2));

            using (var dialog = new Form
            {
                Text = title, Font = font, ClientSize = new Size(width, bodyHeight + 77),
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent
            })
            using (var image = (icon == MessageBoxIcon.Error ? SystemIcons.Error : SystemIcons.Warning).ToBitmap())
            {
                var symbol = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.CenterImage, Bounds = new Rectangle(18, 18, 32, 32) };
                var message = new TextBox
                {
                    Text = text, ReadOnly = true, Multiline = true, TabStop = false, BorderStyle = BorderStyle.None,
                    ScrollBars = messageHeight + 8 > bodyHeight ? ScrollBars.Vertical : ScrollBars.None,
                    Bounds = new Rectangle(62, 19, messageWidth, bodyHeight), AccessibleName = "Message"
                };
                var first = CreateButton(buttons == MessageBoxButtons.YesNo ? "&Yes" : "OK",
                    buttons == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK);
                dialog.Controls.AddRange(new Control[] { symbol, message, first });
                var second = buttons == MessageBoxButtons.OK ? null :
                    CreateButton(buttons == MessageBoxButtons.YesNo ? "&No" : "Cancel",
                        buttons == MessageBoxButtons.YesNo ? DialogResult.No : DialogResult.Cancel);
                first.Location = new Point(width - (second == null ? 102 : 190), bodyHeight + 37);
                if (second != null)
                {
                    second.Location = new Point(width - 102, bodyHeight + 37);
                    dialog.Controls.Add(second);
                }
                dialog.AcceptButton = defaultButton == MessageBoxDefaultButton.Button2 && second != null ? second : first;
                dialog.CancelButton = second ?? first;

                AppTheme.Apply(dialog);
                dialog.BackColor = symbol.BackColor = message.BackColor = surface;
                message.ForeColor = foreground;
                foreach (var button in dialog.Controls.OfType<Button>())
                {
                    button.BackColor = buttonSurface;
                    button.ForeColor = foreground;
                    button.FlatAppearance.BorderColor = border;
                }
                dialog.ActiveControl = (Control)dialog.AcceptButton;
                var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                return result == DialogResult.Cancel && buttons == MessageBoxButtons.YesNo ? DialogResult.No : result;
            }
        }

        private static Button CreateButton(string text, DialogResult result) => new Button
        {
            Text = text, DialogResult = result, Size = new Size(82, 28),
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
        };
    }
}
