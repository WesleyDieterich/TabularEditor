using System;
using FastColoredTextBox = TabularEditor.UI.Extensions.FCTB.FastColoredTextBox;

namespace TabularEditor.UI
{
    /// <summary>
    /// Session-wide word wrap for the code editors (DAX Query, Expression Editor, C# Script),
    /// toggled with Alt+Z like VS Code. Not persisted: every start begins unwrapped.
    /// </summary>
    internal static class EditorWordWrap
    {
        public static bool Enabled { get; private set; }

        public static event EventHandler Changed;

        public static void Toggle()
        {
            Enabled = !Enabled;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Applies the current state to the editor and keeps it in sync until it is disposed.</summary>
        public static void Attach(FastColoredTextBox editor)
        {
            if (editor == null) return;
            EventHandler update = (sender, args) => Apply(editor);
            Apply(editor);
            Changed += update;
            editor.Disposed += (sender, args) => Changed -= update;
        }

        private static void Apply(FastColoredTextBox editor)
        {
            if (editor.IsDisposed || editor.WordWrap == Enabled) return;
            editor.WordWrap = Enabled;
            // Unwrapping can leave the view scrolled sideways; bring the caret back into view.
            editor.DoCaretVisible();
        }
    }
}
