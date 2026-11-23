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
    /// Draws the model diagram and handles panning, zooming, selecting and moving tables.
    /// Everything is drawn in diagram (world) coordinates through a transform:
    /// screen = (world - Scroll) * Zoom.
    /// </summary>
    internal sealed class DiagramCanvas : Control
    {
        internal const float CardWidth = 220f;
        internal const float HeaderHeight = 30f;
        internal const float RowHeight = 20f;
        internal const float CardPadding = 6f;
        internal const int MaxVisibleFields = 12;
        internal const float MinZoom = 0.1f;
        internal const float MaxZoom = 3f;
        private const float EdgeHitTolerance = 5f;
        private const int DragThreshold = 4;

        private const float ParallelEdgeSpacing = 10f;

        private DiagramGraph graph = DiagramGraph.Empty;
        /// <summary>Sideways offset of each edge, so several relationships between the same two tables stay apart.</summary>
        private Dictionary<DiagramEdge, float> edgeOffsets = new Dictionary<DiagramEdge, float>();
        private HashSet<ITabularNamedObject> selection = new HashSet<ITabularNamedObject>();
        private HashSet<DiagramNode> searchMatches = new HashSet<DiagramNode>();
        private float zoom = 1f;
        private PointF scroll;

        private enum DragMode { None, Pending, Pan, MoveNode }
        private DragMode drag;
        private Point dragStartScreen;
        private PointF dragStartScroll;
        private PointF dragNodeOffset;
        private DiagramNode dragNode;
        private object pressedTarget;
        private object hoverTarget;

        public DiagramCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Font = new Font("Segoe UI", 9f);
        }

        /// <summary>A table, field or relationship was clicked.</summary>
        public event EventHandler<ITabularNamedObject> ObjectClicked;
        /// <summary>A table was moved by the user. Raised once, when the drag ends.</summary>
        public event EventHandler NodesMoved;
        /// <summary>Zoom or scroll position changed.</summary>
        public event EventHandler ViewChanged;
        /// <summary>The relationship or table under the mouse changed (null when over nothing).</summary>
        public event EventHandler<ITabularNamedObject> HoverChanged;

        public DiagramGraph Graph
        {
            get => graph;
            set
            {
                graph = value ?? DiagramGraph.Empty;
                edgeOffsets = ComputeEdgeOffsets(graph);
                hoverTarget = null;
                pressedTarget = null;
                dragNode = null;
                drag = DragMode.None;
                searchMatches.RemoveWhere(n => !graph.Nodes.Contains(n));
                Invalidate();
            }
        }

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

        public void SetSelection(IEnumerable<ITabularNamedObject> objects)
        {
            selection = new HashSet<ITabularNamedObject>(objects ?? Enumerable.Empty<ITabularNamedObject>());
            Invalidate();
        }

        public void SetSearchMatches(IEnumerable<DiagramNode> nodes)
        {
            searchMatches = new HashSet<DiagramNode>(nodes ?? Enumerable.Empty<DiagramNode>());
            Invalidate();
        }

        /// <summary>Height of a card for the given number of fields, in world units.</summary>
        internal static float CardHeight(int fieldCount)
        {
            var rows = Math.Min(fieldCount, MaxVisibleFields) + (fieldCount > MaxVisibleFields ? 1 : 0);
            return HeaderHeight + rows * RowHeight + CardPadding;
        }

        #region Coordinates

        private PointF ToWorld(PointF screen) => new PointF(screen.X / zoom + scroll.X, screen.Y / zoom + scroll.Y);
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

        private sealed class FieldHit
        {
            public DiagramNode Node;
            public DiagramField Field;
        }

        /// <summary>Returns a DiagramNode (header or card), FieldHit, DiagramEdge, or null.</summary>
        private object HitTest(Point screen)
        {
            var world = ToWorld(screen);
            // Topmost card first: cards are drawn in list order, so the last one is on top.
            for (var i = graph.Nodes.Count - 1; i >= 0; i--)
            {
                var node = graph.Nodes[i];
                if (!node.Bounds.Contains(world)) continue;
                var row = (int)Math.Floor((world.Y - node.Location.Y - HeaderHeight) / RowHeight);
                if (row >= 0 && row < Math.Min(node.Fields.Count, MaxVisibleFields))
                    return new FieldHit { Node = node, Field = node.Fields[row] };
                return node;
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
            return best;
        }

        private static ITabularNamedObject ObjectOf(object target)
        {
            switch (target)
            {
                case FieldHit f: return f.Field.Object;
                case DiagramNode n: return n.Table;
                case DiagramEdge e: return e.Relationship;
                default: return null;
            }
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
            dragStartScreen = e.Location;
            dragStartScroll = scroll;

            if (e.Button == MouseButtons.Middle)
            {
                drag = DragMode.Pan;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            pressedTarget = HitTest(e.Location);
            dragNode = pressedTarget is FieldHit f ? f.Node : pressedTarget as DiagramNode;
            if (dragNode != null)
            {
                var world = ToWorld(e.Location);
                dragNodeOffset = new PointF(world.X - dragNode.Location.X, world.Y - dragNode.Location.Y);
            }
            drag = DragMode.Pending;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag == DragMode.Pending)
            {
                if (Math.Abs(e.X - dragStartScreen.X) < DragThreshold && Math.Abs(e.Y - dragStartScreen.Y) < DragThreshold) return;
                drag = dragNode != null ? DragMode.MoveNode : DragMode.Pan;
                if (drag == DragMode.MoveNode) graph.BringToFront(dragNode);
                Cursor = Cursors.SizeAll;
            }

            if (drag == DragMode.MoveNode)
            {
                var world = ToWorld(e.Location);
                dragNode.Location = new PointF(world.X - dragNodeOffset.X, world.Y - dragNodeOffset.Y);
                Invalidate();
                return;
            }
            if (drag == DragMode.Pan)
            {
                scroll = new PointF(
                    dragStartScroll.X - (e.X - dragStartScreen.X) / zoom,
                    dragStartScroll.Y - (e.Y - dragStartScreen.Y) / zoom);
                Invalidate();
                return;
            }

            var hit = HitTest(e.Location);
            Cursor = hit is DiagramEdge ? Cursors.Hand : Cursors.Default;
            var hoverObject = ObjectOf(hit is FieldHit fh ? fh.Node : hit);
            if (!Equals(ObjectOf(hoverTarget is FieldHit oh ? oh.Node : hoverTarget), hoverObject))
            {
                hoverTarget = hit;
                HoverChanged?.Invoke(this, hoverObject);
                Invalidate();
            }
            else hoverTarget = hit;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var mode = drag;
            drag = DragMode.None;
            Cursor = Cursors.Default;

            if (mode == DragMode.Pending && e.Button == MouseButtons.Left)
            {
                var obj = ObjectOf(pressedTarget);
                if (obj != null) ObjectClicked?.Invoke(this, obj);
            }
            else if (mode == DragMode.MoveNode) NodesMoved?.Invoke(this, EventArgs.Empty);
            else if (mode == DragMode.Pan) ViewChanged?.Invoke(this, EventArgs.Empty);
            pressedTarget = null;
            dragNode = null;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverTarget == null) return;
            hoverTarget = null;
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
                case Keys.Add: case Keys.Oemplus: if (e.Control) ZoomBy(1.25f); break;
                case Keys.Subtract: case Keys.OemMinus: if (e.Control) ZoomBy(0.8f); break;
                case Keys.D0: case Keys.NumPad0: if (e.Control) FitToWindow(); break;
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
                    var bounds = node.Bounds;
                    if (!bounds.IntersectsWith(visible)) continue;
                    DrawNode(g, node, palette, header, italic, detailed);
                }
            }

            if (graph.Nodes.Count == 0)
            {
                g.ResetTransform();
                TextRenderer.DrawText(g, "No tables to show. Open or connect to a model.", Font, ClientRectangle, palette.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            selection.Contains(edge.Relationship) || ReferenceEquals(ObjectOf(hoverTarget), edge.Relationship);

        private void DrawEdge(Graphics g, DiagramEdge edge, Palette palette, Font labelFont, bool detailed)
        {
            if (!TryGetEdgeLine(edge, out var a, out var b)) return;
            var relationship = edge.Relationship;
            var highlighted = IsHighlighted(edge);
            var related = selection.Contains(edge.From.Table) || selection.Contains(edge.To.Table);
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
            var selected = selection.Contains(table);
            var hovered = ObjectOf(hoverTarget is FieldHit fh ? fh.Node : hoverTarget) == table;
            var border = selected ? palette.Accent : searchMatches.Contains(node) ? palette.Search : palette.CardBorder;
            var borderWidth = (selected || searchMatches.Contains(node) ? 2.2f : hovered ? 1.6f : 1f) / Math.Max(zoom, 0.5f);

            using (var path = RoundedRect(bounds, 6))
            using (var fill = new SolidBrush(palette.Card))
                g.FillPath(fill, path);

            var headerRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, HeaderHeight);
            var headerColor = table is CalculationGroupTable ? palette.HeaderCalcGroup
                : table is CalculatedTable ? palette.HeaderCalculated : palette.Header;
            using (var path = RoundedRect(headerRect, 6, bottom: false))
            using (var fill = new SolidBrush(headerColor))
                g.FillPath(fill, path);

            using (var text = new SolidBrush(table.IsHidden ? palette.Muted : palette.Text))
            using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(table.Name, headerFont, text, new RectangleF(headerRect.X + 10, headerRect.Y, headerRect.Width - 20, headerRect.Height), format);

                if (detailed)
                {
                    var visibleCount = Math.Min(node.Fields.Count, MaxVisibleFields);
                    for (var i = 0; i < visibleCount; i++)
                        DrawField(g, node.Fields[i], new RectangleF(bounds.X, bounds.Y + HeaderHeight + i * RowHeight, bounds.Width, RowHeight), palette, italic, format);

                    if (node.Fields.Count > MaxVisibleFields)
                    {
                        var more = new RectangleF(bounds.X + 28, bounds.Y + HeaderHeight + visibleCount * RowHeight, bounds.Width - 36, RowHeight);
                        using (var muted = new SolidBrush(palette.Muted))
                            g.DrawString($"+ {node.Fields.Count - MaxVisibleFields} more", italic, muted, more, format);
                    }
                }
            }

            using (var path = RoundedRect(bounds, 6))
            using (var pen = new Pen(border, borderWidth))
                g.DrawPath(pen, path);
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
        private void DrawFieldGlyph(Graphics g, DiagramField field, PointF c, Color color)
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
