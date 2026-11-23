using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI.ModelDiagram
{
    internal enum DiagramFieldKind { Column, Measure, Hierarchy }

    internal sealed class DiagramField
    {
        public DiagramField(ITabularNamedObject obj, DiagramFieldKind kind, bool isHidden, bool isKey)
        {
            Object = obj;
            Kind = kind;
            IsHidden = isHidden;
            IsKey = isKey;
        }

        public ITabularNamedObject Object { get; }
        public DiagramFieldKind Kind { get; }
        public bool IsHidden { get; }
        /// <summary>The column takes part in a relationship or is marked as the table key.</summary>
        public bool IsKey { get; }
        public string Name => Object.Name;
    }

    internal sealed class DiagramNode
    {
        public DiagramNode(Table table, IReadOnlyList<DiagramField> fields)
        {
            Table = table;
            Fields = fields;
        }

        public Table Table { get; }
        public IReadOnlyList<DiagramField> Fields { get; }
        /// <summary>Top-left corner in diagram (world) coordinates.</summary>
        public PointF Location { get; set; }
        public SizeF Size { get; set; }
        public RectangleF Bounds => new RectangleF(Location, Size);

        public int IndexOf(ITabularNamedObject field)
        {
            for (var i = 0; i < Fields.Count; i++)
                if (ReferenceEquals(Fields[i].Object, field)) return i;
            return -1;
        }
    }

    internal sealed class DiagramEdge
    {
        public DiagramEdge(SingleColumnRelationship relationship, DiagramNode from, DiagramNode to)
        {
            Relationship = relationship;
            From = from;
            To = to;
        }

        public SingleColumnRelationship Relationship { get; }
        /// <summary>Node of the relationship's FromColumn (usually the "many" side).</summary>
        public DiagramNode From { get; }
        /// <summary>Node of the relationship's ToColumn (usually the "one" side).</summary>
        public DiagramNode To { get; }
    }

    /// <summary>
    /// The tables and relationships of a model, in the shape the diagram draws them. Rebuilt
    /// from the model whenever it changes; positions are carried over by the view.
    /// </summary>
    internal sealed class DiagramGraph
    {
        public static readonly DiagramGraph Empty = new DiagramGraph(new List<DiagramNode>(), new List<DiagramEdge>());

        private readonly List<DiagramNode> nodes;

        private DiagramGraph(List<DiagramNode> nodes, List<DiagramEdge> edges)
        {
            this.nodes = nodes;
            Edges = edges;
        }

        /// <summary>In drawing order: later nodes are drawn on top.</summary>
        public IReadOnlyList<DiagramNode> Nodes => nodes;
        public IReadOnlyList<DiagramEdge> Edges { get; }

        public DiagramNode Find(Table table) => nodes.FirstOrDefault(n => ReferenceEquals(n.Table, table));

        /// <summary>Moves a node to the end of the drawing order, so it is drawn above the others.</summary>
        public void BringToFront(DiagramNode node)
        {
            if (nodes.Remove(node)) nodes.Add(node);
        }

        public static DiagramGraph Build(Model model)
        {
            if (model == null) return Empty;

            var relationships = model.Relationships.OfType<SingleColumnRelationship>()
                .Where(r => r.FromColumn != null && r.ToColumn != null && !r.IsRemoved)
                .ToList();
            var related = new HashSet<Column>(relationships.SelectMany(r => new[] { r.FromColumn, r.ToColumn }));

            var nodes = new List<DiagramNode>();
            var byTable = new Dictionary<Table, DiagramNode>();
            foreach (var table in model.Tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var node = new DiagramNode(table, BuildFields(table, related));
                nodes.Add(node);
                byTable[table] = node;
            }

            var edges = new List<DiagramEdge>();
            foreach (var relationship in relationships)
            {
                if (byTable.TryGetValue(relationship.FromColumn.Table, out var from) &&
                    byTable.TryGetValue(relationship.ToColumn.Table, out var to))
                    edges.Add(new DiagramEdge(relationship, from, to));
            }
            return new DiagramGraph(nodes, edges);
        }

        /// <summary>
        /// Key columns first (like Power BI's "pin key fields to top"), then the remaining
        /// columns, measures and hierarchies, each group alphabetical.
        /// </summary>
        private static List<DiagramField> BuildFields(Table table, HashSet<Column> related)
        {
            var byName = StringComparer.CurrentCultureIgnoreCase;
            // ColumnCollection already leaves out the hidden RowNumber column.
            var columns = table.Columns
                .Select(c => new DiagramField(c, DiagramFieldKind.Column, c.IsHidden, c.IsKey || related.Contains(c)))
                .ToList();

            var fields = new List<DiagramField>();
            fields.AddRange(columns.Where(f => f.IsKey).OrderBy(f => f.Name, byName));
            fields.AddRange(columns.Where(f => !f.IsKey).OrderBy(f => f.Name, byName));
            fields.AddRange(table.Measures.OrderBy(m => m.Name, byName)
                .Select(m => new DiagramField(m, DiagramFieldKind.Measure, m.IsHidden, false)));
            fields.AddRange(table.Hierarchies.OrderBy(h => h.Name, byName)
                .Select(h => new DiagramField(h, DiagramFieldKind.Hierarchy, h.IsHidden, false)));
            return fields;
        }
    }
}
