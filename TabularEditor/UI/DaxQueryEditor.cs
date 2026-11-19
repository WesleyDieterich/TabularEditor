using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TextServices;
using TabularEditor.UI.Extensions;
using TabularEditor.UIServices;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using CodeEditor = TabularEditor.UI.Extensions.FCTB.FastColoredTextBox;
using Database = TabularEditor.TOMWrapper.Database;
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
        private readonly Font typeIconFont;
        private readonly Label typeToolTip = new Label
        {
            AutoSize = true, Visible = false, BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(5, 2, 5, 2)
        };
        private readonly ToolStripComboBox resultSelector;
        private readonly ToolStripLabel rowCount;
        private readonly ToolStripButton runButton;
        private readonly ToolStripButton cancelButton;
        private readonly ToolStripControlHost exportControl;
        private readonly ToolStrip collapsedResultsCommands;
        private readonly Timer executionTimer = new Timer { Interval = 100 };
        private readonly Stopwatch executionClock = new Stopwatch();
        private CancellationTokenSource runningQuery;
        private Task<string> cancellationRequest;
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
            runButton = new ToolStripButton("Run (F5)", Resources.Run, async (sender, args) => await ExecuteAsync())
            {
                ImageTransparentColor = Color.Magenta
            };
            cancelButton = new ToolStripButton("Cancel", null, (sender, args) => CancelExecution()) { Enabled = false };
            commands.Items.Add(runButton);
            commands.Items.Add(cancelButton);
            split.Panel1.Controls.Add(editor);
            split.Panel1.Controls.Add(commands);
            collapsedResultsCommands = new ToolStrip
            {
                Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden, Visible = false
            };
            collapsedResultsCommands.Items.Add(ResultsArrow(true, "Show results", (sender, args) => ShowResultsPane()));
            split.Panel1.Controls.Add(collapsedResultsCommands);

            grid = new ResultGrid
            {
                Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                AllowUserToResizeColumns = true, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ShowCellToolTips = true
            };
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersHeight += (int)Math.Round(4 * DeviceDpi / 96.0);
            typeIconFont = new Font(grid.Font.FontFamily, grid.Font.SizeInPoints * 0.8f, FontStyle.Regular);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.RowTemplate.Height += (int)Math.Round(4 * DeviceDpi / 96.0);
            grid.CellValueNeeded += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.RowIndex < visibleRows.Length &&
                    args.ColumnIndex >= 0 && args.ColumnIndex < visibleRows[args.RowIndex].Table.Columns.Count)
                    args.Value = visibleRows[args.RowIndex][args.ColumnIndex];
            };
            grid.CellPainting += PaintColumnHeader;
            grid.MouseMove += UpdateTypeToolTip;
            grid.MouseLeave += (sender, args) => HideTypeToolTip();
            grid.Scroll += (sender, args) => HideTypeToolTip();
            grid.ColumnWidthChanged += (sender, args) => HideTypeToolTip();
            grid.VisibleChanged += (sender, args) => { if (!grid.Visible) HideTypeToolTip(); };
            grid.CellMouseDown += OpenHeaderFilter;
            resultSelector = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
            resultSelector.SelectedIndexChanged += (sender, args) =>
            {
                if (results != null && resultSelector.SelectedIndex >= 0)
                    ShowTable(results.Tables[resultSelector.SelectedIndex]);
            };
            rowCount = new ToolStripLabel();
            var resultCommands = new AppTheme.BorderlessToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var exportButton = new Button
            {
                Text = "Export to Excel",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 0, 8, 0),
                UseVisualStyleBackColor = true
            };
            exportButton.Click += (sender, args) => Export();
            exportControl = new ToolStripControlHost(exportButton)
            {
                Margin = new Padding(4, 1, 8, 1), Enabled = false
            };
            resultCommands.Items.Add(exportControl);
            resultCommands.Items.Add(resultSelector);
            resultCommands.Items.Add(rowCount);
            resultCommands.Items.Add(ResultsArrow(false, "Hide results", (sender, args) =>
            {
                split.Panel2Collapsed = true;
                collapsedResultsCommands.Show();
            }));
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(new Panel { Dock = DockStyle.Top, Height = split.SplitterWidth });
            split.Panel2.Controls.Add(resultCommands);
            split.Panel2.Controls.Add(typeToolTip);
            typeToolTip.BringToFront();
            Controls.Add(split);
            executionTimer.Tick += (sender, args) => UpdateExecutionStatus();

            Disposed += (sender, args) =>
            {
                CancelExecution();
                executionTimer.Dispose();
                highlight.Dispose();
                typeIconFont.Dispose();
                results?.Dispose();
                codeFont.Dispose();
            };
        }

        public void ApplyTheme()
        {
            intelliSense.ApplyTheme();
            typeToolTip.BackColor = AppTheme.IsDark ? AppTheme.Raised : SystemColors.Info;
            typeToolTip.ForeColor = AppTheme.IsDark ? AppTheme.Text : SystemColors.InfoText;
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
            CancelExecution();
            executionTimer.Stop();
            ResetResults();
            collapsedResultsCommands.Hide();
            split.Panel2Collapsed = true;
        }

        private void ResetResults()
        {
            HideTypeToolTip();
            filterPopup?.Close();
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
            resultSelector.Visible = false;
            exportControl.Enabled = false;
            rowCount.Text = string.Empty;
            executedQuery = string.Empty;
            executionTime = TimeSpan.Zero;
        }

        public async Task ExecuteAsync()
        {
            if (runningQuery != null) return;
            var handler = getHandler();
            if (handler?.IsConnected != true)
            {
                MessageBox.Show(this, "Connect to a semantic model before running a DAX query.", "DAX Query",
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
                MessageBox.Show(this, "Select or enter a DAX query that starts with EVALUATE or DEFINE.", "DAX Query",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            intelliSense.Close();
            var catalog = handler.Database.Name;
            var connectionString = handler.GetQueryConnectionString(catalog);
            ResetResults();
            ShowResultsPane();
            var cancellation = runningQuery = new CancellationTokenSource();
            runButton.Enabled = false;
            cancelButton.Enabled = true;
            executionClock.Restart();
            UpdateExecutionStatus();
            executionTimer.Start();

            DataSet data = null;
            Exception failure = null;
            try
            {
                data = await Task.Run(() => Database.ExecuteDax(connectionString, catalog, query, cancellation.Token));
            }
            catch (Exception error)
            {
                failure = error;
            }

            var elapsed = executionClock.Elapsed;
            var cancelError = cancellationRequest == null ? null : await cancellationRequest;
            var canceled = cancellationRequest != null;
            cancellationRequest = null;
            runningQuery = null;
            cancellation.Dispose();
            if (IsDisposed || Disposing)
            {
                data?.Dispose();
                return;
            }

            var showStatus = executionTimer.Enabled && ReferenceEquals(getHandler(), handler);
            executionTimer.Stop();
            executionClock.Stop();
            runButton.Enabled = true;
            cancelButton.Enabled = false;
            if (!showStatus)
            {
                data?.Dispose();
                return;
            }

            if (canceled)
            {
                data?.Dispose();
                rowCount.Text = cancelError == null
                    ? $"Query canceled  ·  {executionClock.Elapsed.TotalSeconds:N1} s"
                    : $"Could not cancel on server: {cancelError}";
            }
            else if (failure != null)
            {
                rowCount.Text = $"Query failed  ·  {elapsed.TotalSeconds:N1} s";
                MessageBox.Show(this, failure.Message, "DAX query failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else ShowResults(data, query, elapsed);
        }

        private void CancelExecution()
        {
            if (runningQuery == null || cancellationRequest != null) return;
            var cancellation = runningQuery;
            cancellationRequest = Task.Run(() =>
            {
                try
                {
                    cancellation.Cancel();
                    return (string)null;
                }
                catch (Exception error)
                {
                    return error.GetBaseException().Message;
                }
            });
            if (IsDisposed || Disposing) return;
            cancelButton.Enabled = false;
            UpdateExecutionStatus();
        }

        private void UpdateExecutionStatus()
        {
            var elapsed = $"{executionClock.Elapsed.TotalSeconds:N1} s";
            if (cancellationRequest == null)
            {
                rowCount.Text = elapsed;
                return;
            }

            var status = "Canceling query…";
            if (cancellationRequest?.Status == TaskStatus.RanToCompletion && cancellationRequest.Result != null)
                status = "Server cancellation failed. Waiting for the query…";
            rowCount.Text = $"{status}  ·  {elapsed}";
        }

        private void ShowResults(DataSet data, string query, TimeSpan elapsed)
        {
            executedQuery = query;
            executionTime = elapsed;
            results = data;
            for (var i = 0; i < results.Tables.Count; i++) resultSelector.Items.Add($"Result {i + 1}");
            resultSelector.Visible = results.Tables.Count > 1;
            if (results.Tables.Count > 0) resultSelector.SelectedIndex = 0;
            else rowCount.Text = "No result sets";
        }

        private void ShowResultsPane()
        {
            collapsedResultsCommands.Hide();
            split.Panel2Collapsed = false;
            if (!resultsPaneSized && split.Height >= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth)
            {
                split.SplitterDistance = Math.Max(split.Panel1MinSize,
                    Math.Min(split.Height * 3 / 5, split.Height - split.Panel2MinSize - split.SplitterWidth));
                resultsPaneSized = true;
            }
        }

        private static ToolStripButton ResultsArrow(bool pointsUp, string description, EventHandler click)
        {
            var button = new ToolStripButton(description, null, click)
            {
                Alignment = ToolStripItemAlignment.Right,
                AutoSize = false,
                Size = new Size(26, 20),
                Margin = Padding.Empty,
                DisplayStyle = ToolStripItemDisplayStyle.None,
                AccessibleName = description,
                AutoToolTip = false,
                ToolTipText = description
            };
            button.Paint += (sender, args) =>
            {
                var scale = args.Graphics.DpiX / 96f;
                var x = button.Width / 2f;
                var y = button.Height / 2f;
                var halfWidth = 5 * scale;
                var halfHeight = (pointsUp ? -2.5f : 2.5f) * scale;
                using (var pen = new Pen(button.ForeColor, 1.7f * scale))
                {
                    args.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(x - halfWidth, y - halfHeight),
                        new PointF(x, y + halfHeight),
                        new PointF(x + halfWidth, y - halfHeight)
                    });
                }
            };
            return button;
        }

        private sealed class ResultGrid : DataGridView
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!AppTheme.IsDark || !HorizontalScrollBar.Visible || !VerticalScrollBar.Visible) return;

                // The space where the scroll bars meet is painted by the grid itself.
                e.Graphics.FillRectangle(AppTheme.RaisedBrush, VerticalScrollBar.Left, HorizontalScrollBar.Top,
                    VerticalScrollBar.Width, HorizontalScrollBar.Height);
            }
        }

        private void ShowTable(DataTable result)
        {
            HideTypeToolTip();
            filterPopup?.Close();
            grid.RowCount = 0;
            grid.Columns.Clear();
            filters.Clear();
            conditions.Clear();
            sortColumn = -1;
            sortOrder = SortOrder.None;
            table = result;
            exportControl.Enabled = true;
            foreach (DataColumn column in table.Columns)
            {
                var columnType = GetResultType(column);
                var typeIcon = GetTypeIcon(columnType);
                // The standard header renderer already leaves a small margin before its text.
                var iconWidth = Math.Max(TypeIconInset, MeasureTypeIcon(typeIcon).Width + TypeIconInset - 6 * DeviceDpi / 96);
                var gridColumn = new DataGridViewTextBoxColumn
                {
                    HeaderText = DaxQueryWorkbook.ColumnName(column.ColumnName), ValueType = column.DataType,
                    Tag = (Icon: typeIcon, Name: GetTypeName(columnType)), MinimumWidth = iconWidth + FilterWidth + 16,
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
                gridColumn.HeaderCell.Style.Padding = new Padding(iconWidth, 0, FilterWidth + 12, 0);
                grid.Columns.Add(gridColumn);
            }
            ApplyFilters();
            // Fit formatted values and headers while bounding the cost for large virtual result sets.
            grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            foreach (DataGridViewColumn column in grid.Columns)
            {
                var headerStyle = column.HeaderCell.InheritedStyle;
                var headerWidth = TextRenderer.MeasureText(column.HeaderText, headerStyle.Font).Width +
                    headerStyle.Padding.Horizontal + Math.Max(6, 6 * DeviceDpi / 96);
                column.Width = Math.Max(column.Width, Math.Min(headerWidth, 65536));
            }
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
        private int TypeIconInset => Math.Max(5, 5 * DeviceDpi / 96);
        private int TypeIconHoverPadding => Math.Max(2, 2 * DeviceDpi / 96);
        private const TextFormatFlags TypeIconTextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

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

        private Size MeasureTypeIcon(string icon) =>
            TextRenderer.MeasureText(icon, typeIconFont, new Size(int.MaxValue, int.MaxValue), TypeIconTextFlags);

        private Rectangle TypeIconBounds(Rectangle header, string icon)
        {
            var size = MeasureTypeIcon(icon);
            return new Rectangle(header.Left + TypeIconInset,
                header.Top + (header.Height - size.Height) / 2, size.Width, size.Height);
        }

        private void UpdateTypeToolTip(object sender, MouseEventArgs args)
        {
            var hit = grid.HitTest(args.X, args.Y);
            if (hit.Type == DataGridViewHitTestType.ColumnHeader)
            {
                var type = ((string Icon, string Name))grid.Columns[hit.ColumnIndex].Tag;
                var header = grid.GetCellDisplayRectangle(hit.ColumnIndex, -1, false);
                var bounds = TypeIconBounds(header, type.Icon);
                var iconRight = bounds.Right;
                bounds.Inflate(TypeIconHoverPadding, TypeIconHoverPadding);
                bounds.Intersect(Rectangle.FromLTRB(header.Left, header.Top, iconRight, header.Bottom));
                if (bounds.Contains(args.Location))
                {
                    typeToolTip.Text = type.Name;
                    typeToolTip.Location = new Point(
                        Math.Max(0, Math.Min(grid.Left + bounds.Left, split.Panel2.ClientSize.Width - typeToolTip.Width)),
                        grid.Top + header.Bottom + 2);
                    typeToolTip.Show();
                    return;
                }
            }
            HideTypeToolTip();
        }

        private void HideTypeToolTip() => typeToolTip.Hide();

        private void PaintColumnHeader(object sender, DataGridViewCellPaintingEventArgs args)
        {
            if (args.RowIndex != -1 || args.ColumnIndex < 0) return;
            args.Paint(args.ClipBounds, args.PaintParts);
            var type = ((string Icon, string Name))grid.Columns[args.ColumnIndex].Tag;
            var foreground = args.CellStyle.ForeColor;
            var background = args.CellStyle.BackColor;
            var iconColor = Color.FromArgb((foreground.R * 2 + background.R) / 3,
                (foreground.G * 2 + background.G) / 3, (foreground.B * 2 + background.B) / 3);
            TextRenderer.DrawText(args.Graphics, type.Icon, typeIconFont,
                TypeIconBounds(args.CellBounds, type.Icon), iconColor, TypeIconTextFlags);
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

        private static Type GetResultType(DataColumn column)
        {
            if (column.DataType != typeof(object)) return column.DataType;

            // Some XMLA results declare Object even when every nonblank value has the same type.
            Type type = null;
            foreach (DataRow row in column.Table.Rows)
            {
                if (row.IsNull(column)) continue;
                var valueType = row[column].GetType();
                if (type != null && type != valueType) return typeof(object);
                type = valueType;
            }
            return type ?? typeof(object);
        }

        private static string GetTypeIcon(Type type)
        {
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "▦";
            if (type == typeof(TimeSpan)) return "◷";
            if (type == typeof(bool)) return "✓";
            if (type == typeof(string) || type == typeof(char)) return "abc";
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "1.2";
            if (IsNumeric(type)) return "#";
            if (type == typeof(Guid)) return "ID";
            if (type == typeof(byte[])) return "01";
            return "?";
        }

        private static string GetTypeName(Type type)
        {
            if (type == typeof(DateTime)) return "Date/time";
            if (type == typeof(DateTimeOffset)) return "Date/time with offset";
            if (type == typeof(TimeSpan)) return "Duration";
            if (type == typeof(string)) return "Text";
            if (type == typeof(bool)) return "Boolean";
            if (type == typeof(byte[])) return "Binary";
            return type.Name;
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
