using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TextServices;
using TabularEditor.UI.Extensions;
using TabularEditor.UIServices;
using CodeEditor = TabularEditor.UI.Extensions.FCTB.FastColoredTextBox;
using TabularModelHandler = TabularEditor.TOMWrapper.TabularModelHandler;

namespace TabularEditor.UI
{
    internal sealed class DaxQueryEditor : UserControl
    {
        private readonly Func<TabularModelHandler> getHandler;
        private readonly CodeEditor editor;
        private readonly DaxIntelliSense intelliSense;
        private readonly SplitContainer split;
        private readonly DataGridView grid;
        private readonly ToolStripComboBox resultSelector;
        private readonly ToolStripLabel rowCount;
        private readonly Dictionary<int, HashSet<object>> filters = new Dictionary<int, HashSet<object>>();
        private readonly Dictionary<int, ColumnCondition> conditions = new Dictionary<int, ColumnCondition>();
        private int sortColumn = -1;
        private SortOrder sortOrder = SortOrder.None;
        private readonly Timer highlight = new Timer { Interval = 200 };
        private DataSet results;
        private DataTable table;
        private DataRow[] visibleRows = Array.Empty<DataRow>();
        private bool resultsPaneSized;
        private string executedQuery = string.Empty;
        private TimeSpan executionTime;
        private Form filterPopup;

        public DaxQueryEditor(Func<TabularModelHandler> getHandler)
        {
            this.getHandler = getHandler;
            Dock = DockStyle.Fill;
            split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6,
                Size = new Size(640, 450), Panel1MinSize = 60, Panel2MinSize = 60, Panel2Collapsed = true
            };
            var codeFont = FormMain.CreateWorkspaceFont("Cascadia Mono", "Consolas", 9.75f);
            editor = new CodeEditor
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, AutoIndent = true,
                AutoIndentExistingLines = false, AutoIndentChars = false,
                Font = codeFont
            };
            intelliSense = new DaxIntelliSense(editor, () => true, () => getHandler()?.Model, queryEditor: true);
            highlight.Tick += (sender, args) =>
            {
                highlight.Stop();
                if (editor.IsHandleCreated) ExpressionParser.SyntaxHighlight(editor);
            };
            editor.TextChanged += (sender, args) => ScheduleHighlight();
            editor.VisibleChanged += (sender, args) => { if (editor.Visible) ScheduleHighlight(); };
            editor.Text = "EVALUATE\r\n";

            var commands = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            commands.Items.Add("Run (F5)", Resources.Run, (sender, args) => Execute()).ImageTransparentColor = Color.Magenta;
            split.Panel1.Controls.Add(editor);
            split.Panel1.Controls.Add(commands);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                AllowUserToResizeColumns = true, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ShowCellToolTips = true
            };
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.RowTemplate.Height += (int)Math.Round(4 * DeviceDpi / 96.0);
            grid.CellValueNeeded += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.RowIndex < visibleRows.Length &&
                    args.ColumnIndex >= 0 && args.ColumnIndex < visibleRows[args.RowIndex].Table.Columns.Count)
                    args.Value = visibleRows[args.RowIndex][args.ColumnIndex];
            };
            grid.CellPainting += PaintHeaderFilter;
            grid.CellMouseDown += OpenHeaderFilter;
            resultSelector = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
            resultSelector.SelectedIndexChanged += (sender, args) =>
            {
                if (results != null && resultSelector.SelectedIndex >= 0)
                    ShowTable(results.Tables[resultSelector.SelectedIndex]);
            };
            rowCount = new ToolStripLabel();
            var resultCommands = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var exportButton = new Button
            {
                Text = "Export to Excel",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 3, 10, 3),
                UseVisualStyleBackColor = true
            };
            exportButton.Click += (sender, args) => Export();
            resultCommands.Items.Add(new ToolStripControlHost(exportButton)
            {
                Margin = new Padding(4, 3, 8, 3)
            });
            resultCommands.Items.Add(resultSelector);
            resultCommands.Items.Add(rowCount);
            resultCommands.Items.Add(new ToolStripButton("Hide results", null, (sender, args) => split.Panel2Collapsed = true)
            {
                Alignment = ToolStripItemAlignment.Right
            });
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(resultCommands);
            Controls.Add(split);

            Disposed += (sender, args) =>
            {
                highlight.Dispose();
                results?.Dispose();
                codeFont.Dispose();
            };
        }

        public void ApplyTheme()
        {
            intelliSense.ApplyTheme();
            ScheduleHighlight();
        }

        public void Find() => editor.ShowFindDialog();
        public void Replace() => editor.ShowReplaceDialog();
        public bool EditorContainsFocus => editor.ContainsFocus;
        public bool CanComment => editor.ContainsFocus && editor.TextLength > 0;
        public void Comment() => editor.InsertLinePrefix("//");
        public void Uncomment() => editor.RemoveLinePrefix("//");

        private string QueryText => editor.Selection.IsEmpty ? editor.Text : editor.SelectedText;

        private void ScheduleHighlight()
        {
            highlight.Stop();
            highlight.Start();
        }

        public void ClearResults()
        {
            filterPopup?.Close();
            split.Panel2Collapsed = true;
            grid.RowCount = 0;
            grid.Columns.Clear();
            table = null;
            visibleRows = Array.Empty<DataRow>();
            filters.Clear();
            conditions.Clear();
            sortColumn = -1;
            sortOrder = SortOrder.None;
            results?.Dispose();
            results = null;
            resultSelector.Items.Clear();
            rowCount.Text = string.Empty;
            executedQuery = string.Empty;
            executionTime = TimeSpan.Zero;
        }

        public void Execute()
        {
            var handler = getHandler();
            if (handler?.IsConnected != true)
            {
                MessageBox.Show(this, "Connect to a semantic model before running a DAX query.", "DAX Script",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var query = QueryText;
            if (Preferences.Current.UseSemicolonsAsSeparators) query = ExpressionParser.SemicolonsToCommas(query);
            var lexer = new DAXLexer(new DAXCharStream(query, false));
            lexer.RemoveErrorListeners();
            var firstToken = lexer.GetDefaultTokens().FirstOrDefault()?.Text;
            if (!string.Equals(firstToken, "EVALUATE", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(firstToken, "DEFINE", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Select or enter a DAX query that starts with EVALUATE or DEFINE.", "DAX Script",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            intelliSense.Close();
            DataSet data;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // AMO shares the live session with the model editor. Execute on its owning thread.
                using (new Hourglass())
                    data = handler.Model.Database.ExecuteDax(query);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "DAX query failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            stopwatch.Stop();
            ShowResults(data, query, stopwatch.Elapsed);
        }

        private void ShowResults(DataSet data, string query, TimeSpan elapsed)
        {
            ClearResults();
            executedQuery = query;
            executionTime = elapsed;
            results = data;
            for (var i = 0; i < results.Tables.Count; i++) resultSelector.Items.Add($"Result {i + 1}");
            resultSelector.Visible = results.Tables.Count > 1;
            if (results.Tables.Count > 0) resultSelector.SelectedIndex = 0;
            else rowCount.Text = "No result sets";
            split.Panel2Collapsed = false;
            if (!resultsPaneSized && split.Height >= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth)
            {
                split.SplitterDistance = Math.Min(split.Height * 3 / 5, split.Height - split.Panel2MinSize - split.SplitterWidth);
                resultsPaneSized = true;
            }
        }

        private void ShowTable(DataTable result)
        {
            filterPopup?.Close();
            grid.RowCount = 0;
            grid.Columns.Clear();
            filters.Clear();
            conditions.Clear();
            sortColumn = -1;
            sortOrder = SortOrder.None;
            table = result;
            foreach (DataColumn column in table.Columns)
            {
                var name = DaxQueryWorkbook.ColumnName(column.ColumnName);
                var width = TextRenderer.MeasureText(name, grid.Font).Width + 42;
                var gridColumn = new DataGridViewTextBoxColumn
                {
                    HeaderText = name, ValueType = column.DataType,
                    Width = Math.Min(340, Math.Max(110, width)), MinimumWidth = 42,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                };
                var format = ResultFormat(column);
                column.ExtendedProperties["DaxFormat"] = format;
                gridColumn.DefaultCellStyle.Format = DisplayFormat(format);
                gridColumn.DefaultCellStyle.Alignment = IsNumeric(column)
                    ? DataGridViewContentAlignment.MiddleRight
                    : DataGridViewContentAlignment.MiddleLeft;
                var cultureName = getHandler()?.Model.Culture;
                gridColumn.DefaultCellStyle.FormatProvider = string.IsNullOrEmpty(cultureName)
                    ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(cultureName);
                gridColumn.HeaderCell.Style.Padding = new Padding(0, 0, FilterWidth + 12, 0);
                grid.Columns.Add(gridColumn);
            }
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            var rows = table.Rows.Cast<DataRow>()
                .Where(row => filters.All(filter => filter.Value.Contains(row[filter.Key])) &&
                    conditions.All(condition => condition.Value.Matches(row[condition.Key])));
            var comparer = Comparer<object>.Create(CompareValues);
            if (sortOrder == SortOrder.Ascending) rows = rows.OrderBy(row => row[sortColumn], comparer);
            if (sortOrder == SortOrder.Descending) rows = rows.OrderByDescending(row => row[sortColumn], comparer);
            var nextRows = rows.ToArray();
            // Clearing the current rows can validate the old selection and request its values.
            // Keep the old data available until the grid has finished removing those rows.
            grid.CurrentCell = null;
            grid.RowCount = 0;
            visibleRows = nextRows;
            grid.RowCount = visibleRows.Length;
            grid.Invalidate();
            var count = visibleRows.Length == table.Rows.Count
                ? $"{visibleRows.Length:N0} rows"
                : $"{visibleRows.Length:N0} of {table.Rows.Count:N0} rows";
            rowCount.Text = $"{count}  ·  {executionTime.TotalSeconds:N3} s";
        }

        private int FilterWidth => Math.Max(16, 19 * DeviceDpi / 96);

        private static int CompareValues(object left, object right)
        {
            if (left == DBNull.Value) return right == DBNull.Value ? 0 : -1;
            if (right == DBNull.Value) return 1;
            if (left is string text) return StringComparer.CurrentCultureIgnoreCase.Compare(text, (string)right);
            return ((IComparable)left).CompareTo(right);
        }

        private static string DisplayFormat(string format)
        {
            switch (format.ToUpperInvariant())
            {
                case "GENERAL NUMBER": return "G";
                case "CURRENCY": return "C";
                case "FIXED": return "F2";
                case "STANDARD": return "N2";
                case "PERCENT": return "P2";
                case "SCIENTIFIC": return "E2";
                case "GENERAL DATE": return "G";
                case "LONG DATE": return "D";
                case "SHORT DATE": return "d";
                default: return format;
            }
        }

        private string ResultFormat(DataColumn column)
        {
            var model = getHandler()?.Model;
            if (model == null) return string.Empty;
            var name = DaxQueryWorkbook.ColumnName(column.ColumnName);
            var measureName = name;
            // Named expressions such as "Receita", [Valor Receita] retain the measure's format.
            foreach (Match alias in Regex.Matches(executedQuery,
                "\"(?<alias>(?:[^\"]|\"\")*)\"\\s*,\\s*\\[(?<measure>(?:[^\\]]|\\]\\])+)\\]",
                RegexOptions.CultureInvariant))
                if (alias.Groups["alias"].Value.Replace("\"\"", "\"") == name)
                    measureName = alias.Groups["measure"].Value.Replace("]]", "]");
            var measure = model.Tables.SelectMany(t => t.Measures).FirstOrDefault(m => m.Name == measureName);
            if (measure != null) return measure.FormatString ?? string.Empty;
            var source = model.Tables.SelectMany(t => t.Columns)
                .FirstOrDefault(c => c.DaxObjectFullName == column.ColumnName ||
                    c.Table.Name + "[" + c.Name.Replace("]", "]]") + "]" == column.ColumnName);
            return source?.FormatString ?? string.Empty;
        }

        private Rectangle FilterButton(Rectangle header) =>
            new Rectangle(header.Right - FilterWidth - 1, header.Top, FilterWidth, header.Height);

        private void PaintHeaderFilter(object sender, DataGridViewCellPaintingEventArgs args)
        {
            if (args.RowIndex != -1 || args.ColumnIndex < 0) return;
            args.Paint(args.ClipBounds, args.PaintParts);
            var button = FilterButton(args.CellBounds);
            var color = filters.ContainsKey(args.ColumnIndex) || conditions.ContainsKey(args.ColumnIndex)
                ? (AppTheme.IsDark ? AppTheme.Accent : SystemColors.Highlight)
                : grid.ColumnHeadersDefaultCellStyle.ForeColor;
            var x = button.Left + button.Width / 2;
            var y = button.Top + button.Height / 2;
            using (var brush = new SolidBrush(color))
            {
                args.Graphics.FillPolygon(brush, new[]
                {
                    new Point(x - 4, y - 3), new Point(x + 4, y - 3),
                    new Point(x + 1, y + 1), new Point(x + 1, y + 4),
                    new Point(x - 1, y + 3), new Point(x - 1, y + 1)
                });
                if (sortColumn == args.ColumnIndex && sortOrder != SortOrder.None)
                {
                    var direction = sortOrder == SortOrder.Ascending ? -1 : 1;
                    var sortX = button.Left - 7;
                    args.Graphics.FillPolygon(brush, new[]
                    {
                        new Point(sortX - 3, y - direction * 2),
                        new Point(sortX + 3, y - direction * 2),
                        new Point(sortX, y + direction * 3)
                    });
                }
            }
            args.Handled = true;
        }

        private void OpenHeaderFilter(object sender, DataGridViewCellMouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left || args.RowIndex != -1 || args.ColumnIndex < 0) return;
            var column = args.ColumnIndex;
            var header = grid.GetCellDisplayRectangle(column, -1, false);
            var button = FilterButton(header);
            if (args.X <= 3 || args.X >= header.Width - 4) return;
            if (!button.Contains(new Point(header.Left + args.X, header.Top + args.Y)))
            {
                sortOrder = sortColumn != column || sortOrder == SortOrder.None ? SortOrder.Ascending
                    : sortOrder == SortOrder.Ascending ? SortOrder.Descending : SortOrder.None;
                sortColumn = column;
                ApplyFilters();
                return;
            }
            filterPopup?.Close();

            var values = table.Rows.Cast<DataRow>().Select(row => row[column]).Distinct()
                .Select(value => new FilterValue(value)).OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            filters.TryGetValue(column, out var previous);
            var selected = previous == null ? new HashSet<object>(values.Select(value => value.Value)) : new HashSet<object>(previous);
            var search = new TextBox { Dock = DockStyle.Top, AccessibleName = "Search filter values" };
            search.SetCueBanner("Search values");
            var list = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, ColumnHeadersVisible = false,
                BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window
            };
            list.Columns.Add(new DataGridViewCheckBoxColumn { Width = 28, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            list.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true });
            list.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (list.IsCurrentCellDirty) list.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            var numeric = IsNumeric(table.Columns[column]);
            var textColumn = table.Columns[column].DataType == typeof(string);
            var mode = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            mode.Items.Add("Basic");
            if (numeric || textColumn) mode.Items.Add(numeric ? "Advanced" : "Text filter");
            var operation = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            operation.Items.AddRange(numeric
                ? new object[] { "Equals", "Does not equal", "Greater than", "Greater than or equal", "Less than", "Less than or equal" }
                : new object[] { "Contains", "Equals", "Does not contain", "Does not equal", "Starts with", "Ends with" });
            var operand = new TextBox { Dock = DockStyle.Top, AccessibleName = "Filter value" };
            operand.SetCueBanner("Filter value");
            var validation = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Firebrick };
            var basic = new Panel { Dock = DockStyle.Fill };
            basic.Controls.Add(list);
            basic.Controls.Add(search);
            var advanced = new Panel { Dock = DockStyle.Fill };
            advanced.Controls.Add(validation);
            advanced.Controls.Add(operand);
            advanced.Controls.Add(operation);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            var all = new Button { Text = "Select all", AutoSize = true };
            var none = new Button { Text = "Clear", AutoSize = true };
            var apply = new Button { Text = "Apply", AutoSize = true };
            buttons.Controls.AddRange(new Control[] { all, none, apply });
            var panel = new Panel { Size = new Size(260, 270), Padding = new Padding(6) };
            panel.Controls.Add(basic);
            panel.Controls.Add(advanced);
            panel.Controls.Add(mode);
            panel.Controls.Add(buttons);
            var popup = new Form
            {
                Text = "Filter " + grid.Columns[column].HeaderText,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 270),
                MinimizeBox = false,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi
            };
            filterPopup = popup;
            panel.Dock = DockStyle.Fill;
            popup.Controls.Add(panel);
            popup.Deactivate += (s, e) => popup.Close();
            popup.Paint += (s, e) =>
            {
                using (var pen = new Pen(AppTheme.IsDark ? AppTheme.Border : SystemColors.ControlDark))
                    e.Graphics.DrawRectangle(pen, 0, 0, popup.ClientSize.Width - 1, popup.ClientSize.Height - 1);
            };
            popup.Padding = new Padding(1);
            popup.FormClosed += (s, e) =>
            {
                if (ReferenceEquals(filterPopup, popup)) filterPopup = null;
            };
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            buttons.Controls.Add(cancel);
            cancel.Click += (s, e) => popup.Close();
            popup.CancelButton = cancel;
            popup.AcceptButton = apply;
            panel.Width = 360;

            bool refreshing = false;
            void RefreshValues()
            {
                refreshing = true;
                list.Rows.Clear();
                foreach (var value in values)
                    if (value.Label.IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        var index = list.Rows.Add(selected.Contains(value.Value), value.Label);
                        list.Rows[index].Tag = value.Value;
                    }
                refreshing = false;
            }

            list.CellValueChanged += (s, e) =>
            {
                if (refreshing || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = list.Rows[e.RowIndex];
                var value = row.Tag;
                if (Convert.ToBoolean(row.Cells[0].Value)) selected.Add(value);
                else selected.Remove(value);
            };
            all.Click += (s, e) =>
            {
                selected.UnionWith(values.Select(value => value.Value));
                RefreshValues();
            };
            none.Click += (s, e) =>
            {
                selected.Clear();
                RefreshValues();
            };
            search.TextChanged += (s, e) => RefreshValues();
            conditions.TryGetValue(column, out var currentCondition);
            operation.SelectedIndex = 0;
            if (currentCondition != null)
            {
                operation.SelectedItem = currentCondition.Operation;
                operand.Text = currentCondition.Text;
            }
            mode.SelectedIndexChanged += (s, e) =>
            {
                basic.Visible = mode.SelectedIndex == 0;
                advanced.Visible = mode.SelectedIndex != 0;
                all.Visible = none.Visible = mode.SelectedIndex == 0;
            };
            mode.SelectedIndex = currentCondition != null || textColumn && previous == null ? 1 : 0;
            apply.Click += (s, e) =>
            {
                if (mode.SelectedIndex == 0)
                {
                    conditions.Remove(column);
                    if (selected.Count == values.Length) filters.Remove(column);
                    else filters[column] = selected;
                }
                else
                {
                    var culture = (CultureInfo)grid.Columns[column].DefaultCellStyle.FormatProvider;
                    decimal number = 0;
                    if (numeric && operand.Text.Length > 0 && !decimal.TryParse(operand.Text, NumberStyles.Number | NumberStyles.AllowExponent, culture, out number))
                    {
                        validation.Text = "Enter a valid number.";
                        operand.Focus();
                        return;
                    }
                    filters.Remove(column);
                    if (operand.Text.Length == 0) conditions.Remove(column);
                    else conditions[column] = new ColumnCondition(operation.Text, operand.Text, numeric, number, culture);
                }
                ApplyFilters();
                popup.Close();
            };
            RefreshValues();
            AppTheme.Apply(popup);
            var position = grid.PointToScreen(new Point(button.Left, header.Bottom));
            var area = Screen.FromControl(grid).WorkingArea;
            popup.Location = new Point(
                Math.Max(area.Left, Math.Min(position.X, area.Right - popup.Width)),
                Math.Max(area.Top, Math.Min(position.Y, area.Bottom - popup.Height)));
            // Let the grid finish handling the mouse press before activating the filter.
            BeginInvoke((MethodInvoker)(() =>
            {
                if (popup.IsDisposed || IsDisposed) return;
                popup.Show(FindForm());
                if (mode.SelectedIndex == 0) search.Focus();
                else operand.Focus();
            }));
        }

        private void Export()
        {
            if (table == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Excel Workbook (*.xlsx)|*.xlsx", FileName = "DAX Results.xlsx" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    DaxQueryWorkbook.Save(dialog.FileName, table.Columns, visibleRows, executedQuery);
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Excel export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private sealed class FilterValue
        {
            public object Value { get; }
            public string Label { get; }

            public FilterValue(object value)
            {
                Value = value;
                Label = value == DBNull.Value || value is string text && text.Length == 0
                    ? "(Blank)" : Convert.ToString(value);
            }

            public override string ToString() => Label;
        }

        private static bool IsNumeric(DataColumn column)
        {
            if (IsNumeric(column.DataType)) return true;
            if (column.DataType != typeof(object)) return false;

            // XMLA can declare a result as Object while returning boxed numeric values.
            var values = column.Table.Rows.Cast<DataRow>().Select(row => row[column])
                .Where(value => value != DBNull.Value);
            bool hasValue = false;
            foreach (var value in values)
            {
                if (!IsNumeric(value.GetType())) return false;
                hasValue = true;
            }
            return hasValue;
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
                type == typeof(double) || type == typeof(decimal);
        }

        private sealed class ColumnCondition
        {
            public string Operation { get; }
            public string Text { get; }
            private readonly bool numeric;
            private readonly decimal number;
            private readonly CultureInfo culture;

            public ColumnCondition(string operation, string text, bool numeric, decimal number, CultureInfo culture)
            {
                Operation = operation;
                Text = text;
                this.numeric = numeric;
                this.number = number;
                this.culture = culture;
            }

            public bool Matches(object value)
            {
                if (value == DBNull.Value) return false;
                if (numeric)
                {
                    if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var actual)) return false;
                    var comparison = actual.CompareTo(number);
                    switch (Operation)
                    {
                        case "Equals": return comparison == 0;
                        case "Does not equal": return comparison != 0;
                        case "Greater than": return comparison > 0;
                        case "Greater than or equal": return comparison >= 0;
                        case "Less than": return comparison < 0;
                        case "Less than or equal": return comparison <= 0;
                        default: return false;
                    }
                }
                var text = Convert.ToString(value, culture);
                var compare = culture.CompareInfo;
                const CompareOptions options = CompareOptions.IgnoreCase;
                switch (Operation)
                {
                    case "Contains": return compare.IndexOf(text, Text, options) >= 0;
                    case "Does not contain": return compare.IndexOf(text, Text, options) < 0;
                    case "Equals": return compare.Compare(text, Text, options) == 0;
                    case "Does not equal": return compare.Compare(text, Text, options) != 0;
                    case "Starts with": return compare.IsPrefix(text, Text, options);
                    case "Ends with": return compare.IsSuffix(text, Text, options);
                    default: return false;
                }
            }
        }
    }
}
