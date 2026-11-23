using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>
    /// Places tables that have no saved position. Deterministic: the same model always gets
    /// the same layout, so tests and users see stable results.
    ///
    /// Each connected group of tables is laid out in layers along the relationships' many-to-one
    /// direction: tables on the "many" side (facts) in the first layer, the tables they point to
    /// (dimensions) in the next, snowflaked dimensions after that. Within a layer, tables are
    /// ordered by the average position of their neighbours, which keeps lines short and reduces
    /// crossings. Tables without relationships go in a grid underneath.
    /// </summary>
    internal static class AutoLayout
    {
        internal const float HorizontalGap = 60f;
        internal const float VerticalGap = 90f;
        internal const float GroupGap = 140f;
        private const int OrderingSweeps = 4;

        /// <summary>
        /// Sets Location on every node in <paramref name="toPlace"/>. Nodes not in that set keep
        /// their location and are treated as obstacles, so new tables land in free space below them.
        /// Every node must already have its Size set.
        /// </summary>
        public static void Arrange(DiagramGraph graph, ICollection<DiagramNode> toPlace)
        {
            if (toPlace.Count == 0) return;
            var placing = new HashSet<DiagramNode>(toPlace);
            var fixedNodes = graph.Nodes.Where(n => !placing.Contains(n)).ToList();
            var origin = new PointF(0, fixedNodes.Count == 0 ? 0 : fixedNodes.Max(n => n.Bounds.Bottom) + GroupGap);

            var edges = graph.Edges.Where(e => placing.Contains(e.From) && placing.Contains(e.To) && e.From != e.To).ToList();
            var groups = ConnectedGroups(graph.Nodes.Where(placing.Contains).ToList(), edges);

            var x = origin.X;
            var rowTop = origin.Y;
            var rowHeight = 0f;
            var maxRowWidth = Math.Max(1600f, (float)Math.Sqrt(placing.Sum(n => n.Size.Width * n.Size.Height)) * 1.6f);

            foreach (var group in groups.Where(g => g.Count > 1))
            {
                var size = LayoutGroup(group, edges);
                if (x > origin.X && x + size.Width > origin.X + maxRowWidth)
                {
                    x = origin.X;
                    rowTop += rowHeight + GroupGap;
                    rowHeight = 0;
                }
                Offset(group, x, rowTop);
                x += size.Width + GroupGap;
                rowHeight = Math.Max(rowHeight, size.Height);
            }

            var singles = groups.Where(g => g.Count == 1).Select(g => g[0]).ToList();
            if (singles.Count > 0)
            {
                var top = rowHeight > 0 ? rowTop + rowHeight + GroupGap : rowTop;
                GridLayout(singles, origin.X, top, maxRowWidth);
            }
        }

        private static List<List<DiagramNode>> ConnectedGroups(List<DiagramNode> nodes, List<DiagramEdge> edges)
        {
            var neighbours = nodes.ToDictionary(n => n, n => new List<DiagramNode>());
            foreach (var e in edges)
            {
                neighbours[e.From].Add(e.To);
                neighbours[e.To].Add(e.From);
            }

            var seen = new HashSet<DiagramNode>();
            var groups = new List<List<DiagramNode>>();
            foreach (var start in nodes)
            {
                if (!seen.Add(start)) continue;
                var group = new List<DiagramNode>();
                var queue = new Queue<DiagramNode>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    group.Add(n);
                    foreach (var m in neighbours[n])
                        if (seen.Add(m)) queue.Enqueue(m);
                }
                groups.Add(group);
            }
            // Largest groups first: the main star schema ends up at the top left.
            return groups.OrderByDescending(g => g.Count).ToList();
        }

        /// <summary>Lays out one connected group with its top-left at (0,0); returns its size.</summary>
        private static SizeF LayoutGroup(List<DiagramNode> group, List<DiagramEdge> allEdges)
        {
            var members = new HashSet<DiagramNode>(group);
            var edges = allEdges.Where(e => members.Contains(e.From)).ToList();
            var layers = AssignLayers(group, edges);

            var rows = layers.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => g.Select(kv => kv.Key).OrderBy(n => n.Table.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
                .ToList();
            OrderRows(rows, edges);

            // Rows are centred on the widest one, so a single fact table sits above the middle of
            // its dimensions.
            var widths = rows.Select(r => r.Sum(n => n.Size.Width) + HorizontalGap * (r.Count - 1)).ToList();
            var width = widths.Max();
            var y = 0f;
            for (var i = 0; i < rows.Count; i++)
            {
                var x = (width - widths[i]) / 2;
                var height = rows[i].Max(n => n.Size.Height);
                foreach (var n in rows[i])
                {
                    n.Location = new PointF(x, y);
                    x += n.Size.Width + HorizontalGap;
                }
                y += height + VerticalGap;
            }
            return new SizeF(width, y - VerticalGap);
        }

        /// <summary>
        /// Longest-path layering along many → one. Tables nothing points to (facts) get layer 0.
        /// Relationship cycles (rare, but legal for inactive relationships) are broken by
        /// ignoring the edge that closes the cycle.
        /// </summary>
        private static Dictionary<DiagramNode, int> AssignLayers(List<DiagramNode> group, List<DiagramEdge> edges)
        {
            var outgoing = group.ToDictionary(n => n, n => new List<DiagramNode>());
            var incoming = group.ToDictionary(n => n, n => 0);
            foreach (var e in edges.Where(e => e.From != e.To))
            {
                outgoing[e.From].Add(e.To);
                incoming[e.To]++;
            }

            var layer = new Dictionary<DiagramNode, int>();
            var onStack = new HashSet<DiagramNode>();
            int Visit(DiagramNode n, int depth)
            {
                if (layer.TryGetValue(n, out var existing) && existing >= depth) return existing;
                if (!onStack.Add(n)) return depth; // cycle: stop here
                layer[n] = depth;
                foreach (var m in outgoing[n]) Visit(m, depth + 1);
                onStack.Remove(n);
                return depth;
            }

            var sources = group.Where(n => incoming[n] == 0).ToList();
            if (sources.Count == 0) sources.Add(group.OrderBy(n => n.Table.Name, StringComparer.CurrentCultureIgnoreCase).First());
            foreach (var s in sources) Visit(s, 0);
            foreach (var n in group.Where(n => !layer.ContainsKey(n))) Visit(n, 0);

            // Pull each fact down next to its closest dimension: a source only one step above its
            // targets should not float above a deeper, unrelated snowflake.
            foreach (var s in sources)
                if (outgoing[s].Count > 0)
                    layer[s] = Math.Max(layer[s], outgoing[s].Min(m => layer[m]) - 1);
            return layer;
        }

        /// <summary>Barycentre ordering: alternate downward and upward sweeps.</summary>
        private static void OrderRows(List<List<DiagramNode>> rows, List<DiagramEdge> edges)
        {
            var neighbours = rows.SelectMany(r => r).ToDictionary(n => n, n => new List<DiagramNode>());
            foreach (var e in edges)
            {
                neighbours[e.From].Add(e.To);
                neighbours[e.To].Add(e.From);
            }

            for (var sweep = 0; sweep < OrderingSweeps; sweep++)
            {
                var down = sweep % 2 == 0;
                for (var k = 1; k < rows.Count; k++)
                {
                    var i = down ? k : rows.Count - 1 - k;
                    var reference = rows[down ? i - 1 : i + 1];
                    var position = reference.Select((n, index) => (n, index)).ToDictionary(p => p.n, p => p.index);
                    var current = rows[i];
                    var original = current.Select((n, index) => (n, index)).ToDictionary(p => p.n, p => p.index);
                    rows[i] = current
                        .OrderBy(n =>
                        {
                            var ranks = neighbours[n].Where(position.ContainsKey).Select(m => (double)position[m]).ToList();
                            return ranks.Count == 0 ? original[n] * (double)reference.Count / Math.Max(1, current.Count) : ranks.Average();
                        })
                        .ThenBy(n => original[n])
                        .ToList();
                }
            }
        }

        private static void GridLayout(List<DiagramNode> nodes, float left, float top, float maxWidth)
        {
            var x = left;
            var y = top;
            var rowHeight = 0f;
            foreach (var n in nodes.OrderBy(n => n.Table.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (x > left && x + n.Size.Width > left + maxWidth)
                {
                    x = left;
                    y += rowHeight + VerticalGap;
                    rowHeight = 0;
                }
                n.Location = new PointF(x, y);
                x += n.Size.Width + HorizontalGap;
                rowHeight = Math.Max(rowHeight, n.Size.Height);
            }
        }

        private static void Offset(IEnumerable<DiagramNode> nodes, float dx, float dy)
        {
            foreach (var n in nodes) n.Location = new PointF(n.Location.X + dx, n.Location.Y + dy);
        }
    }
}
