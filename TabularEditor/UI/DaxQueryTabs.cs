using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI
{
    internal sealed class DaxQueryTabs : UserControl
    {
        private readonly Func<TabularModelHandler> getHandler;
        private readonly List<QueryTab> tabs = new List<QueryTab>();
        private readonly Panel bar = new Panel { Dock = DockStyle.Top, Height = 31 };
        private readonly FlowLayoutPanel strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true
        };
        private readonly Panel content = new Panel { Dock = DockStyle.Fill };
        private readonly Button addButton = HeaderButton("+", "New query");
        private readonly ToolTip toolTip = new ToolTip();
        private readonly ContextMenuStrip queryMenu = new ContextMenuStrip();
        private QueryTab menuTab;
        private QueryTab active;
        private int nextNumber = 1;

        public DaxQueryTabs(Func<TabularModelHandler> getHandler)
        {
            this.getHandler = getHandler;
            Dock = DockStyle.Fill;
            addButton.Margin = new Padding(2, 2, 0, 0);
            addButton.FlatAppearance.BorderSize = 0;
            addButton.Paint += PaintSeparator;
            addButton.Click += (sender, args) => AddTab();
            toolTip.SetToolTip(addButton, "New query");
            strip.Controls.Add(addButton);
            bar.Controls.Add(strip);
            Controls.Add(content);
            Controls.Add(bar);
            strip.SizeChanged += (sender, args) => SizeTabStrip();
            Disposed += (sender, args) =>
            {
                toolTip.Dispose();
                queryMenu.Dispose();
            };
            ConfigureQueryMenu();
            AddTab();
        }

        public DaxQueryEditor ActiveEditor => active?.Editor;

        public void SaveActiveScript()
        {
            if (active != null) SaveTab(active);
        }

        public void CloseActiveTab()
        {
            if (active != null) CloseTab(active);
        }

        public void ApplyTheme()
        {
            BackColor = bar.BackColor = strip.BackColor = AppTheme.IsDark ? AppTheme.Surface : SystemColors.Control;
            AppTheme.ApplyToolTip(toolTip);
            AppTheme.ApplyContextMenu(queryMenu);
            StyleButton(addButton);
            foreach (var tab in tabs)
            {
                StyleTab(tab);
                tab.Editor.ApplyTheme();
            }
        }

        public void ClearResults()
        {
            foreach (var tab in tabs) tab.Editor.ClearResults();
        }

        private void AddTab()
        {
            var tab = new QueryTab(new DaxQueryEditor(getHandler), $"Query {nextNumber++}");
            tabs.Add(tab);
            tab.Editor.Visible = false;
            content.Controls.Add(tab.Editor);
            tab.Editor.ApplyTheme();
            strip.Controls.Add(tab.Header);
            strip.Controls.SetChildIndex(addButton, strip.Controls.Count - 1);
            tab.Header.ContextMenuStrip = queryMenu;
            foreach (Control button in tab.Header.Controls) button.ContextMenuStrip = queryMenu;
            tab.Title.Click += (sender, args) => SelectTab(tab);
            tab.DirtyIndicator.Click += (sender, args) => SelectTab(tab);
            tab.Editor.ScriptChanged += (sender, args) => UpdateDirtyIndicator(tab);
            tab.Title.DoubleClick += (sender, args) => RenameTab(tab);
            tab.Pin.Click += (sender, args) => TogglePin(tab);
            tab.Close.Click += (sender, args) => CloseTab(tab);
            toolTip.SetToolTip(tab.Title, "Double-click to rename");
            toolTip.SetToolTip(tab.Close, "Close query");
            Point? dragStart = null;
            tab.Title.MouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left) return;
                SelectTab(tab);
                dragStart = tab.Title.PointToScreen(args.Location);
            };
            tab.Title.MouseMove += (sender, args) =>
            {
                if (dragStart.HasValue && args.Button == MouseButtons.Left &&
                    OutsideDragThreshold(dragStart.Value, tab.Title.PointToScreen(args.Location)))
                    tab.Title.Cursor = Cursors.SizeWE;
            };
            tab.Title.MouseUp += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || !dragStart.HasValue) return;
                var point = tab.Title.PointToScreen(args.Location);
                if (OutsideDragThreshold(dragStart.Value, point)) MoveTab(tab, point);
                dragStart = null;
                tab.Title.Cursor = Cursors.Default;
            };
            tab.Title.MouseCaptureChanged += (sender, args) =>
            {
                if (Control.MouseButtons == MouseButtons.None) tab.Title.Cursor = Cursors.Default;
            };
            SelectTab(tab);
            SizeTabStrip();
            strip.ScrollControlIntoView(addButton);
        }

        private void ConfigureQueryMenu()
        {
            var pin = queryMenu.Items.Add("Pin", null, (sender, args) => TogglePin(menuTab));
            queryMenu.Items.Add("Save…", null, (sender, args) => SaveTab(menuTab));
            queryMenu.Items.Add("Rename…", null, (sender, args) => RenameTab(menuTab));
            queryMenu.Items.Add(new ToolStripSeparator());
            queryMenu.Items.Add("Close tab", null, (sender, args) => CloseTab(menuTab));
            var closeOthers = queryMenu.Items.Add("Close all other tabs", null, (sender, args) =>
                CloseMany(tabs.Where(tab => tab != menuTab).ToArray(), menuTab, "Close all other query tabs?"));
            queryMenu.Items.Add("Close all tabs", null, (sender, args) =>
                CloseMany(tabs.ToArray(), null, "Close all query tabs?"));
            queryMenu.Opening += (sender, args) =>
            {
                var source = queryMenu.SourceControl;
                menuTab = tabs.FirstOrDefault(tab => source == tab.Header || source?.Parent == tab.Header);
                args.Cancel = menuTab == null;
                if (args.Cancel) return;
                pin.Text = menuTab.Pinned ? "Unpin" : "Pin";
                closeOthers.Enabled = tabs.Count > 1;
            };
        }

        private void SelectTab(QueryTab tab)
        {
            var previous = active;
            if (previous != tab)
            {
                if (previous != null) previous.Editor.Hide();
                active = tab;
                if (previous != null) StyleTab(previous);
                tab.Editor.Show();
                tab.Editor.BringToFront();
            }
            StyleTab(tab);
            strip.ScrollControlIntoView(tab.Header);
        }

        private void TogglePin(QueryTab tab)
        {
            tabs.Remove(tab);
            tab.Pinned = !tab.Pinned;
            var pinnedCount = tabs.Count(item => item.Pinned);
            tabs.Insert(tab.Pinned ? 0 : pinnedCount, tab);
            strip.Controls.SetChildIndex(tab.Header, tabs.IndexOf(tab));
            StyleTab(tab);
            SizeTabStrip();
        }

        private static bool OutsideDragThreshold(Point start, Point current) =>
            Math.Abs(current.X - start.X) >= SystemInformation.DragSize.Width / 2 ||
            Math.Abs(current.Y - start.Y) >= SystemInformation.DragSize.Height / 2;

        private void MoveTab(QueryTab tab, Point screenPoint)
        {
            var point = strip.PointToClient(screenPoint);
            if (!strip.ClientRectangle.Contains(point)) return;
            tabs.Remove(tab);
            var index = tabs.FindIndex(item => point.X < item.Header.Left + item.Header.Width / 2);
            if (index < 0) index = tabs.Count;
            var pinnedCount = tabs.Count(item => item.Pinned);
            index = tab.Pinned ? Math.Min(index, pinnedCount) : Math.Max(index, pinnedCount);
            tabs.Insert(index, tab);
            strip.Controls.SetChildIndex(tab.Header, index);
        }

        private void CloseTab(QueryTab tab)
        {
            var index = tabs.IndexOf(tab);
            if (!tab.IsSaved && MessageBox.Show(this, $"Close “{tab.Title.Text}” without saving?",
                "Close query", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            RemoveTabs(new[] { tab }, active == tab ? null : active, index);
        }

        private void CloseMany(QueryTab[] closing, QueryTab keep, string prompt)
        {
            if (closing.Length == 0) return;
            if (closing.Any(tab => !tab.IsSaved))
                prompt += "\r\n\r\nUnsaved scripts will be lost.";
            if (MessageBox.Show(this, prompt, "Close queries", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                RemoveTabs(closing, keep, 0);
        }

        private void RemoveTabs(QueryTab[] closing, QueryTab keep, int index)
        {
            foreach (var tab in closing)
            {
                if (active == tab) active = null;
                tabs.Remove(tab);
                tab.Editor.Dispose();
                tab.Header.Dispose();
            }
            if (tabs.Count == 0) AddTab();
            else SelectTab(keep ?? tabs[Math.Min(index, tabs.Count - 1)]);
            SizeTabStrip();
        }

        private void SaveTab(QueryTab tab)
        {
            if (tab.SavedPath != null)
            {
                SaveScriptFile(tab, tab.SavedPath);
                return;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var name = new string(tab.Title.Text.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            using (var dialog = new SaveFileDialog
            {
                Filter = "DAX script (*.dax)|*.dax", DefaultExt = "dax", AddExtension = true,
                FileName = Path.ChangeExtension(name, ".dax"),
                RestoreDirectory = true
            })
            {
                dialog.FileOk += (sender, args) =>
                {
                    if (string.Equals(Path.GetExtension(dialog.FileName), ".dax", StringComparison.OrdinalIgnoreCase)) return;
                    args.Cancel = true;
                    MessageBox.Show(this, "Use the .dax extension for query scripts.", "Save query",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                if (dialog.ShowDialog(this) == DialogResult.OK) SaveScriptFile(tab, dialog.FileName);
            }
        }

        private void SaveScriptFile(QueryTab tab, string path)
        {
            try
            {
                var script = tab.Editor.ScriptText;
                var firstSave = tab.SavedPath == null;
                if (tab.HasPendingRename)
                {
                    var renamedPath = Path.Combine(Path.GetDirectoryName(path), tab.Title.Text + Path.GetExtension(path));
                    if (!string.Equals(path, renamedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(renamedPath))
                        throw new IOException($"A file named “{Path.GetFileName(renamedPath)}” already exists.");

                    if (script != tab.SavedScript)
                    {
                        File.WriteAllText(path, script);
                        tab.SavedScript = script;
                    }
                    File.Move(path, renamedPath);
                    path = renamedPath;
                }
                else File.WriteAllText(path, script);

                tab.SavedPath = path;
                tab.SavedScript = script;
                if (firstSave)
                {
                    tab.Title.Text = Path.GetFileNameWithoutExtension(path);
                    SizeTabStrip();
                    if (tab == active) strip.ScrollControlIntoView(tab.Header);
                }
                UpdateDirtyIndicator(tab);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                AppMessageBox.Show(this, error.Message, "Save query failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RenameTab(QueryTab tab)
        {
            var name = PromptForName(this, "Rename query", tab.Title.Text);
            if (name == null) return;
            if (tab.SavedPath != null && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "Enter a name that can be used for a .dax file.", "Rename query",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            tab.Title.Text = name;
            UpdateDirtyIndicator(tab);
            SizeTabStrip();
        }

        internal static string PromptForName(IWin32Window owner, string title, string currentName)
        {
            using (var dialog = new Form
            {
                Text = title, ClientSize = new Size(330, 84), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            })
            {
                var name = new TextBox { Text = currentName, MaxLength = 100, Bounds = new Rectangle(12, 12, 306, 24) };
                var save = new Button { Text = "Rename", DialogResult = DialogResult.OK, Location = new Point(162, 45) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(243, 45) };
                dialog.Controls.AddRange(new Control[] { name, save, cancel });
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                name.TextChanged += (sender, args) => save.Enabled = !string.IsNullOrWhiteSpace(name.Text);
                name.SelectAll();
                AppTheme.Apply(dialog);
                return dialog.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(name.Text)
                    ? name.Text.Trim() : null;
            }
        }

        private void SizeTabStrip()
        {
            if (strip.Width < 1) return;
            var needsScroll = tabs.Sum(tab => tab.Header.Width + tab.Header.Margin.Horizontal) +
                addButton.Width + addButton.Margin.Horizontal > strip.Width;
            bar.Height = 31 + (needsScroll ? SystemInformation.HorizontalScrollBarHeight : 0);
        }

        private void StyleTab(QueryTab tab)
        {
            var selected = tab == active;
            StyleButton(tab.Title, selected);
            StyleButton(tab.Pin, selected);
            StyleButton(tab.Close, selected);
            StyleDirtyIndicator(tab.DirtyIndicator, selected);
            UpdateDirtyIndicator(tab);
            tab.Pin.ForeColor = tab.Pinned && AppTheme.IsDark ? AppTheme.Accent : tab.Title.ForeColor;
            tab.Pin.AccessibleName = tab.Pinned ? "Unpin query" : "Pin query";
            tab.Pin.Invalidate();
            toolTip.SetToolTip(tab.Pin, tab.Pin.AccessibleName);
            tab.Pin.Visible = tab.Pinned;
            tab.Close.Visible = !tab.Pinned;
        }

        private void UpdateDirtyIndicator(QueryTab tab)
        {
            var wasVisible = tab.DirtyIndicator.Visible;
            tab.DirtyIndicator.Visible = !tab.IsSaved;
            if (tab.DirtyIndicator.Visible != wasVisible) SizeTabStrip();
        }

        internal static Label CreateDirtyIndicator() => new Label
        {
            Text = "●", TextAlign = ContentAlignment.MiddleCenter, Size = new Size(14, 27),
            Margin = Padding.Empty, AccessibleName = "Unsaved changes", Visible = false
        };

        internal static void StyleDirtyIndicator(Label indicator, bool selected)
        {
            indicator.BackColor = TabBackColor(selected);
            indicator.ForeColor = AppTheme.IsDark ? AppTheme.Muted : SystemColors.ControlText;
        }

        internal static Color TabSeparatorColor => AppTheme.IsDark ? AppTheme.Border : SystemColors.ControlLight;

        internal static void PaintSeparator(object sender, PaintEventArgs args)
        {
            var control = (Control)sender;
            using (var pen = new Pen(TabSeparatorColor))
                args.Graphics.DrawLine(pen, control.Width - 1, 4, control.Width - 1, control.Height - 5);
        }

        internal static Color TabBackColor(bool selected) => AppTheme.IsDark
            ? (selected ? AppTheme.Raised : AppTheme.Surface)
            : (selected ? SystemColors.Window : SystemColors.Control);

        internal static void StyleButton(Button button, bool selected = false)
        {
            button.BackColor = TabBackColor(selected);
            button.ForeColor = AppTheme.IsDark ? AppTheme.Text : SystemColors.ControlText;
        }

        internal static Button HeaderButton(string text, string description) =>
            new Button
            {
                Text = text, AccessibleName = description, FlatStyle = FlatStyle.Flat,
                Size = new Size(27, 27), Margin = Padding.Empty, UseVisualStyleBackColor = false
            };

        internal static FlowLayoutPanel CreateTabHeader()
        {
            var header = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Margin = new Padding(2, 2, 0, 0), Padding = new Padding(0, 0, 1, 0)
            };
            header.Paint += PaintSeparator;
            return header;
        }

        internal static Button CreateTabTitle(string name) =>
            new TabTitleButton
            {
                Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, MinimumSize = new Size(52, 27), MaximumSize = new Size(220, 27),
                AutoEllipsis = true, Margin = Padding.Empty, UseMnemonic = false, UseVisualStyleBackColor = false
            };

        private sealed class QueryTab
        {
            public readonly DaxQueryEditor Editor;
            public readonly FlowLayoutPanel Header = CreateTabHeader();
            public readonly Button Title;
            public readonly Label DirtyIndicator = CreateDirtyIndicator();
            public readonly Button Pin = HeaderButton(string.Empty, "Pin query");
            public readonly Button Close = HeaderButton("×", "Close query");
            public bool Pinned;
            public string SavedPath;
            public string SavedScript;
            public bool HasPendingRename => SavedPath != null &&
                !string.Equals(Title.Text, Path.GetFileNameWithoutExtension(SavedPath), StringComparison.Ordinal);
            public bool IsSaved => SavedPath != null && !HasPendingRename && Editor.ScriptText == SavedScript;

            public QueryTab(DaxQueryEditor editor, string name)
            {
                Editor = editor;
                Title = CreateTabTitle(name);
                Header.Controls.Add(Title);
                Header.Controls.Add(DirtyIndicator);
                Header.Controls.Add(Pin);
                Header.Controls.Add(Close);
                foreach (var button in new[] { Title, Pin, Close })
                    button.FlatAppearance.BorderSize = 0;
                Pin.Paint += (sender, args) =>
                {
                    var graphics = args.Graphics;
                    var state = graphics.Save();
                    try
                    {
                        graphics.TranslateTransform(Pin.Width / 2f, Pin.Height / 2f);
                        var scale = graphics.DpiX / 96f;
                        graphics.ScaleTransform(scale, scale);
                        using (var pen = new Pen(Pin.ForeColor, 1.25f))
                        {
                            graphics.DrawLines(pen, new[]
                            {
                                new PointF(-3, -5), new PointF(3, -5), new PointF(2, -1),
                                new PointF(4, 2), new PointF(-4, 2), new PointF(-2, -1), new PointF(-3, -5)
                            });
                            graphics.DrawLine(pen, 0, 2, 0, 6);
                        }
                    }
                    finally { graphics.Restore(state); }
                };
            }
        }

        private sealed class TabTitleButton : Button
        {
            public TabTitleButton() => SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
    }
}
