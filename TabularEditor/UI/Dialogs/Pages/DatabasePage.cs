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

        internal event EventHandler ModelListChanged;
        internal int VisibleModelCount => dataGridView1.Rows.Count;
        internal int TotalModelCount => databases?.Count ?? 0;

        internal void StyleModelPicker()
        {
            var grid = dataGridView1;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.DefaultCellStyle.Padding = new Padding(7, 0, 7, 0);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(7, 0, 7, 0);
            DatabaseIcon.Visible = false;

            if (!SystemInformation.HighContrast)
            {
                var header = Color.FromArgb(232, 239, 247);
                grid.ColumnHeadersDefaultCellStyle.BackColor = header;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = header;
                grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 249, 252);
                grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 235, 249);
                grid.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
                grid.GridColor = Color.FromArgb(232, 239, 247);
            }

            var scale = DeviceDpi / 96.0;
            grid.RowTemplate.Height = Math.Max(grid.Font.Height + 10, (int)Math.Round(28 * scale));
            colName.Width = (int)Math.Round(264 * scale);
            CompatibilityLevel.Width = (int)Math.Round(116 * scale);
            colLastUpdate.Width = (int)Math.Round(147 * scale);
            colDescription.Width = (int)Math.Round(182 * scale);
            colID.Width = (int)Math.Round(200 * scale);
            colName.DisplayIndex = 1;
            colID.DisplayIndex = grid.Columns.Count - 1;
            grid.DataBindingComplete += (sender, args) => ModelListChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void EnableColumnFilters()
        {
            var filterPanel = new Panel { BackColor = dataGridView1.BackgroundColor, TabIndex = 0 };
            dataGridView1.Controls.Add(filterPanel);
            var verticalScrollBar = dataGridView1.Controls.OfType<VScrollBar>().FirstOrDefault();

            foreach (var column in dataGridView1.Columns.Cast<DataGridViewColumn>().OrderBy(item => item.DisplayIndex))
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

            var inset = dataGridView1.DefaultCellStyle.Padding.Left;
            filterPanel.Paint += (sender, args) =>
            {
                args.Graphics.Clear(dataGridView1.ColumnHeadersDefaultCellStyle.BackColor);
                var border = AppTheme.IsDark ? AppTheme.BorderPen : SystemPens.ControlLight;
                foreach (var filter in columnFilters.Values)
                {
                    if (!filter.Visible) continue;

                    var field = Rectangle.Inflate(filter.Bounds, 4, 2);
                    using (var background = new SolidBrush(filter.BackColor))
                        args.Graphics.FillRectangle(background, field);
                    var outline = filter.Focused
                        ? AppTheme.IsDark ? AppTheme.AccentPen : SystemPens.Highlight
                        : border;
                    args.Graphics.DrawRectangle(outline, field.X, field.Y, field.Width - 1, field.Height - 1);
                }
            };

            void LayoutColumnFilters()
            {
                if (IsDisposed || Disposing || dataGridView1.IsDisposed || dataGridView1.Disposing) return;

                var filterHeight = columnFilters[colName].PreferredHeight + 11;
                var headerStyle = dataGridView1.ColumnHeadersDefaultCellStyle;
                headerStyle.Padding = new Padding(inset, 0, inset, filterHeight);
                dataGridView1.ColumnHeadersHeight = (headerStyle.Font ?? dataGridView1.Font).Height + 9 + filterHeight;

                var right = verticalScrollBar?.Visible == true ? verticalScrollBar.Left : dataGridView1.ClientSize.Width;
                filterPanel.SetBounds(0, dataGridView1.ColumnHeadersHeight - filterHeight,
                    Math.Max(0, right), filterHeight);
                foreach (var entry in columnFilters)
                {
                    var bounds = dataGridView1.GetColumnDisplayRectangle(entry.Key.Index, true);
                    var left = Math.Max(bounds.Left, 0);
                    var width = Math.Min(bounds.Right, filterPanel.Right) - left;
                    entry.Value.Visible = width > 2 * inset;
                    if (width > 2 * inset)
                        entry.Value.SetBounds(left + inset, 4, width - 2 * inset, entry.Value.PreferredHeight);
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
            if (verticalScrollBar != null)
            {
                verticalScrollBar.VisibleChanged += (sender, args) => LayoutColumnFilters();
                verticalScrollBar.LocationChanged += (sender, args) => LayoutColumnFilters();
            }
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
            dataGridView1.CurrentCell = index >= 0 ? dataGridView1.Rows[index].Cells[colName.Index] : null;
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
                    dataGridView1.CurrentCell = dataGridView1.Rows[index].Cells[colName.Index];
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
                        dataGridView1.Rows[i % dataGridView1.Rows.Count].Selected = true;
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
