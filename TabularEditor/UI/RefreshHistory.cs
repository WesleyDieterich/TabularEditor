using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using TabularEditor.TOMWrapper;
using TabularEditor.UI.Extensions;
using TabularEditor.UIServices;
using Database = TabularEditor.TOMWrapper.Database;
using Timer = System.Windows.Forms.Timer;
using XmlaError = Microsoft.AnalysisServices.XmlaError;
using XmlaWarning = Microsoft.AnalysisServices.XmlaWarning;

namespace TabularEditor.UI
{
    internal sealed class RefreshHistory : UserControl
    {
        private readonly DataGridView grid = new QueryResultGrid
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        private readonly Label storageWarning = new Label { Dock = DockStyle.Bottom, AutoSize = true, Visible = false };
        private readonly string historyDirectory;
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly SemaphoreSlim queue = new SemaphoreSlim(1);

        public RefreshHistory() : this(Path.Combine(Path.GetDirectoryName(Preferences.PREFERENCES_PATH), "RefreshHistory"))
        {
        }

        internal RefreshHistory(string historyDirectory)
        {
            this.historyDirectory = historyDirectory;
            Dock = DockStyle.Fill;
            grid.Columns.Add("Model", "Model");
            grid.Columns.Add("Target", "Target");
            grid.Columns.Add("Mode", "Mode");
            grid.Columns.Add("Status", "Status");
            grid.Columns.Add("Started", "Started");
            grid.Columns.Add("Duration", "Duration");
            var detailsColumn = new DataGridViewButtonColumn
            {
                Name = "Details", HeaderText = "", Text = "Details…", UseColumnTextForButtonValue = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 100, FlatStyle = FlatStyle.Flat
            };
            grid.Columns.Add(detailsColumn);
            foreach (DataGridViewColumn column in grid.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.CellContentClick += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.ColumnIndex == detailsColumn.Index) ShowDetails(args.RowIndex);
            };
            grid.CellDoubleClick += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.ColumnIndex != detailsColumn.Index) ShowDetails(args.RowIndex);
            };
            grid.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter || grid.CurrentRow == null) return;
                ShowDetails(grid.CurrentRow.Index);
                args.SuppressKeyPress = true;
            };
            timer.Tick += (sender, args) => UpdateRunningDurations();
            Controls.Add(grid);
            Controls.Add(storageWarning);
            Disposed += (sender, args) => timer.Dispose();
            LoadHistory();
        }

        public static string ModeName(RefreshType type)
        {
            switch (type)
            {
                case RefreshType.DataOnly: return "Only Data";
                case RefreshType.ClearValues: return "Clear Values";
                default: return type.ToString();
            }
        }

        public async Task RunAsync(string model, string[] targets, RefreshType type, string origin, string command,
            Func<Database.RefreshResult> execute)
        {
            var entry = new Entry(model, targets, ModeName(type), origin, command);
            var row = AddRow(entry, 0);
            grid.CurrentCell = row.Cells[0];
            SaveEntry(entry);

            await queue.WaitAsync();
            try
            {
                if (IsDisposed || Disposing) return;
                entry.StartTime = DateTime.Now;
                entry.QueueDuration = entry.Clock.Elapsed;
                entry.Clock.Restart();
                entry.Status = "Running";
                UpdateRow(row);
                SaveEntry(entry);
                timer.Start();

                try
                {
                    var result = await Task.Run(execute);
                    entry.Messages = FormatMessages(result);
                    entry.Status = !result.Succeeded ? "Failed"
                        : result.Messages.OfType<XmlaWarning>().Any() ? "Completed with warnings" : "Completed";
                }
                catch (Exception error)
                {
                    entry.Messages = "Client error" + Environment.NewLine + NormalizeLines(error.Message);
                    entry.Status = "Failed";
                }
                finally
                {
                    entry.Clock.Stop();
                    entry.ExecutionDuration = entry.Clock.Elapsed;
                    entry.EndTime = DateTime.Now;
                    SaveEntry(entry);
                    if (!IsDisposed && !Disposing)
                    {
                        UpdateRow(row);
                        timer.Stop();
                    }
                }
            }
            finally { queue.Release(); }
        }

        private DataGridViewRow AddRow(Entry entry, int index)
        {
            grid.Rows.Insert(index, entry.Model,
                entry.Targets.Length == 1 ? entry.Targets[0] : $"{entry.Targets.Length:N0} tables",
                entry.Mode, entry.Status, entry.StartTime?.ToString("g"), entry.Duration);
            var row = grid.Rows[index];
            row.Tag = entry;
            return row;
        }

        private void LoadHistory()
        {
            if (!Directory.Exists(historyDirectory)) return;
            var entries = new List<Entry>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(historyDirectory, "*.json"))
                {
                    try
                    {
                        var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(path));
                        if (entry?.Targets == null || entry.ServiceKey != null) continue;
                        if (entry.Status == "Running" || entry.Status == "Queued")
                            entry.Status = "Completion unknown";
                        entry.Clock.Stop();
                        entries.Add(entry);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
                    {
                        ShowStorageWarning("Some refresh history entries could not be read.", error);
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                ShowStorageWarning("Refresh history could not be read.", error);
            }
            foreach (var entry in entries.OrderByDescending(entry => entry.QueuedAt))
                AddRow(entry, grid.Rows.Count);
        }

        private void SaveEntry(Entry entry)
        {
            try
            {
                Directory.CreateDirectory(historyDirectory);
                var path = Path.Combine(historyDirectory, entry.Id.ToString("N") + ".json");
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(entry));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
            {
                ShowStorageWarning("A refresh history entry could not be saved.", error);
            }
        }

        private void ShowStorageWarning(string message, Exception error)
        {
            if (IsDisposed || Disposing) return;
            storageWarning.Text = message + " " + error.Message;
            storageWarning.Visible = true;
        }

        private void UpdateRunningDurations()
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Entry entry && entry.Clock.IsRunning) UpdateRow(row);
        }

        private static void UpdateRow(DataGridViewRow row)
        {
            var entry = (Entry)row.Tag;
            row.Cells[3].Value = entry.Status;
            row.Cells[4].Value = entry.StartTime?.ToString("g");
            row.Cells[5].Value = entry.Duration;
            entry.NotifyChanged();
        }

        private void ShowDetails(int rowIndex)
        {
            if (!(grid.Rows[rowIndex].Tag is Entry entry)) return;
            using (var dialog = new RefreshDetailsDialog(entry))
                dialog.ShowDialog(FindForm());
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class Entry
        {
            [JsonProperty] public Guid Id = Guid.NewGuid();
            [JsonProperty] public string ServiceKey { get; private set; }
            [JsonProperty] public readonly string Model;
            [JsonProperty] public readonly string[] Targets;
            [JsonProperty] public readonly string Mode;
            [JsonProperty] public readonly string Origin;
            [JsonProperty] public readonly string Command;
            [JsonProperty] public DateTime QueuedAt = DateTime.Now;
            [JsonProperty] public DateTime? StartTime;
            [JsonProperty] public DateTime? EndTime;
            [JsonProperty] public string Status = "Queued";
            [JsonProperty] public string Messages;
            [JsonProperty] public TimeSpan QueueDuration;
            [JsonProperty] public TimeSpan? ExecutionDuration;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public TimeSpan? Elapsed => Status == "Running" ? Clock.Elapsed : ExecutionDuration;
            public string Duration => Elapsed is TimeSpan elapsed ? $"{elapsed.TotalSeconds:N1} s" : "";
            public event Action Changed;
            public void NotifyChanged() => Changed?.Invoke();

            [JsonConstructor]
            public Entry(string model, string[] targets, string mode, string origin, string command)
            {
                Model = model;
                Targets = targets;
                Mode = mode;
                Origin = origin;
                Command = command;
            }
        }

        private sealed class RefreshDetailsDialog : Form
        {
            private readonly Entry entry;
            private readonly Label status = ValueLabel();
            private readonly Label started = ValueLabel();
            private readonly Label finished = ValueLabel();
            private readonly Label waiting = ValueLabel();
            private readonly Label duration = ValueLabel();
            private readonly TextBox messages = DetailTextBox("Refresh messages", true);
            private readonly Button copyMessages = new Button { Text = "Copy messages", AutoSize = true };

            public RefreshDetailsDialog(Entry entry)
            {
                this.entry = entry;
                Text = "Refresh details";
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(680, 470);
                MinimumSize = new Size(510, 375);
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.Dpi;

                var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
                var summary = new TabPage("Overview") { Padding = new Padding(14) };
                var facts = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
                facts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                AddFact(facts, "Model", ValueLabel(entry.Model));
                AddFact(facts, "Mode", ValueLabel(entry.Mode));
                AddFact(facts, "Requested from", ValueLabel(entry.Origin));
                AddFact(facts, "Status", status);
                AddFact(facts, "Queued at", ValueLabel(entry.QueuedAt.ToString("G")));
                AddFact(facts, "Started at", started);
                AddFact(facts, "Finished at", finished);
                AddFact(facts, "Time in queue", waiting);
                AddFact(facts, "Duration", duration);

                var targets = new ListBox
                {
                    Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false,
                    AccessibleName = "Requested objects"
                };
                targets.Items.AddRange(entry.Targets);
                var scope = new GroupBox { Text = "Requested objects", Dock = DockStyle.Fill, Padding = new Padding(8) };
                scope.Controls.Add(targets);
                summary.Controls.Add(scope);
                summary.Controls.Add(facts);
                tabs.TabPages.Add(summary);

                tabs.TabPages.Add(TextPage("Messages", messages, copyMessages, () => messages.Text));
                if (!string.IsNullOrWhiteSpace(entry.Command))
                {
                    var command = DetailTextBox("TMSL command", false);
                    command.Text = NormalizeLines(entry.Command);
                    var codeFont = new Font(FontFamily.GenericMonospace, command.Font.Size);
                    command.Font = codeFont;
                    Disposed += (sender, args) => codeFont.Dispose();
                    var copyCommand = new Button { Text = "Copy TMSL", AutoSize = true };
                    tabs.TabPages.Add(TextPage("TMSL", command, copyCommand, () => entry.Command));
                }

                var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
                var actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(10, 6, 10, 6)
                };
                actions.Controls.Add(close);
                Controls.Add(tabs);
                Controls.Add(actions);
                CancelButton = close;
                AppTheme.Apply(this);
                entry.Changed += UpdateContent;
                Disposed += (sender, args) =>
                {
                    entry.Changed -= UpdateContent;
                };
                UpdateContent();
            }

            private static Label ValueLabel(string text = "") => new Label
            {
                AutoSize = true, Text = text, UseMnemonic = false, Margin = new Padding(2, 3, 0, 4)
            };

            private static void AddFact(TableLayoutPanel layout, string label, Label value)
            {
                var key = ValueLabel(label);
                key.Margin = new Padding(0, 3, 20, 4);
                var row = layout.RowCount++;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(key, 0, row);
                layout.Controls.Add(value, 1, row);
            }

            private static TextBox DetailTextBox(string name, bool wrap) => new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = wrap, TabStop = false,
                ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both, AccessibleName = name
            };

            private static TabPage TextPage(string title, TextBox body, Button copy, Func<string> text)
            {
                var page = new TabPage(title) { Padding = new Padding(12) };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                footer.Controls.Add(copy);
                page.Controls.Add(body);
                page.Controls.Add(footer);
                copy.Click += (sender, args) =>
                {
                    var value = text();
                    if (!string.IsNullOrWhiteSpace(value)) Clipboard.SetText(value);
                };
                return page;
            }

            private void UpdateContent()
            {
                status.Text = entry.Status;
                started.Text = entry.StartTime?.ToString("G") ?? "—";
                finished.Text = entry.EndTime?.ToString("G") ?? "—";
                waiting.Text = entry.StartTime.HasValue ? FormatElapsed(entry.QueueDuration)
                    : entry.Clock.IsRunning ? FormatElapsed(entry.Clock.Elapsed) : "—";
                duration.Text = entry.Elapsed is TimeSpan elapsed ? FormatElapsed(elapsed) : "—";
                var content = entry.Messages;
                copyMessages.Enabled = !string.IsNullOrWhiteSpace(content);
                var display = copyMessages.Enabled ? content : entry.EndTime.HasValue
                    ? "The server returned no messages."
                    : entry.Status == "Completion unknown" ? "No completion was recorded."
                    : "Messages will appear after the refresh finishes.";
                if (messages.Text != display) messages.Text = display;
            }

            private static string FormatElapsed(TimeSpan time) =>
                time.Days > 0 ? time.ToString(@"d\.hh\:mm\:ss") : time.ToString(@"hh\:mm\:ss\.f");
        }

        private static string FormatMessages(Database.RefreshResult result)
        {
            var contents = new StringBuilder();
            foreach (var message in result.Messages)
            {
                if (contents.Length > 0) contents.AppendLine().AppendLine();
                if (message is XmlaError error) contents.Append("Error · ").Append(error.ErrorCode);
                else if (message is XmlaWarning warning) contents.Append("Warning · ").Append(warning.WarningCode);
                else contents.Append("Information");
                if (!string.IsNullOrWhiteSpace(message.Source)) contents.Append(" · ").Append(message.Source);
                contents.AppendLine().Append(NormalizeLines(message.Description));
            }

            var failure = NormalizeLines(result.Failure);
            if (!string.IsNullOrWhiteSpace(failure) && !contents.ToString().Contains(failure))
            {
                if (contents.Length > 0) contents.AppendLine().AppendLine();
                contents.AppendLine("Server error").Append(failure);
            }
            if (contents.Length == 0 && !result.Succeeded)
                contents.Append("The server reported a refresh error.");
            return contents.ToString();
        }

        private static string NormalizeLines(string text) =>
            text?.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine) ?? "";
    }
}
