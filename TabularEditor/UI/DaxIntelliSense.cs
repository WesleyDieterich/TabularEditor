using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FastColoredTextBoxNS;
using TabularEditor.TextServices;
using TabularEditor.TOMWrapper;
using TabularEditor.UIServices;

namespace TabularEditor.UI
{
    internal sealed class DaxIntelliSense
    {
        private readonly FastColoredTextBox editor;
        private readonly Func<bool> isDax;
        private readonly Func<Model> getModel;
        private readonly bool queryEditor;
        private static readonly string[] QueryKeywords =
        {
            "DEFINE", "EVALUATE", "MEASURE", "ORDER BY", "START AT", "ASC", "DESC", "VAR", "RETURN"
        };
        private readonly AutocompleteMenu completion;
        private readonly ToolTip parameterTip = new ToolTip();
        private bool manualCompletion;
        private DaxCaretContext completionContext;

        private bool IsActive => editor.Enabled && editor.Visible && editor.Focused && !editor.ReadOnly && isDax();

        public DaxIntelliSense(FastColoredTextBox editor, Func<bool> isDax, Func<Model> getModel, bool queryEditor = false)
        {
            this.editor = editor;
            this.isDax = isDax;
            this.getModel = getModel;
            this.queryEditor = queryEditor;

            editor.KeyDown += HandleEditorKeyDown;
            completion = new AutocompleteMenu(editor)
            {
                SearchPattern = @"[\w.\[']",
                MinFragmentLength = 0,
                AppearInterval = 150,
                AllowTabKey = true
            };
            completion.Items.SetAutocompleteItems(SizeCompletionList());
            completion.Selecting += Completion_Selecting;
            completion.Selected += (sender, args) => ShowParameterHelp();

            editor.KeyPressing += IndentNewLine;
            editor.KeyPressed += (sender, args) => { if (args.KeyChar == '(') ShowParameterHelp(); };
            editor.ToolTipNeeded += ShowHoveredFunctionHelp;
            editor.SelectionChanged += (sender, args) => parameterTip.Hide(editor);
            editor.TextChanged += (sender, args) => HideHelp();
            editor.VisibleRangeChanged += (sender, args) => parameterTip.Hide(editor);
            editor.LostFocus += (sender, args) => Close();
            editor.VisibleChanged += (sender, args) => { if (!editor.Visible) Close(); };
            editor.Disposed += (sender, args) =>
            {
                parameterTip.Dispose();
                completion.Dispose();
            };
        }

        public void Close()
        {
            manualCompletion = false;
            completionContext = null;
            completion.Close();
            HideHelp();
        }

        internal void ApplyTheme()
        {
            Close();
            AppTheme.ApplyAutocomplete(completion, Color.Orange, Color.Red);
            AppTheme.ApplyToolTip(parameterTip);
            AppTheme.ApplyToolTip(editor.ToolTip);
        }

        private void HandleEditorKeyDown(object sender, KeyEventArgs args)
        {
            manualCompletion = editor.HotkeysMapping.TryGetValue(args.KeyData, out var action)
                && action == FCTBAction.AutocompleteMenu;

        }

        private void HideHelp()
        {
            if (!editor.IsHandleCreated) return;
            parameterTip.Hide(editor);
            editor.ToolTip?.Hide(editor);
        }

        private void ShowParameterHelp()
        {
            if (!IsActive || !editor.Selection.IsEmpty) return;

            var text = editor.Text;
            var position = editor.SelectionStart;
            if (position == 0 || text[position - 1] != '(') return;

            var useSemicolons = Preferences.Current.UseSemicolonsAsSeparators;
            var context = DaxCaretContext.Analyze(text, position, useSemicolons);
            if (context.InCommentOrString || context.ReferenceDelimiter != '\0') return;

            var call = context.Calls.FirstOrDefault();
            if (call.Name == null || !DaxFunctionCatalog.Functions.Value.TryGetValue(call.Name, out var function)) return;

            var caret = editor.PlaceToPoint(editor.Selection.Start);
            if (!editor.ClientRectangle.Contains(caret)) return;

            var help = function.GetHelp(0, useSemicolons);
            var height = TextRenderer.MeasureText(help, SystemFonts.StatusFont).Height;
            parameterTip.Show(help, editor, new Point(caret.X, caret.Y - height - 9));
        }

        private void ShowHoveredFunctionHelp(object sender, ToolTipNeededEventArgs args)
        {
            if (!editor.Enabled || !isDax()) return;

            var text = editor.Text;
            var position = editor.PlaceToPosition(args.Place);
            if (position >= text.Length) return;

            var useSemicolons = Preferences.Current.UseSemicolonsAsSeparators;
            var context = DaxCaretContext.Analyze(text, position + 1, useSemicolons);
            if (context.InCommentOrString || context.ReferenceDelimiter != '\0' || context.Start > position) return;

            var next = context.End;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next >= text.Length || text[next] != '(') return;

            var name = text.Substring(context.Start, context.End - context.Start);
            if (!DaxFunctionCatalog.Functions.Value.TryGetValue(name, out var function)) return;

            args.ToolTipText = (useSemicolons ? function.Syntax.Replace(',', ';') : function.Syntax)
                + Environment.NewLine + function.Description;
        }

        private void IndentNewLine(object sender, KeyPressEventArgs args)
        {
            if (args.Handled || args.KeyChar != '\r' || !IsActive || !editor.Selection.IsEmpty) return;

            var text = editor.Text;
            var position = editor.SelectionStart;
            var context = DaxCaretContext.Analyze(text, position, Preferences.Current.UseSemicolonsAsSeparators);
            if (context.InCommentOrString || context.ReferenceDelimiter != '\0' || !context.OpenGroup.HasValue) return;

            var group = context.OpenGroup.Value;
            var openingLine = editor.GetLineText(editor.PositionToPlace(group.Position).iLine);
            var indent = new string(openingLine.TakeWhile(c => c == ' ' || c == '\t').ToArray());
            var argumentIndent = indent + new string(' ', editor.TabLength);
            var lineBreak = Environment.NewLine + argumentIndent;
            var end = position;
            while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
            var closing = group.Delimiter == '(' ? ')' : '}';
            var splitPair = end < text.Length && text[end] == closing;

            args.Handled = true;
            editor.BeginAutoUndo();
            try
            {
                if (splitPair) editor.Selection = editor.GetRange(position, end);
                editor.InsertText(splitPair ? lineBreak + Environment.NewLine + indent : lineBreak);
                if (splitPair) editor.SelectionStart = position + lineBreak.Length;
            }
            finally
            {
                editor.EndAutoUndo();
            }
            editor.DoCaretVisible();
        }

        private IEnumerable<AutocompleteItem> SizeCompletionList()
        {
            var items = GetCompletions().ToList();
            if (items.Count == 0) yield break;

            var scale = editor.DeviceDpi / 96f;
            var padding = (int)(24 * scale) + SystemInformation.VerticalScrollBarWidth;
            var width = Math.Max((int)(330 * scale),
                items.Max(item => TextRenderer.MeasureText(item.ToString(), completion.Font, Size.Empty, TextFormatFlags.NoPrefix).Width) + padding);
            width = Math.Min(width, Screen.FromControl(editor).WorkingArea.Width - 12);

            completion.Items.MaximumSize = new Size(0, completion.Items.MaximumSize.Height);
            completion.Items.Width = width;
            ((ToolStripDropDown)completion).Items[0].Width = width;
            completion.Width = width + 4;
            foreach (var item in items) yield return item;
        }

        private IEnumerable<AutocompleteItem> GetCompletions()
        {
            var forced = manualCompletion;
            manualCompletion = false;
            completionContext = null;
            if (!IsActive || !editor.Selection.IsEmpty) yield break;

            var text = editor.Text;
            var context = DaxCaretContext.Analyze(text, editor.SelectionStart, Preferences.Current.UseSemicolonsAsSeparators);
            if (context.InCommentOrString || (!forced && context.ReferenceDelimiter == '\0' && context.Prefix.Length < 2))
                yield break;

            completionContext = context;
            var next = context.End;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            var appendParentheses = next >= text.Length || text[next] != '(';

            if (context.ReferenceDelimiter == '\0')
            {
                if (queryEditor)
                    foreach (var keyword in QueryKeywords.Where(keyword => Matches(keyword, context.Prefix)))
                        yield return new CompletionItem(keyword, keyword, "DAX query keyword", string.Empty);

                foreach (var function in DaxFunctionCatalog.Functions.Value)
                {
                    if (Matches(function.Key, context.Prefix))
                        yield return new CompletionItem(function.Key, function.Key, function.Value.Syntax, function.Value.Description, appendParentheses);
                }
            }

            var model = getModel();
            if (model == null) yield break;

            if (context.ReferenceDelimiter != '[')
            {
                foreach (var table in model.Tables)
                {
                    if (Matches(table.Name, context.Prefix))
                        yield return new CompletionItem(table.DaxObjectName, table.DaxObjectName, "Table", table.Description);
                }
            }
            if (context.ReferenceDelimiter == '\'') yield break;

            var qualifyingTable = context.TableName == null ? null :
                model.Tables.FirstOrDefault(table => string.Equals(table.Name, context.TableName, StringComparison.OrdinalIgnoreCase));
            foreach (var table in qualifyingTable == null ? model.Tables.AsEnumerable() : new[] { qualifyingTable })
            {
                foreach (var measure in table.Measures)
                {
                    if (Matches(measure.Name, context.Prefix))
                        yield return new CompletionItem(measure.DaxObjectName, measure.DaxObjectName, "Measure", measure.Description);
                }
                foreach (var column in table.Columns)
                {
                    if (Matches(column.Name, context.Prefix))
                        yield return new CompletionItem(column.DaxObjectFullName,
                            qualifyingTable == null ? column.DaxObjectFullName : column.DaxObjectName, "Column", column.Description);
                }
            }
        }

        private static bool Matches(string name, string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private void Completion_Selecting(object sender, SelectingEventArgs args)
        {
            if (!IsActive || !editor.Selection.IsEmpty || completionContext == null)
            {
                args.Cancel = true;
                return;
            }

            // The menu's default word range cannot include spaces and escaped delimiters in DAX references.
            var context = DaxCaretContext.Analyze(editor.Text, editor.SelectionStart, Preferences.Current.UseSemicolonsAsSeparators);
            if (context.InCommentOrString || context.Start != completionContext.Start
                || context.End != completionContext.End || context.Prefix != completionContext.Prefix)
            {
                args.Cancel = true;
                completion.Close();
                return;
            }
            completion.Fragment.Start = editor.PositionToPlace(context.Start);
            completion.Fragment.End = editor.PositionToPlace(context.End);
        }

        private sealed class CompletionItem : AutocompleteItem
        {
            private readonly bool appendParentheses;

            public CompletionItem(string display, string replacement, string title, string description, bool appendParentheses = false)
                : base(replacement)
            {
                MenuText = display;
                ToolTipTitle = appendParentheses ? title : display;
                ToolTipText = appendParentheses ? description : string.IsNullOrEmpty(description) ? title : title + Environment.NewLine + description;
                this.appendParentheses = appendParentheses;
            }

            public override CompareResult Compare(string fragmentText) => CompareResult.Visible;

            public override string GetTextForReplace() => appendParentheses ? Text + "()" : Text;

            public override void OnSelected(AutocompleteMenu popup, SelectedEventArgs args)
            {
                if (appendParentheses)
                {
                    var inside = args.Tb.SelectionStart - 1;
                    args.Tb.Selection = args.Tb.GetRange(inside, inside);
                }
            }
        }
    }
}
