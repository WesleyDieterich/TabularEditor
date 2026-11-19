using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace TabularEditor.UI
{
    public partial class UIController
    {
        private readonly List<ExpressionTab> expressionTabs = new List<ExpressionTab>();
        private readonly FlowLayoutPanel expressionTabStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, WrapContents = false, AutoScroll = true, Visible = false, Height = 31
        };
        private readonly ContextMenuStrip expressionTabMenu = new ContextMenuStrip();
        private ExpressionTab activeExpressionTab;
        private ExpressionTab expressionMenuTab;

        private void InitializeExpressionTabs()
        {
            UI.ExpressionEditor.Parent.Controls.Add(expressionTabStrip);
            expressionTabStrip.SizeChanged += (sender, args) => SizeExpressionTabStrip();
            UI.FormMain.Disposed += (sender, args) => expressionTabMenu.Dispose();
            var save = expressionTabMenu.Items.Add("Save", null, (sender, args) => SaveExpressionTabs(new[] { expressionMenuTab }));
            var saveAll = expressionTabMenu.Items.Add("Save all", null, (sender, args) => SaveExpressionTabs(expressionTabs.ToArray()));
            var rename = expressionTabMenu.Items.Add("Rename…", null, (sender, args) => RenameExpressionTab(expressionMenuTab));
            expressionTabMenu.Items.Add(new ToolStripSeparator());
            expressionTabMenu.Items.Add("Close tab", null, (sender, args) => CloseExpressionTab(expressionMenuTab));
            var closeOthers = expressionTabMenu.Items.Add("Close all other tabs", null, (sender, args) =>
                CloseExpressionTabs(expressionTabs.Where(tab => tab != expressionMenuTab).ToArray(),
                    expressionMenuTab, "Close all other expression tabs?"));
            expressionTabMenu.Items.Add("Close all tabs", null, (sender, args) =>
                CloseExpressionTabs(expressionTabs.ToArray(), null, "Close all expression tabs?"));
            expressionTabMenu.Opening += (sender, args) =>
            {
                var source = expressionTabMenu.SourceControl;
                expressionMenuTab = expressionTabs.FirstOrDefault(tab => source == tab.Header || source?.Parent == tab.Header);
                args.Cancel = expressionMenuTab == null;
                if (args.Cancel) return;
                save.Enabled = expressionMenuTab.IsDirty && !expressionMenuTab.Object.IsRemoved;
                saveAll.Enabled = expressionTabs.Any(tab => tab.IsDirty && !tab.Object.IsRemoved);
                rename.Enabled = CanRenameExpressionTab(expressionMenuTab);
                closeOthers.Enabled = expressionTabs.Count > 1;
            };
        }

        private void ApplyExpressionTabTheme()
        {
            expressionTabStrip.BackColor = DaxQueryTabs.TabBackColor(false);
            AppTheme.ApplyContextMenu(expressionTabMenu);
            foreach (var tab in expressionTabs) UpdateExpressionTab(tab);
        }

        private void OpenExpressionTab(IExpressionObject obj)
        {
            var tab = expressionTabs.FirstOrDefault(item => ReferenceEquals(item.Object, obj));
            if (tab == null)
            {
                tab = new ExpressionTab(obj);
                var target = tab;
                tab.Header.ContextMenuStrip = expressionTabMenu;
                tab.Title.ContextMenuStrip = expressionTabMenu;
                tab.DirtyIndicator.ContextMenuStrip = expressionTabMenu;
                tab.Close.ContextMenuStrip = expressionTabMenu;
                tab.Title.Click += (sender, args) => ExpressionEditor_Preview(target.Object);
                tab.DirtyIndicator.Click += (sender, args) => ExpressionEditor_Preview(target.Object);
                tab.Title.DoubleClick += (sender, args) => RenameExpressionTab(target);
                tab.Close.Click += (sender, args) => CloseExpressionTab(target);
                obj.PropertyChanged += ExpressionEditor_Current_PropertyChanged;
                expressionTabs.Add(tab);
                expressionTabStrip.Controls.Add(tab.Header);
                expressionTabStrip.Show();
            }
            SelectExpressionTab(tab);
        }

        private void SelectExpressionTab(ExpressionTab tab)
        {
            var previous = activeExpressionTab;
            if (previous != tab)
            {
                LoadExpressionTab(tab);
                if (previous != null) UpdateExpressionTab(previous);
            }
            if (tab != null)
            {
                UpdateExpressionTab(tab);
                SizeExpressionTabStrip();
                expressionTabStrip.ScrollControlIntoView(tab.Header);
            }
        }

        private void UpdateExpressionTab(ExpressionTab tab)
        {
            tab.Title.Text = tab.Name;
            tab.Title.AccessibleName = tab.Name;
            DaxQueryTabs.StyleButton(tab.Title, tab == activeExpressionTab);
            DaxQueryTabs.StyleButton(tab.Close, tab == activeExpressionTab);
            DaxQueryTabs.StyleDirtyIndicator(tab.DirtyIndicator, tab == activeExpressionTab);
            var wasVisible = tab.DirtyIndicator.Visible;
            tab.DirtyIndicator.Visible = tab.IsDirty;
            if (tab.DirtyIndicator.Visible != wasVisible) SizeExpressionTabStrip();
        }

        private void SizeExpressionTabStrip()
        {
            if (expressionTabStrip.Width < 1) return;
            var overflow = expressionTabs.Sum(tab => tab.Header.Width + tab.Header.Margin.Horizontal) > expressionTabStrip.Width;
            expressionTabStrip.Height = 31 + (overflow ? SystemInformation.HorizontalScrollBarHeight : 0);
        }

        private bool CanRenameExpressionTab(ExpressionTab tab) =>
            !tab.Object.IsRemoved && Handler.PowerBIGovernance.AllowEditProperty(tab.Object.ObjectType, nameof(ITabularNamedObject.Name));

        private void RenameExpressionTab(ExpressionTab tab)
        {
            if (!CanRenameExpressionTab(tab)) return;
            var name = DaxQueryTabs.PromptForName(UI.FormMain, "Rename " + tab.Object.ObjectType, tab.Name);
            if (name == null) return;
            tab.NewName = name == tab.Object.Name ? null : name;
            UpdateExpressionTab(tab);
            SizeExpressionTabStrip();
        }

        private void SaveExpressionTabs(ExpressionTab[] candidates)
        {
            var pending = candidates.Where(tab => tab.IsDirty).ToArray();
            if (pending.Length == 0) return;

            try
            {
                foreach (var tab in pending)
                {
                    if (tab.Object.IsRemoved)
                        throw new InvalidOperationException($"“{tab.Name}” no longer exists in the model.");
                    foreach (var property in tab.Drafts.Keys)
                        if (!Handler.PowerBIGovernance.AllowEditProperty(tab.Object.ObjectType, GetProperty(tab.Object, property)))
                            throw new InvalidOperationException($"The expression for “{tab.Name}” is read-only.");
                    if (tab.NewName != null && !Handler.PowerBIGovernance.AllowEditProperty(
                        tab.Object.ObjectType, nameof(ITabularNamedObject.Name)))
                        throw new InvalidOperationException($"“{tab.Name}” cannot be renamed.");
                }

                Handler.BeginUpdate("Save expressions");
                var saved = false;
                try
                {
                    foreach (var tab in pending)
                        foreach (var draft in tab.Drafts)
                            WriteExpression(tab.Object, draft.Key, draft.Value);

                    // Renames use the dependency tree before this batch ends.
                    if (Handler.Settings.AutoFixup && pending.Any(tab => tab.NewName != null))
                        foreach (var tab in pending.Where(tab => tab.Drafts.Count != 0))
                            if (tab.Object is IDaxDependantObject expression)
                                FormulaFixup.BuildDependencyTree(expression, force: true);

                    foreach (var tab in pending)
                        if (tab.NewName != null) tab.Object.Name = tab.NewName;
                    saved = true;
                }
                finally { Handler.EndUpdate(rollback: !saved); }
            }
            catch (Exception error)
            {
                AppMessageBox.Show(UI.FormMain, error.Message, "Save expressions failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            foreach (var tab in pending)
            {
                tab.Drafts.Clear();
                tab.NewName = null;
                UpdateExpressionTab(tab);
            }
            ExpressionEditor_IsEditing = activeExpressionTab?.IsDirty == true;
            if (pending.Contains(activeExpressionTab)) ExpressionEditor_SetText();
            SizeExpressionTabStrip();
            UI.StatusLabel.Text = "Expressions applied to the model.";
        }

        internal void CloseActiveExpressionTab()
        {
            if (activeExpressionTab != null) CloseExpressionTab(activeExpressionTab);
        }

        private void CloseExpressionTab(ExpressionTab tab)
        {
            if (tab.IsDirty && MessageBox.Show(UI.FormMain, $"Close “{tab.Name}” without saving?",
                "Close expression", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            var index = expressionTabs.IndexOf(tab);
            var keep = tab == activeExpressionTab
                ? expressionTabs.Skip(index + 1).FirstOrDefault() ?? expressionTabs.Take(index).LastOrDefault()
                : activeExpressionTab;
            RemoveExpressionTabs(new[] { tab }, keep);
        }

        private void CloseExpressionTabs(ExpressionTab[] closing, ExpressionTab keep, string prompt)
        {
            if (closing.Length == 0) return;
            if (closing.Any(tab => tab.IsDirty)) prompt += "\r\n\r\nUnsaved changes will be lost.";
            if (MessageBox.Show(UI.FormMain, prompt, "Close expressions", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                RemoveExpressionTabs(closing, keep);
        }

        private void RemoveExpressionTabs(ExpressionTab[] closing, ExpressionTab keep)
        {
            SelectExpressionTab(keep);
            foreach (var tab in closing)
            {
                tab.Object.PropertyChanged -= ExpressionEditor_Current_PropertyChanged;
                expressionTabs.Remove(tab);
                tab.Header.Dispose();
            }
            expressionTabStrip.Visible = expressionTabs.Count != 0;
            SizeExpressionTabStrip();
        }

        private void ResetExpressionTabs()
        {
            RemoveExpressionTabs(expressionTabs.ToArray(), null);
            // The editor starts empty even when no expression was opened in the previous model.
            LoadExpressionTab(null);
        }

        internal bool CancelDiscardingExpressionDrafts() =>
            expressionTabs.Any(tab => tab.IsDirty) &&
            MessageBox.Show(UI.FormMain, "Continue without saving the changes in your expression tabs?",
                "Unsaved expressions", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Cancel;

        private sealed class ExpressionTab
        {
            public IExpressionObject Object { get; }
            public FlowLayoutPanel Header { get; } = DaxQueryTabs.CreateTabHeader();
            public Button Title { get; }
            public Label DirtyIndicator { get; } = DaxQueryTabs.CreateDirtyIndicator();
            public Button Close { get; } = DaxQueryTabs.HeaderButton("×", "Close expression");
            public DAXProperty Property { get; set; }
            public string NewName { get; set; }
            public Dictionary<DAXProperty, string> Drafts { get; } = new Dictionary<DAXProperty, string>();
            public string Name => NewName ?? Object.Name;
            public bool IsDirty => NewName != null || Drafts.Count != 0;

            public ExpressionTab(IExpressionObject obj)
            {
                Object = obj;
                Property = (obj as IDaxDependantObject)?.GetDefaultDAXProperty() ?? DAXProperty.Expression;
                Title = DaxQueryTabs.CreateTabTitle(obj.Name);
                Title.FlatAppearance.BorderSize = 0;
                Close.FlatAppearance.BorderSize = 0;
                Header.Controls.Add(Title);
                Header.Controls.Add(DirtyIndicator);
                Header.Controls.Add(Close);
            }
        }
    }
}
