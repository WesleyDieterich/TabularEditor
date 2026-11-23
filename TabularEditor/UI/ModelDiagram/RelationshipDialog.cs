using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// Creates or edits a relationship, like Power BI Desktop's "Edit relationship" dialog.
    /// Nothing changes in the model until OK; the change is then one undo step.
    /// </summary>
    internal sealed class RelationshipDialog : Form
    {
        private readonly TabularModelHandler handler;
        private readonly SingleColumnRelationship editing;
        private readonly ComboBox table1 = DropDown();
        private readonly ComboBox column1 = DropDown();
        private readonly ComboBox table2 = DropDown();
        private readonly ComboBox column2 = DropDown();
        private readonly ComboBox cardinality = DropDown();
        private readonly ComboBox crossFilter = DropDown();
        private readonly CheckBox active = new CheckBox { Text = "Make this relationship active", AutoSize = true };
        private readonly CheckBox securityBoth = new CheckBox { Text = "Apply security filter in both directions", AutoSize = true };
        private readonly CheckBox referentialIntegrity = new CheckBox { Text = "Assume referential integrity", AutoSize = true };
        private readonly Button detect = new Button { Text = "Detect from data", AutoSize = true };
        private readonly Label message = new Label { AutoSize = false, Height = 48, Dock = DockStyle.Fill };
        private CancellationTokenSource detection;
        /// <summary>Why OK cannot be accepted right now; null when it can.</summary>
        private string currentError;

        private static readonly (RelationshipCardinality Value, string Text)[] Cardinalities =
        {
            (RelationshipCardinality.ManyToOne, "Many to one (*:1)"),
            (RelationshipCardinality.OneToOne, "One to one (1:1)"),
            (RelationshipCardinality.OneToMany, "One to many (1:*)"),
            (RelationshipCardinality.ManyToMany, "Many to many (*:*)"),
        };

        private static ComboBox DropDown() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };

        private RelationshipDialog(TabularModelHandler handler, RelationshipSpec spec, SingleColumnRelationship editing)
        {
            this.handler = handler;
            this.editing = editing;
            Text = editing == null ? "New relationship" : "Edit relationship";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(560, 400);
            Padding = new Padding(14);

            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string label, Control control)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
                control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                control.Margin = new Padding(0, 3, 0, 3);
                grid.Controls.Add(control);
            }
            Row("From table:", table1);
            Row("Column:", column1);
            Row("To table:", table2);
            Row("Column:", column2);
            var cardinalityRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Height = 30 };
            detect.Margin = new Padding(8, 0, 0, 0);
            cardinality.Width = 250;
            cardinality.Margin = Padding.Empty;
            cardinalityRow.Controls.Add(cardinality);
            cardinalityRow.Controls.Add(detect);
            Row("Cardinality:", cardinalityRow);
            Row("Cross filter direction:", crossFilter);
            grid.Controls.Add(new Label());
            var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            checks.Controls.AddRange(new Control[] { active, securityBoth, referentialIntegrity });
            grid.Controls.Add(checks);
            grid.Controls.Add(new Label());
            grid.Controls.Add(message);

            var ok = new Button { Text = "OK", Width = 90, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 40, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            Controls.Add(grid);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            var tables = handler.Model.Tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray();
            table1.Items.AddRange(tables);
            table2.Items.AddRange(tables);
            table1.DisplayMember = table2.DisplayMember = column1.DisplayMember = column2.DisplayMember = "Name";
            cardinality.Items.AddRange(Cardinalities.Select(c => (object)c.Text).ToArray());
            crossFilter.Items.AddRange(new object[] { "Single", "Both" });

            table1.SelectedIndexChanged += (s, e) => FillColumns(table1, column1);
            table2.SelectedIndexChanged += (s, e) => FillColumns(table2, column2);
            foreach (var c in new[] { column1, column2, cardinality, crossFilter })
                c.SelectedIndexChanged += (s, e) => OnChanged();
            foreach (var c in new[] { active, securityBoth, referentialIntegrity })
                c.CheckedChanged += (s, e) => OnChanged();
            detect.Click += async (s, e) => await DetectAsync();
            ok.Click += (s, e) => Accept();
            FormClosed += (s, e) => detection?.Cancel();

            LoadSpec(spec);
            AppTheme.Apply(this);
        }

        /// <summary>The settings the user accepted, or null on cancel.</summary>
        public RelationshipSpec Result { get; private set; }

        /// <summary>Shows the dialog; returns the accepted settings or null.</summary>
        public static RelationshipSpec Show(IWin32Window owner, TabularModelHandler handler, RelationshipSpec spec, SingleColumnRelationship editing = null)
        {
            using (var dialog = new RelationshipDialog(handler, spec ?? new RelationshipSpec(), editing))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
        }

        private void LoadSpec(RelationshipSpec spec)
        {
            table1.SelectedItem = spec.Column1?.Table;
            column1.SelectedItem = spec.Column1;
            table2.SelectedItem = spec.Column2?.Table;
            column2.SelectedItem = spec.Column2;
            cardinality.SelectedIndex = Array.FindIndex(Cardinalities, c => c.Value == spec.Cardinality);
            crossFilter.SelectedIndex = spec.CrossFiltering == CrossFilteringBehavior.BothDirections ? 1 : 0;
            active.Checked = spec.IsActive;
            securityBoth.Checked = spec.SecurityFiltering == SecurityFilteringBehavior.BothDirections;
            referentialIntegrity.Checked = spec.RelyOnReferentialIntegrity;
            OnChanged();
        }

        private void FillColumns(ComboBox tableBox, ComboBox columnBox)
        {
            var previous = columnBox.SelectedItem as Column;
            columnBox.Items.Clear();
            if (tableBox.SelectedItem is Table table)
                columnBox.Items.AddRange(table.Columns.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
            if (previous != null && columnBox.Items.Contains(previous)) columnBox.SelectedItem = previous;
            OnChanged();
        }

        private RelationshipSpec Current() => new RelationshipSpec
        {
            Column1 = column1.SelectedItem as Column,
            Column2 = column2.SelectedItem as Column,
            Cardinality = cardinality.SelectedIndex >= 0 ? Cardinalities[cardinality.SelectedIndex].Value : RelationshipCardinality.ManyToOne,
            CrossFiltering = crossFilter.SelectedIndex == 1 ? CrossFilteringBehavior.BothDirections : CrossFilteringBehavior.OneDirection,
            SecurityFiltering = securityBoth.Checked ? SecurityFilteringBehavior.BothDirections : SecurityFilteringBehavior.OneDirection,
            IsActive = active.Checked,
            RelyOnReferentialIntegrity = referentialIntegrity.Checked,
        };

        /// <summary>Keeps dependent options consistent and shows what would stop the save.</summary>
        private void OnChanged()
        {
            // One-to-one relationships always filter both ways, as in Power BI.
            var oneToOne = cardinality.SelectedIndex >= 0 && Cardinalities[cardinality.SelectedIndex].Value == RelationshipCardinality.OneToOne;
            if (oneToOne && crossFilter.SelectedIndex != 1) crossFilter.SelectedIndex = 1;
            crossFilter.Enabled = !oneToOne;

            var both = crossFilter.SelectedIndex == 1;
            securityBoth.Enabled = both;
            if (!both && securityBoth.Checked) securityBoth.Checked = false;

            var spec = Current();
            var directQuery = spec.Column1 != null && spec.Column2 != null &&
                (RelationshipEdits.IsDirectQuery(spec.Column1.Table) || RelationshipEdits.IsDirectQuery(spec.Column2.Table));
            referentialIntegrity.Enabled = directQuery || referentialIntegrity.Checked;
            detect.Enabled = handler.IsConnected && spec.Column1 != null && spec.Column2 != null && detection == null;

            var error = RelationshipEdits.Validate(spec, editing);
            if (error == null && spec.IsActive && RelationshipEdits.ConflictsWithActiveRelationship(spec.Column1.Table, spec.Column2.Table, editing))
                error = "Another active relationship already joins these tables. Clear \"Make this relationship active\", or deactivate the other one first.";
            currentError = error;
            ShowMessage(error, isError: true);
        }

        private void ShowMessage(string text, bool isError)
        {
            message.Text = text ?? "";
            message.ForeColor = isError ? (AppTheme.IsDark ? Color.FromArgb(240, 140, 130) : Color.Firebrick)
                : (AppTheme.IsDark ? AppTheme.Muted : SystemColors.GrayText);
        }

        private async System.Threading.Tasks.Task DetectAsync()
        {
            var spec = Current();
            if (spec.Column1 == null || spec.Column2 == null) return;
            detection = new CancellationTokenSource();
            detect.Enabled = false;
            ShowMessage("Checking the data...", isError: false);
            try
            {
                var found = await RelationshipEdits.DetectCardinalityAsync(handler, spec.Column1, spec.Column2, detection.Token);
                if (IsDisposed) return;
                cardinality.SelectedIndex = Array.FindIndex(Cardinalities, c => c.Value == found);
                OnChanged();
                if (currentError == null)
                    ShowMessage("Cardinality set from the data: " + Cardinalities.First(c => c.Value == found).Text + ".", isError: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    ShowMessage("Could not check the data. New or renamed columns must be saved to the model first. " + ex.Message, isError: true);
            }
            finally
            {
                detection = null;
                if (!IsDisposed) detect.Enabled = handler.IsConnected;
            }
        }

        private void Accept()
        {
            OnChanged();
            if (currentError != null) return; // OnChanged shows the reason
            Result = Current();
            DialogResult = DialogResult.OK;
        }
    }
}
