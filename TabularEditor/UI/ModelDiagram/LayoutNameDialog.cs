using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>Asks for a layout name. The validator returns an error message, or null to accept.</summary>
    internal sealed class LayoutNameDialog : Form
    {
        private readonly TextBox name = new TextBox { Dock = DockStyle.Top };
        private readonly Label error = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 22, ForeColor = Color.Firebrick };
        private readonly Func<string, string> validate;

        private LayoutNameDialog(string title, string initial, Func<string, string> validate)
        {
            this.validate = validate;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            ClientSize = new Size(340, 118);
            Padding = new Padding(12);
            Font = new Font("Segoe UI", 9f);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(error);
            Controls.Add(name);
            Controls.Add(new Label { Text = "Layout name:", Dock = DockStyle.Top, Height = 20 });
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            name.Text = initial;
            name.TextChanged += (s, e) => error.Text = "";
            ok.Click += (s, e) =>
            {
                var message = validate(name.Text);
                if (message == null) DialogResult = DialogResult.OK;
                else
                {
                    error.Text = message;
                    name.Focus();
                }
            };
            Shown += (s, e) => name.SelectAll();
            AppTheme.Apply(this);
        }

        public static string Ask(IWin32Window owner, string title, string initial, Func<string, string> validate)
        {
            using (var dialog = new LayoutNameDialog(title, initial, validate))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.name.Text.Trim() : null;
        }
    }
}
