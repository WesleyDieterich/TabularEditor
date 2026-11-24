using Aga.Controls.Tree;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TextServices;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.PowerBI;
using TabularEditor.TOMWrapper.Utils;
using TabularEditor.UIServices;

namespace TabularEditor.UI
{
    public partial class UIController
    {
        DependencyForm DependencyForm = new DependencyForm();

        public void ShowDependencies(IDaxObject dependantObject)
        {
            if (DependencyForm.IsDisposed) DependencyForm = new DependencyForm();
            DependencyForm.Owner = UI.FormMain;
            DependencyForm.RootObject = dependantObject;
            DependencyForm.Show();
        }

        public IExpressionObject ExpressionEditor_Current => activeExpressionTab?.Object;

        private void ExpressionEditor_Current_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var tab = expressionTabs.FirstOrDefault(item => ReferenceEquals(item.Object, sender));
            if (tab == null) return;
            UpdateExpressionTab(tab);
            if (tab == activeExpressionTab)
            {
                if (e.PropertyName == GetProperty() || e.PropertyName.EndsWith("Expression", StringComparison.Ordinal))
                {
                    if (!tab.Drafts.ContainsKey(CurrentDaxProperty)) ExpressionEditor_SetText();
                    if (UI.PropertyGrid.SelectedObject == tab.Object) UI.PropertyGrid.Refresh();
                }
            }
        }

        public bool ExpressionEditor_IsEditing { get; private set; } = false;
        private DaxIntelliSense daxIntelliSense;

        internal void ApplyEditorTheme()
        {
            ExpressionParser.ApplyTheme();
            daxIntelliSense.ApplyTheme();
            ApplyScriptEditorTheme();
            ApplyExpressionTabTheme();
            UI.ExpressionEditor.Invalidate();
            UI.ScriptEditor.Invalidate();
        }

        private void ExpressionEditor_Init()
        {
            UI.ExpressionEditor.TextChanged += ExpressionEditor_TextChanged;
            UI.ExpressionEditor.KeyPress += ExpressionEditor_KeyPress;
            UI.ExpressionEditor.DragEnter += ExpressionEditor_DragEnter;
            UI.ExpressionEditor.DragLeave += ExpressionEditor_DragLeave;
            daxIntelliSense = new DaxIntelliSense(UI.ExpressionEditor, () => ExpressionEditor_IsDax, () => Handler?.Model);

            UI.ExpressionSelector.ComboBox.ValueMember = "Value";
            UI.ExpressionSelector.ComboBox.DisplayMember = "Description";

            syntaxHighlightTimer.Tick += ExpressionEditor_SyntaxHighlightTick;
            InitializeExpressionTabs();
        }

        Task syntaxHighlightTask;

        private void ExpressionEditor_SyntaxHighlightTick(object sender, EventArgs e)
        {
            if(syntaxHighlightTask == null || syntaxHighlightTask.Status != TaskStatus.Running)
            {
                syntaxHighlightTimer.Enabled = false;
                syntaxHighlightTimer.Interval = 500;
                syntaxHighlightTask = new Task(() => currentTokens = ExpressionParser.SyntaxHighlight(UI.ExpressionEditor));
                syntaxHighlightTask.Start();
            }
        }

        private List<Antlr4.Runtime.IToken> currentTokens = new List<Antlr4.Runtime.IToken>();

        private void ExpressionEditor_ExpressionSelectorChanged(object sender, EventArgs e)
        {
            if (ExpressionEditor_Current is IDaxDependantObject)
            {
                activeExpressionTab.Property = CurrentDaxProperty;
                ExpressionEditor_SetText();
            }
        }

        sealed class DaxPropertyComboBoxItem
        {
            public string Description { get; }
            public DAXProperty Value { get; }
            public DaxPropertyComboBoxItem(DAXProperty value, string description) { Value = value; Description = description; }
            public DaxPropertyComboBoxItem(DAXProperty value) { Value = value; Description = Enum.GetName(typeof(DAXProperty), Value).SplitCamelCase(); }
            public static readonly IReadOnlyDictionary<DAXProperty, DaxPropertyComboBoxItem> Items = new Dictionary<DAXProperty, DaxPropertyComboBoxItem>(
                Enum.GetValues(typeof(DAXProperty)).Cast<DAXProperty>().ToDictionary(v => v, v => new DaxPropertyComboBoxItem(v))
                );
            public static readonly DaxPropertyComboBoxItem MExpression = new DaxPropertyComboBoxItem(DAXProperty.Expression, "M Expression");
            public static readonly DaxPropertyComboBoxItem Query = new DaxPropertyComboBoxItem(DAXProperty.Expression, "Query");
        }

        private void ExpressionEditor_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (ExpressionEditor_IsEditing)
            {
                if (e.KeyChar == 27)
                {
                    ExpressionEditor_CancelEdit();
                    e.Handled = true;
                }
            }
        }

        public const string DAX_GUIDE_URL = "https://dax.guide/{0}/?utm_source=tabular-editor";

        public void ExpressionEditor_GoToDefinition()
        {
            // Navigate to symbol under cursor
            var obj = ExpressionEditor_Current as IDaxDependantObject;
            var dependsOnList = DependsOnList.GetDependencies(obj, UI.ExpressionEditor.Text, CurrentDaxProperty);

            var dest = dependsOnList.GetObjectAt(CurrentDaxProperty, UI.ExpressionEditor.SelectionStart);
            if (dest == null)
            {
                var token = ExpressionParser.GetTokenAtPos(currentTokens, UI.ExpressionEditor.SelectionStart);
                if (token != null)
                {
                    if (token.Channel == DAXLexer.KEYWORD_CHANNEL) Process.Start(string.Format(DAX_GUIDE_URL, token.Text.ToLowerInvariant()));
                }
                else
                    UI.StatusLabel.Text = "Cannot navigate to symbol under cursor.";
            }
            else
                Goto(dest);
        }

        private string Tree_DragBackup;
        private IDataObject Tree_CurrentDragObject;

        private void ExpressionEditor_DragEnter(object sender, DragEventArgs e)
        {
            Tree_CurrentDragObject = e.Data;
            if (draggedNodes != null)
            {
                // To be able to drag objects into the Expression Editor, we must temporarily swap the String contents of the Drag Data object with the DAX name to be inserted.
                // But first, backup the current string contents, to make sure we can still retrieve it if the drag operation leaves the Expression Editor.
                Tree_DragBackup = (string)e.Data.GetData(typeof(string));
                if (draggedNodes[0].Tag is IDaxObject)
                {
                    e.Data.SetData((draggedNodes[0].Tag as IDaxObject).DaxObjectFullName);
                }
                else
                {
                    e.Data.SetData(typeof(string), null);
                    e.Effect = DragDropEffects.None;
                }
            }
        }

        private void ExpressionEditor_DragLeave(object sender, EventArgs e)
        {
            // If a drag backup was set, restore the backup
            if (Tree_DragBackup != null)
            {
                Tree_CurrentDragObject.SetData(Tree_DragBackup);
            }
        }

        public void ExpressionEditor_BeginEdit()
        {
            if (!ExpressionEditor_IsEditing && ExpressionEditor_Current != null)
                ExpressionEditor_Edit(ExpressionEditor_Current, false);
        }

        Timer syntaxHighlightTimer = new Timer() { Interval = 500, Enabled = false };

        bool ExpressionEditor_SuspendTextChanged = false;

        private void ExpressionEditor_TextChanged(object sender, FastColoredTextBoxNS.TextChangedEventArgs e)
        {
            if (ExpressionEditor_SuspendTextChanged) return;

            ExpressionEditor_BeginEdit();
            if (activeExpressionTab != null && !UI.ExpressionEditor.ReadOnly)
            {
                var property = CurrentDaxProperty;
                var text = UI.ExpressionEditor.Text.Replace("\r", "");
                if (ExpressionEditor_IsDax && Preferences.Current.UseSemicolonsAsSeparators)
                    text = ExpressionParser.SemicolonsToCommas(text);
                if (text == ReadExpression(activeExpressionTab.Object, property).Replace("\r", ""))
                    activeExpressionTab.Drafts.Remove(property);
                else activeExpressionTab.Drafts[property] = text;
                UpdateExpressionTab(activeExpressionTab);
            }

            if(syntaxHighlightTimer.Enabled) syntaxHighlightTimer.Enabled = false;
            syntaxHighlightTimer.Interval = 500;
            ExpressionEditor_SyntaxHighlight();
        }

        public bool ExpressionEditor_IsDax
        {
            get
            {
                if (ExpressionEditor_Current == null) return false;
                switch (ExpressionEditor_Current.ObjectType)
                {
                    case ObjectType.CalculationItem:
                    case ObjectType.Measure:
                    case ObjectType.Function:
                    case ObjectType.Table:
                    case ObjectType.CalculationGroupTable:
                    case ObjectType.Column:
                    case ObjectType.KPI:
                    case ObjectType.TablePermission:
                        // These are the only object types that contain DAX expression properties:
                        return true;
                    case ObjectType.Partition:
                        return CurrentDaxProperty == DAXProperty.DataCoverageExpression;
                    default:
                        // Other object types (partitions) do typically not contain DAX expressions:
                        return false;
                }
            }
        }

        private void ExpressionEditor_SyntaxHighlight()
        {
            if (!ExpressionEditor_IsDax) return;

            // For short DAX expressions, do synchronous syntax highlighting. Otherwise, do asynchrounous:
            if (UI.ExpressionEditor.Text.Length < 2000) currentTokens = ExpressionParser.SyntaxHighlight(UI.ExpressionEditor);
            else syntaxHighlightTimer.Enabled = true;
        }

        internal void ExpressionEditor_RefreshHighlight() => ExpressionEditor_SyntaxHighlight();

        public void ExpressionEditor_SwitchToSemicolons()
        {
            if (ExpressionEditor_IsDax)
            {
                ExpressionEditor_SuspendTextChanged = true;
                UI.ExpressionEditor.Text = ExpressionParser.CommasToSemicolons(UI.ExpressionEditor.Text);
                ExpressionEditor_SuspendTextChanged = false;
                ExpressionParser.SyntaxHighlight(UI.ExpressionEditor);
            }
        }

        public void ExpressionEditor_SwitchToCommas()
        {
            if (ExpressionEditor_IsDax)
            {
                ExpressionEditor_SuspendTextChanged = true;
                UI.ExpressionEditor.Text = ExpressionParser.SemicolonsToCommas(UI.ExpressionEditor.Text);
                ExpressionEditor_SuspendTextChanged = false;
                ExpressionParser.SyntaxHighlight(UI.ExpressionEditor);
            }
        }

        private string GetProperty() => GetProperty(ExpressionEditor_Current, CurrentDaxProperty);

        private static string GetProperty(IExpressionObject obj, DAXProperty property)
        {
            if (obj is Partition && property == DAXProperty.Expression)
            {
                return TOMWrapper.Properties.EXPRESSION;
            }
            return (obj as IDaxDependantObject)?.GetDAXProperty(property) ?? TOMWrapper.Properties.EXPRESSION;
        }

        private static string ReadExpression(IExpressionObject obj, DAXProperty prop)
        {
            if (obj is Partition && prop == DAXProperty.Expression) return obj.Expression ?? "";
            if (obj is IDaxDependantObject daxObject) return daxObject.GetDAX(prop) ?? "";
            return obj?.Expression ?? "";
        }

        private string GetText()
        {
            var value = activeExpressionTab != null && activeExpressionTab.Drafts.TryGetValue(CurrentDaxProperty, out var draft)
                ? draft : ReadExpression(ExpressionEditor_Current, CurrentDaxProperty);
            if (ExpressionEditor_IsDax && Preferences.Current.UseSemicolonsAsSeparators)
                value = ExpressionParser.CommasToSemicolons(value);
            return value;
        }

        private static void WriteExpression(IExpressionObject obj, DAXProperty prop, string value)
        {
            if (obj is Partition && prop == DAXProperty.Expression) obj.Expression = value;
            else if (obj is IDaxDependantObject daxObject) daxObject.SetDAX(prop, value);
            else obj.Expression = value;
        }

        private DAXProperty CurrentDaxProperty
        {
            get
            {
                return (UI.ExpressionSelector.SelectedItem as DaxPropertyComboBoxItem)?.Value ?? DAXProperty.Expression;
            }
            set
            {
                UI.ExpressionSelector.SelectedItem = UI.ExpressionSelector.Items.Cast<DaxPropertyComboBoxItem>().FirstOrDefault(i => i.Value == value) ?? DaxPropertyComboBoxItem.Items[value];
            }
        }

        private void ExpressionEditor_SetText()
        {
            daxIntelliSense?.Close();
            ExpressionEditor_SuspendTextChanged = true;

            var i = UI.ExpressionEditor.SelectionStart;
            UI.ExpressionEditor.Text = GetText();
            UI.ExpressionEditor.ReadOnly = ExpressionEditor_Current == null || !Handler.PowerBIGovernance.AllowEditProperty(ExpressionEditor_Current.ObjectType, GetProperty());
            if (!string.IsNullOrEmpty(UI.ExpressionEditor.Text))
            {
                if (syntaxHighlightTimer.Enabled) syntaxHighlightTimer.Enabled = false;
                syntaxHighlightTimer.Interval = 1;
                ExpressionEditor_SyntaxHighlight();
            }
            UI.ExpressionEditor.ClearUndo();
            UI.ExpressionEditor.SelectionStart = Math.Min(i, UI.ExpressionEditor.TextLength);
            ExpressionEditor_SuspendTextChanged = false;
        }

        private void ExpressionEditor_Preview(IExpressionObject obj)
        {
            if (obj == null || ReferenceEquals(obj, ExpressionEditor_Current)) return;
            if (ExpressionEditor_Current != null)
            {
                (IsNavigatingBack ? Forward : Back).Push(ExpressionEditor_Current);
            }
            OpenExpressionTab(obj);
        }

        private void LoadExpressionTab(ExpressionTab tab)
        {
            activeExpressionTab = tab;
            ExpressionEditor_IsEditing = tab?.Drafts.Count > 0;
            var obj = tab?.Object;
            // Update the Expression Selector combobox:
            UI.ExpressionSelector.ComboBox.SelectedValueChanged -= ExpressionEditor_ExpressionSelectorChanged;
            UI.ExpressionSelector.Items.Clear();
            var ddo = obj as IDaxDependantObject;
            if (ddo != null)
            {
                UI.ExpressionSelector.Enabled = true;
                if (obj is MPartition) UI.ExpressionSelector.Items.Add(DaxPropertyComboBoxItem.MExpression);
                else if (obj is Partition) UI.ExpressionSelector.Items.Add(DaxPropertyComboBoxItem.Query);
                foreach (var daxProp in ddo.GetDAXProperties()) UI.ExpressionSelector.Items.Add(DaxPropertyComboBoxItem.Items[daxProp]);
                CurrentDaxProperty = tab.Property;
            }
            else
            {
                UI.ExpressionSelector.Enabled = false;
            }
            UI.ExpressionSelector.ComboBox.SelectedValueChanged += ExpressionEditor_ExpressionSelectorChanged;

            ExpressionEditor_SetText();
            UI.ExpressionEditor.Enabled = obj != null;
        }

        private void ExpressionEditor_Edit(IExpressionObject obj, bool switchToTab = true)
        {
            if (obj == null) return;
            if (switchToTab)
            {
                var page = UI.ExpressionEditor.Parent as TabPage;
                if (page is TabPage)
                    (page.Parent as TabControl).SelectTab(page);
            }

            if (!ReferenceEquals(ExpressionEditor_Current, obj)) ExpressionEditor_Preview(obj);
            UI.StatusLabel.Text = "Editing " + ((obj as IDaxObject)?.DaxObjectFullName ?? obj.Name);
            ExpressionEditor_IsEditing = true;
            if(!UI.ExpressionEditor.Focused) UI.ExpressionEditor.SelectAll();
            UI.ExpressionEditor.Focus();
        }

        public void ExpressionEditor_CancelEdit()
        {
            if (ExpressionEditor_IsEditing)
            {
                UI.StatusLabel.Text = "";
                activeExpressionTab?.Drafts.Remove(CurrentDaxProperty);
                if (activeExpressionTab != null) UpdateExpressionTab(activeExpressionTab);
                ExpressionEditor_IsEditing = false;
                ExpressionEditor_SetText();
                UI.TreeView.Focus();
            }
        }

        /// <returns>False when the active tab's draft could not be applied.</returns>
        public bool ExpressionEditor_AcceptEdit() =>
            activeExpressionTab == null || SaveExpressionTabs(new[] { activeExpressionTab });

        public bool ExpressionEditor_IsDirty => activeExpressionTab?.Drafts.Count > 0;
    }
}
