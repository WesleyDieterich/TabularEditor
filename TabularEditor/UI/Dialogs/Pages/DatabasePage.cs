using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TOM = Microsoft.AnalysisServices.Tabular;
using System.Globalization;
using TabularEditor.UIServices;
using AS = Microsoft.AnalysisServices;

namespace TabularEditor.UI.Dialogs.Pages
{
    public partial class DatabasePage : UserControl, IValidationPage
    {
        public event ValidationEventHandler Validation;
        public event EventHandler Accept;

        public DatabasePage()
        {
            InitializeComponent();
            dataGridView1.ColumnHeaderMouseClick += dataGridView1_ColumnHeaderMouseClick;
        }

        public bool ClearSelection { get; set; } = false;
        public string PreselectDb { get; set; } = "";

        private readonly Dictionary<DataGridViewColumn, TextBox> columnFilters = new Dictionary<DataGridViewColumn, TextBox>();
        private List<DatabaseInfo> databases;

        internal void EnableColumnFilters()
        {
            var filterPanel = new Panel { BackColor = dataGridView1.BackgroundColor };
            dataGridView1.Controls.Add(filterPanel);

            foreach (DataGridViewColumn column in dataGridView1.Columns)
            {
                if (string.IsNullOrEmpty(column.DataPropertyName)) continue;

                var filter = new TextBox
                {
                    AccessibleName = "Filter " + column.HeaderText,
                    BackColor = filterPanel.BackColor,
                    BorderStyle = BorderStyle.None
                };
                columnFilters.Add(column, filter);
                filterPanel.Controls.Add(filter);
                filter.GotFocus += (sender, args) => filterPanel.Invalidate();
                filter.LostFocus += (sender, args) => filterPanel.Invalidate();
                filter.TextChanged += (sender, args) => ApplyColumnFilters();
            }

            filterPanel.Paint += (sender, args) =>
            {
                var bottom = filterPanel.ClientSize.Height - 1;
                var right = filterPanel.ClientSize.Width - 1;
                args.Graphics.DrawLine(SystemPens.ControlLight, 0, 0, right, 0);
                args.Graphics.DrawLine(SystemPens.ControlLight, 0, bottom, right, bottom);
                args.Graphics.DrawLine(SystemPens.ControlLight, DatabaseIcon.Width - 1, 0, DatabaseIcon.Width - 1, bottom);
                foreach (var filter in columnFilters.Values)
                {
                    if (!filter.Visible) continue;

                    var columnRight = filter.Right + 1;
                    args.Graphics.DrawLine(SystemPens.ControlLight, columnRight, 0, columnRight, bottom);
                    if (filter.Focused)
                        args.Graphics.DrawRectangle(SystemPens.Highlight, filter.Left - 2, 0, filter.Width + 3, bottom);
                }
            };

            void LayoutColumnFilters()
            {
                var filterHeight = columnFilters[colName].PreferredHeight + 4;
                var headerStyle = dataGridView1.ColumnHeadersDefaultCellStyle;
                headerStyle.Padding = new Padding(0, 0, 0, filterHeight);
                dataGridView1.ColumnHeadersHeight = (headerStyle.Font ?? dataGridView1.Font).Height + 8 + filterHeight;

                var iconHeader = dataGridView1.GetCellDisplayRectangle(DatabaseIcon.Index, -1, true);
                if (iconHeader.IsEmpty) return;

                filterPanel.SetBounds(iconHeader.Left, iconHeader.Bottom - filterHeight,
                    Math.Max(0, dataGridView1.ClientSize.Width - 2 * iconHeader.Left), filterHeight);
                foreach (var entry in columnFilters)
                {
                    var bounds = dataGridView1.GetColumnDisplayRectangle(entry.Key.Index, true);
                    var left = Math.Max(bounds.Left, iconHeader.Right);
                    var width = Math.Min(bounds.Right, filterPanel.Right) - left;
                    entry.Value.Visible = width > 4;
                    if (width > 4)
                        entry.Value.SetBounds(left - filterPanel.Left + 2, 2, width - 4, entry.Value.PreferredHeight);
                }
                filterPanel.Invalidate();
            }

            dataGridView1.Scroll += (sender, args) =>
            {
                // The grid updates its displayed column rectangles after raising Scroll.
                if (args.ScrollOrientation == ScrollOrientation.HorizontalScroll && dataGridView1.IsHandleCreated)
                    dataGridView1.BeginInvoke((MethodInvoker)LayoutColumnFilters);
            };
            dataGridView1.DataBindingComplete += (sender, args) => LayoutColumnFilters();
            dataGridView1.ColumnWidthChanged += (sender, args) => LayoutColumnFilters();
            dataGridView1.ClientSizeChanged += (sender, args) => LayoutColumnFilters();
            dataGridView1.VisibleChanged += (sender, args) => LayoutColumnFilters();
            dataGridView1.FontChanged += (sender, args) => LayoutColumnFilters();
            dataGridView1.DpiChangedAfterParent += (sender, args) => LayoutColumnFilters();
            LayoutColumnFilters();
        }

        private void ApplyColumnFilters()
        {
            if (databases == null) return;

            var matches = databases.AsEnumerable();
            foreach (var entry in columnFilters)
            {
                var query = entry.Value.Text.Trim();
                if (query.Length == 0) continue;

                var getValue = GetColumnValue(entry.Key);
                matches = matches.Where(db => CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                    Convert.ToString(getValue(db), CultureInfo.CurrentCulture) ?? "",
                    query, CompareOptions.IgnoreCase) >= 0);
            }

            var selected = dataGridView1.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.DataBoundItem as DatabaseInfo;
            var list = SortDatabases(matches, GetColumnValue(sortedColumn));
            var index = list.IndexOf(selected);

            suspendSelectionChanged = true;
            dataGridView1.DataSource = list;
            dataGridView1.CurrentCell = index >= 0 ? dataGridView1.Rows[index].Cells[0] : null;
            dataGridView1.ClearSelection();
            if (index >= 0) dataGridView1.Rows[index].Selected = true;
            txtDatabaseName.Text = index >= 0 ? selected.Name : "";
            suspendSelectionChanged = false;
            OnValidation();
        }

        private TOM.Server _server;
        public TOM.Server Server
        {
            set
            {
                _server = value;
                if (_server == null)
                {
                    databases = null;
                    dataGridView1.DataSource = null;
                    dataGridView1.ClearSelection();
                }
                else
                {
                    dataGridView1.DataBindingComplete += dataGridView1_DataBindingComplete;
                    databases = DatabaseInfo.GetDatabasesFromServer(_server).OrderBy(db => db.Name).ToList();
                    dataGridView1.DataSource = databases;

                    // Default sort
                    sortedColumn = dataGridView1.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "Name");
                    sortOrder = SortOrder.Ascending;
                }
            }
            get
            {
                return _server;
            }
        }

        private void dataGridView1_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            dataGridView1.DataBindingComplete -= dataGridView1_DataBindingComplete;
            if (ClearSelection) DoClearSelection();
            var databaseList = dataGridView1.DataSource as List<DatabaseInfo>;
            if (databaseList != null && !string.IsNullOrEmpty(PreselectDb))
            {
                var index = databaseList.FirstIndexOf(db => db.Name == PreselectDb);
                if (index >= 0)
                {
                    dataGridView1.CurrentCell = dataGridView1.Rows[index].Cells[0];
                    txtDatabaseName.Text = (dataGridView1.SelectedRows[0].DataBoundItem as DatabaseInfo).Name;
                }
                else
                {
                    clearSelectionOnEnable = true;
                    txtDatabaseName.Text = PreselectDb;
                }
            }
        }

        private bool suspendAllEvents = true;
        private bool clearSelectionOnEnable = false;

        public void EnableEvents()
        {
            if (clearSelectionOnEnable) dataGridView1.ClearSelection();
            suspendAllEvents = false;
            OnValidation();
        }
        public void DisableEvents()
        {
            suspendAllEvents = true;
        }

        public bool AllowNew
        {
            set {
                pnlDatabaseID.Visible = value;
            }
            get
            {
                return pnlDatabaseID.Visible;
            }
        }

        bool suspendSelectionChanged = false;

        public void DoClearSelection()
        {
            suspendSelectionChanged = true;
            dataGridView1.ClearSelection();
            txtDatabaseName.Text = "";
            suspendSelectionChanged = false;
            OnValidation();
        }

        private void dataGridView1_SelectionChanged_1(object sender, EventArgs e)
        {
            if (suspendAllEvents) return;
            if (suspendSelectionChanged) return;

            if (dataGridView1.SelectedRows.Count == 1)
            {
                suspendSelectionChanged = true;

                txtDatabaseName.Text = (dataGridView1.SelectedRows[0].DataBoundItem as DatabaseInfo).Name;
                OnValidation();

                suspendSelectionChanged = false;
            }
        }

        private void txtDatabaseID_TextChanged(object sender, EventArgs e)
        {
            if (suspendAllEvents) return;

            if (!suspendSelectionChanged)
            {
                suspendSelectionChanged = true;

                dataGridView1.ClearSelection();
                OnValidation();

                suspendSelectionChanged = false;
            }
        }

        public string DatabaseName { get; private set; }

        bool valid = false;
        public bool IsValid => valid;

        private void OnValidation()
        {
            DatabaseName = string.IsNullOrWhiteSpace(txtDatabaseName.Text) ? null : txtDatabaseName.Text;

            var newValid = !string.IsNullOrEmpty(DatabaseName);
            if (valid != newValid)
            {
                valid = newValid;
                Validation?.Invoke(this, new ValidationEventArgs(valid));
            }
        }

        private void dataGridView1_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if(e.RowIndex >= 0)
                Accept?.Invoke(this, new EventArgs());
        }

        private void dataGridView1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept?.Invoke(this, new EventArgs());
            }
        }

        private void dataGridView1_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (Char.IsLetter(e.KeyChar))
            {
                int index = 0;
                // This works only if dataGridView1's SelectionMode property is set to FullRowSelect
                if (dataGridView1.SelectedRows.Count > 0)
                {
                    index = dataGridView1.SelectedRows[0].Index + 1;
                }
                for (int i = index; i < (dataGridView1.Rows.Count + index); i++)
                {
                    if (dataGridView1.Rows[i % dataGridView1.Rows.Count].Cells["colID"].Value.ToString().StartsWith(e.KeyChar.ToString(), true, CultureInfo.InvariantCulture))
                    {
                        foreach (var row in dataGridView1.Rows.Cast<DataGridViewRow>().Where(t => t.Selected))
                        {
                            row.Selected = false;
                        }
                        dataGridView1.Rows[i % dataGridView1.Rows.Count].Cells[0].Selected = true;
                        return; // stop looping
                    }
                }
            }

        }
        private DataGridViewColumn sortedColumn;
        private SortOrder sortOrder = SortOrder.None;

        private static Func<DatabaseInfo, object> GetColumnValue(DataGridViewColumn column)
        {
            switch (column?.DataPropertyName)
            {
                case "ID": return db => db.ID;
                case "Name": return db => db.Name;
                case "CompatibilityLevel": return db => db.CompatibilityLevel;
                case "LastUpdate": return db => db.LastUpdate;
                case "Description": return db => db.Description;
                default: return null;
            }
        }

        private List<DatabaseInfo> SortDatabases(IEnumerable<DatabaseInfo> list, Func<DatabaseInfo, object> getValue)
        {
            return (sortOrder == SortOrder.Ascending ? list.OrderBy(getValue) : list.OrderByDescending(getValue)).ToList();
        }

        private void dataGridView1_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            var column = dataGridView1.Columns[e.ColumnIndex];
            var keySelector = GetColumnValue(column);
            if (keySelector == null) return;

            if (sortedColumn == column && sortOrder == SortOrder.Ascending)
                sortOrder = SortOrder.Descending;
            else
                sortOrder = SortOrder.Ascending;

            sortedColumn = column;

            var list = dataGridView1.DataSource as List<DatabaseInfo>;
            if (list == null) return;

            if (columnFilters.Count > 0)
            {
                ApplyColumnFilters();
            }
            else
            {
                // Preserve selection
                string selectedName = null;
                if (dataGridView1.SelectedRows.Count > 0)
                {
                    selectedName = (dataGridView1.SelectedRows[0].DataBoundItem as DatabaseInfo)?.Name;
                }

                list = SortDatabases(list, keySelector);

                dataGridView1.DataSource = list;

                // Restore selection
                if (selectedName != null)
                {
                    var index = list.FindIndex(db => db.Name == selectedName);
                    if (index >= 0)
                    {
                        dataGridView1.ClearSelection();
                        if (dataGridView1.Rows.Count > index)
                        {
                            dataGridView1.Rows[index].Selected = true;
                            dataGridView1.FirstDisplayedScrollingRowIndex = index;
                            dataGridView1.CurrentCell = dataGridView1.Rows[index].Cells[0];
                        }
                    }
                }
            }

            foreach (DataGridViewColumn col in dataGridView1.Columns) col.HeaderCell.SortGlyphDirection = SortOrder.None;
            column.HeaderCell.SortGlyphDirection = sortOrder;
        }
    }

    public class DatabaseInfo
    {
        public string ID { get; set; }
        public string Name { get; set; }
        public int CompatibilityLevel { get; set; }
        public DateTime LastUpdate { get; set; }
        public string Description { get; set; }

        public static List<DatabaseInfo> GetDatabasesFromServer(TOM.Server server)
        {
            var result = new List<DatabaseInfo>();
            using (var reader = server.ExecuteReader("<Statement>SELECT * FROM $SYSTEM.DBSCHEMA_CATALOGS</Statement>", out AS.XmlaResultCollection results))
            {
                if(results != null && results.ContainsErrors)
                {
                    throw new Exception("Unable to query databases from server: " + results.GetMessageString());
                }
                while(reader.Read())
                {
                    // If multi-dimensional and tabular servers are installed side-by-side, we may have
                    // records where the COMPATIBILITY_LEVEL column is null - filter these away:
                    if (reader.IsDBNull(reader.GetOrdinal("COMPATIBILITY_LEVEL"))) continue;

                    result.Add(new DatabaseInfo
                    {
                        ID = reader.GetString(reader.GetOrdinal("DATABASE_ID")),
                        Name = reader.GetString(reader.GetOrdinal("CATALOG_NAME")),
                        CompatibilityLevel = reader.GetInt32(reader.GetOrdinal("COMPATIBILITY_LEVEL")),
                        LastUpdate = reader.GetDateTime(reader.GetOrdinal("DATE_MODIFIED")),
                        Description = reader.GetString(reader.GetOrdinal("DESCRIPTION"))
                    });
                }
            }
            return result;
        }
    }

    static class XmlaExtension
    {
        public static string GetMessageString(this AS.XmlaResultCollection xmlaResults)
        {
            return string.Join(Environment.NewLine,
                xmlaResults.OfType<AS.XmlaResult>().SelectMany(r => r.Messages.OfType<AS.XmlaMessage>()).Select(msg => msg.Description).ToArray());
        }
    }
}
