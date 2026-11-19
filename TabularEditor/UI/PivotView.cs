using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
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
        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        private readonly Label status = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        private readonly Button run = new Button { Text = "Calculate", AutoSize = true };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
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

            var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
            commands.Controls.AddRange(new Control[] { run, cancel, status });
            Controls.Add(grid);
            Controls.Add(commands);
            Controls.Add(shelves);
            run.Click += async (sender, args) => await ExecuteAsync();
            cancel.Click += (sender, args) => CancelExecution();
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
                choices = model.AllMeasures.Where(measure => !measure.IsHidden)
                    .Select(measure => new Field(measure, measure.Table.DaxTableName + measure.DaxObjectName));
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
            var resultFields = axes.Concat(valueFields).ToArray();
            var fields = resultFields.Concat(filterFields);
            if (fields.Any(field => field.Object.IsRemoved))
            {
                status.Text = "Remove fields that are no longer in the model.";
                return;
            }

            var version = modelVersion;
            var tokenSource = execution = new CancellationTokenSource();
            run.Enabled = shelves.Enabled = false;
            cancel.Enabled = true;
            status.Text = "Calculating…";
            try
            {
                var catalog = handler.Database.Name;
                var connectionString = handler.GetQueryConnectionString(catalog);
                var display = resultFields.Select(DisplayExpression).ToArray();
                var query = BuildQuery(axes, valueFields, filterFields, display);
                using (var data = await Task.Run(() => Database.ExecuteDax(connectionString, query, tokenSource.Token)))
                {
                    if (CanShowResult(handler, version) && cancellation == null)
                        ShowMatrix(data.Tables[0], rowFields, columnFields, valueFields, display, handler.Model.Culture);
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
                    run.Enabled = shelves.Enabled = true;
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

        private static string DisplayExpression(Field field)
        {
            string value, format;
            if (field.Object is Column column)
            {
                value = $"SELECTEDVALUE({column.DaxObjectFullName})";
                format = string.IsNullOrEmpty(column.FormatString) ? null : DaxString(column.FormatString);
            }
            else
            {
                var measure = (Measure)field.Object;
                value = measure.DaxObjectName;
                format = string.IsNullOrWhiteSpace(measure.FormatStringExpression)
                    ? (string.IsNullOrEmpty(measure.FormatString) ? null : DaxString(measure.FormatString))
                    : $"{measure.Table.DaxTableName}[_{measure.Name.Replace("]", "]]")} FormatString]";
            }
            return format == null ? null : $"IGNORE(IF(ISBLANK({value}), BLANK(), FORMAT({value}, {format})))";
        }

        private static string DaxString(string text) => $"\"{text.Replace("\"", "\"\"")}\"";

        private static string BuildQuery(Field[] axes, Field[] measures, Field[] filters, string[] display)
        {
            var groups = axes.Select(field => ((Column)field.Object).DaxObjectFullName).ToArray();
            var values = measures.Select((field, index) => $"\"__PivotValue{index}\", {((Measure)field.Object).DaxObjectName}");
            var formattedValues = display.Select((expression, index) =>
                expression == null ? null : $"\"__PivotDisplay{index}\", {expression}").Where(expression => expression != null);
            var query = $"SUMMARIZECOLUMNS({string.Join(",\n    ", groups.Concat(values).Concat(formattedValues))})";
            if (filters.Length > 0)
            {
                var filterExpressions = filters.Select(field =>
                {
                    var column = ((Column)field.Object).DaxObjectFullName;
                    var comparison = field.Value.Length == 0
                        ? $"ISBLANK({column})"
                        : $"({column} & \"\") == {DaxString(field.Value)}";
                    return $"KEEPFILTERS(FILTER(ALL({column}), {comparison}))";
                });
                query = $"CALCULATETABLE({query},\n    {string.Join(",\n    ", filterExpressions)})";
            }
            if (groups.Length == 0) return $"EVALUATE\n{query}";

            var topOrder = string.Join(", ", groups.Select(group => $"{group}, ASC"));
            var resultOrder = string.Join(", ", groups.Select(group => $"{group} ASC"));
            return $"EVALUATE\nTOPN({GroupLimit + 1}, {query}, {topOrder})\nORDER BY {resultOrder}";
        }

        private void ShowMatrix(DataTable data, Field[] rowFields, Field[] columnFields, Field[] valueFields,
            string[] display, string modelCulture)
        {
            if (data.Rows.Count > GroupLimit)
            {
                status.Text = $"More than {GroupLimit:N0} combinations. Apply filters or remove fields.";
                return;
            }

            var rowKeys = new Dictionary<object[], int>(new AxisComparer());
            var columnKeys = new Dictionary<object[], int>(new AxisComparer());
            var headers = rowFields.Select(field => field.Object.Name).ToList();
            if (columnFields.Length == 0)
                headers.AddRange(valueFields.Select(field => field.Object.Name));

            var firstMeasure = rowFields.Length + columnFields.Length;
            var nextDisplayColumn = display.Length;
            var displayColumns = display.Select((expression, index) => expression == null ? index : nextDisplayColumn++).ToArray();
            var culture = string.IsNullOrEmpty(modelCulture) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(modelCulture);
            var result = new List<Dictionary<int, object>>();
            foreach (DataRow record in data.Rows)
            {
                var values = record.ItemArray;
                var rowKey = values.Take(rowFields.Length).ToArray();
                var columnKey = values.Skip(rowFields.Length).Take(columnFields.Length).ToArray();
                if (!rowKeys.TryGetValue(rowKey, out var rowIndex))
                {
                    rowIndex = result.Count;
                    rowKeys.Add(rowKey, rowIndex);
                    var row = new Dictionary<int, object>();
                    for (var index = 0; index < rowKey.Length; index++) row.Add(index, values[displayColumns[index]]);
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
                        var label = string.Join(" / ", displayColumns.Skip(rowFields.Length).Take(columnFields.Length)
                            .Select(index => values[index] == DBNull.Value ? "(Blank)" : Convert.ToString(values[index], culture)));
                        headers.AddRange(valueFields.Select(field => $"{label} | {field.Object.Name}"));
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
            foreach (var header in headers)
            {
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = header, Width = 145, SortMode = DataGridViewColumnSortMode.NotSortable,
                    DefaultCellStyle = new DataGridViewCellStyle { FormatProvider = culture }
                });
            }
            matrix.AddRange(result);
            grid.RowCount = matrix.Count;
            status.Text = $"{matrix.Count:N0} rows";
        }

        private sealed class AxisComparer : IEqualityComparer<object[]>
        {
            public bool Equals(object[] left, object[] right) => StructuralComparisons.StructuralEqualityComparer.Equals(left, right);
            public int GetHashCode(object[] key) => StructuralComparisons.StructuralEqualityComparer.GetHashCode(key);
        }

        private sealed class Field
        {
            public Field(TabularNamedObject item, string label, string value = null)
            {
                Object = item;
                Label = label;
                Value = value;
            }

            public TabularNamedObject Object { get; }
            public string Label { get; }
            public string Value { get; }
            public override string ToString() => Value == null ? Label : $"{Label} = {(Value.Length == 0 ? "(Blank)" : Value)}";
        }

        private sealed class FieldShelf : GroupBox
        {
            private readonly ComboBox picker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            private readonly Button add = new Button { Text = "Add", AutoSize = true, Dock = DockStyle.Fill, Enabled = false };
            private readonly ListBox list = new ListBox
            {
                Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, AllowDrop = true
            };
            private readonly TextBox filterValue;
            private readonly bool axis;
            private Point dragStart;
            private int dragIndex = -1;
            private int insertionIndex = -1;
            private bool updatingPicker;

            public FieldShelf(string title, bool isFilter = false, bool axis = false)
            {
                this.axis = axis;
                Text = title;
                Dock = DockStyle.Fill;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = isFilter ? 3 : 2 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(picker, 0, 0);
                layout.Controls.Add(add, 1, 0);
                var listRow = 1;
                if (isFilter)
                {
                    filterValue = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Filter value as text (empty matches blank)" };
                    filterValue.SetCueBanner("Value as text (empty = blank)");
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    layout.Controls.Add(filterValue, 0, listRow++);
                    layout.SetColumnSpan(filterValue, 2);
                }
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.Controls.Add(list, 0, listRow);
                layout.SetColumnSpan(list, 2);
                Controls.Add(layout);

                picker.AccessibleName = $"Search {title.ToLowerInvariant()} fields";
                picker.HandleCreated += (sender, args) => picker.SetCueBanner("Search fields");
                list.AccessibleName = "Selected " + title.ToLowerInvariant();
                list.AccessibleDescription = "Drag to reorder fields. Press Delete to remove the selected field.";
                picker.TextUpdate += (sender, args) => RefreshSearch();
                picker.TextChanged += (sender, args) =>
                {
                    if (!updatingPicker) add.Enabled = Candidate != null;
                };
                picker.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Enter || picker.DroppedDown || !add.Enabled) return;
                    add.PerformClick();
                    args.SuppressKeyPress = true;
                };
                picker.DropDown += (sender, args) =>
                {
                    if (!updatingPicker) PickerOpening?.Invoke(this);
                };
                picker.SelectedIndexChanged += (sender, args) =>
                {
                    if (!updatingPicker) add.Enabled = Candidate != null;
                };
                add.Click += (sender, args) =>
                {
                    var selected = Candidate;
                    PickerOpening?.Invoke(this);
                    if (selected == null || !ReferenceEquals(selected.Object, Candidate?.Object)) return;
                    list.SelectedIndex = list.Items.Add(new Field(selected.Object, selected.Label, filterValue?.Text));
                    ClearPicker();
                    filterValue?.Clear();
                    PickerOpening?.Invoke(this);
                    Changed?.Invoke();
                };
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
                    if (args.KeyCode != Keys.Delete || list.SelectedIndex < 0) return;
                    RemoveField(list.SelectedIndex);
                    args.SuppressKeyPress = true;
                };
            }

            private static Rectangle RemoveBounds(Rectangle item) =>
                new Rectangle(item.Right - item.Height, item.Top, item.Height, item.Height);

            private void DrawField(object sender, DrawItemEventArgs args)
            {
                if (args.Index < 0) return;
                var selected = (args.State & DrawItemState.Selected) != 0;
                var dark = AppTheme.IsDark;
                var background = selected ? (dark ? AppTheme.Selection : SystemColors.Highlight) : list.BackColor;
                var foreground = selected && !dark ? SystemColors.HighlightText : list.ForeColor;
                using (var brush = new SolidBrush(background)) args.Graphics.FillRectangle(brush, args.Bounds);
                var remove = RemoveBounds(args.Bounds);
                var label = Rectangle.FromLTRB(args.Bounds.Left + 4, args.Bounds.Top, remove.Left, args.Bounds.Bottom);
                var textFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(args.Graphics, list.Items[args.Index].ToString(), args.Font, label, foreground,
                    textFlags | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(args.Graphics, "×", args.Font, remove, foreground,
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
                if (RemoveBounds(list.GetItemRectangle(index)).Contains(args.Location))
                {
                    RemoveField(index);
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

            private Field SelectedChoice
            {
                get
                {
                    var index = picker.SelectedIndex;
                    if (index < 0 || index >= picker.Items.Count) return null;
                    var selected = (Field)picker.Items[index];
                    return picker.Text == selected.Label ? selected : null;
                }
            }

            private Field Candidate => SelectedChoice ??
                (picker.Items.Count == 1 && !string.IsNullOrWhiteSpace(picker.Text) ? (Field)picker.Items[0] : null);

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
                add.Enabled = Candidate != null;
            }

            public void SetChoices(IEnumerable<Field> choices)
            {
                var selected = SelectedChoice;
                var text = picker.Text;
                var caret = picker.SelectionStart;
                var selectionLength = picker.SelectionLength;
                var searchText = selected == null ? text.Trim() : string.Empty;
                var added = Fields.Select(field => field.Object).ToArray();
                var available = choices.Where(field => !added.Contains(field.Object) &&
                    CultureInfo.CurrentCulture.CompareInfo.IndexOf(field.Label, searchText,
                        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0).ToArray();
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
                add.Enabled = Candidate != null;
            }

            private void ClearPicker()
            {
                picker.DroppedDown = false;
                picker.SelectedIndex = -1;
                picker.Items.Clear();
                picker.Text = string.Empty;
                add.Enabled = false;
            }

            public void Reset()
            {
                ClearPicker();
                list.Items.Clear();
                filterValue?.Clear();
            }
        }
    }
}
