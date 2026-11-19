using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace TabularEditor.UI
{
    internal sealed class FilterCalendar : Panel
    {
        private readonly CultureInfo culture;
        private readonly Label monthLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button previous = MakeButton("←", "Previous month");
        private readonly Button next = MakeButton("→", "Next month");
        private readonly Button clear = MakeButton("Clear", "Clear date");
        private readonly Button today = MakeButton("Today", "Select today's date");
        private readonly Label[] weekdays = new Label[7];
        private readonly Button[] days = new Button[42];
        private readonly DateTime minimum;
        private readonly DateTime maximum;
        private DateTime month;
        private DateTime? selected;

        public FilterCalendar(CultureInfo culture)
        {
            this.culture = culture;
            minimum = culture.DateTimeFormat.Calendar.MinSupportedDateTime.Date;
            maximum = culture.DateTimeFormat.Calendar.MaxSupportedDateTime.Date;
            Dock = DockStyle.Top;
            Height = 218;
            Margin = new Padding(3, 2, 3, 2);
            Padding = new Padding(5, 2, 5, 2);
            Visible = false;

            var header = new Panel { Dock = DockStyle.Top, Height = 27 };
            previous.Dock = next.Dock = DockStyle.Right;
            previous.Width = next.Width = 26;
            previous.Click += (sender, args) => ChangeMonth(-1);
            next.Click += (sender, args) => ChangeMonth(1);
            header.Controls.Add(monthLabel);
            header.Controls.Add(previous);
            header.Controls.Add(next);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 27 };
            clear.Dock = DockStyle.Left;
            today.Dock = DockStyle.Right;
            clear.Width = today.Width = 52;
            clear.Click += (sender, args) => DateChosen?.Invoke(null);
            today.Click += (sender, args) => DateChosen?.Invoke(DateTime.Today);
            footer.Controls.Add(clear);
            footer.Controls.Add(today);

            var dates = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 7, Margin = Padding.Empty };
            for (var i = 0; i < 7; i++)
            {
                dates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
                dates.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7));
                var dayOfWeek = (DayOfWeek)(((int)culture.DateTimeFormat.FirstDayOfWeek + i) % 7);
                weekdays[i] = new Label
                {
                    Text = culture.DateTimeFormat.GetAbbreviatedDayName(dayOfWeek).TrimEnd('.'),
                    Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter
                };
                dates.Controls.Add(weekdays[i], i, 0);
            }
            for (var i = 0; i < days.Length; i++)
            {
                var button = MakeButton("", "");
                button.Dock = DockStyle.Fill;
                button.TabStop = false;
                button.Click += (sender, args) =>
                {
                    if (button.Tag is DateTime date) DateChosen?.Invoke(date);
                };
                button.PreviewKeyDown += (sender, args) =>
                {
                    if (args.KeyCode == Keys.Enter || DayOffset(args.KeyCode) != 0) args.IsInputKey = true;
                };
                button.KeyDown += (sender, args) =>
                {
                    if (!(button.Tag is DateTime day)) return;
                    if (args.KeyCode == Keys.Enter)
                    {
                        args.SuppressKeyPress = true;
                        DateChosen?.Invoke(day);
                        return;
                    }
                    var offset = DayOffset(args.KeyCode);
                    if (offset == 0) return;
                    args.SuppressKeyPress = true;
                    var ticks = day.Ticks + offset * TimeSpan.TicksPerDay;
                    if (ticks < minimum.Ticks || ticks > maximum.Ticks) return;
                    var date = new DateTime(ticks);
                    month = new DateTime(date.Year, date.Month, 1);
                    UpdateMonth();
                    foreach (var dayButton in days)
                    {
                        dayButton.TabStop = dayButton.Tag is DateTime candidate && candidate == date;
                        if (dayButton.TabStop) dayButton.Focus();
                    }
                };
                days[i] = button;
                dates.Controls.Add(button, i % 7, i / 7 + 1);
            }

            Controls.Add(dates);
            Controls.Add(footer);
            Controls.Add(header);
            ShowDate(null);
        }

        public event Action<DateTime?> DateChosen;

        public void ShowDate(DateTime? date)
        {
            selected = date?.Date;
            var day = selected ?? DateTime.Today;
            day = day < minimum ? minimum : day > maximum ? maximum : day;
            month = new DateTime(day.Year, day.Month, 1);
            UpdateMonth();
        }

        private void ChangeMonth(int offset)
        {
            month = month.AddMonths(offset);
            UpdateMonth();
        }

        private void UpdateMonth()
        {
            monthLabel.Text = (month < minimum ? minimum : month).ToString("Y", culture);
            previous.Enabled = month > minimum;
            next.Enabled = month.Year != maximum.Year || month.Month != maximum.Month;
            var offset = (7 + (int)month.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek) % 7;
            for (var i = 0; i < days.Length; i++)
            {
                var ticks = month.Ticks + (i - offset) * TimeSpan.TicksPerDay;
                var date = ticks < minimum.Ticks || ticks > maximum.Ticks ? (DateTime?)null : new DateTime(ticks);
                var button = days[i];
                button.Tag = date;
                button.Enabled = date.HasValue;
                button.Text = date?.Day.ToString(culture) ?? "";
                button.AccessibleName = date?.ToString("D", culture) ?? "";
                button.TabStop = date == (selected?.Year == month.Year && selected?.Month == month.Month
                    ? selected : (month < minimum ? minimum : month));
            }
            ApplyPalette();
        }

        public void ApplyPalette()
        {
            var dark = AppTheme.IsDark;
            var surface = dark ? AppTheme.Window : SystemColors.Window;
            var text = dark ? AppTheme.Text : SystemColors.WindowText;
            var muted = dark ? AppTheme.Muted : SystemColors.GrayText;
            var accent = dark ? AppTheme.Accent : SystemColors.HotTrack;
            var hover = dark ? AppTheme.Raised : SystemColors.ControlLight;
            BackColor = surface;
            foreach (Control section in Controls) section.BackColor = surface;
            monthLabel.BackColor = surface;
            monthLabel.ForeColor = text;
            foreach (var label in weekdays)
            {
                label.BackColor = surface;
                label.ForeColor = muted;
            }
            foreach (var button in new[] { previous, next, clear, today })
                StyleButton(button, surface, button == clear || button == today ? accent : text, hover);

            foreach (var button in days)
            {
                var date = button.Tag as DateTime?;
                var isSelected = date.HasValue && date == selected;
                var isToday = date == DateTime.Today;
                StyleButton(button, isSelected ? (dark ? AppTheme.Selection : SystemColors.Highlight) : surface,
                    isSelected && !dark ? SystemColors.HighlightText : date?.Month == month.Month ? text : muted,
                    hover);
                button.FlatAppearance.BorderSize = isToday && !isSelected ? 1 : 0;
                button.FlatAppearance.BorderColor = accent;
            }
        }

        private static int DayOffset(Keys key)
        {
            switch (key)
            {
                case Keys.Left: return -1;
                case Keys.Right: return 1;
                case Keys.Up: return -7;
                case Keys.Down: return 7;
                default: return 0;
            }
        }

        private static Button MakeButton(string text, string accessibleName) => new Button
        {
            Text = text, AccessibleName = accessibleName, FlatStyle = FlatStyle.Flat,
            Margin = Padding.Empty, UseVisualStyleBackColor = false
        };

        private static void StyleButton(Button button, Color background, Color foreground, Color hover)
        {
            button.BackColor = background;
            button.ForeColor = foreground;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.FlatAppearance.MouseDownBackColor = hover;
        }
    }
}
