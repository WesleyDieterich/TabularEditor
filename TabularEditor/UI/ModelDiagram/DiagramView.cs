using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Aga.Controls.Tree;
using TabularEditor.TOMWrapper;
using TabularEditor.UI.Extensions;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// The "Model Diagram" workspace tab: tables and relationships of the loaded model, kept
    /// in sync with model changes and with the selection in the model tree. "All tables" shows
    /// every table; custom layouts show the tables added to them.
    /// </summary>
    internal sealed class DiagramView : UserControl
    {
        private const int RebuildDelayMs = 150;
        private const int SaveDelayMs = 800;
        private const string EmptyLayoutText = "This layout is empty.\nDrag tables here from the model tree.";
        private const string NoModelText = "No tables to show. Open or connect to a model.";

        private readonly Func<TabularModelHandler> getHandler;
        private readonly DiagramCanvas canvas = new DiagramCanvas { Dock = DockStyle.Fill };
        private readonly ToolStrip toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        private readonly ToolStrip layoutTabs = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Bottom };
        private readonly ToolStripLabel zoomLabel = new ToolStripLabel("100%") { AutoSize = false, Width = 48 };
        private readonly ToolStripTextBox search = new ToolStripTextBox { AutoSize = false, Width = 180 };
        private readonly ToolStripLabel status = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Timer rebuildTimer = new Timer { Interval = RebuildDelayMs };
        private readonly Timer saveTimer = new Timer { Interval = SaveDelayMs };

        private TabularModelHandler handler;
        private TabularTree tree;
        private DiagramLayoutFile layoutFile = new DiagramLayoutFile();
        private DiagramLayout layout;
        private bool rebuildPending;
        private bool fitPending;
        private bool syncingSelection;

        public DiagramView(Func<TabularModelHandler> getHandler)
        {
            this.getHandler = getHandler;
            Dock = DockStyle.Fill;
            layout = layoutFile.AllTablesLayout;

            toolbar.Items.Add(Button("−", "Zoom out (Ctrl+-)", () => canvas.ZoomBy(0.8f)));
            toolbar.Items.Add(zoomLabel);
            toolbar.Items.Add(Button("+", "Zoom in (Ctrl++)", () => canvas.ZoomBy(1.25f)));
            toolbar.Items.Add(Button("Fit", "Fit all tables in the window (Ctrl+0)", () => canvas.FitToWindow()));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("Arrange all", "Place every table in this layout automatically, replacing the positions you set", ArrangeAll));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("New relationship", "Create a relationship. You can also drag a column onto a column of another table.", () => NewRelationship()));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(search);
            toolbar.Items.Add(status);
            search.Control.SetCueBanner("Find table");

            Controls.Add(canvas);
            Controls.Add(toolbar);
            Controls.Add(layoutTabs);
            RefreshLayoutTabs();

            canvas.ObjectClicked += Canvas_ObjectClicked;
            canvas.LayoutChanged += (s, e) => ScheduleSave();
            canvas.ViewChanged += (s, e) => { UpdateZoomLabel(); ScheduleSave(); };
            canvas.HoverChanged += (s, obj) => status.Text = obj is SingleColumnRelationship r ? Describe(r) : StatusText();
            canvas.ContextMenuRequested += (s, e) => ShowContextMenu(e.Node, e.Edge, e.Location);
            canvas.RelationshipDragged += (s, e) => NewRelationship(RelationshipEdits.Suggest(e.Source, e.Target));
            canvas.EditRequested += (s, r) => EditRelationship(r);
            canvas.DeleteRequested += (s, list) => DeleteRelationships(list);
            canvas.DragEnter += Canvas_DragOver;
            canvas.DragOver += Canvas_DragOver;
            canvas.DragDrop += Canvas_DragDrop;
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
                menu.Dispose();
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
            search.Text = "";
            ShowLayout(layoutFile.Find(layoutFile.SelectedLayout) ?? layoutFile.AllTablesLayout, save: false);
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

        #region Layouts

        /// <summary>Switches to a layout, restoring its zoom and scroll position.</summary>
        private void ShowLayout(DiagramLayout target, bool save = true)
        {
            if (save) SaveLayout();
            layout = target;
            layoutFile.SelectedLayout = layout.Name;
            fitPending = !layout.Nodes.Any(n => n.IsPlaced);
            canvas.Graph = DiagramGraph.Empty;
            canvas.SelectNodes(Enumerable.Empty<DiagramNode>());
            canvas.Zoom = layout.Zoom;
            canvas.Scroll = new PointF(layout.ScrollX, layout.ScrollY);
            UpdateZoomLabel();
            RefreshLayoutTabs();
            rebuildPending = true;
            if (Visible) Rebuild();
            if (save) ScheduleSave();
        }

        private void RefreshLayoutTabs()
        {
            layoutTabs.SuspendLayout();
            layoutTabs.Items.Clear();
            foreach (var l in layoutFile.Layouts.OrderBy(l => l.IsAllTables ? 0 : 1).ToList())
            {
                var tab = new ToolStripButton(l.Name)
                {
                    Checked = l == layout,
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    DoubleClickEnabled = !l.IsAllTables,
                    ToolTipText = l.IsAllTables ? "Every table in the model" : "Double-click to rename. Right-click for more.",
                };
                if (l == layout) tab.Font = new Font(layoutTabs.Font, FontStyle.Bold);
                var target = l;
                tab.Click += (s, e) => { if (target != layout) ShowLayout(target); };
                tab.DoubleClick += (s, e) => RenameLayout(target);
                tab.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowLayoutTabMenu(target, tab); };
                layoutTabs.Items.Add(tab);
            }
            layoutTabs.Items.Add(Button("+", "New layout (starts with the tables selected in the diagram)", NewLayout));
            layoutTabs.ResumeLayout();
        }

        private void ShowLayoutTabMenu(DiagramLayout target, ToolStripItem tab)
        {
            menu.Items.Clear();
            menu.Items.Add("New layout", null, (s, e) => NewLayout());
            if (!target.IsAllTables)
            {
                menu.Items.Add("Rename layout...", null, (s, e) => RenameLayout(target));
                menu.Items.Add("Delete layout", null, (s, e) => DeleteLayout(target));
            }
            AppTheme.ApplyContextMenu(menu);
            menu.Show(layoutTabs, new Point(tab.Bounds.Left, tab.Bounds.Top), ToolStripDropDownDirection.AboveRight);
        }

        /// <summary>
        /// Creates a custom layout holding the tables currently selected in the diagram (or in
        /// the tree), in their current arrangement, so "select a few tables, click +" gives a
        /// focused view straight away.
        /// </summary>
        private void NewLayout()
        {
            var seed = canvas.SelectedNodes.ToList();
            var created = layoutFile.AddLayout();
            foreach (var node in seed)
                created.Nodes.Add(SaveNode(node));
            created.Zoom = canvas.Zoom;
            created.ScrollX = canvas.Scroll.X;
            created.ScrollY = canvas.Scroll.Y;
            ShowLayout(created);
            if (seed.Count > 0) canvas.FitToWindow();
        }

        private void RenameLayout(DiagramLayout target)
        {
            if (target.IsAllTables) return;
            var name = LayoutNameDialog.Ask(FindForm(), "Rename layout", target.Name, n => layoutFile.ValidateName(target, n));
            if (name == null || name == target.Name) return;
            target.Name = name;
            if (target == layout) layoutFile.SelectedLayout = name;
            RefreshLayoutTabs();
            ScheduleSave();
        }

        private void DeleteLayout(DiagramLayout target)
        {
            if (target.IsAllTables) return;
            var answer = AppMessageBox.Show(FindForm(),
                $"Delete the layout \"{target.Name}\"?\n\nOnly the layout is deleted. The tables stay in the model.",
                "Delete layout", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            layoutFile.Remove(target);
            if (target == layout) ShowLayout(layoutFile.AllTablesLayout);
            else
            {
                RefreshLayoutTabs();
                ScheduleSave();
            }
        }

        /// <summary>Adds tables to the current custom layout; returns how many were new.</summary>
        private int AddTables(IEnumerable<Table> tables, PointF? at)
        {
            if (layout.IsAllTables) return 0;
            var added = 0;
            var position = at;
            foreach (var table in tables.Distinct())
            {
                if (!layout.Add(table.Name, table.LineageTag)) continue;
                added++;
                if (position is PointF p)
                {
                    // Dropped tables land where they were dropped, side by side.
                    var node = layout.Find(table.Name, table.LineageTag);
                    node.X = p.X;
                    node.Y = p.Y;
                    position = new PointF(p.X + CardMetrics.Width + AutoLayout.HorizontalGap, p.Y);
                }
            }
            if (added > 0)
            {
                Rebuild();
                ScheduleSave();
            }
            return added;
        }

        private void AddRelatedTables(IEnumerable<DiagramNode> nodes)
        {
            var tables = nodes.SelectMany(n => DiagramGraph.RelatedTables(n.Table)).ToList();
            var added = AddTables(tables, null);
            UpdateStatus(added == 0 ? "All related tables are already in this layout" : $"Added {added} related table{(added == 1 ? "" : "s")}");
        }

        private void RemoveFromLayout(IEnumerable<DiagramNode> nodes)
        {
            if (layout.IsAllTables) return;
            // Positions first, so the remaining tables keep any unsaved moves.
            CaptureNodes();
            foreach (var node in nodes.ToList()) layout.Remove(node.Table.Name, node.Table.LineageTag);
            Rebuild();
            ScheduleSave();
        }

        #endregion

        #region Editing relationships

        private bool CanCreate => handler?.Model != null && handler.PowerBIGovernance.AllowCreate(typeof(SingleColumnRelationship));
        private bool CanDelete => handler?.Model != null && handler.PowerBIGovernance.AllowDelete(typeof(SingleColumnRelationship));

        /// <summary>
        /// Opens the relationship dialog for a new relationship. Without settings, the two tables
        /// selected in the diagram (if exactly two) are filled in.
        /// </summary>
        private void NewRelationship(RelationshipSpec spec = null)
        {
            if (!CanCreate)
            {
                AppMessageBox.Show(FindForm(), "Relationships cannot be created in this model.", "New relationship", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (spec == null)
            {
                spec = new RelationshipSpec();
                var tables = canvas.SelectedNodes.Select(n => n.Table).ToList();
                if (tables.Count == 2)
                {
                    spec.Column1 = tables[0].Columns.FirstOrDefault(c => c.IsKey) ?? tables[0].Columns.FirstOrDefault();
                    spec.Column2 = tables[1].Columns.FirstOrDefault(c => c.IsKey) ?? tables[1].Columns.FirstOrDefault();
                }
            }
            var result = RelationshipDialog.Show(FindForm(), handler, spec);
            if (result == null) return;
            var created = RelationshipEdits.Create(result);
            Rebuild();
            Canvas_ObjectClicked(canvas, created);
        }

        private void EditRelationship(SingleColumnRelationship relationship)
        {
            if (relationship.IsRemoved) return;
            var result = RelationshipDialog.Show(FindForm(), handler, RelationshipSpec.From(relationship), relationship);
            if (result == null) return;
            RelationshipEdits.Update(relationship, result);
            Rebuild();
            Canvas_ObjectClicked(canvas, relationship);
        }

        private void ToggleActive(SingleColumnRelationship relationship)
        {
            if (!relationship.IsActive && RelationshipEdits.ConflictsWithActiveRelationship(relationship.FromColumn.Table, relationship.ToColumn.Table, relationship))
            {
                AppMessageBox.Show(FindForm(), "Another active relationship already joins these tables. Make that one inactive first.",
                    "Make active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var spec = RelationshipSpec.From(relationship);
            spec.IsActive = !relationship.IsActive;
            var ambiguity = spec.IsActive ? RelationshipEdits.FindNewAmbiguity(spec, relationship) : null;
            if (ambiguity != null)
            {
                AppMessageBox.Show(FindForm(), ambiguity, "Make active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RelationshipEdits.Update(relationship, spec);
            Rebuild();
        }

        /// <summary>Deletes after confirmation, as one undo step.</summary>
        private void DeleteRelationships(IReadOnlyList<SingleColumnRelationship> relationships)
        {
            relationships = relationships.Where(r => !r.IsRemoved).ToList();
            if (relationships.Count == 0) return;
            if (!CanDelete)
            {
                AppMessageBox.Show(FindForm(), "Relationships cannot be deleted in this model.", "Delete relationship", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var text = relationships.Count == 1
                ? $"Delete the relationship {relationships[0].Name}?"
                : $"Delete {relationships.Count} relationships?";
            if (AppMessageBox.Show(FindForm(), text + "\n\nYou can undo this with Ctrl+Z.", "Delete relationship",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            handler.BeginUpdate(relationships.Count == 1 ? "delete relationship" : "delete relationships");
            try
            {
                foreach (var r in relationships) r.Delete();
            }
            finally
            {
                handler.EndUpdate();
            }
            Rebuild();
        }

        #endregion

        #region Context menu

        private void ShowContextMenu(DiagramNode node, DiagramEdge edge, Point location)
        {
            menu.Items.Clear();
            if (edge != null)
            {
                var r = edge.Relationship;
                menu.Items.Add("Edit relationship...", null, (s, e) => EditRelationship(r));
                menu.Items.Add(r.IsActive ? "Make inactive" : "Make active", null, (s, e) => ToggleActive(r));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Delete relationship", null, (s, e) => DeleteRelationships(new[] { r })).Enabled = CanDelete;
                AppTheme.ApplyContextMenu(menu);
                menu.Show(canvas, location);
                return;
            }
            var targets = node == null ? new List<DiagramNode>() : canvas.SelectedNodes.ToList();
            if (node != null && !targets.Contains(node)) targets = new List<DiagramNode> { node };

            if (targets.Count > 0)
            {
                var label = targets.Count == 1 ? "" : $" ({targets.Count} tables)";
                if (targets.Any(n => !n.Collapsed)) menu.Items.Add("Collapse" + label, null, (s, e) => canvas.SetCollapsed(targets, true));
                if (targets.Any(n => n.Collapsed)) menu.Items.Add("Expand" + label, null, (s, e) => canvas.SetCollapsed(targets, false));
                if (targets.Any(n => n.HasCustomSize)) menu.Items.Add("Reset size" + label, null, (s, e) => canvas.ResetSize(targets));
                if (!layout.IsAllTables)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Add related tables", null, (s, e) => AddRelatedTables(targets));
                    menu.Items.Add("Remove from layout" + label, null, (s, e) => RemoveFromLayout(targets));
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("New layout with " + (targets.Count == 1 ? "this table" : "these tables"), null, (s, e) =>
                {
                    canvas.SelectNodes(targets);
                    NewLayout();
                });
            }
            else
            {
                menu.Items.Add("Fit to window", null, (s, e) => canvas.FitToWindow());
                menu.Items.Add("Arrange all", null, (s, e) => ArrangeAll());
                menu.Items.Add("Select all", null, (s, e) => canvas.SelectNodes(canvas.Graph.Nodes));
                menu.Items.Add("New relationship...", null, (s, e) => NewRelationship()).Enabled = CanCreate;
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("New layout", null, (s, e) => NewLayout());
            }
            AppTheme.ApplyContextMenu(menu);
            menu.Show(canvas, location);
        }

        #endregion

        #region Drag and drop from the model tree

        /// <summary>
        /// Tables in a drag that started in the model tree. The tree publishes what it is
        /// dragging for the whole drag; the tree nodes in the drag data are only a fallback,
        /// since the tree removes them once the mouse leaves it.
        /// </summary>
        private static List<Table> DraggedTables(IDataObject data)
        {
            var fromTree = UIController.Current?.TreeDragObjects.OfType<Table>().ToList();
            if (fromTree != null && fromTree.Count > 0) return fromTree.Distinct().ToList();
            if (!(data?.GetData(typeof(TreeNodeAdv[])) is TreeNodeAdv[] nodes)) return new List<Table>();
            return nodes.Where(n => n != null).Select(n => n.Tag).OfType<Table>().Distinct().ToList();
        }

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            var tables = DraggedTables(e.Data);
            var any = !layout.IsAllTables && tables.Any(t => !layout.Includes(t.Name, t.LineageTag));
            e.Effect = any ? DragDropEffects.Copy : DragDropEffects.None;
            if (layout.IsAllTables && tables.Count > 0)
                status.Text = "\"All tables\" already shows every table. Drop on a custom layout.";
            else if (tables.Count > 0 && !any)
                status.Text = tables.Count == 1 ? $"{tables[0].Name} is already in this layout." : "These tables are already in this layout.";
        }

        private void Canvas_DragDrop(object sender, DragEventArgs e)
        {
            var tables = DraggedTables(e.Data);
            var world = canvas.ToWorld(canvas.PointToClient(new Point(e.X, e.Y)));
            var added = AddTables(tables, world);
            UpdateStatus(added == 0 ? null : $"Added {added} table{(added == 1 ? "" : "s")}");
        }

        #endregion

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
            var previous = canvas.Graph.Nodes.ToDictionary(n => n.Table);
            var graph = DiagramGraph.Build(model, t => layout.Includes(t.Name, t.LineageTag));

            var unplaced = new List<DiagramNode>();
            foreach (var node in graph.Nodes)
            {
                var saved = layout.Find(node.Table.Name, node.Table.LineageTag);
                if (previous.TryGetValue(node.Table, out var before))
                {
                    node.Location = before.Location;
                    node.Collapsed = before.Collapsed;
                    node.HasCustomSize = before.HasCustomSize;
                    node.CustomHeight = before.CustomHeight;
                    node.Size = new SizeF(before.Size.Width, 0);
                }
                else if (saved != null)
                {
                    node.Collapsed = saved.Collapsed;
                    node.HasCustomSize = saved.Width > 0 || saved.Height > 0;
                    node.CustomHeight = saved.Height;
                    node.Size = new SizeF(saved.Width > 0 ? saved.Width : CardMetrics.Width, 0);
                    if (saved.IsPlaced) node.Location = new PointF(saved.X, saved.Y);
                    else unplaced.Add(node);
                }
                else unplaced.Add(node);
                node.FitHeight();
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
            canvas.AllowRelationshipEditing = CanCreate;
            canvas.EmptyText = model == null ? NoModelText : layout.IsAllTables ? "This model has no tables." : EmptyLayoutText;
            canvas.Graph = graph;
            canvas.SetSelection(CurrentTreeSelection(), selectTables: false);
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
            canvas.SetSelection(CurrentTreeSelection(), selectTables: true);
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
            canvas.SetSelection(selected.Contains(obj) ? selected : new List<ITabularNamedObject> { obj }, selectTables: false);
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

        private static DiagramNodeLayout SaveNode(DiagramNode n) => new DiagramNodeLayout
        {
            Table = n.Table.Name,
            LineageTag = string.IsNullOrEmpty(n.Table.LineageTag) ? null : n.Table.LineageTag,
            X = n.Location.X,
            Y = n.Location.Y,
            Width = n.HasCustomSize ? n.Size.Width : 0,
            Height = n.HasCustomSize ? n.CustomHeight : 0,
            Collapsed = n.Collapsed,
        };

        /// <summary>
        /// Copies the canvas state into the current layout. Entries for tables that are not in
        /// the model right now are kept: a table removed by mistake and restored with undo comes
        /// back where it was, and stays in the custom layouts it belonged to.
        /// </summary>
        private void CaptureNodes()
        {
            var graph = canvas.Graph;
            if (handler?.Model == null) return;
            var shown = new HashSet<Table>(graph.Nodes.Select(n => n.Table));
            var kept = layout.Nodes.Where(saved => !handler.Model.Tables.Any(t =>
                    (!string.IsNullOrEmpty(saved.LineageTag) && saved.LineageTag == t.LineageTag) ||
                    string.Equals(saved.Table, t.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            // Tables in the model but not yet drawn (a rebuild is pending) keep their saved entry.
            var pending = layout.Nodes.Where(saved => !kept.Contains(saved) && !graph.Nodes.Any(n =>
                    (!string.IsNullOrEmpty(saved.LineageTag) && saved.LineageTag == n.Table.LineageTag) ||
                    string.Equals(saved.Table, n.Table.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            layout.Nodes = kept.Concat(pending).Concat(graph.Nodes.Select(SaveNode)).ToList();
        }

        /// <summary>
        /// Writes all layouts. The model key is worked out at save time, so a model saved under a
        /// new path keeps its layouts from then on.
        /// </summary>
        private void SaveLayout()
        {
            saveTimer.Stop();
            if (handler == null) return;
            var key = DiagramLayoutStore.GetModelKey(handler);
            if (key == null) return;

            if (!rebuildPending) CaptureNodes();
            layout.Zoom = canvas.Zoom;
            layout.ScrollX = canvas.Scroll.X;
            layout.ScrollY = canvas.Scroll.Y;
            layoutFile.SelectedLayout = layout.Name;
            DiagramLayoutStore.Save(key, layoutFile);
        }

        #endregion

        #region Status

        private void UpdateZoomLabel() => zoomLabel.Text = Math.Round(canvas.Zoom * 100) + "%";

        private string StatusText()
        {
            var graph = canvas.Graph;
            return graph.Nodes.Count == 0 ? "" : $"{graph.Nodes.Count} tables, {graph.Edges.Count} relationships";
        }

        private void UpdateStatus(string message = null) => status.Text = message ?? StatusText();

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
