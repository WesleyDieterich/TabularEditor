using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FastColoredTextBoxNS;
using TabularEditor.UIServices;

namespace TabularEditor.UI
{
    internal static class AppTheme
    {
        internal static readonly Color Window = Color.FromArgb(27, 31, 36);
        internal static readonly Color Surface = Color.FromArgb(35, 40, 47);
        internal static readonly Color Raised = Color.FromArgb(44, 51, 60);
        internal static readonly Color Border = Color.FromArgb(65, 76, 89);
        internal static readonly Color Text = Color.FromArgb(231, 237, 244);
        internal static readonly Color Muted = Color.FromArgb(166, 179, 195);
        internal static readonly Color Selection = Color.FromArgb(48, 75, 103);
        internal static readonly Color Accent = Color.FromArgb(133, 191, 250);
        internal static readonly SolidBrush SelectionBrush = new SolidBrush(Selection);
        internal static readonly SolidBrush RaisedBrush = new SolidBrush(Raised);
        internal static readonly Pen BorderPen = new Pen(Border);
        internal static readonly Pen AccentPen = new Pen(Accent);
        internal static readonly SolidBrush CodeText = new SolidBrush(Text);
        internal static readonly SolidBrush CodeKeyword = new SolidBrush(Accent);
        internal static readonly SolidBrush CodeComment = new SolidBrush(Color.FromArgb(145, 198, 153));
        internal static readonly SolidBrush CodeLiteral = new SolidBrush(Color.FromArgb(241, 181, 141));
        internal static readonly SolidBrush CodeType = new SolidBrush(Color.FromArgb(130, 210, 200));
        internal static readonly SolidBrush CodePunctuation = new SolidBrush(Muted);
        internal static bool IsDark => Preferences.Current.UseDarkTheme && !SystemInformation.HighContrast;

        private static readonly ConditionalWeakTable<Form, ThemeState> Themes = new ConditionalWeakTable<Form, ThemeState>();
        private static readonly ConditionalWeakTable<DaxQueryEditor, ThemeState> QueryThemes =
            new ConditionalWeakTable<DaxQueryEditor, ThemeState>();
        private static readonly ToolStripRenderer Renderer = new DarkRenderer();
        private static readonly ToolStripRenderer CompletionRenderer = new AutocompleteRenderer();
        private static readonly ConditionalWeakTable<TextStyle, Brush> LightBrushes = new ConditionalWeakTable<TextStyle, Brush>();

        internal static void ApplySyntaxColor(Style style, Brush darkBrush)
        {
            if (!(style is TextStyle textStyle)) return;
            var lightBrush = LightBrushes.GetValue(textStyle, key => key.ForeBrush ?? SystemBrushes.WindowText);
            textStyle.ForeBrush = IsDark ? darkBrush : lightBrush;
        }

        internal static void ApplyAutocomplete(AutocompleteMenu menu)
        {
            menu.Renderer = CompletionRenderer;
            menu.DropShadowEnabled = false;
            menu.BackColor = IsDark ? Surface : SystemColors.Window;
            menu.Items.BackColor = menu.BackColor;
            menu.Items.ForeColor = IsDark ? Text : SystemColors.WindowText;
            // FastColoredTextBox uses a gradient for SelectedColor. Highlight the item itself instead.
            menu.SelectedColor = Color.Transparent;
            menu.HoveredColor = Color.Transparent;

            var tip = menu.ToolTip;
            tip.OwnerDraw = IsDark;
            tip.BackColor = IsDark ? Raised : SystemColors.Window;
            tip.ForeColor = IsDark ? Text : SystemColors.WindowText;
            tip.Draw -= DrawAutocompleteToolTip;
            tip.Draw += DrawAutocompleteToolTip;
        }

        private static Color CompletionBorder => IsDark ? Border : SystemColors.ControlDark;

        private static void DrawAutocompleteToolTip(object sender, DrawToolTipEventArgs e)
        {
            e.DrawBackground();
            using (var pen = new Pen(CompletionBorder))
                e.Graphics.DrawRectangle(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - 1, e.Bounds.Height - 1);
            e.DrawText(TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private sealed class AutocompleteRenderer : ToolStripSystemRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(e.ToolStrip.BackColor))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var size = e.ToolStrip.ClientSize;
                using (var pen = new Pen(CompletionBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
            }
        }

        internal static Color AutocompleteItemBackground(AutocompleteItem item)
        {
            if (item.Parent?.Items.FocussedItem != item) return Color.Transparent;
            return SystemInformation.HighContrast ? SystemColors.Highlight
                : IsDark ? Selection : Color.FromArgb(222, 235, 249);
        }

        internal static Color AutocompleteItemForeground(AutocompleteItem item) =>
            SystemInformation.HighContrast && item.Parent?.Items.FocussedItem == item
                ? SystemColors.HighlightText : Color.Transparent;

        internal static void ApplyOpenForms(object sender, EventArgs e)
        {
            foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
                Apply(form);
        }

        internal static void Apply(Form form)
        {
            if (form.IsDisposed) return;
            var dark = IsDark;
            if (!Themes.TryGetValue(form, out var state))
            {
                if (!dark) return;
                state = new ThemeState();
                Themes.Add(form, state);
                form.HandleCreated += UpdateTitleBar;
            }
            if (state.Dark == dark) return;

            form.SuspendLayout();
            try
            {
                state.Dark = dark;
                if (dark) state.Apply(form);
                else
                {
                    state.Restore();
                    Themes.Remove(form);
                    form.HandleCreated -= UpdateTitleBar;
                }
                UpdateTitleBar(form, EventArgs.Empty);
            }
            finally
            {
                form.ResumeLayout(true);
                form.Invalidate(true);
            }
        }

        internal static void ApplyQueryEditor(DaxQueryEditor editor)
        {
            if (IsDark)
                QueryThemes.GetValue(editor, query =>
                {
                    var state = new ThemeState();
                    state.Apply(query);
                    return state;
                });
            else if (QueryThemes.TryGetValue(editor, out var previous))
            {
                previous.Restore();
                QueryThemes.Remove(editor);
            }
        }

        internal static void ApplyContextMenu(ContextMenuStrip menu)
        {
            if (IsDark) menu.Renderer = Renderer;
            else menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            menu.BackColor = IsDark ? Surface : SystemColors.Control;
            menu.ForeColor = IsDark ? Text : SystemColors.ControlText;
        }

        private static void UpdateTitleBar(object sender, EventArgs e)
        {
            const int immersiveDarkMode = 20, captionColor = 35, textColor = 36;
            var form = (Form)sender;
            if (!form.IsHandleCreated) return;
            var mode = IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, immersiveDarkMode, ref mode, sizeof(int));
            var caption = IsDark ? ColorTranslator.ToWin32(Surface) : -1;
            var text = IsDark ? ColorTranslator.ToWin32(Text) : -1;
            DwmSetWindowAttribute(form.Handle, captionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(form.Handle, textColor, ref text, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string applicationName, string idList);

        internal static void ApplyToolTip(ToolTip tip)
        {
            if (tip == null) return;
            tip.BackColor = IsDark ? Raised : SystemColors.Info;
            tip.ForeColor = IsDark ? Text : SystemColors.InfoText;
            tip.Draw -= DrawToolTip;
            tip.OwnerDraw = IsDark;
            if (IsDark) tip.Draw += DrawToolTip;
        }

        private static void DrawToolTip(object sender, DrawToolTipEventArgs e)
        {
            e.DrawBackground();
            e.DrawBorder();
            e.DrawText(TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private sealed class ThemeState
        {
            internal bool Dark;
            private readonly List<Action> restore = new List<Action>();
            private readonly HashSet<Control> visited = new HashSet<Control>();

            private void Set<T>(T original, Action<T> write, T value)
            {
                restore.Add(() => write(original));
                write(value);
            }

            internal void Restore()
            {
                for (var i = restore.Count - 1; i >= 0; i--) restore[i]();
            }

            internal void Apply(Control control)
            {
                if (!visited.Add(control)) return;

                // Capture ambient colors before changing the parent.
                if (control is PropertyGrid || control is FastColoredTextBox)
                    ApplyNestedScrollBars(control);
                else
                    foreach (Control child in control.Controls)
                        if (!(child is DaxQueryEditor)) Apply(child);
                if (control is ToolStrip hostedStrip)
                {
                    foreach (var host in hostedStrip.Items.OfType<ToolStripControlHost>()) Apply(host.Control);
                    foreach (var item in hostedStrip.Items.OfType<ToolStripDropDownItem>())
                        if (item.HasDropDownItems) Apply(item.DropDown);
                }
                if (control.ContextMenuStrip != null) Apply(control.ContextMenuStrip);
                if (control is ButtonBase buttonBase)
                {
                    var original = buttonBase.UseVisualStyleBackColor;
                    restore.Add(() => buttonBase.UseVisualStyleBackColor = original);
                }
                if (control is TabPage tabPage)
                {
                    var original = tabPage.UseVisualStyleBackColor;
                    restore.Add(() => tabPage.UseVisualStyleBackColor = original);
                }

                var back = control is TextBoxBase || control is ComboBox || control is ListBox ||
                    control is ListView || control is TreeView || control is Aga.Controls.Tree.TreeViewAdv ? Window : Surface;
                if (control.Parent is DataGridView || control.Parent?.Parent is DataGridView)
                    back = Window;
                Set(control.BackColor, value => control.BackColor = value, back);
                Set(control.ForeColor, value => control.ForeColor = value, control.ForeColor == SystemColors.GrayText ? Muted : Text);

                if (control is ToolStrip strip)
                {
                    var renderer = strip.Renderer;
                    var mode = strip.RenderMode;
                    restore.Add(() =>
                    {
                        if (mode == ToolStripRenderMode.Custom) strip.Renderer = renderer;
                        else strip.RenderMode = mode;
                    });
                    strip.Renderer = Renderer;
                    foreach (var label in strip.Items.OfType<ToolStripStatusLabel>())
                        Set(label.BorderSides, value => label.BorderSides = value, ToolStripStatusLabelBorderSides.None);
                }
                else if (control is PropertyGrid properties)
                {
                    Set(properties.ViewBackColor, value => properties.ViewBackColor = value, Window);
                    Set(properties.ViewForeColor, value => properties.ViewForeColor = value, Text);
                    Set(properties.ViewBorderColor, value => properties.ViewBorderColor = value, Border);
                    Set(properties.LineColor, value => properties.LineColor = value, Surface);
                    Set(properties.CategorySplitterColor, value => properties.CategorySplitterColor = value, Raised);
                    Set(properties.CategoryForeColor, value => properties.CategoryForeColor = value, Text);
                    Set(properties.HelpBackColor, value => properties.HelpBackColor = value, Raised);
                    Set(properties.HelpForeColor, value => properties.HelpForeColor = value, Text);
                    Set(properties.HelpBorderColor, value => properties.HelpBorderColor = value, Border);
                    Set(properties.CommandsBackColor, value => properties.CommandsBackColor = value, Raised);
                    Set(properties.CommandsForeColor, value => properties.CommandsForeColor = value, Text);
                    Set(properties.CommandsLinkColor, value => properties.CommandsLinkColor = value, Accent);
                    Set(properties.CommandsActiveLinkColor, value => properties.CommandsActiveLinkColor = value, Accent);
                    Set(properties.CommandsDisabledLinkColor, value => properties.CommandsDisabledLinkColor = value, Muted);
                    Set(properties.CommandsBorderColor, value => properties.CommandsBorderColor = value, Border);
                    Set(properties.DisabledItemForeColor, value => properties.DisabledItemForeColor = value, Muted);
                    Set(properties.SelectedItemWithFocusBackColor, value => properties.SelectedItemWithFocusBackColor = value, Selection);
                    Set(properties.SelectedItemWithFocusForeColor, value => properties.SelectedItemWithFocusForeColor = value, Text);
                }
                else if (control is DataGridView grid)
                {
                    Set(grid.BackgroundColor, value => grid.BackgroundColor = value, Window);
                    Set(grid.GridColor, value => grid.GridColor = value, Border);
                    Set(grid.EnableHeadersVisualStyles, value => grid.EnableHeadersVisualStyles = value, false);
                    StyleGridCells(grid.DefaultCellStyle, Window);
                    StyleGridCells(grid.AlternatingRowsDefaultCellStyle, Surface);
                    StyleGridCells(grid.ColumnHeadersDefaultCellStyle, Raised);
                    StyleGridCells(grid.RowHeadersDefaultCellStyle, Raised);
                }
                else if (control is FastColoredTextBox editor)
                {
                    editor.BackColor = Window;
                    Set(editor.IndentBackColor, value => editor.IndentBackColor = value, Surface);
                    Set(editor.LineNumberColor, value => editor.LineNumberColor = value, Muted);
                    Set(editor.CaretColor, value => editor.CaretColor = value, Text);
                    Set(editor.SelectionColor, value => editor.SelectionColor = value, Color.FromArgb(125, Selection));
                    Set(editor.CurrentLineColor, value => editor.CurrentLineColor = value, Surface);
                    Set(editor.PaddingBackColor, value => editor.PaddingBackColor = value, Window);
                    Set(editor.ServiceLinesColor, value => editor.ServiceLinesColor = value, Border);
                    Set(editor.FoldingIndicatorColor, value => editor.FoldingIndicatorColor = value, Muted);
                }
                else if (control is Button button)
                {
                    button.UseVisualStyleBackColor = false;
                    Set(button.FlatStyle, value => button.FlatStyle = value, FlatStyle.Flat);
                    Set(button.FlatAppearance.BorderColor, value => button.FlatAppearance.BorderColor = value, Border);
                    Set(button.FlatAppearance.MouseOverBackColor, value => button.FlatAppearance.MouseOverBackColor = value, Raised);
                }
                else if (control is ThemedTabControl workspaceTabs)
                    Set(workspaceTabs.DrawDarkBackground, value => workspaceTabs.DrawDarkBackground = value, true);
                else if (control is TabControl tabs && tabs.DrawMode == TabDrawMode.Normal)
                {
                    Set(tabs.DrawMode, value => tabs.DrawMode = value, TabDrawMode.OwnerDrawFixed);
                    tabs.DrawItem += DrawTab;
                    restore.Add(() => tabs.DrawItem -= DrawTab);
                }
                else if (control is TabPage page)
                    page.UseVisualStyleBackColor = false;
                else if (control is ComboBox combo && combo.DrawMode == DrawMode.Normal && combo.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    Set(combo.DrawMode, value => combo.DrawMode = value, DrawMode.OwnerDrawFixed);
                    combo.DrawItem += DrawComboItem;
                    restore.Add(() => combo.DrawItem -= DrawComboItem);
                }
                else if (control is LinkLabel link)
                {
                    Set(link.LinkColor, value => link.LinkColor = value, Accent);
                    Set(link.ActiveLinkColor, value => link.ActiveLinkColor = value, Text);
                    Set(link.VisitedLinkColor, value => link.VisitedLinkColor = value, Accent);
                }

                if (control is ComboBox selector)
                {
                    Set(selector.FlatStyle, value => selector.FlatStyle = value, FlatStyle.Standard);
                    selector.HandleCreated += StyleSelector;
                    if (selector.IsHandleCreated) StyleSelector(selector, EventArgs.Empty);
                    var chrome = new DarkSelectorChrome(selector);
                    restore.Add(() =>
                    {
                        chrome.Dispose();
                        selector.HandleCreated -= StyleSelector;
                        if (selector.IsHandleCreated) SetWindowTheme(selector.Handle, null, null);
                    });
                }
                else if (control is TextBoxBase input && input.BorderStyle == BorderStyle.Fixed3D)
                    Set(input.BorderStyle, value => input.BorderStyle = value, BorderStyle.FixedSingle);
                else if (control is ScrollBar scrollBar)
                {
                    var chrome = new DarkScrollBar(scrollBar);
                    restore.Add(chrome.Dispose);
                }
            }

            private void ApplyNestedScrollBars(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if (child is ScrollBar) Apply(child);
                    else ApplyNestedScrollBars(child);
                }
            }

            private void StyleGridCells(DataGridViewCellStyle style, Color background)
            {
                Set(style.BackColor, value => style.BackColor = value, background);
                Set(style.ForeColor, value => style.ForeColor = value, Text);
                Set(style.SelectionBackColor, value => style.SelectionBackColor = value, Selection);
                Set(style.SelectionForeColor, value => style.SelectionForeColor = value, Text);
            }
        }

        private static void StyleSelector(object sender, EventArgs e)
        {
            var selector = (ComboBox)sender;
            // List-only selectors paint their own chrome. Disable native hover animations on them.
            var listOnly = selector.DropDownStyle == ComboBoxStyle.DropDownList;
            SetWindowTheme(selector.Handle, listOnly ? "" : "DarkMode_CFD", listOnly ? "" : null);
        }

        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            var tabs = (TabControl)sender;
            if (e.Index < 0 || e.Index >= tabs.TabCount) return;
            DrawTab(e.Graphics, tabs, e.Index, e.Bounds);
            e.DrawFocusRectangle();
        }

        internal static void DrawTab(Graphics graphics, TabControl tabs, int index, Rectangle bounds)
        {
            var selected = index == tabs.SelectedIndex;
            using (var background = new SolidBrush(selected ? Raised : Surface))
                graphics.FillRectangle(background, bounds);
            if (selected)
                using (var brush = new SolidBrush(Accent))
                    graphics.FillRectangle(brush, bounds.Left, bounds.Bottom - 2, bounds.Width, 2);
            TextRenderer.DrawText(graphics, tabs.TabPages[index].Text, tabs.Font, bounds, selected ? Text : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var combo = (ComboBox)sender;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var background = new SolidBrush(selected ? Selection : Window))
                e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index < 0) return;
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), e.Font, e.Bounds,
                combo.Enabled ? Text : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        internal sealed class BorderlessToolStrip : ToolStrip { }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            private readonly ConditionalWeakTable<Image, Image> softenedIcons = new ConditionalWeakTable<Image, Image>();

            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is BorderlessToolStrip) return;

                if (e.ToolStrip is StatusStrip)
                {
                    var bounds = e.AffectedBounds;
                    using (var pen = new Pen(Border))
                        e.Graphics.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right - 1, bounds.Top);
                    return;
                }

                base.OnRenderToolStripBorder(e);
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                if (e.Image is Bitmap bitmap && bitmap.Width <= 32 && bitmap.Height <= 32)
                {
                    var image = softenedIcons.GetValue(bitmap, SoftenIconHighlights);
                    if (!ReferenceEquals(image, bitmap))
                        e = new ToolStripItemImageRenderEventArgs(e.Graphics, e.Item, image, e.ImageRectangle);
                }
                base.OnRenderItemImage(e);
            }

            private static Image SoftenIconHighlights(Image image)
            {
                var source = (Bitmap)image;
                Bitmap softened = null;
                for (var y = 0; y < source.Height; y++)
                {
                    for (var x = 0; x < source.Width; x++)
                    {
                        var pixel = source.GetPixel(x, y);
                        if (pixel.A == 0) continue;

                        // Old glyphs use near-white outlines. Keep the alpha and colored accents.
                        var minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                        var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - minimum;
                        if (minimum <= 165 || spread >= 24) continue;

                        var reduction = (int)Math.Round(68 * (minimum - 165) / 90.0 * (24 - spread) / 24.0);
                        if (reduction == 0) continue;
                        if (softened == null) softened = new Bitmap(source);
                        softened.SetPixel(x, y, Color.FromArgb(pixel.A,
                            pixel.R - reduction, pixel.G - reduction, pixel.B - reduction));
                    }
                }
                return softened ?? image;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderArrow(e);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Surface;
            public override Color ToolStripGradientMiddle => Surface;
            public override Color ToolStripGradientEnd => Surface;
            public override Color MenuStripGradientBegin => Surface;
            public override Color MenuStripGradientEnd => Surface;
            public override Color StatusStripGradientBegin => Surface;
            public override Color StatusStripGradientEnd => Surface;
            public override Color ToolStripDropDownBackground => Surface;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color MenuBorder => Border;
            public override Color ToolStripBorder => Border;
            public override Color OverflowButtonGradientBegin => Raised;
            public override Color OverflowButtonGradientMiddle => Raised;
            public override Color OverflowButtonGradientEnd => Raised;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Surface;
            public override Color MenuItemSelected => Raised;
            public override Color MenuItemBorder => Border;
            public override Color MenuItemSelectedGradientBegin => Raised;
            public override Color MenuItemSelectedGradientEnd => Raised;
            public override Color MenuItemPressedGradientBegin => Selection;
            public override Color MenuItemPressedGradientMiddle => Selection;
            public override Color MenuItemPressedGradientEnd => Selection;
            public override Color ButtonCheckedGradientBegin => Selection;
            public override Color ButtonCheckedGradientMiddle => Selection;
            public override Color ButtonCheckedGradientEnd => Selection;
            public override Color ButtonCheckedHighlightBorder => Border;
            public override Color ButtonSelectedGradientBegin => Raised;
            public override Color ButtonSelectedGradientMiddle => Raised;
            public override Color ButtonSelectedGradientEnd => Raised;
            public override Color ButtonSelectedBorder => Border;
            public override Color ButtonPressedGradientBegin => Selection;
            public override Color ButtonPressedGradientMiddle => Selection;
            public override Color ButtonPressedGradientEnd => Selection;
        }
    }

    internal sealed class ThemedTabControl : TabControl
    {
        private bool drawDarkBackground;
        private bool editorTabStyle;

        internal void UseEditorTabStyle()
        {
            editorTabStyle = true;
            ItemSize = new Size(0, 27);
            Padding = new Point(10, 3);
            DrawDarkBackground = drawDarkBackground;
        }

        internal bool DrawDarkBackground
        {
            get => drawDarkBackground;
            set
            {
                drawDarkBackground = value;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
                    value || editorTabStyle);
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (editorTabStyle) e.Graphics.Clear(DaxQueryTabs.TabBackColor(false));
            else if (drawDarkBackground) e.Graphics.Clear(AppTheme.Surface);
            else base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (editorTabStyle)
            {
                using (var separator = new Pen(DaxQueryTabs.TabSeparatorColor))
                    for (var i = 0; i < TabCount; i++)
                    {
                        var bounds = GetTabRect(i);
                        using (var background = new SolidBrush(DaxQueryTabs.TabBackColor(i == SelectedIndex)))
                            e.Graphics.FillRectangle(background, bounds);
                        TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, bounds,
                            AppTheme.IsDark ? AppTheme.Text : SystemColors.ControlText,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        e.Graphics.DrawLine(separator, bounds.Right - 1, bounds.Top + 4, bounds.Right - 1, bounds.Bottom - 5);
                    }
                return;
            }
            if (!drawDarkBackground)
            {
                base.OnPaint(e);
                return;
            }
            for (var i = 0; i < TabCount; i++)
                AppTheme.DrawTab(e.Graphics, this, i, GetTabRect(i));
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            if (drawDarkBackground || editorTabStyle) Invalidate();
        }
    }
}
