using System.Collections.Generic;
using System.Linq;
using System.Text;
using TabularEditor.TOMWrapper;

namespace TabularEditor.UI
{
    /// <summary>
    /// Builds a "filter dump" measure: a DAX expression that lists every column that is directly
    /// filtered in the current filter context, together with (up to <see cref="MaxValues"/>) of the
    /// values that remain visible. Useful for debugging what a visual or a query actually filters.
    /// </summary>
    internal static class FilterDump
    {
        public const string MeasureName = "Filter Dump";
        public const int MaxValues = 3;

        /// <summary>
        /// Resolves the columns to inspect from a tree selection. For each table involved, columns
        /// selected explicitly (directly, through display folders, hierarchies or levels) win;
        /// a table selected without any of its columns contributes all of its columns.
        /// </summary>
        public static List<Column> ColumnsFromSelection(UITreeSelection selection)
        {
            var explicitColumns = selection.Columns.Cast<Column>()
                .Concat(selection.Levels.Select(level => level.Column))
                .Concat(selection.Hierarchies.SelectMany(h => h.Levels.Select(level => level.Column)))
                .Where(column => column != null)
                .Distinct()
                .ToList();

            var tables = selection.Direct.OfType<Table>()
                .Concat(explicitColumns.Select(column => column.Table))
                .Distinct()
                .ToList();

            var result = new List<Column>();
            foreach (var table in tables)
            {
                var picked = explicitColumns.Where(column => column.Table == table).ToList();
                result.AddRange(picked.Count > 0 ? picked : table.Columns.AsEnumerable());
            }
            return result.Where(IsDumpable).ToList();
        }

        public static List<Column> AllColumns(Model model) =>
            model.Tables.SelectMany(table => table.Columns).Where(IsDumpable).ToList();

        // Binary columns cannot be referenced from DAX.
        private static bool IsDumpable(Column column) => column.DataType != DataType.Binary;

        /// <summary>
        /// Table that should own a model measure created from the selection: the first table
        /// involved, or (for an all-tables dump) the first regular table of the model.
        /// </summary>
        public static Table HostTable(UITreeSelection selection, Model model)
        {
            var first = selection.Direct.FirstOrDefault();
            if (first is Table table) return table;
            if (first is ITabularTableObject tableObject && tableObject.Table != null) return tableObject.Table;
            if (first is Level level) return level.Table;
            return model.Tables.FirstOrDefault(t => !(t is CalculationGroupTable)) ?? model.Tables.FirstOrDefault();
        }

        /// <summary>Returns a measure name that is not used by any measure, nor by a column of <paramref name="host"/>.</summary>
        public static string UniqueMeasureName(Model model, Table host)
        {
            var taken = new HashSet<string>(model.AllMeasures.Select(m => m.Name)
                .Concat(host.Columns.Select(c => c.Name)), System.StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(MeasureName)) return MeasureName;
            for (var i = 2; ; i++)
            {
                var candidate = MeasureName + " " + i;
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// The measure expression. Returns BLANK when nothing is filtered, so the measure does not
        /// force rows into visuals; otherwise one line per filtered column, e.g.
        /// <c>'Product'[Color] = Black, Blue, Red, … (+5)</c>.
        /// </summary>
        public static string BuildExpression(IReadOnlyList<Column> columns, string indent = "")
        {
            var sb = new StringBuilder();
            void Line(string text) => sb.Append(indent).Append(text).Append("\r\n");

            Line($"VAR __MaxValues = {MaxValues}");
            Line("VAR __NewLine = UNICHAR( 13 ) & UNICHAR( 10 )");
            Line("VAR __Result =");
            if (columns.Count == 0) Line("    \"\"");
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i].DaxObjectFullName;
                var label = DaxString(col + " = ");
                Line($"    {(i == 0 ? "  " : "& ")}IF(");
                Line($"        ISFILTERED( {col} ),");
                Line($"        VAR __Count = COUNTROWS( VALUES( {col} ) )");
                Line($"        RETURN");
                Line($"            {label}");
                Line($"                & CONCATENATEX( TOPN( __MaxValues, VALUES( {col} ), {col}, ASC ), {col}, \", \", {col}, ASC )");
                Line($"                & IF( __Count > __MaxValues, \", … (+\" & ( __Count - __MaxValues ) & \")\" )");
                Line($"                & __NewLine");
                Line($"    )");
            }
            Line("RETURN");
            sb.Append(indent).Append("    IF( __Result <> \"\", __Result )");
            return sb.ToString();
        }

        /// <summary>A query-scoped <c>MEASURE 'Table'[Name] = ...</c> definition, indented for a DEFINE block.</summary>
        public static string BuildQueryDefinition(Table host, string name, IReadOnlyList<Column> columns)
        {
            var measureRef = host.DaxObjectFullName + "[" + name.Replace("]", "]]") + "]";
            return $"    MEASURE {measureRef} =\r\n" + BuildExpression(columns, "        ") + "\r\n";
        }

        private static string DaxString(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
