using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.UI.Dialogs
{
    public partial class SelectDatabaseForm : Form
    {
        public SelectDatabaseForm()
        {
            InitializeComponent();
            var font = FormMain.CreateWorkspaceFont("Segoe UI Variable Text", "Segoe UI", 9.5f);
            var titleFont = new Font(font.FontFamily, 13f, FontStyle.Bold);
            Font = font;
            lblTitle.Font = titleFont;
            BackColor = SystemColors.Window;
            Disposed += (sender, args) =>
            {
                titleFont.Dispose();
                font.Dispose();
            };
            databasePage1.StyleModelPicker();
            databasePage1.EnableColumnFilters();
            databasePage1.ModelListChanged += (sender, args) => UpdateModelCount();
            UpdateModelCount();
        }

        private void UpdateModelCount()
        {
            var visible = databasePage1.VisibleModelCount;
            var total = databasePage1.TotalModelCount;
            lblModelCount.Text = visible == total
                ? total == 1 ? "1 model" : $"{total:N0} models"
                : $"{visible:N0} of {total:N0} models";
        }

        public static DialogResult Show(Server server)
        {
            using (var form = new SelectDatabaseForm())
            {
                form.databasePage1.EnableEvents();
                form.databasePage1.Server = server;
                var result = form.ShowDialog();
                DatabaseName = form.databasePage1.DatabaseName;
                return result;
            }
        }

        public static string DatabaseName;

        private void databasePage1_Accept(object sender, EventArgs e)
        {
            btnOK.PerformClick();
        }

        private void databasePage1_Validation(object sender, ValidationEventArgs e)
        {
            btnOK.Enabled = e.IsValid;
        }
    }
}
