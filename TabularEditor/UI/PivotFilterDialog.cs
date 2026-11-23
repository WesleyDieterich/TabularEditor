using System;
using System.Collections.Generic;
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
    internal sealed class PivotFilterDialog : Form
    {
        private readonly Column column;
        private readonly CultureInfo culture;
        private readonly Func<CancellationToken, Task<object[]>> loadValues;
        private readonly bool date;
        private readonly bool numeric;
        private readonly ComboBox mode = Picker("Filter mode");
        private readonly ComboBox operation = Picker("Filter operator");
        private readonly TextBox operand = new TextBox { Dock = DockStyle.Top, AccessibleName = "Filter value" };
        private readonly TextBox search = new TextBox { Dock = DockStyle.Top, AccessibleName = "Search filter values" };
        private readonly CheckedListBox values = new CheckedListBox
        {
            Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, AccessibleName = "Filter values"
        };
        private readonly CheckBox all = new CheckBox
        {
            Text = "(Select all)", Dock = DockStyle.Top, AutoCheck = false, ThreeState = true
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoSize = true };
        private readonly Button apply = new Button { Text = "Apply", AutoSize = true };
        private readonly Panel basic = new Panel { Dock = DockStyle.Fill };
        private readonly TableLayoutPanel condition = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true
        };
        private readonly DaxQueryEditor.FilterDateField start;
        private readonly DaxQueryEditor.FilterDateField end;
        private readonly FilterCalendar calendar;
        private readonly HashSet<object> selected = new HashSet<object>();
        private Choice[] choices = Array.Empty<Choice>();
        private CancellationTokenSource loading;
        private Task cancelLoading;
        private DaxQueryEditor.FilterDateField calendarTarget;
        private bool loaded;
        private bool refreshing;
        private string loadStatus;

        public PivotFilterDialog(Column column, CultureInfo culture, Func<CancellationToken, Task<object[]>> loadValues,
            Filter previous = null)
        {
            this.column = column;
            this.culture = culture;
            this.loadValues = loadValues;
            date = loadValues != null && column.DataType == DataType.DateTime;
            numeric = loadValues != null && (column.DataType == DataType.Int64 || column.DataType == DataType.Decimal ||
                column.DataType == DataType.Double);

            Text = "Filter " + column.Name;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(385, 365);
            MinimumSize = new Size(350, 320);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            mode.Items.AddRange(new object[] { "Basic", "Advanced" });
            mode.Visible = loadValues != null;
            operation.Items.AddRange(date
                ? new object[] { "On", "Before", "After", "On or before", "On or after", "Between" }
                : numeric
                    ? new object[] { "Equals", "Does not equal", "Greater than", "Greater than or equal", "Less than", "Less than or equal" }
                    : new object[] { "Equals", "Contains", "Starts with", "Ends with", "Does not equal", "Does not contain" });
            operation.SelectedIndex = 0;

            basic.Controls.Add(values);
            basic.Controls.Add(all);
            basic.Controls.Add(search);
            search.SetCueBanner("Search values…");
            operand.SetCueBanner("Filter value");
            condition.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            condition.Controls.Add(operation);
            if (date)
            {
                var format = DaxQueryEditor.NormalizeModelDateFormat(DaxQueryEditor.DisplayFormat(column.FormatString ?? ""));
                start = new DaxQueryEditor.FilterDateField(culture, format, "Filter date");
                end = new DaxQueryEditor.FilterDateField(culture, format, "End date");
                calendar = new FilterCalendar(culture);
                condition.Controls.Add(start);
                condition.Controls.Add(end);
                condition.Controls.Add(calendar);
                start.CalendarButton.Click += (sender, args) => ToggleCalendar(start);
                end.CalendarButton.Click += (sender, args) => ToggleCalendar(end);
                calendar.DateChosen += value =>
                {
                    var target = calendarTarget;
                    if (target == null) return;
                    if (value.HasValue) target.SetDate(value.Value);
                    else target.Input.Clear();
                    calendarTarget = null;
                    UpdateMode();
                    target.Input.Focus();
                };
            }
            else condition.Controls.Add(operand);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            body.Controls.Add(basic);
            body.Controls.Add(condition);
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
            };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.AddRange(new Control[] { cancel, apply });
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(mode, 0, 0);
            layout.Controls.Add(body, 0, 1);
            layout.Controls.Add(status, 0, 2);
            layout.Controls.Add(actions, 0, 3);
            Controls.Add(layout);
            CancelButton = cancel;
            AcceptButton = apply;

            mode.SelectedIndexChanged += (sender, args) => UpdateMode();
            operation.SelectedIndexChanged += (sender, args) => { calendarTarget = null; UpdateMode(); };
            search.TextChanged += (sender, args) => RefreshChoices();
            values.ItemCheck += (sender, args) =>
            {
                if (refreshing) return;
                var item = (Choice)values.Items[args.Index];
                if (args.NewValue == CheckState.Checked) selected.Add(item.Value);
                else selected.Remove(item.Value);
                UpdateSelectAll();
            };
            all.Click += (sender, args) =>
            {
                var visible = values.Items.Cast<Choice>().Select(item => item.Value);
                if (all.CheckState == CheckState.Checked) selected.ExceptWith(visible);
                else selected.UnionWith(visible);
                RefreshChoices();
            };
            apply.Click += (sender, args) => ApplyFilter();
            loadStatus = loadValues == null ? "" : "Loading values from the model…";
            if (previous != null)
            {
                if (previous.SelectedValues != null)
                {
                    selected.UnionWith(previous.SelectedValues);
                    SetChoices(previous.SelectedValues);
                    loaded = true;
                }
                else
                {
                    operation.SelectedItem = previous.Operation;
                    if (previous.FirstDate.HasValue) start?.SetDate(previous.FirstDate.Value);
                    if (previous.LastDate.HasValue) end?.SetDate(previous.LastDate.Value);
                    operand.Text = previous.Operand ?? "";
                }
            }
            mode.SelectedIndex = loadValues != null && (previous == null || previous.SelectedValues != null) ? 0 : 1;
            AppTheme.Apply(this);
            calendar?.ApplyPalette();
            Shown += async (sender, args) => await LoadChoicesAsync();
            FormClosed += (sender, args) =>
            {
                var cancellation = loading;
                if (cancellation != null) cancelLoading = Task.Run(() => cancellation.Cancel());
            };
        }

        public Filter Result { get; private set; }
        private bool IsBasic => mode.SelectedIndex == 0;

        private static ComboBox Picker(string name) => new ComboBox
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name
        };

        private async Task LoadChoicesAsync()
        {
            if (loadValues == null)
            {
                operand.Focus();
                return;
            }

            var source = loading = new CancellationTokenSource();
            try
            {
                var data = await loadValues(source.Token);
                if (IsDisposed || Disposing || cancelLoading != null) return;
                loaded = true;
                SetChoices(data.Concat(selected).Distinct().OrderByDescending(value => selected.Contains(value)));
                loadStatus = choices.Length == 0 ? "No values found." : "";
                UpdateMode();
                if (IsBasic) search.Focus();
            }
            catch (Exception error)
            {
                if (IsDisposed || Disposing || cancelLoading != null) return;
                loadStatus = error.GetBaseException().Message;
                UpdateMode();
            }
            finally
            {
                if (cancelLoading != null)
                {
                    try { await cancelLoading; }
                    catch { /* The dialog has already closed. */ }
                }
                loading = null;
                source.Dispose();
            }
        }

        private void SetChoices(IEnumerable<object> items)
        {
            var format = DaxQueryEditor.DisplayFormat(column.FormatString ?? "");
            if (date) format = DaxQueryEditor.NormalizeModelDateFormat(format);
            choices = items.Select(value => new Choice(value, DisplayValue(value, format))).ToArray();
            RefreshChoices();
        }

        private string DisplayValue(object value, string format)
        {
            if (value == DBNull.Value) return "(Blank)";
            if (value is string text && text.Length == 0) return "(Empty text)";
            if (value is IFormattable formattable)
            {
                try { return formattable.ToString(format, culture); }
                catch (FormatException) { return formattable.ToString(null, culture); }
            }
            return Convert.ToString(value, culture);
        }

        private void RefreshChoices()
        {
            refreshing = true;
            values.BeginUpdate();
            values.Items.Clear();
            foreach (var item in choices.Where(item => culture.CompareInfo.IndexOf(item.Label, search.Text,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
                values.Items.Add(item, selected.Contains(item.Value));
            values.EndUpdate();
            refreshing = false;
            UpdateSelectAll();
        }

        private void UpdateSelectAll()
        {
            var count = values.Items.Cast<Choice>().Count(item => selected.Contains(item.Value));
            all.Enabled = values.Items.Count > 0;
            all.CheckState = count == 0 ? CheckState.Unchecked
                : count == values.Items.Count ? CheckState.Checked : CheckState.Indeterminate;
            if (IsBasic) apply.Enabled = selected.Count > 0;
        }

        private void UpdateMode()
        {
            basic.Visible = IsBasic;
            condition.Visible = !IsBasic;
            if (date)
            {
                end.Visible = operation.Text == "Between";
                calendar.Visible = !IsBasic && calendarTarget != null;
                calendar.ApplyPalette();
            }
            status.Text = IsBasic ? loadStatus : "";
            apply.Enabled = !IsBasic || (loaded && selected.Count > 0);
        }

        private void ToggleCalendar(DaxQueryEditor.FilterDateField field)
        {
            calendarTarget = calendarTarget == field ? null : field;
            if (calendarTarget != null) calendar.ShowDate(field.TryRead(out var value) ? value : (DateTime?)null);
            UpdateMode();
        }

        private void ApplyFilter()
        {
            if (IsBasic)
            {
                if (selected.Count == 0) return;
                var selectedValues = choices.Where(item => selected.Contains(item.Value)).ToArray();
                var literals = string.Join(", ", selectedValues.Select(item => Literal(item.Value)));
                var summary = selectedValues.Length == 1 ? "= " + selectedValues[0].Label : $"({selectedValues.Length:N0} selected)";
                Result = new Filter(summary, name => $"{name} IN {{{literals}}}")
                {
                    SelectedValues = selectedValues.Select(item => item.Value).ToArray()
                };
            }
            else if (date)
            {
                if (!start.TryRead(out var first))
                {
                    status.Text = "Enter a valid date.";
                    start.Input.Focus();
                    return;
                }
                var between = operation.Text == "Between";
                var last = first;
                if (between && (!end.TryRead(out last) || last.Date < first.Date))
                {
                    status.Text = "Enter an end date on or after the start date.";
                    end.Input.Focus();
                    return;
                }
                Result = DateFilter(operation.Text, first.Date, last.Date, culture);
                Result.FirstDate = first.Date;
                Result.LastDate = between ? last.Date : (DateTime?)null;
            }
            else if (numeric)
            {
                var integral = column.DataType == DataType.Int64;
                var style = integral ? NumberStyles.Integer | NumberStyles.AllowThousands
                    : NumberStyles.Number | NumberStyles.AllowExponent;
                if (!decimal.TryParse(operand.Text, style, culture, out var number) ||
                    (integral && (number < long.MinValue || number > long.MaxValue)))
                {
                    status.Text = integral ? "Enter a valid whole number." : "Enter a valid number.";
                    operand.Focus();
                    return;
                }
                Result = NumberFilter(operation.Text, operand.Text, number);
            }
            else
            {
                if (operand.Text.Length == 0 && operation.Text != "Equals")
                {
                    status.Text = "Enter a value.";
                    operand.Focus();
                    return;
                }
                Result = TextFilter(operation.Text, operand.Text);
            }
            Result.LoadModelValues = loadValues != null;
            Result.Operation = operation.Text;
            Result.Operand = operand.Text;
            DialogResult = DialogResult.OK;
        }

        private static Filter TextFilter(string method, string value)
        {
            var operand = PivotView.DaxString(value);
            return new Filter(value.Length == 0 ? "= (Blank)" : $"{method} {value}", name =>
            {
                var text = $"({name} & \"\")";
                switch (method)
                {
                    case "Contains": return $"FIND(LOWER({operand}), LOWER({text}), 1, 0) > 0";
                    case "Does not contain": return $"NOT ISBLANK({name}) && FIND(LOWER({operand}), LOWER({text}), 1, 0) == 0";
                    case "Starts with": return $"LEFT({text}, LEN({operand})) == {operand}";
                    case "Ends with": return $"RIGHT({text}, LEN({operand})) == {operand}";
                    case "Does not equal": return $"NOT ISBLANK({name}) && NOT ({text} == {operand})";
                    default: return value.Length == 0 ? $"ISBLANK({name})" : $"{text} == {operand}";
                }
            });
        }

        private static Filter NumberFilter(string method, string text, decimal number)
        {
            var sign = method switch
            {
                "Does not equal" => "<>",
                "Greater than" => ">",
                "Greater than or equal" => ">=",
                "Less than" => "<",
                "Less than or equal" => "<=",
                _ => "=="
            };
            return new Filter($"{method} {text}", name => $"NOT ISBLANK({name}) && {name} {sign} {Literal(number)}");
        }

        private static Filter DateFilter(string method, DateTime first, DateTime last, CultureInfo culture)
        {
            var summary = $"{method} {first.ToString("d", culture)}";
            if (method == "Between") summary += " – " + last.ToString("d", culture);
            return new Filter(summary, name =>
            {
                // Truncating to a date keeps comparisons inclusive of timestamps on the chosen day.
                var day = $"DATE(YEAR({name}), MONTH({name}), DAY({name}))";
                var comparison = method switch
                {
                    "Before" => $"{day} < {Literal(first)}",
                    "After" => $"{day} > {Literal(first)}",
                    "On or before" => $"{day} <= {Literal(first)}",
                    "On or after" => $"{day} >= {Literal(first)}",
                    "Between" => $"{day} >= {Literal(first)} && {day} <= {Literal(last)}",
                    _ => $"{day} == {Literal(first)}"
                };
                return $"NOT ISBLANK({name}) && {comparison}";
            });
        }

        private static string Literal(object value)
        {
            if (value == DBNull.Value) return "BLANK()";
            if (value is string text) return PivotView.DaxString(text);
            if (value is bool boolean) return boolean ? "TRUE()" : "FALSE()";
            if (value is DateTime date)
                return $"dt\"{date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\"";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal sealed class Filter
        {
            public Filter(string summary, Func<string, string> predicate)
            {
                Summary = summary;
                Predicate = predicate;
            }

            public string Summary { get; }
            public Func<string, string> Predicate { get; }
            public bool LoadModelValues { get; set; }
            public string Operation { get; set; }
            public string Operand { get; set; }
            public DateTime? FirstDate { get; set; }
            public DateTime? LastDate { get; set; }
            public object[] SelectedValues { get; set; }
        }

        private sealed class Choice
        {
            public Choice(object value, string label)
            {
                Value = value;
                Label = label;
            }

            public object Value { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }
    }
}
