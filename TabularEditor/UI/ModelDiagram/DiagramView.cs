using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.UI.Extensions;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// The "Model Diagram" workspace tab: tables and relationships of the loaded model, kept
    /// in sync with model changes and with the selection in the model tree.
    /// </summary>
    internal sealed class DiagramView : UserControl
    {
        private const int RebuildDelayMs = 150;
        private const int SaveDelayMs = 800;

        private readonly Func<TabularModelHandler> getHandler;
        private readonly DiagramCanvas canvas = new DiagramCanvas { Dock = DockStyle.Fill };
        private readonly ToolStrip toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        private readonly ToolStripLabel layoutName = new ToolStripLabel(DiagramLayout.AllTables) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        private readonly ToolStripLabel zoomLabel = new ToolStripLabel("100%") { AutoSize = false, Width = 48 };
        private readonly ToolStripTextBox search = new ToolStripTextBox { AutoSize = false, Width = 180 };
        private readonly ToolStripLabel status = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right };
        private readonly Timer rebuildTimer = new Timer { Interval = RebuildDelayMs };
        private readonly Timer saveTimer = new Timer { Interval = SaveDelayMs };

        private TabularModelHandler handler;
        private TabularTree tree;
        private DiagramLayoutFile layoutFile;
        private bool rebuildPending;
        private bool fitPending;
        private bool syncingSelection;

        public DiagramView(Func<TabularModelHandler> getHandler)
        {
            this.getHandler = getHandler;
            Dock = DockStyle.Fill;

            toolbar.Items.Add(layoutName);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("−", "Zoom out (Ctrl+-)", () => canvas.ZoomBy(0.8f)));
            toolbar.Items.Add(zoomLabel);
            toolbar.Items.Add(Button("+", "Zoom in (Ctrl++)", () => canvas.ZoomBy(1.25f)));
            toolbar.Items.Add(Button("Fit", "Fit all tables in the window (Ctrl+0)", () => canvas.FitToWindow()));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("Arrange all", "Place every table automatically, replacing the positions you set", ArrangeAll));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(search);
            toolbar.Items.Add(status);
            search.Control.SetCueBanner("Find table");

            Controls.Add(canvas);
            Controls.Add(toolbar);

            canvas.ObjectClicked += Canvas_ObjectClicked;
            canvas.NodesMoved += (s, e) => ScheduleSave();
            canvas.ViewChanged += (s, e) => { UpdateZoomLabel(); ScheduleSave(); };
            canvas.HoverChanged += (s, obj) => status.Text = obj is SingleColumnRelationship r ? Describe(r) : "";
            search.TextChanged += (s, e) => ApplySearch(scrollToFirst: false);
            search.KeyDown += Search_KeyDown;
            rebuildTimer.Tick += (s, e) => { rebuildTimer.Stop(); if (Visible) Rebuild(); };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveLayout(); };
            VisibleChanged += (s, e) => { if (Visible && rebuildPending) Rebuild(); };

            if (UIController.Current != null) UIController.Current.SelectionChanged += Tree_SelectionChanged;
            Disposed += (s, e) =>
            {
                SaveLayout();
                Detach();
                if (UIController.Current != null) UIController.Current.SelectionChanged -= Tree_SelectionChanged;
                rebuildTimer.Dispose();
                saveTimer.Dispose();
            };
        }

        private static ToolStripButton Button(string text, string tip, Action click)
        {
            var button = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += (s, e) => click();
            return button;
        }

        /// <summary>Called when a model is loaded or closed.</summary>
        public void ResetModel()
        {
            SaveLayout();
            Detach();
            handler = getHandler();
            if (handler != null)
            {
                handler.ObjectChanged += Model_Changed;
                handler.ObjectDeleted += Model_Deleted;
                tree = handler.Tree;
                if (tree != null)
                {
                    tree.StructureChanged += Tree_StructureChanged;
                    tree.UpdateComplete += Tree_UpdateComplete;
                }
            }

            layoutFile = DiagramLayoutStore.Load(DiagramLayoutStore.GetModelKey(handler)) ?? new DiagramLayoutFile();
            var layout = CurrentLayout;
            fitPending = layout.Nodes.Count == 0;
            canvas.Graph = DiagramGraph.Empty;
            canvas.Zoom = layout.Zoom;
            canvas.Scroll = new PointF(layout.ScrollX, layout.ScrollY);
            search.Text = "";
            rebuildPending = true;
            if (Visible) Rebuild();
        }

        private void Detach()
        {
            rebuildTimer.Stop();
            if (handler != null)
            {
                handler.ObjectChanged -= Model_Changed;
                handler.ObjectDeleted -= Model_Deleted;
            }
            if (tree != null)
            {
                tree.StructureChanged -= Tree_StructureChanged;
                tree.UpdateComplete -= Tree_UpdateComplete;
            }
            handler = null;
            tree = null;
        }

        private DiagramLayout CurrentLayout
        {
            get
            {
                var layout = layoutFile.Layouts.FirstOrDefault(l => l.Name == DiagramLayout.AllTables);
                if (layout == null)
                {
                    layout = new DiagramLayout();
                    layoutFile.Layouts.Insert(0, layout);
                }
                return layout;
            }
        }

        #region Keeping in sync with the model

        private void Model_Changed(object sender, ObjectChangedEventArgs e) => ScheduleRebuild();
        private void Model_Deleted(object sender, ObjectDeletedEventArgs e) => ScheduleRebuild();
        private void Tree_StructureChanged(object sender, ITabularNamedObject e) => ScheduleRebuild();
        private void Tree_UpdateComplete(object sender, EventArgs e) => ScheduleRebuild();

        /// <summary>
        /// Model events arrive in bursts (a script can change thousands of objects), so rebuilding
        /// waits until they stop, and only happens while the tab is visible.
        /// </summary>
        private void ScheduleRebuild()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)ScheduleRebuild);
                return;
            }
            rebuildPending = true;
            if (!Visible) return;
            rebuildTimer.Stop();
            rebuildTimer.Start();
        }

        private void Rebuild()
        {
            rebuildPending = false;
            var model = handler?.Model;
            var previous = canvas.Graph.Nodes.ToDictionary(n => n.Table, n => n.Location);
            var graph = DiagramGraph.Build(model);
            var layout = CurrentLayout;

            var unplaced = new List<DiagramNode>();
            foreach (var node in graph.Nodes)
            {
                node.Size = new SizeF(DiagramCanvas.CardWidth, DiagramCanvas.CardHeight(node.Fields.Count));
                if (previous.TryGetValue(node.Table, out var location))
                    node.Location = location;
                else if (layout.Find(node.Table.Name, node.Table.LineageTag) is DiagramNodeLayout saved)
                    node.Location = new PointF(saved.X, saved.Y);
                else
                    unplaced.Add(node);
            }

            // Restore the saved stacking order: nodes saved later are drawn on top.
            var order = layout.Nodes.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
            foreach (var node in graph.Nodes
                .Select(n => (node: n, saved: layout.Find(n.Table.Name, n.Table.LineageTag)))
                .Where(p => p.saved != null)
                .OrderBy(p => order[p.saved])
                .Select(p => p.node)
                .ToList())
                graph.BringToFront(node);

            AutoLayout.Arrange(graph, unplaced);
            canvas.Graph = graph;
            canvas.SetSelection(CurrentTreeSelection());
            ApplySearch(scrollToFirst: false);

            if (fitPending && graph.Nodes.Count > 0)
            {
                fitPending = false;
                canvas.FitToWindow();
            }
            if (unplaced.Count > 0 && graph.Nodes.Count > 0) ScheduleSave();
            UpdateStatus();
        }

        private void ArrangeAll()
        {
            var graph = canvas.Graph;
            if (graph.Nodes.Count == 0) return;
            AutoLayout.Arrange(graph, graph.Nodes.ToList());
            canvas.FitToWindow();
            ScheduleSave();
        }

        #endregion

        #region Selection

        private static IEnumerable<ITabularNamedObject> CurrentTreeSelection() =>
            UIController.Current?.Selection?.ToList() ?? Enumerable.Empty<ITabularNamedObject>();

        private void Tree_SelectionChanged(object sender, EventArgs e)
        {
            if (syncingSelection || IsDisposed) return;
            canvas.SetSelection(CurrentTreeSelection());
        }

        private void Canvas_ObjectClicked(object sender, ITabularNamedObject obj)
        {
            var ui = UIController.Current;
            if (ui == null) return;
            syncingSelection = true;
            try
            {
                ui.Goto(obj, focusTree: false);
            }
            finally
            {
                syncingSelection = false;
            }
            // Goto may not find a tree node (for example when the tree is filtered); show the
            // click in the diagram regardless.
            var selected = CurrentTreeSelection().ToList();
            canvas.SetSelection(selected.Contains(obj) ? selected : new List<ITabularNamedObject> { obj });
            canvas.Focus();
        }

        #endregion

        #region Search

        private void ApplySearch(bool scrollToFirst)
        {
            var text = search.Text.Trim();
            var matches = text.Length == 0
                ? new List<DiagramNode>()
                : canvas.Graph.Nodes
                    .Where(n => CultureInfo.CurrentCulture.CompareInfo.IndexOf(n.Table.Name, text, CompareOptions.IgnoreCase) >= 0)
                    .ToList();
            canvas.SetSearchMatches(matches);
            if (scrollToFirst && matches.Count > 0)
                canvas.CenterOn(matches.OrderBy(n => n.Table.Name.Length).First());
            UpdateStatus(text.Length == 0 ? null : matches.Count == 1 ? "1 table found" : $"{matches.Count} tables found");
        }

        private void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                ApplySearch(scrollToFirst: true);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                search.Text = "";
                canvas.Focus();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        #endregion

        #region Saving the layout

        private void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        /// <summary>
        /// Writes every table's position. The model key is worked out at save time, so a model
        /// saved under a new path keeps its layout from then on.
        /// </summary>
        private void SaveLayout()
        {
            saveTimer.Stop();
            if (layoutFile == null || handler == null) return;
            var key = DiagramLayoutStore.GetModelKey(handler);
            if (key == null) return;

            var layout = CurrentLayout;
            var graph = canvas.Graph;
            if (graph.Nodes.Count > 0)
            {
                // Keep entries for tables not in the model right now: a table removed by mistake
                // and restored with undo comes back where it was.
                var current = new HashSet<string>(graph.Nodes.Select(n => n.Table.Name), StringComparer.OrdinalIgnoreCase);
                var kept = layout.Nodes.Where(n => !current.Contains(n.Table) &&
                    !graph.Nodes.Any(g => !string.IsNullOrEmpty(n.LineageTag) && n.LineageTag == g.Table.LineageTag));
                layout.Nodes = kept.Concat(graph.Nodes.Select(n => new DiagramNodeLayout
                {
                    Table = n.Table.Name,
                    LineageTag = string.IsNullOrEmpty(n.Table.LineageTag) ? null : n.Table.LineageTag,
                    X = n.Location.X,
                    Y = n.Location.Y,
                })).ToList();
            }
            layout.Zoom = canvas.Zoom;
            layout.ScrollX = canvas.Scroll.X;
            layout.ScrollY = canvas.Scroll.Y;
            layoutFile.SelectedLayout = layout.Name;
            DiagramLayoutStore.Save(key, layoutFile);
        }

        #endregion

        #region Status

        private void UpdateZoomLabel() => zoomLabel.Text = Math.Round(canvas.Zoom * 100) + "%";

        private void UpdateStatus(string message = null)
        {
            var graph = canvas.Graph;
            status.Text = message ?? (graph.Nodes.Count == 0 ? "" :
                $"{graph.Nodes.Count} tables, {graph.Edges.Count} relationships");
        }

        private static string Describe(SingleColumnRelationship r)
        {
            var direction = r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections ? "both directions" : "single direction";
            var cardinality = $"{Cardinality(r.FromCardinality)}:{Cardinality(r.ToCardinality)}";
            return $"{r.Name}  ·  {cardinality}, {direction}{(r.IsActive ? "" : ", inactive")}";
        }

        private static string Cardinality(RelationshipEndCardinality c) =>
            c == RelationshipEndCardinality.Many ? "*" : c == RelationshipEndCardinality.One ? "1" : "?";

        #endregion

        /// <summary>Repaints with the current theme colours.</summary>
        public void ApplyTheme()
        {
            AppTheme.ApplyWorkspaceControl(this);
            canvas.Invalidate();
        }
    }
}
