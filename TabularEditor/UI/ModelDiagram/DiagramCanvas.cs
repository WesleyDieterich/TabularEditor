using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// Draws the model diagram and handles panning, zooming, selecting, moving, resizing and
    /// collapsing tables. Everything is drawn in diagram (world) coordinates through a transform:
    /// screen = (world - Scroll) * Zoom.
    /// </summary>
    internal sealed class DiagramCanvas : Control
    {
        internal const float MinZoom = 0.1f;
        internal const float MaxZoom = 3f;
        private const float EdgeHitTolerance = 5f;
        private const float ParallelEdgeSpacing = 10f;
        private const float ResizeGrip = 12f;
        private const float ChevronWidth = 24f;
        private const int DragThreshold = 4;

        private DiagramGraph graph = DiagramGraph.Empty;
        /// <summary>Sideways offset of each edge, so several relationships between the same two tables stay apart.</summary>
        private Dictionary<DiagramEdge, float> edgeOffsets = new Dictionary<DiagramEdge, float>();
        /// <summary>Objects selected in the model tree: drawn highlighted.</summary>
        private HashSet<ITabularNamedObject> selection = new HashSet<ITabularNamedObject>();
        /// <summary>Tables selected in the diagram: moved together, targets of the context menu.</summary>
        private HashSet<DiagramNode> selectedNodes = new HashSet<DiagramNode>();
        private HashSet<DiagramNode> searchMatches = new HashSet<DiagramNode>();
        private float zoom = 1f;
        private PointF scroll;

        private enum DragMode { None, Pending, Pan, MoveNodes, Resize, Marquee }
        private DragMode drag;
        private Point dragStart;
        private PointF dragStartScroll;
        private PointF dragStartWorld;
        private Dictionary<DiagramNode, PointF> dragStartLocations;
        private SizeF dragStartSize;
        private DiagramNode dragNode;
        private RectangleF marquee;
        private bool marqueeAdds;
        private Hit pressed;
        private Hit hover;

        public DiagramCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AllowDrop = true;
            Font = new Font("Segoe UI", 9f);
        }

        /// <summary>A table, field or relationship was clicked (without Ctrl).</summary>
        public event EventHandler<ITabularNamedObject> ObjectClicked;
        /// <summary>Tables were moved, resized or collapsed. Raised once, when the gesture ends.</summary>
        public event EventHandler LayoutChanged;
        /// <summary>Zoom or scroll position changed.</summary>
        public event EventHandler ViewChanged;
        /// <summary>The relationship or table under the mouse changed (null when over nothing).</summary>
        public event EventHandler<ITabularNamedObject> HoverChanged;
        /// <summary>Right-click: the node under the mouse (null on empty space) and the screen position.</summary>
        public event EventHandler<(DiagramNode Node, Point Location)> ContextMenuRequested;

        public DiagramGraph Graph
        {
            get => graph;
            set
            {
                graph = value ?? DiagramGraph.Empty;
                edgeOffsets = ComputeEdgeOffsets(graph);
                hover = null;
                pressed = null;
                dragNode = null;
                drag = DragMode.None;
                var tables = new HashSet<Table>(selectedNodes.Select(n => n.Table));
                selectedNodes = new HashSet<DiagramNode>(graph.Nodes.Where(n => tables.Contains(n.Table)));
                searchMatches.RemoveWhere(n => !graph.Nodes.Contains(n));
                Invalidate();
            }
        }

        public IReadOnlyCollection<DiagramNode> SelectedNodes => selectedNodes;

        public float Zoom
        {
            get => zoom;
            set => SetZoom(value, new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f));
        }

        public PointF Scroll
        {
            get => scroll;
            set
            {
                scroll = value;
                Invalidate();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Mirrors the model tree's selection: highlights those objects and selects their tables.</summary>
        public void SetSelection(IEnumerable<ITabularNamedObject> objects, bool selectTables)
        {
            selection = new HashSet<ITabularNamedObject>(objects ?? Enumerable.Empty<ITabularNamedObject>());
            if (selectTables)
                selectedNodes = new HashSet<DiagramNode>(graph.Nodes.Where(n => selection.Contains(n.Table)));
            Invalidate();
        }

        public void SelectNodes(IEnumerable<DiagramNode> nodes)
        {
            selectedNodes = new HashSet<DiagramNode>(nodes);
            Invalidate();
        }

        public void SetSearchMatches(IEnumerable<DiagramNode> nodes)
        {
            searchMatches = new HashSet<DiagramNode>(nodes ?? Enumerable.Empty<DiagramNode>());
            Invalidate();
        }

        #region Coordinates

        public PointF ToWorld(PointF screen) => new PointF(screen.X / zoom + scroll.X, screen.Y / zoom + scroll.Y);
        private PointF ToScreen(PointF world) => new PointF((world.X - scroll.X) * zoom, (world.Y - scroll.Y) * zoom);

        private void SetZoom(float value, PointF anchorScreen)
        {
            value = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            if (Math.Abs(value - zoom) < 0.0001f) return;
            // Keep the world point under the anchor in place.
            var anchorWorld = ToWorld(anchorScreen);
            zoom = value;
            scroll = new PointF(anchorWorld.X - anchorScreen.X / zoom, anchorWorld.Y - anchorScreen.Y / zoom);
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ZoomBy(float factor) => SetZoom(zoom * factor, new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f));

        /// <summary>Zooms and scrolls so that every table is visible.</summary>
        public void FitToWindow()
        {
            if (graph.Nodes.Count == 0 || ClientSize.Width < 10 || ClientSize.Height < 10) return;
            var bounds = graph.Nodes.Select(n => n.Bounds).Aggregate(RectangleF.Union);
            bounds.Inflate(40, 40);
            zoom = Math.Max(MinZoom, Math.Min(1f, Math.Min(ClientSize.Width / bounds.Width, ClientSize.Height / bounds.Height)));
            scroll = new PointF(
                bounds.X - (ClientSize.Width / zoom - bounds.Width) / 2,
                bounds.Y - (ClientSize.Height / zoom - bounds.Height) / 2);
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Scrolls a table into the middle of the view without changing the zoom.</summary>
        public void CenterOn(DiagramNode node)
        {
            var c = new PointF(node.Location.X + node.Size.Width / 2, node.Location.Y + node.Size.Height / 2);
            Scroll = new PointF(c.X - ClientSize.Width / 2f / zoom, c.Y - ClientSize.Height / 2f / zoom);
        }

        #endregion

        #region Hit testing

        private enum HitKind { Header, Field, More, Chevron, ResizeGrip, Edge }

        private sealed class Hit
        {
            public HitKind Kind;
            public DiagramNode Node;
            public DiagramField Field;
            public DiagramEdge Edge;

            public ITabularNamedObject Object =>
                Kind == HitKind.Edge ? Edge.Relationship : Kind == HitKind.Field ? Field.Object : (ITabularNamedObject)Node.Table;
        }

        private Hit HitTest(Point screen)
        {
            var world = ToWorld(screen);
            // Topmost card first: cards are drawn in list order, so the last one is on top.
            for (var i = graph.Nodes.Count - 1; i >= 0; i--)
            {
                var node = graph.Nodes[i];
                var bounds = node.Bounds;
                if (!bounds.Contains(world)) continue;

                if (world.X >= bounds.Right - ResizeGrip && world.Y >= bounds.Bottom - ResizeGrip)
                    return new Hit { Kind = HitKind.ResizeGrip, Node = node };
                if (world.Y < bounds.Y + CardMetrics.HeaderHeight)
                    return new Hit { Kind = world.X >= bounds.Right - ChevronWidth ? HitKind.Chevron : HitKind.Header, Node = node };

                var row = (int)Math.Floor((world.Y - bounds.Y - CardMetrics.HeaderHeight) / CardMetrics.RowHeight);
                var visible = node.VisibleFieldRows(out var hasMore);
                if (row >= 0 && row < visible) return new Hit { Kind = HitKind.Field, Node = node, Field = node.DisplayFields[row] };
                return new Hit { Kind = hasMore && row == visible ? HitKind.More : HitKind.Header, Node = node };
            }

            DiagramEdge best = null;
            var bestDistance = EdgeHitTolerance;
            foreach (var edge in graph.Edges)
            {
                if (!TryGetEdgeLine(edge, out var a, out var b)) continue;
                var d = DistanceToSegment(screen, ToScreen(a), ToScreen(b));
                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = edge;
                }
            }
            return best == null ? null : new Hit { Kind = HitKind.Edge, Edge = best };
        }

        private static float DistanceToSegment(PointF p, PointF a, PointF b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared < 0.0001f ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared));
            var x = a.X + t * dx - p.X;
            var y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        /// <summary>
        /// Relationships between the same pair of tables (an active one and several inactive
        /// role-playing ones, typically) are spread side by side, centred on the direct line.
        /// </summary>
        private static Dictionary<DiagramEdge, float> ComputeEdgeOffsets(DiagramGraph graph)
        {
            var offsets = new Dictionary<DiagramEdge, float>();
            var pairs = graph.Edges.GroupBy(e =>
            {
                var a = e.From.Table.Name;
                var b = e.To.Table.Name;
                return string.CompareOrdinal(a, b) <= 0 ? a + "\u0001" + b : b + "\u0001" + a;
            });
            foreach (var pair in pairs)
            {
                // Active first, so the active relationship sits nearest the middle.
                var edges = pair.OrderBy(e => e.Relationship.IsActive ? 0 : 1).ThenBy(e => e.Relationship.Name, StringComparer.Ordinal).ToList();
                for (var i = 0; i < edges.Count; i++)
                {
                    // 0, +1, -1, +2, -2 ... steps from the centre line.
                    var step = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
                    // Measure the offset in one fixed direction for the pair, whichever end is "From".
                    var flip = string.CompareOrdinal(edges[i].From.Table.Name, edges[i].To.Table.Name) > 0 ? -1 : 1;
                    offsets[edges[i]] = step * ParallelEdgeSpacing * flip;
                }
            }
            return offsets;
        }

        /// <summary>
        /// The visible part of an edge: the segment between the two card centres, shifted sideways
        /// by the edge's offset and clipped to the card borders. False for a self-relationship or
        /// overlapping cards.
        /// </summary>
        private bool TryGetEdgeLine(DiagramEdge edge, out PointF a, out PointF b)
        {
            var from = edge.From.Bounds;
            var to = edge.To.Bounds;
            var cFrom = new PointF(from.X + from.Width / 2, from.Y + from.Height / 2);
            var cTo = new PointF(to.X + to.Width / 2, to.Y + to.Height / 2);

            if (edgeOffsets.TryGetValue(edge, out var offset) && offset != 0)
            {
                var dx = cTo.X - cFrom.X;
                var dy = cTo.Y - cFrom.Y;
                var length = (float)Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.001f)
                {
                    var nx = -dy / length * offset;
                    var ny = dx / length * offset;
                    cFrom = new PointF(cFrom.X + nx, cFrom.Y + ny);
                    cTo = new PointF(cTo.X + nx, cTo.Y + ny);
                }
            }

            a = ClipToBorder(from, cFrom, cTo);
            b = ClipToBorder(to, cTo, cFrom);
            return edge.From != edge.To && !from.IntersectsWith(to);
        }

        /// <summary>Where the ray from the rectangle's centre towards target leaves the rectangle.</summary>
        private static PointF ClipToBorder(RectangleF r, PointF centre, PointF target)
        {
            var dx = target.X - centre.X;
            var dy = target.Y - centre.Y;
            if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f) return centre;
            var sx = Math.Abs(dx) < 0.001f ? float.MaxValue : (r.Width / 2) / Math.Abs(dx);
            var sy = Math.Abs(dy) < 0.001f ? float.MaxValue : (r.Height / 2) / Math.Abs(dy);
            var s = Math.Min(sx, sy);
            return new PointF(centre.X + dx * s, centre.Y + dy * s);
        }

        #endregion

        #region Mouse and keyboard

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            dragStart = e.Location;
            dragStartScroll = scroll;
            dragStartWorld = ToWorld(e.Location);

            if (e.Button == MouseButtons.Middle)
            {
                drag = DragMode.Pan;
                Cursor = Cursors.SizeAll;
                return;
            }

            pressed = HitTest(e.Location);
            dragNode = pressed?.Node;

            if (e.Button == MouseButtons.Right)
            {
                // Right-clicking a table outside the selection selects just that table, as in Explorer.
                if (dragNode != null && !selectedNodes.Contains(dragNode)) SelectNodes(new[] { dragNode });
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (pressed?.Kind == HitKind.ResizeGrip)
            {
                drag = DragMode.Resize;
                dragStartSize = dragNode.Size;
                return;
            }
            if (pressed == null && (ModifierKeys & Keys.Shift) != 0)
            {
                drag = DragMode.Marquee;
                marqueeAdds = (ModifierKeys & Keys.Control) != 0;
                marquee = RectangleF.Empty;
                return;
            }
            drag = DragMode.Pending;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var world = ToWorld(e.Location);

            if (drag == DragMode.Pending)
            {
                if (Math.Abs(e.X - dragStart.X) < DragThreshold && Math.Abs(e.Y - dragStart.Y) < DragThreshold) return;
                if (dragNode != null)
                {
                    // Dragging a table outside the selection moves just that table.
                    if (!selectedNodes.Contains(dragNode)) selectedNodes = new HashSet<DiagramNode> { dragNode };
                    foreach (var n in graph.Nodes.Where(selectedNodes.Contains).ToList()) graph.BringToFront(n);
                    dragStartLocations = selectedNodes.ToDictionary(n => n, n => n.Location);
                    drag = DragMode.MoveNodes;
                }
                else drag = DragMode.Pan;
                Cursor = Cursors.SizeAll;
            }

            switch (drag)
            {
                case DragMode.MoveNodes:
                    var dx = world.X - dragStartWorld.X;
                    var dy = world.Y - dragStartWorld.Y;
                    foreach (var kv in dragStartLocations) kv.Key.Location = new PointF(kv.Value.X + dx, kv.Value.Y + dy);
                    Invalidate();
                    return;

                case DragMode.Pan:
                    scroll = new PointF(
                        dragStartScroll.X - (e.X - dragStart.X) / zoom,
                        dragStartScroll.Y - (e.Y - dragStart.Y) / zoom);
                    Invalidate();
                    return;

                case DragMode.Resize:
                    var width = Math.Max(CardMetrics.MinWidth, dragStartSize.Width + world.X - dragStartWorld.X);
                    var height = dragNode.Collapsed
                        ? dragNode.Size.Height
                        : Math.Max(CardMetrics.MinHeight, dragStartSize.Height + world.Y - dragStartWorld.Y);
                    dragNode.Size = new SizeF(width, height);
                    dragNode.HasCustomSize = true;
                    if (!dragNode.Collapsed) dragNode.CustomHeight = height;
                    Invalidate();
                    return;

                case DragMode.Marquee:
                    marquee = RectangleF.FromLTRB(
                        Math.Min(dragStartWorld.X, world.X), Math.Min(dragStartWorld.Y, world.Y),
                        Math.Max(dragStartWorld.X, world.X), Math.Max(dragStartWorld.Y, world.Y));
                    Invalidate();
                    return;
            }

            UpdateHover(HitTest(e.Location));
        }

        private void UpdateHover(Hit hit)
        {
            Cursor = hit?.Kind == HitKind.Edge || hit?.Kind == HitKind.Chevron ? Cursors.Hand
                : hit?.Kind == HitKind.ResizeGrip ? Cursors.SizeNWSE : Cursors.Default;
            // Hovering is reported per table or relationship, not per field.
            var before = hover == null ? null : hover.Kind == HitKind.Edge ? (object)hover.Edge : hover.Node;
            var after = hit == null ? null : hit.Kind == HitKind.Edge ? (object)hit.Edge : hit.Node;
            hover = hit;
            if (before == after) return;
            HoverChanged?.Invoke(this, hit?.Kind == HitKind.Edge ? hit.Edge.Relationship : (ITabularNamedObject)hit?.Node?.Table);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var mode = drag;
            drag = DragMode.None;
            Cursor = Cursors.Default;

            if (e.Button == MouseButtons.Right && mode == DragMode.None)
            {
                ContextMenuRequested?.Invoke(this, (dragNode, e.Location));
            }
            else if (mode == DragMode.Pending && e.Button == MouseButtons.Left)
            {
                HandleClick(pressed);
            }
            else if (mode == DragMode.MoveNodes || mode == DragMode.Resize)
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (mode == DragMode.Marquee)
            {
                var inside = graph.Nodes.Where(n => marquee.IntersectsWith(n.Bounds));
                SelectNodes(marqueeAdds ? selectedNodes.Concat(inside) : inside);
                marquee = RectangleF.Empty;
            }
            else if (mode == DragMode.Pan) ViewChanged?.Invoke(this, EventArgs.Empty);

            pressed = null;
            dragNode = null;
            dragStartLocations = null;
            Invalidate();
        }

        private void HandleClick(Hit hit)
        {
            var ctrl = (ModifierKeys & Keys.Control) != 0;
            if (hit == null)
            {
                if (!ctrl) SelectNodes(Enumerable.Empty<DiagramNode>());
                return;
            }
            if (hit.Kind == HitKind.Chevron)
            {
                SetCollapsed(selectedNodes.Contains(hit.Node) ? selectedNodes : new HashSet<DiagramNode> { hit.Node }, !hit.Node.Collapsed);
                return;
            }
            if (hit.Kind == HitKind.Edge)
            {
                if (!ctrl) SelectNodes(Enumerable.Empty<DiagramNode>());
                ObjectClicked?.Invoke(this, hit.Edge.Relationship);
                return;
            }
            if (ctrl)
            {
                // Ctrl+click toggles the table in the diagram selection only.
                var nodes = new HashSet<DiagramNode>(selectedNodes);
                if (!nodes.Remove(hit.Node)) nodes.Add(hit.Node);
                SelectNodes(nodes);
                return;
            }
            SelectNodes(new[] { hit.Node });
            ObjectClicked?.Invoke(this, hit.Kind == HitKind.Field ? hit.Field.Object : hit.Node.Table);
        }

        public void SetCollapsed(IEnumerable<DiagramNode> nodes, bool collapsed)
        {
            foreach (var node in nodes.ToList())
            {
                node.Collapsed = collapsed;
                node.FitHeight();
            }
            Invalidate();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Returns cards to the size that fits their fields.</summary>
        public void ResetSize(IEnumerable<DiagramNode> nodes)
        {
            foreach (var node in nodes.ToList())
            {
                node.HasCustomSize = false;
                node.CustomHeight = 0;
                node.Size = new SizeF(CardMetrics.Width, node.AutoHeight);
            }
            Invalidate();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var hit = HitTest(e.Location);
            // Double-clicking "+ N more" grows the card to show every field.
            if (hit?.Kind == HitKind.More)
            {
                var node = hit.Node;
                node.HasCustomSize = true;
                node.CustomHeight = CardMetrics.HeaderHeight + node.DisplayFields.Count * CardMetrics.RowHeight + CardMetrics.Padding;
                node.FitHeight();
                Invalidate();
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover == null) return;
            hover = null;
            HoverChanged?.Invoke(this, null);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) != 0)
            {
                SetZoom(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), e.Location);
                return;
            }
            var step = e.Delta / 120f * 60f / zoom;
            Scroll = (ModifierKeys & Keys.Shift) != 0
                ? new PointF(scroll.X - step, scroll.Y)
                : new PointF(scroll.X, scroll.Y - step);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var step = 60f / zoom;
            switch (e.KeyCode)
            {
                case Keys.Left: Scroll = new PointF(scroll.X - step, scroll.Y); break;
                case Keys.Right: Scroll = new PointF(scroll.X + step, scroll.Y); break;
                case Keys.Up: Scroll = new PointF(scroll.X, scroll.Y - step); break;
                case Keys.Down: Scroll = new PointF(scroll.X, scroll.Y + step); break;
                case Keys.Add: case Keys.Oemplus: if (e.Control) ZoomBy(1.25f); else return; break;
                case Keys.Subtract: case Keys.OemMinus: if (e.Control) ZoomBy(0.8f); else return; break;
                case Keys.D0: case Keys.NumPad0: if (e.Control) FitToWindow(); else return; break;
                case Keys.A: if (e.Control) SelectNodes(graph.Nodes); else return; break;
                case Keys.Escape: SelectNodes(Enumerable.Empty<DiagramNode>()); break;
                default: return;
            }
            e.Handled = true;
        }

        #endregion

        #region Painting

        private sealed class Palette
        {
            public Color Background, Grid, Card, CardBorder, Header, HeaderCalculated, HeaderCalcGroup,
                Text, Muted, Accent, Line, Search, SelectionFill;

            public static Palette Current => AppTheme.IsDark ? Dark : Light;

            private static readonly Palette Dark = new Palette
            {
                Background = AppTheme.Window,
                Grid = Color.FromArgb(36, 41, 48),
                Card = AppTheme.Surface,
                CardBorder = AppTheme.Border,
                Header = AppTheme.Raised,
                HeaderCalculated = Color.FromArgb(52, 64, 58),
                HeaderCalcGroup = Color.FromArgb(62, 55, 72),
                Text = AppTheme.Text,
                Muted = AppTheme.Muted,
                Accent = AppTheme.Accent,
                Line = Color.FromArgb(120, 134, 150),
                Search = Color.FromArgb(230, 190, 90),
                SelectionFill = AppTheme.Selection,
            };

            private static readonly Palette Light = new Palette
            {
                Background = Color.FromArgb(243, 245, 248),
                Grid = Color.FromArgb(230, 233, 238),
                Card = Color.White,
                CardBorder = Color.FromArgb(196, 202, 210),
                Header = Color.FromArgb(232, 236, 242),
                HeaderCalculated = Color.FromArgb(222, 238, 228),
                HeaderCalcGroup = Color.FromArgb(234, 226, 244),
                Text = Color.FromArgb(32, 36, 42),
                Muted = Color.FromArgb(118, 126, 138),
                Accent = Color.FromArgb(0, 103, 192),
                Line = Color.FromArgb(128, 138, 152),
                Search = Color.FromArgb(214, 150, 0),
                SelectionFill = Color.FromArgb(214, 230, 248),
            };
        }

        /// <summary>Shown on an empty diagram, e.g. a new custom layout.</summary>
        public string EmptyText { get; set; } = "No tables to show. Open or connect to a model.";

        protected override void OnPaint(PaintEventArgs e)
        {
            var palette = Palette.Current;
            var g = e.Graphics;
            g.Clear(palette.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.ScaleTransform(zoom, zoom);
            g.TranslateTransform(-scroll.X, -scroll.Y);
            var visible = new RectangleF(scroll.X, scroll.Y, ClientSize.Width / zoom, ClientSize.Height / zoom);

            DrawGrid(g, visible, palette);
            var detailed = zoom >= 0.45f;

            using (var header = new Font(Font, FontStyle.Bold))
            using (var italic = new Font(Font, FontStyle.Italic))
            {
                foreach (var edge in graph.Edges) DrawEdge(g, edge, palette, header, detailed);
                foreach (var node in graph.Nodes)
                {
                    if (!node.Bounds.IntersectsWith(visible)) continue;
                    DrawNode(g, node, palette, header, italic, detailed);
                }
            }

            if (drag == DragMode.Marquee && !marquee.IsEmpty)
            {
                using (var fill = new SolidBrush(Color.FromArgb(40, palette.Accent)))
                using (var pen = new Pen(palette.Accent, 1f / zoom) { DashPattern = new[] { 4f, 3f } })
                {
                    g.FillRectangle(fill, marquee);
                    g.DrawRectangle(pen, marquee.X, marquee.Y, marquee.Width, marquee.Height);
                }
            }

            if (graph.Nodes.Count == 0)
            {
                g.ResetTransform();
                TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, palette.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }

        private void DrawGrid(Graphics g, RectangleF visible, Palette palette)
        {
            if (zoom < 0.35f) return;
            const float spacing = 40f;
            using (var pen = new Pen(palette.Grid, 1f / zoom))
            {
                for (var x = (float)Math.Floor(visible.Left / spacing) * spacing; x < visible.Right; x += spacing)
                    g.DrawLine(pen, x, visible.Top, x, visible.Bottom);
                for (var y = (float)Math.Floor(visible.Top / spacing) * spacing; y < visible.Bottom; y += spacing)
                    g.DrawLine(pen, visible.Left, y, visible.Right, y);
            }
        }

        private bool IsHighlighted(DiagramEdge edge) =>
            selection.Contains(edge.Relationship) || (hover?.Kind == HitKind.Edge && hover.Edge == edge);

        private bool IsSelected(DiagramNode node) => selectedNodes.Contains(node) || selection.Contains(node.Table);

        private void DrawEdge(Graphics g, DiagramEdge edge, Palette palette, Font labelFont, bool detailed)
        {
            if (!TryGetEdgeLine(edge, out var a, out var b)) return;
            var relationship = edge.Relationship;
            var highlighted = IsHighlighted(edge);
            var related = IsSelected(edge.From) || IsSelected(edge.To);
            var color = highlighted || related ? palette.Accent : palette.Line;

            using (var pen = new Pen(color, (highlighted ? 2.6f : 1.4f) / Math.Max(zoom, 0.5f)))
            {
                if (!relationship.IsActive) pen.DashPattern = new[] { 5f, 4f };
                g.DrawLine(pen, a, b);
            }

            var length = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (length < 1) return;
            var ux = (b.X - a.X) / length;
            var uy = (b.Y - a.Y) / length;

            // Filter direction: from the "one" side (To) towards the "many" side (From).
            var mid = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            using (var brush = new SolidBrush(color))
            {
                if (relationship.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections)
                {
                    DrawArrow(g, brush, new PointF(mid.X - ux * 8, mid.Y - uy * 8), -ux, -uy);
                    DrawArrow(g, brush, new PointF(mid.X + ux * 8, mid.Y + uy * 8), ux, uy);
                }
                else DrawArrow(g, brush, mid, -ux, -uy);

                if (!detailed) return;
                DrawCardinality(g, labelFont, brush, relationship.FromCardinality, a, ux, uy);
                DrawCardinality(g, labelFont, brush, relationship.ToCardinality, b, -ux, -uy);
            }
        }

        private static void DrawArrow(Graphics g, Brush brush, PointF tip, float ux, float uy)
        {
            const float length = 9f, width = 5f;
            var baseX = tip.X - ux * length;
            var baseY = tip.Y - uy * length;
            g.FillPolygon(brush, new[]
            {
                tip,
                new PointF(baseX - uy * width, baseY + ux * width),
                new PointF(baseX + uy * width, baseY - ux * width),
            });
        }

        /// <summary>Draws "1" or "*" just outside the card, beside the line.</summary>
        private static void DrawCardinality(Graphics g, Font font, Brush brush, RelationshipEndCardinality cardinality,
            PointF end, float ux, float uy)
        {
            var text = cardinality == RelationshipEndCardinality.Many ? "*" : cardinality == RelationshipEndCardinality.One ? "1" : null;
            if (text == null) return;
            var p = new PointF(end.X + ux * 13 - uy * 10, end.Y + uy * 13 + ux * 10);
            // An asterisk is a small, raised glyph; draw it larger and lower so it reads like the "1".
            using (var star = new Font(font.FontFamily, font.Size * 1.6f, FontStyle.Bold))
            {
                var f = text == "*" ? star : font;
                var size = g.MeasureString(text, f);
                var lift = text == "*" ? -size.Height * 0.18f : 0;
                g.DrawString(text, f, brush, p.X - size.Width / 2, p.Y - size.Height / 2 - lift);
            }
        }

        private void DrawNode(Graphics g, DiagramNode node, Palette palette, Font headerFont, Font italic, bool detailed)
        {
            var table = node.Table;
            var bounds = node.Bounds;
            var selected = IsSelected(node);
            var hovered = hover != null && hover.Kind != HitKind.Edge && hover.Node == node;
            var border = selected ? palette.Accent : searchMatches.Contains(node) ? palette.Search : palette.CardBorder;
            var borderWidth = (selected || searchMatches.Contains(node) ? 2.2f : hovered ? 1.6f : 1f) / Math.Max(zoom, 0.5f);

            using (var path = RoundedRect(bounds, 6))
            using (var fill = new SolidBrush(palette.Card))
                g.FillPath(fill, path);

            var headerRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, CardMetrics.HeaderHeight);
            var headerColor = table is CalculationGroupTable ? palette.HeaderCalcGroup
                : table is CalculatedTable ? palette.HeaderCalculated : palette.Header;
            using (var path = RoundedRect(headerRect, 6, bottom: node.Collapsed && node.DisplayFields.Count == 0))
            using (var fill = new SolidBrush(headerColor))
                g.FillPath(fill, path);

            using (var text = new SolidBrush(table.IsHidden ? palette.Muted : palette.Text))
            using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(table.Name, headerFont, text,
                    new RectangleF(headerRect.X + 10, headerRect.Y, headerRect.Width - 10 - ChevronWidth, headerRect.Height), format);
                DrawChevron(g, new PointF(headerRect.Right - ChevronWidth / 2, headerRect.Y + headerRect.Height / 2), node.Collapsed,
                    hovered ? palette.Text : palette.Muted);

                if (detailed)
                {
                    var visibleRows = node.VisibleFieldRows(out var hasMore);
                    var fields = node.DisplayFields;
                    for (var i = 0; i < visibleRows; i++)
                        DrawField(g, fields[i],
                            new RectangleF(bounds.X, bounds.Y + CardMetrics.HeaderHeight + i * CardMetrics.RowHeight, bounds.Width, CardMetrics.RowHeight),
                            palette, italic, format);

                    if (hasMore)
                    {
                        var more = new RectangleF(bounds.X + 28, bounds.Y + CardMetrics.HeaderHeight + visibleRows * CardMetrics.RowHeight,
                            bounds.Width - 36, CardMetrics.RowHeight);
                        using (var muted = new SolidBrush(palette.Muted))
                            g.DrawString($"+ {fields.Count - visibleRows} more", italic, muted, more, format);
                    }
                }
            }

            if (hovered && !node.Collapsed) DrawResizeGrip(g, bounds, palette.Muted);

            using (var path = RoundedRect(bounds, 6))
            using (var pen = new Pen(border, borderWidth))
                g.DrawPath(pen, path);
        }

        private static void DrawChevron(Graphics g, PointF c, bool collapsed, Color color)
        {
            using (var pen = new Pen(color, 1.4f))
            {
                // ▾ when expanded, ▸ when collapsed.
                if (collapsed) g.DrawLines(pen, new[] { new PointF(c.X - 2, c.Y - 4), new PointF(c.X + 2, c.Y), new PointF(c.X - 2, c.Y + 4) });
                else g.DrawLines(pen, new[] { new PointF(c.X - 4, c.Y - 2), new PointF(c.X, c.Y + 2), new PointF(c.X + 4, c.Y - 2) });
            }
        }

        private static void DrawResizeGrip(Graphics g, RectangleF bounds, Color color)
        {
            using (var pen = new Pen(color, 1f))
                for (var i = 1; i <= 3; i++)
                    g.DrawLine(pen, bounds.Right - 3 * i - 2, bounds.Bottom - 3, bounds.Right - 3, bounds.Bottom - 3 * i - 2);
        }

        private void DrawField(Graphics g, DiagramField field, RectangleF row, Palette palette, Font italic, StringFormat format)
        {
            if (selection.Contains(field.Object) || IsEndOfHighlightedEdge(field))
                using (var fill = new SolidBrush(palette.SelectionFill))
                    g.FillRectangle(fill, row.X + 1, row.Y, row.Width - 2, row.Height);

            var color = field.IsHidden ? palette.Muted : palette.Text;
            DrawFieldGlyph(g, field, new PointF(row.X + 14, row.Y + row.Height / 2), field.IsKey ? palette.Accent : palette.Muted);
            using (var brush = new SolidBrush(color))
                g.DrawString(field.Name, field.IsHidden ? italic : Font, brush,
                    new RectangleF(row.X + 28, row.Y, row.Width - 36, row.Height), format);
        }

        private bool IsEndOfHighlightedEdge(DiagramField field)
        {
            if (!(field.Object is Column column)) return false;
            foreach (var edge in graph.Edges)
                if (IsHighlighted(edge) && (edge.Relationship.FromColumn == column || edge.Relationship.ToColumn == column))
                    return true;
            return false;
        }

        /// <summary>A small shape per field kind: dot for keys, square for columns, ∑-like bars for measures.</summary>
        private static void DrawFieldGlyph(Graphics g, DiagramField field, PointF c, Color color)
        {
            using (var pen = new Pen(color, 1.2f))
            using (var brush = new SolidBrush(color))
            {
                switch (field.Kind)
                {
                    case DiagramFieldKind.Column when field.IsKey:
                        g.FillEllipse(brush, c.X - 3.5f, c.Y - 3.5f, 7, 7);
                        break;
                    case DiagramFieldKind.Column:
                        g.DrawRectangle(pen, c.X - 3.5f, c.Y - 3.5f, 7, 7);
                        break;
                    case DiagramFieldKind.Measure:
                        g.DrawLines(pen, new[]
                        {
                            new PointF(c.X + 3.5f, c.Y - 4), new PointF(c.X - 3.5f, c.Y - 4), new PointF(c.X + 0.5f, c.Y),
                            new PointF(c.X - 3.5f, c.Y + 4), new PointF(c.X + 3.5f, c.Y + 4),
                        });
                        break;
                    case DiagramFieldKind.Hierarchy:
                        for (var i = 0; i < 3; i++)
                            g.DrawLine(pen, c.X - 3.5f + i * 2, c.Y - 3.5f + i * 3.5f, c.X + 3.5f, c.Y - 3.5f + i * 3.5f);
                        break;
                }
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius, bool bottom = true)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            if (bottom)
            {
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            }
            else
            {
                path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            }
            path.CloseFigure();
            return path;
        }

        #endregion
    }
}
