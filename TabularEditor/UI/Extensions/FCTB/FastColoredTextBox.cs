using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TabularEditor.UI.Extensions.FCTB
{
    public class FastColoredTextBox: FastColoredTextBoxNS.FastColoredTextBox
    {
        ReplaceForm betterReplaceForm;

        public FastColoredTextBox()
        {
            // Match the gap between the line-number divider and the text.
            Paddings = new Padding(0, 7, 0, 0);
        }

        public override void ShowReplaceDialog(string findText)
        {
            if (ReadOnly)
                return;
            if (betterReplaceForm == null)
                betterReplaceForm = new ReplaceForm(this);

            if (findText != null)
                betterReplaceForm.tbFind.Text = findText;
            else if (!Selection.IsEmpty && Selection.Start.iLine == Selection.End.iLine)
                betterReplaceForm.tbFind.Text = Selection.Text;

            betterReplaceForm.tbFind.SelectAll();
            betterReplaceForm.Show();
            betterReplaceForm.Focus();
        }

        public void ResetSearch()
        {
            betterReplaceForm.ResetSearch();
        }

        public override void ShowReplaceDialog()
        {
            this.ShowReplaceDialog(null);
        }

        public override void Cut()
        {
            base.Cut();
            OnTextChanged();
        }
    }
}
