using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI.ModelDiagram
{
    /// <summary>Cardinality as Power BI presents it: first column to second column.</summary>
    internal enum RelationshipCardinality { ManyToOne, OneToOne, OneToMany, ManyToMany }

    /// <summary>
    /// The settings of a relationship as the edit dialog shows them. Column1/Column2 are the two
    /// sides in the order the user sees; <see cref="RelationshipEdits"/> maps them to TOM's
    /// From/To, where From is always the "many" side when there is one.
    /// </summary>
    internal sealed class RelationshipSpec
    {
        public Column Column1 { get; set; }
        public Column Column2 { get; set; }
        public RelationshipCardinality Cardinality { get; set; } = RelationshipCardinality.ManyToOne;
        public CrossFilteringBehavior CrossFiltering { get; set; } = CrossFilteringBehavior.OneDirection;
        public SecurityFilteringBehavior SecurityFiltering { get; set; } = SecurityFilteringBehavior.OneDirection;
        public bool IsActive { get; set; } = true;
        public bool RelyOnReferentialIntegrity { get; set; }

        /// <summary>The TOM From column: the many side, or Column1 when both sides are alike.</summary>
        public Column FromColumn => Cardinality == RelationshipCardinality.OneToMany ? Column2 : Column1;
        public Column ToColumn => Cardinality == RelationshipCardinality.OneToMany ? Column1 : Column2;

        public RelationshipEndCardinality FromCardinality =>
            Cardinality == RelationshipCardinality.OneToOne ? RelationshipEndCardinality.One : RelationshipEndCardinality.Many;
        public RelationshipEndCardinality ToCardinality =>
            Cardinality == RelationshipCardinality.ManyToMany ? RelationshipEndCardinality.Many : RelationshipEndCardinality.One;

        public static RelationshipSpec From(SingleColumnRelationship r)
        {
            var spec = new RelationshipSpec
            {
                Column1 = r.FromColumn,
                Column2 = r.ToColumn,
                CrossFiltering = r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections
                    ? CrossFilteringBehavior.BothDirections : CrossFilteringBehavior.OneDirection,
                SecurityFiltering = r.SecurityFilteringBehavior == SecurityFilteringBehavior.BothDirections
                    ? SecurityFilteringBehavior.BothDirections : SecurityFilteringBehavior.OneDirection,
                IsActive = r.IsActive,
                RelyOnReferentialIntegrity = r.RelyOnReferentialIntegrity,
            };
            var fromMany = r.FromCardinality == RelationshipEndCardinality.Many;
            var toMany = r.ToCardinality == RelationshipEndCardinality.Many;
            spec.Cardinality = fromMany && toMany ? RelationshipCardinality.ManyToMany
                : fromMany ? RelationshipCardinality.ManyToOne
                : toMany ? RelationshipCardinality.OneToMany
                : RelationshipCardinality.OneToOne;
            return spec;
        }
    }

    internal static class RelationshipEdits
    {
        /// <summary>
        /// Returns why the relationship cannot be saved, or null if it can.
        /// <paramref name="editing"/> is the relationship being edited, excluded from duplicate checks.
        /// </summary>
        public static string Validate(RelationshipSpec spec, SingleColumnRelationship editing = null)
        {
            if (spec.Column1 == null || spec.Column2 == null) return "Choose a column on both sides.";
            if (spec.Column1.Table == spec.Column2.Table) return "Both columns are in the same table. A relationship joins two different tables.";
            if (spec.Column1.DataType != spec.Column2.DataType)
                return $"The columns have different data types ({spec.Column1.DataType} and {spec.Column2.DataType}). Relationships need columns of the same type.";
            if (spec.SecurityFiltering == SecurityFilteringBehavior.BothDirections && spec.CrossFiltering != CrossFilteringBehavior.BothDirections)
                return "Security filtering in both directions requires cross filtering in both directions.";

            var model = spec.Column1.Model;
            var duplicate = model.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r != editing &&
                ((r.FromColumn == spec.Column1 && r.ToColumn == spec.Column2) || (r.FromColumn == spec.Column2 && r.ToColumn == spec.Column1)));
            if (duplicate != null) return $"These columns are already related: {duplicate.Name}.";
            return null;
        }

        /// <summary>
        /// An active relationship already joins the two tables directly, so a new one between them
        /// has to start inactive. Ambiguous paths through other tables are not detected.
        /// </summary>
        public static bool ConflictsWithActiveRelationship(Table a, Table b, SingleColumnRelationship editing = null) =>
            a.Model.Relationships.OfType<SingleColumnRelationship>().Any(r => r != editing && r.IsActive &&
                r.FromColumn != null && r.ToColumn != null &&
                ((r.FromColumn.Table == a && r.ToColumn.Table == b) || (r.FromColumn.Table == b && r.ToColumn.Table == a)));

        /// <summary>
        /// Default settings for a relationship dragged from one column to another: the dragged
        /// column is the many side unless it is the key of its table and the target is not. It
        /// starts inactive when making it active would conflict or create ambiguous filter paths.
        /// </summary>
        public static RelationshipSpec Suggest(Column source, Column target)
        {
            var spec = new RelationshipSpec { Column1 = source, Column2 = target };
            if (source.IsKey && !target.IsKey) spec.Cardinality = RelationshipCardinality.OneToMany;
            spec.IsActive = !ConflictsWithActiveRelationship(source.Table, target.Table) &&
                FindNewAmbiguity(spec) == null;
            return spec;
        }

        #region Ambiguous filter paths

        /// <summary>
        /// One direction a filter can travel along an active relationship: from the "one" side
        /// (To) to the "many" side (From), and back as well when cross filtering is both ways.
        /// </summary>
        internal sealed class FilterEdge
        {
            public Table Source;
            public Table Target;
            /// <summary>The relationship, or null for the relationship being created.</summary>
            public SingleColumnRelationship Relationship;
        }

        /// <summary>Two different filter routes from one table to another.</summary>
        internal sealed class Ambiguity
        {
            public Table Source;
            public Table Target;
            public IReadOnlyList<Table> Path1;
            public IReadOnlyList<Table> Path2;

            public string Describe() =>
                $"{Source.Name} would filter {Target.Name} along two paths: " +
                $"{string.Join(" → ", Path1.Select(t => t.Name))}, and {string.Join(" → ", Path2.Select(t => t.Name))}.";
        }

        /// <summary>
        /// Returns why the relationship, saved with these settings, would make the model's
        /// filter paths ambiguous, or null when it would not. Only ambiguity this change
        /// introduces counts, so a problem elsewhere in the model does not block it.
        /// </summary>
        public static string FindNewAmbiguity(RelationshipSpec spec, SingleColumnRelationship editing = null)
        {
            if (spec.Column1 == null || spec.Column2 == null) return null;
            var model = spec.Column1.Model;

            var before = ActiveEdges(model, exclude: null);
            var after = ActiveEdges(model, exclude: editing);
            if (spec.IsActive)
            {
                after.Add(new FilterEdge { Source = spec.ToColumn.Table, Target = spec.FromColumn.Table, Relationship = editing });
                if (spec.CrossFiltering == CrossFilteringBehavior.BothDirections)
                    after.Add(new FilterEdge { Source = spec.FromColumn.Table, Target = spec.ToColumn.Table, Relationship = editing });
            }

            var existing = new HashSet<(Table, Table)>(FindAmbiguities(before).Select(a => (a.Source, a.Target)));
            var introduced = FindAmbiguities(after).FirstOrDefault(a => !existing.Contains((a.Source, a.Target)));
            return introduced == null ? null :
                "This would make filter paths ambiguous. " + introduced.Describe() +
                " Make this relationship inactive, or deactivate or change one of the others.";
        }

        /// <summary>Ambiguous filter paths in the model as it is now.</summary>
        internal static List<Ambiguity> FindAmbiguities(Model model) => FindAmbiguities(ActiveEdges(model, exclude: null));

        private static List<FilterEdge> ActiveEdges(Model model, SingleColumnRelationship exclude)
        {
            var edges = new List<FilterEdge>();
            foreach (var r in model.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r == exclude || !r.IsActive || r.FromColumn == null || r.ToColumn == null) continue;
                edges.Add(new FilterEdge { Source = r.ToColumn.Table, Target = r.FromColumn.Table, Relationship = r });
                if (r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections)
                    edges.Add(new FilterEdge { Source = r.FromColumn.Table, Target = r.ToColumn.Table, Relationship = r });
            }
            return edges;
        }

        /// <summary>
        /// Every (source, target) pair of tables with more than one filter path between them,
        /// with two such paths. Paths are simple: they never visit a table twice. The search is
        /// capped so a pathological model cannot hang the UI; past the cap it reports what it found.
        /// </summary>
        internal static List<Ambiguity> FindAmbiguities(IEnumerable<FilterEdge> edges, int maxSteps = 200000)
        {
            var byName = StringComparer.CurrentCultureIgnoreCase;
            var outgoing = edges.Where(e => e.Source != e.Target)
                .GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Target.Name, byName).ToList());
            var result = new List<Ambiguity>();
            var steps = 0;

            foreach (var source in outgoing.Keys.OrderBy(t => t.Name, byName))
            {
                var firstPath = new Dictionary<Table, List<Table>>();
                var reported = new HashSet<Table>();
                var path = new List<Table> { source };
                var onPath = new HashSet<Table> { source };

                void Walk(Table table)
                {
                    if (!outgoing.TryGetValue(table, out var next)) return;
                    foreach (var edge in next)
                    {
                        if (++steps > maxSteps) return;
                        var target = edge.Target;
                        if (onPath.Contains(target)) continue;
                        path.Add(target);
                        if (!firstPath.TryGetValue(target, out var earlier))
                            firstPath[target] = path.ToList();
                        else if (reported.Add(target))
                            result.Add(new Ambiguity { Source = source, Target = target, Path1 = earlier, Path2 = path.ToList() });
                        onPath.Add(target);
                        Walk(target);
                        onPath.Remove(target);
                        path.RemoveAt(path.Count - 1);
                    }
                }

                Walk(source);
                if (steps > maxSteps) break;
            }
            // Shortest explanations first: they are the easiest to act on.
            return result.OrderBy(a => a.Path1.Count + a.Path2.Count).ToList();
        }

        #endregion

        /// <summary>Creates the relationship as one undo step.</summary>
        public static SingleColumnRelationship Create(RelationshipSpec spec)
        {
            var handler = spec.Column1.Handler;
            handler.BeginUpdate("create relationship");
            try
            {
                var relationship = spec.Column1.Model.AddRelationship();
                Apply(relationship, spec);
                return relationship;
            }
            finally
            {
                handler.EndUpdate();
            }
        }

        /// <summary>Applies the settings as one undo step, touching only properties that change.</summary>
        public static void Update(SingleColumnRelationship relationship, RelationshipSpec spec)
        {
            var handler = relationship.Handler;
            handler.BeginUpdate("edit relationship");
            try
            {
                Apply(relationship, spec);
            }
            finally
            {
                handler.EndUpdate();
            }
        }

        private static void Apply(SingleColumnRelationship r, RelationshipSpec spec)
        {
            if (r.FromColumn != spec.FromColumn) r.FromColumn = spec.FromColumn;
            if (r.ToColumn != spec.ToColumn) r.ToColumn = spec.ToColumn;
            if (r.FromCardinality != spec.FromCardinality) r.FromCardinality = spec.FromCardinality;
            if (r.ToCardinality != spec.ToCardinality) r.ToCardinality = spec.ToCardinality;
            if (r.CrossFilteringBehavior != spec.CrossFiltering) r.CrossFilteringBehavior = spec.CrossFiltering;
            if (r.SecurityFilteringBehavior != spec.SecurityFiltering) r.SecurityFilteringBehavior = spec.SecurityFiltering;
            if (r.IsActive != spec.IsActive) r.IsActive = spec.IsActive;
            if (r.RelyOnReferentialIntegrity != spec.RelyOnReferentialIntegrity) r.RelyOnReferentialIntegrity = spec.RelyOnReferentialIntegrity;
        }

        /// <summary>DirectQuery tables are the only place "assume referential integrity" has an effect.</summary>
        public static bool IsDirectQuery(Table table) =>
            table.Partitions.Any(p => p.Mode == ModeType.DirectQuery) ||
            (table.Partitions.Any(p => p.Mode == ModeType.Default) && table.Model.DefaultMode == ModeType.DirectQuery);

        /// <summary>
        /// Asks the connected model whether each column's values are unique, and returns the
        /// cardinality that fits the data. Needs the columns to exist on the server.
        /// </summary>
        public static Task<RelationshipCardinality> DetectCardinalityAsync(TabularModelHandler handler, Column column1, Column column2, CancellationToken cancellation)
        {
            var connectionString = handler.GetQueryConnectionString(handler.Database.Name);
            var query = "EVALUATE ROW(" +
                $"\"Rows1\", COUNTROWS({column1.Table.DaxTableName}), \"Distinct1\", DISTINCTCOUNT({column1.DaxObjectFullName}), " +
                $"\"Rows2\", COUNTROWS({column2.Table.DaxTableName}), \"Distinct2\", DISTINCTCOUNT({column2.DaxObjectFullName}))";
            return Task.Run(() =>
            {
                using (var data = Database.ExecuteDax(connectionString, query, cancellation))
                {
                    var row = data.Tables[0].Rows[0];
                    long Value(int i) => row.IsNull(i) ? 0 : Convert.ToInt64(row[i]);
                    var unique1 = Value(0) == Value(1);
                    var unique2 = Value(2) == Value(3);
                    return unique1 && unique2 ? RelationshipCardinality.OneToOne
                        : unique2 ? RelationshipCardinality.ManyToOne
                        : unique1 ? RelationshipCardinality.OneToMany
                        : RelationshipCardinality.ManyToMany;
                }
            }, cancellation);
        }
    }
}
