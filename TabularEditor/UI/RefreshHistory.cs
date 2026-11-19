using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using Timer = System.Windows.Forms.Timer;

namespace TabularEditor.UI
{
    internal sealed class RefreshHistory : UserControl
    {
        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        private readonly TextBox details = new TextBox
        {
            Dock = DockStyle.Bottom, Height = 138, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical
        };
        private readonly ToolStripButton closeSelected = new ToolStripButton("Close selected");
        private readonly ToolStripButton closeAll = new ToolStripButton("Close all");
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly SemaphoreSlim queue = new SemaphoreSlim(1);

        public RefreshHistory()
        {
            Dock = DockStyle.Fill;
            grid.Columns.Add("Model", "Model");
            grid.Columns.Add("Target", "Target");
            grid.Columns.Add("Mode", "Mode");
            grid.Columns.Add("Status", "Status");
            grid.Columns.Add("Started", "Started");
            grid.Columns.Add("Duration", "Duration");
            foreach (DataGridViewColumn column in grid.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;

            var commands = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            commands.Items.Add(closeSelected);
            commands.Items.Add(closeAll);
            closeSelected.Click += (sender, args) => CloseSelected();
            closeAll.Click += (sender, args) => CloseCompleted();
            grid.SelectionChanged += (sender, args) => UpdateDetails();
            timer.Tick += (sender, args) => UpdateRunningDurations();
            Controls.Add(grid);
            Controls.Add(details);
            Controls.Add(commands);
            Disposed += (sender, args) => timer.Dispose();
            UpdateDetails();
        }

        public event EventHandler Emptied;

        public static string ModeName(RefreshType type)
        {
            switch (type)
            {
                case RefreshType.DataOnly: return "Only Data";
                case RefreshType.ClearValues: return "Clear Values";
                default: return type.ToString();
            }
        }

        public async Task RunAsync(string model, string target, RefreshType type, Func<string> execute)
        {
            var entry = new Entry(model, target, ModeName(type));
            grid.Rows.Insert(0, entry.Model, entry.Target, entry.Mode, entry.Status, "", "");
            var row = grid.Rows[0];
            row.Tag = entry;
            grid.CurrentCell = row.Cells[0];
            UpdateDetails();

            await queue.WaitAsync();
            try
            {
                if (IsDisposed || Disposing) return;
                entry.StartTime = DateTime.Now;
                entry.Status = "Running";
                entry.Clock.Start();
                UpdateRow(row);
                UpdateDetails();
                timer.Start();

                try
                {
                    entry.Messages = await Task.Run(execute);
                    entry.Status = "Completed";
                }
                catch (Exception error)
                {
                    entry.Messages = error.Message;
                    entry.Status = "Failed";
                }
                finally
                {
                    entry.Clock.Stop();
                    entry.EndTime = DateTime.Now;
                    if (!IsDisposed && !Disposing)
                    {
                        UpdateRow(row);
                        UpdateDetails();
                        timer.Stop();
                    }
                }
            }
            finally { queue.Release(); }
        }

        public void CloseSelected()
        {
            var row = grid.CurrentRow;
            if (!(row?.Tag is Entry entry) || !entry.EndTime.HasValue) return;
            grid.Rows.Remove(row);
            AfterClose();
        }

        private void CloseCompleted()
        {
            foreach (var row in grid.Rows.Cast<DataGridViewRow>().Where(row => ((Entry)row.Tag).EndTime.HasValue).ToArray())
                grid.Rows.Remove(row);
            AfterClose();
        }

        private void AfterClose()
        {
            UpdateDetails();
            if (grid.Rows.Count == 0) Emptied?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateRunningDurations()
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Entry entry && entry.Clock.IsRunning) UpdateRow(row);
            if (grid.CurrentRow?.Tag is Entry selected && selected.Clock.IsRunning) UpdateDetails();
        }

        private static void UpdateRow(DataGridViewRow row)
        {
            var entry = (Entry)row.Tag;
            row.Cells[3].Value = entry.Status;
            row.Cells[4].Value = entry.StartTime?.ToString("g");
            row.Cells[5].Value = entry.Duration;
        }

        private void UpdateDetails()
        {
            var entry = grid.CurrentRow?.Tag as Entry;
            closeSelected.Enabled = entry?.EndTime.HasValue == true;
            closeAll.Enabled = grid.Rows.Cast<DataGridViewRow>().Any(row => (row.Tag as Entry)?.EndTime.HasValue == true);
            details.Text = entry == null ? string.Empty :
                $"Model: {entry.Model}\r\nTarget: {entry.Target}\r\nMode: {entry.Mode}\r\nStatus: {entry.Status}" +
                (entry.StartTime.HasValue ? $"\r\nStarted: {entry.StartTime:G}\r\nDuration: {entry.Duration}" : "") +
                (entry.EndTime.HasValue ? $"\r\nFinished: {entry.EndTime:G}" : "") +
                (string.IsNullOrWhiteSpace(entry.Messages) ? "" : $"\r\n\r\n{entry.Messages}");
        }

        private sealed class Entry
        {
            public readonly string Model;
            public readonly string Target;
            public readonly string Mode;
            public readonly Stopwatch Clock = new Stopwatch();
            public DateTime? StartTime;
            public DateTime? EndTime;
            public string Status = "Queued";
            public string Messages;
            public string Duration => StartTime.HasValue ? $"{Clock.Elapsed.TotalSeconds:N1} s" : "";

            public Entry(string model, string target, string mode)
            {
                Model = model;
                Target = target;
                Mode = mode;
            }
        }
    }
}
