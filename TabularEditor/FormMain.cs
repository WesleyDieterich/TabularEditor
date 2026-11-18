using System;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using TabularEditor.UI;
using TabularEditor.UI.Extensions;
using TabularEditor.UI.Actions;
using TabularEditor.UI.Dialogs;
using TabularEditor.UIServices;
using System.Collections.Generic;
using System.Threading;
using TabularEditor.BestPracticeAnalyzer;
using TabularEditor.Scripting;
using TabularEditor.TOMWrapper;

namespace TabularEditor
{
    public partial class FormMain : Form
    {
        public UIController UI;

        private MacroJson CurrentMacro;

        public static FormMain Singleton;

        public FormMain()
        {
            Singleton = this;
            Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            InitializeComponent();
            StyleWorkspace();
            ConfigurePropertiesPane();

            dlgOpenFile.Filter = "All supported files|*.bim;database.json;model.tmd;model.tmdl;database.tmdl;*.pbit;*.pbip|Tabular Model Files|*.bim;database.json;model.tmd;model.tmdl;database.tmdl|Power BI Files|*.pbit;*.pbip|All files|*.*";

            // Assign our own custom Designer, to make sure we can handle property changes on multiple objects simultaneously:
            propertyGrid1.Site = new DesignerHost();

            SetupUIController();
            txtFilter.Control.SetCueBanner("Search model");

            ///// Populate macros and samples /////
            // TODO: Do this somewhere else
            PopulateMacrosDropDown();

            var tutorial = (samplesMenu.DropDownItems.Add("Tutorials") as ToolStripMenuItem).DropDownItems;
            var translations = (samplesMenu.DropDownItems.Add("Translations") as ToolStripMenuItem).DropDownItems;

            tutorial.Add("Loop through all selected columns", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"foreach(var column in Selected.Columns) {
    // column.DisplayFolder = ""Test"";
}");
            });
            tutorial.Add("Loop through all selected tables", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"foreach(var table in Selected.Tables) {
    // table.IsHidden = false;
}");
            });
            tutorial.Add("Loop through columns on all selected tables", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"foreach(var column in Selected.Tables.SelectMany(t => t.Columns)) {
    // column.DisplayFolder = ""test"";
}");
            });
            tutorial.Add("Loop through columns on all tables conditionally", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"foreach(var column in Model.Tables.SelectMany(t => t.Columns)) {
    if(column.Name.EndsWith(""Key"")) {
        // column.IsHidden = true;
    }
}");
            });
            translations.Add("Copy display folder to active translation", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"Selected.Measures.ForEach(item => item.TranslatedDisplayFolders[Selected.Culture] = item.DisplayFolder);
Selected.Columns.ForEach(item => item.TranslatedDisplayFolders[Selected.Culture] = item.DisplayFolder);
Selected.Hierarchies.ForEach(item => item.TranslatedDisplayFolders[Selected.Culture] = item.DisplayFolder);");
            });

            translations.Add("Copy display folder to all translations", null, (s, e) =>
            {
                txtAdvanced.InsertText(
@"Selected.Measures.ForEach(item => item.TranslatedDisplayFolders.SetAll(item.DisplayFolder));
Selected.Columns.ForEach(item => item.TranslatedDisplayFolders.SetAll(item.DisplayFolder));
Selected.Hierarchies.ForEach(item => item.TranslatedDisplayFolders.SetAll(item.DisplayFolder));");
            });
            ApplyWorkspaceTheme();
        }

        private void ApplyWorkspaceTheme()
        {
            AppTheme.Apply(this);
            UI.ApplyEditorTheme();
        }

        private void StyleWorkspace()
        {
            var workspaceFont = CreateWorkspaceFont("Segoe UI Variable Text", "Segoe UI", 9.5f);
            var paneTitleFont = new Font(workspaceFont, FontStyle.Bold);
            var codeFont = CreateWorkspaceFont("Cascadia Mono", "Consolas", 9.75f);
            Font = workspaceFont;
            tvModel.Font = workspaceFont;
            txtExpression.Font = codeFont;
            txtAdvanced.Font = codeFont;
            lblCurrentMeasure.Font = codeFont;
            ArrangeWorkspaceToolbars();

            var renderer = new ToolStripProfessionalRenderer(new WorkspaceColors()) { RoundedEdges = false };
            foreach (var strip in new ToolStrip[] { menuStrip1, toolStrip2, toolModelSearch, toolStrip3, toolStrip4, toolPropertiesPane, statusStrip1 })
            {
                strip.Renderer = renderer;
                strip.Font = workspaceFont;
            }

            lblModelPane.Font = paneTitleFont;
            lblPropertiesPane.Font = paneTitleFont;
            lblPropertyObject.Font = paneTitleFont;
            Disposed += (sender, args) =>
            {
                paneTitleFont.Dispose();
                codeFont.Dispose();
                workspaceFont.Dispose();
            };

            tvModel.BorderStyle = BorderStyle.None;
            tvModel.RowHeight = Math.Max(tvModel.RowHeight, (int)Math.Round(22 * DeviceDpi / 96.0));
            lblModelPane.BackColor = SystemColors.Control;
            splitContainer1.BackColor = SystemColors.ControlLight;
            splitContainer2.BackColor = SystemColors.ControlLight;
            txtExpression.BorderStyle = BorderStyle.None;
            txtAdvanced.BorderStyle = BorderStyle.None;
        }

        private void ArrangeWorkspaceToolbars()
        {
            GroupCommands(toolModelSearch, "Results", toolStripButton5, toolStripButton4, toolStripButton7);
            GroupCommands(toolStrip3, "Edit", btnFind, btnReplace, toolStripButton1, toolStripButton2);
            GroupCommands(toolStrip4, "Edit", toolStripButton11, toolStripButton12, toolStripButton13, toolStripButton14);

            var zoomMenu = new ToolStripMenuItem("Script zoom");
            zoomMenu.DropDownItems.Add(toolStripComboBox1);
            viewToolStripMenuItem.DropDownItems.Insert(1, zoomMenu);

            var perspectiveMenu = new ToolStripMenuItem("Perspective");
            perspectiveMenu.DropDownItems.Add(cmbPerspective);
            var translationMenu = new ToolStripMenuItem("Translation");
            translationMenu.DropDownItems.Add(cmbTranslation);
            modelToolStripMenuItem.DropDownItems.Insert(0, perspectiveMenu);
            modelToolStripMenuItem.DropDownItems.Insert(1, translationMenu);
            modelToolStripMenuItem.DropDownItems.Insert(2, new ToolStripSeparator());

            cmbExpressionSelector.Width = (int)Math.Round(148 * DeviceDpi / 96.0);

            var gap = Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96.0));
            foreach (var strip in new[] { toolStrip2, toolStrip3, toolStrip4 })
            {
                strip.Padding = new Padding(2 * gap, gap, 2 * gap, gap);
                foreach (ToolStripItem item in strip.Items)
                {
                    if (item is ToolStripSeparator)
                        item.Margin = new Padding(gap, 0, gap, 0);
                    else if (item is ToolStripButton || item is ToolStripDropDownItem)
                    {
                        item.Margin = new Padding(1, 1, 1, 1);
                        item.Padding = new Padding(gap, 1, gap, 1);
                    }
                }
            }
            toolModelSearch.Padding = new Padding(2 * gap, gap, 2 * gap, gap);
            tbApplyFilter.Padding = new Padding(gap, 1, gap, 1);
        }

        private static void GroupCommands(ToolStrip strip, string label, params ToolStripButton[] buttons)
        {
            var menu = new ToolStripDropDownButton(label);
            var index = strip.Items.IndexOf(buttons[0]);
            strip.Items.Insert(index, menu);
            foreach (var button in buttons)
            {
                strip.Items.Remove(button);
                button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
                button.Margin = Padding.Empty;
                menu.DropDownItems.Add(button);
            }

            // Collapse separators that no longer divide a pair of command groups.
            var separators = strip.Items.OfType<ToolStripSeparator>().ToArray();
            foreach (var separator in separators)
            {
                var position = strip.Items.IndexOf(separator);
                if (position == 0 || position == strip.Items.Count - 1 || strip.Items[position - 1] is ToolStripSeparator)
                    strip.Items.Remove(separator);
            }
        }

        internal static Font CreateWorkspaceFont(string preferred, string fallback, float size)
        {
            var font = new Font(preferred, size);
            if (string.Equals(font.Name, preferred, StringComparison.OrdinalIgnoreCase)) return font;

            font.Dispose();
            return new Font(fallback, size);
        }

        private void ConfigurePropertiesPane()
        {
            propertyGrid1.ToolbarVisible = false;
            propertyGrid1.ViewBorderColor = SystemColors.Window;
            propertyGrid1.HelpBorderColor = SystemColors.ControlLight;
            propertyGrid1.LineColor = SystemColors.ControlLightLight;
            lblPropertyObject.BackColor = SystemColors.Window;
            lblPropertyContext.BackColor = SystemColors.Window;

            if (!SystemInformation.HighContrast)
            {
                lblPropertyContext.ForeColor = Color.FromArgb(82, 94, 110);
                propertyGrid1.LineColor = Color.FromArgb(240, 243, 247);
                propertyGrid1.CategorySplitterColor = Color.FromArgb(232, 239, 247);
                propertyGrid1.CategoryForeColor = Color.FromArgb(47, 62, 80);
                propertyGrid1.HelpBackColor = Color.FromArgb(246, 249, 252);
                propertyGrid1.SelectedItemWithFocusBackColor = Color.FromArgb(222, 235, 249);
                propertyGrid1.SelectedItemWithFocusForeColor = SystemColors.WindowText;
            }

            var grouped = new ToolStripMenuItem("Grouped");
            var alphabetical = new ToolStripMenuItem("Alphabetical");
            grouped.Click += (sender, args) => propertyGrid1.PropertySort = PropertySort.CategorizedAlphabetical;
            alphabetical.Click += (sender, args) => propertyGrid1.PropertySort = PropertySort.Alphabetical;
            btnPropertyOrder.DropDownItems.AddRange(new ToolStripItem[] { grouped, alphabetical });

            void UpdatePropertyOrder()
            {
                grouped.Checked = (propertyGrid1.PropertySort & PropertySort.Categorized) != 0;
                alphabetical.Checked = !grouped.Checked;
                btnPropertyOrder.Text = grouped.Checked ? "Grouped" : "A–Z";
            }

            ITabularNamedObject namedSelection = null;
            void UpdatePropertyContext()
            {
                var count = propertyGrid1.SelectedObjects.Length;
                lblPropertyObject.Text = count == 0 ? "Select a model object"
                    : count == 1 ? namedSelection?.Name ?? propertyGrid1.SelectedObject.ToString()
                    : $"{count} objects selected";
                var parent = (namedSelection as ITabularTableObject)?.Table;
                var type = namedSelection?.GetTypeName();
                lblPropertyContext.Text = parent == null ? type : $"{type} · {parent.Name}";
                lblPropertyContext.Visible = !string.IsNullOrEmpty(lblPropertyContext.Text);
                btnPropertyOrder.Enabled = count > 0;
            }

            void SelectedNameChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(ITabularNamedObject.Name)) UpdatePropertyContext();
            }

            propertyGrid1.SelectedObjectsChanged += (sender, args) =>
            {
                if (namedSelection != null) namedSelection.PropertyChanged -= SelectedNameChanged;
                namedSelection = propertyGrid1.SelectedObjects.Length == 1 ? propertyGrid1.SelectedObject as ITabularNamedObject : null;
                if (namedSelection != null) namedSelection.PropertyChanged += SelectedNameChanged;
                UpdatePropertyContext();
            };
            Disposed += (sender, args) =>
            {
                if (namedSelection != null) namedSelection.PropertyChanged -= SelectedNameChanged;
            };
            propertyGrid1.PropertySortChanged += (sender, args) => UpdatePropertyOrder();
            UpdatePropertyContext();
            UpdatePropertyOrder();
        }

        private void TogglePropertiesPane(object sender, EventArgs e)
        {
            if (!splitContainer2.Panel2Collapsed && propertyGrid1.ContainsFocus)
                tvModel.Focus();

            splitContainer2.Panel2Collapsed = !splitContainer2.Panel2Collapsed;
            btnPropertiesPane.Checked = !splitContainer2.Panel2Collapsed;
            menuPropertiesPane.Checked = btnPropertiesPane.Checked;
        }

        private sealed class WorkspaceColors : ProfessionalColorTable
        {
            private static Color CheckedSurface => SystemInformation.HighContrast
                ? SystemColors.Highlight
                : Color.FromArgb(222, 235, 249);

            public override Color ToolStripGradientBegin => SystemColors.Control;
            public override Color ToolStripGradientMiddle => SystemColors.Control;
            public override Color ToolStripGradientEnd => SystemColors.Control;
            public override Color MenuStripGradientBegin => SystemColors.Control;
            public override Color MenuStripGradientEnd => SystemColors.Control;
            public override Color StatusStripGradientBegin => SystemColors.Control;
            public override Color StatusStripGradientEnd => SystemColors.Control;
            public override Color ToolStripBorder => SystemColors.ControlLight;
            public override Color ButtonCheckedGradientBegin => CheckedSurface;
            public override Color ButtonCheckedGradientMiddle => CheckedSurface;
            public override Color ButtonCheckedGradientEnd => CheckedSurface;
            public override Color ButtonCheckedHighlightBorder => SystemColors.ControlDark;
            public override Color ButtonSelectedBorder => SystemColors.ControlDark;
        }

        private void PopulateMacrosDropDown()
        {
            macroToolStripMenuItem.DropDownItems.Clear();
            try
            {
                macros = ScriptEngine.GetMacrosJson();
                if (macros?.Actions != null)
                {
                    foreach (var action in macros.Actions)
                    {
                        var item = macroToolStripMenuItem.DropDownItems.Add(action.Name);
                        item.ToolTipText = action.Tooltip;
                        item.AutoToolTip = true;
                        item.Tag = action;
                        item.Click += (s, e) =>
                        {
                            var clickAction = CurrentMacro = (MacroJson)(s as ToolStripItem).Tag;
                            txtAdvanced.Text = clickAction.Execute;
                        };
                    }
                }
            }
            catch { }

            if (macros == null || macros.Actions.Length == 0)
            {
                var item = macroToolStripMenuItem.DropDownItems.Add("(No macros)");
                item.Enabled = false;
            }
        }

        MacrosJson macros = null;

        /// <summary>
        /// Instantiates the UIController, which will handle advanced UI interactions and host
        /// the TabularModelHandler from the TOMWrapper library.
        /// </summary>
        public void SetupUIController()
        {
            var elements = new UIElements()
            {
                FormMain = this,
                TreeView = tvModel,
                PropertyGrid = propertyGrid1,
                ExpressionEditor = txtExpression,
                ScriptEditor = txtAdvanced,
                ErrorLabel = lblErrors,
                StatusLabel = lblStatus,
                BpaLabel = lblBpaRules,
                StatusExLabel = lblScriptStatus,
                OpenBimDialog = dlgOpenFile,
                CurrentMeasureLabel = lblCurrentMeasure,
                ModelMenu = modelToolStripMenuItem,
                PerspectiveSelector = cmbPerspective,
                TranslationSelector = cmbTranslation,
                ToolsMenu = toolsToolStripMenuItem,
                DynamicMenu = dynamicToolStripMenuItem,
                ExpressionSelector = cmbExpressionSelector
            };

            // The UIController class sets up all bindings and event handlers needed for UI
            // interaction, once we pass it a reference to the main UI elements. This is not
            // really a "loose coupling" between UI and model code, but it helps to keep the
            // code nicely separated.
            UI = new UIController(elements);
            UI.ModelLoaded += UI_ModelLoaded;

            actToggleInfoColumns.Checked = Preferences.Current.View_MetadataInformation;
            UI.SetInfoColumns(actToggleInfoColumns.Checked);
            actToggleDisplayFolders.Checked = Preferences.Current.View_DisplayFolders;
            actToggleHidden.Checked = Preferences.Current.View_HiddenObjects;
            actToggleMeasures.Checked = Preferences.Current.View_Measures;
            actToggleColumns.Checked = Preferences.Current.View_Columns;
            actToggleHierarchies.Checked = Preferences.Current.View_Hierarchies;
            actTogglePartitions.Checked = Preferences.Current.View_Partitions;
            actToggleCalendars.Checked = Preferences.Current.View_Calendars;
            actToggleAllObjectTypes.Checked = Preferences.Current.View_AllObjectTypes;
            actToggleOrderByName.Checked = !Preferences.Current.View_SortAlphabetically;

            actSearchChild.Checked = Preferences.Current.View_SearchResults == SearchResultOption.ByChild;
            actSearchParent.Checked = Preferences.Current.View_SearchResults == SearchResultOption.ByParent;
            actSearchFlat.Checked = Preferences.Current.View_SearchResults == SearchResultOption.Flat;

            // Assign column widths from preferences:
            foreach (var cp in Preferences.Current.View_ColumnPreferences)
            {
                var column = elements.TreeView.Columns.FirstOrDefault(c => c.Header == cp.Name);
                if(column != null) column.Width = cp.Width;
            } 

            actViewOptions_Execute(this, null);
        }

        private void UI_ModelLoaded(object sender, EventArgs e)
        {
            if (BPAForm != null && BPAForm.Visible) BPAForm.Hide();
        }

        // TODO: Handle below as actions
        private void actOpenFile_Execute(object sender, EventArgs e)
        {
            UI.File_Open();
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            UI.Database_Connect();
        }

        public const string STR_DEFAULT_TRANSLATION = "(No translation)";
        public const string STR_DEFAULT_PERSPECTIVE = "(Model)";

        private void lblErrors_Click(object sender, EventArgs e)
        {
            if (UI.Handler?.Errors == null) return;
            var c = UI.Handler.Errors.Count;
            if (c == 0) return;
            TabularEditor.Scripting.ScriptHelper.OutputErrors(UI.Handler.Errors.OfType<TOMWrapper.TabularNamedObject>());
            // TODO: Show error information
            //MessageBox.Show((c > 25 ? c + " errors in total. Only 25 first errors shown:\n\n" : "") + string.Join("\n", UI.Handler.Errors.Select(t => t.Item1.Name + ": " + t.Item2).Take(25)), "Error messages returned from server", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if(UI.Handler != null && UI.Handler.HasUnsavedChanges)
            {
                if(UI.Handler.IsConnected)
                {
                    // Handle undeployed changes to connected model:
                    var res = MessageBox.Show("You have made changes to the currently connected model, which have not yet been saved. Are you sure you want to quit?", "Unsaved changes in the model", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (res == DialogResult.Cancel) e.Cancel = true;
                } else
                {
                    // Handle unsaved changes to file:
                    var res = MessageBox.Show("You have made changes to the currently loaded Model.bim file, which have not yet been saved. Are you sure you want to quit?", "Unsaved changes in the model", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (res == DialogResult.Cancel) e.Cancel = true;
                }
                if (e.Cancel) return;
            }

            SaveTreeViewColumnPreferences();
        }

        private void SaveTreeViewColumnPreferences()
        {
            foreach (var column in tvModel.Columns)
            {
                // Persist column widths:
                var cp = Preferences.Current.View_ColumnPreferences.FirstOrDefault(c => c.Name == column.Header);
                if (cp == null)
                {
                    cp = new ColumnPreferences { Name = column.Header, Visible = true };
                    Preferences.Current.View_ColumnPreferences.Add(cp);
                }
                cp.Width = column.Width;
            }
            Preferences.Current.Save();
        }

        private void actViewOptions_Execute(object sender, EventArgs e)
        {
            UI.SetDisplayOptions(
                actToggleHidden.Checked,
                actToggleDisplayFolders.Checked,
                actToggleColumns.Checked,
                actToggleMeasures.Checked,
                actToggleHierarchies.Checked,
                actTogglePartitions.Checked,
                actToggleCalendars.Checked,
                actToggleAllObjectTypes.Checked,
                actToggleOrderByName.Checked,
                actToggleFilter.Checked ? txtFilter.Text : null
            );

            Preferences.Current.View_HiddenObjects = actToggleHidden.Checked;
            Preferences.Current.View_DisplayFolders = actToggleDisplayFolders.Checked;
            Preferences.Current.View_Columns = actToggleColumns.Checked;
            Preferences.Current.View_Measures = actToggleMeasures.Checked;
            Preferences.Current.View_Hierarchies = actToggleHierarchies.Checked;
            Preferences.Current.View_Partitions = actTogglePartitions.Checked;
            Preferences.Current.View_Calendars = actToggleCalendars.Checked;
            Preferences.Current.View_AllObjectTypes = actToggleAllObjectTypes.Checked;
            Preferences.Current.View_SortAlphabetically = !actToggleOrderByName.Checked;
            Preferences.Current.Save();
        }

        private void txtFilter_TextChanged(object sender, EventArgs e)
        {
            // Only update filter results continously when we're not in LINQ mode:
            if(!LinqMode && actToggleFilter.Checked) actToggleFilter.DoExecute();        
        }

        /// <summary>
        /// Returns true only when there's an active Dynamic LINQ Filter
        /// </summary>
        private bool LinqMode => txtFilter.Text.StartsWith(":") && actToggleFilter.Checked;

        private void actExit_Execute(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void actCollapseExpand_Execute(object sender, EventArgs e)
        {
            if (sender == actCollapseAll) tvModel.CollapseAll();
            if (sender == actExpandAll) tvModel.ExpandAll();
            if (sender == actExpandFromHere) foreach (var n in tvModel.SelectedNodes) n.ExpandAll();
            if (sender == actCollapseFromHere) foreach (var n in tvModel.SelectedNodes) n.CollapseAll();
            if (tvModel.SelectedNode != null) tvModel.ScrollTo(tvModel.SelectedNode);
        }

        private void FormMain_Load(object sender, EventArgs e)
        {
            actTogglePartitions.Image = tabularTreeImages.Images["partition"];
            actToggleCalendars.Image = tabularTreeImages.Images["calendar"];

            // Populate list of recent files...
            PopulateRecentFilesList();
        }

        public void PopulateRecentFilesList()
        {
            recentFilesToolStripMenuItem.DropDown.Items.Clear();

            if (RecentFiles.Current.RecentHistory == null || RecentFiles.Current.RecentHistory.Count == 0)
            {
                recentFilesToolStripMenuItem.Enabled = false;
            }
            else
            {
                recentFilesToolStripMenuItem.DropDown.Items.AddRange(
                    (RecentFiles.Current.RecentHistory as IEnumerable<string>).Reverse().Take(10).Select(f =>
                        new ToolStripMenuItem(f, null, (s, ev) => {
                            if (UI.DiscardChangesCheck()) return;
                            if(File.Exists(f) || Directory.Exists(f))
                            {
                                UI.File_Open(f);
                            } else
                            {
                                MessageBox.Show("This file seems to have been moved or deleted.", "Could not open file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                var tsi = s as ToolStripMenuItem;
                                tsi.GetCurrentParent().Items.Remove(tsi);
                                RecentFiles.Current.RecentHistory.Remove(f);
                                RecentFiles.Save();
                                if (RecentFiles.Current.RecentHistory.Count == 0) recentFilesToolStripMenuItem.Enabled = false;
                            }
                        })).ToArray());
            }
        }

        private void actUndoRedo_Execute(object sender, EventArgs e)
        {
            if(!txtExpression.Focused) UI.ExpressionEditor_CancelEdit();
            //propertyGrid1.Refresh();
            //tvModel.Refresh();
        }

        private void txtFilter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !string.IsNullOrWhiteSpace(txtFilter.Text))
            {
                actToggleFilter.Checked = true;
                actToggleFilter.DoExecute();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void actExpressionAcceptEdit_Execute(object sender, EventArgs e)
        {
            UI.ExpressionEditor_AcceptEdit();
        }

        private void actExpressionCancelEdit_Execute(object sender, EventArgs e)
        {
            UI.ExpressionEditor_CancelEdit();
        }

        private void actExpressionAcceptEdit_UpdateEx(object sender, UpdateExEventArgs e)
        {
            e.Enabled = UI.ExpressionEditor_IsDirty;
        }

        private void actExpressionCancelEdit_UpdateEx(object sender, UpdateExEventArgs e)
        {
            e.Enabled = UI.ExpressionEditor_IsEditing;
        }

        private void lblCurrentMeasure_Paint(object sender, PaintEventArgs e)
        {
            var c = Color.FromArgb(100, 100, 100);
            var s = ButtonBorderStyle.Solid;
            ControlPaint.DrawBorder(e.Graphics, lblCurrentMeasure.DisplayRectangle, c, 1, s, c, 1, s, c, 1, s, c, 0, ButtonBorderStyle.None);
        }

        private void actExpression_UpdateEx(object sender, UpdateExEventArgs e)
        {
            e.Enabled = UI.ExpressionEditor_IsDax;
        }

        private void actFind_Execute(object sender, EventArgs e)
        {
            switch (tabCodeEditors.SelectedIndex)
            {
                case 0:
                    txtExpression.ShowFindDialog();
                    break;
                case 1:
                    txtAdvanced.ShowFindDialog();
                    break;
            }
        }

        private void actReplace_Execute(object sender, EventArgs e)
        {
            switch (tabCodeEditors.SelectedIndex) {
                case 0:
                    UI.ExpressionEditor_BeginEdit();
                    txtExpression.ShowReplaceDialog();
                    break;
                case 1:
                    txtAdvanced.ShowReplaceDialog();
                    break;
            }
        }

        private void actExpressionFormatDAX_Execute(object sender, EventArgs e)
        {
            if (Policies.Instance.DisableWebDaxFormatter || string.IsNullOrEmpty(txtExpression.Text)) return;

            using (var hg = new Hourglass())
            {
                var textToFormat = ScriptHelper.PrepareForDaxFormatter(txtExpression.Text, UI.ExpressionEditor_Current is Function, out var firstComments);
                var newline = txtExpression.Text.StartsWith("\n") || txtExpression.Text.StartsWith("\r\n");
                try
                {
                    var result = TabularEditor.Dax.DaxFormatterProxy.Instance.FormatDax(textToFormat, Preferences.Current.UseSemicolonsAsSeparators, sender == actExpressionFormatDAXShort, Preferences.Current.DaxFormatterSkipSpaceAfterFunctionName).FormattedDax;
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        lblStatus.Text = "Could not format DAX (invalid syntax).";
                        return;
                    }
                    lblStatus.Text = "DAX formatted successfully";
                    txtExpression.Text = (newline ? "\n" : "") +
                        ScriptHelper.ExtractFromDaxFormatter(result, UI.ExpressionEditor_Current is Function, firstComments);
                    result.Substring(6).Trim();
                }
                catch (Exception ex)
                {
                    lblStatus.Text = "Could not format DAX (" + ex.Message + ").";
                }
            }
        }

        private void actToggleInfoColumns_Execute(object sender, EventArgs e)
        {
            UI.SetInfoColumns(actToggleInfoColumns.Checked);
            Preferences.Current.View_MetadataInformation = actToggleInfoColumns.Checked;
            Preferences.Current.Save();
        }

        private void actToggleFilter_UpdateEx(object sender, UpdateExEventArgs e)
        {
            if (string.IsNullOrEmpty(txtFilter.Text))
            {
                actToggleFilter.Checked = false;
                e.Enabled = false;
            }
            else
            {
                e.Enabled = true;
            }
        }

        private void toolStripMenuItem9_Click(object sender, EventArgs e)
        {
            if (UI.Handler == null) return;
            var f = new ClipForm();
            f.txtCode.Text = UI.Handler.UndoManager.GetHistory();
            f.ShowDialog();
        }

        private void actExecuteScript_Execute(object sender, EventArgs e)
        {
            UI.ScriptEditor_ExecuteScript(btnUndoErrors.Checked);
        }

        private void actExecuteScript_Update(object sender, UpdateExEventArgs e)
        {
            e.Enabled = !Policies.Instance.DisableCSharpScripts && !string.IsNullOrEmpty(txtAdvanced.Text);
        }

        public void FocusFilter()
        {
            txtFilter.Focus();
        }

        private void actSave_Execute(object sender, EventArgs e)
        {
            UI.Save();
        }

        private void actSaveAs_Execute(object sender, EventArgs e)
        {
            UI.File_SaveAs();
        }

        private void actDeploy_Execute(object sender, EventArgs e)
        {
            UI.Database_Deploy();
        }

        private void SaveMacro()
        {
            // TODO: Move this somewhere else
            using (var form = new SaveMacroForm())
            {   
                form.txtName.Text = CurrentMacro?.Name;
                form.txtTooltip.Text = CurrentMacro?.Tooltip;
                form.Context = CurrentMacro?.ValidContexts ?? UI.Selection.Context;
             
                var res = form.ShowDialog();

                if (res == DialogResult.OK)
                {
                    var act = new MacroJson();
                    act.Name = form.txtName.Text;
                    act.Tooltip = form.txtTooltip.Text;
                    act.Execute = txtAdvanced.Text;
                    act.Enabled = "true";
                    act.ValidContexts = form.Context;
                    
                    ScriptEngine.CompileMacros(new MacrosJson() { Actions = new[] { act } });
                    if (ScriptEngine.MacroErrors)
                    {
                        MessageBox.Show("Compile failed, macro contains errors and cannot be saved.", "Validation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    if (macros == null) macros = new MacrosJson() { Actions = new MacroJson[0] };

                    // Remove any existing actions with the same name:
                    macros.Actions = macros.Actions.Where(a => !a.Name.Equals(act.Name, StringComparison.InvariantCultureIgnoreCase)).ToArray();
                    var toRemove = UI.Actions.OfType<MacroAction>().FirstOrDefault(a => a.BaseName.Equals(act.Name, StringComparison.InvariantCultureIgnoreCase));
                    if (toRemove != null) UI.Actions.Remove(toRemove);

                    var list = macros.Actions.ToList();
                    list.Add(act);

                    macros.Actions = list.ToArray();
                    macros.SaveToJson();
                    CurrentMacro = act;

                    ScriptEngine.AddMacros(UI.Actions);
                    PopulateMacrosDropDown();
                }
            }
        }

        private void DeleteCurrentMacro()
        {
            if (CurrentMacro == null)
                return;

            var dialogResult = MessageBox.Show($"Are you sure you want to delete the macro [{ CurrentMacro.Name }] ?", "Confirm delete macro", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dialogResult != DialogResult.Yes)
                return;

            if (macros == null) 
                macros = new MacrosJson() { Actions = new MacroJson[0] };

            macros.Actions = macros.Actions.Where(a => !a.Name.Equals(CurrentMacro.Name, StringComparison.InvariantCultureIgnoreCase)).ToArray();
            var toRemove = UI.Actions.OfType<MacroAction>().FirstOrDefault(a => a.BaseName.Equals(CurrentMacro.Name, StringComparison.InvariantCultureIgnoreCase));
            if (toRemove != null) 
                UI.Actions.Remove(toRemove);

            macros.SaveToJson();
            CurrentMacro = null;
            
            PopulateMacrosDropDown();
        }

        private void actSaveMacro_Execute(object sender, EventArgs e)
        {
            SaveMacro();
        }

        private void actSaveMacro_Update(object sender, EventArgs e)
        {
            actSaveMacro.Enabled = !Policies.Instance.DisableMacros && !string.IsNullOrEmpty(txtAdvanced.Text);
        }

        private void actDeleteMacro_Execute(object sender, EventArgs e)
        {
            DeleteCurrentMacro();
        }

        private void actDeleteMacro_Update(object sender, EventArgs e)
        {
            actDeleteMacro.Enabled = !Policies.Instance.DisableMacros && CurrentMacro != null;
        }


        PreferencesForm PreferencesForm = new PreferencesForm();


        private void preferencesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var usingSemicolons = Preferences.Current.UseSemicolonsAsSeparators;
            var usingDarkTheme = Preferences.Current.UseDarkTheme;
            PreferencesForm.Show(UI.Handler);
            if (usingDarkTheme != Preferences.Current.UseDarkTheme)
            {
                ApplyWorkspaceTheme();
                AppTheme.ApplyOpenForms(this, EventArgs.Empty);
            }
            if (!Preferences.Current.BackgroundBpa)
                lblBpaRules.Text = "";
            else
                UI.InvokeBPABackground();

            if(usingSemicolons != Preferences.Current.UseSemicolonsAsSeparators)
            {
                if (Preferences.Current.UseSemicolonsAsSeparators) UI.ExpressionEditor_SwitchToSemicolons();
                else UI.ExpressionEditor_SwitchToCommas();
            }

            if (UI.Handler != null) UI.Handler.Settings = Preferences.Current.GetSettings();
        }

        private void FormMain_Shown(object sender, EventArgs e)
        {
            if (Policies.Instance.DisableWebDaxFormatter)
            {
                formatDAXToolStripMenuItem.Visible = false;
                btnFormatDAX.Visible = false;
            }
            if (Policies.Instance.DisableCSharpScripts)
            {
                tabCodeEditors.TabPages.Remove(pgCSharpScript);
            }
            if (Policies.Instance.DisableMacros)
            {
                macroToolStripSeparator.Visible = false;
                macroToolStripMenuItem.Visible = false;
            }
            // Various checks to be performed when the main form is shown

            if(!Preferences.Current.IsLoaded)
            {
                // If IsLoaded is false, it means that this is the first time Tabular Editor is started (or that the Preferences.json file has been deleted)
                // In this case, let's ask the user if he wants to enable automatic updates.

                var res = MessageBox.Show("Do you want Tabular Editor to automatically check GitHub for updates on every startup?\n\nThis can be changed under File > Preferences.", "Enable automatic update check?", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                Preferences.Current.CheckForUpdates = res == DialogResult.Yes;
                Preferences.Current.Save();
            }

            if(Preferences.Current.CheckForUpdates && !Policies.Instance.DisableUpdates)
            {
                if(UpdateService.Check(false).UpdateAvailable(Preferences.Current.SkipPatchUpdates))
                {
                    var res = MessageBox.Show("A new version of Tabular Editor is available. Would you like to open the download page now?\n\nYou can disable this check under File > Preferences.", "Tabular Editor update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (res == DialogResult.Yes) UpdateService.OpenDownloadPage();
                }
            }

            // Auto-load the file specified as command line arguments:
            var args = Environment.GetCommandLineArgs();
            var modelSource = CommandLineHandler.GetModelSourceFromCLI(args, out _);
            if (modelSource == CommandLineHandler.ModelSource.File && (File.Exists(args[1]) 
                || File.Exists(Path.Combine(args[1],"database.json")) 
                || File.Exists(Path.Combine(args[1],"model.tmd"))
                || File.Exists(Path.Combine(args[1],"model.tmdl"))))
            {
                UI.File_Open(args[1]);
            }
            else if (modelSource == CommandLineHandler.ModelSource.ServerAndDatabase)
            {
                UI.Database_Open(args[1], args[2]);
            }
            else if (modelSource == CommandLineHandler.ModelSource.SingleLocalInstance)
            {
                var localInstances = PowerBIHelper.Instances;
                if(localInstances.Count == 1)
                {
                    UI.Database_Open($"localhost:{localInstances[0].Port}", "");
                }
            }
            else if (modelSource == CommandLineHandler.ModelSource.NamedLocalInstance)
            {
                var localInstance = PowerBIHelper.Instances.FirstOrDefault(i => i.Name.EqualsI(args[2]));
                if (localInstance != null)
                {
                    UI.Database_Open($"localhost:{localInstance.Port}", "");
                }
            }
        }

        public void UpdateTreeUIButtons()
        {
            var treeModel = tvModel.Model as TabularUITree;
            displayFoldersToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.DisplayFolders);
            hiddenObjectsToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.ShowHidden);
            showAllObjectTypesToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.AllObjectTypes);
            mEasToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.Measures);
            xToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.Columns);
            yToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.Hierarchies);
            partitionsToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.Partitions);
            calendarsToolStripMenuItem.Checked = treeModel.Options.HasFlag(TOMWrapper.LogicalTreeOptions.Calendars);
            tbApplyFilter.Checked = !string.IsNullOrEmpty(treeModel.Filter);
        }

        private void fromFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            UI.File_Open(true);
        }

        private void bestPracticeAnalyzerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            BPAForm.ShowBPA();
        }

        internal BPAForm BPAForm = new BPAForm();

        private void CanComment(object sender, EventArgs e)
        {
            (sender as Crad.Windows.Forms.Actions.Action).Enabled = 
                (txtExpression.Focused && txtExpression.Text != "") ||
                (txtAdvanced.Focused && txtAdvanced.Text != "");
        }

        private void actComment_Execute(object sender, EventArgs e)
        {
            if (txtExpression.Focused) txtExpression.InsertLinePrefix("//");
            if (txtAdvanced.Focused) txtAdvanced.InsertLinePrefix("//");
        }

        private void actUncomment_Execute(object sender, EventArgs e)
        {
            if (txtExpression.Focused) txtExpression.RemoveLinePrefix("//");
            if (txtAdvanced.Focused) txtAdvanced.RemoveLinePrefix("//");
        }

        private void actSaveToFolder_Execute(object sender, EventArgs e)
        {
            UI.File_SaveAs_ToFolder();
        }

        private void actNewModel_Execute(object sender, EventArgs e)
        {
            UI.File_New();
        }

        private void btnOpenScript_Click(object sender, EventArgs e)
        {
            if(ofdScript.ShowDialog() == DialogResult.OK)
            {
                txtAdvanced.Text = File.ReadAllText(ofdScript.FileName);
            }
        }

        private void btnSaveScript_Click(object sender, EventArgs e)
        {
            if(sfdScript.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(sfdScript.FileName, txtAdvanced.Text);
            }
        }

        private void toolStripComboBox1_TextChanged(object sender, EventArgs e)
        {
            int zoom;
            if(int.TryParse(toolStripComboBox1.Text.Replace("%", "").Trim(), out zoom))
            {
                txtAdvanced.Zoom = zoom;
            } else
            {
                zoom = txtAdvanced.Zoom;
            }
        }

        private void txtAdvanced_ZoomChanged(object sender, EventArgs e)
        {
            int zoom = txtAdvanced.Zoom;
            toolStripComboBox1.Text = string.Format("{0} %", zoom);
        }

        private void actBack_Update(object sender, EventArgs e)
        {
            actBack.Enabled = UI.CanNavigateBack;
        }

        private void actForward_Update(object sender, EventArgs e)
        {
            actForward.Enabled = UI.CanNavigateForward;
        }

        private void actBack_Execute(object sender, EventArgs e)
        {
            UI.Tree_NavigateBack();
        }

        private void actForward_Execute(object sender, EventArgs e)
        {
            UI.Tree_NavigateForward();
        }

        private void nodeTextBox7_ValueNeeded(object sender, Aga.Controls.Tree.NodeControls.NodeControlValueEventArgs e)
        {
            e.Value = (e.Node.Tag is TOMWrapper.Model) ? "" : (e.Node.Tag as TOMWrapper.KPI)?.MeasureName ?? 
                (e.Node.Tag as TOMWrapper.Level)?.Hierarchy?.Name ??
                (e.Node.Tag as TOMWrapper.ITabularTableObject)?.Table?.DaxTableName ??
                "Model";
        }

        private void actToggleColumns_Update(object sender, EventArgs e)
        {
            
        }

        private void DisableIfFlatResult(object sender, UpdateExEventArgs e)
        {
            e.Enabled = !UI.FilterEnabled || UI.FilterMode != FilterMode.Flat;
        }

        private void nodeTextBox5_ValueNeeded(object sender, Aga.Controls.Tree.NodeControls.NodeControlValueEventArgs e)
        {
            var value = 
                (e.Node.Tag as TOMWrapper.Table)?.Source ??
                (e.Node.Tag as TOMWrapper.Partition)?.Expression ??
                (e.Node.Tag as TOMWrapper.DataColumn)?.SourceColumn ??
                (e.Node.Tag as TOMWrapper.CalculatedTableColumn)?.SourceColumn ??
                (e.Node.Tag as TOMWrapper.Level)?.Column?.DaxObjectName ??
                (e.Node.Tag as TOMWrapper.IExpressionObject)?.Expression;
            e.Value = (value ?? string.Empty).Replace('\n', ' ').Trim();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Global keyboard shortcuts (if this method returns TRUE, then the key press will not be propagated to form controls)
            switch (keyData)
            {
                case (Keys.Alt | Keys.Left): UI.Tree_NavigateBack(); return true;
                case (Keys.Alt | Keys.Right): UI.Tree_NavigateForward(); return true;
                default:
                    if (UIController.Current.Actions.HandleKeyPress(keyData)) return true;
                    break;
            }

            // Shortcuts that apply only when the explorer tree has focus:
            if (tvModel.Focused)
            {
                switch (keyData) {
                    case (Keys.Control | Keys.Left):
                        if (tvModel.ContainsFocus) { actCollapseFromHere.DoExecute(); return true; }
                        break;
                    case (Keys.Control | Keys.Right):
                        if (tvModel.ContainsFocus) { actExpandFromHere.DoExecute(); return true; }
                        break;
                    case (Keys.Control | Keys.Shift | Keys.Left):
                        if (tvModel.ContainsFocus) { actCollapseAll.DoExecute(); return true; }
                        break;
                    case (Keys.Control | Keys.Shift | Keys.Right):
                        if (tvModel.ContainsFocus) { actExpandAll.DoExecute(); return true; }
                        break;
                }
            }


            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void actFind_UpdateEx(object sender, UpdateExEventArgs e)
        {
            e.Enabled = (txtExpression.ContainsFocus && UI.ExpressionEditor_Current != null) 
                || txtAdvanced.ContainsFocus 
                || propertyGrid1.ContainsFocus;
        }

        private void actSearch_Execute(object sender, EventArgs e)
        {
            actSearchChild.Checked = actSearchChild == sender;
            actSearchParent.Checked = actSearchParent == sender;
            actSearchFlat.Checked = actSearchFlat == sender;
            Preferences.Current.View_SearchResults = actSearchChild.Checked ? SearchResultOption.ByChild
                : (actSearchParent.Checked ? SearchResultOption.ByParent : SearchResultOption.Flat);
            UI.SetFilterMode((FilterMode)Preferences.Current.View_SearchResults);
        }

        private void actGotoDef_Execute(object sender, EventArgs e)
        {
            UI.ExpressionEditor_GoToDefinition();
        }

        private void actSearchResultView_UpdateEx(object sender, UpdateExEventArgs e)
        {
            e.Enabled = actToggleFilter.Checked;
        }

        private void lblBpaRules_Click(object sender, EventArgs e)
        {
            BPAForm.ShowBPA();
        }

        private void manageBPARulesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            BPAManager.Show(BPAForm.Analyzer);
        }

        private void actOpenBPA_Execute(object sender, EventArgs e)
        {
            BPAForm.ShowBPA();
        }

        private void btnFormatDAX_ButtonClick(object sender, EventArgs e)
        {
            actExpressionFormatDAX.DoExecute();
        }
    }
}
