using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TabularEditor.TextServices;
using TabularEditor.UI.Extensions;

namespace TabularEditor.UI
{
    internal sealed class PivotView : UserControl
    {
        private const int GroupLimit = 10000;
        private const int ColumnLimit = 250;
        private readonly Func<TabularModelHandler> getHandler;
        private readonly FieldShelf rows = new FieldShelf("Rows", axis: true);
        private readonly FieldShelf columns = new FieldShelf("Columns", axis: true);
        private readonly FieldShelf filters = new FieldShelf("Filters", isFilter: true);
        private readonly FieldShelf measures = new FieldShelf("Measures");
        private readonly TableLayoutPanel shelves = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 165, ColumnCount = 4, RowCount = 1
        };
        private readonly MatrixGrid grid = new MatrixGrid
        {
            Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        private readonly Label status = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        private readonly Button run = new Button { Text = "Calculate", AutoSize = true };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
        private readonly CheckBox useLocalMeasures = new CheckBox
        {
            Text = "Use local measure expressions", AutoSize = true, Margin = new Padding(8, 7, 0, 0),
            AccessibleDescription = "Use measure edits applied in Tabular Editor for this query. Clear to use measures saved on the server."
        };
        private readonly List<Dictionary<int, object>> matrix = new List<Dictionary<int, object>>();
        private CancellationTokenSource execution;
        private Task cancellation;
        private int modelVersion;

        public PivotView(Func<TabularModelHandler> getHandler)
        {
            this.getHandler = getHandler;
            Dock = DockStyle.Fill;
            foreach (var shelf in new[] { rows, columns, filters, measures })
            {
                shelves.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                shelves.Controls.Add(shelf);
                shelf.PickerOpening += RefreshFields;
                shelf.Changed += ClearResults;
            }
            filters.EditFilter = EditFilter;

            var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
            commands.Controls.AddRange(new Control[] { run, cancel, useLocalMeasures, status });
            Controls.Add(grid);
            Controls.Add(commands);
            Controls.Add(shelves);
            run.Click += async (sender, args) => await ExecuteAsync();
            cancel.Click += (sender, args) => CancelExecution();
            useLocalMeasures.CheckedChanged += (sender, args) => ClearResults();
            grid.CellValueNeeded += (sender, args) =>
            {
                if (matrix[args.RowIndex].TryGetValue(args.ColumnIndex, out var value)) args.Value = value;
            };
            Disposed += (sender, args) => CancelExecution();
            ClearResults();
        }

        public void ResetModel()
        {
            modelVersion++;
            CancelExecution();
            foreach (var shelf in new[] { rows, columns, filters, measures }) shelf.Reset();
            ClearResults();
        }

        private void RefreshFields(FieldShelf shelf)
        {
            var model = getHandler()?.Model;
            if (model == null)
            {
                shelf.SetChoices(Enumerable.Empty<Field>());
                return;
            }

            var axes = rows.Fields.Concat(columns.Fields).Select(field => field.Object);
            IEnumerable<Field> choices;
            if (shelf == measures)
            {
                choices = model.AllMeasures.Where(measure => !measure.IsHidden)
                    .Select(measure => new Field(measure, measure.Table.DaxTableName + measure.DaxObjectName))
                    .Concat(model.AllColumns.Where(column => !column.IsHidden && ParameterReference(column) != null)
                        .Select(column => new Field(column, $"{column.DaxObjectFullName} (Field parameter)")));
            }
            else
                choices = model.AllColumns.Where(column => !column.IsHidden)
                    .Where(column => shelf == filters || !axes.Contains(column))
                    .Select(column => new Field(column, column.DaxObjectFullName));
            shelf.SetChoices(choices.OrderBy(field => field.Label, StringComparer.CurrentCultureIgnoreCase));
        }

        private void ClearResults()
        {
            grid.RowCount = 0;
            grid.Columns.Clear();
            grid.SetColumnLevels(Array.Empty<string>());
            matrix.Clear();
            status.Text = getHandler()?.IsConnected == true
                ? "Add measures and calculate the pivot table."
                : "Connect to a semantic model to calculate the pivot table.";
        }

        public async Task ExecuteAsync()
        {
            if (execution != null) return;
            var handler = getHandler();
            ClearResults();
            if (handler?.IsConnected != true || measures.Fields.Length == 0) return;

            var rowFields = rows.Fields;
            var columnFields = columns.Fields;
            var valueFields = measures.Fields;
            var filterFields = filters.Fields;
            var axes = rowFields.Concat(columnFields).ToArray();
            var fields = axes.Concat(valueFields).Concat(filterFields);
            if (fields.Any(field => field.Object.IsRemoved))
            {
                status.Text = "Remove fields that are no longer in the model.";
                return;
            }

            var version = modelVersion;
            var previewLocal = useLocalMeasures.Checked;
            var tokenSource = execution = new CancellationTokenSource();
            run.Enabled = shelves.Enabled = useLocalMeasures.Enabled = false;
            cancel.Enabled = true;
            status.Text = "Calculating…";
            try
            {
                var catalog = handler.Database.Name;
                var connectionString = handler.GetQueryConnectionString(catalog);
                if (valueFields.Any(field => field.Object is Column))
                {
                    valueFields = await ResolveMeasuresAsync(handler.Model, connectionString, valueFields, filterFields, tokenSource.Token);
                    if (!CanShowResult(handler, version) || cancellation != null) return;
                    if (valueFields.Length == 0)
                    {
                        status.Text = "The selected field parameters contain no matching measures.";
                        return;
                    }
                }
                var rowGroups = GroupingColumns(rowFields.Select(field => (Column)field.Object));
                var columnLevels = columnFields.Select((field, index) =>
                    GroupingColumns(columnFields.Take(index + 1).Select(item => (Column)item.Object))).ToArray();
                var columnGroups = columnLevels.LastOrDefault() ?? Array.Empty<Column>();
                var columnOrder = AxisOrder(columnFields).Concat(columnGroups).Distinct().ToArray();
                var axisOrder = AxisOrder(rowFields).Concat(rowGroups).Concat(columnOrder).Distinct().ToArray();
                var groups = axes.Select(field => (Column)field.Object).Concat(rowGroups).Concat(columnGroups)
                    .Concat(GroupingColumns(axisOrder)).Distinct().ToArray();
                var display = axes.Concat(valueFields).Select(field => DisplayExpression(field, previewLocal)).ToArray();
                var query = BuildQuery(groups, axisOrder, valueFields, filterFields, display);
                if (previewLocal) query = DefineLocalMeasures(valueFields, query);
                using (var data = await Task.Run(() => Database.ExecuteDax(connectionString, query, tokenSource.Token)))
                {
                    if (CanShowResult(handler, version) && cancellation == null)
                        ShowMatrix(data.Tables[0], rowFields, columnFields, valueFields, groups.Length,
                            rowGroups.Select(column => Array.IndexOf(groups, column)).ToArray(),
                            columnLevels.Select(level => level.Select(column => Array.IndexOf(groups, column)).ToArray()).ToArray(),
                            columnOrder.Select(column => Array.IndexOf(groups, column)).ToArray(),
                            display, handler.Model.Culture);
                }
            }
            catch (Exception error)
            {
                if (CanShowResult(handler, version) && cancellation == null)
                {
                    status.Text = "Calculation failed.";
                    AppMessageBox.Show(this, error.GetBaseException().Message, "Pivot table",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (cancellation != null)
                {
                    try
                    {
                        await cancellation;
                        if (CanShowResult(handler, version)) status.Text = "Calculation canceled.";
                    }
                    catch (Exception error)
                    {
                        if (CanShowResult(handler, version)) status.Text = error.GetBaseException().Message;
                    }
                }
                cancellation = null;
                execution = null;
                tokenSource.Dispose();
                if (!IsDisposed && !Disposing)
                {
                    run.Enabled = shelves.Enabled = useLocalMeasures.Enabled = true;
                    cancel.Enabled = false;
                }
            }
        }

        private bool CanShowResult(TabularModelHandler handler, int version) =>
            !IsDisposed && !Disposing && modelVersion == version && ReferenceEquals(handler, getHandler());

        private void CancelExecution()
        {
            if (execution == null || cancellation != null) return;
            var tokenSource = execution;
            cancellation = Task.Run(() => tokenSource.Cancel());
            if (IsDisposed || Disposing) return;
            cancel.Enabled = false;
            status.Text = "Canceling…";
        }

        private static string DefineLocalMeasures(Field[] fields, string query)
        {
            var selected = new HashSet<Measure>(fields.Select(field => (Measure)field.Object));
            var definitions = new List<Measure>(selected);
            var included = new HashSet<Measure>(selected);
            for (var index = 0; index < definitions.Count; index++)
            {
                var measure = definitions[index];
                foreach (var dependency in measure.DependsOn)
                    if (dependency.Key is Measure referenced && dependency.Value.Any(reference =>
                        reference.property == DAXProperty.Expression ||
                        (selected.Contains(measure) && reference.property == DAXProperty.FormatStringExpression)) &&
                        included.Add(referenced))
                        definitions.Add(referenced);
            }

            return "DEFINE\n" + string.Join("\n", definitions.Select(measure =>
                $"    MEASURE {measure.Table.DaxTableName}{measure.DaxObjectName} = (\n{measure.Expression}\n)")) +
                "\n" + query;
        }

        private static string DisplayExpression(Field field, bool previewLocal)
        {
            string value, format;
            if (field.Object is Column column)
            {
                var aggregate = column.GroupByColumns?.Count > 0 ? "MAX" : "SELECTEDVALUE";
                value = $"{aggregate}({column.DaxObjectFullName})";
                format = string.IsNullOrEmpty(column.FormatString) ? null : DaxString(column.FormatString);
            }
            else
            {
                var measure = (Measure)field.Object;
                value = measure.DaxObjectName;
                format = string.IsNullOrWhiteSpace(measure.FormatStringExpression)
                    ? (string.IsNullOrEmpty(measure.FormatString) ? null : DaxString(measure.FormatString))
                    : previewLocal ? LocalFormatExpression(measure)
                    : $"{measure.Table.DaxTableName}[_{measure.Name.Replace("]", "]]")} FormatString]";
            }
            return format == null ? null : $"IGNORE(IF(ISBLANK({value}), BLANK(), FORMAT({value}, {format})))";
        }

        private static string LocalFormatExpression(Measure measure)
        {
            var expression = measure.FormatStringExpression;
            var lexer = new DAXLexer(new DAXCharStream(expression, false));
            lexer.RemoveErrorListeners();
            var tokens = lexer.GetDefaultTokens().ToArray();
            var formatted = new StringBuilder(expression);
            // SELECTEDMEASURE is available in a model format string but not in the query expression.
            for (var index = tokens.Length - 3; index >= 0; index--)
                if (string.Equals(tokens[index].Text, "SELECTEDMEASURE", StringComparison.OrdinalIgnoreCase) &&
                    tokens[index + 1].Text == "(" && tokens[index + 2].Text == ")")
                    formatted.Remove(tokens[index].StartIndex, tokens[index + 2].StopIndex - tokens[index].StartIndex + 1)
                        .Insert(tokens[index].StartIndex, measure.DaxObjectName);
            return $"(\n{formatted}\n)";
        }

        internal static string DaxString(string text) => $"\"{text.Replace("\"", "\"\"")}\"";

        private PivotFilterDialog.Filter EditFilter(Column column, PivotFilterDialog.Filter previous)
        {
            var handler = getHandler();
            var loadValuesFromModel = previous?.LoadModelValues ?? false;
            if (previous == null)
            {
                var load = AppMessageBox.Show(this, $"Load values from the model for {column.Name}?",
                    "Add filter", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (load != DialogResult.Yes && load != DialogResult.No) return null;
                loadValuesFromModel = load == DialogResult.Yes;
            }

            Func<CancellationToken, Task<object[]>> loadValues = null;
            if (loadValuesFromModel)
            {
                if (handler?.IsConnected != true)
                {
                    AppMessageBox.Show(this, "Connect to a semantic model to load filter values.", "Filter",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }
                var connection = handler.GetQueryConnectionString(handler.Database.Name);
                var order = AxisOrder(new[] { new Field(column, column.DaxObjectFullName) });
                var groups = GroupingColumns(new[] { column }.Concat(order));
                var names = groups.Select(group => group.DaxObjectFullName).ToArray();
                var query = LimitQuery($"SUMMARIZECOLUMNS({string.Join(", ", names)})",
                    order.Select(group => group.DaxObjectFullName).Concat(names).Distinct(), GroupLimit);
                loadValues = token => Task.Run(() =>
                {
                    using (var data = Database.ExecuteDax(connection, query, token))
                    {
                        var items = data.Tables[0].Rows;
                        if (items.Count > GroupLimit)
                            throw new InvalidOperationException($"More than {GroupLimit:N0} values. Use Advanced to enter a condition.");
                        return items.Cast<DataRow>().Select(row => row[0]).Distinct().ToArray();
                    }
                }, token);
            }

            var cultureName = handler?.Model.Culture;
            var culture = string.IsNullOrEmpty(cultureName) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(cultureName);
            using (var dialog = new PivotFilterDialog(column, culture, loadValues, previous))
                return dialog.ShowDialog(this) == DialogResult.OK && ReferenceEquals(handler, getHandler()) && !column.IsRemoved
                    ? dialog.Result : null;
        }

        private static Column ParameterReference(Column label) =>
            label.GroupByColumns?.FirstOrDefault(column => column.HasExtendedProperty("ParameterMetadata"));

        private static async Task<Field[]> ResolveMeasuresAsync(Model model, string connectionString,
            Field[] selected, Field[] filters, CancellationToken token)
        {
            var byReference = model.AllMeasures.ToDictionary(
                measure => measure.Table.DaxTableName + measure.DaxObjectName, StringComparer.OrdinalIgnoreCase);
            var resolved = new List<Field>();
            foreach (var field in selected)
            {
                if (!(field.Object is Column parameter))
                {
                    resolved.Add(field);
                    continue;
                }

                var reference = ParameterReference(parameter);
                if (reference == null)
                    throw new InvalidOperationException($"{parameter.Name} is no longer a field parameter.");
                var order = parameter.SortByColumn;
                var groups = GroupingColumns(new[] { parameter, reference, order }.Where(column => column != null));
                var grouping = string.Join(", ", groups.Select(column => column.DaxObjectFullName));
                var query = $"SUMMARIZECOLUMNS({grouping})";
                query = ApplyFilters(query, filters.Where(filter => ((Column)filter.Object).Table == parameter.Table));
                var ordering = groups.OrderBy(column => column == order ? 0 : 1).Select(column => column.DaxObjectFullName);
                query = LimitQuery(query, ordering, GroupLimit);
                using (var data = await Task.Run(() => Database.ExecuteDax(connectionString, query, token)))
                {
                    token.ThrowIfCancellationRequested();
                    var rows = data.Tables[0].Rows;
                    if (rows.Count > GroupLimit)
                        throw new InvalidOperationException($"The field parameter {parameter.Name} has more than {GroupLimit:N0} entries. Apply a filter.");
                    foreach (DataRow row in rows)
                        if (row[1] is string name && byReference.TryGetValue(name, out var measure))
                            resolved.Add(new Field(measure, name, displayName: row[0] as string));
                }
            }
            return resolved.ToArray();
        }

        private static IEnumerable<Column> AxisOrder(IEnumerable<Field> fields)
        {
            foreach (var field in fields)
            {
                var column = (Column)field.Object;
                if (column.SortByColumn != null) yield return column.SortByColumn;
                yield return column;
            }
        }

        private static Column[] GroupingColumns(IEnumerable<Column> columns)
        {
            var groups = columns.Distinct().ToList();
            var added = new HashSet<Column>(groups);
            for (var index = 0; index < groups.Count; index++)
            {
                var related = groups[index].GroupByColumns;
                if (related == null) continue;
                foreach (var column in related)
                    if (added.Add(column)) groups.Add(column);
            }
            return groups.ToArray();
        }

        private static string BuildQuery(Column[] groups, Column[] axisOrder, Field[] measures, Field[] filters, string[] display)
        {
            var values = measures.Select((field, index) => $"\"__PivotValue{index}\", {((Measure)field.Object).DaxObjectName}");
            var formattedValues = display.Select((expression, index) =>
                expression == null ? null : $"\"__PivotDisplay{index}\", {expression}").Where(expression => expression != null);
            var groupNames = groups.Select(column => column.DaxObjectFullName);
            var query = $"SUMMARIZECOLUMNS({string.Join(",\n    ", groupNames.Concat(values).Concat(formattedValues))})";
            query = ApplyFilters(query, filters);
            return LimitQuery(query, axisOrder.Concat(groups).Distinct().Select(column => column.DaxObjectFullName), GroupLimit);
        }

        private static string ApplyFilters(string query, IEnumerable<Field> filters)
        {
            var filterExpressions = filters.Select(field =>
            {
                var filterColumn = (Column)field.Object;
                var filterGroups = string.Join(", ", GroupingColumns(new[] { filterColumn }).Select(group => group.DaxObjectFullName));
                var comparison = field.Filter.Predicate(filterColumn.DaxObjectFullName);
                return $"KEEPFILTERS(FILTER(ALL({filterGroups}), {comparison}))";
            }).ToArray();
            return filterExpressions.Length == 0 ? query : $"CALCULATETABLE({query},\n    {string.Join(",\n    ", filterExpressions)})";
        }

        private static string LimitQuery(string query, IEnumerable<string> order, int limit)
        {
            var ordering = order.ToArray();
            if (ordering.Length == 0) return $"EVALUATE\n{query}";
            var topOrder = string.Join(", ", ordering.Select(group => $"{group}, ASC"));
            var resultOrder = string.Join(", ", ordering.Select(group => $"{group} ASC"));
            return $"EVALUATE\nTOPN({limit + 1}, {query}, {topOrder})\nORDER BY {resultOrder}";
        }

        private void ShowMatrix(DataTable data, Field[] rowFields, Field[] columnFields, Field[] valueFields,
            int groupCount, int[] rowKeyColumns, int[][] columnKeyColumns, int[] columnOrder, string[] display, string modelCulture)
        {
            if (data.Rows.Count > GroupLimit)
            {
                status.Text = $"More than {GroupLimit:N0} combinations. Apply filters or remove fields.";
                return;
            }

            var rowKeys = new Dictionary<object[], int>(new AxisComparer());
            var columnKeys = new Dictionary<object[], int>(new AxisComparer());
            var headers = rowFields.Select(field => field.Object.Name).ToList();
            var columnHeaders = new Dictionary<int, MatrixHeader>();
            var columnSortKeys = new List<(int Start, object[] Key)>();
            if (columnFields.Length == 0)
                headers.AddRange(valueFields.Select(field => field.DisplayName));

            var firstMeasure = rowFields.Length + columnFields.Length;
            var nextDisplayColumn = groupCount + valueFields.Length;
            var displayColumns = display.Select((expression, index) => expression != null ? nextDisplayColumn++
                : index < firstMeasure ? index : groupCount + index - firstMeasure).ToArray();
            var culture = string.IsNullOrEmpty(modelCulture) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(modelCulture);
            var lastColumnKey = columnKeyColumns.LastOrDefault() ?? Array.Empty<int>();
            var result = new List<Dictionary<int, object>>();
            foreach (DataRow record in data.Rows)
            {
                var values = record.ItemArray;
                var rowKey = rowKeyColumns.Select(index => values[index]).ToArray();
                var columnKey = lastColumnKey.Select(index => values[index]).ToArray();
                if (!rowKeys.TryGetValue(rowKey, out var rowIndex))
                {
                    rowIndex = result.Count;
                    rowKeys.Add(rowKey, rowIndex);
                    var row = new Dictionary<int, object>();
                    for (var index = 0; index < rowFields.Length; index++) row.Add(index, values[displayColumns[index]]);
                    result.Add(row);
                }
                if (!columnKeys.TryGetValue(columnKey, out var firstValueColumn))
                {
                    firstValueColumn = rowFields.Length + columnKeys.Count * valueFields.Length;
                    if (firstValueColumn + valueFields.Length > ColumnLimit)
                    {
                        status.Text = $"More than {ColumnLimit:N0} result columns. Apply filters or remove fields.";
                        return;
                    }
                    columnKeys.Add(columnKey, firstValueColumn);
                    if (columnFields.Length > 0)
                    {
                        columnSortKeys.Add((firstValueColumn, columnOrder.Select(index => values[index]).ToArray()));
                        var labels = displayColumns.Skip(rowFields.Length).Take(columnFields.Length)
                            .Select(index => values[index] == DBNull.Value ? "(Blank)" : Convert.ToString(values[index], culture)).ToArray();
                        var keys = columnKeyColumns.Select(level => level.Select(index => values[index]).ToArray()).ToArray();
                        var header = new MatrixHeader(labels, keys);
                        foreach (var valueField in valueFields)
                        {
                            columnHeaders.Add(headers.Count, header);
                            headers.Add(valueField.DisplayName);
                        }
                    }
                }
                for (var index = 0; index < valueFields.Length; index++)
                    result[rowIndex].Add(firstValueColumn + index, values[displayColumns[firstMeasure + index]]);
            }

            if (result.Count == 0)
            {
                status.Text = "No results.";
                return;
            }
            grid.SetColumnLevels(columnFields.Select(field => field.Object.Name).ToArray());
            for (var index = 0; index < headers.Count; index++)
            {
                columnHeaders.TryGetValue(index, out var header);
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = headers[index], Tag = header, SortMode = DataGridViewColumnSortMode.NotSortable,
                    DefaultCellStyle = new DataGridViewCellStyle { FormatProvider = culture }
                });
            }
            // The query is ordered by the row axis first. A column group may first appear in a later row.
            var columnComparer = Comparer<object[]>.Create((left, right) => CompareSortKeys(left, right, culture));
            var displayIndex = rowFields.Length;
            foreach (var group in columnSortKeys.OrderBy(group => group.Key, columnComparer))
                for (var index = 0; index < valueFields.Length; index++)
                    grid.Columns[group.Start + index].DisplayIndex = displayIndex++;
            matrix.AddRange(result);
            grid.RowCount = matrix.Count;
            grid.FitColumns(matrix, culture);
            status.Text = $"{matrix.Count:N0} rows";
        }

        private static int CompareSortKeys(object[] left, object[] right, CultureInfo culture)
        {
            for (var index = 0; index < left.Length; index++)
            {
                var a = left[index];
                var b = right[index];
                if (Equals(a, b)) continue;
                if (a == DBNull.Value) return -1;
                if (b == DBNull.Value) return 1;
                var result = a is string text && b is string other
                    ? culture.CompareInfo.Compare(text, other, CompareOptions.IgnoreCase)
                    : ((IComparable)a).CompareTo(b);
                if (result != 0) return result;
            }
            return 0;
        }

        private sealed class AxisComparer : IEqualityComparer<object[]>
        {
            public bool Equals(object[] left, object[] right) => StructuralComparisons.StructuralEqualityComparer.Equals(left, right);
            public int GetHashCode(object[] key) => StructuralComparisons.StructuralEqualityComparer.GetHashCode(key);
        }

        private sealed class MatrixHeader
        {
            public MatrixHeader(string[] labels, object[][] keys)
            {
                Labels = labels;
                Keys = keys;
            }

            public string[] Labels { get; }
            public object[][] Keys { get; }
        }

        private sealed class MatrixGrid : QueryResultGrid
        {
            private const TextFormatFlags HeaderTextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping;
            private string[] levels = Array.Empty<string>();
            private int headerRowHeight;
            private int CellPadding => Math.Max(8, 12 * DeviceDpi / 96);

            public void SetColumnLevels(string[] labels)
            {
                levels = labels;
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                headerRowHeight = Math.Max(Font.Height, ColumnHeadersDefaultCellStyle.Font?.Height ?? 0) + CellPadding / 2;
                ColumnHeadersHeight = headerRowHeight * (levels.Length + 1);
            }

            public void FitColumns(IEnumerable<Dictionary<int, object>> rows, CultureInfo culture)
            {
                var headerFont = ColumnHeadersDefaultCellStyle.Font ?? Font;
                var cellFont = DefaultCellStyle.Font ?? Font;
                int Measure(string text, Font font) =>
                    TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + CellPadding;
                var widths = Columns.Cast<DataGridViewColumn>().Select(column => Measure(column.HeaderText, headerFont)).ToArray();
                var measured = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var row in rows)
                    foreach (var cell in row)
                    {
                        if (cell.Value == null || cell.Value == DBNull.Value) continue;
                        var text = Convert.ToString(cell.Value, culture);
                        if (!measured.TryGetValue(text, out var width))
                            measured.Add(text, width = Measure(text, cellFont));
                        widths[cell.Key] = Math.Max(widths[cell.Key], width);
                    }

                var ordered = OrderedColumns();
                for (var level = 0; level < levels.Length; level++)
                {
                    if (ordered[0].Tag == null)
                        widths[ordered[0].Index] = Math.Max(widths[ordered[0].Index], Measure(levels[level], headerFont));
                    foreach (var span in HeaderSpans(ordered, level))
                    {
                        var width = 0;
                        for (var index = span.First; index <= span.Last; index++) width += widths[ordered[index].Index];
                        widths[ordered[span.First].Index] += Math.Max(0, Measure(span.Label, headerFont) - width);
                    }
                }
                foreach (DataGridViewColumn column in Columns)
                    column.Width = Math.Min(65536, Math.Max(column.MinimumWidth, widths[column.Index]));
            }

            protected override void OnScroll(ScrollEventArgs e)
            {
                base.OnScroll(e);
                if (levels.Length > 0 && e.ScrollOrientation == ScrollOrientation.HorizontalScroll)
                    Invalidate(new Rectangle(0, 0, ClientSize.Width, ColumnHeadersHeight + 2));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (levels.Length == 0 || e.ClipRectangle.Top >= ColumnHeadersHeight + 2) return;

                var ordered = OrderedColumns();
                if (ordered.Length == 0) return;
                var bounds = ordered.Select(column => GetCellDisplayRectangle(column.Index, -1, true)).ToArray();
                var style = ColumnHeadersDefaultCellStyle;
                var font = style.Font ?? Font;
                using (var background = new SolidBrush(style.BackColor))
                using (var border = new Pen(GridColor))
                {
                    void Draw(Rectangle rect, string label)
                    {
                        if (rect.Width <= 0 || rect.Height <= 0 || !e.ClipRectangle.IntersectsWith(rect)) return;
                        e.Graphics.FillRectangle(background, rect);
                        e.Graphics.DrawLine(border, rect.Left, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1);
                        e.Graphics.DrawLine(border, rect.Right - 1, rect.Top, rect.Right - 1, rect.Bottom - 1);
                        var text = Rectangle.Inflate(rect, -CellPadding / 2, 0);
                        if (text.Width > 0 && !string.IsNullOrEmpty(label))
                            TextRenderer.DrawText(e.Graphics, label, font, text, style.ForeColor, HeaderTextFlags);
                    }

                    for (var index = 0; index < ordered.Length; index++)
                    {
                        var rect = bounds[index];
                        if (rect.Width <= 0) continue;
                        Draw(new Rectangle(rect.Left, rect.Top + levels.Length * headerRowHeight, rect.Width, headerRowHeight),
                            ordered[index].HeaderText);
                        if (ordered[index].Tag != null) continue;
                        for (var level = 0; level < levels.Length; level++)
                            Draw(new Rectangle(rect.Left, rect.Top + level * headerRowHeight, rect.Width, headerRowHeight),
                                index == 0 ? levels[level] : string.Empty);
                    }

                    for (var level = 0; level < levels.Length; level++)
                        foreach (var span in HeaderSpans(ordered, level))
                        {
                            var first = span.First;
                            var last = span.Last;
                            while (first <= last && bounds[first].Width <= 0) first++;
                            while (last >= first && bounds[last].Width <= 0) last--;
                            if (first > last) continue;
                            var rect = bounds[first];
                            Draw(new Rectangle(rect.Left, rect.Top + level * headerRowHeight,
                                bounds[last].Right - rect.Left, headerRowHeight), span.Label);
                        }
                }
            }

            private DataGridViewColumn[] OrderedColumns() =>
                Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).OrderBy(column => column.DisplayIndex).ToArray();

            private static IEnumerable<(int First, int Last, string Label)> HeaderSpans(DataGridViewColumn[] ordered, int level)
            {
                for (var first = 0; first < ordered.Length;)
                {
                    if (!(ordered[first].Tag is MatrixHeader header))
                    {
                        first++;
                        continue;
                    }
                    var last = first;
                    while (last + 1 < ordered.Length && ordered[last + 1].Tag is MatrixHeader next &&
                        StructuralComparisons.StructuralEqualityComparer.Equals(header.Keys[level], next.Keys[level]))
                        last++;
                    yield return (first, last, header.Labels[level]);
                    first = last + 1;
                }
            }
        }

        private sealed class Field
        {
            public Field(TabularNamedObject item, string label, PivotFilterDialog.Filter filter = null, string displayName = null)
            {
                Object = item;
                Label = label;
                Filter = filter;
                DisplayName = displayName ?? item.Name;
            }

            public TabularNamedObject Object { get; }
            public string Label { get; }
            public PivotFilterDialog.Filter Filter { get; }
            public string DisplayName { get; }
            public override string ToString() => Filter == null ? Label : $"{Label} {Filter.Summary}";
        }

        private sealed class FieldShelf : GroupBox
        {
            private static readonly Regex SearchTerms = new Regex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);
            private readonly ComboBox picker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            private readonly ListBox list = new ListBox
            {
                Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, AllowDrop = true
            };
            private readonly Button addFilter;
            private readonly bool axis;
            private Point dragStart;
            private int dragIndex = -1;
            private int insertionIndex = -1;
            private bool updatingPicker;
            private Field pendingChoice;

            public FieldShelf(string title, bool isFilter = false, bool axis = false)
            {
                this.axis = axis;
                Text = title;
                Dock = DockStyle.Fill;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = isFilter ? 2 : 1, RowCount = 2 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(picker, 0, 0);
                if (isFilter)
                {
                    layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    addFilter = new Button { Text = "Add", AutoSize = true, Enabled = false };
                    layout.Controls.Add(addFilter, 1, 0);
                    addFilter.Click += (sender, args) => CommitChoice(FilterChoice);
                    picker.SelectedIndexChanged += (sender, args) => UpdateAddFilter();
                    picker.TextChanged += (sender, args) => UpdateAddFilter();
                }
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.Controls.Add(list, 0, 1);
                layout.SetColumnSpan(list, layout.ColumnCount);
                Controls.Add(layout);

                picker.AccessibleName = $"Search {title.ToLowerInvariant()} fields";
                picker.HandleCreated += (sender, args) => picker.SetCueBanner("Search fields");
                list.AccessibleName = "Selected " + title.ToLowerInvariant();
                list.AccessibleDescription = isFilter
                    ? "Press Enter to edit the selected filter or Delete to remove it."
                    : "Drag to reorder fields. Press Delete to remove the selected field.";
                picker.TextUpdate += (sender, args) =>
                {
                    if (updatingPicker) return;
                    var text = picker.Text;
                    // Let the native combo box finish committing its selection before deciding to reopen the list.
                    picker.BeginInvoke((Action)(() =>
                    {
                        if (picker.IsDisposed || pendingChoice != null || !picker.ContainsFocus ||
                            picker.Text != text || SelectedChoice != null) return;
                        RefreshSearch();
                        RestoreSearchCursor();
                    }));
                };
                picker.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Enter) return;
                    if (isFilter) return;
                    var choice = (picker.DroppedDown ? CurrentChoice : SelectedChoice) ??
                        (picker.Items.Count == 1 && !string.IsNullOrWhiteSpace(picker.Text) ? (Field)picker.Items[0] : null);
                    if (choice == null) return;
                    CommitChoice(choice);
                    args.SuppressKeyPress = true;
                };
                picker.DropDown += (sender, args) =>
                {
                    if (!updatingPicker) PickerOpening?.Invoke(this);
                };
                if (!isFilter) picker.SelectionChangeCommitted += (sender, args) => CommitChoice(CurrentChoice);
                list.FontChanged += (sender, args) => list.ItemHeight = list.Font.Height + 8;
                list.ItemHeight = list.Font.Height + 8;
                list.DrawItem += DrawField;
                list.MouseDown += StartDrag;
                list.MouseMove += DragField;
                list.MouseUp += (sender, args) => dragIndex = -1;
                list.DragOver += UpdateDropTarget;
                list.DragLeave += (sender, args) => SetInsertionIndex(-1);
                list.DragDrop += DropField;
                list.KeyDown += (sender, args) =>
                {
                    if (list.SelectedIndex < 0) return;
                    if (args.KeyCode == Keys.Delete) RemoveField(list.SelectedIndex);
                    else if (args.KeyCode == Keys.Enter && isFilter) QueueFilterEdit((Field)list.SelectedItem);
                    else return;
                    args.SuppressKeyPress = true;
                };
            }

            private static Rectangle RemoveBounds(Rectangle item) =>
                new Rectangle(item.Right - item.Height, item.Top, item.Height, item.Height);

            private static Rectangle EditBounds(Rectangle item) =>
                new Rectangle(item.Right - 2 * item.Height, item.Top, item.Height, item.Height);

            private void DrawField(object sender, DrawItemEventArgs args)
            {
                if (args.Index < 0) return;
                var selected = (args.State & DrawItemState.Selected) != 0;
                var dark = AppTheme.IsDark;
                var background = selected ? (dark ? AppTheme.Selection : SystemColors.Highlight) : list.BackColor;
                var foreground = selected && !dark ? SystemColors.HighlightText : list.ForeColor;
                using (var brush = new SolidBrush(background)) args.Graphics.FillRectangle(brush, args.Bounds);
                var remove = RemoveBounds(args.Bounds);
                var field = (Field)list.Items[args.Index];
                var edit = EditBounds(args.Bounds);
                var label = Rectangle.FromLTRB(args.Bounds.Left + 4, args.Bounds.Top,
                    field.Filter == null ? remove.Left : edit.Left, args.Bounds.Bottom);
                var textFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                if (label.Width > 0)
                    TextRenderer.DrawText(args.Graphics, field.ToString(), args.Font, label, foreground,
                        textFlags | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(args.Graphics, "×", args.Font, remove, foreground,
                    textFlags | TextFormatFlags.HorizontalCenter);
                if (field.Filter != null)
                    TextRenderer.DrawText(args.Graphics, "✎", args.Font, edit, foreground,
                        textFlags | TextFormatFlags.HorizontalCenter);
                if (insertionIndex == args.Index || (insertionIndex == list.Items.Count && args.Index == list.Items.Count - 1))
                {
                    var y = insertionIndex == args.Index ? args.Bounds.Top : args.Bounds.Bottom - 1;
                    using (var pen = new Pen(dark ? AppTheme.Accent : SystemColors.Highlight, 2))
                        args.Graphics.DrawLine(pen, args.Bounds.Left, y, args.Bounds.Right, y);
                }
                args.DrawFocusRectangle();
            }

            private void StartDrag(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Left) return;
                var index = list.IndexFromPoint(args.Location);
                if (index < 0) return;
                var bounds = list.GetItemRectangle(index);
                var field = (Field)list.Items[index];
                if (RemoveBounds(bounds).Contains(args.Location))
                {
                    RemoveField(index);
                    return;
                }
                if (field.Filter != null && EditBounds(bounds).Contains(args.Location))
                {
                    QueueFilterEdit(field);
                    return;
                }
                dragStart = args.Location;
                dragIndex = index;
            }

            private void DragField(object sender, MouseEventArgs args)
            {
                if (dragIndex < 0 || args.Button != MouseButtons.Left ||
                    !DaxQueryTabs.OutsideDragThreshold(dragStart, args.Location)) return;
                var drag = new FieldDrag(this, (Field)list.Items[dragIndex]);
                dragIndex = -1;
                list.DoDragDrop(drag, DragDropEffects.Move);
            }

            private void UpdateDropTarget(object sender, DragEventArgs args)
            {
                if (!(args.Data.GetData(typeof(FieldDrag)) is FieldDrag drag) ||
                    (drag.Source != this && !(axis && drag.Source.axis)))
                {
                    args.Effect = DragDropEffects.None;
                    return;
                }
                var point = list.PointToClient(new Point(args.X, args.Y));
                if (list.Items.Count > 0 && list.TopIndex > 0 && point.Y < list.ItemHeight / 2)
                    list.TopIndex--;
                else if (list.Items.Count > 0 && point.Y > list.ClientSize.Height - list.ItemHeight / 2 &&
                    list.TopIndex + Math.Max(1, list.ClientSize.Height / list.ItemHeight) < list.Items.Count)
                    list.TopIndex++;
                var index = list.IndexFromPoint(point);
                if (index < 0) index = list.Items.Count;
                else
                {
                    var bounds = list.GetItemRectangle(index);
                    if (point.Y >= bounds.Top + bounds.Height / 2) index++;
                }
                SetInsertionIndex(index);
                args.Effect = DragDropEffects.Move;
            }

            private void DropField(object sender, DragEventArgs args)
            {
                var target = insertionIndex;
                SetInsertionIndex(-1);
                if (target < 0 || !(args.Data.GetData(typeof(FieldDrag)) is FieldDrag drag)) return;
                var source = drag.Source.list.Items.IndexOf(drag.Field);
                if (source < 0) return;
                if (drag.Source == this && source < target) target--;
                if (drag.Source == this && source == target) return;
                drag.Source.list.Items.RemoveAt(source);
                list.Items.Insert(target, drag.Field);
                list.SelectedIndex = target;
                drag.Source.PickerOpening?.Invoke(drag.Source);
                if (drag.Source != this) PickerOpening?.Invoke(this);
                Changed?.Invoke();
            }

            private void SetInsertionIndex(int index)
            {
                if (insertionIndex == index) return;
                insertionIndex = index;
                list.Invalidate();
            }

            private void RemoveField(int index)
            {
                list.Items.RemoveAt(index);
                if (list.Items.Count > 0) list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
                PickerOpening?.Invoke(this);
                Changed?.Invoke();
            }

            private void QueueFilterEdit(Field field)
            {
                if (field.Filter == null) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || !list.Items.Contains(field) || field.Object.IsRemoved) return;
                    var edited = EditFilter?.Invoke((Column)field.Object, field.Filter);
                    var index = list.Items.IndexOf(field);
                    if (edited == null || index < 0) return;
                    list.Items[index] = new Field(field.Object, field.Label, edited);
                    list.SelectedIndex = index;
                    Changed?.Invoke();
                }));
            }

            private sealed class FieldDrag
            {
                public FieldDrag(FieldShelf source, Field field)
                {
                    Source = source;
                    Field = field;
                }

                public FieldShelf Source { get; }
                public Field Field { get; }
            }

            public Field[] Fields => list.Items.Cast<Field>().ToArray();
            public event Action<FieldShelf> PickerOpening;
            public event Action Changed;
            public Func<Column, PivotFilterDialog.Filter, PivotFilterDialog.Filter> EditFilter { get; set; }

            private Field CurrentChoice
            {
                get
                {
                    var index = picker.SelectedIndex;
                    if (index < 0 || index >= picker.Items.Count) return null;
                    return (Field)picker.Items[index];
                }
            }

            private Field SelectedChoice => CurrentChoice is Field selected && picker.Text == selected.Label ? selected : null;

            private Field FilterChoice => SelectedChoice ??
                (picker.Items.Count == 1 && !string.IsNullOrWhiteSpace(picker.Text) ? (Field)picker.Items[0] : null);

            private void UpdateAddFilter()
            {
                if (addFilter != null && !updatingPicker) addFilter.Enabled = FilterChoice != null;
            }

            private void CommitChoice(Field choice)
            {
                if (updatingPicker || pendingChoice != null || choice == null) return;
                pendingChoice = choice;
                // Let the native popup finish closing before resetting its items and text.
                picker.BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || !ReferenceEquals(pendingChoice, choice)) return;
                    pendingChoice = null;
                    PickerOpening?.Invoke(this);
                    if (!picker.Items.Cast<Field>().Any(field => ReferenceEquals(field.Object, choice.Object))) return;
                    var filter = EditFilter?.Invoke((Column)choice.Object, null);
                    if (EditFilter != null && filter == null) return;
                    list.SelectedIndex = list.Items.Add(new Field(choice.Object, choice.Label, filter));
                    ClearPicker();
                    PickerOpening?.Invoke(this);
                    Changed?.Invoke();
                }));
            }

            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(Point point);

            private void RestoreSearchCursor()
            {
                if (Cursor.Current != null) return;
                var info = new DarkSelectorChrome.ComboBoxInfo { Size = Marshal.SizeOf<DarkSelectorChrome.ComboBoxInfo>() };
                if (!DarkSelectorChrome.GetComboBoxInfo(picker.Handle, ref info)) return;
                var target = WindowFromPoint(Cursor.Position);
                if (target == IntPtr.Zero) return;
                // Restore only the cursor over this search control, without changing the Windows pointer setting.
                if (target == info.EditHandle) Cursor.Current = Cursors.IBeam;
                else if (target == info.ComboHandle || target == info.ListHandle) Cursor.Current = Cursors.Arrow;
            }

            private void RefreshSearch()
            {
                if (updatingPicker) return;
                var text = picker.Text;
                var caret = picker.SelectionStart;
                PickerOpening?.Invoke(this);
                updatingPicker = true;
                try
                {
                    var showResults = picker.Items.Count > 0;
                    if (picker.DroppedDown != showResults) picker.DroppedDown = showResults;
                    if (picker.SelectedIndex >= picker.Items.Count) picker.SelectedIndex = -1;
                    picker.Text = text;
                    picker.SelectionStart = Math.Min(caret, text.Length);
                    picker.SelectionLength = 0;
                }
                finally
                {
                    updatingPicker = false;
                }
                UpdateAddFilter();
            }

            public void SetChoices(IEnumerable<Field> choices)
            {
                var selected = SelectedChoice;
                var text = picker.Text;
                var caret = picker.SelectionStart;
                var selectionLength = picker.SelectionLength;
                var terms = selected == null ? SearchTerms.Matches(text).Cast<Match>().Select(match => match.Value).ToArray()
                    : Array.Empty<string>();
                var comparison = CultureInfo.CurrentCulture.CompareInfo;
                var added = Fields.Select(field => field.Object).ToArray();
                var available = choices.Where(field => !added.Contains(field.Object) &&
                    MatchesInOrder(field.Label, terms, comparison)).ToArray();
                if (available.Length == picker.Items.Count && available.Zip(picker.Items.Cast<Field>(),
                    (next, current) => ReferenceEquals(next.Object, current.Object) && next.Label == current.Label).All(same => same))
                    return;
                updatingPicker = true;
                picker.BeginUpdate();
                try
                {
                    // Closing the native list can restore its old selection. Close it before discarding all choices.
                    if (available.Length == 0 && picker.DroppedDown) picker.DroppedDown = false;
                    picker.SelectedIndex = -1;
                    picker.Items.Clear();
                    picker.Items.AddRange(available);
                    picker.SelectedItem = available.FirstOrDefault(field => ReferenceEquals(field.Object, selected?.Object));
                    picker.Text = text;
                    picker.SelectionStart = Math.Min(caret, text.Length);
                    picker.SelectionLength = Math.Min(selectionLength, text.Length - picker.SelectionStart);
                    picker.DropDownWidth = Math.Max(picker.Width, available.Select(field =>
                        TextRenderer.MeasureText(field.Label, picker.Font).Width + 24)
                        .DefaultIfEmpty(picker.Width).Max());
                }
                finally
                {
                    picker.EndUpdate();
                    updatingPicker = false;
                }
                UpdateAddFilter();
            }

            private static bool MatchesInOrder(string label, string[] terms, CompareInfo comparison)
            {
                var start = 0;
                foreach (var term in terms)
                {
                    var index = comparison.IndexOf(label, term, start, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                    if (index < 0) return false;
                    start = Math.Min(label.Length, index + term.Length);
                }
                return true;
            }

            private void ClearPicker()
            {
                picker.DroppedDown = false;
                picker.SelectedIndex = -1;
                picker.Items.Clear();
                picker.Text = string.Empty;
                UpdateAddFilter();
            }

            public void Reset()
            {
                pendingChoice = null;
                ClearPicker();
                list.Items.Clear();
            }
        }
    }
}
