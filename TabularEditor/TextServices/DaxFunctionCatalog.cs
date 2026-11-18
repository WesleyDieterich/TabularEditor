using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TabularEditor.TextServices
{
    internal static class DaxFunctionCatalog
    {
        internal static readonly Lazy<IReadOnlyDictionary<string, DaxFunctionHelp>> Functions =
            new Lazy<IReadOnlyDictionary<string, DaxFunctionHelp>>(LoadFunctions);

        private static IReadOnlyDictionary<string, DaxFunctionHelp> LoadFunctions()
        {
            var functions = new Dictionary<string, DaxFunctionHelp>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StreamReader(typeof(DaxFunctionCatalog).Assembly
                .GetManifestResourceStream("TabularEditor.TextServices.DaxFunctions.tsv")))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var fields = line.Split('\t');
                    functions.Add(fields[0], new DaxFunctionHelp(fields[1], fields.Skip(3)));
                }
            }
            return functions;
        }
    }

    internal sealed class DaxFunctionHelp
    {
        private static readonly Dictionary<string, string> ParameterDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alternateresult"] = "Fallback returned when the normal result is unavailable.",
            ["column"] = "Reference to a column in the model.",
            ["condition"] = "Boolean expression that determines which rows or values qualify.",
            ["denominator"] = "Number or expression to divide by.",
            ["expression"] = "DAX expression to evaluate.",
            ["filter"] = "Boolean filter, table filter, or filter modifier.",
            ["filterexpression"] = "Boolean expression evaluated for each row of the input table.",
            ["filtertable"] = "Table expression containing the rows to use as a filter.",
            ["logicaltest"] = "Expression that evaluates to TRUE or FALSE.",
            ["numerator"] = "Number or expression to divide.",
            ["resultcolumnname"] = "Column from which to return a matching value.",
            ["searchcolumnname"] = "Column in which to look for the search value.",
            ["searchvalue"] = "Value to match against the search column.",
            ["table"] = "Table reference or an expression that returns a table.",
            ["valueiffalse"] = "Result to return when the logical test is false.",
            ["valueiftrue"] = "Result to return when the logical test is true."
        };

        public string Description { get; }
        public string Syntax => string.Join(Environment.NewLine, signatures.Select(signature => signature.Text));

        private readonly Signature[] signatures;

        public DaxFunctionHelp(string description, IEnumerable<string> syntax)
        {
            Description = description;
            signatures = syntax.Select(text => new Signature(text)).ToArray();
        }

        public string GetHelp(int argument, bool useSemicolons)
        {
            var matches = signatures.Where(candidate => candidate.Accepts(argument)).DefaultIfEmpty(signatures.Last()).ToArray();
            var syntax = string.Join(Environment.NewLine, matches.Select(signature =>
                useSemicolons ? signature.Text.Replace(',', ';') : signature.Text));
            var parameters = matches.Select(signature => signature.GetParameter(argument)).Where(parameter => parameter != null).Distinct();
            var active = string.Join(" / ", parameters);
            return syntax + Environment.NewLine + Description
                + (active.Length == 0 ? "" : Environment.NewLine + "→ Parameter " + (argument + 1) + ": " + active);
        }

        private sealed class Signature
        {
            public string Text { get; }
            private readonly List<(string Name, bool Optional)> parameters = new List<(string, bool)>();
            private int repeatAt = -1;

            public Signature(string syntax)
            {
                Text = syntax.Replace("<", "").Replace(">", "");
                var start = Text.IndexOf('(') + 1;
                var end = Text.LastIndexOf(')');
                if (end <= start) return;

                var depth = 0;
                foreach (var part in Text.Substring(start, end - start).Split(','))
                {
                    var optional = depth > 0;
                    var name = part.Trim('[', ']', ' ');
                    var position = 0;
                    for (; position < part.Length; position++)
                    {
                        if (part[position] == '[') depth++;
                        else if (part[position] == ']') depth--;
                        else if (!char.IsWhiteSpace(part[position])) break;
                        optional = depth > 0;
                    }

                    if (part.Contains("…") || part.Contains("..."))
                    {
                        if (repeatAt < 0) repeatAt = parameters.Count;
                        name = name.Replace("…", "").Replace("...", "").Trim('[', ']', ' ');
                    }

                    if (name.Length != 0) parameters.Add((name, optional));
                    for (; position < part.Length; position++)
                        depth += part[position] == '[' ? 1 : part[position] == ']' ? -1 : 0;
                }
            }

            public bool Accepts(int argument) => repeatAt >= 0 || argument < parameters.Count;

            public string GetParameter(int argument)
            {
                if (repeatAt >= 0 && argument >= repeatAt)
                    return "additional arguments (see syntax)";
                if (argument >= parameters.Count) return null;

                var parameter = parameters[argument];
                var key = parameter.Name.Replace("_", "").Replace(" ", "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                ParameterDescriptions.TryGetValue(key, out var description);
                return parameter.Name + (parameter.Optional ? " (optional)" : "")
                    + (description == null ? "" : " — " + description);
            }
        }
    }
}
